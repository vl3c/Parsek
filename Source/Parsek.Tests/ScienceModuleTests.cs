using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class ScienceModuleTests : IDisposable
    {
        private readonly ScienceModule module;
        private readonly List<string> logLines = new List<string>();

        public ScienceModuleTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            module = new ScienceModule();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Helper — build science earning action
        // ================================================================

        private static GameAction MakeEarning(double ut, string subjectId, float scienceAwarded,
            float subjectMaxValue, string recordingId = null)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ScienceEarning,
                SubjectId = subjectId,
                ScienceAwarded = scienceAwarded,
                SubjectMaxValue = subjectMaxValue,
                RecordingId = recordingId
            };
        }

        private static GameAction MakeSpending(double ut, string nodeId, float cost, int sequence = 0)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ScienceSpending,
                NodeId = nodeId,
                Cost = cost,
                Sequence = sequence
            };
        }

        // ================================================================
        // Design doc 4.7 — Verified scenarios
        // ================================================================

        // Scenario 1: Basic earning and retroactive priority
        // Subject: crewReport@MunSrfLandedMidlands, scienceCap = 15
        //
        // Step 1: T1 at UT=1000, earns 10. Effective=10. Total=10.
        // Step 2: T0 added at UT=700, earns 10.
        //   Walk: T0 at UT=700: headroom=15, effective=10, credited=10
        //         T1 at UT=1000: headroom=5, effective=5, credited=15
        //   Total = 15.

        [Fact]
        public void BasicEarning_FullCredit()
        {
            var earning = MakeEarning(1000.0, "crewReport@MunSrfLandedMidlands", 10f, 15f, "rec-T1");

            module.ProcessAction(earning);

            Assert.Equal(10f, earning.EffectiveScience);
            Assert.Equal(10.0, module.GetRunningScience());
            Assert.Equal(10.0, module.GetSubjectCredited("crewReport@MunSrfLandedMidlands"));
        }

        [Fact]
        public void RetroactivePriority_EarlierGetsFullCredit()
        {
            // Simulate recalculation walk with T0 (earlier) processed before T1 (later)
            // Both earn 10 from a subject with cap 15
            var t0 = MakeEarning(700.0, "crewReport@MunSrfLandedMidlands", 10f, 15f, "rec-T0");
            var t1 = MakeEarning(1000.0, "crewReport@MunSrfLandedMidlands", 10f, 15f, "rec-T1");

            // Walk in UT order (as RecalculationEngine would sort them)
            module.ProcessAction(t0);
            module.ProcessAction(t1);

            // T0 gets full credit (10), T1 gets remainder (5)
            Assert.Equal(10f, t0.EffectiveScience);
            Assert.Equal(5f, t1.EffectiveScience);
            Assert.Equal(15.0, module.GetRunningScience());
            Assert.Equal(15.0, module.GetSubjectCredited("crewReport@MunSrfLandedMidlands"));
        }

        // Scenario 2: Mixed transmit/recover
        // Subject scienceCap = 10
        //
        // Walk (all committed, sorted by UT):
        //   UT=200: recover, awarded 10 → effective=10, credited=10
        //   UT=500: transmit, awarded 3 → headroom=0, effective=0, credited=10
        //   UT=1000: transmit, awarded 3 → headroom=0, effective=0, credited=10. Total=10.

        [Fact]
        public void MixedTransmitRecover_CapRespected()
        {
            var recover = MakeEarning(200.0, "tempScan@MinmusSrfLanded", 10f, 10f, "rec-C");
            var transmit1 = MakeEarning(500.0, "tempScan@MinmusSrfLanded", 3f, 10f, "rec-B");
            var transmit2 = MakeEarning(1000.0, "tempScan@MinmusSrfLanded", 3f, 10f, "rec-A");

            // Walk in UT order
            module.ProcessAction(recover);
            module.ProcessAction(transmit1);
            module.ProcessAction(transmit2);

            Assert.Equal(10f, recover.EffectiveScience);
            Assert.Equal(0f, transmit1.EffectiveScience);
            Assert.Equal(0f, transmit2.EffectiveScience);
            Assert.Equal(10.0, module.GetRunningScience());
            Assert.Equal(10.0, module.GetSubjectCredited("tempScan@MinmusSrfLanded"));
        }

        // Scenario 3: Earnings with interleaved spending
        // Subject scienceCap = 10 for subjectA. Tech node costs 8.
        //
        // Walk:
        //   UT=200: recover subjectA, earn 10 → effective=10, balance=10
        //   UT=1000: recover subjectA from another recording, earn 10 → effective=0 (cap hit), balance=10
        //   UT=1500: spend 8 → affordable (10 >= 8), balance=2

        [Fact]
        public void InterleavedEarningAndSpending()
        {
            var earn1 = MakeEarning(200.0, "subjectA", 10f, 10f, "rec-new");
            var earn2 = MakeEarning(1000.0, "subjectA", 10f, 10f, "rec-old");
            var spend = MakeSpending(1500.0, "basicRocketry", 8f);

            module.ProcessAction(earn1);
            module.ProcessAction(earn2);
            module.ProcessAction(spend);

            Assert.Equal(10f, earn1.EffectiveScience);
            Assert.Equal(0f, earn2.EffectiveScience);
            Assert.True(spend.Affordable);
            Assert.Equal(2.0, module.GetRunningScience(), 10);
        }

        // ================================================================
        // Unit tests
        // ================================================================

        [Fact]
        public void ProcessAction_IgnoresNonScienceActions()
        {
            var fundsEarning = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.FundsEarning,
                FundsAwarded = 5000f
            };
            var milestone = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = "FirstLaunch"
            };
            var contractAccept = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.ContractAccept,
                ContractId = "C1"
            };

            module.ProcessAction(fundsEarning);
            module.ProcessAction(milestone);
            module.ProcessAction(contractAccept);

            Assert.Equal(0.0, module.GetRunningScience());
            Assert.Equal(0, module.SubjectCount);
        }

        [Fact]
        public void ProcessAction_IgnoresNullAction()
        {
            // Should not throw
            module.ProcessAction(null);

            Assert.Equal(0.0, module.GetRunningScience());
        }

        [Fact]
        public void Reset_ClearsState()
        {
            // Accumulate some state
            var earning = MakeEarning(1000.0, "crewReport@KerbinSrfLanded", 5f, 10f, "rec-1");
            module.ProcessAction(earning);

            Assert.Equal(5.0, module.GetRunningScience());
            Assert.Equal(1, module.SubjectCount);

            // Reset
            module.Reset();

            Assert.Equal(0.0, module.GetRunningScience());
            Assert.Equal(0, module.SubjectCount);
            Assert.Equal(0.0, module.GetSubjectCredited("crewReport@KerbinSrfLanded"));
        }

        [Fact]
        public void SubjectCap_ExactlyHit_NoOvercredit()
        {
            // Subject cap is exactly 10, earning exactly 10 — should get full credit, no overflow
            var earning = MakeEarning(1000.0, "tempScan@MunSrfLanded", 10f, 10f, "rec-1");

            module.ProcessAction(earning);

            Assert.Equal(10f, earning.EffectiveScience);
            Assert.Equal(10.0, module.GetSubjectCredited("tempScan@MunSrfLanded"));

            // Second earning on same subject — no headroom left
            var earning2 = MakeEarning(2000.0, "tempScan@MunSrfLanded", 5f, 10f, "rec-2");
            module.ProcessAction(earning2);

            Assert.Equal(0f, earning2.EffectiveScience);
            Assert.Equal(10.0, module.GetSubjectCredited("tempScan@MunSrfLanded"));
        }

        [Fact]
        public void SubjectCap_AlreadyFull_ZeroEffective()
        {
            // Fill subject completely
            var fill = MakeEarning(500.0, "mystGoo@KerbinSrfLanded", 8f, 8f, "rec-fill");
            module.ProcessAction(fill);

            Assert.Equal(8f, fill.EffectiveScience);

            // Try to earn more — should get 0
            var overflow = MakeEarning(1000.0, "mystGoo@KerbinSrfLanded", 5f, 8f, "rec-overflow");
            module.ProcessAction(overflow);

            Assert.Equal(0f, overflow.EffectiveScience);
            Assert.Equal(8.0, module.GetRunningScience());
        }

        [Fact]
        public void Spending_Affordable_DeductsBalance()
        {
            var earning = MakeEarning(1000.0, "crewReport@KerbinSrfLanded", 20f, 30f, "rec-1");
            module.ProcessAction(earning);

            var spending = MakeSpending(1500.0, "survivability", 15f);
            module.ProcessAction(spending);

            Assert.True(spending.Affordable);
            Assert.Equal(5.0, module.GetRunningScience(), 10);
        }

        [Fact]
        public void Spending_NotAffordable_FlagsFalse()
        {
            var earning = MakeEarning(1000.0, "crewReport@KerbinSrfLanded", 5f, 30f, "rec-1");
            module.ProcessAction(earning);

            var spending = MakeSpending(1500.0, "basicRocketry", 10f);
            module.ProcessAction(spending);

            Assert.False(spending.Affordable);
            // Balance should NOT be deducted when not affordable
            Assert.Equal(5.0, module.GetRunningScience());
        }

        [Fact]
        public void MultipleSubjects_IndependentCaps()
        {
            var subjectA = MakeEarning(1000.0, "crewReport@MunSrfLanded", 8f, 10f, "rec-1");
            var subjectB = MakeEarning(1000.0, "tempScan@MinmusSrfLanded", 6f, 12f, "rec-1");

            module.ProcessAction(subjectA);
            module.ProcessAction(subjectB);

            Assert.Equal(8f, subjectA.EffectiveScience);
            Assert.Equal(6f, subjectB.EffectiveScience);
            Assert.Equal(14.0, module.GetRunningScience());

            Assert.Equal(8.0, module.GetSubjectCredited("crewReport@MunSrfLanded"));
            Assert.Equal(6.0, module.GetSubjectCredited("tempScan@MinmusSrfLanded"));

            // Earn more on subjectA — partial credit (2 headroom)
            var subjectA2 = MakeEarning(2000.0, "crewReport@MunSrfLanded", 5f, 10f, "rec-2");
            module.ProcessAction(subjectA2);

            Assert.Equal(2f, subjectA2.EffectiveScience);
            Assert.Equal(16.0, module.GetRunningScience());

            // SubjectB still has 6 headroom
            var subjectB2 = MakeEarning(2000.0, "tempScan@MinmusSrfLanded", 4f, 12f, "rec-2");
            module.ProcessAction(subjectB2);

            Assert.Equal(4f, subjectB2.EffectiveScience);
            Assert.Equal(20.0, module.GetRunningScience());
        }

        [Fact]
        public void GetSubjectCredited_UnknownSubject_ReturnsZero()
        {
            Assert.Equal(0.0, module.GetSubjectCredited("nonexistent@subject"));
        }

        [Fact]
        public void GetSubjectCredited_NullSubject_ReturnsZero()
        {
            Assert.Equal(0.0, module.GetSubjectCredited(null));
        }

        [Fact]
        public void Spending_ExactBalance_IsAffordable()
        {
            var earning = MakeEarning(1000.0, "crewReport@KerbinSrfLanded", 10f, 30f, "rec-1");
            module.ProcessAction(earning);

            var spending = MakeSpending(1500.0, "survivability", 10f);
            module.ProcessAction(spending);

            Assert.True(spending.Affordable);
            Assert.Equal(0.0, module.GetRunningScience(), 10);
        }

        [Fact]
        public void MultipleSpendingsInSequence()
        {
            var earning = MakeEarning(1000.0, "crewReport@KerbinSrfLanded", 30f, 50f, "rec-1");
            module.ProcessAction(earning);

            var spend1 = MakeSpending(1500.0, "survivability", 10f, 0);
            var spend2 = MakeSpending(1500.0, "stability", 15f, 1);
            module.ProcessAction(spend1);
            module.ProcessAction(spend2);

            Assert.True(spend1.Affordable);
            Assert.True(spend2.Affordable);
            Assert.Equal(5.0, module.GetRunningScience(), 10);
        }

        // ================================================================
        // Integration with RecalculationEngine
        // ================================================================

        [Fact]
        public void RecalculationWalk_RetroactivePriority()
        {
            // Full recalculation walk: engine sorts and dispatches to module
            RecalculationEngine.ClearModules();
            RecalculationEngine.RegisterModule(module, RecalculationEngine.ModuleTier.FirstTier);

            // Two recordings same subject, added in reverse chronological order
            var actions = new List<GameAction>
            {
                MakeEarning(1000.0, "crewReport@MunSrfLandedMidlands", 10f, 15f, "rec-T1"),
                MakeEarning(700.0, "crewReport@MunSrfLandedMidlands", 10f, 15f, "rec-T0")
            };

            RecalculationEngine.Recalculate(actions);

            // After recalc: T0 (earlier) gets full credit, T1 gets remainder
            // actions[1] = T0 at UT=700, actions[0] = T1 at UT=1000
            Assert.Equal(10f, actions[1].EffectiveScience); // T0
            Assert.Equal(5f, actions[0].EffectiveScience);  // T1
            Assert.Equal(15.0, module.GetRunningScience());

            RecalculationEngine.ClearModules();
        }

        // ================================================================
        // Log assertion tests
        // ================================================================

        [Fact]
        public void Earning_LogsEffectiveScience()
        {
            var earning = MakeEarning(1000.0, "crewReport@MunSrfLanded", 10f, 15f, "rec-1");
            module.ProcessAction(earning);

            Assert.Contains(logLines, l =>
                l.Contains("[ScienceModule]") &&
                l.Contains("Earning") &&
                l.Contains("effective=") &&
                l.Contains("crewReport@MunSrfLanded"));
        }

        [Fact]
        public void CapHit_LogsReducedCredit()
        {
            // Fill the subject first
            var fill = MakeEarning(500.0, "crewReport@MunSrfLanded", 10f, 12f, "rec-fill");
            module.ProcessAction(fill);
            logLines.Clear();

            // Now earn more than headroom
            var overflow = MakeEarning(1000.0, "crewReport@MunSrfLanded", 8f, 12f, "rec-overflow");
            module.ProcessAction(overflow);

            // Should log "cap hit" when effectiveScience < scienceAwarded
            Assert.Contains(logLines, l =>
                l.Contains("[ScienceModule]") &&
                l.Contains("cap hit") &&
                l.Contains("crewReport@MunSrfLanded"));
        }

        [Fact]
        public void Spending_LogsAffordable()
        {
            var earning = MakeEarning(1000.0, "sub@kerbin", 20f, 30f, "rec-1");
            module.ProcessAction(earning);
            logLines.Clear();

            var spending = MakeSpending(1500.0, "survivability", 10f);
            module.ProcessAction(spending);

            Assert.Contains(logLines, l =>
                l.Contains("[ScienceModule]") &&
                l.Contains("Spending") &&
                l.Contains("affordable=true") &&
                l.Contains("survivability"));
        }

        [Fact]
        public void Spending_NotAffordable_LogsWarning()
        {
            var spending = MakeSpending(1500.0, "basicRocketry", 10f);
            module.ProcessAction(spending);

            Assert.Contains(logLines, l =>
                l.Contains("[ScienceModule]") &&
                l.Contains("NOT affordable") &&
                l.Contains("basicRocketry"));
        }

        [Fact]
        public void Reset_LogsSubjectCount()
        {
            var earning = MakeEarning(1000.0, "sub@kerbin", 5f, 10f, "rec-1");
            module.ProcessAction(earning);
            logLines.Clear();

            module.Reset();

            Assert.Contains(logLines, l =>
                l.Contains("[ScienceModule]") &&
                l.Contains("Reset") &&
                l.Contains("cleared 1 subjects"));
        }
    }
}
