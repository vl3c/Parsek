using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Design section 2.12 Rules 2, 4 and 5 and Invariant LS: the Re-Fly merge dominance
    /// check. The pure cases drive <see cref="ReFlyMergeDominance.Evaluate"/> over explicit
    /// L rows, attempt rows and retire sets on the real cloned module walk; the live cases
    /// drive <see cref="ReFlyMergeDominance.EvaluateForMerge"/> and
    /// <see cref="MergeDialog.MergeCommit"/> against a scenario with a session marker.
    /// </summary>
    [Collection("Sequential")]
    public class ReFlyMergeDominanceTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        private const string Jeb = "Jebediah Kerman";
        private const string Sess = "sess_dom";
        private const string NewRec = "rec_new";
        private const string OldRec = "rec_old";
        private const string LaterRec = "rec_later";

        public ReFlyMergeDominanceTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            SessionSuppressionState.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            InFlightRecoveryRequest.ResetForTesting();
            ReFlyMergeDominance.ResetForTesting();
            MergeDialog.ReShowRefusedMergeDialogForTesting = null;
            KerbalsModule.CrewRespawnPolicyProviderForTesting = PermanentDeath;
            LedgerOrchestrator.Initialize();
        }

        public void Dispose()
        {
            ReFlyMergeDominance.ResetForTesting();
            MergeDialog.ReShowRefusedMergeDialogForTesting = null;
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            SessionSuppressionState.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            InFlightRecoveryRequest.ResetForTesting();
            KerbalsModule.CrewRespawnPolicyProviderForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static bool PermanentDeath(out bool respawns, out double timerSeconds)
        {
            respawns = false;
            timerSeconds = 0.0;
            return true;
        }

        // ------------------------------------------------------------------
        // Row helpers
        // ------------------------------------------------------------------

        private static List<GameAction> Seeds(float funds = 10000f, float science = 100f, float rep = 0f)
        {
            return new List<GameAction>
            {
                new GameAction { ActionId = "seed_funds", Type = GameActionType.FundsInitial, UT = 0.0, InitialFunds = funds },
                new GameAction { ActionId = "seed_sci", Type = GameActionType.ScienceInitial, UT = 0.0, InitialScience = science },
                new GameAction { ActionId = "seed_rep", Type = GameActionType.ReputationInitial, UT = 0.0, InitialReputation = rep },
            };
        }

        private static GameAction RepLoss(string id, string rec, double ut, float penalty)
        {
            return new GameAction
            {
                ActionId = id,
                Type = GameActionType.ReputationPenalty,
                RecordingId = rec,
                UT = ut,
                NominalPenalty = penalty,
                RepPenaltySource = ReputationPenaltySource.KerbalDeath,
            };
        }

        private static GameAction Crew(string id, string rec, string kerbal, double start, double end,
            KerbalEndState endState)
        {
            return new GameAction
            {
                ActionId = id,
                Type = GameActionType.KerbalAssignment,
                RecordingId = rec,
                UT = start,
                KerbalName = kerbal,
                KerbalRole = "Pilot",
                StartUT = (float)start,
                EndUT = (float)end,
                KerbalEndStateField = endState,
            };
        }

        private static ReFlyMergeDominance.Inputs Inputs(
            List<GameAction> els, List<GameAction> newRows, List<GameAction> retire,
            double rewindUT = 100.0, Func<string, double> respawn = null)
        {
            return new ReFlyMergeDominance.Inputs
            {
                SessionId = Sess,
                Els = els,
                AttemptRecordingIds = new HashSet<string> { NewRec },
                NewRows = newRows ?? new List<GameAction>(),
                RetireSet = retire ?? new List<GameAction>(),
                RewindUT = rewindUT,
                RespawnSecondsForRecording = respawn ?? (_ => double.NaN),
            };
        }

        // ------------------------------------------------------------------
        // Rule 4
        // ------------------------------------------------------------------

        [Fact]
        public void Evaluate_AttemptFailsAContractALaterFlightCompletes_RefusesOnFunds()
        {
            var els = Seeds();
            els.Add(new GameAction
            {
                ActionId = "accept_c1", Type = GameActionType.ContractAccept, UT = 50.0,
                ContractId = "c1", DeadlineUT = double.NaN,
            });
            els.Add(new GameAction
            {
                ActionId = "complete_c1", Type = GameActionType.ContractComplete, UT = 800.0,
                RecordingId = LaterRec, ContractId = "c1", FundsReward = 5000f, RepReward = 10f,
            });
            var fail = new GameAction
            {
                ActionId = "fail_c1", Type = GameActionType.ContractFail, UT = 300.0,
                RecordingId = NewRec, ContractId = "c1", FundsPenalty = 1000f, RepPenalty = 5f,
            };

            var v = ReFlyMergeDominance.Evaluate(Inputs(els, new List<GameAction> { fail }, null));

            Assert.True(v.Applicable);
            Assert.False(v.Pass);
            Assert.Equal("funds", v.Pool);
            Assert.Equal(300.0, v.UT);
            Assert.Equal(10000.0, v.Before, 3);
            Assert.Equal(9000.0, v.After, 3);
        }

        /// <summary>An active contract with deadline 400 accepted at 50, L's last row at
        /// 300 (before the deadline), and the attempt's row at 500 (after it).</summary>
        private static List<GameAction> DeadlineContractL(out List<GameAction> attemptRows)
        {
            var els = Seeds();
            els.Add(new GameAction
            {
                ActionId = "accept_c2", Type = GameActionType.ContractAccept, UT = 50.0,
                ContractId = "c2", DeadlineUT = 400.0, FundsPenalty = 1000f, RepPenalty = 5f,
            });
            els.Add(new GameAction
            {
                ActionId = "earn_l", Type = GameActionType.FundsEarning, UT = 300.0,
                RecordingId = LaterRec, FundsAwarded = 100f, FundsSource = FundsEarningSource.Other,
            });
            attemptRows = new List<GameAction>
            {
                new GameAction
                {
                    ActionId = "earn_attempt", Type = GameActionType.FundsEarning, UT = 500.0,
                    RecordingId = NewRec, FundsAwarded = 100f, FundsSource = FundsEarningSource.Other,
                },
            };
            return els;
        }

        // catches: each walk judging deadlines at its own last action, so L' alone fails
        // the contract between L's last UT and the attempt's row (a false refuse).
        [Fact]
        public void Evaluate_AttemptRowPastAnActiveDeadline_JudgedAtTheSessionClock_Passes()
        {
            List<GameAction> attemptRows;
            var els = DeadlineContractL(out attemptRows);
            var inputs = Inputs(els, attemptRows, null);
            inputs.DeadlineClockUT = 350.0;

            var v = ReFlyMergeDominance.Evaluate(inputs);

            Assert.True(v.Pass);
        }

        [Fact]
        public void Evaluate_AttemptRowPastAnActiveDeadline_NoSessionClock_BothWalksShareOneClock()
        {
            List<GameAction> attemptRows;
            var els = DeadlineContractL(out attemptRows);

            var v = ReFlyMergeDominance.Evaluate(Inputs(els, attemptRows, null));

            Assert.True(v.Pass);
        }

        private static GameAction DeadlineFail(double ut, string recordingId) => new GameAction
        {
            ActionId = "fail_c2", Type = GameActionType.ContractFail, UT = ut,
            ContractId = "c2", RecordingId = recordingId, FundsPenalty = 1000f, RepPenalty = 5f,
        };

        // catches: a stock deadline expiry during the attempt filed under the attempt, so
        // L walks only its penalty-free synthetic expiry while L' charges the real one and
        // every re-fly that outlives an open deadline is refused (todo
        // REFLY-DEADLINE-EXPIRY-REFUSES-THE-MERGE). The expiry is filed untagged
        // (`GameStateRecorder.IsDeadlineExpiryFire`), so it is in L as well.
        [Fact]
        public void Evaluate_UntaggedDeadlineExpiryDuringTheAttempt_IsInBothWalks_Passes()
        {
            List<GameAction> attemptRows;
            var els = DeadlineContractL(out attemptRows);
            els.Add(DeadlineFail(400.5, null));
            var inputs = Inputs(els, attemptRows, null);
            inputs.DeadlineClockUT = 450.0;

            var v = ReFlyMergeDominance.Evaluate(inputs);

            Assert.True(v.Pass, "refused at pool=" + v.Pool + " ut=" + v.UT);
        }

        [Fact]
        public void Evaluate_TaggedDeadlineExpiry_DocumentsWhyTheExpiryIsFiledUntagged_Refused()
        {
            List<GameAction> attemptRows;
            var els = DeadlineContractL(out attemptRows);
            attemptRows.Add(DeadlineFail(400.5, NewRec));
            var inputs = Inputs(els, attemptRows, null);
            inputs.DeadlineClockUT = 450.0;

            var v = ReFlyMergeDominance.Evaluate(inputs);

            Assert.False(v.Pass);
            Assert.Equal("funds", v.Pool);
        }

        // A fail BEFORE the deadline is the attempt's own (a parameter it failed), filed
        // under it, and stays a loss L does not have.
        [Fact]
        public void Evaluate_AttemptFailBeforeTheDeadline_StaysTheAttemptsOwnLoss_Refused()
        {
            List<GameAction> attemptRows;
            var els = DeadlineContractL(out attemptRows);
            attemptRows.Add(DeadlineFail(350.0, NewRec));
            var inputs = Inputs(els, attemptRows, null);
            inputs.DeadlineClockUT = 450.0;

            var v = ReFlyMergeDominance.Evaluate(inputs);

            Assert.False(v.Pass);
            Assert.Equal("funds", v.Pool);
        }

        [Fact]
        public void ResolveDeadlineClockUT_TheLaterOfTheSessionClockAndTheLatestRowOfBothLists()
        {
            var l = new List<GameAction> { new GameAction { UT = 300.0 } };
            var a = new List<GameAction> { new GameAction { UT = 500.0 } };

            Assert.Equal(600.0, ReFlyMergeDominance.ResolveDeadlineClockUT(600.0, l, a));
            Assert.Equal(500.0, ReFlyMergeDominance.ResolveDeadlineClockUT(350.0, l, a));
            Assert.Equal(500.0, ReFlyMergeDominance.ResolveDeadlineClockUT(double.NaN, l, a));
            Assert.Equal(500.0, ReFlyMergeDominance.ResolveDeadlineClockUT(double.MaxValue, l, a));
        }

        [Fact]
        public void Evaluate_AttemptReachesAMilestoneEarlier_PassesAndTheCreditMovesEarlier()
        {
            var els = Seeds();
            var later = new GameAction
            {
                ActionId = "ms_later", Type = GameActionType.MilestoneAchievement, UT = 800.0,
                RecordingId = LaterRec, MilestoneId = "FirstOrbit",
                MilestoneFundsAwarded = 1000f, MilestoneRepAwarded = 2f,
            };
            els.Add(later);
            var earlier = new GameAction
            {
                ActionId = "ms_attempt", Type = GameActionType.MilestoneAchievement, UT = 300.0,
                RecordingId = NewRec, MilestoneId = "FirstOrbit",
                MilestoneFundsAwarded = 1000f, MilestoneRepAwarded = 2f,
            };

            var v = ReFlyMergeDominance.Evaluate(Inputs(els, new List<GameAction> { earlier }, null));

            Assert.True(v.Pass);
            Assert.Equal(LossSwapVariant.A, v.Variant);

            var after = new List<GameAction>(els) { earlier };
            var walk = RecalculationEngine.WalkBalancesOnClones(after);
            Assert.True(walk.EffectiveByActionId["ms_attempt"]);
            Assert.False(walk.EffectiveByActionId["ms_later"]);
            Assert.True(later.Effective);
            Assert.True(earlier.Effective);
        }

        // ------------------------------------------------------------------
        // Rule 2
        // ------------------------------------------------------------------

        [Fact]
        public void Evaluate_AttemptLosesMoreReputation_ChoosesVariantBAndReputationNeverDropsBelowL()
        {
            var els = Seeds();
            var oldLoss = RepLoss("rep_old", OldRec, 300.0, 10f);
            els.Add(oldLoss);
            var newLoss = RepLoss("rep_new", NewRec, 310.0, 20f);

            var v = ReFlyMergeDominance.Evaluate(Inputs(
                els, new List<GameAction> { newLoss }, new List<GameAction> { oldLoss }));

            Assert.True(v.Pass);
            Assert.Equal(LossSwapVariant.B, v.Variant);
            Assert.Equal(10.0, v.RepLossOld, 3);
            Assert.Equal(20.0, v.RepLossNew, 3);

            var retireIds = new HashSet<string> { "rep_old" };
            var after = ReFlyMergeDominance.ComposeAfter(
                els, new List<GameAction> { newLoss }, retireIds, retireIds, new HashSet<string> { NewRec });
            Assert.Contains(after, a => a.ActionId == "rep_old");
            Assert.DoesNotContain(after, a => a.ActionId == "rep_new");
            var before = ReFlyMergeDominance.BalanceSeries.From(RecalculationEngine.WalkBalancesOnClones(els));
            var afterSeries = ReFlyMergeDominance.BalanceSeries.From(RecalculationEngine.WalkBalancesOnClones(after));
            foreach (double ut in before.UTs.Concat(afterSeries.UTs).Concat(new[] { 305.0, 310.0, 1000.0 }))
                Assert.True(afterSeries.ValueAt("reputation", ut) >= before.ValueAt("reputation", ut) - 1e-6,
                    "reputation at ut=" + ut);
        }

        [Fact]
        public void Evaluate_AttemptLosesLessReputation_ChoosesVariantA()
        {
            var els = Seeds();
            var oldLoss = RepLoss("rep_old", OldRec, 300.0, 20f);
            els.Add(oldLoss);
            var newLoss = RepLoss("rep_new", NewRec, 300.0, 10f);

            var v = ReFlyMergeDominance.Evaluate(Inputs(
                els, new List<GameAction> { newLoss }, new List<GameAction> { oldLoss }));

            Assert.True(v.Pass);
            Assert.Equal(LossSwapVariant.A, v.Variant);
        }

        // ------------------------------------------------------------------
        // World clause
        // ------------------------------------------------------------------

        [Fact]
        public void Evaluate_AttemptKillsAKerbalALaterFlightFlies_RefusesOnTheWorldClause()
        {
            var els = Seeds();
            els.Add(Crew("crew_later", LaterRec, Jeb, 500.0, 600.0, KerbalEndState.Recovered));
            var death = Crew("crew_attempt", NewRec, Jeb, 100.0, 300.0, KerbalEndState.Dead);

            var v = ReFlyMergeDominance.Evaluate(Inputs(els, new List<GameAction> { death }, null));

            Assert.False(v.Pass);
            Assert.Equal("kerbal", v.WorldKind);
            Assert.Equal(Jeb, v.WorldId);
            Assert.Equal(500.0, v.UT);
        }

        [Fact]
        public void Evaluate_KilledKerbalRespawnsBeforeTheLaterFlight_Passes()
        {
            var els = Seeds();
            els.Add(Crew("crew_later", LaterRec, Jeb, 500.0, 600.0, KerbalEndState.Recovered));
            var death = Crew("crew_attempt", NewRec, Jeb, 100.0, 300.0, KerbalEndState.Dead);

            var v = ReFlyMergeDominance.Evaluate(Inputs(els, new List<GameAction> { death }, null,
                respawn: rec => rec == NewRec ? 100.0 : double.NaN));

            Assert.True(v.Pass);
            Assert.Null(v.WorldKind);
        }

        [Fact]
        public void Evaluate_AttemptDestroysAFacilityALaterRowUpgrades_RefusesOnTheWorldClause()
        {
            var els = Seeds();
            els.Add(new GameAction
            {
                ActionId = "upgrade_lp", Type = GameActionType.FacilityUpgrade, UT = 700.0,
                FacilityId = "LaunchPad", ToLevel = 2, FacilityCost = 0f,
            });
            var destroyed = new GameAction
            {
                ActionId = "destroy_lp", Type = GameActionType.FacilityDestruction, UT = 300.0,
                RecordingId = NewRec, FacilityId = "LaunchPad",
            };

            var v = ReFlyMergeDominance.Evaluate(Inputs(els, new List<GameAction> { destroyed }, null));

            Assert.False(v.Pass);
            Assert.Equal("facility", v.WorldKind);
            Assert.Equal("LaunchPad", v.WorldId);
            Assert.Equal(700.0, v.UT);
        }

        // ------------------------------------------------------------------
        // Funds / science
        // ------------------------------------------------------------------

        [Fact]
        public void Evaluate_AttemptSpendLeavesALaterPurchaseShort_RefusesOnFunds()
        {
            var els = Seeds();
            els.Add(new GameAction
            {
                ActionId = "buy_later", Type = GameActionType.FundsSpending, UT = 500.0,
                RecordingId = LaterRec, FundsSpent = 8000f,
            });
            var spend = new GameAction
            {
                ActionId = "spend_attempt", Type = GameActionType.FundsSpending, UT = 300.0,
                RecordingId = NewRec, FundsSpent = 5000f,
            };

            var v = ReFlyMergeDominance.Evaluate(Inputs(els, new List<GameAction> { spend }, null));

            Assert.False(v.Pass);
            Assert.Equal("funds", v.Pool);
            Assert.Equal(300.0, v.UT);
        }

        [Fact]
        public void Evaluate_AttemptScienceDebitLeavesALaterTechShort_RefusesOnScience()
        {
            var els = Seeds();
            els.Add(new GameAction
            {
                ActionId = "tech_later", Type = GameActionType.ScienceSpending, UT = 500.0,
                NodeId = "basicRocketry", Cost = 80f,
            });
            var debit = new GameAction
            {
                ActionId = "debit_attempt", Type = GameActionType.StrategyScienceDebit, UT = 300.0,
                RecordingId = NewRec, Cost = 50f,
            };

            var v = ReFlyMergeDominance.Evaluate(Inputs(els, new List<GameAction> { debit }, null));

            Assert.False(v.Pass);
            Assert.Equal("science", v.Pool);
            Assert.Equal(300.0, v.UT);
        }

        [Fact]
        public void Evaluate_SpendAffordableInLButNotInLPrime_RefusesNamingTheSpend()
        {
            var els = Seeds();
            var tech = new GameAction
            {
                ActionId = "tech_later", Type = GameActionType.ScienceSpending, UT = 500.0,
                NodeId = "basicRocketry", Cost = 80f,
            };
            els.Add(tech);
            int calls = 0;
            ReFlyMergeDominance.WalkerForTesting = actions =>
            {
                calls++;
                var w = new RecalculationEngine.IsolatedBalanceWalk();
                w.Samples.Add(new RecalculationEngine.LedgerBalanceSample { UT = 0.0, Funds = 10000, Science = 100, Reputation = 0 });
                w.AffordableByActionId["tech_later"] = calls == 1;
                return w;
            };

            var v = ReFlyMergeDominance.Evaluate(Inputs(els, null, null));

            Assert.False(v.Pass);
            Assert.Equal("science", v.Pool);
            Assert.Equal(500.0, v.UT);
            Assert.Equal("ScienceSpending:tech_later", v.SpendingRow);
        }

        // ------------------------------------------------------------------
        // Rule 1 re-check
        // ------------------------------------------------------------------

        [Fact]
        public void Evaluate_RetireSetHoldsASealingRow_RefusesOnSealing()
        {
            var els = Seeds();
            var milestone = new GameAction
            {
                ActionId = "ms_old", Type = GameActionType.MilestoneAchievement, UT = 300.0,
                RecordingId = OldRec, MilestoneId = "FirstOrbit", MilestoneFundsAwarded = 1000f,
            };
            els.Add(milestone);

            var v = ReFlyMergeDominance.Evaluate(Inputs(els, null, new List<GameAction> { milestone }));

            Assert.False(v.Pass);
            Assert.Equal("sealing", v.Pool);
            Assert.Equal("MilestoneAchievement:ms_old", v.SealingRow);
        }

        [Fact]
        public void Evaluate_BareMilestoneAheadOfAnEarning_NamesTheEarning()
        {
            var els = Seeds();
            var bare = new GameAction
            {
                ActionId = "ms_bare", Type = GameActionType.MilestoneAchievement, UT = 300.0,
                RecordingId = OldRec, MilestoneId = "Kerbin/Science",
            };
            var earning = new GameAction
            {
                ActionId = "fund_old", Type = GameActionType.FundsEarning, UT = 300.0,
                RecordingId = OldRec, FundsAwarded = 500f,
            };
            els.Add(bare);
            els.Add(earning);

            var v = ReFlyMergeDominance.Evaluate(
                Inputs(els, null, new List<GameAction> { bare, earning }));

            Assert.False(v.Pass);
            Assert.Equal("sealing", v.Pool);
            Assert.Equal("FundsEarning:fund_old", v.SealingRow);
        }

        [Fact]
        public void LogVerdict_WritesTheDesignedLineShapes()
        {
            ReFlyMergeDominance.LogVerdict(new ReFlyMergeDominanceVerdict
            {
                Applicable = true, Pass = false, SessionId = Sess, Pool = "funds", UT = 300.0,
                Before = 10000.0, After = 9000.0, Variant = LossSwapVariant.B, RepLossOld = 10.0, RepLossNew = 20.0,
            }, "merge-click", 2);
            ReFlyMergeDominance.LogVerdict(new ReFlyMergeDominanceVerdict
            {
                Applicable = true, Pass = false, SessionId = Sess, Pool = "sealing", UT = 300.0,
                SealingRow = "MilestoneAchievement:ms_old",
            }, "dialog-open", 1);
            ReFlyMergeDominance.LogVerdict(new ReFlyMergeDominanceVerdict
            {
                Applicable = true, Pass = true, SessionId = Sess, Pool = "none", UT = double.NaN,
            }, "dialog-open", 0);

            Assert.Contains(logLines, l => l.Contains("[INFO][LedgerSwap] Merge dominance check sess=sess_dom " +
                "result=refuse pool=Funds ut=300 before=10000 after=9000 context=merge-click"));
            Assert.Contains(logLines, l => l.Contains("[INFO][LedgerSwap] Loss swap sess=sess_dom variant=B " +
                "repLossOld=10 repLossNew=20"));
            Assert.Contains(logLines, l => l.Contains("Merge dominance check sess=sess_dom result=refuse " +
                "reason=recordingAction:MilestoneAchievement:ms_old ut=300 context=dialog-open"));
            Assert.Equal(2, logLines.Count(l => l.Contains("Loss swap sess=")));
            Assert.Contains(logLines, l => l.Contains("Merge dominance check sess=sess_dom result=pass " +
                "pool=- ut=<none> before=0 after=0 context=dialog-open"));
        }

        // ------------------------------------------------------------------
        // Live wrapper
        // ------------------------------------------------------------------

        [Fact]
        public void EvaluateForMerge_NoSession_IsANoOp()
        {
            var tree = new RecordingTree { Id = "tree_plain", TreeName = "Plain" };
            Ledger.AddActions(Seeds());
            string ledgerBefore = LedgerCommitRowBuildTests.CanonicalLedger(Ledger.Actions);

            List<GameAction> attemptRows;
            var v = ReFlyMergeDominance.EvaluateForMerge(tree, "dialog-open", out attemptRows);

            Assert.False(v.Applicable);
            Assert.True(v.Pass);
            Assert.Empty(attemptRows);
            Assert.Equal(ledgerBefore, LedgerCommitRowBuildTests.CanonicalLedger(Ledger.Actions));
            Assert.Contains(logLines, l => l.Contains("[LedgerSwap]") && l.Contains("Merge dominance check skipped"));
            Assert.DoesNotContain(logLines, l => l.Contains("Merge dominance check sess="));
            Assert.DoesNotContain(logLines, l => l.Contains("Loss swap sess="));
        }

        /// <summary>
        /// A committed origin tree whose origin recording killed Bill (death + rep penalty,
        /// both loss rows the merge retires), a later committed recording that flies Jeb at
        /// UT 500, and a pending Re-Fly tree whose attempt recording ends Destroyed at 300
        /// with Jeb aboard.
        /// </summary>
        private RecordingTree InstallLiveFixture()
        {
            var origin = new Recording
            {
                RecordingId = "rec_origin",
                VesselName = "Origin",
                TreeId = "tree_1",
                MergeState = MergeState.Immutable,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 250.0,
                TerminalStateValue = TerminalState.Destroyed,
            };
            var originTree = new RecordingTree { Id = "tree_1", TreeName = "Origin", RootRecordingId = "rec_origin" };
            originTree.AddOrReplaceRecording(origin);
            RecordingStore.AddRecordingWithTreeForTesting(origin, "tree_1");
            RecordingStore.AddCommittedTreeForTesting(originTree);

            var later = new Recording
            {
                RecordingId = LaterRec,
                VesselName = "Later",
                TreeId = "tree_later",
                MergeState = MergeState.Immutable,
                ExplicitStartUT = 500.0,
                ExplicitEndUT = 600.0,
            };
            var laterTree = new RecordingTree { Id = "tree_later", TreeName = "Later", RootRecordingId = LaterRec };
            laterTree.AddOrReplaceRecording(later);
            RecordingStore.AddRecordingWithTreeForTesting(later, "tree_later");
            RecordingStore.AddCommittedTreeForTesting(laterTree);

            Ledger.AddActions(Seeds());
            Ledger.AddAction(Crew("crew_origin_bill", "rec_origin", "Bill Kerman", 100.0, 250.0, KerbalEndState.Dead));
            Ledger.AddAction(RepLoss("rep_origin_bill", "rec_origin", 250.0, 10f));
            Ledger.AddAction(Crew("crew_later_jeb", LaterRec, Jeb, 500.0, 600.0, KerbalEndState.Recovered));

            var provisional = new Recording
            {
                RecordingId = "rec_provisional",
                VesselName = "Re-Fly",
                TreeId = "tree_1",
                MergeState = MergeState.NotCommitted,
                CreatingSessionId = Sess,
                SupersedeTargetId = "rec_origin",
                GhostVisualSnapshot = LedgerCommitRowBuildTests.CrewSnapshot(Jeb),
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 300.0,
                TerminalStateValue = TerminalState.Destroyed,
            };
            provisional.Points.Add(new TrajectoryPoint { ut = 100.0 });
            provisional.Points.Add(new TrajectoryPoint { ut = 300.0 });
            var pending = new RecordingTree
            {
                Id = "tree_1",
                TreeName = "Re-Fly",
                RootRecordingId = "rec_provisional",
                ActiveRecordingId = "rec_provisional",
            };
            pending.AddOrReplaceRecording(provisional);

            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = Sess,
                    TreeId = "tree_1",
                    ActiveReFlyRecordingId = "rec_provisional",
                    OriginChildRecordingId = "rec_origin",
                    RewindPointId = "rp_dom",
                    RewindPointUT = 100.0,
                    InvokedUT = 100.0,
                },
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();
            return pending;
        }

        [Fact]
        public void EvaluateForMerge_DialogOpenAndClickAgree_AndTouchNoLiveState()
        {
            var pending = InstallLiveFixture();
            string ledgerBefore = LedgerCommitRowBuildTests.CanonicalLedger(Ledger.Actions);
            int stateVersionBefore = Ledger.StateVersion;

            List<GameAction> openRows, clickRows;
            var open = ReFlyMergeDominance.EvaluateForMerge(pending, "dialog-open", out openRows);
            var click = ReFlyMergeDominance.EvaluateForMerge(pending, "merge-click", out clickRows);

            Assert.True(open.Applicable);
            Assert.False(open.Pass);
            Assert.Equal("kerbal", open.WorldKind);
            Assert.Equal(Jeb, open.WorldId);
            Assert.Equal(open.Pass, click.Pass);
            Assert.Equal(open.Variant, click.Variant);
            Assert.Equal(open.Pool, click.Pool);
            Assert.Equal(open.UT, click.UT);
            Assert.Equal(open.WorldKind, click.WorldKind);
            Assert.Equal(open.WorldId, click.WorldId);
            Assert.Equal(LedgerCommitRowBuildTests.CanonicalLedger(openRows),
                LedgerCommitRowBuildTests.CanonicalLedger(clickRows));
            Assert.Contains(openRows, r => r.Type == GameActionType.KerbalAssignment
                && r.KerbalName == Jeb && r.KerbalEndStateField == KerbalEndState.Dead);

            Assert.Equal(ledgerBefore, LedgerCommitRowBuildTests.CanonicalLedger(Ledger.Actions));
            Assert.Equal(stateVersionBefore, Ledger.StateVersion);
            Assert.Null(pending.Recordings["rec_provisional"].CrewEndStates);
            Assert.Empty(ParsekScenario.Instance.LedgerTombstones);

            Assert.Contains(logLines, l => l.Contains("[INFO][LedgerSwap]")
                && l.Contains("Merge dominance check sess=sess_dom result=refuse world=kerbal:Jebediah Kerman ut=500 ")
                && l.Contains("context=dialog-open"));
            Assert.Contains(logLines, l => l.Contains("[INFO][LedgerSwap]")
                && l.Contains("Merge dominance check sess=sess_dom result=refuse world=kerbal:Jebediah Kerman ut=500 ")
                && l.Contains("context=merge-click"));
            Assert.Contains(logLines, l => l.Contains("[INFO][LedgerSwap]")
                && l.Contains("Loss swap sess=sess_dom variant=A repLossOld=10 repLossNew=0"));
        }

        [Fact]
        public void EvaluateForMerge_RespawningKerbal_PassesAndLogsVariantA()
        {
            KerbalsModule.CrewRespawnPolicyProviderForTesting =
                (out bool respawns, out double timerSeconds) =>
                {
                    respawns = true;
                    timerSeconds = 100.0;
                    return true;
                };
            var pending = InstallLiveFixture();

            List<GameAction> rows;
            var v = ReFlyMergeDominance.EvaluateForMerge(pending, "dialog-open", out rows);

            Assert.True(v.Pass);
            Assert.Equal(LossSwapVariant.A, v.Variant);
            Assert.Contains(logLines, l => l.Contains("[INFO][LedgerSwap]")
                && l.Contains("Merge dominance check sess=sess_dom result=pass pool="));
            Assert.Contains(logLines, l => l.Contains("[INFO][LedgerSwap]")
                && l.Contains("Loss swap sess=sess_dom variant=A "));
        }

        // catches: the check modeling the retire set as retired while the merge retires
        // nothing (AppendRelations refuses an unflown attempt), so a Variant A pass would
        // land the attempt's reputation loss with nothing retired against it.
        [Fact]
        public void EvaluateForMerge_UnflownAttempt_ModelsAnEmptyRetireSet_AndPicksVariantB()
        {
            KerbalsModule.CrewRespawnPolicyProviderForTesting =
                (out bool respawns, out double timerSeconds) =>
                {
                    respawns = true;
                    timerSeconds = 100.0;
                    return true;
                };
            var pending = InstallLiveFixture();
            pending.Recordings["rec_provisional"].Points.Clear();
            Ledger.AddAction(RepLoss("rep_attempt", "rec_provisional", 260.0, 10f));

            List<GameAction> rows;
            var v = ReFlyMergeDominance.EvaluateForMerge(pending, "dialog-open", out rows);

            Assert.True(v.Pass);
            Assert.Equal(0, v.RetireSetCount);
            Assert.Equal(0.0, v.RepLossOld, 3);
            Assert.Equal(LossSwapVariant.B, v.Variant);
            Assert.Contains(logLines, l => l.Contains("[LedgerSwap]")
                && l.Contains("merge would retire nothing")
                && l.Contains("refused-unflown-provisional"));
            Assert.Contains(logLines, l => l.Contains("Merge dominance check sess=sess_dom result=pass")
                && l.Contains("retireSet=0 "));
        }

        [Fact]
        public void Evaluate_RetiresNothing_StillRefusesASealingRowInTheRetireSet()
        {
            var els = Seeds();
            var milestone = new GameAction
            {
                ActionId = "ms_old", Type = GameActionType.MilestoneAchievement, UT = 300.0,
                RecordingId = OldRec, MilestoneId = "FirstOrbit", MilestoneFundsAwarded = 1000f,
            };
            els.Add(milestone);
            var inputs = Inputs(els, null, new List<GameAction> { milestone });
            inputs.RetiresNothing = true;

            var v = ReFlyMergeDominance.Evaluate(inputs);

            Assert.False(v.Pass);
            Assert.Equal("sealing", v.Pool);
        }

        [Fact]
        public void Evaluate_RetiresNothing_KeepsTheRetireSetInLPrime()
        {
            var els = Seeds();
            var oldLoss = RepLoss("rep_old", OldRec, 300.0, 10f);
            els.Add(oldLoss);
            var newLoss = RepLoss("rep_new", NewRec, 300.0, 10f);
            var inputs = Inputs(els, new List<GameAction> { newLoss }, new List<GameAction> { oldLoss });

            var modeledRetired = ReFlyMergeDominance.Evaluate(inputs);
            inputs.RetiresNothing = true;
            var retiresNothing = ReFlyMergeDominance.Evaluate(inputs);

            Assert.Equal(LossSwapVariant.A, modeledRetired.Variant);
            Assert.Equal(1, modeledRetired.RetireSetCount);
            Assert.True(retiresNothing.Pass);
            Assert.Equal(LossSwapVariant.B, retiresNothing.Variant);
            Assert.Equal(0, retiresNothing.RetireSetCount);
        }

        // catches: a replaced-flight row the ledger never filed (the build step files it at
        // the commit, then the merge tombstones it) read as the attempt's own new loss, so a
        // re-fly that saved the crew was refused on reputation (QL-3).
        [Fact]
        public void Evaluate_UnfiledReplacedFlightLossInTheRetireSet_Passes()
        {
            var els = Seeds();
            var oldLoss = RepLoss("rep_old_unfiled", OldRec, 300.0, 9f);
            var inputs = Inputs(els, new List<GameAction> { oldLoss }, new List<GameAction> { oldLoss });

            var v = ReFlyMergeDominance.Evaluate(inputs);

            Assert.True(v.Pass);
            Assert.Equal(LossSwapVariant.A, v.Variant);
        }

        // L is the Discard world, where the unfiled row is never filed: retiring it does not
        // make room for an attempt loss (the merge would still lower reputation by 5).
        [Fact]
        public void Evaluate_UnfiledReplacedFlightLossRetired_AttemptLossStillRefuses()
        {
            var els = Seeds();
            var oldLoss = RepLoss("rep_old_unfiled", OldRec, 300.0, 9f);
            var newLoss = RepLoss("rep_new", NewRec, 300.0, 5f);
            var inputs = Inputs(els, new List<GameAction> { oldLoss, newLoss },
                new List<GameAction> { oldLoss });

            var v = ReFlyMergeDominance.Evaluate(inputs);

            Assert.False(v.Pass);
            Assert.Equal("reputation", v.Pool);
        }

        // The commit files a build-step row of another recording and keeps it; against the
        // Discard world that is a loss the merge brings, so the check refuses.
        [Fact]
        public void Evaluate_UnfiledLossOutsideTheAttemptAndTheRetireSet_Refuses()
        {
            var els = Seeds();
            var otherLoss = RepLoss("rep_other_unfiled", LaterRec, 300.0, 9f);

            var v = ReFlyMergeDominance.Evaluate(
                Inputs(els, new List<GameAction> { otherLoss }, null));

            Assert.False(v.Pass);
        }

        [Fact]
        public void EvaluateForMerge_FlownAttempt_ModelsTheRetireSet_AndPicksVariantA()
        {
            KerbalsModule.CrewRespawnPolicyProviderForTesting =
                (out bool respawns, out double timerSeconds) =>
                {
                    respawns = true;
                    timerSeconds = 100.0;
                    return true;
                };
            var pending = InstallLiveFixture();
            Ledger.AddAction(RepLoss("rep_attempt", "rec_provisional", 260.0, 10f));

            List<GameAction> rows;
            var v = ReFlyMergeDominance.EvaluateForMerge(pending, "dialog-open", out rows);

            Assert.True(v.Pass);
            Assert.Equal(2, v.RetireSetCount);
            Assert.Equal(10.0, v.RepLossOld, 3);
            Assert.Equal(LossSwapVariant.A, v.Variant);
        }

        // catches: a throw in the row build / retire set / closure walk escaping the
        // check, which aborts the merge dialog so no Discard is offered.
        [Fact]
        public void EvaluateForMerge_ThrowOutsideTheWalk_RefusesWithDiscardOnly()
        {
            var pending = InstallLiveFixture();
            ReFlyMergeDominance.PreviewBuilderForTesting = t =>
                throw new InvalidOperationException("injected preview failure");

            List<GameAction> rows;
            var v = ReFlyMergeDominance.EvaluateForMerge(pending, "dialog-open", out rows);

            Assert.True(v.Applicable);
            Assert.False(v.Pass);
            Assert.Equal("error", v.Pool);
            Assert.NotNull(rows);
            Assert.Empty(rows);
            Assert.NotNull(ReFlyMergeDominance.DescribeRefusal(v));
            Assert.Contains(logLines, l => l.Contains("[WARN][LedgerSwap]")
                && l.Contains("Merge dominance check threw (dialog-open)")
                && l.Contains("injected preview failure"));
            Assert.Contains(logLines, l => l.Contains("Merge dominance check sess=sess_dom result=refuse reason=error"));
        }

        [Fact]
        public void MergeCommit_DominanceThrows_LeavesTheTreePendingAndReShowsTheDialog()
        {
            var pending = InstallLiveFixture();
            RecordingStore.StashPendingTree(pending);
            ReFlyMergeDominance.PreviewBuilderForTesting = t =>
                throw new InvalidOperationException("injected preview failure");
            RecordingTree reShown = null;
            MergeDialog.ReShowRefusedMergeDialogForTesting = t => reShown = t;

            MergeDialog.MergeCommit(pending, new Dictionary<string, bool>(), 0,
                refreshQuicksaveAfterCommit: false, reShowDialogOnRefusal: true);

            Assert.Same(pending, RecordingStore.PendingTree);
            Assert.Same(pending, reShown);
            Assert.NotNull(ParsekScenario.Instance.ActiveReFlySessionMarker);
        }

        [Fact]
        public void MergeCommit_DominanceRefused_LeavesTheTreePendingAndTheLedgerUntouched()
        {
            var pending = InstallLiveFixture();
            RecordingStore.StashPendingTree(pending);
            string ledgerBefore = LedgerCommitRowBuildTests.CanonicalLedger(Ledger.Actions);
            int committedBefore = RecordingStore.CommittedRecordings.Count;
            int refusalsBefore = MergeDialog.MergeDominanceRefusalCount;
            RecordingTree reShown = null;
            MergeDialog.ReShowRefusedMergeDialogForTesting = t => reShown = t;

            MergeDialog.MergeCommit(pending, new Dictionary<string, bool>(), 0,
                refreshQuicksaveAfterCommit: false, reShowDialogOnRefusal: true);

            Assert.Same(pending, RecordingStore.PendingTree);
            // The automation seam reads this counter to report the refusal, not a commit.
            Assert.Equal(refusalsBefore + 1, MergeDialog.MergeDominanceRefusalCount);
            Assert.Equal(committedBefore, RecordingStore.CommittedRecordings.Count);
            Assert.Equal(ledgerBefore, LedgerCommitRowBuildTests.CanonicalLedger(Ledger.Actions));
            Assert.NotNull(ParsekScenario.Instance.ActiveReFlySessionMarker);
            Assert.Null(ParsekScenario.Instance.ActiveMergeJournal);
            Assert.Same(pending, reShown);
            Assert.Contains(logLines, l => l.Contains("[MergeDialog]")
                && l.Contains("Merge refused by the dominance check"));
        }

        [Fact]
        public void BuildReFlyDialogBody_WithARefusal_SaysWhyAndOffersNoMerge()
        {
            var v = new ReFlyMergeDominanceVerdict
            {
                Applicable = true, Pass = false, WorldKind = "kerbal", WorldId = Jeb, Pool = "world",
            };
            string reason = ReFlyMergeDominance.DescribeRefusal(v);

            string body = MergeDialog.BuildReFlyDialogBody(
                "Pad Hopper", 120.0, ReFlyAutoSealPreviewResult.NoSeal(), false, reason);

            Assert.Contains(Jeb, body);
            Assert.Contains("cannot be merged", body);
            Assert.DoesNotContain("Merge & Seal", body);
        }
    }
}
