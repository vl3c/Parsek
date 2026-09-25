using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using KSP.UI;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The VAB/SPH crew assignment dialog on stock mechanisms (StockUiCrewDialogDecoration.cs,
    /// Patches/CrewDialogReservationPatches.cs): every patched stock member resolves with the
    /// parameter names the patches bind, the per-kerbal decision and the row-look decision,
    /// and the pairing cells - the greyed set is exactly the set every seat backstop refuses,
    /// with the same text, and exactly the set the retired hiding filter used to hide
    /// (<see cref="KerbalsModule.ShouldFilterFromCrewDialog"/>).
    /// </summary>
    [Collection("Sequential")]
    public class StockUiCrewDialogTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> dialogTitles = new List<string>();
        private readonly List<string> dialogReasons = new List<string>();

        public StockUiCrewDialogTests()
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
            ParsekScenario.ResetInstanceForTesting();
            StockUiCrewDialogDecoration.ResetForTesting();
            CommittedActionDialog.TestHookForTesting = (action, reason, detail) =>
            {
                dialogTitles.Add(action);
                dialogReasons.Add(reason);
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
            ParsekScenario.ResetInstanceForTesting();
            StockUiCrewDialogDecoration.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // ---------------- target resolution: every patched stock member ----------------

        private static MethodBase Resolve(Type patch)
        {
            Assert.NotEmpty(patch.GetCustomAttributes(typeof(HarmonyPatch), inherit: false));
            MethodInfo helper = patch.GetMethod("ResolveTargetMethodForTesting",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(helper);
            var method = helper.Invoke(null, null) as MethodBase;
            Assert.NotNull(method);
            Assert.Equal(typeof(BaseCrewAssignmentDialog), method.DeclaringType);
            return method;
        }

        private static string[] ParamNames(MethodBase m) => m.GetParameters().Select(p => p.Name).ToArray();

        [Fact]
        public void Target_AddAvailItem_ResolvesTheOutOverload_WithTheBoundParameterNames()
        {
            var m = Resolve(typeof(CrewDialogAvailItemPatch));
            var ps = m.GetParameters();
            Assert.Equal(new[] { typeof(ProtoCrewMember), typeof(CrewListItem).MakeByRefType(), typeof(UIList), typeof(CrewListItem.ButtonTypes) },
                ps.Select(p => p.ParameterType).ToArray());
            Assert.True(ps[1].IsOut);
            // The postfix binds "crew" and "item" by name.
            Assert.Equal("crew", ps[0].Name);
            Assert.Equal("item", ps[1].Name);
            Assert.True(((MethodInfo)m).IsVirtual);
        }

        [Fact]
        public void Target_CreateAvailList_ResolvesThePrivateBuilder()
        {
            var m = Resolve(typeof(CrewDialogAvailListPatch));
            Assert.True(m.IsPrivate);
            Assert.Equal(new[] { typeof(VesselCrewManifest) }, m.GetParameters().Select(p => p.ParameterType).ToArray());
        }

        [Fact]
        public void Target_MoveCrewToEmptySeat_Resolves_WithItemToMove()
        {
            var m = Resolve(typeof(CrewDialogMoveToSeatPatch));
            Assert.Equal(new[] { "fromlist", "tolist", "itemToMove", "index" }, ParamNames(m));
            Assert.Equal(m, StockUiCrewDialogDecoration.ResolveMoveCrewToEmptySeat());
        }

        [Fact]
        public void Target_DropOnCrewList_Resolves_WithFromListAndInsertItem()
        {
            var m = Resolve(typeof(CrewDialogDropOnCrewListPatch));
            Assert.Equal(new[] { "fromList", "insertItem", "insertIndex" }, ParamNames(m));
        }

        [Fact]
        public void Target_ButtonFill_Resolves()
        {
            var m = Resolve(typeof(CrewDialogFillPatch));
            Assert.Empty(m.GetParameters());
            Assert.Equal(typeof(void), ((MethodInfo)m).ReturnType);
        }

        [Fact]
        public void TheEditorDialog_OverridesNoneOfTheDecoratedBuilders()
        {
            // The postfixes sit on the base class; an override that did not call base would
            // bypass them. CrewAssignmentDialog overrides the seat moves (and calls base, then
            // rebuilds), but neither AddAvailItem overload nor Fill.
            const BindingFlags declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var names = typeof(CrewAssignmentDialog).GetMethods(declared).Select(m => m.Name).ToList();
            Assert.DoesNotContain("AddAvailItem", names);
            Assert.DoesNotContain("CreateAvailList", names);
            Assert.DoesNotContain("ButtonFill", names);
            Assert.Contains("MoveCrewToEmptySeat", names);
            Assert.Contains("DropOnCrewList", names);
        }

        [Fact]
        public void TheInactiveLookMembers_TheApplicatorReads_Exist()
        {
            Assert.NotNull(AccessTools.Field(typeof(BaseCrewAssignmentDialog), "disabledCrewListSprite"));
            var color = AccessTools.Field(typeof(BaseCrewAssignmentDialog), "disabledColor");
            Assert.NotNull(color);
            Assert.True(color.IsStatic);
            Assert.NotNull(typeof(CrewListItem).GetField("kerbalName", BindingFlags.Instance | BindingFlags.Public));
            Assert.NotNull(typeof(CrewListItem).GetField("xp_trait", BindingFlags.Instance | BindingFlags.Public));
            Assert.NotNull(typeof(CrewListItem).GetField("kerbalSprite", BindingFlags.Instance | BindingFlags.Public));
            Assert.NotNull(typeof(CrewListItem).GetProperty("MouseoverEnabled"));
            Assert.NotNull(typeof(CrewListItem).GetMethod("SetButtonEnabled", new[] { typeof(bool), typeof(string), typeof(string) }));
            Assert.NotNull(typeof(UIDragPanel).GetField("dragEnabled"));
            Assert.NotNull(typeof(UIHoverPanel).GetField("backgroundNormal"));
            Assert.NotNull(typeof(UIHoverPanel).GetField("backgroundHover"));
            Assert.NotNull(typeof(UIHoverPanel).GetField("backgroundImage"));
        }

        // ---------------- pure decisions ----------------

        [Theory]
        [InlineData(true, false, "Grey")]
        [InlineData(true, true, "Grey")]
        [InlineData(false, true, "RestoreStock")]
        [InlineData(false, false, "LeaveStock")]
        public void DecideLook_GreysRefusedRows_AndRestoresOnlyWhatParsekGreyed(bool blocked, bool greyed, string expected)
        {
            Assert.Equal(expected, StockUiCrewDialogDecoration.DecideLook(blocked, greyed).ToString());
        }

        [Fact]
        public void Decide_NotRefused_IsUndecorated()
        {
            var d = StockUiCrewDialogDecoration.Decide("Bob", false, KerbalReservationKind.ReservedActive, null, null, null);
            Assert.False(d.Marked);
            Assert.False(d.Blocked);
            Assert.Null(d.Why);
            Assert.Null(d.Title);
            Assert.Equal(StockUiDecorationKind.None, d.Kind);
            Assert.Equal(StockUiScreen.CrewAssignment, d.Screen);
            Assert.Equal("Available", d.Tab);
        }

        [Fact]
        public void Decide_RetiredStandIn_NamesTheOwner_WithNoWayOutSentence()
        {
            var context = new AstronautComplexContext { SlotOwner = n => "Jebediah Kerman" };
            var d = StockUiCrewDialogDecoration.Decide("Standin Kerman", true, KerbalReservationKind.ReservedRetired,
                null, context, null);

            Assert.True(d.Marked);
            Assert.True(d.Blocked);
            Assert.Equal(StockUiDecorationKind.KerbalRetiredStandIn, d.Kind);
            Assert.Equal("Retired", d.Title);
            Assert.Equal("Stood in for Jebediah Kerman on a committed flight. Jebediah Kerman is free again, "
                + "so Parsek has retired this stand-in and they cannot join a new crew.", d.Why);
            AssertHouseStyle(d.Why);
        }

        [Fact]
        public void Decide_RefusedWithoutAKind_StaysBlocked_UnderTheFallbackTitle()
        {
            // Unreachable while the predicate is reserved-or-retired; a refusal must never
            // turn into an assignable row or a blank reason.
            var d = StockUiCrewDialogDecoration.Decide("Odd Kerman", true, KerbalReservationKind.NotManaged, null, null, null);
            Assert.True(d.Blocked);
            Assert.Equal(StockUiCrewDialogDecoration.FallbackTitle, d.Title);
            Assert.False(string.IsNullOrEmpty(d.Why));
        }

        [Fact]
        public void Decide_LostKerbal_ReadsTheLostText()
        {
            RecordingStore.AddRecordingWithTreeForTesting(MakeRecording("Doomed", new[] { "Val" }, TerminalState.Destroyed, 900));
            LedgerOrchestrator.SetKerbalsForTesting(KerbalsTestHelper.RecalculateFromStore());
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 100;

            var d = StockUiCrewDialogDecoration.DescribeCurrent("Val");

            Assert.True(d.Blocked);
            Assert.Equal(StockUiDecorationKind.KerbalLost, d.Kind);
            Assert.Equal("Lost", d.Title);
            Assert.StartsWith("Lost on", d.Why);
            AssertHouseStyle(d.Why);
        }

        [Fact]
        public void ShouldAllowAssignment_NoKerbalOrNoLedger_Allows_WithNoDialog()
        {
            Assert.True(StockUiCrewDialogDecoration.ShouldAllowAssignment(null, "test"));
            Assert.True(StockUiCrewDialogDecoration.ShouldAllowAssignment("", "test"));
            Assert.True(StockUiCrewDialogDecoration.ShouldAllowAssignment("Jeb", "test")); // no KerbalsModule
            Assert.Empty(dialogReasons);
        }

        // ---------------- pairing ----------------

        /// <summary>
        /// Jeb flies a committed flight that ends landed (open-ended hold), Bill one that is
        /// recovered later (finite hold), Val dies on one (permanent), StandIn1 is Jeb's
        /// active stand-in, Free Kerman is nobody's. The greyed rows, the refused seat
        /// placements, the seats the auto-assign swap clears and the rows the old filter hid
        /// are the same three kerbals, and the greyed row, the refusal dialog and the
        /// Astronaut Complex hover say the same thing.
        /// </summary>
        [Fact]
        public void Pairing_GreyedRowsAreExactlyTheRefusedPlacements_AndTheOldHiddenSet_WithTheSameText()
        {
            RecordingStore.AddRecordingWithTreeForTesting(MakeRecording("Mun Lander", new[] { "Jeb" }, TerminalState.Landed, 1000));
            RecordingStore.AddRecordingWithTreeForTesting(MakeRecording("Hopper", new[] { "Bill" }, TerminalState.Recovered, 2000));
            RecordingStore.AddRecordingWithTreeForTesting(MakeRecording("Doomed", new[] { "Val" }, TerminalState.Destroyed, 900));
            var kerbals = new KerbalsModule();
            var slotsNode = new ConfigNode("KERBAL_SLOTS");
            var slot = slotsNode.AddNode("SLOT");
            slot.AddValue("owner", "Jeb");
            slot.AddValue("trait", "Pilot");
            slot.AddNode("CHAIN_ENTRY").AddValue("name", "StandIn1");
            var parent = new ConfigNode();
            parent.AddNode(slotsNode);
            kerbals.LoadSlots(parent);
            kerbals = KerbalsTestHelper.RecalculateModule(kerbals);
            LedgerOrchestrator.SetKerbalsForTesting(kerbals);
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 100;

            var ids = new[] { "Jeb", "Bill", "Val", "StandIn1", "Free Kerman" };
            var decorations = ids.Select(StockUiCrewDialogDecoration.DescribeCurrent).ToList();

            var greyed = decorations.Where(d => d.Marked && d.Blocked).Select(d => d.Id).ToArray();
            var oldHidden = ids.Where(kerbals.ShouldFilterFromCrewDialog).ToArray();
            var seatCleared = ids.Where(id => CrewAutoAssignPatch.DecideSlotAction(id, kerbals,
                new Dictionary<string, string>(), out _) != CrewAutoAssignPatch.SlotAction.Keep).ToArray();
            var refused = ids.Where(id => !StockUiCrewDialogDecoration.ShouldAllowAssignment(id, "test")).ToArray();

            var expected = new[] { "Jeb", "Bill", "Val" };
            Assert.Equal(expected, greyed);
            Assert.Equal(expected, oldHidden);
            Assert.Equal(expected, seatCleared);
            Assert.Equal(expected, refused);

            // Row tooltip caption == refusal dialog text, per kerbal, in order.
            Assert.Equal(decorations.Where(d => d.Blocked).Select(d => d.Why).ToArray(), dialogReasons.ToArray());
            Assert.Equal(new[] { "Cannot assign \"Jeb\"", "Cannot assign \"Bill\"", "Cannot assign \"Val\"" }, dialogTitles.ToArray());

            // And the same text the Astronaut Complex shows for a held kerbal.
            foreach (var name in expected)
                Assert.Equal(KerbalDismissalPatch.DescribeHeldKerbal(kerbals, name),
                    decorations.Single(d => d.Id == name).Why);

            var jeb = decorations.Single(d => d.Id == "Jeb");
            Assert.Equal(StockUiDecorationKind.KerbalOnFlight, jeb.Kind);
            Assert.StartsWith("Reserved", jeb.Title);
            Assert.StartsWith("Flies ", jeb.Why);
            Assert.Equal(StockUiDecorationKind.KerbalOnFlight, decorations.Single(d => d.Id == "Bill").Kind);
            Assert.Equal(StockUiDecorationKind.KerbalLost, decorations.Single(d => d.Id == "Val").Kind);
            foreach (var d in decorations.Where(x => x.Blocked)) AssertHouseStyle(d.Why);

            Assert.Contains(logLines, l => l.Contains("[CrewDialog]") && l.Contains("Blocked crew assignment of 'Jeb' via test"));
            Assert.DoesNotContain(logLines, l => l.Contains("Blocked crew assignment of 'StandIn1'"));
            Assert.DoesNotContain(logLines, l => l.Contains("Blocked crew assignment of 'Free Kerman'"));
        }

        [Fact]
        public void Pairing_AReleasedHold_IsNeitherGreyedNorRefused()
        {
            // The same kerbal, before and after his only committed flight is removed: the row
            // decision is recomputed from the ledger each time, never carried over.
            RecordingStore.AddRecordingWithTreeForTesting(MakeRecording("Hopper", new[] { "Jeb" }, TerminalState.Recovered, 2000));
            LedgerOrchestrator.SetKerbalsForTesting(KerbalsTestHelper.RecalculateFromStore());
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 100;
            Assert.True(StockUiCrewDialogDecoration.DescribeCurrent("Jeb").Blocked);

            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            LedgerOrchestrator.SetKerbalsForTesting(KerbalsTestHelper.RecalculateFromStore());
            CommittedFutureIndexCache.ResetForTesting();

            var d = StockUiCrewDialogDecoration.DescribeCurrent("Jeb");
            Assert.False(d.Blocked);
            Assert.False(d.Marked);
            Assert.True(StockUiCrewDialogDecoration.ShouldAllowAssignment("Jeb", "test"));
            Assert.Equal(CrewDialogRowLook.RestoreStock, StockUiCrewDialogDecoration.DecideLook(d.Blocked, greyedByParsek: true));
            Assert.Empty(dialogReasons);
        }

        [Fact]
        public void PassLog_IsOneInfoLinePerBuild_PlusOneVerboseLinePerGreyedKerbal()
        {
            RecordingStore.AddRecordingWithTreeForTesting(MakeRecording("Mun Lander", new[] { "Jeb" }, TerminalState.Landed, 1000));
            LedgerOrchestrator.SetKerbalsForTesting(KerbalsTestHelper.RecalculateFromStore());
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 100;
            var decorations = new[] { "Jeb", "Bob", "Free Kerman" }.Select(StockUiCrewDialogDecoration.DescribeCurrent).ToList();
            logLines.Clear();

            StockUiDecorationQuery.LogPass(StockUiScreen.CrewAssignment,
                new[] { StockUiDecorationQuery.CrewAssignmentAvailableTab }, decorations);

            Assert.Single(logLines, l => l.Contains("decorate screen=CrewAssignment tab=Available items=3 marked=1 blocked=1"));
            Assert.Single(logLines, l => l.Contains("decorate screen=CrewAssignment tab=Available item=Jeb kind=KerbalOnFlight")
                && l.Contains("why=\"" + decorations[0].Why + "\""));
            Assert.Equal(2, logLines.Count);
        }

        private static void AssertHouseStyle(string text)
        {
            Assert.False(string.IsNullOrEmpty(text));
            Assert.DoesNotContain("—", text);
            Assert.True(text.All(c => c < 128), "non-ASCII in: " + text);
        }

        private static Recording MakeRecording(string vesselName, string[] crew, TerminalState terminal, double endUT)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            foreach (var c in crew)
                part.AddValue("crew", c);
            var rec = new Recording
            {
                VesselName = vesselName,
                VesselSnapshot = snapshot,
                TerminalStateValue = terminal,
                ExplicitStartUT = 0,
                ExplicitEndUT = endUT,
                CrewEndStates = new Dictionary<string, KerbalEndState>()
            };
            var endCrewSet = new HashSet<string>(crew);
            for (int i = 0; i < crew.Length; i++)
                rec.CrewEndStates[crew[i]] = KerbalsModule.InferCrewEndState(crew[i], terminal, endCrewSet);
            return rec;
        }
    }
}
