using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Per-case suite for <see cref="PostWalkActionReconciler"/>.
    ///
    /// The pre-existing net (EarningsReconciliationTests) drives the reconciler
    /// through the LedgerOrchestrator facade and mostly asserts WARN-line
    /// PRESENCE. This suite asserts the RECONCILED VALUES instead: for every
    /// action type the classifier routes, the per-leg Applies / Expected /
    /// ReasonKey / EventType triple that the compare stage consumes, plus the
    /// match, mismatch and idempotence behaviour at the reconcile stage.
    ///
    /// The rep-source mapping cell is the load-bearing one: stock KSP keys the
    /// crew-death reputation penalty under TransactionReasons.VesselLoss, not
    /// under a "CrewKilled" key (no such TransactionReasons member exists).
    /// Measured on the CL-1-pod-impact flights:
    ///   "Added -9.999828 (-10) reputation: 'VesselLoss'."
    /// See docs/dev/autotest-status.md (CL-1-pod-impact row) and
    /// harness/scenarios/CL-1-pod-impact.toml.
    /// </summary>
    [Collection("Sequential")]
    public class PostWalkActionReconcilerTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public PostWalkActionReconcilerTests()
        {
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            GameStateStore.SuppressLogging = true;
            LedgerOrchestrator.SetResourceTrackingAvailabilityForTesting(true, true, true);
        }

        public void Dispose()
        {
            LedgerOrchestrator.SetResourceTrackingAvailabilityForTesting(null, null, null);
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------

        private static GameStateEvent RepEvent(double ut, double before, double after, string reason)
        {
            return new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.ReputationChanged,
                key = reason,
                valueBefore = before,
                valueAfter = after,
                recordingId = ""
            };
        }

        private static GameStateEvent FundsEvent(double ut, double before, double after, string reason)
        {
            return new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.FundsChanged,
                key = reason,
                valueBefore = before,
                valueAfter = after,
                recordingId = ""
            };
        }

        private static GameStateEvent ScienceEvent(double ut, double before, double after, string reason)
        {
            return new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.ScienceChanged,
                key = reason,
                valueBefore = before,
                valueAfter = after,
                recordingId = ""
            };
        }

        /// <summary>The crew-death penalty row the ledger would carry for a CL-1-shaped death.</summary>
        private static GameAction KerbalDeathPenalty(double ut = 500, float effectiveRep = -10f)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ReputationPenalty,
                NominalPenalty = 10f,
                RepPenaltySource = ReputationPenaltySource.KerbalDeath,
                EffectiveRep = effectiveRep
            };
        }

        private static void AssertLeg(
            PostWalkActionReconciler.PostWalkLeg leg,
            double expected,
            string reasonKey,
            GameStateEventType eventType)
        {
            Assert.True(leg.Applies);
            Assert.Equal(expected, leg.Expected, 4);
            Assert.Equal(reasonKey, leg.ReasonKey);
            Assert.Equal(eventType, leg.EventType);
        }

        private static void AssertNoLeg(PostWalkActionReconciler.PostWalkLeg leg)
        {
            Assert.False(leg.Applies);
        }

        private static string SummaryLine(IEnumerable<string> lines)
        {
            string found = null;
            foreach (var l in lines)
            {
                if (l.Contains("Post-walk reconcile: actions="))
                    found = l;
            }
            return found;
        }

        private static int SummaryCount(IEnumerable<string> lines)
        {
            int n = 0;
            foreach (var l in lines)
            {
                if (l.Contains("Post-walk reconcile: actions="))
                    n++;
            }
            return n;
        }

        // ==================================================================
        // FIRST ASSERTION -- the ReputationPenaltySource.KerbalDeath mapping.
        //
        // Direction of truth is a MEASUREMENT, not an opinion: the archived
        // CL-1-pod-impact flights both logged the stock crew-death penalty as
        // "Added -9.999828 (-10) reputation: 'VesselLoss'." and stock
        // TransactionReasons has no CrewKilled member at all (the only
        // "CrewKilled" symbol in Assembly-CSharp is GameEvents.onCrewKilled).
        // A "CrewKilled" reason key can therefore never appear on a
        // ReputationChanged event, so the old mapping guaranteed a permanent
        // false "no matching ReputationChanged event" WARN on every crew death.
        // ==================================================================

        [Fact]
        public void ClassifyPostWalk_ReputationPenalty_KerbalDeath_MapsToStockVesselLossKey()
        {
            var exp = PostWalkActionReconciler.ClassifyPostWalk(KerbalDeathPenalty());

            Assert.True(exp.Reconcile);
            AssertLeg(exp.Rep, -10.0, "VesselLoss", GameStateEventType.ReputationChanged);
            AssertNoLeg(exp.Funds);
            AssertNoLeg(exp.Sci);
        }

        [Fact]
        public void ReconcilePostWalk_KerbalDeath_PairsWithStockVesselLossEvent_NoWarn()
        {
            // The CL-1 numbers: stock credits -9.999828 for a nominal -10 penalty.
            var events = new List<GameStateEvent>
            {
                RepEvent(500, 2.0, 2.0 - 9.999828, "VesselLoss")
            };
            var actions = new List<GameAction> { KerbalDeathPenalty(effectiveRep: -10f) };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk,"));
            Assert.Contains("matches=1", SummaryLine(logLines));
            Assert.Contains("mismatches(funds/rep/sci)=0/0/0", SummaryLine(logLines));
        }

        [Fact]
        public void ReconcilePostWalk_KerbalDeath_WithNoStockEvent_WarnsNamingVesselLoss()
        {
            var actions = new List<GameAction> { KerbalDeathPenalty(effectiveRep: -10f) };

            LedgerOrchestrator.ReconcilePostWalk(new List<GameStateEvent>(), actions, utCutoff: null);

            Assert.Contains(logLines, l =>
                l.Contains("Earnings reconciliation (post-walk, rep)") &&
                l.Contains("ReputationPenalty") &&
                l.Contains("keyed 'VesselLoss'") &&
                l.Contains("expected=-10.0"));
        }

        // ==================================================================
        // ReputationPenalty -- remaining sources
        // ==================================================================

        [Theory]
        [InlineData(ReputationPenaltySource.ContractFail, "ContractPenalty")]
        [InlineData(ReputationPenaltySource.ContractDecline, "ContractDecline")]
        [InlineData(ReputationPenaltySource.KerbalDeath, "VesselLoss")]
        public void ClassifyPostWalk_ReputationPenalty_MapsSourceToStockReasonKey(
            ReputationPenaltySource source, string expectedKey)
        {
            var action = new GameAction
            {
                UT = 900,
                Type = GameActionType.ReputationPenalty,
                RepPenaltySource = source,
                EffectiveRep = -4.25f
            };

            var exp = PostWalkActionReconciler.ClassifyPostWalk(action);

            Assert.True(exp.Reconcile);
            AssertLeg(exp.Rep, -4.25, expectedKey, GameStateEventType.ReputationChanged);
        }

        [Theory]
        [InlineData(ReputationPenaltySource.Strategy)]
        [InlineData(ReputationPenaltySource.Other)]
        public void ClassifyPostWalk_ReputationPenalty_DirectlyCapturedSources_NotReconciled(
            ReputationPenaltySource source)
        {
            var action = new GameAction
            {
                UT = 900,
                Type = GameActionType.ReputationPenalty,
                RepPenaltySource = source,
                EffectiveRep = -4.25f
            };

            var exp = PostWalkActionReconciler.ClassifyPostWalk(action);

            Assert.False(exp.Reconcile);
            AssertNoLeg(exp.Rep);
        }

        // ==================================================================
        // ContractComplete
        // ==================================================================

        [Fact]
        public void ClassifyPostWalk_ContractComplete_AppliesAllThreeTransformedLegs()
        {
            var action = new GameAction
            {
                UT = 100,
                Type = GameActionType.ContractComplete,
                ContractId = "c_alpha",
                Effective = true,
                TransformedFundsReward = 12345.0f,
                EffectiveRep = 6.5f,
                TransformedScienceReward = 22.5f
            };

            var exp = PostWalkActionReconciler.ClassifyPostWalk(action);

            Assert.True(exp.Reconcile);
            AssertLeg(exp.Funds, 12345.0, "ContractReward", GameStateEventType.FundsChanged);
            AssertLeg(exp.Rep, 6.5, "ContractReward", GameStateEventType.ReputationChanged);
            AssertLeg(exp.Sci, 22.5, "ContractReward", GameStateEventType.ScienceChanged);
        }

        [Fact]
        public void ClassifyPostWalk_ContractComplete_Ineffective_MirrorsModuleSkip()
        {
            var action = new GameAction
            {
                UT = 100,
                Type = GameActionType.ContractComplete,
                ContractId = "c_alpha",
                Effective = false,
                TransformedFundsReward = 12345.0f,
                EffectiveRep = 6.5f
            };

            var exp = PostWalkActionReconciler.ClassifyPostWalk(action);

            Assert.False(exp.Reconcile);
            AssertNoLeg(exp.Funds);
            AssertNoLeg(exp.Rep);
            AssertNoLeg(exp.Sci);
        }

        // ==================================================================
        // ContractFail / ContractCancel
        // ==================================================================

        [Theory]
        [InlineData(GameActionType.ContractFail)]
        [InlineData(GameActionType.ContractCancel)]
        public void ClassifyPostWalk_ContractPenaltyTypes_NegateFundsPenaltyAndCarryEffectiveRep(
            GameActionType type)
        {
            var action = new GameAction
            {
                UT = 200,
                Type = type,
                ContractId = "c_beta",
                FundsPenalty = 5000f,
                EffectiveRep = -3.5f
            };

            var exp = PostWalkActionReconciler.ClassifyPostWalk(action);

            Assert.True(exp.Reconcile);
            // Sign flip is the contract: the ledger stores a positive penalty, the
            // observed FundsChanged delta is negative.
            AssertLeg(exp.Funds, -5000.0, "ContractPenalty", GameStateEventType.FundsChanged);
            AssertLeg(exp.Rep, -3.5, "ContractPenalty", GameStateEventType.ReputationChanged);
            AssertNoLeg(exp.Sci);
        }

        [Fact]
        public void ClassifyPostWalk_ContractFail_HasNoEffectiveGate()
        {
            // Penalties fire unconditionally in the modules, so Effective=false must
            // still reconcile (unlike ContractComplete).
            var action = new GameAction
            {
                UT = 200,
                Type = GameActionType.ContractFail,
                ContractId = "c_beta",
                Effective = false,
                FundsPenalty = 5000f,
                EffectiveRep = -3.5f
            };

            var exp = PostWalkActionReconciler.ClassifyPostWalk(action);

            Assert.True(exp.Reconcile);
            AssertLeg(exp.Funds, -5000.0, "ContractPenalty", GameStateEventType.FundsChanged);
        }

        // ==================================================================
        // MilestoneAchievement
        // ==================================================================

        [Fact]
        public void ClassifyPostWalk_MilestoneAchievement_UsesAwardedFieldsKeyedProgression()
        {
            var action = new GameAction
            {
                UT = 300,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = "RecordsAltitude",
                Effective = true,
                MilestoneFundsAwarded = 4800.0f,
                EffectiveRep = 0.0f,
                MilestoneScienceAwarded = 3.0f
            };

            var exp = PostWalkActionReconciler.ClassifyPostWalk(action);

            Assert.True(exp.Reconcile);
            // The milestone legs read the Milestone*Awarded fields, NOT the
            // Transformed* fields the contract path uses.
            AssertLeg(exp.Funds, 4800.0, "Progression", GameStateEventType.FundsChanged);
            AssertLeg(exp.Rep, 0.0, "Progression", GameStateEventType.ReputationChanged);
            AssertLeg(exp.Sci, 3.0, "Progression", GameStateEventType.ScienceChanged);
        }

        [Fact]
        public void ClassifyPostWalk_MilestoneAchievement_Ineffective_MirrorsDuplicateSkip()
        {
            var action = new GameAction
            {
                UT = 300,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = "RecordsAltitude",
                Effective = false,
                MilestoneFundsAwarded = 4800.0f
            };

            Assert.False(PostWalkActionReconciler.ClassifyPostWalk(action).Reconcile);
        }

        // ==================================================================
        // ReputationEarning
        // ==================================================================

        [Theory]
        [InlineData(ReputationSource.ContractComplete, "ContractReward")]
        [InlineData(ReputationSource.Milestone, "Progression")]
        public void ClassifyPostWalk_ReputationEarning_MapsSourceToStockReasonKey(
            ReputationSource source, string expectedKey)
        {
            var action = new GameAction
            {
                UT = 700,
                Type = GameActionType.ReputationEarning,
                RepSource = source,
                NominalRep = 10f,
                EffectiveRep = 7.25f
            };

            var exp = PostWalkActionReconciler.ClassifyPostWalk(action);

            Assert.True(exp.Reconcile);
            // Expected is the post-curve EffectiveRep, never the nominal.
            AssertLeg(exp.Rep, 7.25, expectedKey, GameStateEventType.ReputationChanged);
            AssertNoLeg(exp.Funds);
            AssertNoLeg(exp.Sci);
        }

        [Fact]
        public void ClassifyPostWalk_ReputationEarning_OtherSource_NotReconciled()
        {
            var action = new GameAction
            {
                UT = 700,
                Type = GameActionType.ReputationEarning,
                RepSource = ReputationSource.Other,
                EffectiveRep = 7.25f
            };

            Assert.False(PostWalkActionReconciler.ClassifyPostWalk(action).Reconcile);
        }

        // ==================================================================
        // FundsEarning (KSC path)
        // ==================================================================

        [Fact]
        public void ClassifyPostWalk_FundsEarning_KscPath_UsesFundsAwardedKeyedOther()
        {
            var action = new GameAction
            {
                UT = 1100,
                Type = GameActionType.FundsEarning,
                FundsSource = FundsEarningSource.Other,
                Effective = true,
                FundsAwarded = 1000f
            };

            var exp = PostWalkActionReconciler.ClassifyPostWalk(action);

            Assert.True(exp.Reconcile);
            AssertLeg(exp.Funds, 1000.0, "Other", GameStateEventType.FundsChanged);
            AssertNoLeg(exp.Rep);
            AssertNoLeg(exp.Sci);
        }

        [Theory]
        [InlineData(FundsEarningSource.LegacyMigration)]
        [InlineData(FundsEarningSource.Recovery)]
        [InlineData(FundsEarningSource.ContractComplete)]
        [InlineData(FundsEarningSource.ContractAdvance)]
        [InlineData(FundsEarningSource.Milestone)]
        [InlineData(FundsEarningSource.Strategy)]
        public void ClassifyPostWalk_FundsEarning_OwnChannelSources_NotReconciledHere(
            FundsEarningSource source)
        {
            // Each of these arrives as its own action type or is captured directly
            // from the resource event, so reconciling here would double-warn.
            var action = new GameAction
            {
                UT = 1100,
                Type = GameActionType.FundsEarning,
                FundsSource = source,
                Effective = true,
                FundsAwarded = 1000f
            };

            Assert.False(PostWalkActionReconciler.ClassifyPostWalk(action).Reconcile);
        }

        [Fact]
        public void ClassifyPostWalk_FundsEarning_Ineffective_NotReconciled()
        {
            var action = new GameAction
            {
                UT = 1100,
                Type = GameActionType.FundsEarning,
                FundsSource = FundsEarningSource.Other,
                Effective = false,
                FundsAwarded = 1000f
            };

            Assert.False(PostWalkActionReconciler.ClassifyPostWalk(action).Reconcile);
        }

        // ==================================================================
        // ScienceEarning
        // ==================================================================

        [Fact]
        public void ClassifyPostWalk_ScienceEarning_Transmitted_UsesPostCapEffectiveScience()
        {
            var action = new GameAction
            {
                UT = 1200,
                Type = GameActionType.ScienceEarning,
                SubjectId = "crewReport@KerbinSrfLanded",
                Method = ScienceMethod.Transmitted,
                Effective = true,
                ScienceAwarded = 10f,
                EffectiveScience = 8.5f
            };

            var exp = PostWalkActionReconciler.ClassifyPostWalk(action);

            Assert.True(exp.Reconcile);
            // Post-cap EffectiveScience, not the raw ScienceAwarded.
            AssertLeg(exp.Sci, 8.5, "ScienceTransmission", GameStateEventType.ScienceChanged);
            AssertNoLeg(exp.Funds);
            AssertNoLeg(exp.Rep);
        }

        [Fact]
        public void ClassifyPostWalk_ScienceEarning_Recovered_KeysVesselRecovery()
        {
            var action = new GameAction
            {
                UT = 1200,
                Type = GameActionType.ScienceEarning,
                SubjectId = "crewReport@KerbinSrfLanded",
                Method = ScienceMethod.Recovered,
                Effective = true,
                EffectiveScience = 8.5f
            };

            var exp = PostWalkActionReconciler.ClassifyPostWalk(action);

            Assert.True(exp.Reconcile);
            AssertLeg(exp.Sci, 8.5, "VesselRecovery", GameStateEventType.ScienceChanged);
        }

        [Fact]
        public void ClassifyPostWalk_ScienceEarning_Ineffective_NotReconciled()
        {
            var action = new GameAction
            {
                UT = 1200,
                Type = GameActionType.ScienceEarning,
                SubjectId = "crewReport@KerbinSrfLanded",
                Effective = false,
                EffectiveScience = 8.5f
            };

            Assert.False(PostWalkActionReconciler.ClassifyPostWalk(action).Reconcile);
        }

        // ==================================================================
        // Out-of-scope inputs
        // ==================================================================

        [Fact]
        public void ClassifyPostWalk_NullAction_NotReconciled()
        {
            var exp = PostWalkActionReconciler.ClassifyPostWalk(null);

            Assert.False(exp.Reconcile);
            AssertNoLeg(exp.Funds);
            AssertNoLeg(exp.Rep);
            AssertNoLeg(exp.Sci);
        }

        [Theory]
        [InlineData(GameActionType.ScienceSpending)]
        [InlineData(GameActionType.FundsSpending)]
        [InlineData(GameActionType.FacilityUpgrade)]
        public void ClassifyPostWalk_UntransformedTypes_NotReconciled(GameActionType type)
        {
            // These are reconciled per-action by KscActionReconciler, not here.
            var action = new GameAction { UT = 400, Type = type };

            Assert.False(PostWalkActionReconciler.ClassifyPostWalk(action).Reconcile);
        }

        // ==================================================================
        // Reconcile stage -- no-op / match, mismatch VALUES, idempotence
        // ==================================================================

        [Fact]
        public void ReconcilePostWalk_MilestoneMatchesAcrossAllThreeLegs_NoWarn()
        {
            var events = new List<GameStateEvent>
            {
                FundsEvent(300, 500000, 504800, "Progression"),
                RepEvent(300, 0.0, 1.0, "Progression"),
                ScienceEvent(300, 0.0, 3.0, "Progression")
            };
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 300,
                    Type = GameActionType.MilestoneAchievement,
                    MilestoneId = "FirstLaunch",
                    Effective = true,
                    MilestoneFundsAwarded = 4800.0f,
                    EffectiveRep = 1.0f,
                    MilestoneScienceAwarded = 3.0f
                }
            };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk,"));
            Assert.Contains("actions=1", SummaryLine(logLines));
            Assert.Contains("matches=1", SummaryLine(logLines));
        }

        [Fact]
        public void ReconcilePostWalk_ContractFundsDiverge_WarnCarriesBothValues()
        {
            var events = new List<GameStateEvent>
            {
                FundsEvent(100, 0, 10000, "ContractReward"),
                RepEvent(100, 0, 6.5, "ContractReward"),
                ScienceEvent(100, 0, 22.5, "ContractReward")
            };
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 100,
                    Type = GameActionType.ContractComplete,
                    ContractId = "c_alpha",
                    Effective = true,
                    TransformedFundsReward = 12345.0f,   // diverges from observed 10000
                    EffectiveRep = 6.5f,
                    TransformedScienceReward = 22.5f
                }
            };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            // Values, not just presence: both sides of the divergence must be named.
            Assert.Contains(logLines, l =>
                l.Contains("Earnings reconciliation (post-walk, funds)") &&
                l.Contains("expected=12345.0") &&
                l.Contains("observed=10000.0") &&
                l.Contains("keyed 'ContractReward'"));
            // Only the funds leg diverged.
            Assert.Contains("mismatches(funds/rep/sci)=1/0/0", SummaryLine(logLines));
        }

        [Fact]
        public void ReconcilePostWalk_WrongReasonKeyOnObservedEvent_ReportsMissingChannel()
        {
            // A stock event at the same UT but under a different TransactionReasons
            // key must NOT satisfy the leg - key equality is part of the pairing.
            var events = new List<GameStateEvent>
            {
                RepEvent(500, 2.0, -8.0, "ContractPenalty")
            };
            var actions = new List<GameAction> { KerbalDeathPenalty(effectiveRep: -10f) };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.Contains(logLines, l =>
                l.Contains("Earnings reconciliation (post-walk, rep)") &&
                l.Contains("no matching ReputationChanged event") &&
                l.Contains("keyed 'VesselLoss'"));
            Assert.Contains("mismatches(funds/rep/sci)=0/1/0", SummaryLine(logLines));
        }

        [Fact]
        public void ReconcilePostWalk_EventOutsideEpsilonWindow_DoesNotPair()
        {
            // 5 s away is far outside PostWalkReconcileEpsilonSeconds (0.1 s).
            var events = new List<GameStateEvent>
            {
                RepEvent(505, 2.0, -8.0, "VesselLoss")
            };
            var actions = new List<GameAction> { KerbalDeathPenalty(ut: 500, effectiveRep: -10f) };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.Contains(logLines, l =>
                l.Contains("Earnings reconciliation (post-walk, rep)") &&
                l.Contains("no matching ReputationChanged event"));
        }

        [Fact]
        public void ReconcilePostWalk_ActionBeyondCutoff_IsNotWalked()
        {
            var events = new List<GameStateEvent>
            {
                RepEvent(500, 2.0, -8.0, "VesselLoss")
            };
            var actions = new List<GameAction> { KerbalDeathPenalty(ut: 500, effectiveRep: -10f) };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: 400.0);

            Assert.Contains("actions=0", SummaryLine(logLines));
            Assert.Contains("cutoffUT=400", SummaryLine(logLines));
            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk,"));
        }

        [Fact]
        public void ReconcilePostWalk_SecondPass_IsIdempotent()
        {
            var events = new List<GameStateEvent>
            {
                RepEvent(500, 2.0, 2.0 - 9.999828, "VesselLoss")
            };
            var actions = new List<GameAction> { KerbalDeathPenalty(effectiveRep: -10f) };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);
            string firstSummary = SummaryLine(logLines);
            var firstExpectation = PostWalkActionReconciler.ClassifyPostWalk(actions[0]);

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);
            string secondSummary = SummaryLine(logLines);
            var secondExpectation = PostWalkActionReconciler.ClassifyPostWalk(actions[0]);

            // Reconciliation is log-only: the actions must not have been mutated,
            // so the classification and the summary counts repeat verbatim.
            Assert.Equal(2, SummaryCount(logLines));
            Assert.Equal(firstSummary, secondSummary);
            Assert.Equal(firstExpectation.Rep.Expected, secondExpectation.Rep.Expected, 4);
            Assert.Equal(firstExpectation.Rep.ReasonKey, secondExpectation.Rep.ReasonKey);
            Assert.Equal(-10.0, (double)actions[0].EffectiveRep, 4);
        }

        [Fact]
        public void ReconcilePostWalk_SecondPass_OnMismatch_DoesNotDoubleWarn()
        {
            var actions = new List<GameAction> { KerbalDeathPenalty(effectiveRep: -10f) };

            LedgerOrchestrator.ReconcilePostWalk(new List<GameStateEvent>(), actions, utCutoff: null);
            LedgerOrchestrator.ReconcilePostWalk(new List<GameStateEvent>(), actions, utCutoff: null);

            int warnCount = 0;
            foreach (var l in logLines)
            {
                if (l.Contains("Earnings reconciliation (post-walk, rep)"))
                    warnCount++;
            }

            // LogReconcileWarnOnce dedups by warn key, so the identical divergence
            // warns exactly once no matter how many recalcs run.
            Assert.Equal(1, warnCount);
            // The summary is still emitted per pass, with identical counts.
            Assert.Equal(2, SummaryCount(logLines));
        }
    }
}
