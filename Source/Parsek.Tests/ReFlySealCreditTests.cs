using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Resources design section 3.15 item 10 / rewind design section 2.12 (owner ruling
    /// 2026-10-26): a Re-Fly seal reads credit, not presence. A non-loss row in a slot's
    /// retire set seals only when the ledger replay counts it, even at zero value; a
    /// duplicate the replay leaves out for an identity or state reason does not, so stock
    /// stopped in advance (no row) and stock paid then corrected (a row the replay zeroes)
    /// leave the slot in the same state. A not-counted row stays out only when its identity
    /// has a counted holder AND stock's payment of it was prevented or is taken back; a
    /// deadline expiry, a same-tick resolution or a repeat below the take-back era seals.
    /// The verdict comes from a fresh walk, never from flags an earlier walk left on the
    /// shared rows.
    /// </summary>
    [Collection("Sequential")]
    public class ReFlySealCreditTests : IDisposable
    {
        private const double RpUT = 100.0;

        private readonly List<string> logLines = new List<string>();
        private readonly bool priorStoreSuppress;

        public ReFlySealCreditTests()
        {
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;

            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            SupersedeCommit.ResetWorldActionSafetyCacheForTesting();
            ReFlyMergeDominance.ResetForTesting();
            StockShadowTakeBack.ResetForTesting();
            RegisterWalkModules();
        }

        public void Dispose()
        {
            SupersedeCommit.ResetWorldActionSafetyCacheForTesting();
            ReFlyMergeDominance.ResetForTesting();
            StockShadowTakeBack.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            KspStatePatcher.ResetForTesting();
            ParsekLog.SuppressLogging = true;
        }

        private static void RegisterWalkModules()
        {
            RecalculationEngine.ClearModules();
            RecalculationEngine.RegisterModule(new MilestonesModule(), RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(new ContractsModule(), RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(new ScienceModule(), RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(new FundsModule(), RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(new ReputationModule(), RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(new FacilitiesModule(), RecalculationEngine.ModuleTier.Facilities);
        }

        // ---------- fixture ------------------------------------------------

        private static Recording Rec(string id, MergeState state)
        {
            return new Recording
            {
                RecordingId = id, VesselName = id, TreeId = "tree_1", MergeState = state,
                TerminalStateValue = TerminalState.Destroyed,
            };
        }

        /// <summary>One open slot (origin rec_a, CommittedProvisional) on rp_1 at UT 100, a
        /// sealed sibling flight rec_other in the same tree, and seeds.</summary>
        private static void InstallOpenSlot(out RewindPoint rp, out ChildSlot slot, out Recording origin)
        {
            origin = Rec("rec_a", MergeState.CommittedProvisional);
            var other = Rec("rec_other", MergeState.Immutable);
            var tree = new RecordingTree { Id = "tree_1", TreeName = "Test_tree_1" };
            foreach (var rec in new[] { origin, other })
            {
                tree.AddOrReplaceRecording(rec);
                RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");
            }
            RecordingStore.CommittedTrees.Add(tree);
            slot = new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_a", Controllable = true };
            rp = new RewindPoint
            {
                RewindPointId = "rp_1", BranchPointId = "bp_rp_1", UT = RpUT,
                QuicksaveFilename = "Parsek/RewindPoints/rp_1.sfs", SessionProvisional = false,
                ChildSlots = new List<ChildSlot> { slot },
            };
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                RecordingRewindRetirements = new List<RecordingRewindRetirement>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint> { rp },
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            EffectiveState.ResetCachesForTesting();
            Ledger.AddAction(new GameAction
            {
                ActionId = "seed_f", UT = 0, Type = GameActionType.FundsInitial, InitialFunds = 100000f,
            });
            Ledger.AddAction(new GameAction
            {
                ActionId = "seed_s", UT = 0, Type = GameActionType.ScienceInitial, InitialScience = 100f,
            });
        }

        private static GameAction Milestone(string id, string rec, double ut, float funds, long cseq = 0)
        {
            return new GameAction
            {
                ActionId = id, UT = ut, CaptureSequence = cseq, Type = GameActionType.MilestoneAchievement,
                RecordingId = rec, MilestoneId = "Mun/Landing", MilestoneFundsAwarded = funds,
            };
        }

        private static GameAction Science(string id, string rec, double ut, float awarded, long cseq = 0)
        {
            return new GameAction
            {
                ActionId = id, UT = ut, CaptureSequence = cseq, Type = GameActionType.ScienceEarning, RecordingId = rec,
                SubjectId = "crewReport@MunSrfLanded", ScienceAwarded = awarded, SubjectMaxValue = 5f,
                Method = ScienceMethod.Recovered,
            };
        }

        private static GameAction Accept(string contract, double ut)
        {
            return new GameAction
            {
                ActionId = "acc_" + contract, UT = ut, Type = GameActionType.ContractAccept,
                ContractId = contract,
            };
        }

        private static GameAction Complete(string id, string rec, string contract, double ut, float funds, long cseq = 0)
        {
            return new GameAction
            {
                ActionId = id, UT = ut, CaptureSequence = cseq, Type = GameActionType.ContractComplete, RecordingId = rec,
                ContractId = contract, FundsReward = funds,
            };
        }

        private static GameAction PartPurchase(string id, string rec, double ut, long cseq)
        {
            return new GameAction
            {
                ActionId = id, UT = ut, CaptureSequence = cseq, Type = GameActionType.FundsSpending,
                RecordingId = rec, FundsSpent = 800f, FundsSpendingSource = FundsSpendingSource.Other,
                DedupKey = "solidBooster.v2",
            };
        }

        private static GameAction Death(string id, string rec, double ut)
        {
            return new GameAction
            {
                ActionId = id, Type = GameActionType.KerbalAssignment, RecordingId = rec,
                UT = (float)ut, StartUT = (float)ut, EndUT = (float)ut, KerbalName = "Bill Kerman",
                KerbalEndStateField = KerbalEndState.Dead,
            };
        }

        // A holder captured at 10 (UT 110), a rewind to UT 50 at capture high water 15, and a
        // repeat captured at 20 (UT 120): the take-back era starts at 1, so the walk takes the
        // repeat's stock payment back (3.15 step 5).
        private const long HolderSeq = 10;
        private const long RepeatSeq = 20;

        private static void OpenTakeBackEra(long eraStart = 1)
        {
            StockShadowTakeBack.SetEligibleFromForTesting(eraStart);
            StockShadowTakeBack.AddBreakForTesting(15, 50.0, "PlainRewind");
        }

        private bool Gate(RewindPoint rp, ChildSlot slot, Recording origin, out string summary)
        {
            return SupersedeCommit.RetireSetHoldsSealingRow(origin, rp, slot, out summary);
        }

        private string GateLine() => logLines.LastOrDefault(l => l.Contains("[Supersede]")
            && l.Contains("RetireSet gate (section 2.12 Rule 1)"));

        // ---------- counted rows seal ------------------------------------

        // catches: the zero-value case dropped, a first-reached milestone with no award read
        // as "nothing to give back" (it is the timeline's first credit of that milestone).
        [Fact]
        public void ZeroAwardFirstMilestone_Seals()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            Ledger.AddAction(Milestone("act_ms", "rec_a", 120.0, 0f));

            string summary;
            Assert.True(Gate(rp, slot, origin, out summary));
            Assert.Equal("MilestoneAchievement:act_ms", summary);
            string line = GateLine();
            Assert.NotNull(line);
            Assert.Contains("sealingRows=1", line);
            Assert.Contains("notCounted=0 credit=els", line);
            Assert.Contains("-> sealed by MilestoneAchievement:act_ms", line);
        }

        [Fact]
        public void CountedContractOutcome_Seals()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            Ledger.AddAction(Accept("c1", 50.0));
            Ledger.AddAction(Complete("act_done", "rec_a", "c1", 120.0, 5000f));

            string summary;
            Assert.True(Gate(rp, slot, origin, out summary));
            Assert.Equal("ContractComplete:act_done", summary);
        }

        [Fact]
        public void CountedContractFailure_Seals()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            Ledger.AddAction(Accept("c1", 50.0));
            Ledger.AddAction(new GameAction
            {
                ActionId = "act_fail", UT = 120.0, Type = GameActionType.ContractFail, RecordingId = "rec_a",
                ContractId = "c1", FundsPenalty = 100f, RepPenalty = 2f,
            });

            string summary;
            Assert.True(Gate(rp, slot, origin, out summary));
            Assert.Equal("ContractFail:act_fail", summary);
        }

        [Fact]
        public void CountedFlightTaggedSpend_Seals()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            Ledger.AddAction(PartPurchase("act_part", "rec_a", 120.0, 20));

            string summary;
            Assert.True(Gate(rp, slot, origin, out summary));
            Assert.Equal("FundsSpending:act_part", summary);
        }

        [Fact]
        public void PartlyCappedScience_Seals()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            Ledger.AddAction(Science("act_sci_first", "rec_other", 110.0, 3f));
            Ledger.AddAction(Science("act_sci", "rec_a", 120.0, 3f)); // cap 5 leaves 2

            string summary;
            Assert.True(Gate(rp, slot, origin, out summary));
            Assert.Equal("ScienceEarning:act_sci", summary);
        }

        // ---------- duplicates the replay does not count -------------------

        // catches: the seal going back to presence (any non-loss row seals).
        [Fact]
        public void DuplicateMilestone_TakenBack_DoesNotSeal_AndIsLogged()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            OpenTakeBackEra();
            Ledger.AddAction(Milestone("act_first", "rec_other", 110.0, 5000f, HolderSeq));
            Ledger.AddAction(Milestone("act_dup", "rec_a", 120.0, 5000f, RepeatSeq));

            string summary;
            Assert.False(Gate(rp, slot, origin, out summary));
            Assert.Null(summary);
            string line = GateLine();
            Assert.NotNull(line);
            Assert.Contains("retireSet=1", line);
            Assert.Contains("lossRows=0 sealingRows=0 notCounted=1 credit=els", line);
            Assert.Contains("notCountedRows=[MilestoneAchievement:act_dup(MilestoneAlreadyAchieved)]", line);
            Assert.Contains("-> nothing to give back", line);
            Assert.Contains(logLines, l => l.Contains("[Supersede]")
                && l.Contains("Seal credit walk (section 2.12, 3.15 item 10)")
                && l.Contains("notCounted=1") && l.Contains("source=els"));
        }

        [Fact]
        public void DuplicateContractCompletion_TakenBack_DoesNotSeal()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            OpenTakeBackEra();
            Ledger.AddAction(Accept("c1", 50.0));
            Ledger.AddAction(Complete("act_first", "rec_other", "c1", 110.0, 5000f, HolderSeq));
            Ledger.AddAction(Complete("act_dup", "rec_a", "c1", 120.0, 5000f, RepeatSeq));

            string summary;
            Assert.False(Gate(rp, slot, origin, out summary));
            Assert.Contains("ContractComplete:act_dup(ContractAlreadyCompleted)", GateLine());
        }

        [Fact]
        public void ScienceOfASubjectAlreadyCapped_TakenBack_DoesNotSeal()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            OpenTakeBackEra();
            Ledger.AddAction(Science("act_sci_first", "rec_other", 110.0, 5f, HolderSeq));
            Ledger.AddAction(Science("act_sci", "rec_a", 120.0, 5f, RepeatSeq));

            string summary;
            Assert.False(Gate(rp, slot, origin, out summary));
            Assert.Contains("ScienceEarning:act_sci(" + ReplayCredit.SubjectCapReachedLabel + ")", GateLine());
        }

        [Fact]
        public void PurchaseAlreadyOwned_NotCounted_DoesNotSeal()
        {
            // The holder walks first but was captured after the row: a rewind separated them.
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            Ledger.AddAction(PartPurchase("act_holder", null, 110.0, 50));
            Ledger.AddAction(PartPurchase("act_part", "rec_a", 120.0, 20));

            string summary;
            Assert.False(Gate(rp, slot, origin, out summary));
            Assert.Contains("FundsSpending:act_part(PurchaseAlreadyOwned)", GateLine());
        }

        [Fact]
        public void DuplicateBesideACountedGain_TheGainStillSeals()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            OpenTakeBackEra();
            Ledger.AddAction(Milestone("act_first", "rec_other", 110.0, 5000f, HolderSeq));
            Ledger.AddAction(Milestone("act_dup", "rec_a", 120.0, 5000f, RepeatSeq));
            Ledger.AddAction(new GameAction
            {
                ActionId = "act_gain", Type = GameActionType.FundsEarning, RecordingId = "rec_a", UT = 125.0,
                FundsAwarded = 100f, FundsSource = FundsEarningSource.Other,
            });

            string summary;
            Assert.True(Gate(rp, slot, origin, out summary));
            Assert.Equal("FundsEarning:act_gain", summary);
            Assert.Contains("sealingRows=1 notCounted=1", GateLine());
        }

        // A repeat stock paid nothing (the capture prevented it) needs no take-back.
        [Fact]
        public void PreventedRepeat_NoAmount_DoesNotSeal()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            Ledger.AddAction(Milestone("act_first", "rec_other", 110.0, 5000f));
            Ledger.AddAction(Milestone("act_dup", "rec_a", 120.0, 0f));

            string summary;
            Assert.False(Gate(rp, slot, origin, out summary));
            Assert.Contains("MilestoneAchievement:act_dup(MilestoneAlreadyAchieved)", GateLine());
        }

        // ---------- not-counted rows whose stock payment is kept seal --------

        // catches: a deadline expiry read as a holder. The walk zeroes a completion past the
        // contract's deadline, but stock paid it and nothing takes it back, so the payment
        // stays as an untagged adjustment; a re-fly completing in time would be paid again.
        [Fact]
        public void DeadlinePassedCompletion_Seals_AndTheMergeRefusesAsSealing()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            var acc = Accept("c1", 50.0);
            acc.DeadlineUT = 150.0;
            Ledger.AddAction(acc);
            var late = Complete("act_late", "rec_a", "c1", 200.0, 5000f);
            Ledger.AddAction(late);

            string summary;
            Assert.True(Gate(rp, slot, origin, out summary));
            Assert.Equal("ContractComplete:act_late", summary);
            Assert.Contains("sealingRows=1 notCounted=0", GateLine());

            var seed = new GameAction { ActionId = "seed_f", UT = 0, Type = GameActionType.FundsInitial, InitialFunds = 1000f };
            var inputs = DominanceInputs(new List<GameAction> { seed, acc, late }, new List<GameAction> { late });
            inputs.NewRows = new List<GameAction> { Complete("act_new", "rec_fork", "c1", 120.0, 5000f) };
            ReFlyMergeDominanceVerdict v = ReFlyMergeDominance.Evaluate(inputs);
            Assert.False(v.Pass);
            Assert.Equal("sealing", v.Pool);
            Assert.Equal("ContractComplete:act_late", v.SealingRow);
            Assert.Equal(0, v.RetireSetNotCountedCount);
        }

        // catches: a repeat below the take-back era read as settled. An older build captured
        // both rows, so the walk never takes the repeat's payment back and stock kept it.
        [Fact]
        public void RepeatBelowTheTakeBackEra_Seals()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            OpenTakeBackEra(1000);
            Ledger.AddAction(Milestone("act_first", "rec_other", 110.0, 5000f, HolderSeq));
            Ledger.AddAction(Milestone("act_dup", "rec_a", 120.0, 5000f, RepeatSeq));

            string summary;
            Assert.True(Gate(rp, slot, origin, out summary));
            Assert.Equal("MilestoneAchievement:act_dup", summary);
            Assert.Contains("sealingRows=1 notCounted=0", GateLine());
        }

        // A repeat in the era with no timeline break between it and its holder: stock's
        // state held the holder, so the repeat's payment is not taken back.
        [Fact]
        public void PaidRepeatWithNoTimelineBreak_Seals()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            StockShadowTakeBack.SetEligibleFromForTesting(1);
            Ledger.AddAction(Milestone("act_first", "rec_other", 110.0, 5000f, HolderSeq));
            Ledger.AddAction(Milestone("act_dup", "rec_a", 120.0, 5000f, RepeatSeq));

            string summary;
            Assert.True(Gate(rp, slot, origin, out summary));
            Assert.Equal("MilestoneAchievement:act_dup", summary);
        }

        // catches: the same-tick explicit resolution (the pre-pass zeroes a completion a
        // failure at the same UT resolved) read as settled: it has no witness, so nothing
        // takes the paid reward back.
        [Fact]
        public void SameTickResolvedCompletion_Seals()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            OpenTakeBackEra();
            Ledger.AddAction(Accept("c1", 50.0));
            Ledger.AddAction(new GameAction
            {
                ActionId = "act_fail", UT = 120.0, CaptureSequence = HolderSeq, Type = GameActionType.ContractFail,
                RecordingId = "rec_other", ContractId = "c1", FundsPenalty = 100f,
            });
            Ledger.AddAction(Complete("act_done", "rec_a", "c1", 120.0, 5000f, RepeatSeq));

            string summary;
            Assert.True(Gate(rp, slot, origin, out summary));
            Assert.Equal("ContractComplete:act_done", summary);
            Assert.Contains("sealingRows=1 notCounted=0", GateLine());
        }

        // catches: one ActionId on two rows (older saves) letting a not-counted copy hide a
        // counted one.
        [Fact]
        public void SharedActionId_ACountedCopyKeepsTheIdSealing()
        {
            foreach (bool countedFirst in new[] { true, false })
            {
                var seed = new GameAction { ActionId = "seed_f", UT = 0, Type = GameActionType.FundsInitial, InitialFunds = 1000f };
                var counted = Milestone("act_shared", "rec_other", 110.0, 0f);
                var dup = Milestone("act_shared", "rec_a", 120.0, 0f);
                var rows = countedFirst
                    ? new List<GameAction> { seed, counted, dup }
                    : new List<GameAction> { seed, dup, counted };
                RecalculationEngine.IsolatedBalanceWalk walk = RecalculationEngine.WalkBalancesOnClones(rows);
                Assert.False(walk.NotCountedByActionId.ContainsKey("act_shared"), "countedFirst=" + countedFirst);
                Assert.True(ReplayCredit.IsCounted(dup, walk.NotCountedByActionId));
            }
        }

        // ---------- losses ---------------------------------------------------

        [Fact]
        public void LossesOnly_StayNonBlocking_AndRunNoWalk()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            Ledger.AddAction(Death("act_death", "rec_a", 130.0));
            Ledger.AddAction(new GameAction
            {
                ActionId = "act_rep", Type = GameActionType.ReputationPenalty, RecordingId = "rec_a", UT = 130.0,
                NominalPenalty = 10f, RepPenaltySource = ReputationPenaltySource.KerbalDeath,
            });

            string summary;
            Assert.False(Gate(rp, slot, origin, out summary));
            Assert.Equal(0, SupersedeCommit.SealCreditWalkCountForTesting);
            Assert.Contains("lossRows=2 sealingRows=0 notCounted=0 credit=none", GateLine());
        }

        // ---------- walk freshness -----------------------------------------

        // catches: the seal reading the Effective flag an earlier walk left on the shared row
        // (here a walk of a list that held an attempt's earlier copy of the milestone, which
        // never reached the ledger) instead of a walk of the ELS: a stale false would open a
        // slot that holds the timeline's real credit.
        [Fact]
        public void StaleNotCountedFlag_FromAnotherWalk_CannotUnsealACountedGain()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            GameAction counted = Milestone("act_ms", "rec_a", 120.0, 5000f);
            Ledger.AddAction(counted);

            var withAttempt = new List<GameAction>(Ledger.Actions)
            {
                Milestone("act_attempt", "rec_fork", 105.0, 5000f),
            };
            RecalculationEngine.Recalculate(withAttempt);
            Assert.False(counted.Effective);
            Assert.Equal(GameActionNotCountedReason.MilestoneAlreadyAchieved, counted.NotCountedReason);

            string summary;
            Assert.True(Gate(rp, slot, origin, out summary));
            Assert.Equal("MilestoneAchievement:act_ms", summary);
            // The fresh walk restores the row's own fields.
            Assert.False(counted.Effective);
        }

        [Fact]
        public void StaleCountedFlag_OnADuplicate_DoesNotSealIt()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            OpenTakeBackEra();
            Ledger.AddAction(Milestone("act_first", "rec_other", 110.0, 5000f, HolderSeq));
            GameAction dup = Milestone("act_dup", "rec_a", 120.0, 5000f, RepeatSeq);
            Ledger.AddAction(dup);
            dup.Effective = true;
            dup.NotCountedReason = GameActionNotCountedReason.None;

            string summary;
            Assert.False(Gate(rp, slot, origin, out summary));
        }

        // catches: the cached credit walk outliving the ledger it walked.
        [Fact]
        public void HolderRemovedFromTheLedger_TheSlotSealsAgain()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            OpenTakeBackEra();
            GameAction first = Milestone("act_first", "rec_other", 110.0, 5000f, HolderSeq);
            Ledger.AddAction(first);
            Ledger.AddAction(Milestone("act_dup", "rec_a", 120.0, 5000f, RepeatSeq));

            string summary;
            Assert.False(Gate(rp, slot, origin, out summary));
            Assert.False(Gate(rp, slot, origin, out summary));
            Assert.Equal(1, SupersedeCommit.SealCreditWalkCountForTesting);

            int index = -1;
            for (int i = 0; i < Ledger.Actions.Count; i++)
                if (ReferenceEquals(Ledger.Actions[i], first)) index = i;
            Ledger.RemoveActionAt(index);
            Assert.True(Gate(rp, slot, origin, out summary));
            Assert.Equal("MilestoneAchievement:act_dup", summary);
            Assert.Equal(2, SupersedeCommit.SealCreditWalkCountForTesting);
        }

        [Fact]
        public void TombstonedHolder_TheSlotSealsAgain()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            OpenTakeBackEra();
            Ledger.AddAction(Milestone("act_first", "rec_other", 110.0, 5000f, HolderSeq));
            Ledger.AddAction(Milestone("act_dup", "rec_a", 120.0, 5000f, RepeatSeq));

            string summary;
            Assert.False(Gate(rp, slot, origin, out summary));

            var scenario = ParsekScenario.Instance;
            scenario.LedgerTombstones.Add(new LedgerTombstone
            {
                TombstoneId = "tomb_1", ActionId = "act_first", RetiringRecordingId = "rec_x",
                UT = 110.0, CreatedRealTime = "x",
            });
            scenario.BumpTombstoneStateVersion();
            Assert.True(Gate(rp, slot, origin, out summary));
        }

        // catches: an unavailable walk silently opening slots.
        [Fact]
        public void WalkThatThrows_EverythingSeals_AndWarns()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            OpenTakeBackEra();
            Ledger.AddAction(Milestone("act_first", "rec_other", 110.0, 5000f, HolderSeq));
            Ledger.AddAction(Milestone("act_dup", "rec_a", 120.0, 5000f, RepeatSeq));
            SupersedeCommit.SealCreditWalkerForTesting = _ => throw new InvalidOperationException("boom");

            string summary;
            Assert.True(Gate(rp, slot, origin, out summary));
            Assert.Contains(logLines, l => l.Contains("[WARN][Supersede]")
                && l.Contains("Seal credit walk (section 2.12, 3.15 item 10) failed")
                && l.Contains("every candidate row seals"));
            Assert.Contains("credit=walk-failed", GateLine());
        }

        [Fact]
        public void NoWalkModules_EveryNonLossRowSeals_ThePresenceRule()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            RecalculationEngine.ClearModules();
            Ledger.AddAction(Milestone("act_first", "rec_other", 110.0, 5000f));
            Ledger.AddAction(Science("act_sci_first", "rec_other", 110.0, 5f));
            Ledger.AddAction(Science("act_sci", "rec_a", 120.0, 5f));
            Ledger.AddAction(Milestone("act_dup", "rec_a", 120.0, 5000f));

            string summary;
            Assert.True(Gate(rp, slot, origin, out summary));
            Assert.Contains("sealingRows=2 notCounted=0", GateLine());
        }

        // ---------- rows a commit is about to file ----------------------------

        [Fact]
        public void PendingCommitRows_AreWalkedWithTheLedger()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            OpenTakeBackEra();
            Ledger.AddAction(Milestone("act_dup", "rec_a", 120.0, 5000f, RepeatSeq));
            string summary;
            Assert.True(Gate(rp, slot, origin, out summary));

            // A commit about to file an earlier credit of the same milestone.
            var pending = new List<GameAction> { Milestone("act_pending_first", "rec_other", 110.0, 5000f, HolderSeq) };
            using (SupersedeCommit.UsePendingCommitRows(pending))
            {
                Assert.False(Gate(rp, slot, origin, out summary));
            }
            Assert.Contains(logLines, l => l.Contains("Seal credit walk (section 2.12, 3.15 item 10)")
                && l.Contains("extra=1") && l.Contains("source=els+extra"));
            // The scope's walk is gone with it: the ledger alone seals again.
            Assert.True(Gate(rp, slot, origin, out summary));
        }

        // catches: a tree commit walking the ledger once per promoted slot.
        [Fact]
        public void PendingCommitRows_OneWalkServesEveryGateReadInTheScope()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            OpenTakeBackEra();
            Ledger.AddAction(Milestone("act_dup", "rec_a", 120.0, 5000f, RepeatSeq));
            var pending = new List<GameAction> { Milestone("act_pending_first", "rec_other", 110.0, 5000f, HolderSeq) };

            string summary;
            using (SupersedeCommit.UsePendingCommitRows(pending))
            {
                Assert.False(Gate(rp, slot, origin, out summary));
                int walks = SupersedeCommit.SealCreditWalkCountForTesting;
                Assert.False(Gate(rp, slot, origin, out summary));
                Assert.False(Gate(rp, slot, origin, out summary));
                Assert.Equal(walks, SupersedeCommit.SealCreditWalkCountForTesting);
                string source;
                Assert.True(SupersedeCommit.ResolveReplayNotCounted(pending, out source).ContainsKey("act_dup"));
                Assert.Equal("cached+extra", source);
                Assert.Equal(walks, SupersedeCommit.SealCreditWalkCountForTesting);
            }

            // A new scope over another list walks again.
            int before = SupersedeCommit.SealCreditWalkCountForTesting;
            var other = new List<GameAction>(pending);
            using (SupersedeCommit.UsePendingCommitRows(other))
            {
                Assert.False(Gate(rp, slot, origin, out summary));
            }
            Assert.Equal(before + 1, SupersedeCommit.SealCreditWalkCountForTesting);
        }

        // ---------- the merge's Rule 1 re-check (Rule 5) --------------------

        private static ReFlyMergeDominance.Inputs DominanceInputs(List<GameAction> els, List<GameAction> retire)
        {
            return new ReFlyMergeDominance.Inputs
            {
                SessionId = "sess_seal",
                Els = els,
                AttemptRecordingIds = new HashSet<string> { "rec_fork" },
                NewRows = new List<GameAction>(),
                RetireSet = retire,
                RewindUT = RpUT,
                RespawnSecondsForRecording = _ => double.NaN,
            };
        }

        [Fact]
        public void Dominance_RetireSetDuplicate_PassesRuleOne_AndCountsIt()
        {
            OpenTakeBackEra();
            var seed = new GameAction { ActionId = "seed_f", UT = 0, Type = GameActionType.FundsInitial, InitialFunds = 1000f };
            var first = Milestone("act_first", "rec_other", 110.0, 5000f, HolderSeq);
            var dup = Milestone("act_dup", "rec_a", 120.0, 5000f, RepeatSeq);
            var els = new List<GameAction> { seed, first, dup };

            ReFlyMergeDominanceVerdict v = ReFlyMergeDominance.Evaluate(
                DominanceInputs(els, new List<GameAction> { dup }));

            Assert.NotEqual("sealing", v.Pool);
            Assert.True(v.Pass);
            Assert.Equal(1, v.RetireSetNotCountedCount);
            Assert.Equal(1, v.RetireSetCount);
        }

        [Fact]
        public void Dominance_RetireSetCountedZeroAwardMilestone_RefusesAsSealing()
        {
            var seed = new GameAction { ActionId = "seed_f", UT = 0, Type = GameActionType.FundsInitial, InitialFunds = 1000f };
            var ms = Milestone("act_ms", "rec_a", 120.0, 0f);
            var els = new List<GameAction> { seed, ms };

            ReFlyMergeDominanceVerdict v = ReFlyMergeDominance.Evaluate(
                DominanceInputs(els, new List<GameAction> { ms }));

            Assert.False(v.Pass);
            Assert.Equal("sealing", v.Pool);
            Assert.Equal("MilestoneAchievement:act_ms", v.SealingRow);
        }

        [Fact]
        public void Dominance_StaleFlagOnTheRetiredRow_IsNotRead()
        {
            var seed = new GameAction { ActionId = "seed_f", UT = 0, Type = GameActionType.FundsInitial, InitialFunds = 1000f };
            var ms = Milestone("act_ms", "rec_a", 120.0, 5000f);
            ms.Effective = false;
            ms.NotCountedReason = GameActionNotCountedReason.MilestoneAlreadyAchieved;

            ReFlyMergeDominanceVerdict v = ReFlyMergeDominance.Evaluate(
                DominanceInputs(new List<GameAction> { seed, ms }, new List<GameAction> { ms }));

            Assert.Equal("sealing", v.Pool);
        }

        [Fact]
        public void Dominance_RetireRowNotYetFiled_IsJudgedWithTheLedger()
        {
            OpenTakeBackEra();
            var seed = new GameAction { ActionId = "seed_f", UT = 0, Type = GameActionType.FundsInitial, InitialFunds = 1000f };
            var first = Milestone("act_first", "rec_other", 110.0, 5000f, HolderSeq);
            var unfiledDup = Milestone("act_dup", "rec_a", 120.0, 5000f, RepeatSeq);

            bool isBaseline;
            List<GameAction> rows = ReFlyMergeDominance.ComposeCreditRows(
                new List<GameAction> { seed, first }, new List<GameAction> { unfiledDup }, out isBaseline);
            Assert.False(isBaseline);
            Assert.Equal(3, rows.Count);

            ReFlyMergeDominanceVerdict v = ReFlyMergeDominance.Evaluate(
                DominanceInputs(new List<GameAction> { seed, first }, new List<GameAction> { unfiledDup }));
            Assert.NotEqual("sealing", v.Pool);
            Assert.Equal(1, v.RetireSetNotCountedCount);
        }

        [Fact]
        public void Dominance_VerdictLine_NamesTheNotCountedCount()
        {
            var v = new ReFlyMergeDominanceVerdict
            {
                Applicable = true, Pass = true, SessionId = "sess_seal", Pool = "funds", UT = 120.0,
                RetireSetCount = 1, RetireSetNotCountedCount = 1,
            };
            ReFlyMergeDominance.LogVerdict(v, "click", 2);
            Assert.Contains(logLines, l => l.Contains("[INFO][LedgerSwap] Merge dominance check sess=sess_seal result=pass ")
                && l.Contains("retireSet=1 closure=2 notCounted=1"));
        }

        // ---------- the pure predicate --------------------------------------

        // catches: a new not-counted reason silently opening slots before it is reviewed, and
        // a reviewed reason with no counted holder (a deadline expiry, an intact building)
        // added back to the list.
        [Fact]
        public void EveryNotCountedReason_IsReviewed()
        {
            // true: the reason names a counted holder row the timeline keeps.
            var reviewed = new Dictionary<GameActionNotCountedReason, bool>
            {
                { GameActionNotCountedReason.None, false },
                { GameActionNotCountedReason.ContractAlreadyCompleted, true },
                { GameActionNotCountedReason.ContractDeadlinePassed, false },
                { GameActionNotCountedReason.ContractAlreadyResolved, true },
                { GameActionNotCountedReason.ContractOutcomeAfterEnd, true },
                { GameActionNotCountedReason.MilestoneAlreadyAchieved, true },
                { GameActionNotCountedReason.FacilityAlreadyIntact, false },
                { GameActionNotCountedReason.ContractParameterAfterCompletion, true },
                { GameActionNotCountedReason.RecordThresholdsAlreadyCredited, true },
                { GameActionNotCountedReason.PurchaseAlreadyOwned, true },
                { GameActionNotCountedReason.ConversionCauseNotCounted, true },
                { GameActionNotCountedReason.SurveyBodyAlreadyCredited, true },
                { GameActionNotCountedReason.ProducerSliceAlreadyCredited, true },
            };
            foreach (GameActionNotCountedReason r in Enum.GetValues(typeof(GameActionNotCountedReason)))
            {
                bool hasHolder;
                Assert.True(reviewed.TryGetValue(r, out hasHolder),
                    "GameActionNotCountedReason." + r + " is not reviewed for the Re-Fly seal (3.15 item 10)");
                Assert.Equal(hasHolder, ReplayCredit.IsNotCountedReason(r));
            }
        }

        [Fact]
        public void NotCountedLabel_ReadsTheFieldsAWalkJustLeft()
        {
            // A prevented repeat (no captured amount).
            var ms = new GameAction { Type = GameActionType.MilestoneAchievement, Effective = false,
                NotCountedReason = GameActionNotCountedReason.MilestoneAlreadyAchieved };
            Assert.Equal("MilestoneAlreadyAchieved", ReplayCredit.NotCountedLabel(ms, true));
            // A paid repeat: out only while the walk takes the payment back.
            ms.MilestoneFundsAwarded = 5000f;
            Assert.Null(ReplayCredit.NotCountedLabel(ms, true));
            ms.TakeBackFunds = 5000f;
            Assert.Equal("MilestoneAlreadyAchieved", ReplayCredit.NotCountedLabel(ms, true));

            // A deadline expiry has no holder: it seals even with nothing paid.
            var late = new GameAction { Type = GameActionType.ContractComplete, Effective = false,
                NotCountedReason = GameActionNotCountedReason.ContractDeadlinePassed };
            Assert.Null(ReplayCredit.NotCountedLabel(late, true));

            // A purchase already owned: a kept charge is the player's loss.
            var owned = new GameAction { Type = GameActionType.FundsSpending, Effective = false, FundsSpent = 800f,
                NotCountedReason = GameActionNotCountedReason.PurchaseAlreadyOwned };
            Assert.Equal("PurchaseAlreadyOwned", ReplayCredit.NotCountedLabel(owned, true));

            // Effective false with no reviewed reason counts (the safe side).
            var unexplained = new GameAction { Type = GameActionType.FundsEarning, Effective = false };
            Assert.Null(ReplayCredit.NotCountedLabel(unexplained, true));

            var capped = new GameAction { Type = GameActionType.ScienceEarning, ScienceAwarded = 2f, EffectiveScience = 0f };
            // Stock paid the capped award and nothing takes it back.
            Assert.Null(ReplayCredit.NotCountedLabel(capped, true));
            capped.TakeBackScience = 2f;
            Assert.Equal(ReplayCredit.SubjectCapReachedLabel, ReplayCredit.NotCountedLabel(capped, true));
            // Without a science module in the walk the zeroed credit proves nothing.
            Assert.Null(ReplayCredit.NotCountedLabel(capped, false));

            var partly = new GameAction { Type = GameActionType.ScienceEarning, ScienceAwarded = 2f, EffectiveScience = 0.5f };
            Assert.Null(ReplayCredit.NotCountedLabel(partly, true));
            var zeroAward = new GameAction { Type = GameActionType.ScienceEarning, ScienceAwarded = 0f, EffectiveScience = 0f };
            Assert.Null(ReplayCredit.NotCountedLabel(zeroAward, true));
            var zeroMilestone = new GameAction { Type = GameActionType.MilestoneAchievement };
            Assert.Null(ReplayCredit.NotCountedLabel(zeroMilestone, true));
        }

        [Fact]
        public void SelectSealingRows_NullVerdict_IsThePresenceRule()
        {
            var ms = Milestone("act_ms", "rec_a", 120.0, 5000f);
            var death = Death("act_death", "rec_a", 120.0);
            var retire = new List<GameAction> { ms, death };
            var notCountedRows = new List<GameAction>();

            Assert.Equal(new[] { ms }, SupersedeCommit.SelectSealingRows(retire, null, notCountedRows));
            Assert.Empty(notCountedRows);

            var verdict = new Dictionary<string, string> { { "act_ms", "MilestoneAlreadyAchieved" } };
            Assert.Empty(SupersedeCommit.SelectSealingRows(retire, verdict, notCountedRows));
            Assert.Equal(new[] { ms }, notCountedRows);
        }
    }
}
