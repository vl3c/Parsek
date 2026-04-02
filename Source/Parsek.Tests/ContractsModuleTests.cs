using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class ContractsModuleTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly ContractsModule module;

        public ContractsModuleTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            module = new ContractsModule();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Helpers
        // ================================================================

        private static GameAction MakeAccept(string contractId, double ut = 100,
            string contractType = "PartTest", string title = "Test Contract",
            float advanceFunds = 5000f, float deadlineUT = float.NaN)
        {
            return new GameAction
            {
                Type = GameActionType.ContractAccept,
                UT = ut,
                ContractId = contractId,
                ContractType = contractType,
                ContractTitle = title,
                AdvanceFunds = advanceFunds,
                DeadlineUT = deadlineUT
            };
        }

        private static GameAction MakeComplete(string contractId, double ut = 500,
            string recordingId = "rec-1",
            float fundsReward = 40000f, float repReward = 15f, float scienceReward = 0f)
        {
            return new GameAction
            {
                Type = GameActionType.ContractComplete,
                UT = ut,
                RecordingId = recordingId,
                ContractId = contractId,
                FundsReward = fundsReward,
                RepReward = repReward,
                ScienceReward = scienceReward
            };
        }

        private static GameAction MakeFail(string contractId, double ut = 800,
            string recordingId = null,
            float fundsPenalty = 10000f, float repPenalty = 5f)
        {
            return new GameAction
            {
                Type = GameActionType.ContractFail,
                UT = ut,
                RecordingId = recordingId,
                ContractId = contractId,
                FundsPenalty = fundsPenalty,
                RepPenalty = repPenalty
            };
        }

        private static GameAction MakeCancel(string contractId, double ut = 400,
            float fundsPenalty = 3000f, float repPenalty = 2f)
        {
            return new GameAction
            {
                Type = GameActionType.ContractCancel,
                UT = ut,
                ContractId = contractId,
                FundsPenalty = fundsPenalty,
                RepPenalty = repPenalty
            };
        }

        // ================================================================
        // Verified scenarios from design doc 8.10
        // ================================================================

        [Fact]
        public void BasicAcceptAndComplete()
        {
            // Design doc 8.10 scenario 1:
            // UT=100: Accept "Orbit Mun". Advance +5k funds. Slot consumed.
            // UT=500: Recording A completes "Orbit Mun". +40k funds, +15 rep. Commit.
            //   Slot freed. Contract resolved.

            module.SetMaxSlots(3);

            var accept = MakeAccept("orbit-mun", ut: 100, title: "Orbit Mun",
                advanceFunds: 5000f);
            module.ProcessAction(accept);

            Assert.Equal(1, module.GetActiveContractCount());
            Assert.Equal(2, module.GetAvailableSlots());

            var complete = MakeComplete("orbit-mun", ut: 500,
                fundsReward: 40000f, repReward: 15f);
            module.ProcessAction(complete);

            Assert.True(complete.Effective);
            Assert.True(module.IsContractCredited("orbit-mun"));
            Assert.Equal(0, module.GetActiveContractCount());
            Assert.Equal(3, module.GetAvailableSlots());
        }

        [Fact]
        public void OnceEverCompletion_RetroactivePriority()
        {
            // Design doc 8.10 scenario 2:
            // UT=500: Accept "Orbit Mun".
            // UT=1000: Recording A completes it. effective=true.
            // Rewind to UT=600. Recording B also completes at UT=700. Commit.
            //   Recalculate:
            //     UT=700 (B): effective=true.
            //     UT=1000 (A): effective=false.
            //   Credit shifts to B. Total unchanged.

            module.SetMaxSlots(3);

            // First recalculation walk: A gets credit
            var accept = MakeAccept("orbit-mun", ut: 500);
            var completeA = MakeComplete("orbit-mun", ut: 1000, recordingId: "rec-A",
                fundsReward: 40000f, repReward: 15f);

            module.ProcessAction(accept);
            module.ProcessAction(completeA);

            Assert.True(completeA.Effective);
            Assert.True(module.IsContractCredited("orbit-mun"));

            // Reset and recalculate with both completions in UT order
            module.Reset();

            var accept2 = MakeAccept("orbit-mun", ut: 500);
            var completeB = MakeComplete("orbit-mun", ut: 700, recordingId: "rec-B",
                fundsReward: 40000f, repReward: 15f);
            var completeA2 = MakeComplete("orbit-mun", ut: 1000, recordingId: "rec-A",
                fundsReward: 40000f, repReward: 15f);

            module.ProcessAction(accept2);
            module.ProcessAction(completeB);  // UT=700 — first, gets credit
            module.ProcessAction(completeA2); // UT=1000 — second, duplicate

            Assert.True(completeB.Effective);
            Assert.False(completeA2.Effective);
            Assert.True(module.IsContractCredited("orbit-mun"));
        }

        [Fact]
        public void SlotReservation_AcrossRewind()
        {
            // Design doc 8.10 scenario 3:
            // Mission Control: 3 slots max.
            // Contract A: accepted UT=100, completed UT=500.
            // Contract B: accepted UT=200, pending (no resolution).
            // Contract C: accepted UT=300, cancelled UT=400.
            //
            // All three reserved from UT=0. After processing accepts, 3 active.
            // After C cancelled at 400: 2 active. After A completed at 500: 1 active.

            module.SetMaxSlots(3);

            // Process in UT order
            module.ProcessAction(MakeAccept("A", ut: 100));
            module.ProcessAction(MakeAccept("B", ut: 200));
            module.ProcessAction(MakeAccept("C", ut: 300));

            // After all 3 accepted: 0 available slots
            Assert.Equal(3, module.GetActiveContractCount());
            Assert.Equal(0, module.GetAvailableSlots());

            // C cancelled at UT=400
            module.ProcessAction(MakeCancel("C", ut: 400));
            Assert.Equal(2, module.GetActiveContractCount());
            Assert.Equal(1, module.GetAvailableSlots());

            // A completed at UT=500
            module.ProcessAction(MakeComplete("A", ut: 500));
            Assert.Equal(1, module.GetActiveContractCount());
            Assert.Equal(2, module.GetAvailableSlots());

            // B still active (unresolved)
            Assert.Equal(1, module.GetActiveContractCount());
        }

        // ================================================================
        // Unit tests — individual state transitions
        // ================================================================

        [Fact]
        public void Accept_ConsumesSlot()
        {
            module.SetMaxSlots(3);
            Assert.Equal(3, module.GetAvailableSlots());

            module.ProcessAction(MakeAccept("c1"));

            Assert.Equal(1, module.GetActiveContractCount());
            Assert.Equal(2, module.GetAvailableSlots());
        }

        [Fact]
        public void Complete_FreesSlot()
        {
            module.SetMaxSlots(3);

            module.ProcessAction(MakeAccept("c1"));
            Assert.Equal(1, module.GetActiveContractCount());

            module.ProcessAction(MakeComplete("c1"));
            Assert.Equal(0, module.GetActiveContractCount());
            Assert.Equal(3, module.GetAvailableSlots());
        }

        [Fact]
        public void Fail_FreesSlot()
        {
            module.SetMaxSlots(3);

            module.ProcessAction(MakeAccept("c1"));
            Assert.Equal(1, module.GetActiveContractCount());

            module.ProcessAction(MakeFail("c1"));
            Assert.Equal(0, module.GetActiveContractCount());
            Assert.Equal(3, module.GetAvailableSlots());
        }

        [Fact]
        public void Cancel_FreesSlot()
        {
            module.SetMaxSlots(3);

            module.ProcessAction(MakeAccept("c1"));
            Assert.Equal(1, module.GetActiveContractCount());

            module.ProcessAction(MakeCancel("c1"));
            Assert.Equal(0, module.GetActiveContractCount());
            Assert.Equal(3, module.GetAvailableSlots());
        }

        [Fact]
        public void DuplicateCompletion_NotEffective()
        {
            module.ProcessAction(MakeAccept("c1", ut: 100));

            var first = MakeComplete("c1", ut: 500, recordingId: "rec-A");
            module.ProcessAction(first);
            Assert.True(first.Effective);
            Assert.True(module.IsContractCredited("c1"));

            // Re-accept (new timeline branch) and complete again
            module.ProcessAction(MakeAccept("c1", ut: 600));

            var second = MakeComplete("c1", ut: 700, recordingId: "rec-B");
            module.ProcessAction(second);
            Assert.False(second.Effective);

            // Still credited from first completion
            Assert.True(module.IsContractCredited("c1"));
        }

        [Fact]
        public void AvailableSlots_WithMaxSlots()
        {
            module.SetMaxSlots(5);

            module.ProcessAction(MakeAccept("c1", ut: 100));
            module.ProcessAction(MakeAccept("c2", ut: 200));
            module.ProcessAction(MakeAccept("c3", ut: 300));

            Assert.Equal(3, module.GetActiveContractCount());
            Assert.Equal(2, module.GetAvailableSlots());
        }

        [Fact]
        public void Reset_ClearsState()
        {
            module.SetMaxSlots(5);

            module.ProcessAction(MakeAccept("c1"));
            module.ProcessAction(MakeComplete("c1"));

            Assert.True(module.IsContractCredited("c1"));
            Assert.Equal(0, module.GetActiveContractCount());

            module.Reset();

            Assert.False(module.IsContractCredited("c1"));
            Assert.Equal(0, module.GetActiveContractCount());
            Assert.Equal(5, module.GetAvailableSlots());
        }

        [Fact]
        public void ProcessAction_IgnoresNonContractActions()
        {
            module.SetMaxSlots(3);

            // Process actions of various non-contract types — none should affect state
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.ScienceEarning,
                UT = 100,
                SubjectId = "crewReport@KerbinSrfLanded"
            });

            module.ProcessAction(new GameAction
            {
                Type = GameActionType.FundsEarning,
                UT = 200,
                FundsAwarded = 10000f
            });

            module.ProcessAction(new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = 300,
                MilestoneId = "FirstOrbit"
            });

            module.ProcessAction(new GameAction
            {
                Type = GameActionType.KerbalHire,
                UT = 400,
                KerbalName = "Jeb"
            });

            Assert.Equal(0, module.GetActiveContractCount());
            Assert.Equal(3, module.GetAvailableSlots());
        }

        // ================================================================
        // Log assertion tests
        // ================================================================

        [Fact]
        public void Accept_LogsContractId()
        {
            module.ProcessAction(MakeAccept("orbit-mun-001",
                contractType: "ExploreBody", title: "Orbit Mun"));

            Assert.Contains(logLines, l =>
                l.Contains("[Contracts]") && l.Contains("Accept") &&
                l.Contains("orbit-mun-001"));
        }

        [Fact]
        public void Complete_LogsEffective()
        {
            module.ProcessAction(MakeAccept("c1"));

            var complete = MakeComplete("c1");
            module.ProcessAction(complete);

            Assert.Contains(logLines, l =>
                l.Contains("[Contracts]") && l.Contains("Complete") &&
                l.Contains("effective=true"));
        }

        [Fact]
        public void Fail_LogsContractIdAndPenalty()
        {
            module.ProcessAction(MakeAccept("c1"));
            logLines.Clear();

            module.ProcessAction(MakeFail("c1", ut: 800,
                fundsPenalty: 10000f, repPenalty: 5f));

            Assert.Contains(logLines, l =>
                l.Contains("[Contracts]") && l.Contains("Fail") &&
                l.Contains("c1") && l.Contains("wasActive=True"));
        }

        [Fact]
        public void Cancel_LogsContractIdAndPenalty()
        {
            module.ProcessAction(MakeAccept("c1"));
            logLines.Clear();

            module.ProcessAction(MakeCancel("c1", ut: 400,
                fundsPenalty: 3000f, repPenalty: 2f));

            Assert.Contains(logLines, l =>
                l.Contains("[Contracts]") && l.Contains("Cancel") &&
                l.Contains("c1") && l.Contains("wasActive=True"));
        }

        [Fact]
        public void Accept_Duplicate_LogsWarning()
        {
            module.ProcessAction(MakeAccept("c1"));
            logLines.Clear();

            module.ProcessAction(MakeAccept("c1", ut: 200));

            Assert.Contains(logLines, l =>
                l.Contains("[Contracts]") &&
                l.Contains("already active") &&
                l.Contains("c1"));
        }

        [Fact]
        public void Complete_LogsSlotFreed()
        {
            module.ProcessAction(MakeAccept("c1"));
            logLines.Clear();

            module.ProcessAction(MakeComplete("c1"));

            Assert.Contains(logLines, l =>
                l.Contains("[Contracts]") &&
                l.Contains("slot freed") &&
                l.Contains("c1") &&
                l.Contains("wasActive=True"));
        }

        [Fact]
        public void Reset_LogsClearedCounts()
        {
            module.ProcessAction(MakeAccept("c1"));
            module.ProcessAction(MakeComplete("c1"));
            logLines.Clear();

            module.Reset();

            Assert.Contains(logLines, l =>
                l.Contains("[Contracts]") &&
                l.Contains("Reset") &&
                l.Contains("cleared") &&
                l.Contains("1 credited"));
        }

        [Fact]
        public void SetMaxSlots_LogsNewValue()
        {
            logLines.Clear();

            module.SetMaxSlots(5);

            Assert.Contains(logLines, l =>
                l.Contains("[Contracts]") &&
                l.Contains("SetMaxSlots") &&
                l.Contains("maxSlots=5"));
        }

        // ================================================================
        // Deadline failure generation tests
        // ================================================================

        [Fact]
        public void DeadlineExpired_FreesSlot()
        {
            // Contract accepted at UT=100 with deadline at UT=500.
            // Next action at UT=600 triggers deadline check — contract auto-expires.
            module.SetMaxSlots(3);

            var accept = MakeAccept("c1", ut: 100, deadlineUT: 500f);
            module.ProcessAction(accept);

            Assert.Equal(1, module.GetActiveContractCount());
            Assert.Equal(2, module.GetAvailableSlots());

            // Process any action past the deadline
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.FundsEarning,
                UT = 600,
                FundsAwarded = 1000f
            });

            Assert.Equal(0, module.GetActiveContractCount());
            Assert.Equal(3, module.GetAvailableSlots());

            Assert.Contains(logLines, l =>
                l.Contains("[Contracts]") && l.Contains("DeadlineExpired") &&
                l.Contains("c1") && l.Contains("slot freed"));
        }

        [Fact]
        public void DeadlineNaN_NeverExpires()
        {
            // Contract with NaN deadline should never auto-expire.
            module.SetMaxSlots(3);

            var accept = MakeAccept("c1", ut: 100, deadlineUT: float.NaN);
            module.ProcessAction(accept);

            Assert.Equal(1, module.GetActiveContractCount());

            // Process action at very high UT — contract should remain active
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.FundsEarning,
                UT = 999999,
                FundsAwarded = 1000f
            });

            Assert.Equal(1, module.GetActiveContractCount());
        }

        [Fact]
        public void CompletedBeforeDeadline_NoExpiration()
        {
            // Contract completed before its deadline should not trigger expiration.
            module.SetMaxSlots(3);

            var accept = MakeAccept("c1", ut: 100, deadlineUT: 500f);
            module.ProcessAction(accept);

            var complete = MakeComplete("c1", ut: 400);
            module.ProcessAction(complete);

            Assert.True(complete.Effective);
            Assert.Equal(0, module.GetActiveContractCount());

            logLines.Clear();

            // Process action past the deadline — no expiration should fire
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.FundsEarning,
                UT = 600,
                FundsAwarded = 1000f
            });

            Assert.Equal(0, module.GetActiveContractCount());
            Assert.DoesNotContain(logLines, l => l.Contains("DeadlineExpired"));
        }

        [Fact]
        public void DeadlineExpired_CheckDeadlinesReturnsExpiredIds()
        {
            // Verify CheckDeadlines returns the expired contract IDs.
            module.SetMaxSlots(5);

            module.ProcessAction(MakeAccept("c1", ut: 100, deadlineUT: 300f));
            module.ProcessAction(MakeAccept("c2", ut: 100, deadlineUT: 400f));
            module.ProcessAction(MakeAccept("c3", ut: 100, deadlineUT: 600f));

            Assert.Equal(3, module.GetActiveContractCount());

            var expired = module.CheckDeadlines(500);

            Assert.Equal(2, expired.Count);
            Assert.Contains("c1", expired);
            Assert.Contains("c2", expired);
            // c3 should still be active (deadline=600 > currentUT=500)
            Assert.Equal(1, module.GetActiveContractCount());
        }

        [Fact]
        public void DeadlineExpired_LogsExpiration()
        {
            module.SetMaxSlots(3);

            module.ProcessAction(MakeAccept("orbit-mun", ut: 100, deadlineUT: 500f));
            logLines.Clear();

            module.CheckDeadlines(600);

            Assert.Contains(logLines, l =>
                l.Contains("[Contracts]") && l.Contains("DeadlineExpired") &&
                l.Contains("orbit-mun") && l.Contains("currentUT=600"));
        }

        // ================================================================
        // Log assertion tests (continued)
        // ================================================================

        [Fact]
        public void DuplicateComplete_LogsNotEffective()
        {
            module.ProcessAction(MakeAccept("c1"));
            module.ProcessAction(MakeComplete("c1", ut: 500));

            // Re-accept and duplicate complete
            module.ProcessAction(MakeAccept("c1", ut: 600));
            logLines.Clear();

            module.ProcessAction(MakeComplete("c1", ut: 700));

            Assert.Contains(logLines, l =>
                l.Contains("[Contracts]") && l.Contains("Complete") &&
                l.Contains("effective=false"));
        }
    }
}
