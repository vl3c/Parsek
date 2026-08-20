using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
// The capture-matrix cells (and the StrategyLifecycle region as a whole) live in
// Parsek.InGameTests.FlightIntegrationTests, NOT in the RuntimeTests class the file is
// named after - RuntimeTests.cs carries several test classes. Aliased rather than
// imported wholesale so nothing else from that namespace can shadow a name here.
using StrategyCells = Parsek.InGameTests.FlightIntegrationTests;

namespace Parsek.Tests
{
    /// <summary>
    /// The QUERY-FAMILY strategy door (STRATEGY-SCIENCE-CONVERSION-LEAK /
    /// STRATEGY-FUNDS-YIELD-DRIFT): the pure scoping rule in
    /// <see cref="StrategyConversionCapture"/>, the row shapes
    /// <see cref="LedgerOrchestrator.BuildStrategyConversionAction"/> maps them to, and
    /// the source gates for the recorder wiring (a live <c>GameEvents</c> subscription
    /// is not xUnit-drivable, so the hookup is locked with the house source-text
    /// pattern).
    /// </summary>
    [Collection("Sequential")]
    public class StrategyConversionCaptureTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public StrategyConversionCaptureTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        private static StrategyConversionQuery Query(
            double inF = 0, double dF = 0, double inS = 0, double dS = 0,
            double inR = 0, double dR = 0, string reason = "ScienceTransmission")
        {
            return new StrategyConversionQuery
            {
                InputFunds = inF,
                DeltaFunds = dF,
                InputScience = inS,
                DeltaScience = dS,
                InputReputation = inR,
                DeltaReputation = dR,
                Reason = reason
            };
        }

        // ================================================================
        // Scoping rule
        // ================================================================

        [Fact]
        public void ConverterScienceTake_IsCaptured()
        {
            // Patents Licensing: a science transmission of 40 with a 20% share.
            var legs = StrategyConversionCapture.EvaluateLegs(
                Query(inS: 40.0, dS: -8.0, dF: 800.0));

            var sci = legs.Single(l => l.Currency == StrategyConversionCurrency.Science);
            Assert.Equal(-8.0, sci.Delta, 6);
        }

        [Fact]
        public void ConverterScienceYield_IsCaptured()
        {
            // Open-Source Tech Program shape: funds in, science out. The funds TAKE has
            // a nonzero input (the ordinary channel reports it net), the science YIELD
            // does not.
            var legs = StrategyConversionCapture.EvaluateLegs(
                Query(inF: 10000.0, dF: -2000.0, dS: 5.0));

            var sci = legs.Single(l => l.Currency == StrategyConversionCurrency.Science);
            Assert.Equal(5.0, sci.Delta, 6);
            Assert.DoesNotContain(legs, l => l.Currency == StrategyConversionCurrency.Funds);
        }

        [Fact]
        public void CurrencyOperationFundsMultiplier_IsNotCaptured()
        {
            // A reward multiplier on a funds transaction: input != 0, so the net
            // event-driven channel already sees the modified value. Capturing here
            // would double-count it.
            var legs = StrategyConversionCapture.EvaluateLegs(
                Query(inF: 10000.0, dF: 2000.0, reason: "ContractReward"));

            Assert.Empty(legs);
        }

        [Fact]
        public void CurrencyOperationScienceMultiplier_IsCaptured()
        {
            // Science is the asymmetric case ON PURPOSE: the earning channel is
            // archive-derived (subject value), so it never sees a pool-only movement,
            // input or no input.
            var legs = StrategyConversionCapture.EvaluateLegs(
                Query(inS: 40.0, dS: 8.0, reason: "ScienceTransmission"));

            var sci = legs.Single(l => l.Currency == StrategyConversionCurrency.Science);
            Assert.Equal(8.0, sci.Delta, 6);
        }

        [Fact]
        public void CrossCurrencyFundsYield_IsCaptured()
        {
            // The measured STRATEGY-FUNDS-YIELD-DRIFT shape: no funds input, a small
            // funds delta that the recorder's 100-funds threshold would drop.
            var legs = StrategyConversionCapture.EvaluateLegs(
                Query(inS: 5.0, dS: -1.0, dF: 60.526316));

            var funds = legs.Single(l => l.Currency == StrategyConversionCurrency.Funds);
            Assert.Equal(60.526316, funds.Delta, 6);
        }

        [Fact]
        public void SubThresholdMovements_AreNotCaptured()
        {
            var legs = StrategyConversionCapture.EvaluateLegs(
                Query(dS: 0.0000004, dF: 0.0000009, dR: 0.0000002));

            Assert.Empty(legs);
        }

        [Fact]
        public void ZeroInputReputationDelta_IsEvaluatedAsALeg()
        {
            // The SIGN is preserved through the pure evaluation; what each sign becomes is
            // BuildStrategyConversionAction's decision (positive -> a nominal
            // ReputationEarning, negative -> a nominal ReputationPenalty sourced
            // StrategyConverter). Both are pinned below.
            var legs = StrategyConversionCapture.EvaluateLegs(Query(dR: -3.0));

            var rep = legs.Single(l => l.Currency == StrategyConversionCurrency.Reputation);
            Assert.Equal(-3.0, rep.Delta, 6);
        }

