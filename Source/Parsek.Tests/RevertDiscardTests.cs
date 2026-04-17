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
            RevertDetector.ResetForTesting();
        }

        public void Dispose()
        {
            RevertDetector.ResetForTesting();
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
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 100.0,
                eventType = GameStateEventType.ContractAccepted,
                key = "guid-mun",
                recordingId = "rec-mun",
            });
            Assert.True(RecordingStore.HasPendingTree);
            Assert.Single(GameStateStore.Events);

            RecordingStore.UnstashPendingTreeOnRevert();

            Assert.False(RecordingStore.HasPendingTree);
            // Events NOT purged — MilestoneStore.CurrentEpoch bump in ParsekScenario.OnLoad
            // filters them from ledger walks; the file/event data survives for F9-resume.
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

        [Fact]
        public void RevertToLaunch_OnLoadDispatch_SoftUnstashesInsteadOfHardDiscard()
        {
            // Regression for 2026-04-17 stress test: revert-to-launch (FLIGHT→FLIGHT, UT
            // jumps backwards) matched the `utWentBackwards && isFlightToFlight` branch at
            // ParsekScenario.cs:791, which called DiscardStashedOnQuickload → deleted sidecar
            // files + purged tagged events BEFORE the isRevert branch could run its soft
            // UnstashPendingTreeOnRevert path. Breaks F9-from-flight-quicksave because the
            // ACTIVE_TREE node in the flight F5 points at now-deleted sidecars.
            //
            // The fix gates the quickload-discard call with `&& !isRevert`. This test
            // simulates the dispatch order OnLoad uses post-fix: revert branch wins, sidecar
            // state survives. The fallback test just below simulates the *without* gate
            // behaviour to prove the bug's signature when the gate is absent.
            var tree = MakeTreeWithOneRec("Revert-to-Launch tree", "rec-r2l");
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);
            RecordingStore.PendingStashedThisTransition = true;
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 600.0,
                eventType = GameStateEventType.ContractAccepted,
                key = "contract-r2l",
                recordingId = "rec-r2l",
            });
            Assert.True(RecordingStore.HasPendingTree);
            Assert.Single(GameStateStore.Events);

            // Post-fix dispatch: isRevert=true => skip DiscardStashedOnQuickload; run
            // UnstashPendingTreeOnRevert instead.
            RecordingStore.UnstashPendingTreeOnRevert();

            Assert.False(RecordingStore.HasPendingTree);
            // Critical: the tagged event is still on disk/in memory. MilestoneStore's epoch
            // bump (elsewhere in the isRevert branch) filters it from post-revert ledger
            // walks, while the sidecars stay available for F9-from-flight-quicksave.
            Assert.Single(GameStateStore.Events);
            Assert.Contains(logLines, l =>
                l.Contains("Unstashed pending tree 'Revert-to-Launch tree'")
                && l.Contains("sidecar files preserved"));
        }

        [Fact]
        public void RevertToLaunch_UngatedDispatch_WouldDeleteSidecars_DocumentsBug()
        {
            // Negative-control: documents what the caller at ParsekScenario.cs:791 did BEFORE
            // the `&& !isRevert` gate. If a future refactor removes the gate, this test (read
            // alongside RevertToLaunch_OnLoadDispatch_SoftUnstashesInsteadOfHardDiscard above)
            // makes the contract explicit: DiscardStashedOnQuickload is a hard purge and must
            // never be reached from the revert path.
            var tree = MakeTreeWithOneRec("Would-be-victim tree", "rec-victim");
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);
            RecordingStore.PendingStashedThisTransition = true;
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 600.0,
                eventType = GameStateEventType.ContractAccepted,
                key = "contract-victim",
                recordingId = "rec-victim",
            });
            Assert.Single(GameStateStore.Events);

            // Simulates the pre-fix path: hard-discard ran before the revert branch.
            ParsekScenario.DiscardStashedOnQuickload(preChangeUT: 708.0, currentUT: 552.0);

            Assert.False(RecordingStore.HasPendingTree);
            // This is the destructive outcome the gate prevents: tagged events purged.
            Assert.Empty(GameStateStore.Events);
        }

        [Fact]
        public void UnstashPendingTreeOnRevert_DifferenceFromDiscardPendingTree()
        {
            // #434 vs #431 semantic contrast:
            //   UnstashPendingTreeOnRevert  -> soft clear, files + events stay
            //   DiscardPendingTree          -> hard clear, files + events purged
            var tree = MakeTreeWithOneRec("Contrast test", "rec-contrast");
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 50.0,
                eventType = GameStateEventType.TechResearched,
                key = "node-contrast",
                recordingId = "rec-contrast",
            });

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
