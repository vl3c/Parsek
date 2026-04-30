using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for #434: on revert, the pending tree is soft-unstashed (slot cleared) but
    /// sidecar files and tagged events are preserved so that a flight quicksave can still
    /// be F9'd back into. Contrast with merge-dialog Discard, which runs the full #431
    /// purge via <see cref="RecordingStore.DiscardPendingTree"/>.
    /// </summary>
    [Collection("Sequential")]
    public class RevertDiscardTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RevertDiscardTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            RewindInvokeContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RevertDetector.ResetForTesting();
        }

        public void Dispose()
        {
            RevertDetector.ResetForTesting();
            RewindInvokeContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            GameStateRecorder.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static RecordingTree MakeTreeWithOneRec(string treeName, string recId)
        {
            var tree = new RecordingTree
            {
                Id = System.Guid.NewGuid().ToString("N"),
                TreeName = treeName,
                RootRecordingId = recId,
                ActiveRecordingId = recId,
            };
            tree.Recordings[recId] = new Recording
            {
                RecordingId = recId,
                VesselName = "TestVessel",
                TreeId = tree.Id,
            };
            return tree;
        }

        [Fact]
        public void UnstashPendingTreeOnRevert_ClearsSlot_PreservesFilesAndEvents()
        {
            var tree = MakeTreeWithOneRec("Mun Lander", "rec-mun");
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);
            var munEvt = new GameStateEvent
            {
                ut = 100.0,
                eventType = GameStateEventType.ContractAccepted,
                key = "guid-mun",
                recordingId = "rec-mun",
            };
            GameStateStore.AddEvent(ref munEvt);
            Assert.True(RecordingStore.HasPendingTree);
            Assert.Single(GameStateStore.Events);

            RecordingStore.UnstashPendingTreeOnRevert();

            Assert.False(RecordingStore.HasPendingTree);
            // Events NOT purged — soft-unstash preserves the tagged rows and sidecars for
            // F9-resume, while current-timeline visibility keeps them out of normal
            // ledger/milestone walks after the tree is unstashed.
            Assert.Single(GameStateStore.Events);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]") && l.Contains("Unstashed pending tree 'Mun Lander'")
                && l.Contains("sidecar files preserved"));
        }

        [Fact]
        public void UnstashPendingTreeOnRevert_FromLimboState_WorksToo()
        {
            var tree = MakeTreeWithOneRec("F5 mid-mission", "rec-limbo");
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);
            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);

            RecordingStore.UnstashPendingTreeOnRevert();

            Assert.False(RecordingStore.HasPendingTree);
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
            Assert.Contains(logLines, l =>
                l.Contains("Unstashed pending tree 'F5 mid-mission'")
                && l.Contains("was state=Limbo"));
        }

        [Fact]
        public void UnstashPendingTreeOnRevert_FromLimboVesselSwitchState_WorksToo()
        {
            var tree = MakeTreeWithOneRec("Vessel switch", "rec-switch");
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);
            RecordingStore.SetPendingTreeStateForTesting(PendingTreeState.LimboVesselSwitch);

            RecordingStore.UnstashPendingTreeOnRevert();

            Assert.False(RecordingStore.HasPendingTree);
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
            Assert.Contains(logLines, l =>
                l.Contains("was state=LimboVesselSwitch"));
        }

        [Fact]
        public void UnstashPendingTreeOnRevert_NoPendingTree_IsNoop()
        {
            RecordingStore.UnstashPendingTreeOnRevert();

            Assert.False(RecordingStore.HasPendingTree);
            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE]") && l.Contains("UnstashPendingTreeOnRevert called with no pending tree"));
        }

        // --- P2 fix: PendingScienceSubjects clears on revert so they don't leak forward ---

        [Fact]
        public void UnstashPendingTreeOnRevert_ClearsPendingScienceSubjects()
        {
            var tree = MakeTreeWithOneRec("Mun sci", "rec-sci");
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);
            GameStateRecorder.PendingScienceSubjects.Add(new PendingScienceSubject
            {
                subjectId = "crewReport@MunSrfLandedMidlands",
                science = 15.0f,
                subjectMaxValue = 30.0f,
            });
            GameStateRecorder.PendingScienceSubjects.Add(new PendingScienceSubject
            {
                subjectId = "evaReport@MunSrfLandedMidlands",
                science = 12.0f,
                subjectMaxValue = 30.0f,
            });
            Assert.Equal(2, GameStateRecorder.PendingScienceSubjects.Count);

            RecordingStore.UnstashPendingTreeOnRevert();

            Assert.Empty(GameStateRecorder.PendingScienceSubjects);
            Assert.Contains(logLines, l =>
                l.Contains("Unstashed pending tree") && l.Contains("2 pending science subject(s) cleared"));
        }

        [Fact]
        public void UnstashPendingTreeOnRevert_NoPendingTree_StillClearsStraySubjects()
        {
            GameStateRecorder.PendingScienceSubjects.Add(new PendingScienceSubject
            {
                subjectId = "stray",
                science = 1.0f,
            });

            RecordingStore.UnstashPendingTreeOnRevert();

            Assert.Empty(GameStateRecorder.PendingScienceSubjects);
            Assert.Contains(logLines, l =>
                l.Contains("cleared 1 in-flight science subject(s) even with no pending tree"));
        }

        // --- P1 fix: event-based revert detection (replaces epoch-regression heuristic) ---

        [Fact]
        public void RevertDetector_Consume_ReturnsNoneWhenUnarmed()
        {
            Assert.Equal(RevertKind.None, RevertDetector.Consume("test-cold"));
        }

        [Fact]
        public void RevertDetector_ArmAndConsume_OneShot()
        {
            RevertDetector.SetPendingForTesting(RevertKind.Launch);
            Assert.Equal(RevertKind.Launch, RevertDetector.PendingKind);

            var first = RevertDetector.Consume("test-first");
            Assert.Equal(RevertKind.Launch, first);

            // Simulates the F9-to-pre-revert-quicksave case: after the revert's OnLoad
            // consumed the flag, the subsequent F9 OnLoad sees None and classifies as
            // a regular quickload, not a second revert.
            var second = RevertDetector.Consume("test-second-F9");
            Assert.Equal(RevertKind.None, second);
        }

        [Fact]
        public void RevertDetector_PrelaunchKind_ConsumesCorrectly()
        {
            RevertDetector.SetPendingForTesting(RevertKind.Prelaunch);
            Assert.Equal(RevertKind.Prelaunch, RevertDetector.Consume("test-vab"));
            Assert.Equal(RevertKind.None, RevertDetector.PendingKind);
        }

        [Fact]
        public void RevertDetector_Subscribe_DoesNotThrowOnKspGameEventsAdd()
        {
            // Regression: KSP's EventData<T>.EvtDelegate..ctor reads evt.Target.GetType().Name
            // without a null check. A delegate bound to a static method has Target == null,
            // so GameEvents.*.Add(staticMethod) NREs and aborts ParsekScenario.OnLoad — which
            // in production (2026-04-17 playtest) silently skipped active-tree restore and the
            // merge-dialog dispatch. Handlers must be instance-bound so Target is non-null.
            try
            {
                RevertDetector.Subscribe();
                RevertDetector.Subscribe(); // idempotent re-call must also not throw
            }
            finally
            {
                RevertDetector.Unsubscribe();
            }
        }

        [Theory]
        // Revert-to-Launch (FLIGHT→FLIGHT, UT regresses, isRevert=true): the revert branch
        // owns pending-tree handling. Gate must return false so DiscardStashedOnQuickload
        // doesn't run and delete sidecar files that F9-from-flight-quicksave still needs.
        [InlineData(true,  true,  true,  false)]
        // Revert-to-VAB (FLIGHT→EDITOR, isRevert=true, isFlightToFlight=false): same answer,
        // different reason — not flight-to-flight so the quickload heuristic never matched.
        [InlineData(true,  false, true,  false)]
        // Pure F5/F9 quickload (no revert, UT regresses, flight-to-flight): hard-discard runs.
        // This is the original Bug A case the quickload path was introduced to handle.
        [InlineData(true,  true,  false, true)]
        // Quickload-shaped but UT didn't regress (scene reload with forward or equal UT):
        // nothing stashed this transition that would be stale.
        [InlineData(false, true,  false, false)]
        // Not flight-to-flight and not a revert: some non-FLIGHT-origin transition. Gate
        // stays closed; other paths handle it.
        [InlineData(true,  false, false, false)]
        public void ShouldRunQuickloadDiscard_TruthTable(
            bool utWentBackwards, bool isFlightToFlight, bool isRevert, bool expected)
        {
            // Regression for the 2026-04-17 stress test. Revert-to-Launch previously matched
            // `utWentBackwards && isFlightToFlight` at ParsekScenario.cs:801 and ran
            // DiscardStashedOnQuickload before the isRevert branch could soft-unstash,
            // deleting sidecar files and purging tagged events tied to the reverted flight.
            // Extracting the gate as a pure function pins the contract: revert ⇒ never run
            // the hard-discard, even when UT regresses and the scene stays in FLIGHT.
            Assert.Equal(
                expected,
                ParsekScenario.ShouldRunQuickloadDiscard(
                    utWentBackwards,
                    isFlightToFlight,
                    isRevert,
                    isReFlySessionActive: false));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, false, true)]
        [InlineData(false, true, false)]
        [InlineData(false, true, true)]
        [InlineData(true, false, false)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        [InlineData(true, true, true)]
        public void ShouldRunQuickloadDiscard_ActiveReFlySession_ReturnsFalse(
            bool utWentBackwards, bool isFlightToFlight, bool isRevert)
        {
            Assert.False(ParsekScenario.ShouldRunQuickloadDiscard(
                utWentBackwards,
                isFlightToFlight,
                isRevert,
                isReFlySessionActive: true));
        }

        [Fact]
        public void ShouldRunQuickloadDiscard_ActiveReFlyMarker_ReturnsFalse()
        {
            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = "sess_gate_marker",
                    RewindPointId = "rp_gate_marker",
                },
            });

            Assert.False(ParsekScenario.ShouldRunQuickloadDiscard(
                utWentBackwards: true,
                isFlightToFlight: true,
                isRevert: false));
        }

        [Fact]
        public void ShouldRunQuickloadDiscard_PendingReFlyInvoke_ReturnsFalse()
        {
            RewindInvokeContext.Pending = true;
            RewindInvokeContext.SessionId = "sess_gate_pending";
            RewindInvokeContext.RewindPointId = "rp_gate_pending";

            Assert.False(ParsekScenario.ShouldRunQuickloadDiscard(
                utWentBackwards: true,
                isFlightToFlight: true,
                isRevert: false));
        }

        [Fact]
        public void DiscardStashedOnQuickload_RefusesWhenRevertDetectorArmed()
        {
            // Defense-in-depth for #434: even if a future refactor removes the
            // ShouldRunQuickloadDiscard gate from OnLoad's dispatch (or inlines the condition
            // without the !isRevert clause), DiscardStashedOnQuickload itself refuses to
            // proceed when RevertDetector is armed. Without this check, a buggy caller
            // would delete sidecar files tied to the reverted flight and break
            // F9-from-flight-quicksave.
            var tree = MakeTreeWithOneRec("Armed-revert tree", "rec-armed");
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);
            RecordingStore.PendingStashedThisTransition = true;
            var armedEvt = new GameStateEvent
            {
                ut = 600.0,
                eventType = GameStateEventType.ContractAccepted,
                key = "contract-armed",
                recordingId = "rec-armed",
            };
            GameStateStore.AddEvent(ref armedEvt);
            RevertDetector.SetPendingForTesting(RevertKind.Launch);

            ParsekScenario.DiscardStashedOnQuickload(preChangeUT: 708.0, currentUT: 552.0);

            // Tree and event must survive — the guard refused.
            Assert.True(RecordingStore.HasPendingTree);
            Assert.Single(GameStateStore.Events);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("refusing to run with armed RevertDetector"));
        }

        [Fact]
        public void RevertPath_WithoutPendingTree_ClearsStalePendingScienceSubjects()
        {
            // Regression for 2026-04-17 review P2: UnstashPendingTreeOnRevert has a no-pending-tree
            // branch that clears stale PendingScienceSubjects accumulated during the reverted
            // flight. ParsekScenario.OnLoad previously gated the call behind HasPendingTree, so
            // that cleanup was unreachable — a revert that captured science subjects but never
            // stashed a tree would leak the subjects onto the next unrelated commit. The fix
            // calls UnstashPendingTreeOnRevert unconditionally on the revert path; this test
            // pins the no-tree cleanup contract the new dispatch relies on.
            Assert.False(RecordingStore.HasPendingTree);
            GameStateRecorder.PendingScienceSubjects.Add(new PendingScienceSubject
            {
                subjectId = "mysteryGoo@KerbinSrfLandedLaunchPad",
                science = 1.5f,
            });
            GameStateRecorder.PendingScienceSubjects.Add(new PendingScienceSubject
            {
                subjectId = "temperatureScan@KerbinFlyingLow",
                science = 2.8f,
            });
            Assert.Equal(2, GameStateRecorder.PendingScienceSubjects.Count);

            RecordingStore.UnstashPendingTreeOnRevert();

            Assert.Empty(GameStateRecorder.PendingScienceSubjects);
            Assert.Contains(logLines, l =>
                l.Contains("cleared 2 in-flight science subject(s) even with no pending tree"));
        }

        [Fact]
        public void UnstashPendingTreeOnRevert_AfterRevertToLaunch_PreservesSidecarState()
        {
            // Complements ShouldRunQuickloadDiscard_TruthTable by asserting the OTHER half
            // of the fix: when the gate correctly skips the hard-discard, the revert branch's
            // soft-unstash preserves the tagged event (sidecar files aren't touched either —
            // that invariant lives in RecordingStore and is already pinned by the existing
            // UnstashPendingTreeOnRevert_ClearsSlot_PreservesFilesAndEvents test).
            var tree = MakeTreeWithOneRec("Revert-to-Launch tree", "rec-r2l");
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);
            RecordingStore.PendingStashedThisTransition = true;
            var r2lEvt = new GameStateEvent
            {
                ut = 600.0,
                eventType = GameStateEventType.ContractAccepted,
                key = "contract-r2l",
                recordingId = "rec-r2l",
            };
            GameStateStore.AddEvent(ref r2lEvt);

            // Post-fix OnLoad dispatch: ShouldRunQuickloadDiscard returns false on the revert
            // path (asserted in the truth-table test above), so DiscardStashedOnQuickload
            // doesn't run and the flow reaches the isRevert branch here.
            RecordingStore.UnstashPendingTreeOnRevert();

            Assert.False(RecordingStore.HasPendingTree);
            // The tagged event survives. Once the tree is unstashed it is no longer in the
            // current-timeline visibility set, so post-revert ledger walks ignore it while
            // the sidecars stay available for F9-from-flight-quicksave.
            Assert.Single(GameStateStore.Events);
            Assert.Contains(logLines, l =>
                l.Contains("Unstashed pending tree 'Revert-to-Launch tree'")
                && l.Contains("sidecar files preserved"));
        }

        [Fact]
        public void UnstashPendingTreeOnRevert_DifferenceFromDiscardPendingTree()
        {
            // #434 vs #431 semantic contrast:
            //   UnstashPendingTreeOnRevert  -> soft clear, files + events stay
            //   DiscardPendingTree          -> hard clear, files + events purged
            var tree = MakeTreeWithOneRec("Contrast test", "rec-contrast");
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);
            var contrastEvt = new GameStateEvent
            {
                ut = 50.0,
                eventType = GameStateEventType.TechResearched,
                key = "node-contrast",
                recordingId = "rec-contrast",
            };
            GameStateStore.AddEvent(ref contrastEvt);

            RecordingStore.UnstashPendingTreeOnRevert();
            Assert.Single(GameStateStore.Events); // tagged event retained on unstash

            // Restash and discard the second path — events should be purged.
            var tree2 = MakeTreeWithOneRec("Discard path", "rec-contrast");
            RecordingStore.StashPendingTree(tree2, PendingTreeState.Finalized);
            RecordingStore.DiscardPendingTree();
            Assert.Empty(GameStateStore.Events); // purge confirmed
        }
    }
}
