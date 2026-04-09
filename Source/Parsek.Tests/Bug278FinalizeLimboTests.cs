using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for bug #278 — `ParsekScenario.FinalizePendingLimboTreeForRevert` no
    /// longer blanket-marks every leaf without TerminalStateValue as Destroyed.
    /// Instead it routes through `ParsekFlight.FinalizeIndividualRecording`, which
    /// preserves existing terminal states, looks up live vessels via
    /// FlightRecorder.FindVesselByPid, and only falls back to Destroyed when the
    /// vessel is gone. The "vessel found → real situation" branch needs a live
    /// KSP runtime and is covered by an in-game test; these unit tests cover the
    /// branches that don't.
    /// </summary>
    [Collection("Sequential")]
    public class Bug278FinalizeLimboTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug278FinalizeLimboTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void FinalizeIndividualRecording_LeafWithoutVessel_FallsBackToDestroyed()
        {
            // When pid == 0 (or the vessel is not in FlightGlobals), the leaf
            // falls back to Destroyed + PopulateTerminalOrbitFromLastSegment.
            // This is the safety-net branch the limbo finalize relies on for
            // vessels that were genuinely destroyed before the finalize ran;
            // any leaf with a live vessel takes the situation-aware branch
            // (covered by the FinalizeIndividualRecording_ActiveVessel_*
            // in-game test in RuntimeTests.cs).
            var rec = new Recording
            {
                RecordingId = "leaf-no-vessel",
                VesselName = "Gone",
                VesselPersistentId = 0,
                ChildBranchPointId = null, // leaf
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0 });

            ParsekFlight.FinalizeIndividualRecording(rec, commitUT: 200.0, isSceneExit: true);

            Assert.True(rec.TerminalStateValue.HasValue);
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue.Value);
            Assert.Null(rec.VesselSnapshot);
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][WARN][Flight]") &&
                l.Contains("vessel pid=0 not found") &&
                l.Contains("leaf-no-vessel"));
        }

        [Fact]
        public void FinalizeIndividualRecording_LeafWithExistingTerminalState_PreservesIt()
        {
            // Bug #278 regression guard: a leaf that already carries a real
            // terminal state (e.g. Landed, set elsewhere by EndDebrisRecording or
            // by a prior finalize pass) must NOT be overwritten when the limbo
            // finalize runs. The previous code's `HasValue continue` short-circuit
            // protected this; the new code in FinalizeIndividualRecording uses the
            // same predicate (`if (isLeaf && !rec.TerminalStateValue.HasValue)`).
            var rec = new Recording
            {
                RecordingId = "leaf-already-landed",
                VesselName = "Bob",
                VesselPersistentId = 0,
                ChildBranchPointId = null,
                TerminalStateValue = TerminalState.Landed,
            };

            ParsekFlight.FinalizeIndividualRecording(rec, commitUT: 200.0, isSceneExit: true);

            Assert.Equal(TerminalState.Landed, rec.TerminalStateValue.Value);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Parsek][WARN][Flight]") &&
                l.Contains("vessel pid=") &&
                l.Contains("leaf-already-landed"));
        }

        [Fact]
        public void FinalizeIndividualRecording_NonLeaf_SkipsTerminalStateAssignment()
        {
            // Non-leaf recordings (those with a ChildBranchPointId — interior nodes
            // of a recording tree) must NOT get terminal state set by the
            // per-recording finalize. The active-recording-non-leaf case is
            // handled separately by EnsureActiveRecordingTerminalState.
            var rec = new Recording
            {
                RecordingId = "non-leaf",
                VesselName = "Branch",
                VesselPersistentId = 0,
                ChildBranchPointId = "branch-1", // makes it non-leaf
            };

            ParsekFlight.FinalizeIndividualRecording(rec, commitUT: 200.0, isSceneExit: true);

            Assert.False(rec.TerminalStateValue.HasValue);
        }

        [Fact]
        public void FinalizeIndividualRecording_SetsExplicitStartAndEndUT_FromPoints()
        {
            // Sanity check that the finalize call still applies the
            // ExplicitStartUT/EndUT bookkeeping FinalizeTreeRecordings was already
            // doing — this guards against a regression where moving the call to
            // the limbo finalize path drops these field updates.
            var rec = new Recording
            {
                RecordingId = "leaf-no-explicit-times",
                VesselPersistentId = 0,
                ChildBranchPointId = null,
                ExplicitStartUT = double.NaN,
                ExplicitEndUT = double.NaN,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 50.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 75.0 });

            ParsekFlight.FinalizeIndividualRecording(rec, commitUT: 200.0, isSceneExit: true);

            Assert.Equal(50.0, rec.ExplicitStartUT);
            Assert.Equal(75.0, rec.ExplicitEndUT);
        }

        [Fact]
        public void FinalizeIndividualRecording_NoPoints_FallsBackEndUTToCommitUT()
        {
            // Leaf with zero trajectory points (e.g. an empty parent-continuation
            // placeholder from the deferred-split-check path tracked by bug #285)
            // gets ExplicitEndUT = commitUT.
            var rec = new Recording
            {
                RecordingId = "leaf-empty",
                VesselPersistentId = 0,
                ChildBranchPointId = null,
                ExplicitStartUT = double.NaN,
                ExplicitEndUT = double.NaN,
            };

            ParsekFlight.FinalizeIndividualRecording(rec, commitUT: 250.0, isSceneExit: true);

            Assert.Equal(250.0, rec.ExplicitEndUT);
        }

        [Fact]
        public void EnsureActiveRecordingTerminalState_AlreadyTerminal_SkipsAndLogs()
        {
            // The active recording can be a non-leaf (it has child branches);
            // EnsureActiveRecordingTerminalState handles it specially. If it
            // already has a terminal state, log Verbose and bail.
            var tree = new RecordingTree { TreeName = "Test" };
            var active = new Recording
            {
                RecordingId = "active-non-leaf",
                VesselPersistentId = 0,
                ChildBranchPointId = "branch-1",
                TerminalStateValue = TerminalState.SubOrbital,
            };
            tree.Recordings[active.RecordingId] = active;
            tree.ActiveRecordingId = active.RecordingId;

            ParsekFlight.EnsureActiveRecordingTerminalState(tree);

            // Already-terminal state preserved
            Assert.Equal(TerminalState.SubOrbital, active.TerminalStateValue.Value);
            Assert.Contains(logLines, l =>
                l.Contains("active recording") &&
                l.Contains("active-non-leaf") &&
                l.Contains("already has terminalState=SubOrbital"));
        }

        [Fact]
        public void EnsureActiveRecordingTerminalState_NoActiveId_NoOp()
        {
            // tree.ActiveRecordingId == null → early return, no state changes,
            // no log lines.
            var tree = new RecordingTree { TreeName = "Test" };
            tree.ActiveRecordingId = null;

            ParsekFlight.EnsureActiveRecordingTerminalState(tree);

            Assert.DoesNotContain(logLines, l => l.Contains("EnsureActiveRecordingTerminalState"));
        }

        [Fact]
        public void PruneZeroPointLeaves_RemovesEmptyDestroyedLeaves()
        {
            // The limbo finalize now also runs PruneZeroPointLeaves, so any
            // empty parent-continuation placeholders (bug #285's symptom for the
            // gen-0 deferred-split case) get removed alongside the terminal-state
            // pass. Verify the helper still works as advertised post-promotion
            // from private static → internal static.
            var tree = new RecordingTree { TreeName = "Test" };
            var keep = new Recording
            {
                RecordingId = "keep",
                VesselPersistentId = 0,
                ChildBranchPointId = null,
                ExplicitStartUT = 50.0,
                ExplicitEndUT = 75.0,
            };
            keep.Points.Add(new TrajectoryPoint { ut = 50.0 });
            tree.Recordings[keep.RecordingId] = keep;

            var empty = new Recording
            {
                RecordingId = "empty-placeholder",
                VesselPersistentId = 0,
                ChildBranchPointId = null,
                ExplicitStartUT = 50.0,
                ExplicitEndUT = 50.0,
                // No Points, no OrbitSegments, no SurfacePos
            };
            tree.Recordings[empty.RecordingId] = empty;

            ParsekFlight.PruneZeroPointLeaves(tree);

            Assert.True(tree.Recordings.ContainsKey("keep"));
            Assert.False(tree.Recordings.ContainsKey("empty-placeholder"));
            Assert.Contains(logLines, l =>
                l.Contains("PruneZeroPointLeaves: removed 1 zero-point"));
        }
    }
}
