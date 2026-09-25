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
    /// Stock-UI overlays PR 2b: the Mission Control stock-control annotations
    /// (MissionControlStockAnnotation / MissionControlStockUi), the Decline backstop
    /// (ContractDeclinePatch), the Contract Configurator hooks, and the Harmony target
    /// resolution + parameter binding of every patch (a wrong parameter name makes
    /// Harmony refuse the whole patch class at game load, which no other cell sees).
    /// </summary>
    [Collection("Sequential")]
    public class MissionControlStockUiTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private int dialogCount;
        private string dialogAction;
        private string dialogReason;

        private static readonly Func<double, string> Fmt =
            ut => "D" + ((long)(ut / 100.0)).ToString(CultureInfo.InvariantCulture);

        public MissionControlStockUiTests()
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

        private static GameAction Accept(double ut, string id) =>
            new GameAction { UT = ut, Type = GameActionType.ContractAccept, ContractId = id };

        private static CommittedFutureIndex Index(params GameAction[] actions) =>
            CommittedFutureIndex.Build(actions, id => true, id => id == "rec" ? "Mun Lander 3" : null, null);

        // ---------------------------------------------------------------- pure: decision

        [Fact]
        public void Decide_OfferedWithACommittedFutureAccept_IsMarkedAndBlocked()
        {
            var index = Index(Accept(500, "c1"));

            var d = MissionControlStockAnnotation.Decide(index, 100, "c1", Contract.State.Offered, Fmt);

            Assert.True(d.Marked);
            Assert.True(d.Blocked);
            Assert.Equal(StockUiDecorationKind.ContractAccept, d.Kind);
            Assert.Equal("Available", d.Tab);
            Assert.Equal("Accepted on D5", d.Title);
            Assert.StartsWith("Accepted on D5 on your committed timeline.", d.Why);
        }

        [Theory]
        [InlineData(Contract.State.Active)]
        [InlineData(Contract.State.Completed)]
        [InlineData(Contract.State.Declined)]
        public void Decide_NotOffered_IsNeverDecorated(Contract.State state)
        {
            var index = Index(Accept(500, "c1"));

            var d = MissionControlStockAnnotation.Decide(index, 100, "c1", state, Fmt);

            Assert.False(d.Marked);
            Assert.False(d.Blocked);
        }

        [Fact]
        public void Decide_PastAcceptOrUnknownOrEmptyKey_IsNotDecorated()
        {
            var index = Index(Accept(50, "past"));

            Assert.False(MissionControlStockAnnotation.Decide(index, 100, "past", Contract.State.Offered, Fmt).Marked);
            Assert.False(MissionControlStockAnnotation.Decide(index, 100, "other", Contract.State.Offered, Fmt).Marked);
            Assert.False(MissionControlStockAnnotation.Decide(index, 100, "", Contract.State.Offered, Fmt).Marked);
            Assert.False(MissionControlStockAnnotation.Decide(null, 100, "past", Contract.State.Offered, Fmt).Marked);
        }

        // ---------------------------------------------------------------- pure: row label

        [Fact]
        public void StockDefaultLabel_MirrorsStockAddItem()
        {
            // KSP 1.12.5 MissionControl.AddItem with label == "":
            //   mCListItem.Setup(contract, "<color=#fefa87>" + contract.Title + "</color>")
            Assert.Equal("<color=#fefa87>Explore the Mun</color>",
                MissionControlStockAnnotation.StockDefaultLabel("Explore the Mun"));
        }

        [Fact]
        public void ComposeRowLabel_EmptyStockLabel_BuildsTheFullStockTitlePlusStatus()
        {
            var d = MissionControlStockAnnotation.Decide(Index(Accept(500, "c1")), 100, "c1", Contract.State.Offered, Fmt);

            string label = MissionControlStockAnnotation.ComposeRowLabel("", "Explore the Mun", d);

            Assert.Equal("<color=#fefa87>Explore the Mun</color> <color=#8fd3ff>- accepted on D5 on your committed timeline</color>",
                label);
        }

        [Fact]
        public void ComposeRowLabel_ExistingLabel_IsKeptAndExtended()
        {
            var d = MissionControlStockAnnotation.Decide(Index(Accept(500, "c1")), 100, "c1", Contract.State.Offered, Fmt);
            string ccTitle = "<b><color=#fefa87>Explore the Mun</color></b>";

            string label = MissionControlStockAnnotation.ComposeRowLabel(ccTitle, "ignored", d);

            Assert.StartsWith(ccTitle + MissionControlStockAnnotation.RowStatusMarker, label);
        }

        [Fact]
        public void ComposeRowLabel_Unmarked_LeavesTheLabelExactlyAsStockDrewIt()
        {
            var d = MissionControlStockAnnotation.Decide(Index(), 100, "c1", Contract.State.Offered, Fmt);

            Assert.Equal("<color=#00ff00>Completed </color> <color=#fefa87>T</color>",
                MissionControlStockAnnotation.ComposeRowLabel("<color=#00ff00>Completed </color> <color=#fefa87>T</color>", "T", d));
            Assert.Equal("<color=#fefa87>T</color>", MissionControlStockAnnotation.ComposeRowLabel("", "T", d));
        }

        [Fact]
        public void ComposeRowLabel_IsIdempotent_AndStripRemovesTheStatus()
        {
            var d = MissionControlStockAnnotation.Decide(Index(Accept(500, "c1")), 100, "c1", Contract.State.Offered, Fmt);
            string once = MissionControlStockAnnotation.ComposeRowLabel("", "T", d);

            string twice = MissionControlStockAnnotation.ComposeRowLabel(once, "T", d);

            Assert.Equal(once, twice);
            Assert.True(MissionControlStockAnnotation.HasRowStatus(once));
            Assert.Equal("<color=#fefa87>T</color>", MissionControlStockAnnotation.StripRowStatus(once));
            Assert.False(MissionControlStockAnnotation.HasRowStatus(MissionControlStockAnnotation.StripRowStatus(once)));
        }

        [Theory]
        [InlineData("Accepted on Y2, D114", "accepted on Y2, D114 on your committed timeline")]
        [InlineData("", "accepted on your committed timeline")]
        [InlineData(null, "accepted on your committed timeline")]
        public void RowStatus_LowercasesTheTitleAndNamesTheTimeline(string title, string expected)
        {
            Assert.Equal(expected, MissionControlStockAnnotation.RowStatus(title));
        }

        [Fact]
        public void RowText_IsPlainAsciiWithNoEmDash()
        {
            var d = MissionControlStockAnnotation.Decide(Index(Accept(500, "c1")), 100, "c1", Contract.State.Offered, Fmt);
            string label = MissionControlStockAnnotation.ComposeRowLabel("", "T", d);
            string detail = MissionControlStockAnnotation.ComposeDetailText("stock", d);

            Assert.All(label + detail, ch => Assert.True(ch < 128, "non-ASCII char " + (int)ch));
        }

        // ---------------------------------------------------------------- pure: detail text

        [Fact]
        public void ComposeDetailText_Blocked_AppendsHeadingAndWhyOnce()
        {
            var d = MissionControlStockAnnotation.Decide(Index(Accept(500, "c1")), 100, "c1", Contract.State.Offered, Fmt);

            string text = MissionControlStockAnnotation.ComposeDetailText("<b>Stock</b> body", d);
            string again = MissionControlStockAnnotation.ComposeDetailText(text, d);

            Assert.Equal("<b>Stock</b> body\n\n<b><color=#8fd3ff>Accept and Decline are unavailable</color></b>\n" + d.Why, text);
            Assert.Equal(text, again);
        }

        [Fact]
        public void ComposeDetailText_NotBlocked_StripsAnEarlierBlock()
        {
            var blocked = MissionControlStockAnnotation.Decide(Index(Accept(500, "c1")), 100, "c1", Contract.State.Offered, Fmt);
            var free = MissionControlStockAnnotation.Decide(Index(), 100, "c1", Contract.State.Offered, Fmt);
            string withBlock = MissionControlStockAnnotation.ComposeDetailText("Stock", blocked);

            Assert.Equal("Stock", MissionControlStockAnnotation.ComposeDetailText(withBlock, free));
            Assert.Equal("Stock", MissionControlStockAnnotation.ComposeDetailText("Stock", free));
        }

        [Theory]
        [InlineData(Contract.State.Offered, "Available")]
        [InlineData(Contract.State.Active, "Active")]
        [InlineData(Contract.State.Failed, "Archive")]
        public void TabFor_MapsTheContractState(Contract.State state, string tab)
        {
            Assert.Equal(tab, MissionControlStockAnnotation.TabFor(state));
        }

        [Theory]
        [InlineData(MissionControl.DisplayMode.Available, "Available")]
        [InlineData(MissionControl.DisplayMode.Active, "Active")]
        [InlineData(MissionControl.DisplayMode.Archive, "Archive")]
        public void TabForDisplayMode_MapsTheStockTab(MissionControl.DisplayMode mode, string tab)
        {
            Assert.Equal(tab, MissionControlStockUi.TabForDisplayMode(mode));
        }

        // ---------------------------------------------------------------- AddItem label + pass log

        [Fact]
        public void LabelForAddItem_RebuildPass_LabelsOnlyTheCommittedAccept_AndLogsOnePass()
        {
            var committed = new FakeContract("Committed", Guid.NewGuid(), Contract.State.Offered);
            var free = new FakeContract("Free", Guid.NewGuid(), Contract.State.Offered);
            var active = new FakeContract("Running", Guid.NewGuid(), Contract.State.Active);
            Ledger.AddAction(Accept(500, committed.ContractGuid.ToString()));
            Ledger.AddAction(Accept(500, active.ContractGuid.ToString()));

            MissionControlStockUi.BeginRebuildPass(MissionControl.DisplayMode.Available);
            string committedLabel = MissionControlStockUi.LabelForAddItem(committed, "");
            string freeLabel = MissionControlStockUi.LabelForAddItem(free, "");
            string activeLabel = MissionControlStockUi.LabelForAddItem(active, "");
            MissionControlStockUi.EndRebuildPass();

            Assert.StartsWith("<color=#fefa87>Committed</color>" + MissionControlStockAnnotation.RowStatusMarker, committedLabel);
            Assert.Equal("", freeLabel);
            Assert.Equal("", activeLabel);
            Assert.Contains(logLines, l => l.Contains("[INFO][StockUiOverlay]")
                && l.EndsWith("decorate screen=MissionControl tab=Available items=2 marked=1 blocked=1"));
            Assert.Contains(logLines, l => l.Contains("[INFO][StockUiOverlay]")
                && l.EndsWith("decorate screen=MissionControl tab=Active items=1 marked=0 blocked=0"));
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][StockUiOverlay]")
                && l.Contains("item=" + committed.ContractGuid + " kind=ContractAccept marked=true blocked=true"));
            Assert.Equal(1, logLines.Count(l => l.Contains("tab=Available items=")));
        }

        [Fact]
        public void LabelForAddItem_OutsideAPass_StillDecides()
        {
            var committed = new FakeContract("Committed", Guid.NewGuid(), Contract.State.Offered);
            Ledger.AddAction(Accept(500, committed.ContractGuid.ToString()));

            string label = MissionControlStockUi.LabelForAddItem(committed, "");

            Assert.True(MissionControlStockAnnotation.HasRowStatus(label));
            Assert.Contains(logLines, l => l.Contains("AddItem ran outside RebuildContractList"));
            Assert.Null(MissionControlStockUi.LabelForAddItem(null, null));
        }

        // ---------------------------------------------------------------- row payloads

        [Fact]
        public void ExtractContractFromData_ReadsStockSelectionBareContractAndCcContainer()
        {
            var c = new FakeContract("C", Guid.NewGuid(), Contract.State.Offered);

            Assert.Same(c, MissionControlStockUi.ExtractContractFromData(new MissionControl.MissionSelection(true, c, null)));
            Assert.Same(c, MissionControlStockUi.ExtractContractFromData(c));
            Assert.Same(c, MissionControlStockUi.ExtractContractFromData(new CcShape.MissionControlUI.ContractContainer { contract = c }));
            Assert.Same(c, MissionControlStockUi.ExtractContractFromData(new CcShape.MissionControlUI.ContractContainer
            {
                missionSelection = new MissionControl.MissionSelection(true, c, null)
            }));
        }

        [Fact]
        public void ExtractContractFromData_ContractTypeRowGroupRowAndNull_AreNotContracts()
        {
            Assert.Null(MissionControlStockUi.ExtractContractFromData(null));
            Assert.Null(MissionControlStockUi.ExtractContractFromData(new CcShape.MissionControlUI.ContractContainer()));
            Assert.Null(MissionControlStockUi.ExtractContractFromData(new CcShape.MissionControlUI.GroupContainer()));
            Assert.Null(MissionControlStockUi.ExtractContractFromData("not a row"));
        }

        // ---------------------------------------------------------------- Decline backstop

        [Fact]
        public void DeclineBackstop_CommittedAccept_RefusesWithTheAcceptText()
        {
            Ledger.AddAction(Accept(500, "c1"));

            bool allowed = ContractDeclinePatch.ShouldAllowDecline("c1", "Explore the Mun");

            Assert.False(allowed);
            Assert.Equal(1, dialogCount);
            Assert.Equal("Cannot decline \"Explore the Mun\"", dialogAction);
            Assert.Equal(StockUiReservationPredicates.ExplainContractAccept(
                CommittedFutureIndexCache.Current, "c1", 100, ReservationExplanation.DefaultDateFormatter).Body, dialogReason);
            Assert.Contains(logLines, l => l.Contains("[INFO][ContractDeclinePatch]")
                && l.Contains("blocking decline for guid=c1 - committed future accept ut=500 nowUT=100 recording=(ksc)"));
        }

        [Fact]
        public void DeclineBackstop_NoCommittedAccept_Allows()
        {
            Ledger.AddAction(Accept(50, "past"));

            Assert.True(ContractDeclinePatch.ShouldAllowDecline("past", "Past"));
            Assert.True(ContractDeclinePatch.ShouldAllowDecline("free", "Free"));
            Assert.True(ContractDeclinePatch.ShouldAllowDecline("", "Empty"));
            Assert.Equal(0, dialogCount);
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][ContractDeclinePatch]") && l.Contains("allowing decline for guid=free"));
        }

        [Fact]
        public void DeclineBackstop_BypassesWhileReplayingActions()
        {
            Ledger.AddAction(Accept(500, "c1"));
            GameStateRecorder.IsReplayingActions = true;

            Assert.True(ContractDeclinePatch.ShouldAllowDecline("c1", "c1"));
            Assert.Equal(0, dialogCount);
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][ContractDeclinePatch]") && l.Contains("bypass - replay in progress"));
        }

        // ---------------------------------------------------------------- pairing

        /// <summary>
        /// The pairing rule for Mission Control: the rows the label marks, the contracts
        /// whose detail panel greys Accept + Decline, the Accept refusals, the Decline
        /// refusals and the CC CanAccept refusals are ONE set, over the ledger-backed
        /// cached index, and the refusal dialogs say what the panel says.
        /// </summary>
        [Theory]
        [InlineData(0.0)]
        [InlineData(100.0)]
        [InlineData(250.0)]
        [InlineData(1000.0)]
        public void Pairing_RowMark_PanelBlock_AcceptBlock_DeclineBlock_CcCanAccept_AreOneSet(double now)
        {
            Ledger.AddAction(Accept(200, "c-200"));
            Ledger.AddAction(Accept(500, "c-500"));
            Ledger.AddAction(Accept(50, "c-past"));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => now;
            var ids = new[] { "c-200", "c-500", "c-past", "c-free" };
            var index = CommittedFutureIndexCache.Current;

            var decisions = ids.ToDictionary(id => id,
                id => MissionControlStockAnnotation.Decide(index, now, id, Contract.State.Offered, ReservationExplanation.DefaultDateFormatter));
            var rowMarked = Set(ids.Where(id => MissionControlStockAnnotation.HasRowStatus(
                MissionControlStockAnnotation.ComposeRowLabel("", id, decisions[id]))));
            var panelBlocked = Set(ids.Where(id => MissionControlStockAnnotation.ComposeDetailText("stock", decisions[id]) != "stock"));
            var acceptRefused = Set(ids.Where(id => !ContractAcceptPatch.ShouldAllowAccept(id, id)));
            var declineRefused = Set(ids.Where(id => !ContractDeclinePatch.ShouldAllowDecline(id, id)));
            var ccRefused = Set(ids.Where(id => !ContractConfiguratorCompat.FilterCanAccept(true, decisions[id], false)));
            var expected = Set(index.FutureKeys(CommittedFutureKind.ContractAccept, now));

            Assert.Equal(expected, rowMarked);
            Assert.Equal(expected, panelBlocked);
            Assert.Equal(expected, acceptRefused);
            Assert.Equal(expected, declineRefused);
            Assert.Equal(expected, ccRefused);
        }

        [Fact]
        public void Pairing_DeclineDialogAndDetailPanel_SayTheSameThing()
        {
            Ledger.AddAction(Accept(500, "c1"));
            var d = MissionControlStockAnnotation.Decide(CommittedFutureIndexCache.Current, 100, "c1",
                Contract.State.Offered, ReservationExplanation.DefaultDateFormatter);

            ContractDeclinePatch.ShouldAllowDecline("c1", "T");

            Assert.Equal(d.Why, dialogReason);
            Assert.EndsWith("\n" + dialogReason, MissionControlStockAnnotation.ComposeDetailText("stock", d));
        }

        private static List<string> Set(IEnumerable<string> ids) =>
            ids.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();

        // ---------------------------------------------------------------- CC

        [Fact]
        public void FilterCanAccept_OnlyEverTurnsTrueIntoFalseForABlockedContract()
        {
            var blocked = new StockUiDecoration { Blocked = true };
            var free = new StockUiDecoration { Blocked = false };

            Assert.False(ContractConfiguratorCompat.FilterCanAccept(true, blocked, false));
            Assert.True(ContractConfiguratorCompat.FilterCanAccept(true, free, false));
            Assert.False(ContractConfiguratorCompat.FilterCanAccept(false, free, false));
            Assert.False(ContractConfiguratorCompat.FilterCanAccept(false, blocked, false));
            Assert.True(ContractConfiguratorCompat.FilterCanAccept(true, blocked, true));
        }

        [Fact]
        public void Cc_Absent_TypesDoNotResolve_PrepareSkipsEveryCcPatch_WithoutAWarn()
        {
            Assert.Null(ContractConfiguratorCompat.ResolveType(ContractConfiguratorCompat.ContractConfiguratorTypeName));
            Assert.Null(ContractConfiguratorCompat.ResolveType(ContractConfiguratorCompat.MissionControlUiTypeName));
            Assert.Null(ContractConfiguratorCompat.ResolveCanAccept(null));
            Assert.Null(ContractConfiguratorCompat.ResolveSetContractTitle(null));
            Assert.Null(ContractConfiguratorCompat.ResolveListRebuild(null, "OnClickAvailable"));

            foreach (var patch in new[] { typeof(ContractConfiguratorCanAcceptPatch),
                         typeof(ContractConfiguratorContractTitlePatch), typeof(ContractConfiguratorListRebuildPatch) })
            {
                var prepare = patch.GetMethod("Prepare", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(prepare);
                Assert.False((bool)prepare.Invoke(null, null), patch.Name + " must not patch without CC");
            }

            Assert.False(ContractConfiguratorCompat.TryInvokeCanAccept(
                new FakeContract("C", Guid.NewGuid(), Contract.State.Offered), out bool result));
            Assert.True(result);
            Assert.DoesNotContain(logLines, l => l.Contains("[WARN]"));
            Assert.Equal(1, logLines.Count(l => l.Contains("Contract Configurator not loaded")));
        }

        [Fact]
        public void Cc_Present_ResolvesAgainstTheCcSourceShape_AndThePostfixesBind()
        {
            MethodInfo canAccept = ContractConfiguratorCompat.ResolveCanAccept(typeof(CcShape.ContractConfigurator));
            MethodInfo title = ContractConfiguratorCompat.ResolveSetContractTitle(typeof(CcShape.MissionControlUI));
            MethodInfo available = ContractConfiguratorCompat.ResolveListRebuild(typeof(CcShape.MissionControlUI), "OnClickAvailable");
            MethodInfo all = ContractConfiguratorCompat.ResolveListRebuild(typeof(CcShape.MissionControlUI), "OnClickAll");

            Assert.NotNull(canAccept);
            Assert.True(canAccept.IsStatic);
            Assert.NotNull(title);
            Assert.True(title.IsFamily, "CC's SetContractTitle is protected");
            Assert.NotNull(available);
            Assert.NotNull(all);

            AssertPatchBinds(typeof(ContractConfiguratorCanAcceptPatch), "Postfix", canAccept);
            AssertPatchBinds(typeof(ContractConfiguratorContractTitlePatch), "Postfix", title);
            AssertPatchBinds(typeof(ContractConfiguratorListRebuildPatch), "Postfix", available);
            AssertPatchBinds(typeof(ContractConfiguratorListRebuildPatch), "Postfix", all);
        }

        [Fact]
        public void Cc_WrongShape_DoesNotResolve()
        {
            Assert.Null(ContractConfiguratorCompat.ResolveCanAccept(typeof(CcShape.WrongShape)));
            Assert.Null(ContractConfiguratorCompat.ResolveSetContractTitle(typeof(CcShape.WrongShape)));
            Assert.Null(ContractConfiguratorCompat.ResolveListRebuild(typeof(CcShape.WrongShape), "OnClickAvailable"));
        }

        // ---------------------------------------------------------------- stock target resolution

        [Fact]
        public void Target_RebuildContractList_ResolvesAndBinds()
        {
            var method = MissionControlRebuildPassPatch.ResolveTargetMethodForTesting();
            Assert.NotNull(method);
            Assert.Equal(typeof(MissionControl), method.DeclaringType);
            Assert.True(method.IsPublic);
            Assert.Empty(method.GetParameters());
            AssertPatchBinds(typeof(MissionControlRebuildPassPatch), "Prefix", method);
            AssertPatchBinds(typeof(MissionControlRebuildPassPatch), "Postfix", method);
        }

        [Fact]
        public void Target_AddItem_IsTheLabelOverload_AndBinds()
        {
            var method = MissionControlAddItemLabelPatch.ResolveTargetMethodForTesting();
            Assert.NotNull(method);
            Assert.Equal(typeof(MissionControl), method.DeclaringType);
            Assert.Equal(new[] { "contract", "isAvailable", "label" }, method.GetParameters().Select(p => p.Name).ToArray());
            Assert.Equal(new[] { typeof(Contract), typeof(bool), typeof(string) },
                method.GetParameters().Select(p => p.ParameterType).ToArray());
            AssertPatchBinds(typeof(MissionControlAddItemLabelPatch), "Prefix", method);
        }

        [Fact]
        public void Target_UpdateInfoPanelContract_ResolvesAndBinds()
        {
            var method = MissionControlInfoPanelPatch.ResolveTargetMethodForTesting();
            Assert.NotNull(method);
            Assert.Equal(typeof(MissionControl), method.DeclaringType);
            Assert.Equal("contract", method.GetParameters().Single().Name);
            AssertPatchBinds(typeof(MissionControlInfoPanelPatch), "Postfix", method);
        }

        [Fact]
        public void Target_RefreshUIControls_IsThePrivateStockMethod_AndBinds()
        {
            var method = MissionControlRefreshUIControlsPatch.ResolveTargetMethodForTesting();
            Assert.NotNull(method);
            Assert.Equal(typeof(MissionControl), method.DeclaringType);
            Assert.True(method.IsPrivate);
            Assert.Empty(method.GetParameters());
            AssertPatchBinds(typeof(MissionControlRefreshUIControlsPatch), "Postfix", method);
        }

        [Fact]
        public void Target_ContractDecline_IsNonVirtual_AndBinds()
        {
            var method = ContractDeclinePatch.ResolveTargetMethodForTesting() as MethodInfo;
            Assert.NotNull(method);
            Assert.Equal(typeof(Contract), method.DeclaringType);
            Assert.False(method.IsVirtual, "a prefix on a non-virtual Decline covers every contract type");
            Assert.Equal(typeof(bool), method.ReturnType);
            AssertPatchBinds(typeof(ContractDeclinePatch), "Prefix", method);
        }

        [Fact]
        public void TargetMethod_OfEveryStockPatch_ResolvesWithoutAWarn()
        {
            foreach (var patch in new[] { typeof(MissionControlRebuildPassPatch), typeof(MissionControlAddItemLabelPatch),
                         typeof(MissionControlInfoPanelPatch), typeof(MissionControlRefreshUIControlsPatch),
                         typeof(ContractDeclinePatch) })
            {
                var target = patch.GetMethod("TargetMethod", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(target);
                Assert.NotNull(target.Invoke(null, null));
            }
            Assert.DoesNotContain(logLines, l => l.Contains("[WARN]"));
        }

        /// <summary>
        /// Harmony binds a patch parameter by NAME (or <c>__N</c> by position) and refuses
        /// the whole class when one does not match. Mirror that binding here.
        /// </summary>
        private static void AssertPatchBinds(Type patchClass, string patchMethodName, MethodBase target)
        {
            MethodInfo patch = patchClass.GetMethod(patchMethodName,
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.True(patch != null, patchClass.Name + "." + patchMethodName + " missing");
            ParameterInfo[] targetParams = target.GetParameters();
            foreach (ParameterInfo p in patch.GetParameters())
            {
                Type type = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
                string where = patchClass.Name + "." + patchMethodName + "(" + p.Name + ")";
                if (p.Name == "__instance")
                {
                    Assert.False(target.IsStatic, where + " on a static target");
                    Assert.True(type.IsAssignableFrom(target.DeclaringType), where);
                    continue;
                }
                if (p.Name == "__result")
                {
                    Assert.Equal(((MethodInfo)target).ReturnType, type);
                    continue;
                }
                if (p.Name == "__originalMethod")
                    continue;
                if (p.Name.StartsWith("__", StringComparison.Ordinal)
                    && int.TryParse(p.Name.Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int at))
                {
                    Assert.True(at < targetParams.Length, where + " index past the target's parameters");
                    Assert.True(type.IsAssignableFrom(targetParams[at].ParameterType), where);
                    continue;
                }
                ParameterInfo match = targetParams.SingleOrDefault(t => t.Name == p.Name);
                Assert.True(match != null, where + " has no same-named target parameter");
                Assert.True(type.IsAssignableFrom(match.ParameterType), where);
                if (p.ParameterType.IsByRef)
                    Assert.False(match.ParameterType.IsByRef, where);
            }
        }
    }
}

namespace Parsek.Tests.CcShape
{
    // The Contract Configurator member shapes Parsek hooks, copied from CC's source
    // (mods/ContractConfigurator/source/ContractConfigurator: ContractConfigurator.cs:570,
    // MissionControlUI.cs:215 / :278 / :766 / :798 / :1398). Different full names on
    // purpose, so the absent-CC cells above still see no CC type in this process.

    public class ContractConfigurator
    {
        public static bool CanAccept(Contracts.Contract contract) => true;
    }

    public class MissionControlUI
    {
        public class Container
        {
            public UnityEngine.Transform listItemTransform;
            public KSP.UI.Screens.MCListItem mcListItem;
        }

        public class GroupContainer : Container
        {
            public GroupContainer parent;
            public bool expanded;
        }

        public class ContractContainer : Container
        {
            public Contracts.Contract contract;
            public KSP.UI.Screens.MissionControl.MissionSelection missionSelection;
            public int indent;
            public GroupContainer parent;
        }

#pragma warning disable 0414
        private bool displayModeAll = true;
#pragma warning restore 0414

        public void OnClickAvailable(bool selected) { }
        public void OnClickAll(bool selected) { }
        protected void SetContractTitle(KSP.UI.Screens.MCListItem mcListItem, ContractContainer cc) { }
    }

    public class WrongShape
    {
        public bool CanAccept(Contracts.Contract contract) => true;
        public void OnClickAvailable(int selected) { }
        protected void SetContractTitle(string mcListItem, object cc) { }
    }
}
