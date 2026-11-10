using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Characterization of the <c>ParsekFlight.RestoreActiveTreeFromPending</c> coroutine on the
    /// live FLIGHT scene. The coroutine reads <c>FlightGlobals.ActiveVessel</c> and
    /// <c>UnityEngine.Time.time</c> between its yields, so its body cannot run headless; the
    /// decision units it calls are unit-tested in <c>RestoreActiveTreeFromPendingUnitsTests</c>.
    ///
    /// Each test drives the REAL coroutine (reached by reflection, since it is private) over a
    /// synthetic pending tree and then puts the pending slot back as it found it. Only the
    /// paths that leave the live session untouched are driven: the no-pending-tree skip, the
    /// unresolvable active recording and the 3 s give-up. The success path (pop the tree,
    /// start a recorder) and the fresh-rollout disposition change the live session and are
    /// covered by the quickload lanes instead.
    ///
    /// Category TreeRestoreCoroutine is pinned by no harness spec; run it from the test
    /// runner (Ctrl+Shift+T) in any FLIGHT scene.
    /// </summary>
    public class TreeRestoreCoroutineInGameTests
    {
        private const string Category = "TreeRestoreCoroutine";
        private const string SyntheticTreeId = "ingame-restore-char-tree";

        [InGameTest(Category = Category, Scene = GameScenes.FLIGHT,
            Description = "RestoreActiveTreeFromPending with no pending tree logs the skip and "
                + "ends at once with the reentrancy guard cleared")]
        public IEnumerator RestoreFromPending_NoPendingTree_Skips()
        {
            ParsekFlight flight = ParsekFlight.Instance;
            if (!TrySkipUnlessQuiet(flight, out string skipReason))
            {
                InGameAssert.Skip(skipReason);
                yield break;
            }

            var lines = new List<string>();
            Action<string> prevSink = ParsekLog.TestSinkForTesting;
            bool? prevVerbose = ParsekLog.VerboseOverrideForTesting;
            int frames = 0;
            try
            {
                ParsekLog.TestSinkForTesting = line => lines.Add(line);
                ParsekLog.VerboseOverrideForTesting = true;
                IEnumerator it = StartRestore(flight);
                while (it.MoveNext())
                {
                    frames++;
                    yield return it.Current;
                }
            }
            finally
            {
                ParsekLog.TestSinkForTesting = prevSink;
                ParsekLog.VerboseOverrideForTesting = prevVerbose;
            }

            InGameAssert.AreEqual(0, frames, "the skip path yields no frame");
            InGameAssert.IsFalse(ParsekFlight.restoringActiveTree,
                "restoringActiveTree is cleared by the finally block");
            InGameAssert.IsFalse(PendingTreeSlot.HasPendingTree, "no pending tree appears");
            InGameAssert.IsTrue(
                CountContaining(lines, "RestoreActiveTreeFromPending: no pending-Limbo tree, skipping") == 1,
                "the skip line is logged once");
        }

        [InGameTest(Category = Category, Scene = GameScenes.FLIGHT,
            Description = "RestoreActiveTreeFromPending over a Limbo tree whose active recording id "
                + "resolves to nothing warns and leaves the tree pending in Limbo")]
        public IEnumerator RestoreFromPending_UnresolvableActiveRecording_LeavesTreeInLimbo()
        {
            ParsekFlight flight = ParsekFlight.Instance;
            if (!TrySkipUnlessQuiet(flight, out string skipReason))
            {
                InGameAssert.Skip(skipReason);
                yield break;
            }

            var tree = new RecordingTree
            {
                Id = SyntheticTreeId,
                TreeName = "Restore Char Unresolvable",
                ActiveRecordingId = "ingame-restore-char-missing",
            };

            var lines = new List<string>();
            Action<string> prevSink = ParsekLog.TestSinkForTesting;
            bool? prevVerbose = ParsekLog.VerboseOverrideForTesting;
            var slot = SlotSnapshot.Capture();
            int frames = 0;
            bool stillPending;
            PendingTreeState stateAfter;
            RecordingTree pendingAfter;
            try
            {
                ParsekLog.TestSinkForTesting = line => lines.Add(line);
                ParsekLog.VerboseOverrideForTesting = true;
                PendingTreeSlot.StashPendingTree(tree, PendingTreeState.Limbo);
                IEnumerator it = StartRestore(flight);
                while (it.MoveNext())
                {
                    frames++;
                    yield return it.Current;
                }
                stillPending = PendingTreeSlot.HasPendingTree;
                stateAfter = PendingTreeSlot.PendingTreeStateValue;
                pendingAfter = PendingTreeSlot.PendingTree;
            }
            finally
            {
                slot.Restore();
                ParsekLog.TestSinkForTesting = prevSink;
                ParsekLog.VerboseOverrideForTesting = prevVerbose;
            }

            InGameAssert.AreEqual(0, frames, "the unresolvable path yields no frame");
            InGameAssert.IsTrue(stillPending, "the tree stays pending");
            InGameAssert.IsTrue(ReferenceEquals(tree, pendingAfter), "the same tree stays pending");
            InGameAssert.AreEqual(PendingTreeState.Limbo, stateAfter, "the tree stays in Limbo");
            InGameAssert.IsFalse(ParsekFlight.restoringActiveTree,
                "restoringActiveTree is cleared by the finally block");
            InGameAssert.IsTrue(
                CountContaining(lines, "RestoreActiveTreeFromPending: pending tree 'Restore Char Unresolvable' "
                    + "has no resolvable activeRecordingId") == 1,
                "the unresolvable warn is logged once");
            InGameAssert.IsTrue(CountContaining(lines, "waiting for vessel") == 0,
                "the wait loop is never entered");
        }

        [InGameTest(Category = Category, Scene = GameScenes.FLIGHT,
            Description = "RestoreActiveTreeFromPending refuses a recording that shares the active "
                + "vessel's pid but carries another launch guid (and so does its EVA parent): each "
                + "guid reject is logged once across the 3 s wait, then it gives up and leaves the "
                + "tree in Limbo")]
        public IEnumerator RestoreFromPending_GuidMismatchedSamePid_GivesUpAfterWait()
        {
            ParsekFlight flight = ParsekFlight.Instance;
            if (!TrySkipUnlessQuiet(flight, out string skipReason))
            {
                InGameAssert.Skip(skipReason);
                yield break;
            }
            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null || active.id == Guid.Empty)
            {
                InGameAssert.Skip("needs an active vessel with a launch guid");
                yield break;
            }

            uint livePid = active.persistentId;
            string parentId = "ingame-restore-char-parent";
            string childId = "ingame-restore-char-child";
            var parent = new Recording
            {
                RecordingId = parentId,
                TreeId = SyntheticTreeId,
                VesselName = "Restore Char Parent",
                VesselPersistentId = livePid,
                RecordedVesselGuid = Guid.NewGuid().ToString("N"),
            };
            var child = new Recording
            {
                RecordingId = childId,
                TreeId = SyntheticTreeId,
                VesselName = "Restore Char Child",
                VesselPersistentId = livePid,
                RecordedVesselGuid = Guid.NewGuid().ToString("N"),
                ParentRecordingId = parentId,
            };
            var tree = new RecordingTree
            {
                Id = SyntheticTreeId,
                TreeName = "Restore Char GiveUp",
                RootRecordingId = parentId,
                ActiveRecordingId = childId,
            };
            tree.Recordings[parentId] = parent;
            tree.Recordings[childId] = child;

            var lines = new List<string>();
            Action<string> prevSink = ParsekLog.TestSinkForTesting;
            bool? prevVerbose = ParsekLog.VerboseOverrideForTesting;
            uint prevFreshRolloutPid = SceneEntryVesselScope.SceneEntryFreshRolloutVesselPid;
            var slot = SlotSnapshot.Capture();
            int frames = 0;
            float startTime = UnityEngine.Time.time;
            float elapsed;
            bool stillPending;
            PendingTreeState stateAfter;
            string activeIdAfter;
            uint childPidAfter;
            try
            {
                ParsekLog.TestSinkForTesting = line => lines.Add(line);
                ParsekLog.VerboseOverrideForTesting = true;
                // The scene-entry fresh rollout is refused before any match and then disposes
                // the pending tree (commit or discard); clear it so the give-up path runs.
                SceneEntryVesselScope.SceneEntryFreshRolloutVesselPid = 0;
                PendingTreeSlot.StashPendingTree(tree, PendingTreeState.Limbo);
                IEnumerator it = StartRestore(flight);
                while (it.MoveNext())
                {
                    frames++;
                    yield return it.Current;
                }
                elapsed = UnityEngine.Time.time - startTime;
                stillPending = ReferenceEquals(tree, PendingTreeSlot.PendingTree);
                stateAfter = PendingTreeSlot.PendingTreeStateValue;
                activeIdAfter = tree.ActiveRecordingId;
                childPidAfter = child.VesselPersistentId;
            }
            finally
            {
                SceneEntryVesselScope.SceneEntryFreshRolloutVesselPid = prevFreshRolloutPid;
                slot.Restore();
                ParsekLog.TestSinkForTesting = prevSink;
                ParsekLog.VerboseOverrideForTesting = prevVerbose;
            }

            InGameAssert.IsTrue(frames > 1, $"the wait loop polls across frames (frames={frames})");
            InGameAssert.IsTrue(elapsed >= 2.9f, $"the wait lasts about 3 s (elapsed={elapsed:F2})");
            InGameAssert.IsTrue(stillPending, "the tree stays pending");
            InGameAssert.AreEqual(PendingTreeState.Limbo, stateAfter, "the tree stays in Limbo");
            InGameAssert.AreEqual(childId, activeIdAfter, "the active recording pointer is not flipped");
            InGameAssert.AreEqual(livePid, childPidAfter, "no pid remap happens");
            InGameAssert.IsFalse(ParsekFlight.restoringActiveTree,
                "restoringActiveTree is cleared by the finally block");
            InGameAssert.IsTrue(
                CountContaining(lines, "RestoreActiveTreeFromPending: waiting for vessel 'Restore Char Child' "
                    + "(pid=" + livePid + ") to load (activeRecId=" + childId + ")") == 1,
                "the wait line is logged once");
            InGameAssert.IsTrue(CountContaining(lines, "conclusively differs from recording '" + childId + "'") == 1,
                "the active-recording guid reject is logged once across every polled frame");
            InGameAssert.IsTrue(CountContaining(lines, "parent-walk guid gate rejected rec=") == 1,
                "the parent guid reject is logged once across every polled frame");
            InGameAssert.IsTrue(CountContaining(lines, "RestoreActiveTreeFromPending: PID match") == 0, "no pid match");
            InGameAssert.IsTrue(CountContaining(lines, "RestoreActiveTreeFromPending: fallback match") == 0, "no parent fallback match");
            InGameAssert.IsTrue(
                CountContaining(lines, "RestoreActiveTreeFromPending: vessel 'Restore Char Child' (and no EVA parent "
                    + "fallback) not active within 3s") == 1,
                "the give-up warn is logged once");
            InGameAssert.IsTrue(CountContaining(lines, "outcome=unbound-refly-provisional") == 0,
                "no Re-Fly raise without a session marker");
        }

        private static bool TrySkipUnlessQuiet(ParsekFlight flight, out string reason)
        {
            reason = null;
            if (flight == null)
                reason = "needs ParsekFlight.Instance";
            else if (PendingTreeSlot.HasPendingTree)
                reason = "a real pending tree is parked; not touching it";
            else if (ParsekFlight.restoringActiveTree)
                reason = "a restore coroutine is already running";
            else if (RewindInvokeContext.Pending)
                reason = "a rewind invocation is pending";
            else if (ParsekScenario.Instance?.ActiveReFlySessionMarker != null)
                reason = "a Re-Fly session is active";
            return reason == null;
        }

        private static IEnumerator StartRestore(ParsekFlight flight)
        {
            MethodInfo mi = typeof(ParsekFlight).GetMethod(
                "RestoreActiveTreeFromPending",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null, Type.EmptyTypes, null);
            InGameAssert.IsNotNull(mi, "ParsekFlight.RestoreActiveTreeFromPending() not found");
            return (IEnumerator)mi.Invoke(flight, null);
        }

        private static int CountContaining(List<string> lines, string text)
        {
            int n = 0;
            for (int i = 0; i < lines.Count; i++)
                if (lines[i] != null && lines[i].Contains(text))
                    n++;
            return n;
        }

        private sealed class SlotSnapshot
        {
            private RecordingTree pendingTree;
            private PendingTreeState pendingState;
            private RecordingTree savedDuringRestore;
            private bool savedSerialized;
            private bool stashedThisTransition;

            internal static SlotSnapshot Capture()
            {
                return new SlotSnapshot
                {
                    pendingTree = PendingTreeSlot.PendingTree,
                    pendingState = PendingTreeSlot.PendingTreeStateValue,
                    savedDuringRestore = PendingTreeSlot.SavedPendingTreeDuringActiveRestore,
                    savedSerialized = PendingTreeSlot.SavedPendingTreeDuringActiveRestoreSerializedForSave,
                    stashedThisTransition = PendingTreeSlot.PendingStashedThisTransition,
                };
            }

            internal void Restore()
            {
                PendingTreeSlot.RestoreFromSnapshotForTesting(
                    pendingTree, pendingState, savedDuringRestore, savedSerialized);
                PendingTreeSlot.PendingStashedThisTransition = stashedThisTransition;
            }
        }
    }
}
