using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 9 of Rewind-to-Staging tombstone eligibility matrix.
    ///
    /// <para>
    /// <see cref="TombstoneEligibility.IsEligible"/> remains the legacy
    /// death-cleanup helper used by retry/autoseal classifiers. Merge tombstoning
    /// uses <see cref="TombstoneEligibility.IsSupersedeTombstoneEligible"/> to
    /// retire all non-seed recording-scoped career actions from the superseded
    /// subtree, while preserving null-scoped rows and already-paid rollout costs.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class TombstoneEligibilityTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorSuppress;

        public TombstoneEligibilityTests()
        {
            priorSuppress = ParsekLog.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorSuppress;
        }

        // ---------- Helpers -------------------------------------------------

        private static GameAction Kerbal(
            string recordingId, KerbalEndState endState, double ut = 100.0,
            string kerbalName = "Jeb")
        {
            return new GameAction
            {
                Type = GameActionType.KerbalAssignment,
                RecordingId = recordingId,
                KerbalName = kerbalName,
                KerbalEndStateField = endState,
                UT = ut,
            };
        }

        private static GameAction Rep(
            string recordingId, ReputationPenaltySource source,
            double ut = 100.0, float penalty = 10f)
        {
            return new GameAction
            {
                Type = GameActionType.ReputationPenalty,
                RecordingId = recordingId,
                RepPenaltySource = source,
                NominalPenalty = penalty,
                UT = ut,
            };
        }

        // ---------- Direct eligibility --------------------------------------

        [Fact]
        public void KerbalDeath_Eligible()
        {
            var action = Kerbal("rec_1", KerbalEndState.Dead);
            Assert.True(TombstoneEligibility.IsEligible(action));
        }

        [Fact]
        public void KerbalAssignment_NotDeadState_NotEligible()
        {
            // Recovered, Aboard, Unknown — only Dead matters.
            Assert.False(TombstoneEligibility.IsEligible(Kerbal("rec_1", KerbalEndState.Recovered)));
            Assert.False(TombstoneEligibility.IsEligible(Kerbal("rec_1", KerbalEndState.Aboard)));
            Assert.False(TombstoneEligibility.IsEligible(Kerbal("rec_1", KerbalEndState.Unknown)));
        }

        [Fact]
        public void NullScopedAction_NotEligible()
        {
            // §7.41: actions with null RecordingId are never tombstone-eligible.
            var action = Kerbal(null, KerbalEndState.Dead);
            Assert.False(TombstoneEligibility.IsEligible(action));
        }

        [Fact]
        public void NullAction_NotEligible()
        {
            Assert.False(TombstoneEligibility.IsEligible(null));
        }

        // ---------- Type-ineligible matrix (§7.13-§7.15, §7.44) -------------

        [Fact]
        public void ContractAccept_NotEligible()
        {
            var action = new GameAction
            {
                Type = GameActionType.ContractAccept,
                RecordingId = "rec_1",
                ContractId = "c_1",
            };
            Assert.False(TombstoneEligibility.IsEligible(action));
        }

        [Fact]
        public void ContractComplete_NotEligible()
        {
            // §7.13: Contract completes survive supersede (sticky career state).
            var action = new GameAction
            {
                Type = GameActionType.ContractComplete,
                RecordingId = "rec_1",
                ContractId = "c_1",
            };
            Assert.False(TombstoneEligibility.IsEligible(action));
        }

        [Fact]
        public void ContractFail_NotEligible()
        {
            // §7.14: BG-crash contract fail is NOT un-failed by re-fly merge.
            var action = new GameAction
            {
                Type = GameActionType.ContractFail,
                RecordingId = "rec_1",
                ContractId = "c_1",
            };
            Assert.False(TombstoneEligibility.IsEligible(action));
        }

        [Fact]
        public void MilestoneAchievement_NotEligible()
        {
            // §7.15: first-time flag is KSP-owned and sticky.
            var action = new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                RecordingId = "rec_1",
                MilestoneId = "FirstOrbitKerbin",
            };
            Assert.False(TombstoneEligibility.IsEligible(action));
        }

        [Fact]
        public void FacilityUpgrade_NotEligible()
        {
            var action = new GameAction
            {
                Type = GameActionType.FacilityUpgrade,
                RecordingId = "rec_1",
                FacilityId = "LaunchPad",
                ToLevel = 2,
            };
            Assert.False(TombstoneEligibility.IsEligible(action));
        }

        [Fact]
        public void StrategyActivate_NotEligible()
        {
            var action = new GameAction
            {
                Type = GameActionType.StrategyActivate,
                RecordingId = "rec_1",
                StrategyId = "UnpaidResearch",
            };
            Assert.False(TombstoneEligibility.IsEligible(action));
        }

        [Fact]
        public void TechResearched_NotEligible()
        {
            // ScienceSpending is how tech research surfaces in the ledger.
            var action = new GameAction
            {
                Type = GameActionType.ScienceSpending,
                RecordingId = "rec_1",
                NodeId = "survivability",
                Cost = 45f,
            };
            Assert.False(TombstoneEligibility.IsEligible(action));
        }

        [Fact]
        public void ScienceEarning_NotEligible()
        {
            var action = new GameAction
            {
                Type = GameActionType.ScienceEarning,
                RecordingId = "rec_1",
                SubjectId = "crewReport@MunSrfLandedMidlands",
                ScienceAwarded = 12f,
            };
            Assert.False(TombstoneEligibility.IsEligible(action));
        }

        [Fact]
        public void VesselDestructionRep_NotEligible()
        {
            // §7.44: vessel-destruction rep penalty (no paired KerbalDeath)
            // stays in ELS under v1.
            var action = Rep("rec_1", ReputationPenaltySource.Other);
            Assert.False(TombstoneEligibility.IsEligible(action));
        }

        [Fact]
        public void OnlyKerbalDeathAndBundledRep_Eligible_DirectCase()
        {
            // Matrix check: the ONLY type reporting eligible via IsEligible is
            // KerbalAssignment+Dead. All other types (including
            // ReputationPenalty, which requires pairing) return false here.
            var types = new[]
            {
                GameActionType.ScienceEarning, GameActionType.ScienceSpending,
                GameActionType.FundsEarning, GameActionType.FundsSpending,
                GameActionType.MilestoneAchievement, GameActionType.ContractAccept,
                GameActionType.ContractComplete, GameActionType.ContractFail,
                GameActionType.ContractCancel, GameActionType.ReputationEarning,
                GameActionType.ReputationPenalty, GameActionType.KerbalHire,
                GameActionType.KerbalRescue, GameActionType.KerbalStandIn,
                GameActionType.FacilityUpgrade, GameActionType.FacilityDestruction,
                GameActionType.FacilityRepair, GameActionType.StrategyActivate,
                GameActionType.StrategyDeactivate, GameActionType.FundsInitial,
                GameActionType.ScienceInitial, GameActionType.ReputationInitial,
            };
            foreach (var t in types)
            {
                var a = new GameAction { Type = t, RecordingId = "rec_1" };
                Assert.False(TombstoneEligibility.IsEligible(a),
                    $"Type {t} must not be eligible via IsEligible; got true");
            }

            // The one eligible type — KerbalAssignment+Dead.
            Assert.True(TombstoneEligibility.IsEligible(Kerbal("rec_1", KerbalEndState.Dead)));
        }

        // ---------- Paired rep-penalty bundling ----------------------------

        [Fact]
        public void RepPenalty_PairedWithKerbalDeath_Eligible()
        {
            var death = Kerbal("rec_1", KerbalEndState.Dead, ut: 100.0);
            var rep = Rep("rec_1", ReputationPenaltySource.KerbalDeath, ut: 100.2);
            var slice = new List<GameAction> { death, rep };

            GameAction paired;
            Assert.True(TombstoneEligibility.TryPairBundledRepPenalty(rep, slice, out paired));
            Assert.Same(death, paired);
        }

        [Fact]
        public void RepPenalty_DifferentRecording_NotPaired()
        {
            // Bundling requires same RecordingId.
            var death = Kerbal("rec_1", KerbalEndState.Dead, ut: 100.0);
            var rep = Rep("rec_2", ReputationPenaltySource.KerbalDeath, ut: 100.0);
            var slice = new List<GameAction> { rep }; // slice is the same-recording slice; death isn't in rec_2's slice.

            GameAction paired;
            Assert.False(TombstoneEligibility.TryPairBundledRepPenalty(rep, slice, out paired));
            Assert.Null(paired);
        }

        [Fact]
        public void RepPenalty_UTTooFar_NotPaired()
        {
            var death = Kerbal("rec_1", KerbalEndState.Dead, ut: 100.0);
            var rep = Rep("rec_1", ReputationPenaltySource.KerbalDeath, ut: 105.0);
            var slice = new List<GameAction> { death, rep };

            GameAction paired;
            Assert.False(TombstoneEligibility.TryPairBundledRepPenalty(rep, slice, out paired));
            Assert.Null(paired);
        }

        [Fact]
        public void RepPenalty_WithoutDeathInSlice_NotPaired()
        {
            // §7.44 specifically: vessel-destruction rep with no kerbal-death
            // sibling on the same recording is NOT bundled.
            var rep = Rep("rec_1", ReputationPenaltySource.Other, ut: 100.0);
            var recovered = Kerbal("rec_1", KerbalEndState.Recovered, ut: 100.1);
            var slice = new List<GameAction> { recovered, rep };

            GameAction paired;
            Assert.False(TombstoneEligibility.TryPairBundledRepPenalty(rep, slice, out paired));
            Assert.Null(paired);
        }

        [Fact]
        public void RepPenalty_NullRecordingId_NotPaired()
        {
            // §7.41: null-scoped rep penalty never bundles.
            var death = Kerbal("rec_1", KerbalEndState.Dead, ut: 100.0);
            var rep = Rep(null, ReputationPenaltySource.KerbalDeath, ut: 100.0);
            var slice = new List<GameAction> { death, rep };

            GameAction paired;
            Assert.False(TombstoneEligibility.TryPairBundledRepPenalty(rep, slice, out paired));
            Assert.Null(paired);
        }

        [Fact]
        public void RepPenalty_NonRepType_NotPaired()
        {
            // Non-ReputationPenalty action passed to the pair helper returns false.
            var death = Kerbal("rec_1", KerbalEndState.Dead, ut: 100.0);
            var slice = new List<GameAction> { death };

            GameAction paired;
            Assert.False(TombstoneEligibility.TryPairBundledRepPenalty(death, slice, out paired));
            Assert.Null(paired);
        }

        [Fact]
        public void RepPenalty_EmptySlice_NotPaired()
        {
            var rep = Rep("rec_1", ReputationPenaltySource.KerbalDeath, ut: 100.0);
            GameAction paired;
            Assert.False(TombstoneEligibility.TryPairBundledRepPenalty(rep, new List<GameAction>(), out paired));
            Assert.Null(paired);
            Assert.False(TombstoneEligibility.TryPairBundledRepPenalty(rep, null, out paired));
            Assert.Null(paired);
        }

        [Fact]
        public void RepPenalty_PairedWithDeathAtExactUTBoundary_Eligible()
        {
            var death = Kerbal("rec_1", KerbalEndState.Dead, ut: 100.0);
            // Exactly 1s away — inclusive boundary is paired.
            var rep = Rep("rec_1", ReputationPenaltySource.KerbalDeath, ut: 101.0);
            var slice = new List<GameAction> { death, rep };

            GameAction paired;
            Assert.True(TombstoneEligibility.TryPairBundledRepPenalty(rep, slice, out paired));
            Assert.Same(death, paired);
        }

        [Fact]
        public void OnlyKerbalDeathAndBundledRep_Eligible_BundledCase()
        {
            // Matrix check via pairing: only ReputationPenalty reaches eligible
            // via TryPairBundledRepPenalty. Every other type returns false.
            var death = Kerbal("rec_1", KerbalEndState.Dead, ut: 100.0);
            var rep = Rep("rec_1", ReputationPenaltySource.KerbalDeath, ut: 100.1);
            var slice = new List<GameAction> { death, rep };

            GameAction paired;
            Assert.True(TombstoneEligibility.TryPairBundledRepPenalty(rep, slice, out paired));

            // Matrix: any non-ReputationPenalty candidate returns false even
            // when a paired death would exist.
            var types = new[]
            {
                GameActionType.ScienceEarning, GameActionType.ScienceSpending,
                GameActionType.FundsEarning, GameActionType.FundsSpending,
                GameActionType.MilestoneAchievement, GameActionType.ContractAccept,
                GameActionType.ContractComplete, GameActionType.ContractFail,
                GameActionType.ContractCancel, GameActionType.ReputationEarning,
                GameActionType.KerbalHire, GameActionType.KerbalRescue,
                GameActionType.FacilityUpgrade, GameActionType.FacilityDestruction,
                GameActionType.FacilityRepair, GameActionType.StrategyActivate,
                GameActionType.StrategyDeactivate,
            };
            foreach (var t in types)
            {
                var a = new GameAction { Type = t, RecordingId = "rec_1", UT = 100.1 };
                Assert.False(
                    TombstoneEligibility.TryPairBundledRepPenalty(a, slice, out paired),
                    $"Type {t} must not pair via TryPairBundledRepPenalty; got true");
            }
        }

        [Fact]
        public void SupersedeTombstoneEligibility_CareerActionsEligible()
        {
            var actions = new[]
            {
                new GameAction { Type = GameActionType.ScienceEarning, RecordingId = "rec_1" },
                new GameAction { Type = GameActionType.ScienceSpending, RecordingId = "rec_1" },
                new GameAction { Type = GameActionType.FundsEarning, RecordingId = "rec_1" },
                new GameAction { Type = GameActionType.FundsSpending, RecordingId = "rec_1", FundsSpendingSource = FundsSpendingSource.Other },
                new GameAction { Type = GameActionType.MilestoneAchievement, RecordingId = "rec_1" },
                new GameAction { Type = GameActionType.ContractAccept, RecordingId = "rec_1" },
                new GameAction { Type = GameActionType.ContractComplete, RecordingId = "rec_1" },
                new GameAction { Type = GameActionType.ContractFail, RecordingId = "rec_1" },
                new GameAction { Type = GameActionType.ContractCancel, RecordingId = "rec_1" },
                new GameAction { Type = GameActionType.ReputationEarning, RecordingId = "rec_1" },
                new GameAction { Type = GameActionType.ReputationPenalty, RecordingId = "rec_1" },
                new GameAction { Type = GameActionType.KerbalAssignment, RecordingId = "rec_1", KerbalEndStateField = KerbalEndState.Aboard },
                new GameAction { Type = GameActionType.KerbalHire, RecordingId = "rec_1" },
                new GameAction { Type = GameActionType.KerbalRescue, RecordingId = "rec_1" },
                new GameAction { Type = GameActionType.KerbalStandIn, RecordingId = "rec_1" },
                new GameAction { Type = GameActionType.FacilityUpgrade, RecordingId = "rec_1" },
                new GameAction { Type = GameActionType.FacilityDestruction, RecordingId = "rec_1" },
                new GameAction { Type = GameActionType.FacilityRepair, RecordingId = "rec_1" },
                new GameAction { Type = GameActionType.StrategyActivate, RecordingId = "rec_1" },
                new GameAction { Type = GameActionType.StrategyDeactivate, RecordingId = "rec_1" },
            };

            foreach (var action in actions)
            {
                Assert.True(TombstoneEligibility.IsSupersedeTombstoneEligible(action),
                    $"Type {action.Type} should be merge-tombstone eligible");
            }
        }

        [Fact]
        public void SupersedeTombstoneEligibility_PreservesSeedsNullScopeAndRollout()
        {
            Assert.False(TombstoneEligibility.IsSupersedeTombstoneEligible(null));
            Assert.False(TombstoneEligibility.IsSupersedeTombstoneEligible(
                new GameAction { Type = GameActionType.ScienceEarning, RecordingId = null }));
            Assert.False(TombstoneEligibility.IsSupersedeTombstoneEligible(
                new GameAction { Type = GameActionType.FundsInitial, RecordingId = "rec_1" }));
            Assert.False(TombstoneEligibility.IsSupersedeTombstoneEligible(
                new GameAction { Type = GameActionType.ScienceInitial, RecordingId = "rec_1" }));
            Assert.False(TombstoneEligibility.IsSupersedeTombstoneEligible(
                new GameAction { Type = GameActionType.ReputationInitial, RecordingId = "rec_1" }));
            Assert.False(TombstoneEligibility.IsSupersedeTombstoneEligible(
                new GameAction
                {
                    Type = GameActionType.FundsSpending,
                    RecordingId = "rec_1",
                    FundsSpendingSource = FundsSpendingSource.VesselBuild,
                }));
        }

        [Fact]
        public void SupersedeTombstoneEligibility_UnknownFutureType_PreservedUntilReviewed()
        {
            Assert.False(TombstoneEligibility.IsSupersedeTombstoneEligible(
                new GameAction { Type = (GameActionType)999, RecordingId = "rec_1" }));
        }
    }
}
