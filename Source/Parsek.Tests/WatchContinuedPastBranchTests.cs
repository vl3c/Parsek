using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// GS8-WATCH-HOLD-LANDS-ON-THE-PROBE-CHILD: a focused-vessel decouple
    /// (<c>ParsekFlight.WireBreakupIntoTree</c>) stamps <c>ChildBranchPointId</c> on a
    /// recording that keeps flying to its own terminal. The end-of-flight watch search must
    /// not read that branch as "the flight ended here" and jump to the decoupled child; a
    /// recording that really ended at the split still retargets (JointBreak) or holds
    /// (Breakup) as before. Shapes mirror run 2026-09-27_2029: Kerbal X ends at 297.64,
    /// the probe split is a DECOUPLE JointBreak at 146.44 with a 0.5 s coalesce window.
    /// </summary>
    [Collection("Sequential")]
    public class WatchContinuedPastBranchTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        private const double LaunchUT = 30.24;
        private const double SplitUT = 146.44;
        private const double ParentEndUT = 297.64;
        private const double ProbeEndUT = 297.70;

        public WatchContinuedPastBranchTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.ResetRateLimitsForTesting();
            RecordingStore.ResetForTesting();
        }

        private static Recording MakeRec(string id, string name, uint pid, double startUT, double endUT,
            string childBpId = null, bool isDebris = false)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = name,
                VesselPersistentId = pid,
                TreeId = "t1",
                ChildBranchPointId = childBpId,
                IsDebris = isDebris,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = startUT },
                    new TrajectoryPoint { ut = endUT }
                }
            };
        }

        private static List<RecordingTree> MakeTree(BranchPointType type, double bpUT)
        {
            var bp = new BranchPoint
            {
                Id = "bp-probe",
                UT = bpUT,
                Type = type,
                CoalesceWindow = 0.5,
                SplitCause = type == BranchPointType.JointBreak ? "DECOUPLE" : null,
                ParentRecordingIds = new List<string> { "kerbal-x" },
                ChildRecordingIds = new List<string> { "probe" }
            };
            return new List<RecordingTree>
            {
                new RecordingTree
                {
                    Id = "t1",
                    TreeName = "Kerbal X",
                    BranchPoints = new List<BranchPoint> { bp }
                }
            };
        }

        private static List<Recording> MakeRecs(double parentEndUT, double probeStartUT)
        {
            return new List<Recording>
            {
                MakeRec("kerbal-x", "Kerbal X", 3620499050u, LaunchUT, parentEndUT, childBpId: "bp-probe"),
                MakeRec("probe", "Kerbal X Probe", 4292614596u, probeStartUT, ProbeEndUT),
            };
        }

        // --- The GS-8 shape: the parent kept flying after the decouple ---

        [Fact]
        public void FindNextWatchTarget_ParentContinuedPastDecouple_HoldsOnParent()
        {
            var recs = MakeRecs(ParentEndUT, SplitUT);
            var trees = MakeTree(BranchPointType.JointBreak, SplitUT);

            int result = GhostPlaybackLogic.FindNextWatchTarget(recs[0], recs, trees, idx => idx == 1);

            Assert.Equal(-1, result);
            Assert.Contains(logLines, l => l.Contains("[Watch]")
                && l.Contains("FindNextWatchTarget: rec 'Kerbal X'")
                && l.Contains("continued past branch bp-probe")
                && l.Contains("bpUT=146.44")
                && l.Contains("recEndUT=297.64"));
        }

        [Fact]
        public void TryGetPendingWatchActivationUT_ParentContinuedPastDecouple_NoPendingChild()
        {
            // Probe ghost not active yet but has a start UT: the old code extended the
            // hold to wait for it; the parent's own flight owns the hold instead.
            var recs = MakeRecs(ParentEndUT, SplitUT);
            var trees = MakeTree(BranchPointType.JointBreak, SplitUT);

            bool found = GhostPlaybackLogic.TryGetPendingWatchActivationUT(
                recs[0], recs, trees, idx => false, out double activationUT);

            Assert.False(found);
            Assert.True(double.IsNaN(activationUT));
            Assert.Contains(logLines, l => l.Contains("[Watch]")
                && l.Contains("TryGetPendingWatchActivationUT: rec 'Kerbal X'")
                && l.Contains("continued past branch bp-probe"));
        }

        [Fact]
        public void ResolveEffectiveWatchTargetIndex_ParentContinuedPastDecouple_DoesNotResolveToChild()
        {
            var recs = MakeRecs(ParentEndUT, SplitUT);
            var trees = MakeTree(BranchPointType.JointBreak, SplitUT);

            int result = GhostPlaybackLogic.ResolveEffectiveWatchTargetIndex(0, recs, trees, idx => idx == 1);

            Assert.Equal(-1, result);
        }

        [Fact]
        public void FindNextWatchTarget_ParentContinued_SamePidChildStillFollowed()
        {
            // The gate only narrows the different-vessel fallback: a same-PID child is a
            // real continuation and is followed whatever the parent's end UT says.
            var recs = MakeRecs(ParentEndUT, SplitUT);
            recs.Add(MakeRec("same", "Kerbal X", 3620499050u, SplitUT, 400.0));
            var trees = MakeTree(BranchPointType.JointBreak, SplitUT);
            trees[0].BranchPoints[0].ChildRecordingIds.Add("same");

            int result = GhostPlaybackLogic.FindNextWatchTarget(recs[0], recs, trees, idx => idx >= 1);

            Assert.Equal(2, result);
        }

        // --- Mirror: the flight really ended at the split ---

        [Fact]
        public void FindNextWatchTarget_ParentEndedAtDecouple_RetargetsToChild()
        {
            // Parent destroyed at the decouple: the probe is where the flight went on.
            var recs = MakeRecs(SplitUT, SplitUT);
            recs[0].TerminalStateValue = TerminalState.Destroyed;
            var trees = MakeTree(BranchPointType.JointBreak, SplitUT);

            int result = GhostPlaybackLogic.FindNextWatchTarget(recs[0], recs, trees, idx => idx == 1);

            Assert.Equal(1, result);
            Assert.DoesNotContain(logLines, l => l.Contains("continued past branch"));
        }

        [Fact]
        public void FindNextWatchTarget_ParentEndedWithinCoalesceWindowPlusSlack_RetargetsToChild()
        {
            // Explosion 1.4 s after the decouple: inside 0.5 s window + 1.0 s slack.
            var recs = MakeRecs(SplitUT + 1.4, SplitUT);
            recs[0].TerminalStateValue = TerminalState.Destroyed;
            var trees = MakeTree(BranchPointType.JointBreak, SplitUT);

            int result = GhostPlaybackLogic.FindNextWatchTarget(recs[0], recs, trees, idx => idx == 1);

            Assert.Equal(1, result);
        }

        [Fact]
        public void TryGetPendingWatchActivationUT_ParentEndedAtDecouple_WaitsForChild()
        {
            var recs = MakeRecs(SplitUT, SplitUT + 5.0);
            var trees = MakeTree(BranchPointType.JointBreak, SplitUT);

            bool found = GhostPlaybackLogic.TryGetPendingWatchActivationUT(
                recs[0], recs, trees, idx => false, out double activationUT);

            Assert.True(found);
            Assert.Equal(SplitUT + 5.0, activationUT);
        }

        [Fact]
        public void ResolveEffectiveWatchTargetIndex_ParentEndedAtDecouple_ResolvesToChild()
        {
            var recs = MakeRecs(SplitUT, SplitUT);
            var trees = MakeTree(BranchPointType.JointBreak, SplitUT);

            int result = GhostPlaybackLogic.ResolveEffectiveWatchTargetIndex(0, recs, trees, idx => idx == 1);

            Assert.Equal(1, result);
        }

        [Fact]
        public void FindNextWatchTarget_CrashBreakupAtSplit_StillHoldsOnParent()
        {
            // A crash breakup never falls back to a different vessel (#321), whether the
            // parent ended at the breakup or a moment later.
            var recs = MakeRecs(SplitUT + 0.2, SplitUT);
            recs[0].TerminalStateValue = TerminalState.Destroyed;
            var trees = MakeTree(BranchPointType.Breakup, SplitUT);

            int result = GhostPlaybackLogic.FindNextWatchTarget(recs[0], recs, trees, idx => idx == 1);

            Assert.Equal(-1, result);
        }

        // --- The predicate itself ---

        [Theory]
        [InlineData(146.44, false)]   // ended at the split
        [InlineData(147.90, false)]   // inside window + slack
        [InlineData(148.00, true)]    // just past it
        [InlineData(297.64, true)]    // GS-8
        [InlineData(100.00, false)]   // ended before the branch (switch continuation shape)
        public void RecordingContinuedPastBranchPoint_UsesCoalesceWindowPlusSlack(double endUT, bool expected)
        {
            var rec = MakeRec("kerbal-x", "Kerbal X", 1u, LaunchUT, endUT, childBpId: "bp");
            var bp = new BranchPoint { Id = "bp", UT = SplitUT, CoalesceWindow = 0.5 };

            Assert.Equal(expected, GhostPlaybackLogic.RecordingContinuedPastBranchPoint(rec, bp));
        }

        [Fact]
        public void RecordingContinuedPastBranchPoint_UnknownInputs_AnswerFalse()
        {
            var rec = MakeRec("r", "Ship", 1u, LaunchUT, ParentEndUT);

            Assert.False(GhostPlaybackLogic.RecordingContinuedPastBranchPoint(null, new BranchPoint { UT = 1.0 }));
            Assert.False(GhostPlaybackLogic.RecordingContinuedPastBranchPoint(rec, null));
            Assert.False(GhostPlaybackLogic.RecordingContinuedPastBranchPoint(rec, new BranchPoint { UT = double.NaN }));
            Assert.False(GhostPlaybackLogic.RecordingContinuedPastBranchPoint(
                new Recording { RecordingId = "empty" }, new BranchPoint { UT = SplitUT }));
        }
    }
}
