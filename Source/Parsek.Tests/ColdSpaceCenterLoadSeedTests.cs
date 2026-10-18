using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// CAREER-COLD-SC-LOAD-SEEDS-SCIENCE-REP-ZERO (found by harness lane LAB-12, run
    /// 2026-10-18_0050). A cold load through the Space Center runs ParsekScenario.OnLoad
    /// while Planetarium UT reads 0 and BEFORE the R&amp;D and Reputation scenario modules
    /// load (a KSP-written career saves them after ParsekScenario; Funding loads before).
    /// The initial baseline was captured there as ut=0, funds 500000, science 0,
    /// reputation 0 and accepted as the career start, so the science / reputation seeds
    /// were 0 while the save held 400 / 25:
    ///   "Seeded initial science: amount=0" / "Seeded initial reputation: amount=0"
    ///   "sciencePool save=148.625 recon=-251.375 delta=400"
    /// The baseline now records whether each singleton had loaded, and a value captured
    /// without it never seeds; the live pool (DeferredSeed) does instead.
    /// </summary>
    [Collection("Sequential")]
    public class ColdSpaceCenterLoadSeedTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ColdSpaceCenterLoadSeedTests()
        {
            RecalculationEngine.ClearModules();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            CurrencyScenarioReadiness.ResetForTesting();
            LedgerOrchestrator.ExpectedCurrencySingletonsForTesting = () => CurrencySingletons.All;
        }

        public void Dispose()
        {
            LedgerOrchestrator.SetResourceTrackingAvailabilityForTesting(null, null, null);
            RecalculationEngine.ClearModules();
            CurrencyScenarioReadiness.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------

        /// <summary>The LAB-12 capture: Funding loaded, R&amp;D and Reputation not yet.</summary>
        private static GameStateBaseline ColdSpaceCenterBaseline()
        {
            return new GameStateBaseline
            {
                ut = 0.0,
                funds = 500000.0,
                science = 0.0,
                reputation = 0f,
                scienceKnown = false,
                reputationKnown = false
            };
        }

        /// <summary>The pools while ParsekScenario.OnLoad runs on the cold SC route.</summary>
        private static LedgerOrchestrator.CurrencyPoolProbe OnLoadProbe()
        {
            return new LedgerOrchestrator.CurrencyPoolProbe
            {
                FundingPresent = true,
                FundingLoaded = true,
                Funds = 500000.0
            };
        }

        /// <summary>The pools DeferredSeed reads a frame later: everything loaded.</summary>
        private static LedgerOrchestrator.CurrencyPoolProbe DeferredSeedProbe()
        {
            return new LedgerOrchestrator.CurrencyPoolProbe
            {
                FundingPresent = true,
                FundingLoaded = true,
                Funds = 500000.0,
                ScienceSingletonPresent = true,
                ScienceSingletonLoaded = true,
                Science = 400f,
                ReputationSingletonPresent = true,
                ReputationValue = 25f
            };
        }

        private static GameAction SeedOf(GameActionType type)
        {
            return Ledger.Actions.SingleOrDefault(a => a.Type == type);
        }

        // ------------------------------------------------------------------
        // Pure decisions
        // ------------------------------------------------------------------

        // Flags: Funding = 1, R&D = 2, Reputation = 4, All = 7 (expected, loaded, singleton).
        [Theory]
        // Career: all three expected.
        [InlineData(7, 1, 2, false)]
        [InlineData(7, 1, 4, false)]
        [InlineData(7, 7, 2, true)]
        [InlineData(7, 7, 4, true)]
        // Science sandbox: no Reputation singleton exists, so its 0 is the real value.
        [InlineData(2, 0, 4, true)]
        [InlineData(2, 0, 2, false)]
        // Sandbox: nothing expected, every value is real.
        [InlineData(0, 0, 2, true)]
        public void DecideCurrencyValueCaptured_TruthTable(
            int expected, int loaded, int singleton, bool captured)
        {
            // int parameters: CurrencySingletons is internal and xUnit theories are public.
            Assert.Equal(captured,
                GameStateBaseline.DecideCurrencyValueCaptured(
                    (CurrencySingletons)expected, (CurrencySingletons)loaded,
                    (CurrencySingletons)singleton));
        }

        [Theory]
        // A recorded flag is the answer, whatever the value or tech list.
        [InlineData(true, 0.0, 0, true, true)]
        [InlineData(false, 400.0, 30, true, false)]
        // Unrecorded: the R&D-not-loaded signature (0 value, no researched tech) is untrusted.
        [InlineData(null, 0.0, 0, true, false)]
        // Unrecorded with any researched tech: R&D had loaded, the 0 is real (historical).
        [InlineData(null, 0.0, 1, true, true)]
        // Unrecorded non-zero value: a reading.
        [InlineData(null, 25.0, 0, true, true)]
        // Unrecorded in a mode without the singleton: the 0 is real.
        [InlineData(null, 0.0, 0, false, true)]
        public void DecideCurrencyValueKnown_TruthTable(
            bool? recorded, double value, int techCount, bool singletonExpected, bool known)
        {
            Assert.Equal(known,
                GameStateBaseline.DecideCurrencyValueKnown(recorded, value, techCount, singletonExpected));
        }

        [Fact]
        public void Baseline_PresenceFlags_RoundTrip_AndAbsentKeysReadAsUnrecorded()
        {
            var written = ColdSpaceCenterBaseline();
            written.scienceKnown = true;
            var node = new ConfigNode("BASELINE");
            written.SerializeInto(node);

            Assert.Equal("True", node.GetValue("scienceKnown"));
            Assert.Equal("False", node.GetValue("reputationKnown"));
            var read = GameStateBaseline.DeserializeFrom(node);
            Assert.True(read.scienceKnown);
            Assert.False(read.reputationKnown);

            // A baseline written before the keys existed: both stay unrecorded and the
            // writer emits nothing for them, so it round-trips byte-identical.
            var legacy = new GameStateBaseline { ut = 0.0, funds = 25000.0 };
            var legacyNode = new ConfigNode("BASELINE");
            legacy.SerializeInto(legacyNode);
            Assert.Null(legacyNode.GetValue("scienceKnown"));
            Assert.Null(legacyNode.GetValue("reputationKnown"));
            var legacyRead = GameStateBaseline.DeserializeFrom(legacyNode);
            Assert.Null(legacyRead.scienceKnown);
            Assert.Null(legacyRead.reputationKnown);
        }

        [Fact]
        public void FormatCurrencyValue_IsInvariant_AndNamesAnAbsentReading()
        {
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");
                Assert.Equal("148.625", GameStateBaseline.FormatCurrencyValue(148.625, true));
                Assert.Equal("148.625", GameStateBaseline.FormatCurrencyValue(148.625, null));
                Assert.Equal("absent", GameStateBaseline.FormatCurrencyValue(0.0, false));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        // ------------------------------------------------------------------
        // The LAB-12 replay: cold Space Center load, then DeferredSeed
        // ------------------------------------------------------------------

        [Fact]
        public void ColdSpaceCenterLoad_SeedsScienceAndReputationFromTheLivePool_NotTheEarlyBaseline()
        {
            GameStateStore.AddBaseline(ColdSpaceCenterBaseline());

            // OnKspLoad's recalc, inside ParsekScenario.OnLoad: R&D / Reputation missing.
            LedgerOrchestrator.CurrencyPoolProbeForTesting = OnLoadProbe;
            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Equal(500000f, SeedOf(GameActionType.FundsInitial).InitialFunds);
            Assert.Null(SeedOf(GameActionType.ScienceInitial));
            Assert.Null(SeedOf(GameActionType.ReputationInitial));
            Assert.DoesNotContain(logLines, l => l.Contains("Seeded initial science: amount=0"));
            Assert.DoesNotContain(logLines, l => l.Contains("Seeded initial reputation: amount=0"));
            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("carries no science reading")
                && l.Contains("its scenario module had not loaded at capture"));
            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("carries no reputation reading"));

            // DeferredSeed's recalc a frame later: every singleton loaded (400 / 25).
            LedgerOrchestrator.CurrencyPoolProbeForTesting = DeferredSeedProbe;
            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Equal(400f, SeedOf(GameActionType.ScienceInitial).InitialScience);
            Assert.Equal(25f, SeedOf(GameActionType.ReputationInitial).InitialReputation);
            Assert.Contains(logLines, l => l.Contains("[Ledger]") && l.Contains("Seeded initial science: amount=400"));
            Assert.Contains(logLines, l => l.Contains("[Ledger]") && l.Contains("Seeded initial reputation: amount=25"));
            // The funds seed came from the baseline (Funding had loaded) and was not redone.
            Assert.Single(Ledger.Actions, a => a.Type == GameActionType.FundsInitial);
        }

        [Fact]
        public void ColdSpaceCenterLoad_Lab12Career_WalksTo148_625Science_And25Reputation_WithNoReconcileNoise()
        {
            LedgerOrchestrator.SetResourceTrackingAvailabilityForTesting(true, true, true);
            GameStateStore.AddBaseline(ColdSpaceCenterBaseline());

            LedgerOrchestrator.CurrencyPoolProbeForTesting = OnLoadProbe;
            LedgerOrchestrator.RecalculateAndPatch();
            LedgerOrchestrator.CurrencyPoolProbeForTesting = DeferredSeedProbe;
            LedgerOrchestrator.RecalculateAndPatch();

            // Patents Licensing (science -> funds converter): activation charges 263.25
            // science; a lab transmission at ut=510 pays 12.5 gross, 0.625 of which the
            // converter diverts (52.54 funds), so KSP's ScienceChanged reads +11.875.
            const double labUt = 510.006082343961;
            Ledger.AddAction(new GameAction
            {
                UT = 356.07153320304411,
                Type = GameActionType.StrategyActivate,
                StrategyId = "PatentsLicensingCfg",
                SourceResource = StrategyResource.Science,
                TargetResource = StrategyResource.Funds,
                Commitment = 0.05f,
                SetupScienceCost = 263.25f
            });
            Ledger.AddAction(new GameAction
            {
                UT = labUt,
                Type = GameActionType.StrategyScienceDebit,
                Cost = 0.625f,
                ConversionSource = StrategyConversionSource.Converter
            });
            Ledger.AddAction(new GameAction
            {
                UT = labUt,
                Type = GameActionType.FundsEarning,
                FundsSource = FundsEarningSource.Strategy,
                FundsAwarded = 52.540203094482422f
            });
            Ledger.AddAction(new GameAction
            {
                UT = labUt,
                Type = GameActionType.ScienceLabEarning,
                ScienceAwarded = 12.5f,
                Method = ScienceMethod.Transmitted
            });
            var labEvent = new GameStateEvent
            {
                ut = labUt,
                eventType = GameStateEventType.ScienceChanged,
                key = "ScienceTransmission",
                valueBefore = 136.75,
                valueAfter = 148.625,
                recordingId = ""
            };
            GameStateStore.AddEvent(ref labEvent);

            logLines.Clear();
            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Equal(148.625, LedgerOrchestrator.Science.GetRunningScience(), 3);
            Assert.Equal(25.0, (double)LedgerOrchestrator.Reputation.GetRunningRep(), 3);
            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk,"));
            Assert.DoesNotContain(logLines, l => l.Contains("Science reconcile dump (post-walk)"));
            Assert.Contains(logLines, l => l.Contains("Post-walk reconcile: actions=")
                && l.Contains("mismatches(funds/rep/sci)=0/0/0"));
        }

        // ------------------------------------------------------------------
        // Mirrors: what must NOT change
        // ------------------------------------------------------------------

        [Fact]
        public void GenuineCareerStart_WithScienceAndReputationLoadedAtZero_StillSeedsZeroFromTheBaseline()
        {
            GameStateStore.AddBaseline(new GameStateBaseline
            {
                ut = 0.0,
                funds = 25000.0,
                science = 0.0,
                reputation = 0f,
                scienceKnown = true,
                reputationKnown = true
            });
            // A later live pool must not leak in: the baseline is the career start.
            LedgerOrchestrator.CurrencyPoolProbeForTesting = DeferredSeedProbe;

            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Equal(0f, SeedOf(GameActionType.ScienceInitial).InitialScience);
            Assert.Equal(0f, SeedOf(GameActionType.ReputationInitial).InitialReputation);
            Assert.Equal(25000f, SeedOf(GameActionType.FundsInitial).InitialFunds);
            Assert.DoesNotContain(logLines, l => l.Contains("carries no science reading"));
            Assert.DoesNotContain(logLines, l => l.Contains("carries no reputation reading"));
        }

        [Fact]
        public void FlightRouteLoad_BaselineAtTheSaveUt_IsNotInitial_LivePoolSeedsAsBefore()
        {
            // The FLIGHT route has a ready clock: the baseline lands at the save UT, is not
            // a career-start candidate at all, and its flags are never consulted.
            GameStateStore.AddBaseline(new GameStateBaseline
            {
                ut = 355.83153320304433,
                funds = 500000.0,
                science = 0.0,
                reputation = 0f,
                scienceKnown = false,
                reputationKnown = false
            });
            LedgerOrchestrator.CurrencyPoolProbeForTesting = DeferredSeedProbe;

            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Equal(400f, SeedOf(GameActionType.ScienceInitial).InitialScience);
            Assert.Equal(25f, SeedOf(GameActionType.ReputationInitial).InitialReputation);
            Assert.Equal(500000f, SeedOf(GameActionType.FundsInitial).InitialFunds);
            Assert.DoesNotContain(logLines, l => l.Contains("carries no science reading"));
        }

        [Fact]
        public void LegacyBaseline_ZeroScienceWithNoResearchedTech_IsNotTrusted()
        {
            // Written before the flags existed, by the same early capture: tech=0 proves R&D
            // had not loaded.
            GameStateStore.AddBaseline(new GameStateBaseline
            {
                ut = 0.0,
                funds = 500000.0,
                science = 0.0,
                reputation = 0f
            });
            LedgerOrchestrator.CurrencyPoolProbeForTesting = DeferredSeedProbe;

            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Equal(400f, SeedOf(GameActionType.ScienceInitial).InitialScience);
            Assert.Equal(25f, SeedOf(GameActionType.ReputationInitial).InitialReputation);
            Assert.Contains(logLines, l => l.Contains("carries no science reading")
                && l.Contains("unrecorded flag"));
        }

        [Fact]
        public void LegacyBaseline_ZeroScienceWithResearchedTech_KeepsTheHistoricalSeed()
        {
            var legacy = new GameStateBaseline
            {
                ut = 0.0,
                funds = 25000.0,
                science = 0.0,
                reputation = 0f
            };
            legacy.researchedTechIds.Add("start");
            GameStateStore.AddBaseline(legacy);
            LedgerOrchestrator.CurrencyPoolProbeForTesting = DeferredSeedProbe;

            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Equal(0f, SeedOf(GameActionType.ScienceInitial).InitialScience);
            Assert.Equal(0f, SeedOf(GameActionType.ReputationInitial).InitialReputation);
        }

        [Fact]
        public void ColdLoad_WithReputationHistoryAndNoReading_TakesTheNoBaselineRefusal()
        {
            // The fall-through decides exactly as with no baseline: reputation history
            // already in the ledger refuses the live pool (career-start 0, Warn), unchanged.
            GameStateStore.AddBaseline(ColdSpaceCenterBaseline());
            Ledger.AddAction(new GameAction
            {
                UT = 120.0,
                Type = GameActionType.ReputationPenalty,
                RepPenaltySource = ReputationPenaltySource.ContractDecline,
                NominalPenalty = 5f
            });
            LedgerOrchestrator.CurrencyPoolProbeForTesting = DeferredSeedProbe;

            var origin = LedgerOrchestrator.EnsureReputationSeedForCommit();

            Assert.Equal(ReputationSeedOrigin.CreatedThisCommitFromRefusalFallback, origin);
            Assert.Equal(0f, SeedOf(GameActionType.ReputationInitial).InitialReputation);
            Assert.Contains(logLines, l => l.Contains("SeedInitialReputation: refusing to treat current reputation"));
        }
    }
}
