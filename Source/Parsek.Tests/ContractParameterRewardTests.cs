using System;
using System.Collections.Generic;
using System.Linq;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// CONTRACT-PARAMETER-REWARDS-NOT-LEDGERED: stock pays each completed contract parameter its
    /// own FundsCompletion / ScienceCompletion / ReputationCompletion (reason ContractReward)
    /// before the contract pays its contract-level reward. These cells cover the capture core,
    /// the converter, the codec, the walk (funds, science pool, reputation curve, once per
    /// parameter), the supersede tombstone, the discard re-home and the display. The numbers are
    /// CS-3's measured World-Firsts payouts (run 2026-10-25_2353): FIRSTLAUNCH paid 3200 / 1 / 1
    /// for its "Achieve goal" parameter and 2080 / 1 / 1 for the contract; SCIENCE 6400 / 1 / 2
    /// and 4160 / 1 / 2.
    /// </summary>
    [Collection("Sequential")]
    public class ContractParameterRewardTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        private const string FirstLaunch = "a5a1f217-d79b-4ef9-8122-f051ecdd1681";
        private const string Science = "6b2c9e40-1d7a-4f3e-9c55-2b8e1f0a7d33";

        public ContractParameterRewardTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateStore.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            LedgerOrchestrator.SetResourceTrackingAvailabilityForTesting(true, true, true);
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            ContractParameterRewardCapturePatch.ResetForTesting();
            RecalculationEngine.ClearModules();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            ContractParameterRewardCapturePatch.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            KspStatePatcher.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static List<GameStateEvent> ParameterEvents()
        {
            return GameStateStore.Events
                .Where(e => e.eventType == GameStateEventType.ContractParameterCompleted).ToList();
        }

        private static List<GameAction> ParameterRows()
        {
            return Ledger.Actions
                .Where(a => a.Type == GameActionType.ContractParameterComplete).ToList();
        }

        private static GameAction ParameterRow(string contractId, string paramId, double ut,
            float funds, float sci, float rep, string recordingId = null)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ContractParameterComplete,
                RecordingId = recordingId,
                ContractId = contractId,
                ContractParameterId = paramId,
                FundsReward = funds,
                ScienceReward = sci,
                RepReward = rep
            };
        }

        private static GameAction ContractRow(string contractId, double ut, float funds, float sci, float rep)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ContractComplete,
                ContractId = contractId,
                FundsReward = funds,
                ScienceReward = sci,
                RepReward = rep
            };
        }

        // ================================================================
        // Capture core
        // ================================================================

        // catches: the defect itself - a parameter's paid reward reaching no ledger row.
        [Fact]
        public void Capture_NonzeroReward_Untagged_FilesOneEventAndOneLedgerRow()
        {
            new GameStateRecorder().CaptureContractParameterCompleted(
                FirstLaunch, "0", "Launch our first vessel!", 3200f, 1f, 1f, 4.8);

            var stored = ParameterEvents();
            Assert.Single(stored);
            Assert.Equal(FirstLaunch, stored[0].key);
            Assert.Equal("", stored[0].recordingId ?? "");

            var rows = ParameterRows();
            Assert.Single(rows);
            Assert.Equal(FirstLaunch, rows[0].ContractId);
            Assert.Equal("0", rows[0].ContractParameterId);
            Assert.Equal("Launch our first vessel!", rows[0].ContractTitle);
            Assert.Equal(3200f, rows[0].FundsReward);
            Assert.Equal(1f, rows[0].ScienceReward);
            Assert.Equal(1f, rows[0].RepReward);
            Assert.True(string.IsNullOrEmpty(rows[0].RecordingId));
            // The parameter row never stands in for the contract-level row.
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.ContractComplete);
            Assert.Contains(logLines, l => l.Contains("[GameStateRecorder]")
                && l.Contains("ContractParameterCompleted 'Launch our first vessel!' param=0")
                && l.Contains("funds=3200"));
        }

        // catches: a zero-reward parameter (most stock sub-objectives of a reward-on-contract
        // contract) filing an empty row on every completion.
        [Fact]
        public void Capture_ZeroReward_FilesNothing()
        {
            new GameStateRecorder().CaptureContractParameterCompleted(
                FirstLaunch, "1", "Launch our first vessel!", 0f, 0f, 0f, 4.8);

            Assert.Empty(ParameterEvents());
            Assert.Empty(ParameterRows());
            Assert.Contains(logLines, l => l.Contains("[GameStateRecorder]")
                && l.Contains("param=1 paid no reward - not recorded"));
        }

        [Theory]
        [InlineData(null, "0", "contract-guid-missing")]
        [InlineData("", "0", "contract-guid-missing")]
        [InlineData(FirstLaunch, null, "parameter-id-missing")]
        [InlineData(FirstLaunch, "", "parameter-id-missing")]
        public void Capture_MissingIdentity_IsRejected(string guid, string paramId, string reason)
        {
            new GameStateRecorder().CaptureContractParameterCompleted(
                guid, paramId, "x", 100f, 0f, 0f, 4.8);

            Assert.Empty(ParameterEvents());
            Assert.Contains(logLines, l => l.Contains("ContractParameterCompleted") && l.Contains(reason));
        }

        // catches: a duplicate stock instance (the restored-copy class of defect) paying one
        // parameter twice into the ledger.
        [Fact]
        public void Capture_SameParameterWithinWindow_IsDeduped()
        {
            var recorder = new GameStateRecorder();
            recorder.CaptureContractParameterCompleted(FirstLaunch, "0", "t", 3200f, 1f, 1f, 4.8);
            recorder.CaptureContractParameterCompleted(FirstLaunch, "0", "t", 3200f, 1f, 1f, 4.8);

            Assert.Single(ParameterEvents());
            Assert.Single(ParameterRows());
            Assert.Contains(logLines, l => l.Contains("ContractParameterCompleted 't' param=0 DEDUPED"));
        }

        // catches: a dedup keyed on the contract guid alone, which would drop the second of two
        // parameters one contract completes in the same frame.
        [Fact]
        public void Capture_TwoParametersOfOneContractSameUt_BothFiled()
        {
            var recorder = new GameStateRecorder();
            recorder.CaptureContractParameterCompleted(FirstLaunch, "0", "t", 1000f, 0f, 0f, 50.0);
            recorder.CaptureContractParameterCompleted(FirstLaunch, "1", "t", 2000f, 0f, 0f, 50.0);

            Assert.Equal(2, ParameterEvents().Count);
            var rows = ParameterRows();
            Assert.Equal(2, rows.Count);
            Assert.Equal(new[] { "0", "1" }, rows.Select(r => r.ContractParameterId).OrderBy(s => s).ToArray());
        }

        // catches: the parameter sharing the contract-level dedup map, so the contract's own
        // completion right after its parameter (same guid, same UT) is swallowed - or the
        // reverse.
        [Fact]
        public void Capture_ParameterThenContractSameUt_BothFiled()
        {
            var recorder = new GameStateRecorder();
            recorder.CaptureContractParameterCompleted(FirstLaunch, "0", "Launch our first vessel!", 3200f, 1f, 1f, 4.8);
            recorder.CaptureContractCompleted(FirstLaunch, "Launch our first vessel!", 2080f, 1f, 1f, 4.8);

            Assert.Single(ParameterEvents());
            Assert.Single(GameStateStore.Events, e => e.eventType == GameStateEventType.ContractCompleted);
            Assert.Single(ParameterRows());
            Assert.Single(Ledger.Actions, a => a.Type == GameActionType.ContractComplete);
            Assert.DoesNotContain(logLines, l => l.Contains("DEDUPED"));
        }

        [Fact]
        public void ClearContractCompletionDedup_ClearsTheParameterMapToo()
        {
            new GameStateRecorder().CaptureContractParameterCompleted(FirstLaunch, "0", "t", 5f, 0f, 0f, 1.0);
            Assert.NotEmpty(GameStateRecorder.lastContractParameterCompletionUtByKey);

            GameStateRecorder.ClearContractCompletionDedup("test");

            Assert.Empty(GameStateRecorder.lastContractParameterCompletionUtByKey);
            Assert.Empty(GameStateRecorder.lastContractCompletionUtByGuid);
        }

        // catches: a flight's parameter reward written straight to the ledger, which a discard
        // or commit would then count a second time.
        [Fact]
        public void Capture_WithALiveRecorder_IsTaggedAndWaitsForTheCommit()
        {
            GameStateRecorder.TagResolverForTesting = () => "rec-live";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;
            GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = () => true;

            new GameStateRecorder().CaptureContractParameterCompleted(FirstLaunch, "0", "t", 3200f, 1f, 1f, 4.8);

            var stored = ParameterEvents();
            Assert.Single(stored);
            Assert.Equal("rec-live", stored[0].recordingId);
            Assert.Empty(ParameterRows());
        }

        [Fact]
        public void Capture_InsideAClockCauseScope_IsUntaggedAndInTheLedgerAtOnce()
        {
            GameStateRecorder.TagResolverForTesting = () => "rec-live";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;
            GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = () => true;
            GameStateRecorder.EnterClockCauseScope("test ground station");
            try
            {
                new GameStateRecorder().CaptureContractParameterCompleted(
                    FirstLaunch, "0", "Collect seismic data", 900f, 0f, 0f, 500.0);
            }
            finally
            {
                GameStateRecorder.ExitClockCauseScope("test ground station");
            }

            Assert.Equal("", ParameterEvents().Single().recordingId ?? "");
            Assert.Single(ParameterRows());
            Assert.Contains(logLines, l => l.Contains("ContractParameterCompleted 'Collect seismic data'")
                && l.Contains("completed by no flight, filed untagged"));
        }

        [Fact]
        public void Report_RaisesTheSubscribedHandler_AndLogsWithNone()
        {
            ContractParameterRewardCapturePatch.Report(new ContractParameterRewardPaid
            {
                ContractGuid = FirstLaunch, ParameterId = "0", Captured = true
            });
            Assert.Contains(logLines, l => l.Contains("[ContractParameterRewardCapture]")
                && l.Contains("no subscribed recorder"));

            ContractParameterRewardPaid seen = default(ContractParameterRewardPaid);
            ContractParameterRewardCapturePatch.RewardPaid += p => seen = p;
            ContractParameterRewardCapturePatch.Report(new ContractParameterRewardPaid
            {
                ContractGuid = FirstLaunch, ParameterId = "1.0", FundsReward = 7f, Captured = true
            });
            Assert.Equal("1.0", seen.ParameterId);
            Assert.Equal(7f, seen.FundsReward);
        }

        // catches: the target method name drifting, which Harmony would skip silently and leave
        // every parameter reward unbooked again.
        [Fact]
        public void PatchTarget_ResolvesAgainstStock()
        {
            var method = HarmonyLib.AccessTools.Method(typeof(Contracts.ContractParameter), "AwardCompletion", Type.EmptyTypes);
            Assert.NotNull(method);
            Assert.Equal(typeof(Contracts.ContractParameter), method.DeclaringType);
        }

        [Theory]
        [InlineData(new[] { 0 }, "0")]
        [InlineData(new[] { 2 }, "2")]
        [InlineData(new[] { 1, 0 }, "1.0")]
        [InlineData(new[] { 0, 3, 12 }, "0.3.12")]
        [InlineData(new int[0], null)]
        [InlineData(new[] { 0, -1 }, null)]
        public void BuildContractParameterIndexPath_JoinsRootFirstIndices(int[] indices, string expected)
        {
            Assert.Equal(expected, GameStateRecorder.BuildContractParameterIndexPath(indices));
        }

        [Fact]
        public void BuildContractParameterIndexPath_Null_IsNull()
        {
            Assert.Null(GameStateRecorder.BuildContractParameterIndexPath(null));
        }

        // ================================================================
        // Converter + codec
        // ================================================================

        [Fact]
        public void Converter_ReadsParameterIdAndRewards_InvariantCulture()
        {
            var prior = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    System.Globalization.CultureInfo.GetCultureInfo("de-DE");
                var evt = new GameStateEvent
                {
                    ut = 345.9,
                    eventType = GameStateEventType.ContractParameterCompleted,
                    key = Science,
                    detail = GameStateRecorder.BuildContractParameterCompletedDetail(
                        "0", "Gather scientific data; from Kerbin.", 6400.5f, 2f, 1.25f)
                };

                var a = GameStateEventConverter.ConvertEvent(evt, "rec-1");

                Assert.NotNull(a);
                Assert.Equal(GameActionType.ContractParameterComplete, a.Type);
                Assert.Equal(Science, a.ContractId);
                Assert.Equal("0", a.ContractParameterId);
                Assert.Equal("rec-1", a.RecordingId);
                Assert.Equal(6400.5f, a.FundsReward);
                Assert.Equal(2f, a.RepReward);
                Assert.Equal(1.25f, a.ScienceReward);
                Assert.Equal(345.9, a.UT);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = prior;
            }
        }

        [Fact]
        public void Converter_NoParameterId_IsSkipped()
        {
            var evt = new GameStateEvent
            {
                ut = 1.0,
                eventType = GameStateEventType.ContractParameterCompleted,
                key = Science,
                detail = "fundsReward=5;repReward=0;sciReward=0;title=t"
            };

            Assert.Null(GameStateEventConverter.ConvertEvent(evt, null));
            Assert.Contains(logLines, l => l.Contains("ConvertContractParameterCompleted") && l.Contains("no parameter id"));
        }

        // catches: the commit-time batch dedup keyed on (type, contract, UT) alone collapsing two
        // parameters of one contract into one row, while still collapsing a true re-fire.
        [Fact]
        public void ConvertEvents_TwoParametersKept_SameParameterReFireDropped()
        {
            var events = new List<GameStateEvent>();
            foreach (string p in new[] { "0", "1", "1" })
            {
                events.Add(new GameStateEvent
                {
                    ut = 100.0,
                    eventType = GameStateEventType.ContractParameterCompleted,
                    key = FirstLaunch,
                    recordingId = "rec-1",
                    detail = GameStateRecorder.BuildContractParameterCompletedDetail(p, "t", 10f, 0f, 0f)
                });
            }

            var actions = GameStateEventConverter.ConvertEvents(events, "rec-1", 0.0, 200.0);

            Assert.Equal(2, actions.Count);
            Assert.Equal(new[] { "0", "1" }, actions.Select(a => a.ContractParameterId).ToArray());
        }

        [Fact]
        public void Codec_RoundTrip_KeepsParameterIdAndRewards()
        {
            var original = ParameterRow(FirstLaunch, "1.0", 4.8, 3200f, 1f, 1f, "rec-1");
            original.ContractTitle = "Launch our first vessel!";
            var parent = new ConfigNode("LEDGER");
            original.SerializeInto(parent);
            ConfigNode node = parent.GetNode("GAME_ACTION");
            Assert.Equal("40", node.GetValue("type"));
            Assert.Equal("1.0", node.GetValue(GameAction.ContractParameterIdKey));

            var back = GameAction.DeserializeFrom(node);

            Assert.Equal(GameActionType.ContractParameterComplete, back.Type);
            Assert.Equal(FirstLaunch, back.ContractId);
            Assert.Equal("1.0", back.ContractParameterId);
            Assert.Equal("Launch our first vessel!", back.ContractTitle);
            Assert.Equal(3200f, back.FundsReward);
            Assert.Equal(1f, back.ScienceReward);
            Assert.Equal(1f, back.RepReward);
            Assert.Equal("rec-1", back.RecordingId);
            Assert.Equal(original.ActionId, back.ActionId);
        }

        // catches: the parameter key leaking onto the contract-level row (a shape change for
        // every existing ContractComplete row).
        [Fact]
        public void Codec_ContractComplete_WritesNoParameterKey()
        {
            var parent = new ConfigNode("LEDGER");
            ContractRow(FirstLaunch, 4.8, 2080f, 1f, 1f).SerializeInto(parent);
            Assert.False(parent.GetNode("GAME_ACTION").HasValue(GameAction.ContractParameterIdKey));
        }

        [Fact]
        public void EnumValues_AreAppendOnly()
        {
            Assert.Equal(40, (int)GameActionType.ContractParameterComplete);
            Assert.Equal(26, (int)GameStateEventType.ContractParameterCompleted);
        }

        // ================================================================
        // The walk
        // ================================================================

        private sealed class Walk
        {
            internal ContractsModule Contracts = new ContractsModule();
            internal FundsModule Funds = new FundsModule();
            internal ScienceModule Science = new ScienceModule();
            internal ReputationModule Rep = new ReputationModule();
        }

        private static Walk RegisterWalk()
        {
            var w = new Walk();
            RecalculationEngine.RegisterModule(w.Contracts, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(w.Funds, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(w.Science, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(w.Rep, RecalculationEngine.ModuleTier.SecondTier);
            return w;
        }

        private static List<GameAction> Seeds()
        {
            return new List<GameAction>
            {
                new GameAction { UT = 0, Type = GameActionType.FundsInitial, InitialFunds = 25000f },
                new GameAction { UT = 0, Type = GameActionType.ScienceInitial, InitialScience = 0f },
                new GameAction { UT = 0, Type = GameActionType.ReputationInitial, InitialReputation = 0f },
            };
        }

        // catches: the CS-3 gap (+9600 funds, +2 science, +3 rep) - a contract and its parameter
        // completing at one UT must credit BOTH amounts.
        [Fact]
        public void Walk_ContractAndItsParameter_CreditBothAmounts_CS3Numbers()
        {
            var w = RegisterWalk();
            var actions = Seeds();
            actions.Add(ParameterRow(FirstLaunch, "0", 4.8, 3200f, 1f, 1f));
            actions.Add(ContractRow(FirstLaunch, 4.8, 2080f, 1f, 1f));
            actions.Add(ParameterRow(Science, "0", 345.9, 6400f, 1f, 2f));
            actions.Add(ContractRow(Science, 345.9, 4160f, 1f, 2f));

            RecalculationEngine.Recalculate(actions);

            Assert.Equal(25000.0 + 3200 + 2080 + 6400 + 4160, w.Funds.GetRunningBalance(), 3);
            Assert.Equal(4.0, w.Science.GetRunningScience(), 3);
            // Nominal 1 + 1 + 2 + 2 through stock's curve near zero rep: within a few
            // hundredths of the nominal 6 (stock logged 0.9999995 for a nominal 1).
            Assert.InRange(w.Rep.GetRunningRep(), 5.9f, 6.0f);
            Assert.All(actions.Where(a => a.Type == GameActionType.ContractParameterComplete),
                a => Assert.True(a.Effective));
            Assert.All(actions.Where(a => a.Type == GameActionType.ContractParameterComplete),
                a => Assert.True(a.EffectiveRep > 0.9f));
        }

        // catches: the parameter reward being counted through the science SUBJECT path (capped
        // to zero with no subject) instead of as a flat pool credit.
        [Fact]
        public void Walk_ParameterScience_IsAFlatPoolCredit()
        {
            var w = RegisterWalk();
            var actions = Seeds();
            actions.Add(ParameterRow(Science, "0", 10.0, 0f, 7.5f, 0f));

            RecalculationEngine.Recalculate(actions);

            Assert.Equal(7.5, w.Science.GetRunningScience(), 3);
            Assert.True(ScienceModule.TryGetRowDelta(actions.Last(), out double delta));
            Assert.Equal(7.5, delta, 3);
            Assert.True(FundsModule.AffectsFunds(GameActionType.ContractParameterComplete));
            Assert.True(ScienceModule.AffectsScience(GameActionType.ContractParameterComplete));
            Assert.True(ReputationModule.AffectsReputation(GameActionType.ContractParameterComplete));
            Assert.True(RecalculationEngine.IsEarningType(GameActionType.ContractParameterComplete));
        }

        // catches: two rows for one (contract, parameter) - a re-fire that slipped every dedup -
        // crediting twice, while a sibling parameter still credits.
        [Fact]
        public void Walk_SameParameterTwice_CreditedOnce_SiblingCredited()
        {
            var w = RegisterWalk();
            var actions = Seeds();
            var first = ParameterRow(FirstLaunch, "0", 10.0, 1000f, 0f, 0f);
            var again = ParameterRow(FirstLaunch, "0", 20.0, 1000f, 0f, 0f);
            var sibling = ParameterRow(FirstLaunch, "1", 20.0, 500f, 0f, 0f);
            actions.Add(first);
            actions.Add(again);
            actions.Add(sibling);

            RecalculationEngine.Recalculate(actions);

            Assert.True(first.Effective);
            Assert.False(again.Effective);
            Assert.Equal(GameActionNotCountedReason.ContractAlreadyCompleted, again.NotCountedReason);
            Assert.True(sibling.Effective);
            Assert.Equal(25000.0 + 1000 + 500, w.Funds.GetRunningBalance(), 3);
            Assert.Contains(logLines, l => l.Contains("[Contracts]")
                && l.Contains("ParameterComplete: contractId='" + FirstLaunch + "' param='0' effective=false (already credited)"));
        }

        // catches: a parameter row taking or freeing a contract slot, or marking the contract
        // terminal (which would block its real completion).
        [Fact]
        public void Walk_Parameter_LeavesTheContractSlotAndTerminalStateAlone()
        {
            var w = RegisterWalk();
            var actions = Seeds();
            actions.Add(new GameAction
            {
                UT = 1.0, Type = GameActionType.ContractAccept, ContractId = FirstLaunch, ContractTitle = "t"
            });
            actions.Add(ParameterRow(FirstLaunch, "0", 4.8, 3200f, 1f, 1f));

            RecalculationEngine.Recalculate(actions);

            Assert.Equal(1, w.Contracts.GetActiveContractCount());
            Assert.False(w.Contracts.IsContractCredited(FirstLaunch));

            actions.Add(ContractRow(FirstLaunch, 4.8, 2080f, 1f, 1f));
            RecalculationEngine.Recalculate(actions);

            Assert.Equal(0, w.Contracts.GetActiveContractCount());
            Assert.True(w.Contracts.IsContractCredited(FirstLaunch));
            Assert.True(actions.Last().Effective);
            Assert.Equal(25000.0 + 3200 + 2080, w.Funds.GetRunningBalance(), 3);
        }

        // catches: a parameter row the walk places after its contract's explicit fail paying
        // anyway (stock pays a parameter only while the contract is Active).
        [Fact]
        public void Walk_ParameterAfterTheContractFailed_IsNotCounted()
        {
            var w = RegisterWalk();
            var actions = Seeds();
            actions.Add(new GameAction { UT = 1.0, Type = GameActionType.ContractAccept, ContractId = FirstLaunch });
            actions.Add(new GameAction { UT = 5.0, Type = GameActionType.ContractFail, ContractId = FirstLaunch });
            var late = ParameterRow(FirstLaunch, "0", 9.0, 3200f, 0f, 0f);
            actions.Add(late);

            RecalculationEngine.Recalculate(actions);

            Assert.False(late.Effective);
            Assert.Equal(GameActionNotCountedReason.ContractAlreadyResolved, late.NotCountedReason);
            Assert.Equal(25000.0, w.Funds.GetRunningBalance(), 3);
        }

        [Fact]
        public void PostWalk_ParameterRow_HasTheThreeContractRewardLegs()
        {
            var row = ParameterRow(FirstLaunch, "0", 4.8, 3200f, 1f, 1f);
            row.Effective = true;
            row.TransformedFundsReward = 3200f;
            row.TransformedScienceReward = 1f;
            row.EffectiveRep = 0.9999995f;

            var exp = PostWalkActionReconciler.ClassifyPostWalk(row);

            Assert.True(exp.Reconcile);
            Assert.Equal("ContractReward", exp.Funds.ReasonKey);
            Assert.Equal(3200.0, exp.Funds.Expected, 3);
            Assert.Equal("ContractReward", exp.Sci.ReasonKey);
            Assert.Equal("ContractReward", exp.Rep.ReasonKey);
            Assert.Equal(KscActionExpectationClassifier.KscReconcileClass.Transformed,
                KscActionExpectationClassifier.ClassifyAction(row).Class);
        }

        [Fact]
        public void ActionKey_IsPerContractAndParameter()
        {
            string a = LedgerOrchestrator.GetActionKey(ParameterRow(FirstLaunch, "0", 1, 1, 0, 0));
            string b = LedgerOrchestrator.GetActionKey(ParameterRow(FirstLaunch, "1", 1, 1, 0, 0));
            string c = LedgerOrchestrator.GetActionKey(ContractRow(FirstLaunch, 1, 1, 0, 0));
            Assert.NotEqual(a, b);
            Assert.NotEqual(a, c);
        }

        // ================================================================
        // Supersede, discard, reconcile
        // ================================================================

        // catches: a Re-Fly supersede keeping the parameter reward of the flight it replaces
        // (the default preserves an unknown type).
        [Fact]
        public void Tombstone_FlightTaggedParameterRow_IsEligible_UntaggedIsNot()
        {
            Assert.True(TombstoneEligibility.IsSupersedeTombstoneEligible(
                ParameterRow(FirstLaunch, "0", 4.8, 3200f, 1f, 1f, "rec-old")));
            Assert.False(TombstoneEligibility.IsSupersedeTombstoneEligible(
                ParameterRow(FirstLaunch, "0", 4.8, 3200f, 1f, 1f, null)));
        }

        // catches: a non-rewind discard dropping a parameter reward stock already paid.
        [Fact]
        public void Discard_ReHomesEveryParameterRewardAsADirectRow()
        {
            Assert.True(LedgerOrchestrator.IsIrreversibleLiveGameplayEvent(
                GameStateEventType.ContractParameterCompleted));
            foreach (string p in new[] { "0", "1" })
            {
                var e = new GameStateEvent
                {
                    ut = 200.0,
                    eventType = GameStateEventType.ContractParameterCompleted,
                    key = FirstLaunch,
                    recordingId = "rec-A",
                    detail = GameStateRecorder.BuildContractParameterCompletedDetail(p, "t", 3200f, 1f, 1f)
                };
                GameStateStore.AddEvent(ref e);
            }

            LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard(new HashSet<string> { "rec-A" }, "test");

            var rows = ParameterRows();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.True(string.IsNullOrEmpty(r.RecordingId)));
            Assert.Equal(new[] { "0", "1" }, rows.Select(r => r.ContractParameterId).OrderBy(s => s).ToArray());
        }

        // catches: a maxUT-bounded reconcile treating the parameter row as an "other" row.
        [Fact]
        public void LedgerReconcile_KeepsATaggedParameterRowOfAValidRecording()
        {
            Ledger.AddAction(ParameterRow(FirstLaunch, "0", 500.0, 3200f, 1f, 1f, "rec-valid"));
            Ledger.AddAction(ParameterRow(FirstLaunch, "1", 500.0, 3200f, 1f, 1f, "rec-gone"));

            Ledger.Reconcile(new HashSet<string> { "rec-valid" }, 1000.0);

            var rows = ParameterRows();
            Assert.Single(rows);
            Assert.Equal("rec-valid", rows[0].RecordingId);
        }

        // ================================================================
        // Display
        // ================================================================

        [Fact]
        public void Display_ParameterRow_ReadsAsAContractObjective()
        {
            var row = ParameterRow(FirstLaunch, "0", 4.8, 3200f, 1f, 1f);
            Assert.Equal("Contract objective done: Launch our first vessel! +3200 funds",
                GameActionDisplay.GetContractDescription(row, "Launch our first vessel!", null));
            Assert.True(GameActionDisplay.IsContractActionType(GameActionType.ContractParameterComplete));
            Assert.Equal(TimelineEntryType.ContractComplete,
                TimelineEntryDisplay.MapGameActionType(GameActionType.ContractParameterComplete));
            Assert.Equal(TimelineCareerCategory.Contracts, TimelineCareerCategories.Classify(row));
            Assert.Equal("Contract", GameStateEventDisplay.GetDisplayCategory(GameStateEventType.ContractParameterCompleted));
        }

        // catches: the stored event drawing a second (legacy) Timeline row beside its ledger row.
        [Fact]
        public void Timeline_LegacyTwinKey_MatchesTheStoredEvent()
        {
            var row = ParameterRow(FirstLaunch, "0", 4.8, 3200f, 1f, 1f);
            var evt = new GameStateEvent
            {
                ut = 4.8,
                eventType = GameStateEventType.ContractParameterCompleted,
                key = FirstLaunch
            };
            Assert.Equal(TimelineBuilder.GetLegacyEventKey(evt), TimelineBuilder.GetLegacyDuplicateKey(row));
        }

        // ================================================================
        // Review follow-up: stock's parameter state, both effectiveness orders,
        // the skipped-leg rule and the save-recovery mapping
        // ================================================================

        // catches: the first-in-walk-order rule flipping - a re-fly that completes the
        // parameter at Y BEFORE the committed flight's X must be the counted row.
        [Theory]
        [InlineData(100.0, 200.0, true)]   // committed A at X=100, re-fly B at Y=200: A counts
        [InlineData(300.0, 200.0, false)]  // committed A at X=300, re-fly B at Y=200: B counts
        public void Walk_TwoRowsForOneParameter_TheEarlierOneCounts(double xA, double yB, bool aCounts)
        {
            var w = RegisterWalk();
            var actions = Seeds();
            var a = ParameterRow(FirstLaunch, "0", xA, 3200f, 1f, 1f, "rec-A");
            var b = ParameterRow(FirstLaunch, "0", yB, 3200f, 1f, 1f, "rec-B");
            actions.Add(a);
            actions.Add(b);

            RecalculationEngine.Recalculate(actions);

            Assert.Equal(aCounts, a.Effective);
            Assert.Equal(!aCounts, b.Effective);
            Assert.Equal(25000.0 + 3200, w.Funds.GetRunningBalance(), 3);
            var credited = w.Contracts.GetCreditedParameters();
            Assert.Single(credited);
            Assert.Equal(aCounts ? xA : yB, credited[0].UT);
        }

        // catches: the review's double-pay path. A committed flight's parameter at X is
        // credited once the clock crosses it; unless the patch completes stock's parameter
        // then, the re-fly's live completion at Y pays stock a second time while the ledger's
        // row B is not counted, and the guard keeps stock's second payment as an adjustment.
        [Fact]
        public void DoublePay_CommittedParameterCrossed_StockParameterCompletedSilently()
        {
            var w = RegisterWalk();
            var actions = Seeds();
            actions.Add(new GameAction { UT = 1.0, Type = GameActionType.ContractAccept, ContractId = FirstLaunch });
            var a = ParameterRow(FirstLaunch, "0", 100.0, 3200f, 1f, 1f, "rec-A");
            actions.Add(a);

            RecalculationEngine.Recalculate(actions);
            var credited = w.Contracts.GetCreditedParameters();

            // Clock before X: stock's parameter stays open (the flight has not happened yet).
            var before = KspStatePatcher.PlanContractParameterPatch(credited,
                (c, p) => KspStatePatcher.StockContractParameterState.NotComplete, 50.0);
            Assert.Empty(before.ToComplete);
            Assert.Equal(1, before.AfterClock);

            // Clock past X: the patch completes it without paying, so stock's SetComplete
            // returns early at Y and pays nothing.
            var after = KspStatePatcher.PlanContractParameterPatch(credited,
                (c, p) => KspStatePatcher.StockContractParameterState.NotComplete, 150.0);
            Assert.Single(after.ToComplete);
            Assert.Equal(FirstLaunch, after.ToComplete[0].ContractId);
            Assert.Equal("0", after.ToComplete[0].ParameterId);

            // A row B for the same parameter later in the walk is not counted and moves no pool.
            var b = ParameterRow(FirstLaunch, "0", 200.0, 3200f, 1f, 1f, "rec-B");
            actions.Add(b);
            RecalculationEngine.Recalculate(actions);
            Assert.True(a.Effective);
            Assert.False(b.Effective);
            Assert.False(FundsModule.TryGetRowDelta(b, out double bDelta));
            Assert.Equal(25000.0 + 3200, w.Funds.GetRunningBalance(), 3);
        }

        [Theory]
        [InlineData((int)KspStatePatcher.StockContractParameterState.NotComplete, 100.0, 150.0, (int)KspStatePatcher.ContractParameterPatchDecision.CompleteSilently)]
        [InlineData((int)KspStatePatcher.StockContractParameterState.Complete, 100.0, 150.0, (int)KspStatePatcher.ContractParameterPatchDecision.AlreadyComplete)]
        [InlineData((int)KspStatePatcher.StockContractParameterState.Unresolved, 100.0, 150.0, (int)KspStatePatcher.ContractParameterPatchDecision.Unresolved)]
        [InlineData((int)KspStatePatcher.StockContractParameterState.ContractNotActive, 100.0, 150.0, (int)KspStatePatcher.ContractParameterPatchDecision.ContractNotActive)]
        [InlineData((int)KspStatePatcher.StockContractParameterState.NotComplete, 200.0, 150.0, (int)KspStatePatcher.ContractParameterPatchDecision.AfterClock)]
        // Within the accept-clock tolerance counts as reached.
        [InlineData((int)KspStatePatcher.StockContractParameterState.NotComplete, 150.5, 150.0, (int)KspStatePatcher.ContractParameterPatchDecision.CompleteSilently)]
        // No real clock (OnLoad, a clockless full replay): nothing is completed - the write
        // persists in stock's save and has no undo. Unlike the accept filter.
        [InlineData((int)KspStatePatcher.StockContractParameterState.NotComplete, 200.0, double.NaN, (int)KspStatePatcher.ContractParameterPatchDecision.ClockNotReady)]
        [InlineData((int)KspStatePatcher.StockContractParameterState.NotComplete, 50.0, double.NaN, (int)KspStatePatcher.ContractParameterPatchDecision.ClockNotReady)]
        [InlineData((int)KspStatePatcher.StockContractParameterState.NotComplete, 50.0, 0.0, (int)KspStatePatcher.ContractParameterPatchDecision.ClockNotReady)]
        [InlineData((int)KspStatePatcher.StockContractParameterState.NotComplete, 50.0, double.PositiveInfinity, (int)KspStatePatcher.ContractParameterPatchDecision.ClockNotReady)]
        public void DecideContractParameterPatch_Table(
            int stock, double creditedUT, double clockUT, int expected)
        {
            Assert.Equal((KspStatePatcher.ContractParameterPatchDecision)expected,
                KspStatePatcher.DecideContractParameterPatch(
                    (KspStatePatcher.StockContractParameterState)stock, creditedUT, clockUT));
        }

        // catches: review blocker 1 - a clockless patch (OnLoad's full replay credits every
        // committed-future row) completing future parameters in stock for good.
        [Fact]
        public void PlanContractParameterPatch_NaNClock_CompletesNothing()
        {
            var credited = new List<CreditedContractParameter>
            {
                new CreditedContractParameter { ContractId = "c1", ParameterId = "0", UT = 10 },
                new CreditedContractParameter { ContractId = "c1", ParameterId = "1", UT = 1e7 },
            };
            var plan = KspStatePatcher.PlanContractParameterPatch(credited,
                (c, p) => KspStatePatcher.StockContractParameterState.NotComplete, double.NaN);

            Assert.Empty(plan.ToComplete);
            Assert.Equal(2, plan.ClockNotReady);
        }

        [Theory]
        // A real walk cutoff is the walk's now.
        [InlineData(120.0, double.NaN, 120.0)]
        [InlineData(120.0, 500.0, 120.0)]
        // No cutoff: the committed-row clock (caller or live clock).
        [InlineData(null, 500.0, 500.0)]
        // No cutoff and no clock (OnLoad): no clock at all.
        [InlineData(null, double.NaN, double.NaN)]
        [InlineData(0.0, double.NaN, double.NaN)]
        [InlineData(double.NaN, double.NaN, double.NaN)]
        public void ResolveContractParameterClockUT_CutoffElseCommittedRowClock(
            double? walkCutoff, double committedRowClock, double expected)
        {
            double got = KspStatePatcher.ResolveContractParameterClockUT(walkCutoff, committedRowClock);
            if (double.IsNaN(expected))
                Assert.True(double.IsNaN(got));
            else
                Assert.Equal(expected, got);
        }

        // catches: review blocker 2 - every required parameter Complete but the contract left
        // Active (never paid, later expired), and the mirror: re-evaluating a contract the
        // ledger already ended, which would pay its reward twice.
        [Fact]
        public void CollectContractsCompletedAfter_OnlyCompletionsAfterTheClock()
        {
            // A contract the committed timeline completes later must not be handed to stock
            // at the clock (it would move the reward off the flight that earns it).
            var actions = new List<GameAction>
            {
                new GameAction { UT = 100, Type = GameActionType.ContractComplete, ContractId = "before" },
                new GameAction { UT = 200, Type = GameActionType.ContractComplete, ContractId = "atClock" },
                new GameAction { UT = 300, Type = GameActionType.ContractComplete, ContractId = "later" },
                new GameAction { UT = 300, Type = GameActionType.ContractParameterComplete, ContractId = "paramOnly", ContractParameterId = "0" },
                new GameAction { UT = 300, Type = GameActionType.ContractFail, ContractId = "failsLater" },
                null,
            };
            var ids = KspStatePatcher.CollectContractsCompletedAfter(actions, 200);
            Assert.Equal(new[] { "later" }, ids.ToArray());
            Assert.Empty(KspStatePatcher.CollectContractsCompletedAfter(null, 200));
        }

        [Theory]
        [InlineData(true, false, 2, 2, 0, true)]   // all top-level Complete: stock completes it
        [InlineData(true, false, 2, 1, 0, false)]  // one still open: nothing to do
        [InlineData(true, false, 2, 1, 1, true)]   // a group with every child Complete may complete
        [InlineData(true, true, 2, 2, 0, false)]   // the ledger ended it: never re-evaluated
        [InlineData(false, false, 2, 2, 0, false)] // stock has it no longer Active
        [InlineData(true, false, 0, 0, 0, false)]  // a contract with no parameters
        public void NeedsStockContractReEvaluation_Table(bool active, bool ledgerTerminal,
            int topCount, int topComplete, int satisfiedGroups, bool expected)
        {
            Assert.Equal(expected, KspStatePatcher.NeedsStockContractReEvaluation(
                active, ledgerTerminal, topCount, topComplete, satisfiedGroups));
        }

        [Fact]
        public void PlanContractParameterPatch_CountsEachOutcome_AndPlansAPairOnce()
        {
            var credited = new List<CreditedContractParameter>
            {
                new CreditedContractParameter { ContractId = "c1", ParameterId = "0", UT = 10 },
                new CreditedContractParameter { ContractId = "c1", ParameterId = "0", UT = 10 },
                new CreditedContractParameter { ContractId = "c1", ParameterId = "1", UT = 10 },
                new CreditedContractParameter { ContractId = "c1", ParameterId = "9", UT = 10 },
                new CreditedContractParameter { ContractId = "c2", ParameterId = "0", UT = 10 },
                new CreditedContractParameter { ContractId = "c1", ParameterId = "2", UT = 500 },
            };
            var plan = KspStatePatcher.PlanContractParameterPatch(credited, (c, p) =>
            {
                if (c == "c2") return KspStatePatcher.StockContractParameterState.ContractNotActive;
                if (p == "1") return KspStatePatcher.StockContractParameterState.Complete;
                if (p == "9") return KspStatePatcher.StockContractParameterState.Unresolved;
                return KspStatePatcher.StockContractParameterState.NotComplete;
            }, 100.0);

            Assert.Single(plan.ToComplete);
            Assert.Equal("0", plan.ToComplete[0].ParameterId);
            Assert.Equal(1, plan.AlreadyComplete);
            Assert.Equal(1, plan.Unresolved);
            Assert.Equal(1, plan.ContractNotActive);
            Assert.Equal(1, plan.AfterClock);
        }

        [Theory]
        [InlineData("0", new[] { 0 })]
        [InlineData("1.0", new[] { 1, 0 })]
        [InlineData("0.3.12", new[] { 0, 3, 12 })]
        [InlineData("id:goal", null)]
        [InlineData("1.x", null)]
        [InlineData("-1", null)]
        [InlineData("", null)]
        public void ParseContractParameterIndexPath_RoundTripsTheRecorderForm(string id, int[] expected)
        {
            var parsed = KspStatePatcher.ParseContractParameterIndexPath(id);
            if (expected == null)
                Assert.Null(parsed);
            else
                Assert.Equal(expected, parsed.ToArray());
            if (expected != null)
                Assert.Equal(id, GameStateRecorder.BuildContractParameterIndexPath(expected));
        }

        // catches: booking a science / reputation leg stock skipped because a strategy's effect
        // delta cancelled it (stock then leaves the field unpaid and nonzero).
        [Theory]
        [InlineData(3200f, 0f, 3200f)]  // paid and zeroed
        [InlineData(2f, 2f, 0f)]        // skipped: still set after the call
        [InlineData(0f, 0f, 0f)]        // nothing to pay
        public void PaidLeg_IsTheValueOnlyWhenStockZeroedIt(float before, float after, float expected)
        {
            Assert.Equal(expected, ContractParameterRewardCapturePatch.PaidLeg(before, after));
        }

        // catches: the silent write paying, firing the parameter's state event, or leaving
        // the reward fields set (stock zeroes each paid field).
        [Fact]
        public void CompleteSilently_SetsStocksPostPaymentState_WithoutFiringOnStateChange()
        {
            var p = new Contracts.ContractParameter();
            p.FundsCompletion = 3200.0;
            p.ScienceCompletion = 1f;
            p.ReputationCompletion = 1f;
            p.DisableOnStateChange = true;
            int fired = 0;
            p.OnStateChange.Add((cp, st) => fired++);

            Assert.True(KspStatePatcher.TryCompleteStockParameterSilently(p));

            Assert.Equal(Contracts.ParameterState.Complete, p.State);
            Assert.Equal(0.0, p.FundsCompletion);
            Assert.Equal(0f, p.ScienceCompletion);
            Assert.Equal(0f, p.ReputationCompletion);
            Assert.False(p.Enabled);
            Assert.Equal(0, fired);
        }

        [Fact]
        public void SaveRecovery_MapsAndRebuildsALostParameterRow_PerParameter()
        {
            Assert.Equal(GameActionType.ContractParameterComplete,
                LedgerOrchestrator.MapEventTypeToActionType(GameStateEventType.ContractParameterCompleted));
            Assert.True(LedgerOrchestrator.IsRecoverableEventType(GameStateEventType.ContractParameterCompleted));

            foreach (string p in new[] { "0", "1" })
            {
                var e = new GameStateEvent
                {
                    ut = 100.0,
                    eventType = GameStateEventType.ContractParameterCompleted,
                    key = FirstLaunch,
                    detail = GameStateRecorder.BuildContractParameterCompletedDetail(p, "t", 3200f, 1f, 1f)
                };
                GameStateStore.AddEvent(ref e);
            }
            // Parameter "0" already has its row; only "1" is missing.
            Ledger.AddAction(ParameterRow(FirstLaunch, "0", 100.0, 3200f, 1f, 1f));

            int recovered = LedgerOrchestrator.TryRecoverBrokenLedgerOnLoad();

            Assert.Equal(1, recovered);
            Assert.Equal(new[] { "0", "1" },
                ParameterRows().Select(r => r.ContractParameterId).OrderBy(s => s).ToArray());
            Assert.Equal(0, LedgerOrchestrator.TryRecoverBrokenLedgerOnLoad());
        }
    }
}
