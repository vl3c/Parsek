using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Parsek.Patches;
using Xunit;
using Contract = Contracts.Contract;

namespace Parsek.Tests
{
    /// <summary>
    /// The three contract-outcome capture fixes (todos CONTRACT-CANCEL-PENALTY-IGNORES-STOCK-TIME-LERP,
    /// CONTRACT-CANCEL-ALSO-FILES-A-FAIL-ROW, STOCK-OFFER-EXPIRED-ACTIVE-CONTRACT-NO-TERMINAL-ROW),
    /// pinned against stock 1.12.5's decompiled <c>Contract.PenalizeCancellation</c> /
    /// <c>SetState</c> / <c>Update</c>.
    /// </summary>
    [Collection("Sequential")]
    public class ContractOutcomeRowsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ContractOutcomeRowsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateStore.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            ContractOfferExpiredCapturePatch.ResetForTesting();
            ClickReservationGate.RepLossDeclinedProviderForTesting = () => 1.0;
        }

        public void Dispose()
        {
            ClickReservationGate.RepLossDeclinedProviderForTesting = null;
            ContractOfferExpiredCapturePatch.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private sealed class FakeContract : Contract
        {
            private readonly string title;

            internal FakeContract(string title, Guid guid, State state)
            {
                this.title = title;
                SetField("contractGuid", guid);
                SetField("state", state);
            }

            internal void SetField(string name, object value)
            {
                typeof(Contract).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(this, value);
            }

            protected override string GetTitle() => title;
        }

        private static readonly Guid G1 = new Guid("8a2b7d40-0000-4000-8000-000000000001");

        // ================================================================
        // Stock's cancellation penalty
        // ================================================================

        [Fact]
        public void CancelPenalty_RightAfterAccept_IsTheAdvanceAndTheDeclineLoss()
        {
            float t = ContractCancellationPenalty.TimeFraction(1000.0, 101000.0, 1000.0);
            Assert.Equal(0f, t);
            Assert.Equal(5000.0, ContractCancellationPenalty.Funds(5000.0, 27225.0, t));
            Assert.Equal(1f, ContractCancellationPenalty.Reputation(1f, 4f, t));
        }

        [Fact]
        public void CancelPenalty_AtOrPastTheDeadline_IsTheFullFailPenalty()
        {
            float atDeadline = ContractCancellationPenalty.TimeFraction(1000.0, 101000.0, 101000.0);
            float past = ContractCancellationPenalty.TimeFraction(1000.0, 101000.0, 500000.0);
            Assert.Equal(1f, atDeadline);
            Assert.Equal(1f, past);
            Assert.Equal(27225.0, ContractCancellationPenalty.Funds(5000.0, 27225.0, atDeadline));
            Assert.Equal(4f, ContractCancellationPenalty.Reputation(1f, 4f, past));
        }

        [Fact]
        public void CancelPenalty_Halfway_IsTheLerpMidpoint()
        {
            float t = ContractCancellationPenalty.TimeFraction(1000.0, 101000.0, 51000.0);
            Assert.Equal(0.5f, t);
            Assert.Equal(16112.5, ContractCancellationPenalty.Funds(5000.0, 27225.0, t));
            Assert.Equal(2.5f, ContractCancellationPenalty.Reputation(1f, 4f, t));
        }

        [Fact]
        public void CancelPenalty_NoDeadline_ChargesTheAdvanceShare()
        {
            // DeadlineType.None leaves dateDeadline 0: InverseLerp(accepted, 0, now) clamps to 0.
            float t = ContractCancellationPenalty.TimeFraction(5000.0, 0.0, 9000.0);
            Assert.Equal(0f, t);
        }

        [Fact]
        public void CancelPenalty_UsesStocksFloatNarrowedDates()
        {
            // Stock casts the dates to float before the inverse lerp. Around 1e8 s a float's
            // step is 8 s, so 100000300 narrows to 100000304 and the fraction is 304/1000,
            // where an exact double computation would give 0.3.
            float t = ContractCancellationPenalty.TimeFraction(100000000.0, 100001000.0, 100000300.0);
            Assert.Equal(0.304f, t);
        }

        [Fact]
        public void CancelPenalty_NegativeLerpIsChargedAsItsMagnitude()
        {
            // Stock deducts Math.Abs of the lerp.
            Assert.Equal(200.0, ContractCancellationPenalty.Funds(-200.0, -200.0, 0.5f));
            Assert.Equal(2f, ContractCancellationPenalty.Reputation(-2f, -2f, 0.5f));
        }

        // ================================================================
        // The recorder: cancel files the stock penalty and the onFailed re-fire is skipped
        // ================================================================

        private static void InvokeHandler(string name, Contract contract)
        {
            var recorder = new GameStateRecorder();
            var method = typeof(GameStateRecorder).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method.Invoke(recorder, new object[] { contract });
        }

        private static List<GameStateEvent> StoreEvents(GameStateEventType type) =>
            GameStateStore.Events.Where(e => e.eventType == type).ToList();

        [Fact]
        public void Recorder_CancelDetail_CarriesTheStockLerpPenalty_NotTheFailPenalty()
        {
            var contract = new FakeContract("Orbit the Mun", G1, Contract.State.Cancelled);
            contract.FundsAdvance = 5000.0;
            contract.FundsFailure = 27225.0;
            contract.ReputationFailure = 4f;
            contract.SetField("dateAccepted", 1000.0);
            contract.SetField("dateDeadline", 101000.0);
            contract.SetField("dateFinished", 26000.0); // t = 0.25

            float funds, rep, t;
            string detail = GameStateRecorder.BuildContractCancelledDetail(contract, 1f, out funds, out rep, out t);

            Assert.Equal(0.25f, t);
            var action = GameStateEventConverter.ConvertEvent(new GameStateEvent
            {
                ut = 26000.0, eventType = GameStateEventType.ContractCancelled, key = G1.ToString(), detail = detail
            }, "");
            Assert.Equal(GameActionType.ContractCancel, action.Type);
            Assert.Equal(10556.25f, action.FundsPenalty);
            Assert.Equal(1.75f, action.RepPenalty);
            Assert.False(action.ContractRequirementsLost);
            Assert.Equal("Orbit the Mun", action.ContractTitle);
        }

        [Fact]
        public void Recorder_CancelDetail_L9Shape_EarlyCancelChargesTheDeclineLoss()
        {
            // L9 2026-10-22_1332: an early cancel of a contract with fail rep 4 filed 4 where
            // stock took 1 (RepLossDeclined 1 at t near 0).
            var contract = new FakeContract("L9", G1, Contract.State.Cancelled);
            contract.FundsAdvance = 4882.0;
            contract.FundsFailure = 4882.0;
            contract.ReputationFailure = 4f;
            contract.SetField("dateAccepted", 500.0);
            contract.SetField("dateDeadline", 500.0 + 9203545.0);
            contract.SetField("dateFinished", 520.0);

            float funds, rep, t;
            GameStateRecorder.BuildContractCancelledDetail(contract, 1f, out funds, out rep, out t);

            Assert.Equal(4882f, funds);
            Assert.InRange(rep, 1f, 1.0001f);
        }

        [Fact]
        public void Recorder_FailFireOfACancel_IsSkipped()
        {
            var contract = new FakeContract("Orbit the Mun", G1, Contract.State.Cancelled);
            contract.FundsFailure = 27225.0;
            contract.ReputationFailure = 4f;

            InvokeHandler("OnContractFailed", contract);

            Assert.Empty(StoreEvents(GameStateEventType.ContractFailed));
            Assert.Contains(logLines, l => l.Contains("[GameStateRecorder]")
                && l.Contains("ContractFailed 'Orbit the Mun' skipped") && l.Contains("state=Cancelled"));
        }

        [Theory]
        [InlineData(Contract.State.Cancelled, true)]
        [InlineData(Contract.State.Failed, false)]
        [InlineData(Contract.State.DeadlineExpired, false)]
        [InlineData(Contract.State.Active, false)]
        public void IsFailFireOfCancel_OnlyForACancelledContract(Contract.State state, bool expected)
        {
            Assert.Equal(expected, GameStateRecorder.IsFailFireOfCancel(state));
        }

        // ================================================================
        // Active -> OfferExpired: capture and terminal row
        // ================================================================

        [Theory]
        [InlineData(true, Contract.State.OfferExpired, true)]
        [InlineData(false, Contract.State.OfferExpired, false)]   // an Offered contract expiring
        [InlineData(true, Contract.State.Active, false)]          // refused by ContractEarlyFailPatch
        [InlineData(true, Contract.State.Failed, false)]
        public void IsActiveOfferExpiredTransition_TruthTable(bool wasActive, Contract.State after, bool expected)
        {
            Assert.Equal(expected, ContractOfferExpiredCapturePatch.IsActiveOfferExpiredTransition(wasActive, after));
        }

        private static void InvokePatch(Contract contract, Contract.State newState, Action<Contract> stockSetState)
        {
            var prefix = typeof(ContractOfferExpiredCapturePatch).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
            var postfix = typeof(ContractOfferExpiredCapturePatch).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic);
            var args = new object[] { contract, newState, false };
            prefix.Invoke(null, args);
            stockSetState(contract);
            postfix.Invoke(null, new object[] { contract, args[2] });
        }

        [Fact]
        public void Patch_ActiveContractEndedOfferExpired_RaisesOnce()
        {
            var raised = new List<Contract>();
            ContractOfferExpiredCapturePatch.ActiveContractOfferExpired += c => raised.Add(c);
            var contract = new FakeContract("Test the part", G1, Contract.State.Active);

            InvokePatch(contract, Contract.State.OfferExpired,
                c => ((FakeContract)c).SetField("state", Contract.State.OfferExpired));

            Assert.Single(raised);
            Assert.Same(contract, raised[0]);
        }

        [Fact]
        public void Patch_OfferedContractExpiring_RaisesNothing()
        {
            var raised = new List<Contract>();
            ContractOfferExpiredCapturePatch.ActiveContractOfferExpired += c => raised.Add(c);
            var contract = new FakeContract("Test the part", G1, Contract.State.Offered);

            InvokePatch(contract, Contract.State.OfferExpired,
                c => ((FakeContract)c).SetField("state", Contract.State.OfferExpired));

            Assert.Empty(raised);
        }

        [Fact]
        public void Patch_RefusedEnd_LeavesTheContractActive_RaisesNothing()
        {
            var raised = new List<Contract>();
            ContractOfferExpiredCapturePatch.ActiveContractOfferExpired += c => raised.Add(c);
            var contract = new FakeContract("Test the part", G1, Contract.State.Active);

            InvokePatch(contract, Contract.State.OfferExpired, c => { });

            Assert.Empty(raised);
        }

        [Fact]
        public void Patch_NoSubscribedRecorder_LogsAndDoesNotThrow()
        {
            ContractOfferExpiredCapturePatch.Report(new FakeContract("Test the part", G1, Contract.State.OfferExpired));
            Assert.Contains(logLines, l => l.Contains("[ContractOfferExpiredCapture]")
                && l.Contains("no subscribed recorder"));
        }

        [Fact]
        public void RequirementsLostDetail_ConvertsToAnUnpenalizedEndRow()
        {
            var action = GameStateEventConverter.ConvertEvent(new GameStateEvent
            {
                ut = 300.0,
                eventType = GameStateEventType.ContractCancelled,
                key = G1.ToString(),
                detail = GameStateRecorder.BuildContractRequirementsLostDetail("Test the part")
            }, "");
            Assert.Equal(GameActionType.ContractCancel, action.Type);
            Assert.Equal(G1.ToString(), action.ContractId);
            Assert.True(action.ContractRequirementsLost);
            Assert.Equal(0f, action.FundsPenalty);
            Assert.Equal(0f, action.RepPenalty);
            Assert.Equal("Test the part", action.ContractTitle);
        }

        [Fact]
        public void Recorder_HandlesTheCaptureEvent()
        {
            // Subscribe() touches live GameEvents headless, so the wiring is pinned by
            // reading the handler and the event it subscribes to.
            Assert.NotNull(typeof(ContractOfferExpiredCapturePatch).GetEvent("ActiveContractOfferExpired",
                BindingFlags.Static | BindingFlags.NonPublic));
            Assert.NotNull(typeof(GameStateRecorder).GetMethod("OnActiveContractOfferExpired",
                BindingFlags.Instance | BindingFlags.NonPublic));
        }

        // ================================================================
        // Converter, serialization, walk, display
        // ================================================================

        [Fact]
        public void Converter_RequirementsLostDetail_ZeroesAnyPenalty()
        {
            var evt = new GameStateEvent
            {
                ut = 200.0,
                eventType = GameStateEventType.ContractCancelled,
                key = "c1",
                detail = "title=X;fundsPenalty=900;repPenalty=3;ended=requirements"
            };
            var action = GameStateEventConverter.ConvertEvent(evt, "");
            Assert.True(action.ContractRequirementsLost);
            Assert.Equal(0f, action.FundsPenalty);
            Assert.Equal(0f, action.RepPenalty);
        }

        [Fact]
        public void Converter_PlainCancel_KeepsItsPenalty()
        {
            var evt = new GameStateEvent
            {
                ut = 200.0,
                eventType = GameStateEventType.ContractCancelled,
                key = "c1",
                detail = "title=X;fundsPenalty=900;repPenalty=3"
            };
            var action = GameStateEventConverter.ConvertEvent(evt, "");
            Assert.False(action.ContractRequirementsLost);
            Assert.Equal(900f, action.FundsPenalty);
            Assert.Equal(3f, action.RepPenalty);
        }

        [Fact]
        public void Serialization_RequirementsLost_RoundTrips_AndIsAbsentOnAPlainCancel()
        {
            var parent = new ConfigNode("ROOT");
            new GameAction
            {
                UT = 200.0, Type = GameActionType.ContractCancel, ContractId = "c1",
                ContractRequirementsLost = true
            }.SerializeInto(parent);
            new GameAction
            {
                UT = 300.0, Type = GameActionType.ContractCancel, ContractId = "c2",
                FundsPenalty = 10f
            }.SerializeInto(parent);

            var nodes = parent.GetNodes("GAME_ACTION");
            Assert.Equal("True", nodes[0].GetValue(GameAction.ContractRequirementsLostKey));
            Assert.Null(nodes[1].GetValue(GameAction.ContractRequirementsLostKey));
            Assert.True(GameAction.DeserializeFrom(nodes[0]).ContractRequirementsLost);
            Assert.False(GameAction.DeserializeFrom(nodes[1]).ContractRequirementsLost);
        }

        [Fact]
        public void Walk_RequirementsLostEnd_FreesTheSlot_AndBlocksTheSyntheticDeadlineFail()
        {
            var module = new ContractsModule();
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 100, Type = GameActionType.ContractAccept, ContractId = "c1",
                    AdvanceFunds = 5000f, DeadlineUT = 1000
                },
                new GameAction
                {
                    UT = 400, Type = GameActionType.ContractCancel, ContractId = "c1",
                    ContractRequirementsLost = true
                },
                new GameAction { UT = 2000, Type = GameActionType.FundsEarning },
            };

            module.PrePass(actions);
            Assert.DoesNotContain(actions, a => a.Type == GameActionType.ContractFail);

            module.ProcessAction(actions[0]);
            Assert.Equal(1, module.GetActiveContractCount());
            module.ProcessAction(actions[1]);
            Assert.Equal(0, module.GetActiveContractCount());
            Assert.Equal(ContractTerminalOutcome.RequirementsLost, module.GetTerminalContractOutcomes()["c1"]);
            Assert.Contains(logLines, l => l.Contains("Cancel: contractId='c1'") && l.Contains("requirementsLost=True"));
        }

        [Fact]
        public void Display_RequirementsLostEnd_ReadsEnded_AndTheHoverSaysWhy()
        {
            var ended = new GameAction
            {
                UT = 400, Type = GameActionType.ContractCancel, ContractId = "c1", ContractRequirementsLost = true
            };
            var cancel = new GameAction
            {
                UT = 400, Type = GameActionType.ContractCancel, ContractId = "c1", FundsPenalty = 900f
            };
            Assert.Equal("Ended: X", GameActionDisplay.GetContractDescription(ended, "X"));
            Assert.Equal("Cancel: X", GameActionDisplay.GetContractDescription(cancel, "X"));
            Assert.Equal("Its requirements no longer held, no penalty.",
                TimelineRowHover.ContractOutcome(ended, null));
            Assert.Equal("Penalty 900 funds.", TimelineRowHover.ContractOutcome(cancel, null));
        }

        [Fact]
        public void CommittedRequirementsLostRow_ReadsEnded_InTheMissionControlText()
        {
            var index = CommittedFutureIndex.Build(new[]
            {
                new GameAction { UT = 10, Type = GameActionType.ContractAccept, ContractId = "c1" },
                new GameAction
                {
                    UT = 500, Type = GameActionType.ContractCancel, ContractId = "c1", RecordingId = "rec",
                    ContractRequirementsLost = true
                },
                new GameAction { UT = 20, Type = GameActionType.ContractAccept, ContractId = "c2" },
                new GameAction { UT = 600, Type = GameActionType.ContractCancel, ContractId = "c2", RecordingId = "rec" },
            }, id => id == "rec", id => "Mun Lander 3", null);
            Func<double, string> fmt = ut => "D" + ((long)(ut / 100.0)).ToString(System.Globalization.CultureInfo.InvariantCulture);

            var ended = index.FirstFuture(CommittedFutureKind.ContractCancel, "c1", 100);
            var cancel = index.FirstFuture(CommittedFutureKind.ContractCancel, "c2", 100);
            Assert.True(ended.RequirementsLost);
            Assert.True(ended.WithAgentTitle("Agent").RequirementsLost);
            Assert.False(cancel.RequirementsLost);
            Assert.Equal("Ended on D5, blocked by timeline until then.",
                ReservationExplanation.ContractResolution(ended, fmt).Body);
            Assert.Equal("Cancelled on D6, blocked by timeline until then.",
                ReservationExplanation.ContractResolution(cancel, fmt).Body);
        }

        [Fact]
        public void EventDisplay_RequirementsLostEvent_ReadsEnded()
        {
            var e = new GameStateEvent
            {
                eventType = GameStateEventType.ContractCancelled,
                key = "guid-test",
                detail = GameStateRecorder.BuildContractRequirementsLostDetail("Test Contract")
            };
            Assert.Contains("ended (requirements no longer met)", GameStateEventDisplay.GetDisplayDescription(e));
        }
    }
}
