using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class MilestonesModuleTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly MilestonesModule module;

        public MilestonesModuleTests()
        {
            RecalculationEngine.ClearModules();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            module = new MilestonesModule();
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Helper — build a MilestoneAchievement action
        // ================================================================

        private static GameAction MakeMilestone(
            string milestoneId, double ut,
            string recordingId = null,
            float fundsAwarded = 0f,
            float repAwarded = 0f,
            float sciAwarded = 0f)
        {
            return new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = ut,
                RecordingId = recordingId,
                MilestoneId = milestoneId,
                MilestoneFundsAwarded = fundsAwarded,
                MilestoneRepAwarded = repAwarded,
                MilestoneScienceAwarded = sciAwarded
            };
        }

        // ================================================================
        // Design doc 7.4 — Basic achievement (effective=true)
        // ================================================================

        [Fact]
        public void BasicAchievement_Effective()
        {
            // Recording A at UT=1000 achieves "First Mun Landing." Commit.
            // Recalculate: effective=true. +10000 funds, +15 rep.
            var action = MakeMilestone("FirstMunLanding", 1000.0,
                recordingId: "recA", fundsAwarded: 10000f, repAwarded: 15f);

            module.ProcessAction(action);

            Assert.True(action.Effective);
            Assert.True(module.IsMilestoneCredited("FirstMunLanding"));
            Assert.Equal(1, module.GetCreditedCount());
        }

        // ================================================================
        // Design doc 7.4 — Retroactive priority shift
        // ================================================================

        [Fact]
        public void RetroactivePriorityShift()
        {
            // Step 1: Recording A at UT=1000 achieves "First Mun Landing."
            // Step 2: Rewind. Recording B at UT=700 also achieves it.
            // Recalculate (UT-sorted): B at UT=700 first, A at UT=1000 second.
            // B gets credit, A is zeroed.
            module.Reset();

            var actionB = MakeMilestone("FirstMunLanding", 700.0,
                recordingId: "recB", fundsAwarded: 10000f, repAwarded: 15f);
            var actionA = MakeMilestone("FirstMunLanding", 1000.0,
                recordingId: "recA", fundsAwarded: 10000f, repAwarded: 15f);

            // Walk in UT order (B first, A second)
            module.ProcessAction(actionB);
            module.ProcessAction(actionA);

            Assert.True(actionB.Effective);
            Assert.False(actionA.Effective);
            Assert.Equal(1, module.GetCreditedCount());
        }

        // ================================================================
        // Design doc 7.4 — Multiple milestones, independent flags
        // ================================================================

        [Fact]
        public void MultipleMilestones_IndependentFlags()
        {
            // Recording A at UT=1000: "First Mun Landing" + "First Mun EVA."
            // Rewind. Recording B at UT=700: "First Mun Landing" only.
            // Recalculate:
            //   UT=700 (B): "First Mun Landing" -> effective
            //   UT=1000 (A): "First Mun Landing" -> NOT effective, "First Mun EVA" -> effective
            module.Reset();

            var landingB = MakeMilestone("FirstMunLanding", 700.0,
                recordingId: "recB", fundsAwarded: 10000f, repAwarded: 15f);
            var landingA = MakeMilestone("FirstMunLanding", 1000.0,
                recordingId: "recA", fundsAwarded: 10000f, repAwarded: 15f);
            var evaA = MakeMilestone("FirstMunEVA", 1000.0,
                recordingId: "recA", fundsAwarded: 5000f, repAwarded: 8f);

            // Walk in UT order
            module.ProcessAction(landingB);
            module.ProcessAction(landingA);
            module.ProcessAction(evaA);

            Assert.True(landingB.Effective);
            Assert.False(landingA.Effective);
            Assert.True(evaA.Effective);
            Assert.Equal(2, module.GetCreditedCount());
            Assert.True(module.IsMilestoneCredited("FirstMunLanding"));
            Assert.True(module.IsMilestoneCredited("FirstMunEVA"));
        }

        // ================================================================
        // Unit tests — non-milestone actions ignored
        // ================================================================

        [Theory]
        [InlineData(GameActionType.ScienceEarning)]
        [InlineData(GameActionType.ScienceSpending)]
        [InlineData(GameActionType.FundsEarning)]
        [InlineData(GameActionType.FundsSpending)]
        [InlineData(GameActionType.ContractAccept)]
        [InlineData(GameActionType.ContractComplete)]
        [InlineData(GameActionType.ReputationEarning)]
        [InlineData(GameActionType.KerbalAssignment)]
        [InlineData(GameActionType.FacilityUpgrade)]
        [InlineData(GameActionType.StrategyActivate)]
        [InlineData(GameActionType.FundsInitial)]
        public void ProcessAction_IgnoresNonMilestoneActions(GameActionType type)
        {
            var action = new GameAction { Type = type, UT = 1000.0 };

            module.ProcessAction(action);

            // Non-milestone actions should not affect state
            Assert.Equal(0, module.GetCreditedCount());
            // Effective should remain at its default (true)
            Assert.True(action.Effective);
        }

        // ================================================================
        // Unit tests — Reset clears state
        // ================================================================

        [Fact]
        public void Reset_ClearsState()
        {
            var action = MakeMilestone("FirstOrbit", 500.0, recordingId: "rec1");
            module.ProcessAction(action);
            Assert.Equal(1, module.GetCreditedCount());

            module.Reset();

            Assert.Equal(0, module.GetCreditedCount());
            Assert.False(module.IsMilestoneCredited("FirstOrbit"));
        }

        // ================================================================
        // Unit tests — Duplicate milestone not effective
        // ================================================================

        [Fact]
        public void DuplicateMilestone_NotEffective()
        {
            var first = MakeMilestone("FirstOrbit", 500.0, recordingId: "rec1");
            var second = MakeMilestone("FirstOrbit", 600.0, recordingId: "rec2");

            module.ProcessAction(first);
            module.ProcessAction(second);

            Assert.True(first.Effective);
            Assert.False(second.Effective);
            Assert.Equal(1, module.GetCreditedCount());
            Assert.Equal(1, module.GetEffectiveMilestoneCount("FirstOrbit"));
        }

        [Fact]
        public void RepeatableRecordMilestone_RemainsEffectiveAcrossMultipleHits()
        {
            var first = MakeMilestone("RecordsDistance", 500.0,
                recordingId: "rec1", fundsAwarded: 4800f, repAwarded: 2f);
            var second = MakeMilestone("RecordsDistance", 900.0,
                recordingId: "rec2", fundsAwarded: 3200f, repAwarded: 1f);

            module.ProcessAction(first);
            module.ProcessAction(second);

            Assert.True(first.Effective);
            Assert.True(second.Effective);
            Assert.True(module.IsMilestoneCredited("RecordsDistance"));
            Assert.Equal(1, module.GetCreditedCount());
            Assert.Equal(2, module.GetEffectiveMilestoneCount("RecordsDistance"));
        }

        [Fact]
        public void RepeatableRecordMilestone_SameActionRecalculated_RateLimitedToOneLine()
        {
            // Bug #593 (P2 follow-up): the rate-limit key is the stable
            // GameAction.ActionId, not (milestoneId, recordingId). The
            // intended invariant is "recalculating the SAME action collapses
            // its log line"; distinct grants with the same milestoneId/
            // recordingId but different UT/reward must still log on first
            // walk because they are real, separate effective hits.

            // Seed credit for RecordsDistance with a separate recording so
            // subsequent same-recording hits go through the repeatable branch
            // (which only triggers after the milestone is already credited).
            module.ProcessAction(MakeMilestone("RecordsDistance", 0,
                recordingId: "rec-seed", fundsAwarded: 100f));
            logLines.Clear();

            // Build ONE action and re-walk it 100 times. This is the case
            // the rate-limit must collapse — a steady recalc loop replaying
            // the same committed grant.
            var action = MakeMilestone("RecordsDistance", 100.0,
                recordingId: "rec-spam", fundsAwarded: 100f);
            for (int i = 0; i < 100; i++)
            {
                module.ProcessAction(action);
                Assert.True(action.Effective);
            }

            int staysEffectiveLines = logLines.Count(l =>
                l.Contains("[Milestones]") &&
                l.Contains("Repeatable record milestone") &&
                l.Contains("RecordsDistance") &&
                l.Contains("rec-spam"));
            Assert.Equal(1, staysEffectiveLines);
        }

        [Fact]
        public void RepeatableRecordMilestone_DistinctActionsSamePair_LogSeparately()
        {
            // Bug #593 (P2 follow-up): two distinct repeatable record-
            // milestone grants for the same (milestoneId, recordingId) but
            // different UT/reward represent real separate effective hits.
            // The earlier (milestoneId, recordingId)-keyed gate collapsed
            // them into one line, silently hiding a second application.
            // The ActionId-keyed gate must let both grants log on their
            // first walk.

            // Seed credit so the next two hits take the repeatable branch.
            module.ProcessAction(MakeMilestone("RecordsDistance", 0,
                recordingId: "rec-seed", fundsAwarded: 100f));
            logLines.Clear();

            var grantA = MakeMilestone("RecordsDistance", 100.0,
                recordingId: "rec-X", fundsAwarded: 100f);
            var grantB = MakeMilestone("RecordsDistance", 200.0,
                recordingId: "rec-X", fundsAwarded: 150f);
            Assert.NotEqual(grantA.ActionId, grantB.ActionId);

            module.ProcessAction(grantA);
            module.ProcessAction(grantB);

            int staysEffectiveLines = logLines.Count(l =>
                l.Contains("[Milestones]") &&
                l.Contains("Repeatable record milestone") &&
                l.Contains("RecordsDistance") &&
                l.Contains("rec-X"));
            Assert.Equal(2, staysEffectiveLines);
        }

        [Fact]
        public void RepeatableRecordMilestone_NullRecordingId_StillKeysOnActionId()
        {
            // Standalone/KSC-path repeatable record milestones can have a
            // null RecordingId. The earlier key collapsed two distinct
            // null-recording grants into one line; the ActionId-keyed gate
            // must keep them separate.
            module.ProcessAction(MakeMilestone("RecordsSpeed", 0,
                recordingId: "rec-seed", fundsAwarded: 100f));
            logLines.Clear();

            var grant1 = MakeMilestone("RecordsSpeed", 100.0,
                recordingId: null, fundsAwarded: 100f);
            var grant2 = MakeMilestone("RecordsSpeed", 200.0,
                recordingId: null, fundsAwarded: 150f);
            Assert.NotEqual(grant1.ActionId, grant2.ActionId);

            module.ProcessAction(grant1);
            module.ProcessAction(grant2);

            int lines = logLines.Count(l =>
                l.Contains("[Milestones]") &&
                l.Contains("Repeatable record milestone") &&
                l.Contains("RecordsSpeed"));
            Assert.Equal(2, lines);
        }

        // ================================================================
        // Unit tests — IsMilestoneCredited queries
        // ================================================================

        [Fact]
        public void IsMilestoneCredited_True_AfterProcessing()
        {
            var action = MakeMilestone("FirstEVA", 800.0, recordingId: "rec1");
            module.ProcessAction(action);

            Assert.True(module.IsMilestoneCredited("FirstEVA"));
        }

        [Fact]
        public void IsMilestoneCredited_False_BeforeProcessing()
        {
            Assert.False(module.IsMilestoneCredited("FirstEVA"));
        }

        // ================================================================
        // Log assertion tests
        // ================================================================

        [Fact]
        public void Effective_LogsMilestoneId()
        {
            var action = MakeMilestone("FirstOrbitKerbin", 1200.0,
                recordingId: "rec1", fundsAwarded: 8000f, repAwarded: 12f);

            module.ProcessAction(action);

            Assert.Contains(logLines, l =>
                l.Contains("[Milestones]") &&
                l.Contains("Credited milestone") &&
                l.Contains("FirstOrbitKerbin"));
        }

        [Fact]
        public void Duplicate_LogsZeroed()
        {
            var first = MakeMilestone("FirstOrbitKerbin", 1000.0, recordingId: "rec1");
            var second = MakeMilestone("FirstOrbitKerbin", 1200.0, recordingId: "rec2");

            module.ProcessAction(first);
            module.ProcessAction(second);

            Assert.Contains(logLines, l =>
                l.Contains("[Milestones]") &&
                l.Contains("Duplicate milestone") &&
                l.Contains("FirstOrbitKerbin") &&
                l.Contains("zeroed"));
        }

        // ================================================================
        // Integration — works with RecalculationEngine
        // ================================================================

        [Fact]
        public void Integration_EngineDispatchesMilestoneToModule()
        {
            RecalculationEngine.RegisterModule(module, RecalculationEngine.ModuleTier.FirstTier);

            var actions = new List<GameAction>
            {
                MakeMilestone("FirstMunLanding", 1000.0, recordingId: "recA",
                    fundsAwarded: 10000f, repAwarded: 15f),
                MakeMilestone("FirstMunLanding", 700.0, recordingId: "recB",
                    fundsAwarded: 10000f, repAwarded: 15f)
            };

            // Engine sorts by UT, so recB (UT=700) processes before recA (UT=1000)
            RecalculationEngine.Recalculate(actions);

            // recB at UT=700 should be effective (processed first)
            Assert.True(actions[1].Effective);
            // recA at UT=1000 should be duplicate
            Assert.False(actions[0].Effective);
            Assert.Equal(1, module.GetCreditedCount());
        }
    }
}
