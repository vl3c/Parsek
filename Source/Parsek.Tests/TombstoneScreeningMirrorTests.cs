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
            var ledger = SyntheticLedger();
            foreach (var a in ledger)
            {
                double key = TombstoneAttributionHelper.ComputeAttributionUT(a, ledger);
                if (double.IsNaN(key)) continue;
                walked++;

                bool toTip = RecordingTreeSplitter.ShouldRetagLedgerActionToTip(a, OriginId, RewindUT, ledger);
                bool keptPreRewind = TombstoneAttributionHelper.IsPreRewindAttributedAction(a, RewindUT, ledger);
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
            Assert.True(RecordingTreeSplitter.ShouldRetagLedgerActionToTip(a, OriginId, RewindUT, null));
            Assert.False(TombstoneAttributionHelper.IsPreRewindAttributedAction(a, RewindUT, null));
        }

        [Fact]
        public void DeathInterval_EndingAtTheCutoff_BelongsToTip_SameSenseAsUT()
        {
            // Boundary sense matches the UT rule: `>=` retags, `<` keeps.
            var a = Crew(29.94, 131.5f, KerbalEndState.Dead);
            double cutoff = (double)131.5f;
            Assert.True(RecordingTreeSplitter.ShouldRetagLedgerActionToTip(a, OriginId, cutoff, null));
            Assert.False(TombstoneAttributionHelper.IsPreRewindAttributedAction(a, cutoff, null));
        }

        [Theory]
        [InlineData(KerbalEndState.Aboard)]
        [InlineData(KerbalEndState.Recovered)]
        [InlineData(KerbalEndState.Unknown)]
        public void NonDeathInterval_StaysOnItsStartUT(KerbalEndState endState)
        {
            var a = Crew(29.94, 413.53f, endState);
            Assert.Equal(29.94, TombstoneAttributionHelper.ComputeAttributionUT(a));
            Assert.False(RecordingTreeSplitter.ShouldRetagLedgerActionToTip(a, OriginId, RewindUT, null));
            Assert.True(TombstoneAttributionHelper.IsPreRewindAttributedAction(a, RewindUT, null));
        }

        [Fact]
        public void DeathWithUnknownEnd_FallsBackToUT()
        {
            var a = Crew(29.94, float.NaN, KerbalEndState.Dead);
            Assert.False(TombstoneAttributionHelper.IsDeathEncodingInterval(a));
            Assert.Equal(29.94, TombstoneAttributionHelper.ComputeAttributionUT(a));
            Assert.True(TombstoneAttributionHelper.IsPreRewindAttributedAction(a, RewindUT, null));
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
            Assert.True(TombstoneAttributionHelper.IsPreRewindAttributedAction(a, RewindUT, null));
        }

        [Fact]
        public void SplitterPredicate_OtherRecordingOrNull_NeverRetags()
        {
            var a = Crew(29.94, 413.53f, KerbalEndState.Dead);
            a.RecordingId = "rec_other";
            Assert.False(RecordingTreeSplitter.ShouldRetagLedgerActionToTip(a, OriginId, RewindUT, null));
            Assert.False(RecordingTreeSplitter.ShouldRetagLedgerActionToTip(null, OriginId, RewindUT, null));
            Assert.True(double.IsNaN(TombstoneAttributionHelper.ComputeAttributionUT(null)));
        }

        // -----------------------------------------------------------------------
        // DEATH-REP-PENALTY-CAN-LAND-ACROSS-A-SPLIT-CUT (operator ruling 2026-09-26:
        // move them together). A KerbalDeath reputation penalty is keyed by its
        // paired death on the same recording, on both sides of the seam.
        // -----------------------------------------------------------------------

        private static GameAction Penalty(double ut, ReputationPenaltySource source)
        {
            return new GameAction
            {
                Type = GameActionType.ReputationPenalty,
                RecordingId = OriginId,
                UT = ut,
                NominalPenalty = 10f,
                RepPenaltySource = source,
            };
        }

        private static void AssertSide(GameAction a, List<GameAction> ledger, bool expectTip)
        {
            Assert.Equal(expectTip,
                RecordingTreeSplitter.ShouldRetagLedgerActionToTip(a, OriginId, RewindUT, ledger));
            Assert.Equal(!expectTip,
                TombstoneAttributionHelper.IsPreRewindAttributedAction(a, RewindUT, ledger));
        }

        [Fact]
        public void DeathPenalty_BeforeTheCut_FollowsADeathAfterIt()
        {
            var death = Crew(29.94, 413.53f, KerbalEndState.Dead);
            var penalty = Penalty(RewindUT - 0.001, ReputationPenaltySource.KerbalDeath);
            var ledger = new List<GameAction> { death, penalty };
            Assert.Equal((double)413.53f, TombstoneAttributionHelper.ComputeAttributionUT(penalty, ledger));
            AssertSide(penalty, ledger, expectTip: true);
            AssertSide(death, ledger, expectTip: true);
        }

        [Fact]
        public void DeathPenalty_AfterTheCut_FollowsADeathBeforeIt()
        {
            var death = Crew(29.94, 100.0f, KerbalEndState.Dead);
            var penalty = Penalty(RewindUT + 0.001, ReputationPenaltySource.KerbalDeath);
            var ledger = new List<GameAction> { penalty, death };
            AssertSide(penalty, ledger, expectTip: false);
            AssertSide(death, ledger, expectTip: false);
        }

        [Fact]
        public void DeathPenalty_NoContext_KeepsItsOwnUT()
        {
            var penalty = Penalty(RewindUT - 0.001, ReputationPenaltySource.KerbalDeath);
            Assert.Equal(RewindUT - 0.001, TombstoneAttributionHelper.ComputeAttributionUT(penalty, null));
            AssertSide(penalty, null, expectTip: false);
        }

        [Theory]
        [InlineData(ReputationPenaltySource.ContractFail)]
        [InlineData(ReputationPenaltySource.ContractDecline)]
        [InlineData(ReputationPenaltySource.Strategy)]
        [InlineData(ReputationPenaltySource.Other)]
        public void OtherSourcePenalty_KeepsItsOwnUT_EvenBesideADeath(ReputationPenaltySource source)
        {
            var death = Crew(29.94, 413.53f, KerbalEndState.Dead);
            var penalty = Penalty(RewindUT - 0.001, source);
            var ledger = new List<GameAction> { death, penalty };
            Assert.Equal(RewindUT - 0.001, TombstoneAttributionHelper.ComputeAttributionUT(penalty, ledger));
            AssertSide(penalty, ledger, expectTip: false);
        }

        [Fact]
        public void DeathPenalty_DeathOnAnotherRecording_KeepsItsOwnUT()
        {
            var death = Crew(29.94, 413.53f, KerbalEndState.Dead);
            death.RecordingId = "rec_other";
            var penalty = Penalty(RewindUT - 0.001, ReputationPenaltySource.KerbalDeath);
            var ledger = new List<GameAction> { death, penalty };
            AssertSide(penalty, ledger, expectTip: false);
        }

        [Theory]
        [InlineData(KerbalEndState.Aboard)]
        [InlineData(KerbalEndState.Recovered)]
        [InlineData(KerbalEndState.Unknown)]
        public void DeathPenalty_OnlyNonDeathCrewRows_KeepsItsOwnUT(KerbalEndState endState)
        {
            var crew = Crew(29.94, 413.53f, endState);
            var penalty = Penalty(RewindUT - 0.001, ReputationPenaltySource.KerbalDeath);
            var ledger = new List<GameAction> { crew, penalty };
            AssertSide(penalty, ledger, expectTip: false);
        }

        [Fact]
        public void DeathPenalty_SeveralDeaths_FollowsTheLatest()
        {
            var early = Crew(29.94, 100.0f, KerbalEndState.Dead);
            var late = Crew(29.94, 413.53f, KerbalEndState.Dead);
            late.KerbalName = "Bob Kerman";
            var penalty = Penalty(RewindUT - 0.001, ReputationPenaltySource.KerbalDeath);
            var ledger = new List<GameAction> { early, penalty, late };
            Assert.Equal((double)413.53f, TombstoneAttributionHelper.ComputeAttributionUT(penalty, ledger));
            AssertSide(penalty, ledger, expectTip: true);
            AssertSide(early, ledger, expectTip: false);
            AssertSide(late, ledger, expectTip: true);
        }

        [Fact]
        public void DeathPenalty_DeathWithUnknownEnd_FollowsTheDeathStartUT()
        {
            var death = Crew(RewindUT + 1.0, float.NaN, KerbalEndState.Dead);
            var penalty = Penalty(RewindUT - 0.001, ReputationPenaltySource.KerbalDeath);
            var ledger = new List<GameAction> { death, penalty };
            Assert.Equal(RewindUT + 1.0, TombstoneAttributionHelper.ComputeAttributionUT(penalty, ledger));
            AssertSide(penalty, ledger, expectTip: true);
        }

        [Fact]
        public void DeathRow_IgnoresContext_DeathWithoutPenaltyUnchanged()
        {
            var death = Crew(29.94, 413.53f, KerbalEndState.Dead);
            var ledger = new List<GameAction> { death };
            Assert.Equal(TombstoneAttributionHelper.ComputeAttributionUT(death),
                TombstoneAttributionHelper.ComputeAttributionUT(death, ledger));
            AssertSide(death, ledger, expectTip: true);
        }

        // Two-phase selection: the death precedes its penalty in the ledger, so a
        // single-pass retag would move the death first and the penalty, reading its
        // pair on the other id, would stay behind.
        [Fact]
        public void SelectForSecondHalf_DecidesAgainstTheUnmutatedLedger()
        {
            var death = Crew(29.94, 413.53f, KerbalEndState.Dead);
            var penalty = Penalty(RewindUT - 0.001, ReputationPenaltySource.KerbalDeath);
            var funds = Point(GameActionType.FundsEarning, RewindUT - 0.001);
            var ledger = new List<GameAction> { death, penalty, funds };
            int deathIntervals, penaltiesByDeath;
            var selected = RecordingTreeSplitter.SelectLedgerActionsForSecondHalf(
                ledger, OriginId, RewindUT, out deathIntervals, out penaltiesByDeath);
            Assert.Equal(new[] { death, penalty }, selected);
            Assert.Equal(1, deathIntervals);
            Assert.Equal(1, penaltiesByDeath);
        }
    }
}
