using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the effective-leaf detection in
    /// <see cref="ParsekFlight.FinalizeIndividualRecording"/>.
    ///
    /// Reproduces the LU-stage-separation symptom where both halves of a
    /// multi-stage vessel get destroyed, but the original parent recording
    /// (which kept its PID after the side-off split) silently failed to receive
    /// a TerminalState and disappeared from the Unfinished Flights list.
    ///
    /// The original predicate `isLeaf = rec.ChildBranchPointId == null` was
    /// too strict: a recording that points at a branch point whose only child
    /// has a DIFFERENT PID is still the effective continuation of its own PID
    /// and must be finalized like any leaf. Mirrors the pattern already used by
    /// <see cref="GhostPlaybackLogic.IsEffectiveLeafForVessel"/> (#224).
    /// </summary>
    [Collection("Sequential")]
    public class EffectiveLeafFinalizationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public EffectiveLeafFinalizationTests()
        {
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        /// <summary>
        /// LU-stage repro: parent recording (pid 2708531065) carries
        /// ChildBranchPointId; the BP's only child recording is the new L probe
        /// with a DIFFERENT pid (334653631). The parent IS the effective leaf for
        /// its own PID (the U continuation) and must get its terminal state set
        /// during finalization.
        /// </summary>
        [Fact]
        public void FinalizeIndividualRecording_DifferentPidChild_TreatsParentAsEffectiveLeaf()
        {
            var tree = new RecordingTree { Id = "tree-1", TreeName = "Kerbal X" };

            var parent = new Recording
            {
                RecordingId = "34757abf-parent",
                TreeId = tree.Id,
                VesselName = "Kerbal X",
                VesselPersistentId = 2708531065,
                ChildBranchPointId = "bc780859-bp",
                // No live vessel + non-scene-exit path → falls through to
                // "vessel genuinely missing → mark as Destroyed". Mirrors the
                // user's playtest where both halves crashed before finalize.
            };
            parent.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 200.0 });
            parent.Points.Add(new TrajectoryPoint { ut = 138.0, altitude = 5.0 });

            var sideOff = new Recording
            {
                RecordingId = "b4b0470e-sideoff",
                TreeId = tree.Id,
                VesselName = "Kerbal X Probe",
                VesselPersistentId = 334653631, // DIFFERENT PID — side-off split
                ParentBranchPointId = "bc780859-bp",
            };

            tree.Recordings[parent.RecordingId] = parent;
            tree.Recordings[sideOff.RecordingId] = sideOff;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bc780859-bp",
                Type = BranchPointType.JointBreak,
                UT = 130.0,
                ParentRecordingIds = new List<string> { parent.RecordingId },
                ChildRecordingIds = new List<string> { sideOff.RecordingId },
            });

            ParsekFlight.FinalizeIndividualRecording(
                parent,
                commitUT: 200.0,
                isSceneExit: false,
                finalizationCache: null,
                treeContext: tree);

            // Smoking-gun assertion: the parent should now have a terminal state
            // (was null before the fix because isLeaf was false).
            Assert.True(parent.TerminalStateValue.HasValue,
                "parent recording must receive a terminal state when its only " +
                "BP child has a different PID");
            Assert.Equal(TerminalState.Destroyed, parent.TerminalStateValue.Value);

            // The summary log line at the bottom of FinalizeIndividualRecording
            // exposes the leaf= flag and the resolved terminal — this is the
            // exact line that the playtest log showed as `terminal=none leaf=False`
            // before the fix.
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][VERBOSE][Flight]") &&
                l.Contains("FinalizeTreeRecordings:") &&
                l.Contains("34757abf-parent") &&
                l.Contains("terminal=Destroyed") &&
                l.Contains("leaf=True"));

            // The IsEffectiveLeafForVessel diagnostic should also have fired,
            // confirming the new code path is what flipped the decision.
            Assert.Contains(logLines, l =>
                l.Contains("IsEffectiveLeafForVessel:") &&
                l.Contains("34757abf-parent") &&
                l.Contains("breakup-continuous, no same-PID continuation child"));
        }

        /// <summary>
        /// Regression guard: when the BP DOES have a same-PID child, the parent
        /// is a true non-leaf (an interior continuation node) and must NOT get
        /// its terminal state set by the per-recording finalize. The active
        /// recording's non-leaf path is handled separately by
        /// EnsureActiveRecordingTerminalState — pinning that this fix did not
        /// blanket-flip the behaviour for ordinary continuations.
        /// </summary>
        [Fact]
        public void FinalizeIndividualRecording_SamePidContinuation_StillSkipsTerminalAssignment()
        {
            var tree = new RecordingTree { Id = "tree-2", TreeName = "Continuation" };

            var parent = new Recording
            {
                RecordingId = "parent-with-continuation",
                TreeId = tree.Id,
                VesselName = "Kerbal Y",
                VesselPersistentId = 4242,
                ChildBranchPointId = "bp-cont",
            };
            parent.Points.Add(new TrajectoryPoint { ut = 50.0 });

            var continuation = new Recording
            {
                RecordingId = "same-pid-continuation",
                TreeId = tree.Id,
                VesselName = "Kerbal Y",
                VesselPersistentId = 4242, // SAME pid — true non-leaf parent
                ParentBranchPointId = "bp-cont",
            };

            tree.Recordings[parent.RecordingId] = parent;
            tree.Recordings[continuation.RecordingId] = continuation;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-cont",
                Type = BranchPointType.Undock,
                UT = 60.0,
                ParentRecordingIds = new List<string> { parent.RecordingId },
                ChildRecordingIds = new List<string> { continuation.RecordingId },
            });

            ParsekFlight.FinalizeIndividualRecording(
                parent,
                commitUT: 200.0,
                isSceneExit: false,
                finalizationCache: null,
                treeContext: tree);

            Assert.False(parent.TerminalStateValue.HasValue,
                "true non-leaf (same-PID continuation child) must not get " +
                "terminal state assigned by the per-recording finalize");
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][VERBOSE][Flight]") &&
                l.Contains("FinalizeTreeRecordings:") &&
                l.Contains("parent-with-continuation") &&
                l.Contains("terminal=none") &&
                l.Contains("leaf=False"));
        }

        /// <summary>
        /// Three-child variant: BP has multiple children, none sharing the
        /// parent's PID. Parent is still the effective leaf for its own PID.
        /// Pins that the same-PID check considers EVERY child, not just the
        /// first.
        /// </summary>
        [Fact]
        public void FinalizeIndividualRecording_MultipleDifferentPidChildren_TreatsParentAsEffectiveLeaf()
        {
            var tree = new RecordingTree { Id = "tree-3", TreeName = "Multi-debris" };

            var parent = new Recording
            {
                RecordingId = "multi-debris-parent",
                TreeId = tree.Id,
                VesselName = "Kerbal Z",
                VesselPersistentId = 1000,
                ChildBranchPointId = "bp-multi",
            };
            parent.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 8.0 });

            var debrisA = new Recording
            {
                RecordingId = "debris-a",
                TreeId = tree.Id,
                VesselPersistentId = 2000,
                ParentBranchPointId = "bp-multi",
                IsDebris = true,
            };
            var debrisB = new Recording
            {
                RecordingId = "debris-b",
                TreeId = tree.Id,
                VesselPersistentId = 3000,
                ParentBranchPointId = "bp-multi",
                IsDebris = true,
            };

            tree.Recordings[parent.RecordingId] = parent;
            tree.Recordings[debrisA.RecordingId] = debrisA;
            tree.Recordings[debrisB.RecordingId] = debrisB;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-multi",
                Type = BranchPointType.Breakup,
                UT = 120.0,
                ParentRecordingIds = new List<string> { parent.RecordingId },
                ChildRecordingIds = new List<string> { debrisA.RecordingId, debrisB.RecordingId },
            });

            ParsekFlight.FinalizeIndividualRecording(
                parent,
                commitUT: 200.0,
                isSceneExit: true,
                finalizationCache: null,
                treeContext: tree);

            Assert.True(parent.TerminalStateValue.HasValue);
            // Scene-exit path with no live vessel + low-altitude last point →
            // InferTerminalStateFromTrajectory returns Landed.
            Assert.Equal(TerminalState.Landed, parent.TerminalStateValue.Value);
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][VERBOSE][Flight]") &&
                l.Contains("FinalizeTreeRecordings:") &&
                l.Contains("multi-debris-parent") &&
                l.Contains("terminal=Landed") &&
                l.Contains("leaf=True"));
        }
    }
}