        [Fact]
        public void NonZeroInputReputation_IsCaptured_UnlikeFunds()
        {
            // THE ASYMMETRY, PINNED AS ONE CELL because the two halves are only meaningful
            // together. Reputation is captured at ANY input; funds is captured only at
            // zero input. That is not an inconsistency - it is the decompiled mechanism:
            //
            //   Funding.AddFunds       -> funds += v;  then  funds += GetEffectDelta(Funds)
            //   Reputation.AddReputation -> rep += granular(v);
            //                               then rep += granular(GetEffectDelta(Rep))
            //
            // For FUNDS the ordinary event-driven channel watches the transaction and sees
            // the value net of the modifier, so a row here would double-count. For
            // REPUTATION every Parsek channel records a CONFIGURED NOMINAL - the contract's
            // ReputationCompletion, the progress node's award - and nothing anywhere is
            // derived from the observed pool delta, so the second granular call has no
            // channel at all. Capturing it is the only way the reconstruction can follow.
            //
            // This cell replaces NonZeroInputReputation_IsNotCaptured, which asserted the
            // opposite and was the rule that hid STRATEGY-REP-DEBIT-CONVERTERS-UNCAPTURED:
            // every stock reputation-INPUT converter has GetInput(Reputation) != 0 by
            // construction, so the old rule excluded the entire family. MEASURED live at
            // 1.0000572204589844 reputation of uncaptured pool movement on run
            // 2026-08-20_2052_L3-strategy-currency-conversion.
            var repLegs = StrategyConversionCapture.EvaluateLegs(Query(inR: 10.0, dR: 2.0));
            var rep = repLegs.Single(l => l.Currency == StrategyConversionCurrency.Reputation);
            Assert.Equal(2.0, rep.Delta, 6);

            var fundsLegs = StrategyConversionCapture.EvaluateLegs(Query(inF: 10.0, dF: 2.0));
            Assert.Empty(fundsLegs);
        }

        [Fact]
        public void NonZeroInputNegativeReputation_IsCaptured_TheDebitFamily()
        {
            // The shape every stock reputation-INPUT converter actually produces:
            // FundraisingCampaign / UnpaidResearchProgram compute
            // GetInput(Reputation) * share and write it back as a NEGATIVE delta, so the
            // input is nonzero by construction. This is the exact query the old scoping
            // rule dropped on the floor.
            var legs = StrategyConversionCapture.EvaluateLegs(Query(inR: 20.0, dR: -1.0));

            var rep = legs.Single(l => l.Currency == StrategyConversionCurrency.Reputation);
            Assert.Equal(-1.0, rep.Delta, 6);
        }

        // ================================================================
        // Stand-down
        // ================================================================

        [Fact]
        public void StandsDown_WhenResourceEventsSuppressed()
        {
            string reason;
            Assert.True(StrategyConversionCapture.ShouldStandDown(
                suppressResourceEvents: true, isReplayingActions: false, reason: out reason));
            Assert.Contains("suppressed", reason);
        }

        [Fact]
        public void StandsDown_WhenReplayingActions()
        {
            string reason;
            Assert.True(StrategyConversionCapture.ShouldStandDown(
                suppressResourceEvents: false, isReplayingActions: true, reason: out reason));
            Assert.Contains("replay", reason);
        }

        [Fact]
        public void DoesNotStandDown_OnAnOrdinaryFrame()
        {
            string reason;
            Assert.False(StrategyConversionCapture.ShouldStandDown(
                suppressResourceEvents: false, isReplayingActions: false, reason: out reason));
            Assert.Null(reason);
        }

        // ================================================================
        // Row shapes
        // ================================================================

