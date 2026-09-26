using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// KSP-SETTINGS-AUDIT-2026-09-26 ledger items, each at a NON-Normal stock value (every
    /// committed fixture carries the Normal preset, which is why none of these was visible):
    /// <list type="bullet">
    /// <item>S1 - <c>Career.ScienceGainMultiplier</c> (Easy 2, Hard 0.6) frozen on the
    /// science row at capture; subject-level cap math stays pre-multiplier.</item>
    /// <item>S2 - <c>Career.RepLossDeclined</c> (Hard 3) reaches the ledger as a
    /// KSC-origin, already-curved reputation penalty.</item>
    /// <item>S4 - a genuinely zero pool (StartingFunds = 0, Science mode at 0) is seeded
    /// off the positive "OnLoad ran" signal, and the waits key on the mode's singletons.</item>
    /// <item>S11 - Alt+F12 cheat currency is logged as unledgered, once per event.</item>
    /// </list>
    /// </summary>
    [Collection("Sequential")]
    public class KspSettingsLedgerTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public KspSettingsLedgerTests()
        {
            RecalculationEngine.ClearModules();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            CurrencyScenarioReadiness.ResetForTesting();
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            CurrencyScenarioReadiness.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            GameStateStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // S1 - ScienceGainMultiplier
        // ================================================================

        private static GameAction ScienceRow(string subject, float awarded, float cap, float multiplier)
        {
            return new GameAction
            {
                UT = 100.0,
                Type = GameActionType.ScienceEarning,
                SubjectId = subject,
                ScienceAwarded = awarded,
                SubjectMaxValue = cap,
                ScienceGainMultiplier = multiplier
            };
        }

        [Theory]
        [InlineData(2.0f)]   // Easy
        [InlineData(0.6f)]   // Hard
        [InlineData(0.9f)]   // Moderate
        public void ScienceModule_CreditsThePoolTimesTheCapturedMultiplier_SubjectStaysPreMultiplier(float multiplier)
        {
            var module = new ScienceModule();
            module.Reset();
            var row = ScienceRow("crewReport@KerbinSrfLandedLaunchPad", 5f, 50f, multiplier);

            module.ProcessAction(row);

            // Pool: what stock's AddScience received (5 * multiplier).
            Assert.Equal(5.0 * multiplier, module.GetRunningScience(), 4);
            Assert.Equal(5.0 * multiplier, (double)row.EffectiveScience, 4);
            // Subject: what stock added to subject.science (pre-multiplier) - the value
            // ScienceSubjectPatch injects and the cap walk measures against.
            Assert.Equal(5.0, module.GetSubjectCredited("crewReport@KerbinSrfLandedLaunchPad"), 4);
            Assert.Contains(logLines, l => l.Contains("[ScienceModule]") && l.Contains("gainMultiplier="));
        }

        [Fact]
        public void ScienceModule_CapHeadroomIsSubjectUnits_PoolIsScaledAfterTheCap()
        {
            var module = new ScienceModule();
            module.Reset();
            // Cap 8 in subject units: first row fills 6, second row's 5 is capped to 2.
            var first = ScienceRow("s", 6f, 8f, 2f);
            var second = ScienceRow("s", 5f, 8f, 2f);

            module.ProcessAction(first);
            module.ProcessAction(second);

            Assert.Equal(8.0, module.GetSubjectCredited("s"), 4);
            Assert.Equal(4.0, (double)second.EffectiveScience, 4);   // 2 subject * 2
            Assert.Equal(16.0, module.GetRunningScience(), 4);       // (6 + 2) * 2
        }

        [Fact]
        public void ScienceModule_DefaultAndLegacyRowsReadAsTimesOne()
        {
            var module = new ScienceModule();
            module.Reset();
            var legacy = ScienceRow("s", 5f, 50f, 0f); // a pre-field row / default struct
            module.ProcessAction(legacy);
            Assert.Equal(5.0, module.GetRunningScience(), 4);
            Assert.DoesNotContain(logLines, l => l.Contains("gainMultiplier="));
        }

        [Theory]
        [InlineData(0f, 1f)]
        [InlineData(-1f, 1f)]
        [InlineData(float.NaN, 1f)]
        [InlineData(float.PositiveInfinity, 1f)]
        [InlineData(0.6f, 0.6f)]
        [InlineData(10f, 10f)]
        public void NormalizeScienceGainMultiplier_MapsUncapturedToOne(float input, float expected)
        {
            Assert.Equal(expected, GameAction.NormalizeScienceGainMultiplier(input));
        }

        [Fact]
        public void ScienceEarning_Serialization_RoundTripsTheMultiplier_AndStaysSparseAtOne()
        {
            var parent = new ConfigNode("ROOT");
            ScienceRow("s", 5f, 50f, 0.6f).SerializeInto(parent);
            var node = parent.GetNodes("GAME_ACTION")[0];
            Assert.Equal("0.6", node.GetValue(GameAction.ScienceGainMultiplierKey));
            Assert.Equal(0.6f, GameAction.DeserializeFrom(node).ScienceGainMultiplier);

            var parentOne = new ConfigNode("ROOT");
            ScienceRow("s", 5f, 50f, 1f).SerializeInto(parentOne);
            var nodeOne = parentOne.GetNodes("GAME_ACTION")[0];
            Assert.Null(nodeOne.GetValue(GameAction.ScienceGainMultiplierKey));
            Assert.Equal(1f, GameAction.DeserializeFrom(nodeOne).ScienceGainMultiplier);
        }

        [Fact]
        public void ConvertScienceSubjects_StampsTheCapturedMultiplier()
        {
            var subjects = new List<PendingScienceSubject>
            {
                new PendingScienceSubject
                {
                    subjectId = "s", science = 5f, subjectMaxValue = 50f,
                    captureUT = 120.0, recordingId = "rec", scienceGainMultiplier = 2f
                },
                new PendingScienceSubject
                {
                    subjectId = "t", science = 3f, subjectMaxValue = 50f,
                    captureUT = 121.0, recordingId = "rec" // multiplier not captured
                }
            };

            var actions = GameStateEventConverter.ConvertScienceSubjects(subjects, "rec", 100.0, 200.0);

            Assert.Equal(2, actions.Count);
            Assert.Equal(5f, actions[0].ScienceAwarded);           // subject units kept
            Assert.Equal(2f, actions[0].ScienceGainMultiplier);
            Assert.Equal(10f, actions[0].GetScienceAwardedPoolCredit());
            Assert.Equal(1f, actions[1].ScienceGainMultiplier);
        }

        [Fact]
        public void ReadScienceGainMultiplierAtCapture_UsesTheProviderAndNormalizes()
        {
            GameStateRecorder.ScienceGainMultiplierProviderForTesting = () => 0.6f;
            Assert.Equal(0.6f, GameStateRecorder.ReadScienceGainMultiplierAtCapture());
            GameStateRecorder.ScienceGainMultiplierProviderForTesting = () => 0f;
            Assert.Equal(1f, GameStateRecorder.ReadScienceGainMultiplierAtCapture());
        }

        [Fact]
        public void PendingRecentKscScienceCredit_ComparesPoolUnitsOnBothSides()
        {
            // Stock fired ScienceChanged +10 (5 subject * x2). The committed row's pool
            // credit is also 10, so nothing is pending. Pre-fix the committed side read the
            // subject value 5 and held a phantom 5 forward.
            var evt = new GameStateEvent
            {
                ut = 500.0,
                eventType = GameStateEventType.ScienceChanged,
                key = "VesselRecovery",
                valueBefore = 20.0,
                valueAfter = 30.0
            };
            var events = new List<GameStateEvent> { evt };
            var row = ScienceRow("s", 5f, 50f, 2f);
            row.UT = 500.0;

            double pending = LedgerOrchestrator.ComputePendingRecentKscScienceCredit(
                events, new List<GameAction> { row }, 500.0);

            Assert.Equal(0.0, pending, 4);
        }

        [Fact]
        public void Recorder_StampsTheMultiplierOnThePendingSubject()
        {
            string body = ReadRecorderMethod("private void OnScienceReceived(", "#endregion");
            string collapsed = Regex.Replace(body, @"\s+", " ");
            Assert.Contains("scienceGainMultiplier = ReadScienceGainMultiplierAtCapture()", collapsed);
        }

        // ================================================================
        // S2 - RepLossDeclined
        // ================================================================

        private static GameStateEvent DeclineEvent(double ut, double before, double after, string recordingId = "")
        {
            return new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.ReputationChanged,
                key = GameStateEventConverter.ContractDeclineReasonKey,
                valueBefore = before,
                valueAfter = after,
                recordingId = recordingId
            };
        }

        [Fact]
        public void ConvertEvent_ContractDeclineReputation_BecomesAPreCurvedPenalty()
        {
            // Hard: RepLossDeclined 3; stock's curve at a high pool subtracted 3.25.
            var action = GameStateEventConverter.ConvertEvent(DeclineEvent(700.0, 400.0, 396.75), null);

            Assert.NotNull(action);
            Assert.Equal(GameActionType.ReputationPenalty, action.Type);
            Assert.Equal(ReputationPenaltySource.ContractDecline, action.RepPenaltySource);
            Assert.Equal(3.25f, action.NominalPenalty, 0.0001f);
            Assert.Null(action.RecordingId);
        }

        [Fact]
        public void ConvertEvent_ContractDeclineWithNoLoss_WritesNothing()
        {
            Assert.Null(GameStateEventConverter.ConvertEvent(DeclineEvent(700.0, 400.0, 400.0), null));
            Assert.Null(GameStateEventConverter.ConvertEvent(DeclineEvent(700.0, 400.0, 401.0), null));
        }

        [Fact]
        public void ConvertEvent_OtherReputationReasonsStayDropped()
        {
            var evt = DeclineEvent(700.0, 400.0, 397.0);
            evt.key = "ContractPenalty";
            Assert.Null(GameStateEventConverter.ConvertEvent(evt, null));
        }

        [Fact]
        public void ReputationModule_AppliesTheDeclinePenaltyWithoutRecurving()
        {
            var module = new ReputationModule();
            module.Reset();
            // A high pool, where stock's subtraction curve is far from x1: re-curving the
            // captured magnitude here would move it again.
            module.ProcessAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.ReputationInitial,
                InitialReputation = 800f
            });
            var penalty = new GameAction
            {
                UT = 700.0,
                Type = GameActionType.ReputationPenalty,
                NominalPenalty = 3f,
                RepPenaltySource = ReputationPenaltySource.ContractDecline
            };

            module.ProcessAction(penalty);

            Assert.Equal(-3f, penalty.EffectiveRep, 0.0001f);
            Assert.Equal(797f, module.GetRunningRep(), 0.0001f);
            Assert.Contains(logLines, l =>
                l.Contains("[Reputation]") && l.Contains("ContractDecline, pre-curved"));
        }

        [Fact]
        public void OnKscSpending_ContractDecline_WritesAKscOriginPenaltyRow()
        {
            LedgerOrchestrator.Initialize();
            var evt = DeclineEvent(700.0, 12.0, 9.0);
            GameStateStore.AddEvent(ref evt);

            LedgerOrchestrator.OnKscSpending(evt);

            var rows = Ledger.Actions.Where(a => a.Type == GameActionType.ReputationPenalty).ToList();
            Assert.Single(rows);
            Assert.Equal(ReputationPenaltySource.ContractDecline, rows[0].RepPenaltySource);
            Assert.Null(rows[0].RecordingId);
            Assert.Equal(3f, rows[0].NominalPenalty, 0.0001f);
            Assert.NotEqual(0, rows[0].Sequence);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("KSC spending recorded")
                && l.Contains("ReputationPenalty"));
        }

        [Fact]
        public void DeclinePenaltyDedupKey_IsDistinctFromTheHistoricalEmptyKey()
        {
            var decline = GameStateEventConverter.ConvertEvent(DeclineEvent(700.0, 12.0, 9.0), null);
            var other = new GameAction
            {
                UT = 700.0,
                Type = GameActionType.ReputationPenalty,
                NominalPenalty = 3f,
                RepPenaltySource = ReputationPenaltySource.Other
            };

            Assert.NotEqual(LedgerOrchestrator.GetActionKey(other), LedgerOrchestrator.GetActionKey(decline));
            Assert.Contains("ContractDecline", LedgerOrchestrator.GetActionKey(decline));
        }

        [Theory]
        [InlineData(-0.5f, false)]   // RepLossDeclined 1 curved small at a negative pool
        [InlineData(-3f, false)]
        [InlineData(0f, true)]
        public void ReputationThreshold_ExemptsContractDecline(float delta, bool expectedBelow)
        {
            Assert.Equal(expectedBelow,
                GameStateRecorder.IsReputationDeltaBelowThreshold(delta, TransactionReasons.ContractDecline));
            // Every other reason keeps the 1-point floor.
            Assert.True(GameStateRecorder.IsReputationDeltaBelowThreshold(-0.5f, TransactionReasons.ContractPenalty));
        }

        [Fact]
        public void Recorder_OnReputationChanged_ForwardsContractDeclineAfterTheEmit()
        {
            string body = ReadRecorderMethod(
                "private void OnReputationChanged(", "internal static bool IsReputationDeltaBelowThreshold(float delta)");
            int emitIndex = body.IndexOf("Emit(ref repEvt", StringComparison.Ordinal);
            int forwardIndex = body.IndexOf("LedgerOrchestrator.OnKscSpending(repEvt)", StringComparison.Ordinal);
            Assert.True(emitIndex >= 0 && forwardIndex > emitIndex,
                "OnReputationChanged must forward to the ledger AFTER the Emit");

            string collapsed = Regex.Replace(body, @"\s+", " ");
            Assert.Contains(
                "if ((reason == TransactionReasons.StrategyInput || reason == TransactionReasons.ContractDecline) && " +
                "ShouldForwardDirectLedgerEvent(repEvt.recordingId, HasLiveRecorder())) " +
                "LedgerOrchestrator.OnKscSpending(repEvt);",
                collapsed);
            Assert.Contains("IsReputationDeltaBelowThreshold(delta, reason)", collapsed);
        }

        // ================================================================
        // S4 - zero pools and mode-keyed waits
        // ================================================================

        [Theory]
        // expected: Funding=1, R&D=2, Reputation=4 (CurrencySingletons is internal, so the
        // theory row carries the raw flags).
        [InlineData(Game.Modes.CAREER, 7)]
        [InlineData(Game.Modes.SCIENCE_SANDBOX, 2)]
        [InlineData(Game.Modes.SANDBOX, 0)]
        [InlineData(Game.Modes.MISSION, 3)]
        [InlineData(Game.Modes.SCENARIO, 7)]
        public void ExpectedFor_MatchesStockScenarioCreationOptions(Game.Modes mode, int expected)
        {
            Assert.Equal((CurrencySingletons)expected, CurrencyScenarioReadiness.ExpectedFor(mode));
        }

        [Fact]
        public void ScienceMode_IsSatisfiedByRnDAlone_SandboxByNothing()
        {
            CurrencyScenarioReadiness.ModeProviderForTesting = () => Game.Modes.SCIENCE_SANDBOX;
            CurrencyScenarioReadiness.PresentProviderForTesting = () => CurrencySingletons.ResearchAndDevelopment;
            Assert.True(CurrencyScenarioReadiness.AllExpectedPresent());

            CurrencyScenarioReadiness.ModeProviderForTesting = () => Game.Modes.SANDBOX;
            CurrencyScenarioReadiness.PresentProviderForTesting = () => CurrencySingletons.None;
            Assert.True(CurrencyScenarioReadiness.AllExpectedPresent());

            // Career still needs all three (the "any one" defect stays closed).
            CurrencyScenarioReadiness.ModeProviderForTesting = () => Game.Modes.CAREER;
            CurrencyScenarioReadiness.PresentProviderForTesting = () => CurrencySingletons.ResearchAndDevelopment;
            Assert.False(CurrencyScenarioReadiness.AllExpectedPresent());
        }

        [Fact]
        public void AllPresentLoaded_WaitsOnOnLoadNotOnValue()
        {
            CurrencyScenarioReadiness.PresentProviderForTesting = () => CurrencySingletons.All;
            CurrencyScenarioReadiness.LoadedProviderForTesting =
                () => CurrencySingletons.Funding | CurrencySingletons.ResearchAndDevelopment;
            Assert.False(CurrencyScenarioReadiness.AllPresentLoaded());

            CurrencyScenarioReadiness.LoadedProviderForTesting = () => CurrencySingletons.All;
            Assert.True(CurrencyScenarioReadiness.AllPresentLoaded());
        }

        [Theory]
        [InlineData(false, true, true, true, false)]   // absent is never loaded
        [InlineData(true, false, false, false, true)]  // no proto list: presence fallback
        [InlineData(true, true, false, false, true)]   // no proto names it: presence fallback
        [InlineData(true, true, true, false, false)]   // proto still points elsewhere: OnLoad not done
        [InlineData(true, true, true, true, true)]     // proto.moduleRef is this instance
        public void DecideModuleLoaded_Table(bool present, bool listAvailable, bool named, bool refIsModule, bool expected)
        {
            Assert.Equal(expected,
                CurrencyScenarioReadiness.DecideModuleLoaded(present, listAvailable, named, refIsModule));
        }

        private static LedgerOrchestrator.CurrencyPoolProbe LoadedFunding(double funds)
        {
            return new LedgerOrchestrator.CurrencyPoolProbe
            {
                FundingPresent = true,
                FundingLoaded = true,
                Funds = funds
            };
        }

        [Fact]
        public void DecideInitialFundsSeed_Table()
        {
            var loadedZero = LoadedFunding(0.0);
            var unloadedZero = new LedgerOrchestrator.CurrencyPoolProbe { FundingPresent = true, Funds = 0.0 };

            Assert.Equal(LedgerOrchestrator.FundsSeedDecision.SeedConfirmedZero,
                LedgerOrchestrator.DecideInitialFundsSeed(false, 0f, false, true, 0.0, loadedZero, false));
            Assert.Equal(LedgerOrchestrator.FundsSeedDecision.Defer,
                LedgerOrchestrator.DecideInitialFundsSeed(false, 0f, false, true, 0.0, unloadedZero, false));
            // Funds history + a zero pool says nothing about the start value.
            Assert.Equal(LedgerOrchestrator.FundsSeedDecision.Defer,
                LedgerOrchestrator.DecideInitialFundsSeed(false, 0f, false, false, 0.0, loadedZero, true));
            // A confirmed zero is never repaired, even when a later pool / baseline is non-zero.
            Assert.Equal(LedgerOrchestrator.FundsSeedDecision.AlreadySeeded,
                LedgerOrchestrator.DecideInitialFundsSeed(true, 0f, true, true, 25000.0, LoadedFunding(5000.0), true));
            // Unchanged career paths.
            Assert.Equal(LedgerOrchestrator.FundsSeedDecision.AlreadySeeded,
                LedgerOrchestrator.DecideInitialFundsSeed(true, 25000f, false, true, 25000.0, LoadedFunding(1.0), true));
            Assert.Equal(LedgerOrchestrator.FundsSeedDecision.SeedFromBaseline,
                LedgerOrchestrator.DecideInitialFundsSeed(false, 0f, false, true, 25000.0, LoadedFunding(1.0), false));
            Assert.Equal(LedgerOrchestrator.FundsSeedDecision.SeedFromLivePool,
                LedgerOrchestrator.DecideInitialFundsSeed(true, 0f, false, false, 0.0, LoadedFunding(1234.0), false));
        }

        [Fact]
        public void ZeroStartingFundsCareer_IsSeededAtZero_AndTheSeedIsSealed()
        {
            LedgerOrchestrator.CurrencyPoolProbeForTesting = () => LoadedFunding(0.0);
            GameStateStore.AddBaseline(new GameStateBaseline { ut = 0.0, funds = 0.0, science = 0.0, reputation = 0f });

            LedgerOrchestrator.RecalculateAndPatch();

            var seed = Ledger.Actions.Single(a => a.Type == GameActionType.FundsInitial);
            Assert.Equal(0f, seed.InitialFunds);
            Assert.True(seed.InitialFundsConfirmedZero);
            Assert.True(LedgerOrchestrator.Funds.HasSeed);
            Assert.Contains(logLines, l => l.Contains("[Ledger]") && l.Contains("confirmedZero=True"));

            // The stale-zero repair must not fold a later pool into UT0.
            Ledger.SeedInitialFunds(5000.0);
            Assert.Equal(0f, Ledger.Actions.Single(a => a.Type == GameActionType.FundsInitial).InitialFunds);
            Assert.Contains(logLines, l => l.Contains("[Ledger]") && l.Contains("is a confirmed zero"));

            // And the flag survives a save/load round trip.
            var parent = new ConfigNode("ROOT");
            seed.SerializeInto(parent);
            Assert.True(GameAction.DeserializeFrom(parent.GetNodes("GAME_ACTION")[0]).InitialFundsConfirmedZero);
        }

        [Fact]
        public void UnloadedFundingAtZero_StillDefers()
        {
            LedgerOrchestrator.CurrencyPoolProbeForTesting = () =>
                new LedgerOrchestrator.CurrencyPoolProbe { FundingPresent = true, FundingLoaded = false, Funds = 0.0 };

            LedgerOrchestrator.RecalculateAndPatch();

            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.FundsInitial);
            Assert.Contains(logLines, l => l.Contains("SeedInitialFunds: deferring") && l.Contains("OnLoad has not run"));
        }

        [Fact]
        public void ScienceModeAtZeroScience_WithNoBaseline_IsSeededAtZero()
        {
            LedgerOrchestrator.CurrencyPoolProbeForTesting = () => new LedgerOrchestrator.CurrencyPoolProbe
            {
                ScienceSingletonPresent = true,
                ScienceSingletonLoaded = true,
                Science = 0f
            };

            LedgerOrchestrator.RecalculateAndPatch();

            var seed = Ledger.Actions.Single(a => a.Type == GameActionType.ScienceInitial);
            Assert.Equal(0f, seed.InitialScience);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.FundsInitial);
            Assert.Contains(logLines, l => l.Contains("SeedInitialScience: seeded a loaded zero pool"));
        }

        [Fact]
        public void ScienceModeAtZeroScience_BeforeOnLoad_StillDefers()
        {
            LedgerOrchestrator.CurrencyPoolProbeForTesting = () => new LedgerOrchestrator.CurrencyPoolProbe
            {
                ScienceSingletonPresent = true,
                ScienceSingletonLoaded = false,
                Science = 0f
            };

            LedgerOrchestrator.RecalculateAndPatch();

            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.ScienceInitial);
        }

        // ================================================================
        // S11 - cheat currency
        // ================================================================

        [Fact]
        public void CheatCurrency_LogsOneUnledgeredLinePerEvent()
        {
            Assert.True(GameStateRecorder.LogUnledgeredCheatCurrency(
                TransactionReasons.Cheating, "funds", 100000.0, 125000.0));
            Assert.True(GameStateRecorder.LogUnledgeredCheatCurrency(
                TransactionReasons.Cheating, "science", -20.5, 0.0));

            var lines = logLines.Where(l => l.Contains("[GameStateRecorder]") && l.Contains("is not ledgered")).ToList();
            Assert.Equal(2, lines.Count);
            Assert.Contains(lines, l => l.Contains("[INFO]") && l.Contains("Cheat funds change +100000") && l.Contains("next rewind"));
            Assert.Contains(lines, l => l.Contains("Cheat science change -20.5"));
        }

        [Fact]
        public void NonCheatCurrency_DoesNotLogTheCheatLine()
        {
            Assert.False(GameStateRecorder.LogUnledgeredCheatCurrency(
                TransactionReasons.ContractReward, "funds", 100.0, 200.0));
            Assert.DoesNotContain(logLines, l => l.Contains("is not ledgered"));
        }

        [Fact]
        public void CheatCurrency_ProducesNoLedgerRow()
        {
            var evt = new GameStateEvent
            {
                ut = 10.0,
                eventType = GameStateEventType.FundsChanged,
                key = "Cheating",
                valueBefore = 0.0,
                valueAfter = 100000.0
            };
            Assert.Null(GameStateEventConverter.ConvertEvent(evt, null));
            evt.eventType = GameStateEventType.ReputationChanged;
            Assert.Null(GameStateEventConverter.ConvertEvent(evt, null));
        }

        [Fact]
        public void Recorder_AllThreeCurrencyHandlersLogCheats()
        {
            string source = ReadRecorderSource();
            Assert.Contains("LogUnledgeredCheatCurrency(reason, \"funds\", delta, newFunds);", source);
            Assert.Contains("LogUnledgeredCheatCurrency(reason, \"science\", delta, newScience);", source);
            Assert.Contains("LogUnledgeredCheatCurrency(reason, \"reputation\", delta, newReputation);", source);
        }

        // ================================================================

        private static string ReadRecorderSource()
        {
            string path = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "Source", "Parsek", "GameStateRecorder.cs"));
            Assert.True(File.Exists(path), "source file not found for scan: " + path);
            return File.ReadAllText(path);
        }

        private static string ReadRecorderMethod(string signature, string endAnchor)
        {
            string source = ReadRecorderSource();
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, signature + " not found");
            int end = source.IndexOf(endAnchor, start, StringComparison.Ordinal);
            Assert.True(end > start, endAnchor + " not found after " + signature);
            return source.Substring(start, end - start);
        }
    }
}
