using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using KSP.UI;
using KSP.UI.Screens;
using KSP.UI.TooltipTypes;
using Parsek.Patches;
using Upgradeables;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The KSC facility context menu annotation (StockUiFacilityDecoration.cs,
    /// Patches/FacilityMenuDecorationPatch.cs): the stock members it reads and patches still
    /// resolve (including the private Upgrade button through a Harmony FieldRef), the pure
    /// decision and text helpers, and the pairing cells - the facilities whose Upgrade the
    /// menu greys out are exactly the ones the upgrade refusal refuses, with the same text,
    /// including the F1 case where the block lifts once the clock passes the committed UT.
    /// </summary>
    [Collection("Sequential")]
    public class FacilityMenuStockUiTests : IDisposable
    {
        private const string LaunchPadId = "SpaceCenter/LaunchPad";
        private const string VabId = "SpaceCenter/VehicleAssemblyBuilding";
        private const string TrackingId = "SpaceCenter/TrackingStation";

        private readonly List<string> logLines = new List<string>();
        private readonly List<(string title, string reason)> dialogs = new List<(string, string)>();

        private static readonly Func<double, string> Fmt =
            ut => "D" + ((long)(ut / 100.0)).ToString(CultureInfo.InvariantCulture);

        public FacilityMenuStockUiTests()
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
            CommittedFutureIndexCache.ResetForTesting();
            StockUiFacilityDecoration.ResetForTesting();
            // Headless: stock's localized names are unavailable, so the humanized id is used.
            FacilityDisplayNames.FacilityNameLookupForTesting = id => null;
            CommittedActionDialog.TestHookForTesting = (title, reason, detail) => dialogs.Add((title, reason));
        }

        public void Dispose()
        {
            CommittedActionDialog.TestHookForTesting = null;
            FacilityDisplayNames.FacilityNameLookupForTesting = null;
            GameStateRecorder.IsReplayingActions = false;
            StockUiFacilityDecoration.ResetForTesting();
            CommittedFutureIndexCache.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static GameAction Upgrade(double ut, string facilityId, int toLevel) =>
            new GameAction
            {
                UT = ut,
                Type = GameActionType.FacilityUpgrade,
                FacilityId = facilityId,
                ToLevel = toLevel,
                FacilityCost = 75000f
            };

        // ---------------- target resolution ----------------

        [Fact]
        public void Target_OnFacilityValuesModified_ResolvesTheProtectedParameterlessMethod()
        {
            Assert.NotEmpty(typeof(FacilityMenuUpgradeBlockPatch).GetCustomAttributes(typeof(HarmonyPatch), false));
            var m = (MethodInfo)FacilityMenuUpgradeBlockPatch.ResolveTargetMethodForTesting();
            Assert.NotNull(m);
            Assert.Equal(typeof(KSCFacilityContextMenu), m.DeclaringType);
            Assert.Equal("OnFacilityValuesModified", m.Name);
            Assert.True(m.IsFamily, "stock declares it protected");
            Assert.False(m.IsVirtual);
            Assert.Equal(typeof(void), m.ReturnType);
            Assert.Empty(m.GetParameters());
        }

        [Fact]
        public void Target_UpgradeButton_IsThePrivateButtonField_AndItsFieldRefResolves()
        {
            var field = AccessTools.Field(typeof(KSCFacilityContextMenu), StockUiFacilityDecoration.UpgradeButtonFieldName);
            Assert.NotNull(field);
            Assert.True(field.IsPrivate);
            // The test project does not reference UnityEngine.UI, so the type is read by name.
            Assert.Equal("UnityEngine.UI.Button", field.FieldType.FullName);
            var resolve = typeof(StockUiFacilityDecoration).GetMethod("ResolveUpgradeButtonRefForTesting",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(resolve);
            Assert.NotNull(resolve.Invoke(null, null));
        }

        [Fact]
        public void Target_Host_IsTheProtectedBuildingField_AndItsFieldRefResolves()
        {
            var field = AccessTools.Field(typeof(KSCFacilityContextMenu), StockUiFacilityDecoration.HostFieldName);
            Assert.NotNull(field);
            Assert.True(field.IsFamily);
            Assert.Equal(typeof(SpaceCenterBuilding), field.FieldType);
            Assert.NotNull(StockUiFacilityDecoration.ResolveHostRefForTesting());
            var facility = typeof(SpaceCenterBuilding).GetProperty("Facility");
            Assert.NotNull(facility);
            Assert.Equal(typeof(UpgradeableFacility), facility.PropertyType);
        }

        [Fact]
        public void Target_TheFallbackDescriptionField_Exists()
        {
            var field = typeof(KSCFacilityContextMenu).GetField(StockUiFacilityDecoration.DescriptionFieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(field);
            Assert.True(field.IsPublic);
        }

        [Fact]
        public void Target_TooltipControllerText_CarriesThePrefabTextAndRequireInteractableMembers()
        {
            var prefab = typeof(TooltipController_Text).GetField("prefab");
            Assert.NotNull(prefab);
            Assert.Equal(typeof(Tooltip_Text), prefab.FieldType);
            Assert.Equal(typeof(string), typeof(TooltipController_Text).GetField("textString").FieldType);
            Assert.NotNull(typeof(TooltipController_Text).GetMethod("SetText", new[] { typeof(string) }));
            var require = typeof(TooltipController).GetField("RequireInteractable");
            Assert.NotNull(require);
            Assert.Equal(typeof(bool), require.FieldType);
        }

        [Fact]
        public void Target_TheMenuSpawnEvents_AndTheBackstopTargets_Exist()
        {
            Assert.Equal(typeof(EventData<KSCFacilityContextMenu>),
                typeof(GameEvents).GetField("onFacilityContextMenuSpawn").FieldType);
            Assert.Equal(typeof(EventData<KSCFacilityContextMenu>),
                typeof(GameEvents).GetField("onFacilityContextMenuDespawn").FieldType);
            Assert.NotNull(AccessTools.Method(typeof(SpaceCenterBuilding), "UpgradeFacility", new[] { typeof(bool) }));
            Assert.NotNull(AccessTools.Method(typeof(UpgradeableFacility), nameof(UpgradeableFacility.SetLevel), new[] { typeof(int) }));
        }

        // ---------------- pure decision ----------------

        [Fact]
        public void ForFacilityMenu_BeforeTheCommittedUpgrade_MarksAndBlocksWithTheExplanation()
        {
            Ledger.AddAction(Upgrade(1000, LaunchPadId, 2));
            var d = StockUiDecorationQuery.ForFacilityMenu(CommittedFutureIndexCache.Current, 500, LaunchPadId, false, Fmt);

            Assert.Equal(StockUiScreen.FacilityMenu, d.Screen);
            Assert.Equal(StockUiDecorationKind.FacilityUpgrade, d.Kind);
            Assert.True(d.Marked);
            Assert.True(d.Blocked);
            Assert.Equal(1000, d.UT);
            Assert.Equal("Upgraded to level 2 on D10 on your committed timeline. "
                + ReservationExplanation.TimelineRule + " The upgrade happens on that date.", d.Why);
            Assert.DoesNotContain("SpaceCenter", d.Why);
        }

        [Theory]
        [InlineData(1000.0)]
        [InlineData(5000.0)]
        public void ForFacilityMenu_AtAndAfterTheCommittedUpgrade_LeavesTheMenuStock(double now)
        {
            Ledger.AddAction(Upgrade(1000, LaunchPadId, 2));
            var d = StockUiDecorationQuery.ForFacilityMenu(CommittedFutureIndexCache.Current, now, LaunchPadId, false, Fmt);
            Assert.False(d.Marked);
            Assert.False(d.Blocked);
            Assert.Null(d.Why);
            Assert.Equal(StockUiDecorationKind.None, d.Kind);
        }

        [Fact]
        public void ForFacilityMenu_AnotherFacility_NoFacility_AndReplay_AreUnmarked()
        {
            Ledger.AddAction(Upgrade(1000, LaunchPadId, 2));
            var index = CommittedFutureIndexCache.Current;
            Assert.False(StockUiDecorationQuery.ForFacilityMenu(index, 500, VabId, false, Fmt).Blocked);
            Assert.False(StockUiDecorationQuery.ForFacilityMenu(index, 500, null, false, Fmt).Blocked);
            Assert.False(StockUiDecorationQuery.ForFacilityMenu(index, 500, "", false, Fmt).Marked);
            var replay = StockUiDecorationQuery.ForFacilityMenu(index, 500, LaunchPadId, true, Fmt);
            Assert.False(replay.Marked);
            Assert.False(replay.Blocked);
        }

        [Fact]
        public void ForFacilityMenu_TwoCommittedUpgrades_ListsBothUntilTheFirstPasses()
        {
            Ledger.AddAction(Upgrade(1000, LaunchPadId, 2));
            Ledger.AddAction(Upgrade(2000, LaunchPadId, 3));
            var index = CommittedFutureIndexCache.Current;
            var both = StockUiDecorationQuery.ForFacilityMenu(index, 500, LaunchPadId, false, Fmt);
            Assert.StartsWith("Upgraded to level 2 on D10 and to level 3 on D20 on your committed timeline.", both.Why);
            Assert.EndsWith("The upgrades happen on those dates.", both.Why);
            var later = StockUiDecorationQuery.ForFacilityMenu(index, 1500, LaunchPadId, false, Fmt);
            Assert.True(later.Blocked);
            Assert.Equal(2000, later.UT);
            Assert.StartsWith("Upgraded to level 3 on D20 on your committed timeline.", later.Why);
        }

        [Fact]
        public void Decide_BlockedDisablesUpgradeWithTheWhy_UnblockedLeavesItToStock()
        {
            var blocked = new StockUiDecoration { Marked = true, Blocked = true, Why = "the why" };
            var decision = StockUiFacilityDecoration.Decide(blocked);
            Assert.True(decision.DisableUpgrade);
            Assert.Equal("the why", decision.Reason);

            var stock = StockUiFacilityDecoration.Decide(new StockUiDecoration { Why = null });
            Assert.False(stock.DisableUpgrade);
            Assert.Null(stock.Reason);
        }

        [Fact]
        public void ComposeTooltipText_OwnedShowsTheWhyAlone_AStockControllerKeepsItsTextOnce()
        {
            Assert.Equal("why", StockUiFacilityDecoration.ComposeTooltipText(true, "ignored", "why"));
            string composed = StockUiFacilityDecoration.ComposeTooltipText(false, "Stock text", "why");
            Assert.Equal("Stock text\n<color=" + StockUiRnDDecoration.ReasonColorHex + ">why</color>", composed);
            Assert.Equal(composed, StockUiFacilityDecoration.ComposeTooltipText(false, composed, "why"));
        }

        [Fact]
        public void RemoveReason_UndoesAppendReason_AndLeavesOtherTextAlone()
        {
            string stock = "The Launch Pad is where rockets go up.";
            string appended = StockUiRnDDecoration.AppendReason(stock, "why");
            Assert.Equal(stock, StockUiFacilityDecoration.RemoveReason(appended, "why"));
            Assert.Equal(stock, StockUiFacilityDecoration.RemoveReason(stock, "why"));
            Assert.Equal("", StockUiFacilityDecoration.RemoveReason(StockUiRnDDecoration.AppendReason("", "why"), "why"));
            Assert.Null(StockUiFacilityDecoration.RemoveReason(null, "why"));
            Assert.Equal(appended, StockUiFacilityDecoration.RemoveReason(appended, null));
        }

        // ---------------- logging ----------------

        [Fact]
        public void LogFacilityMenu_WritesOneInfoLineAndTheWhyAsVerbose()
        {
            Ledger.AddAction(Upgrade(1000, LaunchPadId, 2));
            var d = StockUiDecorationQuery.ForFacilityMenu(CommittedFutureIndexCache.Current, 500, LaunchPadId, false, Fmt);
            StockUiDecorationQuery.LogFacilityMenu(d, false, "values modified");

            Assert.Equal("decorate screen=FacilityMenu facility=SpaceCenter/LaunchPad marked=true blocked=true",
                StockUiDecorationQuery.FormatFacilityMenuLine(d));
            Assert.Contains(logLines, l => l.Contains("[INFO][StockUiOverlay]")
                && l.Contains("decorate screen=FacilityMenu facility=SpaceCenter/LaunchPad marked=true blocked=true"));
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][StockUiOverlay]")
                && l.Contains("decorate screen=FacilityMenu facility=SpaceCenter/LaunchPad (values modified) why=\"Upgraded to level 2 on D10"));
        }

        [Fact]
        public void LogFacilityMenu_Unmarked_SaysWhyItIsLeftStock()
        {
            var index = CommittedFutureIndexCache.Current;
            StockUiDecorationQuery.LogFacilityMenu(
                StockUiDecorationQuery.ForFacilityMenu(index, 500, VabId, false, Fmt), false, "values modified");
            StockUiDecorationQuery.LogFacilityMenu(
                StockUiDecorationQuery.ForFacilityMenu(index, 500, VabId, true, Fmt), true, "timeline changed");
            StockUiDecorationQuery.LogFacilityMenu(
                StockUiDecorationQuery.ForFacilityMenu(index, 500, null, false, Fmt), false, "values modified");

            Assert.Contains(logLines, l => l.Contains("[INFO][StockUiOverlay]")
                && l.Contains("decorate screen=FacilityMenu facility=SpaceCenter/VehicleAssemblyBuilding marked=false blocked=false"));
            Assert.Contains(logLines, l => l.Contains("unmarked: no committed future upgrade of this facility"));
            Assert.Contains(logLines, l => l.Contains("(timeline changed) unmarked: action replay in progress"));
            Assert.Contains(logLines, l => l.Contains("decorate screen=FacilityMenu facility=<none> marked=false blocked=false"));
            Assert.Contains(logLines, l => l.Contains("unmarked: the building has no upgradeable facility"));
        }

        // ---------------- pairing: the greyed set == the refused set ----------------

        private static bool MenuGreysUpgrade(string facilityId, double now, bool replaying)
        {
            var d = StockUiDecorationQuery.ForFacilityMenu(CommittedFutureIndexCache.Current, now, facilityId, replaying,
                ReservationExplanation.DefaultDateFormatter);
            return StockUiFacilityDecoration.Decide(d).DisableUpgrade;
        }

        private static string MenuReason(string facilityId, double now)
        {
            var d = StockUiDecorationQuery.ForFacilityMenu(CommittedFutureIndexCache.Current, now, facilityId, false,
                ReservationExplanation.DefaultDateFormatter);
            return StockUiFacilityDecoration.Decide(d).Reason;
        }

        [Fact]
        public void Pairing_FacilityUpgrade_GreyedFacilitiesAreExactlyTheRefusedOnes_WithTheSameText()
        {
            Ledger.AddAction(Upgrade(500, LaunchPadId, 2));
            Ledger.AddAction(Upgrade(50, VabId, 2));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 100;
            var ids = new[] { LaunchPadId, VabId, TrackingId };

            var greyed = ids.Where(id => MenuGreysUpgrade(id, 100, false)).ToArray();
            var refused = ids.Where(FacilityUpgradePatch.TryBlockFacilityUpgradeById).ToArray();

            Assert.Equal(new[] { LaunchPadId }, greyed);
            Assert.Equal(new[] { LaunchPadId }, refused);
            var dialog = dialogs.Single();
            Assert.Equal(MenuReason(LaunchPadId, 100), dialog.reason);
            // The player-facing strings name the building, never the raw facility id.
            Assert.Equal("Cannot upgrade \"Launch Pad\"", dialog.title);
            Assert.DoesNotContain("SpaceCenter", dialog.title);
            Assert.DoesNotContain("SpaceCenter", dialog.reason);
            Assert.Contains(logLines, l => l.Contains("[FacilityUpgradePatch]")
                && l.Contains("Blocking facility upgrade: 'SpaceCenter/LaunchPad'"));
        }

        [Fact]
        public void Pairing_F1_TheGreyedUpgradeLiftsWithTheRefusal_OnceTheClockPassesTheCommittedUT()
        {
            Ledger.AddAction(Upgrade(1000, LaunchPadId, 2));
            foreach (double now in new[] { 0.0, 500.0, 999.9, 1000.0, 1000.1, 5000.0 })
            {
                CommittedFutureIndexCache.NowUtProviderForTesting = () => now;
                bool greyed = MenuGreysUpgrade(LaunchPadId, now, false);
                bool refused = FacilityUpgradePatch.TryBlockFacilityUpgradeById(LaunchPadId);
                Assert.Equal(now < 1000.0, greyed);
                Assert.Equal(greyed, refused);
            }
            // One refusal dialog per refused UT (0, 500, 999.9), each with the menu's text.
            Assert.Equal(3, dialogs.Count);
            Assert.All(dialogs, x => Assert.Equal(MenuReason(LaunchPadId, 500), x.reason));
        }

        [Fact]
        public void Pairing_DuringReplay_NeitherTheMenuNorTheRefusalBlocks()
        {
            Ledger.AddAction(Upgrade(1000, LaunchPadId, 2));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 500;
            GameStateRecorder.IsReplayingActions = true;

            Assert.False(MenuGreysUpgrade(LaunchPadId, 500, GameStateRecorder.IsReplayingActions));
            Assert.False(FacilityUpgradePatch.TryBlockFacilityUpgradeById(LaunchPadId));
            Assert.Empty(dialogs);
            Assert.Contains(logLines, l => l.Contains("[FacilityUpgradePatch]") && l.Contains("action replay in progress"));
        }

        [Fact]
        public void BlockedDialogTitle_NamesEveryFacilityShape_WithoutTheRawId()
        {
            Assert.Equal("Cannot upgrade \"Launch Pad\"", FacilityUpgradePatch.BlockedDialogTitle(LaunchPadId));
            Assert.Equal("Cannot upgrade \"Vehicle Assembly Building\"", FacilityUpgradePatch.BlockedDialogTitle(VabId));
            FacilityDisplayNames.FacilityNameLookupForTesting = id => id == "LaunchPad" ? "Launchpad" : null;
            Assert.Equal("Cannot upgrade \"Launchpad\"", FacilityUpgradePatch.BlockedDialogTitle(LaunchPadId));
        }
    }
}