        [Fact]
        public void ScienceTake_BuildsAConverterSourcedDebit()
        {
            var action = LedgerOrchestrator.BuildStrategyConversionAction(
                123.0,
                new StrategyConversionLeg
                {
                    Currency = StrategyConversionCurrency.Science,
                    Delta = -8.0
                },
                "ScienceTransmission");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.StrategyScienceDebit, action.Type);
            Assert.Equal(8.0, (double)action.Cost, 4);
            Assert.Equal(StrategyConversionSource.Converter, action.ConversionSource);
            // UNTAGGED: this is irreversible global economy, not recording-owned
            // economy, so a discard must not take it back.
            Assert.Null(action.RecordingId);
        }

        [Fact]
        public void ScienceYield_BuildsACreditRow()
        {
            var action = LedgerOrchestrator.BuildStrategyConversionAction(
                123.0,
                new StrategyConversionLeg
                {
                    Currency = StrategyConversionCurrency.Science,
                    Delta = 5.0
                },
                "ContractReward");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.StrategyScienceCredit, action.Type);
            Assert.Equal(5.0, (double)action.ScienceAwarded, 4);
            Assert.Null(action.RecordingId);
        }

        [Fact]
        public void FundsYield_BuildsAStrategyFundsEarning()
        {
            var action = LedgerOrchestrator.BuildStrategyConversionAction(
                123.0,
                new StrategyConversionLeg
                {
                    Currency = StrategyConversionCurrency.Funds,
                    Delta = 60.526316
                },
                "ScienceTransmission");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.FundsEarning, action.Type);
            Assert.Equal(FundsEarningSource.Strategy, action.FundsSource);
            Assert.Equal(60.526316, (double)action.FundsAwarded, 3);
        }

        [Fact]
        public void NegativeFundsLeg_IsRefusedAndWarned()
        {
            var action = LedgerOrchestrator.BuildStrategyConversionAction(
                123.0,
                new StrategyConversionLeg
                {
                    Currency = StrategyConversionCurrency.Funds,
                    Delta = -50.0
                },
                "ScienceTransmission");

            Assert.Null(action);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("NEGATIVE zero-input funds delta"));
        }

        [Fact]
        public void ReputationYield_BuildsAStrategySourcedEarning()
        {
            // THE ROW CARRIES THE RAW PRE-CURVE DELTA, ON PURPOSE. The query delta is the
            // very argument stock's Reputation.OnCurrenciesModified hands to
            // addReputation_granular, and ReputationModule.ApplyReputationCurve mirrors
            // that routine - so NominalRep must be the untouched leg delta and the curve
            // must be left to the walk. A row that pre-applied the curve here would apply
            // it twice, and one that carried a post-curve magnitude would be measured
            // against the LIVE running rep rather than the reconstruction's.
            var action = LedgerOrchestrator.BuildStrategyConversionAction(
                123.0,
                new StrategyConversionLeg
                {
                    Currency = StrategyConversionCurrency.Reputation,
                    Delta = 0.33540129661560059
                },
                "ScienceTransmission");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.ReputationEarning, action.Type);
            Assert.Equal(ReputationSource.Strategy, action.RepSource);
            Assert.Equal(0.33540129661560059f, action.NominalRep);
            // UNTAGGED, like the science / funds siblings on this path.
            Assert.Null(action.RecordingId);
            Assert.Contains(logLines, l =>
                l.Contains("reputation CREDIT captured pre-curve"));
        }

        [Fact]
        public void NegativeReputationLeg_IsANominalStrategyConverterPenalty()
        {
            // THE MIRROR IMAGE of the credit cell above, and the row shape the debit
            // family lands as. NominalPenalty carries the POSITIVE magnitude (the sign is
            // in the action type) and the source is StrategyConverter, NOT Strategy -
            // pinned separately below because conflating the two is the one mistake that
            // would silently break the arithmetic.
            var action = LedgerOrchestrator.BuildStrategyConversionAction(
                123.0,
                new StrategyConversionLeg
                {
                    Currency = StrategyConversionCurrency.Reputation,
                    Delta = -3.0
                },
                "ContractReward");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.ReputationPenalty, action.Type);
            Assert.Equal(ReputationPenaltySource.StrategyConverter, action.RepPenaltySource);
            Assert.Equal(3.0f, action.NominalPenalty);
            Assert.Equal(123.0, action.UT);
            // UNTAGGED, like the science / funds / credit siblings on this path.
            Assert.Null(action.RecordingId);
            Assert.Contains(logLines, l =>
                l.Contains("reputation DEBIT captured pre-curve"));
        }

        [Fact]
        public void NegativeReputationLeg_DoesNotTakeTheExchangersPreCurvedSource()
        {
            // MUTATION GUARD. ReputationPenaltySource.Strategy is the EXCHANGER family's
            // capture: it reads a POST-curve magnitude off a ReputationChanged event and
            // ReputationModule.ProcessRepPenalty gives it a no-recurve shortcut. Routing
            // the query family's PRE-curve delta through it would apply the curve's INPUT
            // as if it were the curve's OUTPUT. This cell reds the moment the two are
            // conflated, in either direction.
            var action = LedgerOrchestrator.BuildStrategyConversionAction(
                123.0,
                new StrategyConversionLeg
                {
                    Currency = StrategyConversionCurrency.Reputation,
                    Delta = -1.0
                },
                "ContractReward");

            Assert.NotEqual(ReputationPenaltySource.Strategy, action.RepPenaltySource);
        }

        [Fact]
        public void StrategyReputationPenaltyRows_DedupKeyDisambiguatesByAmount()
        {
            // Same shape and same two honest limitations as the credit sibling: no
            // strategy identity is available at this seam and the KSC clock is FROZEN, so
            // the pre-curve magnitude is the only disambiguator between two conversions at
            // one UT.
            var first = LedgerOrchestrator.BuildStrategyConversionAction(
                123.0,
                new StrategyConversionLeg
                {
                    Currency = StrategyConversionCurrency.Reputation,
                    Delta = -1.0
                },
                "ContractReward");
            var second = LedgerOrchestrator.BuildStrategyConversionAction(
                123.0,
                new StrategyConversionLeg
                {
                    Currency = StrategyConversionCurrency.Reputation,
                    Delta = -2.0
                },
                "ContractReward");

            Assert.NotEqual(
                LedgerOrchestrator.GetActionKey(first),
                LedgerOrchestrator.GetActionKey(second));

            // A non-converter reputation penalty keeps the historical empty key, so this
            // narrows nothing that was previously unique.
            var unrelated = new GameAction
            {
                UT = 123.0,
                Type = GameActionType.ReputationPenalty,
                NominalPenalty = 1.0f,
                RepPenaltySource = ReputationPenaltySource.KerbalDeath
            };
            Assert.Equal("", LedgerOrchestrator.GetActionKey(unrelated));
        }

        [Fact]
        public void StrategyReputationRows_DedupKeyDisambiguatesByAmount()
        {
            // Mirrors the converter science rows: two conversions at one FROZEN KSC UT
            // must not collapse into one if these rows ever reach DeduplicateAgainstLedger.
            var first = LedgerOrchestrator.BuildStrategyConversionAction(
                123.0,
                new StrategyConversionLeg
                {
                    Currency = StrategyConversionCurrency.Reputation,
                    Delta = 0.25
                },
                "ScienceTransmission");
            var second = LedgerOrchestrator.BuildStrategyConversionAction(
                123.0,
                new StrategyConversionLeg
                {
                    Currency = StrategyConversionCurrency.Reputation,
                    Delta = 0.75
                },
                "ScienceTransmission");

            Assert.NotEqual(
                LedgerOrchestrator.GetActionKey(first),
                LedgerOrchestrator.GetActionKey(second));

            // A non-strategy reputation earning keeps the historical empty key: this
            // change narrows nothing that was previously unique.
            var contractRep = new GameAction
            {
                UT = 123.0,
                Type = GameActionType.ReputationEarning,
                NominalRep = 5f,
                RepSource = ReputationSource.ContractComplete
            };
            Assert.Equal("", LedgerOrchestrator.GetActionKey(contractRep));
        }

        // ================================================================
        // Interaction with the exchanger family's pending-adjustment helper
        // ================================================================

        [Fact]
        public void PendingExchangerDebit_IsNotShrunkByConverterRows()
        {
            // One OBSERVED exchanger debit of 5 (a stored StrategyInput event, still
            // uncommitted) plus a converter row of 3 that has no observed counterpart.
            // Counting the converter row on the committed side would report 2 pending
            // instead of 5 and re-open the clamp this helper prevents.
            var events = new List<GameStateEvent>
            {
                new GameStateEvent
                {
                    ut = 100.0,
                    eventType = GameStateEventType.ScienceChanged,
                    key = "StrategyInput",
                    valueBefore = 50.0,
                    valueAfter = 45.0
                }
            };
            var ledger = new List<GameAction>
            {
                new GameAction
                {
                    UT = 100.0,
                    Type = GameActionType.StrategyScienceDebit,
                    Cost = 3.0f,
                    ConversionSource = StrategyConversionSource.Converter
                }
            };

            double pending = LedgerOrchestrator.ComputePendingUncommittedStrategyScienceDebit(
                events, ledger);

            Assert.Equal(5.0, pending, 4);
        }

        [Fact]
        public void PendingExchangerDebit_StillCancelsAgainstExchangerRows()
        {
            var events = new List<GameStateEvent>
            {
                new GameStateEvent
                {
                    ut = 100.0,
                    eventType = GameStateEventType.ScienceChanged,
                    key = "StrategyInput",
                    valueBefore = 50.0,
                    valueAfter = 45.0
                }
            };
            var ledger = new List<GameAction>
            {
                new GameAction
                {
                    UT = 100.0,
                    Type = GameActionType.StrategyScienceDebit,
                    Cost = 5.0f,
                    ConversionSource = StrategyConversionSource.Exchanger
                }
            };

            Assert.Equal(0.0,
                LedgerOrchestrator.ComputePendingUncommittedStrategyScienceDebit(events, ledger), 4);
        }

        // ================================================================
        // Serialization round-trips
        // ================================================================

        [Fact]
        public void ConverterSourcedDebit_RoundTrips()
        {
            var original = new GameAction
            {
                UT = 42.5,
                Type = GameActionType.StrategyScienceDebit,
                Cost = 8.25f,
                ConversionSource = StrategyConversionSource.Converter
            };

            var parent = new ConfigNode("ACTIONS");
            original.SerializeInto(parent);
            var loaded = GameAction.DeserializeFrom(parent.GetNode("GAME_ACTION"));

            Assert.Equal(GameActionType.StrategyScienceDebit, loaded.Type);
            Assert.Equal(8.25, (double)loaded.Cost, 4);
            Assert.Equal(StrategyConversionSource.Converter, loaded.ConversionSource);
        }

        [Fact]
        public void DebitWithoutAConversionSourceValue_LoadsAsExchanger()
        {
            // Rows written before the query-family door existed carry no
            // conversionSource key. They must keep their original meaning.
            var node = new ConfigNode("GAME_ACTION");
            node.AddValue("ut", "42.5");
            node.AddValue("type", ((int)GameActionType.StrategyScienceDebit).ToString());
            node.AddValue("actionId", "act_legacy");
            node.AddValue("cost", "8.25");

            var loaded = GameAction.DeserializeFrom(node);

            Assert.Equal(StrategyConversionSource.Exchanger, loaded.ConversionSource);
        }

        [Fact]
        public void ScienceCreditRow_RoundTrips()
        {
            var original = new GameAction
            {
                UT = 42.5,
                Type = GameActionType.StrategyScienceCredit,
                ScienceAwarded = 5.5f
            };

            var parent = new ConfigNode("ACTIONS");
            original.SerializeInto(parent);
            var loaded = GameAction.DeserializeFrom(parent.GetNode("GAME_ACTION"));

            Assert.Equal(GameActionType.StrategyScienceCredit, loaded.Type);
            Assert.Equal(5.5, (double)loaded.ScienceAwarded, 4);
        }

        // ================================================================
        // ScienceModule replay
        // ================================================================

        [Fact]
        public void ScienceModule_CreditsAYieldUnconditionally()
        {
            var module = new ScienceModule();
            module.Reset();
            module.ProcessStrategyScienceCredit(new GameAction
            {
                UT = 1.0,
                Type = GameActionType.StrategyScienceCredit,
                ScienceAwarded = 12.0f
            });

            Assert.Equal(12.0, module.GetRunningScience(), 4);
        }

        [Fact]
        public void ScienceModule_IgnoresANonPositiveYield()
        {
            var module = new ScienceModule();
            module.Reset();
            module.ProcessStrategyScienceCredit(new GameAction
            {
                UT = 1.0,
                Type = GameActionType.StrategyScienceCredit,
                ScienceAwarded = 0f
            });

            Assert.Equal(0.0, module.GetRunningScience(), 4);
            Assert.Contains(logLines, l => l.Contains("non-positive credit"));
        }

        // ================================================================
        // Source gates for the recorder wiring (not xUnit-drivable live)
        // ================================================================

        [Fact]
        public void Recorder_SubscribesAndUnsubscribesTheQueryDoorSymmetrically()
        {
            string src = ReadParsekSource("GameStateRecorder.cs");

            Assert.Contains("GameEvents.Modifiers.OnCurrencyModified.Add(OnCurrencyModified);", src);
            Assert.Contains("GameEvents.Modifiers.OnCurrencyModified.Remove(OnCurrencyModified);", src);
            // Instance handler, never a static method: EventData.Add throws for a static
            // target (the static-GameEvent-handler NRE trap).
            Assert.Contains("private void OnCurrencyModified(CurrencyModifierQuery qry)", src);
            Assert.DoesNotContain("private static void OnCurrencyModified(", src);
        }

        [Fact]
        public void Recorder_QueryDoorStandsDownBeforeReadingTheQuery()
        {
            string src = ReadParsekSource("GameStateRecorder.cs");
            int handlerIdx = src.IndexOf(
                "private void OnCurrencyModified(", StringComparison.Ordinal);
            Assert.True(handlerIdx >= 0);

            int standDownIdx = src.IndexOf(
                "StrategyConversionCapture.ShouldStandDown(", handlerIdx, StringComparison.Ordinal);
            int readIdx = src.IndexOf("GetEffectDelta(", handlerIdx, StringComparison.Ordinal);
            Assert.True(standDownIdx >= 0, "the query door must consult the stand-down predicate");
            Assert.True(readIdx >= 0);
            Assert.True(standDownIdx < readIdx,
                "the stand-down check must precede any read of the query");
        }

        [Fact]
        public void Recorder_ScienceCaptureBlockSitsBelowTheThresholdReturn()
        {
            // STRATEGY-ECHO-CAPTURE-WIPE. The converter's zero-delta trailing echo must
            // return at the threshold gate BEFORE the capture block, so it cannot wipe a
            // recovery's reasonKey.
            string src = ReadParsekSource("GameStateRecorder.cs");
            int handlerIdx = src.IndexOf(
                "private void OnScienceChanged(", StringComparison.Ordinal);
            Assert.True(handlerIdx >= 0);

            int thresholdIdx = src.IndexOf(
                "if (IsScienceDeltaBelowThreshold(delta))", handlerIdx, StringComparison.Ordinal);
            int captureIdx = src.IndexOf(
                "latestScienceChangeCapture = new RecentScienceChangeCapture",
                handlerIdx, StringComparison.Ordinal);
            Assert.True(thresholdIdx >= 0);
            Assert.True(captureIdx >= 0);
            Assert.True(thresholdIdx < captureIdx,
                "the below-threshold early return must precede the capture set/clear block");
        }

        [Fact]
        public void InGameCell_DrivesTheConverterFamilyByName()
        {
            string src = ReadParsekSource("InGameTests/RuntimeTests.cs");
            Assert.Contains("CurrencyConverterStrategy_LedgerMatchesNetCredit", src);
            Assert.Contains("PatentsLicensingCfg", src);
            // Exactly one award: two exchanges at the frozen KSC clock share a UT and
            // KscActionExpectationClassifier would WARN falsely.
            int awards = Regex.Matches(
                src, @"AddScience\(Award, TransactionReasons\.ScienceTransmission\)").Count;
            Assert.Equal(1, awards);
        }

        // ================================================================
        // Recalc dispatch: the door writes rows synchronously and defers only the
        // recalc, because OnCurrencyModified fires before KSP has applied the
        // query's output leg (one self-healing GUARDED UPLIFT, run 2026-08-18_2019).
        // ================================================================

        [Fact]
        public void DecideRecalcDispatch_DefersWhenRowsWereWrittenAndAHostExists()
        {
            Assert.Equal(
                StrategyConversionRecalcDispatch.Deferred,
                StrategyConversionCapture.DecideRecalcDispatch(1, hasFrameDeferHost: true));
            Assert.Equal(
                StrategyConversionRecalcDispatch.Deferred,
                StrategyConversionCapture.DecideRecalcDispatch(2, hasFrameDeferHost: true));
        }

        [Fact]
        public void DecideRecalcDispatch_RunsInlineWithNoFrameHostRatherThanDroppingIt()
        {
            Assert.Equal(
                StrategyConversionRecalcDispatch.Immediate,
                StrategyConversionCapture.DecideRecalcDispatch(1, hasFrameDeferHost: false));
        }

        [Fact]
        public void DecideRecalcDispatch_NoRowsMeansNoRecalcInEitherHostState()
        {
            Assert.Equal(
                StrategyConversionRecalcDispatch.None,
                StrategyConversionCapture.DecideRecalcDispatch(0, hasFrameDeferHost: true));
            Assert.Equal(
                StrategyConversionRecalcDispatch.None,
                StrategyConversionCapture.DecideRecalcDispatch(0, hasFrameDeferHost: false));
            Assert.Equal(
                StrategyConversionRecalcDispatch.None,
                StrategyConversionCapture.DecideRecalcDispatch(-1, hasFrameDeferHost: true));
        }

        [Fact]
        public void TheDoorWritesItsRowsBeforeItDecidesHowToDispatchTheRecalc()
        {
            // Capture-loss risk is zero only while Ledger.AddAction precedes the dispatch
            // decision. A refactor that moved the rows behind the defer would put every
            // captured conversion at the mercy of a coroutine that may never resume.
            string src = ReadParsekSource("GameActions/LedgerOrchestrator.cs");
            int addIdx = src.IndexOf("Ledger.AddAction(action);", StringComparison.Ordinal);
            int dispatchIdx = src.IndexOf(
                "StrategyConversionCapture.DecideRecalcDispatch(", StringComparison.Ordinal);
            Assert.True(addIdx > 0, "Ledger.AddAction call not found in LedgerOrchestrator");
            Assert.True(dispatchIdx > 0, "DecideRecalcDispatch call not found in LedgerOrchestrator");
            Assert.True(addIdx < dispatchIdx,
                "the ledger write must precede the recalc dispatch decision");
            Assert.Contains("WarpToTimeConsumer.RunNextFrame(", src);
        }

        // ================================================================
        // THE CAPTURE MATRIX cells (RuntimeTests StrategyLifecycle). The cell BODIES
        // need a live KSP, but the pure helpers they lean on do not - and the log-field
        // parser in particular is load-bearing: the reputation cell asserts on the VALUE
        // of `dR`, so a parser that silently returned 0 would turn that cell's whole
        // subject into a vacuous pass.
        // ================================================================

        [Fact]
        public void QueryLogField_ReadsEveryFieldOfTheDoorsOwnSummaryLine()
        {
            // Built by FormatQuery itself rather than hand-typed, so the parser is
            // pinned against the real producer and a formatting change breaks this
            // cell instead of silently degrading the in-game assertion.
            string line = "[Parsek][INFO][GameStateRecorder] Game state: strategy currency conversion - "
                + StrategyConversionCapture.FormatQuery(new StrategyConversionQuery
                {
                    InputFunds = 0.0,
                    DeltaFunds = 168.12864685058594,
                    InputScience = 40.0,
                    DeltaScience = -2.0,
                    InputReputation = 0.0,
                    DeltaReputation = 0.03354,
                    Reason = "ScienceTransmission"
                }, 2);

            Assert.True(StrategyCells.TryReadStrategyQueryLogField(line, "inS", out double inS));
            Assert.Equal(40.0, inS, 6);
            Assert.True(StrategyCells.TryReadStrategyQueryLogField(line, "dS", out double dS));
            Assert.Equal(-2.0, dS, 6);
            Assert.True(StrategyCells.TryReadStrategyQueryLogField(line, "dF", out double dF));
            Assert.Equal(168.12864685058594, dF, 6);
            Assert.True(StrategyCells.TryReadStrategyQueryLogField(line, "dR", out double dR));
            Assert.Equal(0.03354, dR, 6);
            Assert.True(StrategyCells.TryReadStrategyQueryLogField(line, "legs", out double legs));
            Assert.Equal(2.0, legs, 6);
        }

        [Fact]
        public void QueryLogField_DoesNotConfuseAPrefixFieldWithItsLongerSibling()
        {
            // `inS` is a substring of nothing here, but `dR` IS a substring of no other
            // token only because FormatQuery happens to order them that way. The parser
            // matches "<field>=", so the guard that matters is that a field name which is
            // a PREFIX of another key cannot steal its value.
            string line = "reason=X inF=1 dF=2 inS=3 dS=4 inR=5 dR=6 legs=7";
            Assert.True(StrategyCells.TryReadStrategyQueryLogField(line, "inF", out double inF));
            Assert.Equal(1.0, inF, 6);
            Assert.True(StrategyCells.TryReadStrategyQueryLogField(line, "inR", out double inR));
            Assert.Equal(5.0, inR, 6);
        }

        [Fact]
        public void QueryLogField_FailsClosedRatherThanReturningZero()
        {
            // The in-game cell asserts |dR| > the capture floor. A parser that returned
            // `true` with 0 on a missing or malformed field would make that assertion
            // fail for the wrong reason; one that returned `false` lets the cell say so.
            Assert.False(StrategyCells.TryReadStrategyQueryLogField(
                "reason=X inF=1 legs=1", "dR", out _));
            Assert.False(StrategyCells.TryReadStrategyQueryLogField(null, "dR", out _));
            Assert.False(StrategyCells.TryReadStrategyQueryLogField("dR=", "dR", out _));
            Assert.False(StrategyCells.TryReadStrategyQueryLogField("dR=notanumber", "dR", out _));
            Assert.False(StrategyCells.TryReadStrategyQueryLogField("reason=X", null, out _));
        }

        [Fact]
        public void QueryLogField_ReadsTheInvariantCultureRoundTripFormTheDoorWrites()
        {
            // Every numeric the door writes goes through ToString("R", InvariantCulture),
            // so the parser must accept exponent form and a leading sign, and must NOT be
            // culture-sensitive (a comma-locale machine would otherwise mis-read a point).
            Assert.True(StrategyCells.TryReadStrategyQueryLogField(
                "dR=7.8053E-05 legs=1", "dR", out double small));
            Assert.Equal(7.8053E-05, small, 12);
            Assert.True(StrategyCells.TryReadStrategyQueryLogField(
                "dF=-60.525981 legs=1", "dF", out double negative));
            Assert.Equal(-60.525981, negative, 6);
        }

        [Fact]
        public void GuardedClampLine_MatchesBothDirectionsAndNamesTheResource()
        {
            // Shaped exactly like KspStatePatcher.EmitDrawdownGuardClamp writes them.
            string drawdown = "[Parsek][WRN][KspStatePatcher] PatchScience: GUARDED DRAWDOWN clamped "
                + "resource=Science running=-163.25 live=36.75 wouldBeTarget=-163.25 clampedTo=36.75 "
                + "(no time-travel context) - earned value preserved; ledger may be missing an earning channel";
            string uplift = "[Parsek][WRN][KspStatePatcher] PatchFunds: GUARDED UPLIFT clamped "
                + "resource=Funds running=500168.13 live=500000 wouldBeTarget=500168.13 clampedTo=500000 "
                + "(no time-travel context) - spent value held; ledger may be missing a spending channel";
            string repClamp = "[Parsek][WRN][KspStatePatcher] PatchReputation: GUARDED DRAWDOWN clamped "
                + "resource=Reputation running=0 live=0.03354 wouldBeTarget=0 clampedTo=0.03354 "
                + "(no time-travel context) - earned value preserved; ledger may be missing an earning channel";

            Assert.True(StrategyCells.IsGuardedClampLine(drawdown));
            Assert.True(StrategyCells.IsGuardedClampLine(uplift));
            Assert.True(StrategyCells.IsGuardedClampLine(repClamp));

            // The resource discriminator is what lets the reputation cell tell "the
            // restore did not take" apart from "a door missed a leg".
            Assert.True(StrategyCells.IsGuardedClampLineFor(repClamp, "Reputation"));
            Assert.False(StrategyCells.IsGuardedClampLineFor(repClamp, "Science"));
            Assert.True(StrategyCells.IsGuardedClampLineFor(drawdown, "Science"));
            Assert.False(StrategyCells.IsGuardedClampLineFor(drawdown, "Funds"));
        }

        [Fact]
        public void TeardownTruncate_RaisesOnlyWhenTheLedgerShrankBelowTheCapturedBaseline()
        {
            // The teardown idiom captures Ledger.Actions.Count on entry and truncates back
            // to it in the finally, which is only correct while every row the cell added
            // sits at the TAIL. A live count BELOW the captured one says rows were removed
            // by something outside the cell, so the captured index no longer names the
            // cell's own boundary - the case the in-game guard WARNs on and skips.
            Assert.True(StrategyCells.TeardownTruncateWouldRaceRemoval(10, 9));
            Assert.True(StrategyCells.TeardownTruncateWouldRaceRemoval(1, 0));

            // Equal is the ordinary "cell added nothing" teardown and MUST stay a silent
            // no-op; greater is the ordinary "cell added rows" teardown. Neither is a race.
            Assert.False(StrategyCells.TeardownTruncateWouldRaceRemoval(10, 10));
            Assert.False(StrategyCells.TeardownTruncateWouldRaceRemoval(10, 14));
            Assert.False(StrategyCells.TeardownTruncateWouldRaceRemoval(0, 0));
        }

        [Fact]
        public void GuardedClampLine_IgnoresOrdinaryLinesAndNulls()
        {
            Assert.False(StrategyCells.IsGuardedClampLine(null));
            Assert.False(StrategyCells.IsGuardedClampLine(""));
            Assert.False(StrategyCells.IsGuardedClampLine(
                "[Parsek][INFO][KspStatePatcher] PatchScience: resource=Science running=10 live=10"));
            // A line naming the resource but carrying no clamp is not a clamp.
            Assert.False(StrategyCells.IsGuardedClampLineFor(
                "[Parsek][INFO][X] resource=Reputation", "Reputation"));
            Assert.False(StrategyCells.IsGuardedClampLineFor(
                "PatchFunds: GUARDED UPLIFT clamped resource=Funds", null));
        }

        [Fact]
        public void InGameCells_CoverTheWholeCaptureMatrixByName()
        {
            // The matrix is only as good as the strategies it actually drives, and each
            // one is picked BY Config.Name precisely so the choice is reviewable here
            // rather than left to whatever the readiness probe stabilizes on.
            string src = ReadParsekSource("InGameTests/RuntimeTests.cs");

            Assert.Contains("ExchangerStrategy_OneShot_CapturesBothLegs", src);
            Assert.Contains("researchIPsellout", src);

            Assert.Contains("ConverterStrategy_ScienceYield_CapturesCredit", src);
            Assert.Contains("OutsourcedResearchCfg", src);

            Assert.Contains("OperationStrategy_RewardMultiplier_IsNotCaptured", src);
            Assert.Contains("LeadershipInitiative", src);

            Assert.Contains("ConverterStrategy_ReputationLeg_CapturesEarning", src);
            Assert.Contains("OpenSourceTechProgramCfg", src);
        }

        [Fact]
        public void InGameCells_EachFireTheirExchangeExactlyOnce()
        {
            // Two exchanges at the frozen KSC clock share a UT, and
            // KscActionExpectationClassifier would then see -2*cost against a -cost
            // expectation and WARN falsely. One driving transaction per cell, counted
            // from the source because the cells are not xUnit-drivable.
            string src = ReadParsekSource("InGameTests/RuntimeTests.cs");

            int repYieldAwards = Regex.Matches(
                src, @"AddScience\(RepYieldAward, TransactionReasons\.ScienceTransmission\)").Count;
            int contractRewardAwards = Regex.Matches(
                src, @"AddFunds\(FundsAward, TransactionReasons\.ContractReward\)").Count;
            int progressionAwards = Regex.Matches(
                src, @"AddFunds\(FundsAward, TransactionReasons\.Progression\)").Count;

            Assert.Equal(1, repYieldAwards);
            Assert.Equal(1, contractRewardAwards);
            Assert.Equal(1, progressionAwards);
        }

        [Fact]
        public void InGameCells_PairEveryLedgerInvisiblePoolMoveWithARow()
        {
            // THE 2026-08-18_2019 LESSON, locked. Funds and reputation have no pending
            // adjuster on the guard's discriminator, so a bare pool move clamps on the
            // next recalc. Both fixture-row helpers must exist and must use the action
            // types that credit unconditionally.
            string src = ReadParsekSource("InGameTests/RuntimeTests.cs");

            Assert.Contains("WriteLedgerVisibleFundsRow", src);
            Assert.Contains("WriteLedgerVisibleScienceTopUp", src);
            // FundsEarningSource.Strategy, not Other: Other credits identically but then
            // WARNs "no matching FundsChanged event keyed 'Other'" on every recalc.
            Assert.Contains("FundsSource = FundsEarningSource.Strategy", src);

            Assert.Contains("WriteLedgerVisibleReputationRow", src);

            // EVERY FUNDS / SCIENCE fixture row this category writes is stamped in the
            // PAST, so none of them sits inside the 0.1s window the KSC reconcilers pair
            // against - at the frozen KSC clock a row at "now" would net against the very
            // award under test. Counted as an EQUALITY over the StrategyLifecycle region
            // rather than against a hardcoded number, so a cell that adds a fixture row is
            // checked rather than merely changing a total.
            //
            // REPUTATION IS THE ONE DELIBERATE EXCEPTION, and it is asserted POSITIVELY
            // rather than carved out of the count. Reputation has no KSC pairing window
            // and no pending adjuster, so there is nothing for a backdate to protect it
            // from; what it DOES have is a state-dependent curve, which makes ORDER decide
            // the arithmetic. A backdated reputation row sorts ahead of the strategy's own
            // StrategyActivate setup charge and the walk applies the award at the
            // pre-charge reputation - measured ~0.002 off KSP's pool, a quarter of the
            // reputation guard's 0.01 epsilon spent on a stamping artefact. At "now" it
            // sorts after the activation and, at the same UT as the conversion's debit
            // row, ahead of it via SortActions' earnings-before-spendings secondary key,
            // which is the order stock applied them in.
            string region = StrategyLifecycleRegion(src);
            int fixtureRows = Regex.Matches(region, @"Ledger\.AddAction\(new GameAction").Count;
            int pastStamped = Regex.Matches(
                region, @"UT = Planetarium\.GetUniversalTime\(\) - 1\.0").Count;
            int nowStamped = Regex.Matches(
                region, @"UT = Planetarium\.GetUniversalTime\(\),").Count;
            Assert.True(fixtureRows > 0, "the StrategyLifecycle region writes no fixture ledger row at all");
            Assert.Equal(fixtureRows, pastStamped + nowStamped);

            // Exactly one now-stamped row, and it must be the reputation one: a funds or
            // science helper that stopped backdating would land in this count and red
            // here rather than silently netting itself against its own award.
            Assert.Equal(1, nowStamped);
            int repHelperStart = src.IndexOf(
                "private static void WriteLedgerVisibleReputationRow", StringComparison.Ordinal);
            Assert.True(repHelperStart >= 0,
                "WriteLedgerVisibleReputationRow has moved or been renamed");
            int repHelperEnd = src.IndexOf("private static void", repHelperStart + 20, StringComparison.Ordinal);
            Assert.True(repHelperEnd > repHelperStart, "cannot bound WriteLedgerVisibleReputationRow");
            string repHelper = src.Substring(repHelperStart, repHelperEnd - repHelperStart);
            Assert.Contains("UT = Planetarium.GetUniversalTime(),", repHelper);
            Assert.Contains("ReputationSource.Other", repHelper);
        }

        /// <summary>
        /// The text of the <c>#region StrategyLifecycle</c> block, so a source gate can
        /// assert over THIS category without matching the other test classes that share
        /// RuntimeTests.cs.
        /// </summary>
        private static string StrategyLifecycleRegion(string src)
        {
            int start = src.IndexOf("#region StrategyLifecycle", StringComparison.Ordinal);
            Assert.True(start >= 0, "the #region StrategyLifecycle marker has moved or been renamed");
            int end = src.IndexOf("#endregion", start, StringComparison.Ordinal);
            Assert.True(end > start, "the StrategyLifecycle region has no closing #endregion");
            return src.Substring(start, end - start);
        }

        private static string ReadParsekSource(string relPath)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(
                root, "Source", "Parsek", relPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                path = Path.Combine(root, "Parsek", relPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "Source file not found at " + path);
            return File.ReadAllText(path);
        }
    }
}
