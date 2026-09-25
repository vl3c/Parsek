using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using KSP.UI.Screens;
using Parsek.Patches;
using Xunit;
using Contract = Contracts.Contract;

namespace Parsek.Tests
{
    /// <summary>
    /// Stock-UI overlays PR 3: the Mission Control Cancel block (C4, owner ruling D7,
    /// docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md section 7.3) and the
    /// Active-row annotation. Cancel is refused when the committed timeline has a LATER
    /// explicit ContractComplete / ContractFail / ContractCancel row for the contract
    /// (cases X1-X3); derived deadline expiry is not a committed row and never blocks (X4).
    /// The ledger outcomes of X1-X3 stay pinned by StockUiReservationVerificationTests;
    /// these cells pin the block that keeps the player out of them.
    /// </summary>
    [Collection("Sequential")]
    public class MissionControlCancelBlockTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private int dialogCount;
        private string dialogAction;
        private string dialogReason;

        private static readonly Func<double, string> Fmt =
            ut => "D" + ((long)(ut / 100.0)).ToString(CultureInfo.InvariantCulture);

        private const string Rule =
            "Parsek's timeline is fixed once committed, so this cannot happen earlier or twice.";

        public MissionControlCancelBlockTests()
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
            EffectiveState.ResetCachesForTesting();
            CommittedFutureIndexCache.ResetForTesting();
            MissionControlStockUi.ResetForTesting();
            ContractConfiguratorCompat.ResetForTesting();
            ContractSystemRebuildContractsScopePatch.ResetForTesting();
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 100.0;
            CommittedActionDialog.TestHookForTesting = (action, reason, detail) =>
            {
                dialogCount++;
                dialogAction = action;
                dialogReason = reason;
            };
        }

        public void Dispose()
        {
            CommittedActionDialog.TestHookForTesting = null;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            CommittedFutureIndexCache.ResetForTesting();
            MissionControlStockUi.ResetForTesting();
            ContractConfiguratorCompat.ResetForTesting();
            ContractSystemRebuildContractsScopePatch.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // ---------------------------------------------------------------- fixtures

        private sealed class FakeContract : Contract
        {
            private readonly string title;

            internal FakeContract(string title, Guid guid, State state)
            {
                this.title = title;
                typeof(Contract).GetField("contractGuid", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(this, guid);
                typeof(Contract).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(this, state);
            }

            protected override string GetTitle() => title;
        }

        private static GameAction Accept(double ut, string id, double deadlineUT = double.NaN) =>
            new GameAction { UT = ut, Type = GameActionType.ContractAccept, ContractId = id, DeadlineUT = deadlineUT };

        private static GameAction Resolve(GameActionType type, double ut, string id, string recordingId = null) =>
            new GameAction { UT = ut, Type = type, ContractId = id, RecordingId = recordingId };

        private static GameAction Complete(double ut, string id, string rec = null) => Resolve(GameActionType.ContractComplete, ut, id, rec);
        private static GameAction Fail(double ut, string id, string rec = null) => Resolve(GameActionType.ContractFail, ut, id, rec);
        private static GameAction Cancel(double ut, string id, string rec = null) => Resolve(GameActionType.ContractCancel, ut, id, rec);

        private static CommittedFutureIndex Index(params GameAction[] actions) =>
            CommittedFutureIndex.Build(actions, id => id == "rec", id => id == "rec" ? "Mun Lander 3" : null, null);

        private static StockUiDecoration ActiveRow(CommittedFutureIndex index, string id, double now = 100) =>
            MissionControlStockAnnotation.Decide(index, now, id, Contract.State.Active, Fmt);

        // ---------------------------------------------------------------- X1-X4 (pure)

        [Fact]
        public void X1_CommittedCompletionLater_BlocksCancel_AndMarksTheActiveRow()
        {
            var index = Index(Accept(10, "c1"), Complete(500, "c1", "rec"));

            var resolution = StockUiReservationPredicates.CommittedContractResolutionAfter(index, "c1", 100);
            var d = ActiveRow(index, "c1");

            Assert.NotNull(resolution);
            Assert.Equal(CommittedFutureKind.ContractComplete, resolution.Kind);
            Assert.Equal(500, resolution.UT);
            Assert.Equal("rec", resolution.RecordingId);
            Assert.True(StockUiReservationPredicates.IsContractCancelBlocked(index, "c1", 100));
            Assert.True(d.Marked);
            Assert.True(d.Blocked);
            Assert.Equal(StockUiDecorationKind.ContractResolution, d.Kind);
            Assert.Equal("Active", d.Tab);
            Assert.Equal(500, d.UT);
            Assert.Equal("Completes on D5", d.Title);
            Assert.Equal("Completes on D5 by the committed flight 'Mun Lander 3'. " + Rule
                + " It completes and frees its slot on that date.", d.Why);
        }

        [Fact]
        public void X2_CommittedFailureLater_BlocksCancel()
        {
            var index = Index(Accept(10, "c1"), Fail(500, "c1"));

            var d = ActiveRow(index, "c1");

            Assert.True(StockUiReservationPredicates.IsContractCancelBlocked(index, "c1", 100));
            Assert.True(d.Blocked);
            Assert.Equal("Fails on D5", d.Title);
            Assert.Equal("Fails on D5 on your committed timeline. " + Rule + " It fails and frees its slot on that date.", d.Why);
        }

        [Fact]
        public void X3_CommittedCancellationLater_BlocksCancel()
        {
            var index = Index(Accept(10, "c1"), Cancel(500, "c1", "rec"));

            var d = ActiveRow(index, "c1");

            Assert.True(StockUiReservationPredicates.IsContractCancelBlocked(index, "c1", 100));
            Assert.True(d.Blocked);
            Assert.Equal("Cancelled on D5", d.Title);
            Assert.Equal("Cancelled on D5 by the committed flight 'Mun Lander 3'. " + Rule
                + " It is cancelled and frees its slot on that date.", d.Why);
        }

        [Fact]
        public void X4_OnlyADerivedDeadlineExpiryAhead_LeavesCancelAllowed()
        {
            // The committed accept carries a deadline at UT 500; expiry is derived by the
            // ledger walk, not a committed row, so cancelling early stays legitimate play.
            var index = Index(Accept(10, "c1", deadlineUT: 500));
            Ledger.AddAction(Accept(10, "c1", deadlineUT: 500));

            var d = ActiveRow(index, "c1");

            Assert.Null(StockUiReservationPredicates.CommittedContractResolutionAfter(index, "c1", 100));
            Assert.False(StockUiReservationPredicates.IsContractCancelBlocked(index, "c1", 100));
            Assert.False(d.Marked);
            Assert.False(d.Blocked);
            Assert.True(ContractCancelPatch.ShouldAllowCancel("c1", "Deadline only"));
            Assert.Equal(0, dialogCount);
        }

        [Fact]
        public void Resolution_AtOrBeforeNow_OrOnAnotherContract_DoesNotBlock()
        {
            var index = Index(Complete(100, "at-now"), Fail(50, "past"), Complete(500, "other"));

            Assert.False(StockUiReservationPredicates.IsContractCancelBlocked(index, "at-now", 100));
            Assert.False(StockUiReservationPredicates.IsContractCancelBlocked(index, "past", 100));
            Assert.False(StockUiReservationPredicates.IsContractCancelBlocked(index, "c1", 100));
            Assert.False(StockUiReservationPredicates.IsContractCancelBlocked(index, "", 100));
            Assert.False(StockUiReservationPredicates.IsContractCancelBlocked(null, "other", 100));
            Assert.True(StockUiReservationPredicates.IsContractCancelBlocked(index, "other", 100));
            Assert.False(StockUiReservationPredicates.IsContractCancelBlocked(index, "other", 500));
        }

        [Fact]
        public void Resolution_OfAnUncommittedRecording_DoesNotBlock()
        {
            // "live" is not in the committed set: a live or Re-Fly tree reserves nothing.
            var index = Index(Complete(500, "c1", "live"));

            Assert.False(StockUiReservationPredicates.IsContractCancelBlocked(index, "c1", 100));
        }

        [Fact]
        public void Resolution_TheEarliestLaterRowWins_AndATieResolvesCompleteFailCancel()
        {
            var index = Index(Cancel(300, "c1"), Complete(700, "c1"), Fail(400, "c2"), Complete(400, "c2"),
                Cancel(400, "c3"), Fail(400, "c3"));

            Assert.Equal(CommittedFutureKind.ContractCancel,
                StockUiReservationPredicates.CommittedContractResolutionAfter(index, "c1", 100).Kind);
            Assert.Equal(CommittedFutureKind.ContractComplete,
                StockUiReservationPredicates.CommittedContractResolutionAfter(index, "c1", 300).Kind);
            Assert.Equal(CommittedFutureKind.ContractComplete,
                StockUiReservationPredicates.CommittedContractResolutionAfter(index, "c2", 100).Kind);
            Assert.Equal(CommittedFutureKind.ContractFail,
                StockUiReservationPredicates.CommittedContractResolutionAfter(index, "c3", 100).Kind);
        }

        [Fact]
        public void TheCancelBlock_IsForTheActiveTabOnly_AndTheAcceptBlockForTheAvailableTabOnly()
        {
            var index = Index(Accept(500, "offered"), Complete(500, "active"));

            var offeredWithOnlyAResolution = MissionControlStockAnnotation.Decide(index, 100, "active", Contract.State.Offered, Fmt);
            var activeWithOnlyAnAccept = ActiveRow(index, "offered");
            var archived = MissionControlStockAnnotation.Decide(index, 100, "active", Contract.State.Completed, Fmt);

            Assert.False(offeredWithOnlyAResolution.Marked);
            Assert.False(activeWithOnlyAnAccept.Marked);
            Assert.False(archived.Marked);
        }

        [Fact]
        public void BlocksHelpers_SeparateTheAcceptBlockFromTheCancelBlock()
        {
            var index = Index(Accept(500, "offered"), Complete(500, "active"));
            var accept = MissionControlStockAnnotation.Decide(index, 100, "offered", Contract.State.Offered, Fmt);
            var cancel = ActiveRow(index, "active");

            Assert.True(MissionControlStockAnnotation.BlocksAcceptAndDecline(accept));
            Assert.False(MissionControlStockAnnotation.BlocksCancel(accept));
            Assert.True(MissionControlStockAnnotation.BlocksCancel(cancel));
            Assert.False(MissionControlStockAnnotation.BlocksAcceptAndDecline(cancel));
            // CC's CanAccept filter reads only the accept block.
            Assert.True(ContractConfiguratorCompat.FilterCanAccept(true, cancel, false));
            Assert.False(ContractConfiguratorCompat.FilterCanAccept(true, accept, false));
        }

        // ---------------------------------------------------------------- texts

        [Fact]
        public void ContractResolution_Text_PerKind_AndPlainAscii()
        {
            var complete = ReservationExplanation.ContractResolution(
                new CommittedFutureEntry(CommittedFutureKind.ContractComplete, "k", 11400, "rec", "Mun Lander 3"), Fmt);
            var fail = ReservationExplanation.ContractResolution(
                new CommittedFutureEntry(CommittedFutureKind.ContractFail, "k", 11400, null, null), Fmt);
            var cancel = ReservationExplanation.ContractResolution(
                new CommittedFutureEntry(CommittedFutureKind.ContractCancel, "k", 11400, null, null), Fmt);

            Assert.Equal("Completes on D114", complete.Title);
            Assert.Equal("Completes on D114 by the committed flight 'Mun Lander 3'. " + Rule
                + " It completes and frees its slot on that date.", complete.Body);
            Assert.Equal("Fails on D114 on your committed timeline. " + Rule
                + " It fails and frees its slot on that date.", fail.Body);
            Assert.Equal("Cancelled on D114 on your committed timeline. " + Rule
                + " It is cancelled and frees its slot on that date.", cancel.Body);
            foreach (char c in complete.Body + fail.Body + cancel.Body + complete.Title + fail.Title + cancel.Title)
                Assert.True(c < 128, "non-ASCII char " + (int)c);
        }

        [Fact]
        public void ActiveRowLabel_KeepsTheStockTitle_AndSaysWhenItResolves()
        {
            var d = ActiveRow(Index(Complete(500, "c1")), "c1");

            string label = MissionControlStockAnnotation.ComposeRowLabel("", "Explore the Mun", d);

            Assert.Equal("<color=#fefa87>Explore the Mun</color> <color=#8fd3ff>- completes on D5 on your committed timeline</color>",
                label);
            Assert.Equal(label, MissionControlStockAnnotation.ComposeRowLabel(label, "Explore the Mun", d));
        }

        [Fact]
        public void DetailText_NamesCancelAsUnavailable_AndCarriesTheWhyOnce()
        {
            var d = ActiveRow(Index(Fail(500, "c1")), "c1");

            string text = MissionControlStockAnnotation.ComposeDetailText("Stock", d);

            Assert.Equal("Stock\n\n<b><color=#8fd3ff>Cancel is unavailable</color></b>\n" + d.Why, text);
            Assert.Equal(text, MissionControlStockAnnotation.ComposeDetailText(text, d));
            Assert.Equal(MissionControlStockAnnotation.CancelDetailHeading,
                MissionControlStockAnnotation.DetailHeadingFor(StockUiDecorationKind.ContractResolution));
            Assert.Equal(MissionControlStockAnnotation.DetailHeading,
                MissionControlStockAnnotation.DetailHeadingFor(StockUiDecorationKind.ContractAccept));
        }

        // ---------------------------------------------------------------- AddItem label + pass log

        [Fact]
        public void LabelForAddItem_ActiveTabPass_LabelsOnlyTheCommittedResolution_AndLogsTabActive()
        {
            var resolved = new FakeContract("Resolved", Guid.NewGuid(), Contract.State.Active);
            var open = new FakeContract("Open", Guid.NewGuid(), Contract.State.Active);
            Ledger.AddAction(Complete(500, resolved.ContractGuid.ToString()));
            Ledger.AddAction(Accept(10, open.ContractGuid.ToString(), deadlineUT: 500));

            MissionControlStockUi.BeginRebuildPass(MissionControl.DisplayMode.Active);
            string resolvedLabel = MissionControlStockUi.LabelForAddItem(resolved, "");
            string openLabel = MissionControlStockUi.LabelForAddItem(open, "");
            MissionControlStockUi.EndRebuildPass();

            Assert.StartsWith("<color=#fefa87>Resolved</color>" + MissionControlStockAnnotation.RowStatusMarker + "completes on ",
                resolvedLabel);
            Assert.EndsWith(MissionControlStockAnnotation.RowStatusTail + "</color>", resolvedLabel);
            Assert.Equal("", openLabel);
            Assert.Contains(logLines, l => l.Contains("[INFO][StockUiOverlay]")
                && l.EndsWith("decorate screen=MissionControl tab=Active items=2 marked=1 blocked=1"));
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][StockUiOverlay]")
                && l.Contains("item=" + resolved.ContractGuid + " kind=ContractResolution marked=true blocked=true"));
        }

        // ---------------------------------------------------------------- Cancel backstop

        [Fact]
        public void CancelBackstop_CommittedResolution_RefusesWithTheResolutionText()
        {
            Ledger.AddAction(Complete(500, "c1"));

            bool allowed = ContractCancelPatch.ShouldAllowCancel("c1", "Explore the Mun");

            Assert.False(allowed);
            Assert.Equal(1, dialogCount);
            Assert.Equal("Cannot cancel \"Explore the Mun\"", dialogAction);
            Assert.Equal(StockUiReservationPredicates.ExplainContractCancel(
                CommittedFutureIndexCache.Current, "c1", 100, ReservationExplanation.DefaultDateFormatter).Body, dialogReason);
            Assert.Contains(logLines, l => l.Contains("[INFO][ContractCancelPatch]")
                && l.Contains("blocking cancel for guid=c1 - committed ContractComplete ut=500 nowUT=100 recording=(ksc)"));
        }

        [Fact]
        public void CancelBackstop_NoLaterResolution_Allows()
        {
            Ledger.AddAction(Complete(50, "past"));

            Assert.True(ContractCancelPatch.ShouldAllowCancel("past", "Past"));
            Assert.True(ContractCancelPatch.ShouldAllowCancel("free", "Free"));
            Assert.True(ContractCancelPatch.ShouldAllowCancel("", "Empty"));
            Assert.Equal(0, dialogCount);
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][ContractCancelPatch]")
                && l.Contains("allowing cancel for guid=free - no committed resolution after now"));
        }

        [Fact]
        public void CancelBackstop_BypassesWhileReplayingActions()
        {
            Ledger.AddAction(Fail(500, "c1"));
            GameStateRecorder.IsReplayingActions = true;

            Assert.True(ContractCancelPatch.ShouldAllowCancel("c1", "c1"));
            Assert.Equal(0, dialogCount);
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][ContractCancelPatch]") && l.Contains("bypass - replay in progress"));
        }

        [Fact]
        public void CancelBackstop_BypassesInsideRebuildContracts_AndRefusesAgainAfterIt()
        {
            Ledger.AddAction(Cancel(500, "c1"));

            ContractSystemRebuildContractsScopePatch.EnterScope();
            bool inside = ContractCancelPatch.ShouldAllowCancel("c1", "c1");
            ContractSystemRebuildContractsScopePatch.ExitScope();
            bool after = ContractCancelPatch.ShouldAllowCancel("c1", "c1");

            Assert.True(inside);
            Assert.False(after);
            Assert.Equal(1, dialogCount);
            Assert.False(ContractSystemRebuildContractsScopePatch.InRebuildContracts);
            Assert.Contains(logLines, l => l.Contains("[INFO][ContractCancelPatch]")
                && l.Contains("bypass - ContractSystem.RebuildContracts is regenerating every contract (guid=c1)"));
        }

        [Fact]
        public void RebuildContractsScope_FinalizerClearsTheScope_EvenWhenTheMethodThrows()
        {
            var prefix = typeof(ContractSystemRebuildContractsScopePatch).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
            var finalizer = typeof(ContractSystemRebuildContractsScopePatch).GetMethod("Finalizer", BindingFlags.Static | BindingFlags.NonPublic);
            var thrown = new InvalidOperationException("boom");

            prefix.Invoke(null, null);
            Assert.True(ContractSystemRebuildContractsScopePatch.InRebuildContracts);
            object rethrown = finalizer.Invoke(null, new object[] { thrown });

            Assert.Same(thrown, rethrown);
            Assert.False(ContractSystemRebuildContractsScopePatch.InRebuildContracts);
            ContractSystemRebuildContractsScopePatch.ExitScope();
            Assert.False(ContractSystemRebuildContractsScopePatch.InRebuildContracts);
        }

        [Fact]
        public void CancelPrefix_RefusesOnlyAnActiveContract()
        {
            var active = new FakeContract("Active one", Guid.NewGuid(), Contract.State.Active);
            var offered = new FakeContract("Offered one", Guid.NewGuid(), Contract.State.Offered);
            Ledger.AddAction(Complete(500, active.ContractGuid.ToString()));
            Ledger.AddAction(Complete(500, offered.ContractGuid.ToString()));
            var prefix = typeof(ContractCancelPatch).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);

            bool activeAllowed = (bool)prefix.Invoke(null, new object[] { active });
            bool offeredAllowed = (bool)prefix.Invoke(null, new object[] { offered });

            Assert.False(activeAllowed);
            Assert.True(offeredAllowed);
            Assert.Equal(1, dialogCount);
            Assert.Equal("Cannot cancel \"Active one\"", dialogAction);
            Assert.Contains(logLines, l => l.Contains("allowing cancel for guid=" + offered.ContractGuid)
                && l.Contains("is not Active (stock no-op)"));
        }

        // ---------------------------------------------------------------- pairing

        /// <summary>
        /// The pairing rule for Cancel: the Active rows the label marks, the contracts whose
        /// detail panel greys out Cancel and the Cancel backstop's refusals are ONE set over
        /// the ledger-backed cached index, and the refusal says what the panel says.
        /// </summary>
        [Theory]
        [InlineData(0.0)]
        [InlineData(100.0)]
        [InlineData(250.0)]
        [InlineData(1000.0)]
        public void Pairing_ActiveRowMark_PanelCancelBlock_CancelBackstop_AreOneSet_WithTheSameText(double now)
        {
            Ledger.AddAction(Accept(10, "x1"));
            Ledger.AddAction(Complete(200, "x1"));
            Ledger.AddAction(Accept(10, "x2"));
            Ledger.AddAction(Fail(500, "x2"));
            Ledger.AddAction(Accept(10, "x3"));
            Ledger.AddAction(Cancel(300, "x3"));
            Ledger.AddAction(Accept(10, "x4", deadlineUT: 400));
            Ledger.AddAction(Complete(50, "past"));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => now;
            var ids = new[] { "x1", "x2", "x3", "x4", "past", "free" };
            var index = CommittedFutureIndexCache.Current;

            var decisions = ids.ToDictionary(id => id,
                id => MissionControlStockAnnotation.Decide(index, now, id, Contract.State.Active, ReservationExplanation.DefaultDateFormatter));
            var rowMarked = Set(ids.Where(id => MissionControlStockAnnotation.HasRowStatus(
                MissionControlStockAnnotation.ComposeRowLabel("", id, decisions[id]))));
            var panelBlocked = Set(ids.Where(id => MissionControlStockAnnotation.BlocksCancel(decisions[id])
                && MissionControlStockAnnotation.ComposeDetailText("stock", decisions[id]) != "stock"));
            var refused = new List<string>();
            var refusalText = new Dictionary<string, string>();
            foreach (string id in ids)
            {
                dialogReason = null;
                if (!ContractCancelPatch.ShouldAllowCancel(id, id))
                {
                    refused.Add(id);
                    refusalText[id] = dialogReason;
                }
            }
            var expected = Set(ids.Where(id => StockUiReservationPredicates.IsContractCancelBlocked(index, id, now)));

            Assert.Equal(expected, rowMarked);
            Assert.Equal(expected, panelBlocked);
            Assert.Equal(expected, Set(refused));
            Assert.DoesNotContain("x4", expected);
            foreach (string id in refused)
            {
                Assert.Equal(decisions[id].Why, refusalText[id]);
                Assert.EndsWith("\n" + refusalText[id], MissionControlStockAnnotation.ComposeDetailText("stock", decisions[id]));
            }
            if (now >= 50 && now < 200)
                Assert.Equal(new[] { "x1", "x2", "x3" }, expected.ToArray());
        }

        private static List<string> Set(IEnumerable<string> ids) =>
            ids.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();

        // ---------------------------------------------------------------- target resolution

        [Fact]
        public void Target_ContractCancel_IsNonVirtual_AndBinds()
        {
            var method = ContractCancelPatch.ResolveTargetMethodForTesting() as MethodInfo;
            Assert.NotNull(method);
            Assert.Equal(typeof(Contract), method.DeclaringType);
            Assert.False(method.IsVirtual, "a prefix on a non-virtual Cancel covers every contract type");
            Assert.Equal(typeof(bool), method.ReturnType);
            Assert.Empty(method.GetParameters());
            MissionControlStockUiTests.AssertPatchBinds(typeof(ContractCancelPatch), "Prefix", method);
        }

        [Fact]
        public void Target_RebuildContracts_ResolvesAndBinds()
        {
            var method = ContractSystemRebuildContractsScopePatch.ResolveTargetMethodForTesting() as MethodInfo;
            Assert.NotNull(method);
            Assert.Equal(typeof(Contracts.ContractSystem), method.DeclaringType);
            Assert.True(method.IsPublic);
            Assert.Empty(method.GetParameters());
            MissionControlStockUiTests.AssertPatchBinds(typeof(ContractSystemRebuildContractsScopePatch), "Prefix", method);
            MissionControlStockUiTests.AssertPatchBinds(typeof(ContractSystemRebuildContractsScopePatch), "Finalizer", method);
            var finalizer = typeof(ContractSystemRebuildContractsScopePatch).GetMethod("Finalizer", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.Equal(typeof(Exception), finalizer.ReturnType);
        }

        [Fact]
        public void TargetMethod_OfBothCancelPatches_ResolvesWithoutAWarn()
        {
            foreach (var patch in new[] { typeof(ContractCancelPatch), typeof(ContractSystemRebuildContractsScopePatch) })
            {
                var target = patch.GetMethod("TargetMethod", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(target);
                Assert.NotNull(target.Invoke(null, null));
            }
            Assert.DoesNotContain(logLines, l => l.Contains("[WARN]"));
        }
    }
}
