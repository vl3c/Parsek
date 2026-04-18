using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 9 of Rewind-to-Staging (design §6.6 step 4 / §7.13 / §7.14 /
    /// §7.15 / §7.16 / §7.41 / §7.44): v1 narrow-scope eligibility matrix.
    ///
    /// <para>
    /// Only <see cref="GameActionType.KerbalAssignment"/>+Dead actions and
    /// <see cref="GameActionType.ReputationPenalty"/> actions paired with one
    /// of those within a 1s UT window are tombstone-eligible. Everything else
    /// — contracts, milestones, facility upgrades, strategies, tech research,
    /// science spending, funds spending, vessel-destruction rep — stays in ELS.
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
    }
}
