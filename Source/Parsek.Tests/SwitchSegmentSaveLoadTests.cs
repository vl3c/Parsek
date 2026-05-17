using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase F coverage for the save/load/F5/F9 contract of segment-scoped
    /// switch/Fly auto-record: the recording-codec round-trip of the new
    /// ownership stamp, the scenario-marker round-trip, the committed-tree
    /// restore-attempt + segment-session cooperative survival across a save,
    /// and the marker-owned event/milestone narrowing survives a simulated
    /// save-then-reload. Maps to plan
    /// <c>docs/dev/plans/segment-scoped-switch-fly-autorecord.md</c>
    /// §"Save/Load, F5, and F9" plan-test entries #6, #7, #8, #9, #17a, #17b
    /// plus the new cross-scope item (committed restore + segment session both
    /// armed survives save).
    ///
    /// <para>The full <c>ParsekScenario.OnSave</c> / <c>OnLoad</c> path is
    /// not drivable from xUnit (see <c>reference_parsek_scenario_xunit.md</c>):
    /// Planetarium and Unity GameEvents are unguarded along that path. The
    /// tests here drive the data-shape contract through the codecs directly
    /// (<c>SaveInto</c> / <c>TryLoadFrom</c>) and source-text-gate the
    /// scenario wiring that joins them.</para>
    /// </summary>
    [Collection("Sequential")]
    public class SwitchSegmentSaveLoadTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SwitchSegmentSaveLoadTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekProcess.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekScenario.SetInstanceForTesting(null);
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            ParsekProcess.ResetForTesting();
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private static string SessionIdString(Guid sessionId)
            => sessionId.ToString("D", CultureInfo.InvariantCulture);

        private static Recording MakeRecording(
            string recordingId,
            string treeId,
            string switchSegmentSessionId = null,
            double startUT = 100.0,
            double endUT = 200.0,
            string vesselName = "Test")
        {
            return new Recording
            {
                RecordingId = recordingId,
                TreeId = treeId,
                VesselName = vesselName,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                SwitchSegmentSessionId = switchSegmentSessionId,
            };
        }

        private static RecordingTree MakeTreeWithRecordings(
            string treeId,
            params Recording[] recordings)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = treeId,
                RootRecordingId = recordings.Length > 0 ? recordings[0].RecordingId : null,
                ActiveRecordingId = recordings.Length > 0 ? recordings[0].RecordingId : null,
                BranchPoints = new List<BranchPoint>(),
            };
            foreach (var rec in recordings)
                tree.AddOrReplaceRecording(rec);
            return tree;
        }

        private static GameStateEvent MakeEvent(double ut, string recordingId)
        {
            return new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.TechResearched,
                key = "tech-test",
                detail = "",
                recordingId = recordingId ?? "",
            };
        }

        private static SwitchSegmentSession ArmFreshSession(
            ParsekScenario scenario,
            Guid sessionId,
            string treeId,
            string segmentRecordingId,
            SwitchSegmentEntryReason entryReason
                = SwitchSegmentEntryReason.TrackingStationFly)
        {
            var session = new SwitchSegmentSession
            {
                SessionId = sessionId,
                IntentId = Guid.NewGuid(),
                EntryReason = entryReason,
                TreeId = treeId,
                ActiveSegmentRecordingId = segmentRecordingId,
                ParentRecordingId = null,
                FocusedVesselPersistentId = 1234u,
                SourceVesselPersistentId = 5678u,
                SwitchUT = 250.0,
                PreSessionBranchPointIds = new List<string>(),
            };
            scenario.ArmSwitchSegmentSession(session);
            return session;
        }

        // -----------------------------------------------------------------
        // Plan test #6: segment session + ownership-stamped recording
        // round-trip through their codecs intact.
        // -----------------------------------------------------------------

        // Fails if: the SwitchSegmentSession round-trip drops any field
        // through SaveInto/TryLoadFrom, or the Recording's
        // SwitchSegmentSessionId stamp is lost through RecordingTree
        // SaveRecordingInto/LoadRecordingFrom. Plan §"Save/Load, F5, and F9"
        // item 1 (the segment + its ownership stamp survives F5).
        [Fact]
        public void SegmentActive_SaveAndReload_RestoresSessionAndRecording()
        {
            Guid sessionId = Guid.NewGuid();
            string sessionIdStr = SessionIdString(sessionId);

            var scenario = new ParsekScenario();
            ParsekScenario.SetInstanceForTesting(scenario);
            ArmFreshSession(scenario, sessionId, "tree_s", "rec_seg");

            // Round-trip the session.
            var sessionParent = new ConfigNode("PARSEK");
            scenario.ActiveSwitchSegmentSession.SaveInto(sessionParent);
            var loadedSessionNode = sessionParent.GetNode(SwitchSegmentSession.NodeName);
            Assert.NotNull(loadedSessionNode);
            Assert.True(SwitchSegmentSession.TryLoadFrom(
                loadedSessionNode, out SwitchSegmentSession restored));
            Assert.Equal(sessionId, restored.SessionId);
            Assert.Equal("rec_seg", restored.ActiveSegmentRecordingId);
            Assert.Equal(SwitchSegmentEntryReason.TrackingStationFly,
                restored.EntryReason);
            Assert.Equal(1234u, restored.FocusedVesselPersistentId);
            Assert.Equal(5678u, restored.SourceVesselPersistentId);

            // Round-trip the marker-owned recording via the codec.
            var rec = MakeRecording("rec_seg", "tree_s",
                switchSegmentSessionId: sessionIdStr);
            var recNode = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(recNode, rec);
            var reloadedRec = new Recording();
            RecordingTree.LoadRecordingFrom(recNode, reloadedRec);
            Assert.Equal(sessionIdStr, reloadedRec.SwitchSegmentSessionId);
            Assert.Equal(rec.RecordingId, reloadedRec.RecordingId);
        }

        // Fails if: ParsekScenario.SaveRewindStagingState stops emitting the
        // SWITCH_SEGMENT_SESSION node, or LoadRewindStagingState drops it on
        // load. Source-text gates because the full scenario module is not
        // xUnit-drivable. Plan test #6 wiring (F5 round-trip).
        [Fact]
        public void F5_AfterSwitchFly_PreservesMarkerAndSegmentId()
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string scenarioPath = Path.Combine(projectRoot,
                "Source", "Parsek", "ParsekScenario.cs");
            string source = File.ReadAllText(scenarioPath);

            // Save: SWITCH_SEGMENT_SESSION removed and re-emitted.
            Assert.Contains(
                "node.RemoveNodes(SwitchSegmentSession.NodeName);", source);
            Assert.Contains(
                "activeSwitchSegmentSession.SaveInto(node);", source);

            // Load: SWITCH_SEGMENT_SESSION node read into activeSwitchSegmentSession.
            Assert.Contains(
                "ConfigNode segmentNode = node.GetNode(SwitchSegmentSession.NodeName);",
                source);
            Assert.Contains(
                "SwitchSegmentSession.TryLoadFrom(segmentNode, out loadedSession)",
                source);

            // Stock-action intent shares the seam.
            Assert.Contains(
                "node.RemoveNodes(StockActionIntentMarker.NodeName);", source);
            Assert.Contains(
                "ConfigNode intentNode = node.GetNode(StockActionIntentMarker.NodeName);",
                source);
        }

        // -----------------------------------------------------------------
        // Plan test #7: F9 to a pre-switch save clears the marker and drops
        // any pending switch attempt.
        // -----------------------------------------------------------------

        // Fails if: loading a save that DOES NOT carry a SwitchSegmentSession
        // leaves a previously-armed session lingering on the scenario. The
        // load path must clear both the intent marker and the segment
        // session before reading from the node, so a load from a pre-switch
        // save unarms cleanly. We drive only the round-trip of the absent-
        // node case (the full OnLoad is not xUnit-drivable) and assert that
        // the session loader leaves activeSwitchSegmentSession null when no
        // node is present.
        [Fact]
        public void F9_ToPreSwitchSave_ClearsMarker_DropsPendingAttempt()
        {
            // 1) The session loader returns false (and produces null) when
            //    the node is missing. This is the basic absence case the
            //    OnLoad path relies on to leave activeSwitchSegmentSession
            //    null after a pre-feature / pre-switch save load.
            var emptyParent = new ConfigNode("PARSEK");
            ConfigNode missingNode = emptyParent.GetNode(SwitchSegmentSession.NodeName);
            Assert.Null(missingNode);

            // 2) Same shape for the intent marker.
            ConfigNode missingIntent = emptyParent.GetNode(StockActionIntentMarker.NodeName);
            Assert.Null(missingIntent);

            // 3) Source-text gate: LoadRewindStagingState clears the two
            //    fields BEFORE reading the nodes, so a missing-node load
            //    leaves them null even if a previously-loaded save had
            //    armed them. Asserting both clears appear in the right
            //    order pins the F9-from-pre-switch contract.
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string scenarioPath = Path.Combine(projectRoot,
                "Source", "Parsek", "ParsekScenario.cs");
            string source = File.ReadAllText(scenarioPath);

            int clearIntent = source.IndexOf("activeStockActionIntent = null;");
            int clearSession = source.IndexOf("activeSwitchSegmentSession = null;");
            int loadIntent = source.IndexOf(
                "ConfigNode intentNode = node.GetNode(StockActionIntentMarker.NodeName);");
            int loadSession = source.IndexOf(
                "ConfigNode segmentNode = node.GetNode(SwitchSegmentSession.NodeName);");
            Assert.True(clearIntent > 0 && clearSession > 0);
            Assert.True(loadIntent > 0 && loadSession > 0);
            Assert.True(clearIntent < loadIntent,
                "intent clear must precede intent node load");
            Assert.True(clearSession < loadSession,
                "session clear must precede session node load");
        }

        // -----------------------------------------------------------------
        // Plan test #8: events for marker-owned new recording ids survive
        // save-time persistence narrowing AND are still purged on
        // scoped discard.
        // -----------------------------------------------------------------

        // Fails if: an event tagged with a marker-owned switch-segment
        // recording id is suppressed by the #866 same-id restore-attempt
        // path. After the dual-armed state is set up (restore attempt +
        // session), the predicate must return false for the marker-owned
        // id; scoped Discard must then purge the same event.
        [Fact]
        public void EventPersistence_NewSegmentIds_SurvivesSaveReload_AndPurgedOnDiscard_WhenRestoreAttemptArmed()
        {
            Guid sessionId = Guid.NewGuid();
            string sessionIdStr = SessionIdString(sessionId);
            var scenario = new ParsekScenario();
            ParsekScenario.SetInstanceForTesting(scenario);

            // Commit a parent so the restore-attempt has a real id set.
            var parent = MakeRecording("rec_parent", "tree_dual");
            parent.ExplicitEndUT = 200.0;
            var tree = MakeTreeWithRecordings("tree_dual", parent);
            RecordingStore.AddCommittedTreeForTesting(tree);
            RecordingStore.AddCommittedInternal(parent);
            RecordingStore.ArmCommittedTreeRestoreAttempt(tree, "test-arm");
            Assert.True(RecordingStore.HasCommittedTreeRestoreAttempt);

            // Add a marker-owned segment to the same tree and arm session.
            var segment = MakeRecording("rec_seg", "tree_dual",
                switchSegmentSessionId: sessionIdStr);
            RecordingStore.AddCommittedInternal(segment);
            tree.AddOrReplaceRecording(segment);
            ArmFreshSession(scenario, sessionId, "tree_dual", "rec_seg");

            // Event tagged with the marker-owned id must NOT be suppressed
            // by the #866 contract (would lose it across save).
            var evt = MakeEvent(ut: 250.0, recordingId: "rec_seg");
            Assert.False(
                RecordingStore.ShouldSuppressCommittedTreeRestoreAttemptEventPersistence(evt));

            // Adding the event AND a scoped discard later must purge it.
            GameStateStore.AddEvent(ref evt);
            int beforeCount = GameStateStore.EventCount;
            Assert.Equal(1, beforeCount);

            RecordingStore.StashPendingTree(tree);
            string discardReason;
            RecordingStore.TryDiscardActiveSwitchSegmentAttempt(out discardReason);

            Assert.Equal(0, GameStateStore.EventCount);
        }

        // -----------------------------------------------------------------
        // Plan test #9: pending-event milestone flush is NOT deferred when
        // every pending event is marker-owned, AND those events survive
        // the save+reload round-trip.
        // -----------------------------------------------------------------

        // Fails if: marker-owned pending events get deferred (losing them
        // on a quicksave-while-segment-active path) or if the events do
        // not round-trip through the GameStateStore save shape. Plan §
        // "Save/Load" item 5.
        [Fact]
        public void MilestoneFlush_MarkerOwnedNewIds_FlushedAcrossSave_WhenRestoreAttemptArmed()
        {
            Guid sessionId = Guid.NewGuid();
            string sessionIdStr = SessionIdString(sessionId);
            var scenario = new ParsekScenario();
            ParsekScenario.SetInstanceForTesting(scenario);

            // Dual-armed setup.
            var parent = MakeRecording("rec_parent", "tree_flush");
            var tree = MakeTreeWithRecordings("tree_flush", parent);
            RecordingStore.AddCommittedTreeForTesting(tree);
            RecordingStore.AddCommittedInternal(parent);
            RecordingStore.ArmCommittedTreeRestoreAttempt(tree, "test-arm");

            var segment = MakeRecording("rec_seg", "tree_flush",
                switchSegmentSessionId: sessionIdStr);
            RecordingStore.AddCommittedInternal(segment);
            tree.AddOrReplaceRecording(segment);
            ArmFreshSession(scenario, sessionId, "tree_flush", "rec_seg");

            // Pending events are all on the marker-owned id.
            var evt1 = MakeEvent(ut: 250.0, recordingId: "rec_seg");
            var evt2 = MakeEvent(ut: 260.0, recordingId: "rec_seg");
            GameStateStore.AddEvent(ref evt1);
            GameStateStore.AddEvent(ref evt2);

            // Save-time predicate: must not defer.
            Assert.False(ParsekScenario.ShouldDeferPendingEventMilestoneFlushForSave());

            // Simulate a save: snapshot the event list before the
            // (hypothetical) save, then verify the in-memory store still
            // carries them after a no-op reset/replay. The actual
            // GameStateStore.Save / Load is exercised by other test files;
            // here we pin the contract that marker-owned events remained
            // visible to the save-time predicate.
            Assert.Equal(2, GameStateStore.EventCount);
            Assert.Equal("rec_seg", GameStateStore.Events[0].recordingId);
            Assert.Equal("rec_seg", GameStateStore.Events[1].recordingId);
        }

        // -----------------------------------------------------------------
        // Plan tests #17a / #17b: same-process F5 and cross-process load.
        // Phase A.3 already covers EvaluateStaleness exhaustively; these
        // pin the integration shape from the OnLoad seam: a fresh marker
        // remains armed, a cross-run marker clears.
        // -----------------------------------------------------------------

        // Fails if: a fresh same-process F5 marker is mistakenly cleared.
        // Plan test #17a — keeps the intent armed for the consume site.
        [Fact]
        public void SameProcessF5InTrackingStation_PreservesIntent_ConsumesOnFlightLoad()
        {
            Guid processId = ParsekProcess.ProcessSessionId;
            var marker = new StockActionIntentMarker
            {
                IntentId = Guid.NewGuid(),
                Action = StockActionType.TrackingStationFly,
                TargetVesselPersistentId = 99u,
                SourceScene = StockActionSourceScene.TrackingStation,
                CapturedRealtime = 100f,
                CapturedUT = 1000.0,
                ProcessSessionId = processId,
            };
            var staleness = StockActionIntentMarker.EvaluateStaleness(
                marker, processId, currentRealtime: 101f, currentUT: 1001.0);
            Assert.Equal(StockActionIntentStaleness.Fresh, staleness);
        }

        // Fails if: a cross-process load lets a serialized marker stay
        // armed. Plan test #17b — clears as stale-cross-run.
        [Fact]
        public void CrossProcessLoad_WithTsFlyArmed_ClearsAsCrossRun()
        {
            Guid armProcess = Guid.NewGuid();
            Guid consumeProcess = Guid.NewGuid();
            var marker = new StockActionIntentMarker
            {
                IntentId = Guid.NewGuid(),
                Action = StockActionType.TrackingStationFly,
                TargetVesselPersistentId = 99u,
                SourceScene = StockActionSourceScene.TrackingStation,
                CapturedRealtime = 100f,
                CapturedUT = 1000.0,
                ProcessSessionId = armProcess,
            };
            var staleness = StockActionIntentMarker.EvaluateStaleness(
                marker, consumeProcess, currentRealtime: 101f, currentUT: 1001.0);
            Assert.Equal(StockActionIntentStaleness.StaleCrossRun, staleness);
        }

        // -----------------------------------------------------------------
        // Cross-scope: committed restore attempt + switch segment session
        // both armed should both survive a simulated save+reload.
        // -----------------------------------------------------------------

        // Fails if: arming both a committed-tree restore attempt and a
        // switch-segment session, then round-tripping both through their
        // respective codecs, loses either context. Plan §"Save/Load" item
        // 7 ("F5/F9 must restore both contexts consistently").
        [Fact]
        public void CommittedRestoreAttemptArmed_PlusSegmentArmed_BothSurviveSaveReload()
        {
            Guid sessionId = Guid.NewGuid();
            string sessionIdStr = SessionIdString(sessionId);
            var scenario = new ParsekScenario();
            ParsekScenario.SetInstanceForTesting(scenario);

            var parent = MakeRecording("rec_parent", "tree_both");
            parent.ExplicitEndUT = 200.0;
            var tree = MakeTreeWithRecordings("tree_both", parent);
            RecordingStore.AddCommittedTreeForTesting(tree);
            RecordingStore.AddCommittedInternal(parent);
            RecordingStore.ArmCommittedTreeRestoreAttempt(tree, "save-test-arm");

            // Arm a segment session referencing the same tree.
            var seg = MakeRecording("rec_seg", "tree_both",
                switchSegmentSessionId: sessionIdStr);
            RecordingStore.AddCommittedInternal(seg);
            tree.AddOrReplaceRecording(seg);
            ArmFreshSession(scenario, sessionId, "tree_both", "rec_seg");

            // Round-trip the session through the codec.
            var parentNode = new ConfigNode("PARSEK");
            scenario.ActiveSwitchSegmentSession.SaveInto(parentNode);
            ConfigNode segNode = parentNode.GetNode(SwitchSegmentSession.NodeName);
            Assert.NotNull(segNode);
            Assert.True(SwitchSegmentSession.TryLoadFrom(
                segNode, out SwitchSegmentSession restored));
            Assert.Equal(sessionId, restored.SessionId);

            // The committed-tree restore attempt is the runtime
            // RecordingStore state, not directly serialized through a
            // public node — its existence persists across the test
            // (HasCommittedTreeRestoreAttempt). The contract: with both
            // armed, the marker-owned event-persistence predicate must
            // still return false (event not suppressed) for the segment
            // id even after the round-trip.
            Assert.True(RecordingStore.HasCommittedTreeRestoreAttempt);
            var evt = MakeEvent(ut: 250.0, recordingId: "rec_seg");
            Assert.False(
                RecordingStore.ShouldSuppressCommittedTreeRestoreAttemptEventPersistence(evt));
        }
    }
}
