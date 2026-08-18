using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

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
            // Evaluated (so the observation is testable and the asymmetry explicit);
            // NOT turned into a row - see BuildStrategyConversionAction below.
            var legs = StrategyConversionCapture.EvaluateLegs(Query(dR: -3.0));

            var rep = legs.Single(l => l.Currency == StrategyConversionCurrency.Reputation);
            Assert.Equal(-3.0, rep.Delta, 6);
        }

        [Fact]
        public void NonZeroInputReputation_IsNotCaptured()
        {
            var legs = StrategyConversionCapture.EvaluateLegs(Query(inR: 10.0, dR: 2.0));

            Assert.Empty(legs);
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
        public void ReputationLeg_IsObservedButNotWritten()
        {
            var action = LedgerOrchestrator.BuildStrategyConversionAction(
                123.0,
                new StrategyConversionLeg
                {
                    Currency = StrategyConversionCurrency.Reputation,
                    Delta = -3.0
                },
                "ContractReward");

            Assert.Null(action);
            Assert.Contains(logLines, l => l.Contains("pre-curve"));
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
