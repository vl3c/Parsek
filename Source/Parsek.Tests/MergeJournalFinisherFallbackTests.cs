using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling 2026-11-18 (RULE 4, "Both, in that order"): a Re-Fly merge the
    /// load-time finisher cannot complete must never abort the load. After one failed
    /// retry the finisher hides the WHOLE old recording instead of cutting it; when that
    /// fails too it gives the merge up with the old flight as it was and the journal
    /// cleared, so Discard is no longer refused.
    /// </summary>
    [Collection("Sequential")]
    public class MergeJournalFinisherFallbackTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> screenMessages = new List<string>();
        private readonly List<string> durableSaveCheckpoints = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public MergeJournalFinisherFallbackTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.ScreenMessageSinkForTesting = (m, d) => screenMessages.Add(m);

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            MergeJournalOrchestrator.ResetTestOverrides();
            RecordingTreeSplitter.SplitFaultForTesting = null;
            MergeJournalOrchestrator.DurableSaveForTesting = label => durableSaveCheckpoints.Add(label);
        }

        public void Dispose()
        {
            RecordingTreeSplitter.SplitFaultForTesting = null;
            MergeJournalOrchestrator.ResetTestOverrides();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
        }

        // ---------- Fixtures ------------------------------------------------

        private static void InstallTree(string treeId, IEnumerable<Recording> recordings,
            List<BranchPoint> branchPoints = null)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Test_" + treeId,
                BranchPoints = branchPoints ?? new List<BranchPoint>(),
            };
            foreach (var rec in recordings)
            {
                tree.AddOrReplaceRecording(rec);
                RecordingStore.AddRecordingWithTreeForTesting(rec, treeId);
            }
            var trees = RecordingStore.CommittedTrees;
            for (int i = trees.Count - 1; i >= 0; i--)
                if (trees[i].Id == treeId) trees.RemoveAt(i);
            trees.Add(tree);
        }

        private static ParsekScenario InstallScenario(ReFlySessionMarker marker)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = marker,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();
            return scenario;
        }

        private static ReFlySessionMarker Marker(string originId, string provisionalId)
        {
            return new ReFlySessionMarker
            {
                SessionId = "sess_fb",
                TreeId = "tree_1",
                ActiveReFlyRecordingId = provisionalId,
                OriginChildRecordingId = originId,
                RewindPointId = "rp_1",
                InvokedUT = 0.0,
            };
        }

        private static Recording Provisional(string id, TerminalState terminal)
        {
            var p = new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = "tree_1",
                MergeState = MergeState.NotCommitted,
                TerminalStateValue = terminal,
            };
            p.Points.Add(new TrajectoryPoint { ut = 40.0 });
            p.Points.Add(new TrajectoryPoint { ut = 60.0 });
            return p;
        }

        // Same shape as MergeJournalOrchestratorTests.BuildSpanningOrigin: a sample at
        // the cut keeps SplitAtSection off its Unity-only interpolation branch.
        private static Recording SpanningOrigin(string id, double start, double mid, double end)
        {
            var origin = new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = "tree_1",
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Destroyed,
            };
            origin.Points.Add(new TrajectoryPoint { ut = start });
            origin.Points.Add(new TrajectoryPoint { ut = mid });
            origin.Points.Add(new TrajectoryPoint { ut = end });
            origin.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = start,
                endUT = end,
                sampleRateHz = 1f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = start },
                    new TrajectoryPoint { ut = mid },
                    new TrajectoryPoint { ut = end },
                },
            });
            return origin;
        }

        private static MergeJournal Journal(string phase)
        {
            return new MergeJournal
            {
                JournalId = "mj_fb",
                SessionId = "sess_fb",
                TreeId = "tree_1",
                Phase = phase,
                StartedRealTime = "2026-11-18T10:00:00.0000000Z",
            };
        }

        // ---------- RULE 4 stage 3: give up, old flight kept --------------

        [Fact]
        public void Finisher_SlotLookupFailsEveryTime_GivesUp_LoadContinues_OldFlightKept()
        {
            // An Orbiting provisional whose slot cannot resolve throws from
            // ClassifyMergeStateOrThrow on every run (deterministic): today the finisher
            // re-threw and OnLoad aborted on every load, journal never clearing.
            var origin = new Recording
            {
                RecordingId = "rec_origin", VesselName = "o", TreeId = "tree_1",
                MergeState = MergeState.Immutable, TerminalStateValue = TerminalState.Destroyed,
            };
            origin.Points.Add(new TrajectoryPoint { ut = 1.0 });
            var provisional = Provisional("rec_prov", TerminalState.Orbiting);
            provisional.ParentBranchPointId = "bp_missing";
            InstallTree("tree_1", new[] { origin, provisional });
            var marker = Marker("rec_origin", "rec_prov");
            var scenario = InstallScenario(marker);
            // Rows an earlier interrupted attempt of THIS merge left in memory (saved by an
            // ordinary save), plus an older unrelated row and tombstone that must survive.
            scenario.RecordingSupersedes.Add(new RecordingSupersedeRelation
            {
                RelationId = "rsr_this", OldRecordingId = "rec_origin", NewRecordingId = "rec_prov",
                CreatedRealTime = "2026-11-18T10:00:01.0000000Z",
            });
            scenario.RecordingSupersedes.Add(new RecordingSupersedeRelation
            {
                RelationId = "rsr_old", OldRecordingId = "rec_a", NewRecordingId = "rec_b",
                CreatedRealTime = "2026-01-01T00:00:00.0000000Z",
            });
            scenario.LedgerTombstones.Add(new LedgerTombstone
            {
                TombstoneId = "tomb_this", ActionId = "act_1", RetiringRecordingId = "rec_prov",
                CreatedRealTime = "2026-11-18T10:00:02.0000000Z",
            });
            scenario.LedgerTombstones.Add(new LedgerTombstone
            {
                TombstoneId = "tomb_old", ActionId = "act_0", RetiringRecordingId = "rec_b",
                CreatedRealTime = "2026-01-01T00:00:00.0000000Z",
            });
            scenario.ActiveMergeJournal = Journal(MergeJournal.Phases.Tombstone);
            var recalcs = new List<string>();
            MergeJournalOrchestrator.RecalcAfterMarkerClearedForTesting = reason => recalcs.Add(reason);

            bool completed = MergeJournalOrchestrator.RunFinisher();

            Assert.False(completed);
            Assert.Null(scenario.ActiveMergeJournal); // Discard is no longer refused
            Assert.Same(marker, scenario.ActiveReFlySessionMarker);
            Assert.Equal(MergeState.NotCommitted, provisional.MergeState);
            Assert.DoesNotContain(scenario.RecordingSupersedes, r => r.NewRecordingId == "rec_prov");
            Assert.DoesNotContain(scenario.LedgerTombstones, t => t.RetiringRecordingId == "rec_prov");
            Assert.Contains(scenario.RecordingSupersedes, r => r.RelationId == "rsr_old");
            Assert.Contains(scenario.LedgerTombstones, t => t.TombstoneId == "tomb_old");
            Assert.Contains(logLines, l => l.Contains("[MergeJournal]")
                && l.Contains("Finisher escalating sess=sess_fb") && l.Contains("retryFailures=1"));
            Assert.Contains(logLines, l => l.Contains("[MergeJournal]")
                && l.Contains("Finisher gave up sess=sess_fb") && l.Contains("Discard unlocked")
                && l.Contains("rowsRemoved=1") && l.Contains("tombstonesRemoved=1"));
            Assert.Contains(screenMessages, m => m.Contains("Re-Fly merge could not be finished"));
            // The load's own recalc ran before the finisher with this merge's tombstones in
            // place; removing them must re-derive KSP state at once (review item 2).
            Assert.Equal(new[] { "merge-journal-finisher-gave-up" }, recalcs.ToArray());
        }

        [Fact]
        public void Finisher_GivesUpAfterFinalize_KeepsTheSessionRewindPointsLikeACompletedMerge()
        {
            // After Finalize the merge state is final, so a give-up there is a completed merge:
            // the session's rewind points (the Unfinished Flight quicksaves of slots the merge
            // left open) must be promoted like Durable1Done's step does, not left
            // session-provisional for the load-time sweep to delete once the marker clears
            // (review item 3).
            var origin = new Recording
            {
                RecordingId = "rec_origin", VesselName = "o", TreeId = "tree_1",
                MergeState = MergeState.Immutable, TerminalStateValue = TerminalState.Destroyed,
            };
            origin.Points.Add(new TrajectoryPoint { ut = 1.0 });
            var provisional = Provisional("rec_prov", TerminalState.Destroyed);
            provisional.MergeState = MergeState.CommittedProvisional;
            InstallTree("tree_1", new[] { origin, provisional });
            var marker = Marker("rec_origin", "rec_prov");
            var scenario = InstallScenario(marker);
            var sessionRp = new RewindPoint
            {
                RewindPointId = "rp_session", SessionProvisional = true, CreatingSessionId = "sess_fb",
            };
            scenario.RewindPoints.Add(sessionRp);
            scenario.ActiveMergeJournal = Journal(MergeJournal.Phases.MarkerCleared);
            var recalcs = new List<string>();
            MergeJournalOrchestrator.RecalcAfterMarkerClearedForTesting = reason =>
            {
                recalcs.Add(reason);
                if (reason == "merge-journal-finisher-marker-cleared")
                    throw new InvalidOperationException("synthetic recalc failure");
            };

            bool completed = MergeJournalOrchestrator.RunFinisher();

            Assert.False(completed);
            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.False(sessionRp.SessionProvisional);
            Assert.Null(sessionRp.CreatingSessionId);
            Assert.Contains("merge-journal-finisher-gave-up-after-finalize", recalcs);
            Assert.Contains(logLines, l => l.Contains("[MergeJournal]")
                && l.Contains("Finisher gave up sess=sess_fb after phase=MarkerCleared")
                && l.Contains("session rewind points promoted like a completed merge"));
        }

        // ---------- RULE 4 stage 2: whole-recording fallback ----------------

        [Fact]
        public void Finisher_SplitFailsEveryTime_HidesTheWholeOldRecording()
        {
            var origin = SpanningOrigin("rec_origin", 8.0, 34.0, 53.0);
            var provisional = Provisional("rec_prov", TerminalState.Landed);
            InstallTree("tree_1", new[] { origin, provisional });
            var marker = Marker("rec_origin", "rec_prov");
            marker.RewindPointUT = 34.0;
            var scenario = InstallScenario(marker);
            scenario.ActiveMergeJournal = Journal(MergeJournal.Phases.TreeMerge);
            int splitCalls = 0;
            RecordingTreeSplitter.SplitFaultForTesting = rec =>
            {
                splitCalls++;
                throw new InvalidOperationException("synthetic split failure");
            };

            bool completed = MergeJournalOrchestrator.RunFinisher();

            Assert.True(completed);
            Assert.Equal(1, splitCalls); // the retry tried to cut; the fallback did not
            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Null(scenario.ActiveReFlySessionMarker);
            // No TIP: the split rolled back and the fallback never cut.
            // The split's rollback puts a pre-split clone in the origin's slot.
            Assert.Equal(2, RecordingStore.CommittedRecordings.Count);
            Assert.Equal(53.0, RecordingStore.CommittedRecordings
                .Single(r => r.RecordingId == "rec_origin").EndUT);
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_origin" && r.NewRecordingId == "rec_prov");
            Assert.Contains(logLines, l => l.Contains("[MergeJournal]")
                && l.Contains("split skipped (whole-recording fallback)"));
        }

        [Fact]
        public void Finisher_WholeRecordingFallbackAfterACompletedSplit_SupersedesHeadToo()
        {
            var origin = SpanningOrigin("rec_origin", 8.0, 34.0, 53.0);
            var provisional = Provisional("rec_prov", TerminalState.Landed);
            InstallTree("tree_1", new[] { origin, provisional });
            var marker = Marker("rec_origin", "rec_prov");
            marker.RewindPointUT = 34.0;
            var scenario = InstallScenario(marker);
            MergeJournalOrchestrator.FaultInjectionPoint = MergeJournalOrchestrator.Phase.Split;
            Assert.Throws<MergeJournalOrchestrator.FaultInjectionException>(
                () => MergeJournalOrchestrator.RunMerge(marker, provisional));
            MergeJournalOrchestrator.FaultInjectionPoint = null;
            var tip = RecordingStore.CommittedRecordings.Single(r => r.ChainIndex == 1);
            Assert.Equal(tip.RecordingId, marker.SupersedeTargetId);
            // Stage 2 entered directly, the state the in-load escalation leaves after a retry
            // that failed past the split: the fallback starts with the TIP already cut.
            bool completed = MergeJournalOrchestrator.RunFinisherCore(startWithWholeRecordingFallback: true);

            Assert.True(completed);
            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_origin" && r.NewRecordingId == "rec_prov");
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == tip.RecordingId && r.NewRecordingId == "rec_prov");
            Assert.Contains(logLines, l => l.Contains("[MergeJournal]")
                && l.Contains("closure root moved back to HEAD=rec_origin"));
        }

        [Fact]
        public void RemoveSessionRows_KeepsOlderRowsNamingTheSameRecording()
        {
            // An in-place continuation's provisional is the origin, which an older merge
            // may name: only rows created at or after the journal start go.
            var rows = new List<RecordingSupersedeRelation>
            {
                new RecordingSupersedeRelation { OldRecordingId = "x", NewRecordingId = "p",
                    CreatedRealTime = "2026-01-01T00:00:00.0000000Z" },
                new RecordingSupersedeRelation { OldRecordingId = "y", NewRecordingId = "p",
                    CreatedRealTime = "2026-11-18T10:00:05.0000000Z" },
                new RecordingSupersedeRelation { OldRecordingId = "z", NewRecordingId = "q",
                    CreatedRealTime = "2026-11-18T10:00:05.0000000Z" },
            };
            int removed = MergeJournalOrchestrator.RemoveSessionRows(
                rows, "p", "2026-11-18T10:00:00.0000000Z",
                r => r.NewRecordingId, r => r.CreatedRealTime);
            Assert.Equal(1, removed);
            Assert.DoesNotContain(rows, r => r.OldRecordingId == "y");
            Assert.Contains(rows, r => r.OldRecordingId == "x");
            Assert.Contains(rows, r => r.OldRecordingId == "z");
        }
    }
}
