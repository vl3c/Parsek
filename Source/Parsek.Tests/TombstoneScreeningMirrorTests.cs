using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// TOMBSTONE-GUARD-SCREENS-AN-INTERVAL-ACTION-BY-ITS-START: the two sides of the
    /// rewind seam - <see cref="RecordingTreeSplitter.ShouldRetagLedgerActionToTip"/>
    /// (step-2.9 retag) and <see cref="TombstoneAttributionHelper.IsPreRewindAttributedAction"/>
    /// (the tombstone write-set guard) - must stay exact complements for every action
    /// whose screening key is readable. One synthetic ledger walked through both
    /// predicates pins that contract, including the death-interval clause that screens
    /// a Dead KerbalAssignment by its EndUT. Pure: no store, no scenario, no logging.
    /// </summary>
    public class TombstoneScreeningMirrorTests
    {
        private const string OriginId = "rec_origin";
        private const double RewindUT = 131.54;

        private static GameAction Point(GameActionType type, double ut)
        {
            return new GameAction { Type = type, RecordingId = OriginId, UT = ut };
        }

        private static GameAction Crew(double startUT, float endUT, KerbalEndState endState)
        {
            return new GameAction
            {
                Type = GameActionType.KerbalAssignment,
                RecordingId = OriginId,
                KerbalName = "Bill Kerman",
                UT = startUT,
                StartUT = (float)startUT,
                EndUT = endUT,
                KerbalEndStateField = endState,
            };
        }

        private static List<GameAction> SyntheticLedger()
        {
            var ledger = new List<GameAction>();
            double[] uts = { 0.0, 29.94, RewindUT - 0.001, RewindUT, RewindUT + 0.001, 413.53 };
            var pointTypes = new[]
            {
                GameActionType.FundsEarning, GameActionType.ScienceEarning,
                GameActionType.ReputationPenalty, GameActionType.MilestoneAchievement,
                GameActionType.ContractComplete, GameActionType.KerbalRescue,
            };
            foreach (var t in pointTypes)
                foreach (double ut in uts)
                    ledger.Add(Point(t, ut));

            var endStates = new[]
            {
                KerbalEndState.Aboard, KerbalEndState.Dead,
                KerbalEndState.Recovered, KerbalEndState.Unknown,
            };
            float[] ends = { 100.0f, (float)RewindUT, 413.53f, float.NaN, float.PositiveInfinity };
            foreach (var es in endStates)
                foreach (double start in uts)
                    foreach (float end in ends)
                        ledger.Add(Crew(start, end, es));
            return ledger;
        }

        [Fact]
        public void BothSidesOfTheSeam_AreExactComplements_OverOneSyntheticLedger()
        {
            int walked = 0;
            int deathIntervalsMoved = 0;
            foreach (var a in SyntheticLedger())
            {
                double key = TombstoneAttributionHelper.ComputeAttributionUT(a);
                if (double.IsNaN(key)) continue;
                walked++;

                bool toTip = RecordingTreeSplitter.ShouldRetagLedgerActionToTip(a, OriginId, RewindUT);
                bool keptPreRewind = TombstoneAttributionHelper.IsPreRewindAttributedAction(a, RewindUT);
                Assert.True(toTip != keptPreRewind,
                    $"mirror broken: type={a.Type} ut={a.UT} endUT={a.EndUT} " +
                    $"endState={a.KerbalEndStateField} toTip={toTip} kept={keptPreRewind}");

                if (TombstoneAttributionHelper.IsDeathEncodingInterval(a) && a.UT < RewindUT && toTip)
                    deathIntervalsMoved++;
            }
            Assert.True(walked > 100, $"ledger too small to mean anything: {walked}");
            // The clause actually fired on this ledger (non-vacuous).
            Assert.True(deathIntervalsMoved > 0);
        }

        [Fact]
        public void DeathInterval_BoardedBeforeDiedAfter_BelongsToTip()
        {
            var a = Crew(29.94, 413.53f, KerbalEndState.Dead);
            Assert.Equal((double)413.53f, TombstoneAttributionHelper.ComputeAttributionUT(a));
            Assert.True(RecordingTreeSplitter.ShouldRetagLedgerActionToTip(a, OriginId, RewindUT));
            Assert.False(TombstoneAttributionHelper.IsPreRewindAttributedAction(a, RewindUT));
        }

        [Fact]
        public void DeathInterval_EndingAtTheCutoff_BelongsToTip_SameSenseAsUT()
        {
            // Boundary sense matches the UT rule: `>=` retags, `<` keeps.
            var a = Crew(29.94, 131.5f, KerbalEndState.Dead);
            double cutoff = (double)131.5f;
            Assert.True(RecordingTreeSplitter.ShouldRetagLedgerActionToTip(a, OriginId, cutoff));
            Assert.False(TombstoneAttributionHelper.IsPreRewindAttributedAction(a, cutoff));
        }

        [Theory]
        [InlineData(KerbalEndState.Aboard)]
        [InlineData(KerbalEndState.Recovered)]
        [InlineData(KerbalEndState.Unknown)]
        public void NonDeathInterval_StaysOnItsStartUT(KerbalEndState endState)
        {
            var a = Crew(29.94, 413.53f, endState);
            Assert.Equal(29.94, TombstoneAttributionHelper.ComputeAttributionUT(a));
            Assert.False(RecordingTreeSplitter.ShouldRetagLedgerActionToTip(a, OriginId, RewindUT));
            Assert.True(TombstoneAttributionHelper.IsPreRewindAttributedAction(a, RewindUT));
        }

        [Fact]
        public void DeathWithUnknownEnd_FallsBackToUT()
        {
            var a = Crew(29.94, float.NaN, KerbalEndState.Dead);
            Assert.False(TombstoneAttributionHelper.IsDeathEncodingInterval(a));
            Assert.Equal(29.94, TombstoneAttributionHelper.ComputeAttributionUT(a));
            Assert.True(TombstoneAttributionHelper.IsPreRewindAttributedAction(a, RewindUT));
        }

        [Fact]
        public void NonAssignmentAction_IgnoresEndStateAndEndUT()
        {
            // KerbalEndStateField / EndUT are kerbal-assignment fields; a point action
            // that happens to carry values in them is still screened by UT.
            var a = Point(GameActionType.FundsEarning, 29.94);
            a.KerbalEndStateField = KerbalEndState.Dead;
            a.EndUT = 413.53f;
            Assert.Equal(29.94, TombstoneAttributionHelper.ComputeAttributionUT(a));
            Assert.True(TombstoneAttributionHelper.IsPreRewindAttributedAction(a, RewindUT));
        }

        [Fact]
        public void SplitterPredicate_OtherRecordingOrNull_NeverRetags()
        {
            var a = Crew(29.94, 413.53f, KerbalEndState.Dead);
            a.RecordingId = "rec_other";
            Assert.False(RecordingTreeSplitter.ShouldRetagLedgerActionToTip(a, OriginId, RewindUT));
            Assert.False(RecordingTreeSplitter.ShouldRetagLedgerActionToTip(null, OriginId, RewindUT));
            Assert.True(double.IsNaN(TombstoneAttributionHelper.ComputeAttributionUT(null)));
        }
    }
}
