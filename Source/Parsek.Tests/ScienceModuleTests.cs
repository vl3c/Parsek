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

        // ================================================================
        // Reservation system tests (design doc 4.5)
        // ================================================================

        // Design doc 4.7, "Reservation blocks new spending on rewind":
        //   Recording A earns 30 at UT=500. Recording B earns 20 at UT=1000.
        //   Tech node at UT=1500 costs 45. Balance after walk: 5.
        //
        //   Player rewinds, fast-forwards to UT=600.
        //     Effective earnings up to UT=600: 30 (recording A only).
        //     ALL committed spendings: 45 (tech node).
        //     Available: 30 - 45 = -15 → 0.
        //     Player cannot unlock a new tech node. Correct.

        [Fact]
        public void Reservation_BlocksNewSpending()
        {
            // Set up the full action list for the pre-pass
            var actions = new List<GameAction>
            {
                MakeEarning(500.0, "subA", 30f, 100f, "rec-A"),
                MakeEarning(1000.0, "subB", 20f, 100f, "rec-B"),
                MakeSpending(1500.0, "advRocketry", 45f)
            };

            // Pre-pass: compute total committed spendings (45)
            module.ComputeTotalSpendings(actions);

            // Walk only up to UT=600: only recording A's earning is processed
            module.ProcessAction(actions[0]); // earn 30 at UT=500

            // At UT=600: effective earnings = 30, all spendings = 45
            // Available = 30 - 45 = -15 → clamped to 0
            Assert.Equal(0.0, module.GetAvailableScience());
            Assert.Equal(30.0, module.GetTotalEffectiveEarnings());
            Assert.Equal(45.0, module.GetTotalCommittedSpendings());
        }

        // Design doc 4.7, "Reservation blocks new spending on rewind" (continued):
        //   Player fast-forwards to UT=1100.
        //     Effective earnings up to UT=1100: 50 (both recordings).
        //     Available: 50 - 45 = 5.
        //     Player can unlock a node costing up to 5. Correct.

        [Fact]
        public void Reservation_AllowsAfterMoreEarnings()
        {
            var actions = new List<GameAction>
            {
                MakeEarning(500.0, "subA", 30f, 100f, "rec-A"),
                MakeEarning(1000.0, "subB", 20f, 100f, "rec-B"),
                MakeSpending(1500.0, "advRocketry", 45f)
            };

            // Pre-pass
            module.ComputeTotalSpendings(actions);

            // Walk up to UT=1100: both earnings processed
            module.ProcessAction(actions[0]); // earn 30 at UT=500
            module.ProcessAction(actions[1]); // earn 20 at UT=1000

            // At UT=1100: effective earnings = 50, all spendings = 45
            // Available = 50 - 45 = 5
            Assert.Equal(5.0, module.GetAvailableScience());
            Assert.Equal(50.0, module.GetTotalEffectiveEarnings());
        }

        [Fact]
        public void GetAvailableScience_NoSpendings_EqualsRunningScience()
        {
            var actions = new List<GameAction>
            {
                MakeEarning(500.0, "subA", 30f, 100f, "rec-A"),
                MakeEarning(1000.0, "subB", 20f, 100f, "rec-B")
            };

            // Pre-pass: no spendings → totalCommittedSpendings = 0
            module.ComputeTotalSpendings(actions);

            // Walk all earnings
            module.ProcessAction(actions[0]);
            module.ProcessAction(actions[1]);

            // Available = 50 - 0 = 50 = runningScience (since no spendings deducted)
            Assert.Equal(50.0, module.GetAvailableScience());
            Assert.Equal(50.0, module.GetRunningScience());
            Assert.Equal(0.0, module.GetTotalCommittedSpendings());
        }

        [Fact]
        public void GetAvailableScience_WithSpendings_ReflectsReservation()
        {
            var actions = new List<GameAction>
            {
                MakeEarning(500.0, "subA", 30f, 100f, "rec-A"),
                MakeEarning(1000.0, "subB", 20f, 100f, "rec-B"),
                MakeSpending(800.0, "node1", 10f, 0),
                MakeSpending(1500.0, "node2", 15f, 0)
            };

            // Pre-pass: total spendings = 10 + 15 = 25
            module.ComputeTotalSpendings(actions);
            Assert.Equal(25.0, module.GetTotalCommittedSpendings());

            // Full walk (sorted by UT, earnings before spendings):
            //   UT=500: earn 30
            //   UT=800: spend 10
            //   UT=1000: earn 20
            //   UT=1500: spend 15
            module.ProcessAction(actions[0]); // earn 30
            module.ProcessAction(actions[2]); // spend 10
            module.ProcessAction(actions[1]); // earn 20
            module.ProcessAction(actions[3]); // spend 15

            // runningScience = 30 - 10 + 20 - 15 = 25
            Assert.Equal(25.0, module.GetRunningScience(), 10);

            // totalEffectiveEarnings = 30 + 20 = 50
            Assert.Equal(50.0, module.GetTotalEffectiveEarnings());

            // availableScience = totalEffectiveEarnings - totalCommittedSpendings = 50 - 25 = 25
            Assert.Equal(25.0, module.GetAvailableScience());
        }

        [Fact]
        public void Reservation_AfterFullWalk_MatchesBalance()
        {
            // After a full walk (all actions processed), available should match runningScience
            // because all spendings have been deducted from both.
            var actions = new List<GameAction>
            {
                MakeEarning(500.0, "subA", 30f, 100f, "rec-A"),
                MakeSpending(1000.0, "node1", 10f)
            };

            module.ComputeTotalSpendings(actions);

            // Walk all actions
            module.ProcessAction(actions[0]); // earn 30
            module.ProcessAction(actions[1]); // spend 10

            // runningScience = 30 - 10 = 20
            // totalEffectiveEarnings = 30
            // available = 30 - 10 = 20 = runningScience (all spendings consumed)
            Assert.Equal(20.0, module.GetRunningScience(), 10);
            Assert.Equal(20.0, module.GetAvailableScience());
        }

        [Fact]
        public void Reservation_MidWalk_LowerThanRunningScience()
        {
            // Mid-walk (not all spendings processed yet), available should be lower than runningScience
            // because future spendings are reserved but not yet deducted from runningScience.
            var actions = new List<GameAction>
            {
                MakeEarning(500.0, "subA", 30f, 100f, "rec-A"),
                MakeSpending(1500.0, "node1", 10f)
            };

            module.ComputeTotalSpendings(actions);

            // Walk only the earning
            module.ProcessAction(actions[0]); // earn 30

            // runningScience = 30 (no spending deducted yet)
            // available = 30 - 10 = 20 (future spending reserved)
            Assert.Equal(30.0, module.GetRunningScience());
            Assert.Equal(20.0, module.GetAvailableScience());
        }

        [Fact]
        public void Reservation_WithSubjectCap_UsesEffectiveNotAwarded()
        {
            // The reservation uses effective science (post-cap), not raw scienceAwarded.
            // Two recordings earn 20 each from a subject with cap 25. Total effective = 25.
            var actions = new List<GameAction>
            {
                MakeEarning(500.0, "subA", 20f, 25f, "rec-A"),
                MakeEarning(1000.0, "subA", 20f, 25f, "rec-B"),
                MakeSpending(1500.0, "node1", 20f)
            };

            module.ComputeTotalSpendings(actions);

            // Walk all earnings
            module.ProcessAction(actions[0]); // effective = 20 (headroom 25)
            module.ProcessAction(actions[1]); // effective = 5 (headroom 5, cap hit)

            // totalEffectiveEarnings = 25, totalCommittedSpendings = 20
            // available = 25 - 20 = 5
            Assert.Equal(5.0, module.GetAvailableScience());
            Assert.Equal(25.0, module.GetTotalEffectiveEarnings());
        }

        [Fact]
        public void ComputeTotalSpendings_NullList_SetsZero()
        {
            module.ComputeTotalSpendings(null);
            Assert.Equal(0.0, module.GetTotalCommittedSpendings());
        }

        [Fact]
        public void ComputeTotalSpendings_EmptyList_SetsZero()
        {
            module.ComputeTotalSpendings(new List<GameAction>());
            Assert.Equal(0.0, module.GetTotalCommittedSpendings());
        }

        [Fact]
        public void ComputeTotalSpendings_IgnoresNonSpendingActions()
        {
            var actions = new List<GameAction>
            {
                MakeEarning(500.0, "subA", 30f, 100f, "rec-A"),
                new GameAction { UT = 600.0, Type = GameActionType.FundsSpending, Cost = 999f },
                MakeSpending(1000.0, "node1", 15f)
            };

            module.ComputeTotalSpendings(actions);

            // Only ScienceSpending (15) counted, not FundsSpending (999)
            Assert.Equal(15.0, module.GetTotalCommittedSpendings());
        }

        [Fact]
        public void ComputeTotalSpendings_SkipsNullEntries()
        {
            var actions = new List<GameAction>
            {
                MakeSpending(500.0, "node1", 10f),
                null,
                MakeSpending(1000.0, "node2", 20f)
            };

            module.ComputeTotalSpendings(actions);
            Assert.Equal(30.0, module.GetTotalCommittedSpendings());
        }

        [Fact]
        public void Reset_ClearsReservationState()
        {
            var actions = new List<GameAction>
            {
                MakeEarning(500.0, "subA", 30f, 100f, "rec-A"),
                MakeSpending(1000.0, "node1", 10f)
            };

            module.ComputeTotalSpendings(actions);
            module.ProcessAction(actions[0]);

            Assert.Equal(30.0, module.GetTotalEffectiveEarnings());
            Assert.Equal(10.0, module.GetTotalCommittedSpendings());
            Assert.Equal(20.0, module.GetAvailableScience());

            // Reset should clear everything
            module.Reset();

            Assert.Equal(0.0, module.GetTotalEffectiveEarnings());
            Assert.Equal(0.0, module.GetTotalCommittedSpendings());
            Assert.Equal(0.0, module.GetAvailableScience());
        }

        // ================================================================
        // Reservation log assertion tests
        // ================================================================

        [Fact]
        public void ComputeTotalSpendings_LogsSummary()
        {
            var actions = new List<GameAction>
            {
                MakeSpending(500.0, "node1", 10f),
                MakeSpending(1000.0, "node2", 20f)
            };

            module.ComputeTotalSpendings(actions);

            Assert.Contains(logLines, l =>
                l.Contains("[ScienceModule]") &&
                l.Contains("ComputeTotalSpendings") &&
                l.Contains("spendingCount=2") &&
                l.Contains("totalCommittedSpendings="));
        }

        [Fact]
        public void Reset_LogsReservationFields()
        {
            logLines.Clear();
            module.Reset();

            Assert.Contains(logLines, l =>
                l.Contains("[ScienceModule]") &&
                l.Contains("Reset") &&
                l.Contains("totalCommittedSpendings=0") &&
                l.Contains("totalEffectiveEarnings=0"));
        }

        // ================================================================
        // ScienceInitial seeding tests (D19)
        // ================================================================

        [Fact]
        public void ScienceInitial_SeedsRunningBalance()
        {
            var module = new ScienceModule();
            module.Reset();
            module.ProcessAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.ScienceInitial,
                InitialScience = 250f
            });

            Assert.Equal(250.0, module.GetRunningScience());
            Assert.Equal(250.0, module.GetTotalEffectiveEarnings());
        }

        [Fact]
        public void ScienceInitial_PlusEarnings_Accumulates()
        {
            var module = new ScienceModule();
            module.Reset();
            module.ProcessAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.ScienceInitial,
                InitialScience = 100f
            });
            module.ProcessAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.ScienceEarning,
                SubjectId = "crewReport@KerbinSrfLanded",
                ScienceAwarded = 5f,
                SubjectMaxValue = 10f
            });

            Assert.Equal(105.0, module.GetRunningScience());
            Assert.Equal(105.0, module.GetTotalEffectiveEarnings());
        }

        // ================================================================
        // ContractComplete science reward tests (D20 fix)
        // ================================================================

        [Fact]
        public void ContractComplete_AddsScienceReward_WhenEffective()
        {
            module.Reset();
            module.ProcessAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.ContractComplete,
                ContractId = "contract-1",
                Effective = true,
                TransformedScienceReward = 25f,
                ScienceReward = 25f
            });

            Assert.Equal(25.0, module.GetRunningScience());
            Assert.Equal(25.0, module.GetTotalEffectiveEarnings());
        }

        [Fact]
        public void ContractComplete_SkipsScienceReward_WhenNotEffective()
        {
            module.Reset();
            module.ProcessAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.ContractComplete,
                ContractId = "contract-1",
                Effective = false,
                TransformedScienceReward = 25f,
                ScienceReward = 25f
            });

            Assert.Equal(0.0, module.GetRunningScience());
            Assert.Equal(0.0, module.GetTotalEffectiveEarnings());
        }

        [Fact]
        public void ContractComplete_UsesTransformedReward_NotRawReward()
        {
            module.Reset();
            module.ProcessAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.ContractComplete,
                ContractId = "contract-1",
                Effective = true,
                ScienceReward = 50f,
                TransformedScienceReward = 40f  // Strategy reduced it
            });

            Assert.Equal(40.0, module.GetRunningScience());
        }

        [Fact]
        public void ContractComplete_ZeroReward_NoEffect()
        {
            module.Reset();
            module.ProcessAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.ContractComplete,
                ContractId = "contract-1",
                Effective = true,
                TransformedScienceReward = 0f,
                ScienceReward = 0f
            });

            Assert.Equal(0.0, module.GetRunningScience());
        }

        [Fact]
        public void ContractComplete_ScienceAddedToAvailable_ForSpending()
        {
            module.Reset();

            // Pre-pass with no spendings
            module.ComputeTotalSpendings(new List<GameAction>());

            // Contract awards science
            module.ProcessAction(new GameAction
            {
                UT = 50.0,
                Type = GameActionType.ContractComplete,
                ContractId = "contract-1",
                Effective = true,
                TransformedScienceReward = 30f,
                ScienceReward = 30f
            });

            Assert.Equal(30.0, module.GetAvailableScience());
        }

        // ================================================================
        // HasSeed tests
        // ================================================================

        [Fact]
        public void HasSeed_FalseBeforeAnyAction()
        {
            Assert.False(module.HasSeed);
        }

        [Fact]
        public void HasSeed_TrueAfterScienceInitial()
        {
            module.ProcessAction(new GameAction
            {
                UT = 0,
                Type = GameActionType.ScienceInitial,
                InitialScience = 500f
            });
            Assert.True(module.HasSeed);
        }

        [Fact]
        public void HasSeed_FalseAfterReset()
        {
            module.ProcessAction(new GameAction
            {
                UT = 0,
                Type = GameActionType.ScienceInitial,
                InitialScience = 500f
            });
            Assert.True(module.HasSeed);
            module.Reset();
            Assert.False(module.HasSeed);
        }

        [Fact]
        public void HasSeed_FalseWithOnlyEarnings()
        {
            module.ProcessEarning(new GameAction
            {
                UT = 10,
                Type = GameActionType.ScienceEarning,
                SubjectId = "crewReport@KerbinSrfLandedLaunchPad",
                ScienceAwarded = 5f,
                SubjectMaxValue = 10f
            });
            Assert.False(module.HasSeed);
        }
    }
}
