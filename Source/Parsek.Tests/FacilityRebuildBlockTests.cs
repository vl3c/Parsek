using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using KSP.UI.Screens;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling 2026-10-07 (FACILITY-DOWNGRADE-DEBIT-NOT-LEDGERED, its open limit): a
    /// committed FUTURE level change of a facility (an upgrade, or a stock "Rebuild lvl N"
    /// booked as a <c>FacilityUpgrade</c> row marked <c>FacilityDowngrade</c>) blocks every
    /// earlier level change of that facility: the Upgrade button and the Rebuild button. A
    /// committed future repair that covers the facility's current destruction blocks the
    /// Rebuild and the Upgrade too, because both repair for free (<c>ResetStructures</c>) and
    /// would rewrite that repair (FACILITY-UPGRADE-FREE-REPAIR-REWRITES-COMMITTED-REPAIR for
    /// the Upgrade). The predicate, the explanation, the menu decoration, the
    /// pairing, the replay bypass, the slot consumers and the Harmony prefix / finalizer
    /// ordering of <c>SpaceCenterBuilding.DowngradeFacility(bool)</c>.
    /// </summary>
    [Collection("Sequential")]
    public class FacilityRebuildBlockTests : IDisposable
    {
        private const string LaunchPadId = "SpaceCenter/LaunchPad";
        private const string PadBuilding = "SpaceCenter/LaunchPad/Facility/LaunchPadMedium/ksp_pad_launchPad";
        private const string VabId = "SpaceCenter/VehicleAssemblyBuilding";
        private const string AdminId = "SpaceCenter/Administration";
        private const string AdminBuilding = "SpaceCenter/Administration/Facility/Building";
        private const string VabBuilding = "SpaceCenter/VehicleAssemblyBuilding/Facility/Building";

        private readonly List<string> logLines = new List<string>();
        private readonly List<(string title, string reason)> dialogs = new List<(string, string)>();

        private static readonly Func<double, string> Fmt =
            ut => "D" + ((long)(ut / 100.0)).ToString(CultureInfo.InvariantCulture);

        public FacilityRebuildBlockTests()
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
            FacilityUpgradeCapture.ResetForTesting();
            FacilityDisplayNames.FacilityNameLookupForTesting = id => null;
            CommittedActionDialog.TestHookForTesting = (title, reason, detail) => dialogs.Add((title, reason));
        }

        public void Dispose()
        {
            CommittedActionDialog.TestHookForTesting = null;
            FacilityDisplayNames.FacilityNameLookupForTesting = null;
            GameStateRecorder.IsReplayingActions = false;
            FacilityUpgradeCapture.ResetForTesting();
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
                UT = ut, Type = GameActionType.FacilityUpgrade, FacilityId = facilityId,
                ToLevel = toLevel, FacilityCost = 75000f
            };

        private static GameAction Rebuild(double ut, string facilityId, int toLevel) =>
            new GameAction
            {
                UT = ut, Type = GameActionType.FacilityUpgrade, FacilityId = facilityId,
                ToLevel = toLevel, FacilityCost = 50000f, FacilityDowngrade = true
            };

        private static GameAction Destroy(double ut, string buildingId) =>
            new GameAction { UT = ut, Type = GameActionType.FacilityDestruction, FacilityId = buildingId };

        private static GameAction Repair(double ut, string buildingId) =>
            new GameAction { UT = ut, Type = GameActionType.FacilityRepair, FacilityId = buildingId, FacilityCost = 4000f };

        /// <summary>The out-of-service pad stock offers a Rebuild on: its main building down.</summary>
        private static List<FacilityRepairCapture.BuildingRepairInput> DownPad() =>
            new List<FacilityRepairCapture.BuildingRepairInput>
            {
                new FacilityRepairCapture.BuildingRepairInput { BuildingId = PadBuilding, IsDestroyed = true, RepairCost = 4000f }
            };

        /// <summary>The same pad with its building standing.</summary>
        private static List<FacilityRepairCapture.BuildingRepairInput> IntactPad() =>
            new List<FacilityRepairCapture.BuildingRepairInput>
            {
                new FacilityRepairCapture.BuildingRepairInput { BuildingId = PadBuilding, IsDestroyed = false, RepairCost = 4000f }
            };

        private static List<FacilityRepairCapture.BuildingRepairInput> Down(string buildingId) =>
            new List<FacilityRepairCapture.BuildingRepairInput>
            {
                new FacilityRepairCapture.BuildingRepairInput { BuildingId = buildingId, IsDestroyed = true, RepairCost = 4000f }
            };

        private static bool RebuildBlocked(double now, string facilityId = LaunchPadId) =>
            StockUiReservationPredicates.IsFacilityRebuildBlocked(
                CommittedFutureIndexCache.Current, facilityId, DownPad(), now);

        private static string RebuildWhy(double now) =>
            StockUiReservationPredicates.ExplainFacilityRebuild(
                CommittedFutureIndexCache.Current, LaunchPadId, DownPad(), now, Fmt).Body;

        // ---------------- index: a committed Rebuild is a level change ----------------

        // catches: #2042's "a committed downgrade reserves nothing" surviving the ruling.
        [Fact]
        public void Index_ClassifiesARebuildRowAsALevelChange_ThatKnowsItsDirection_AndCountsIt()
        {
            CommittedFutureKind kind;
            string key;
            Assert.True(CommittedFutureIndex.TryClassify(Rebuild(900, AdminId, 1), out kind, out key));
            Assert.Equal(CommittedFutureKind.FacilityUpgrade, kind);
            Assert.Equal(AdminId, key);

            Ledger.AddAction(Upgrade(500, AdminId, 2));
            Ledger.AddAction(Rebuild(900, AdminId, 1));
            var index = CommittedFutureIndexCache.Current;
            var rows = index.FutureEntries(CommittedFutureKind.FacilityUpgrade, AdminId, 100);
            Assert.Equal(new[] { 500.0, 900.0 }, rows.Select(r => r.UT));
            Assert.False(rows[0].FacilityDowngrade);
            Assert.True(rows[1].FacilityDowngrade);
            Assert.Equal(1, rows[1].FacilityToLevel);
            Assert.Equal(50000f, rows[1].Amount);
            Assert.Contains("facility=2 ", index.DescribeCounts());
            Assert.Contains(" rebuild=1 ", index.DescribeCounts());
        }

        // ---------------- predicate ----------------

        [Fact]
        public void AFutureUpgrade_BlocksTheRebuild_NamingTheUpgrade()
        {
            Ledger.AddAction(Upgrade(1000, LaunchPadId, 3));
            Assert.True(RebuildBlocked(500));
            Assert.Equal("Upgraded to level 3 on D10, blocked by timeline until then.", RebuildWhy(500));
            Assert.Equal("Upgraded on D10", StockUiReservationPredicates.ExplainFacilityRebuild(
                CommittedFutureIndexCache.Current, LaunchPadId, DownPad(), 500, Fmt).Title);
        }

        [Fact]
        public void AFutureRebuild_BlocksTheUpgradeAndTheRebuild_NamingTheRebuild()
        {
            Ledger.AddAction(Rebuild(1000, LaunchPadId, 2));
            var index = CommittedFutureIndexCache.Current;

            Assert.True(StockUiReservationPredicates.IsFacilityUpgradeBlocked(index, LaunchPadId, 500));
            Assert.True(RebuildBlocked(500));
            const string why = "Rebuilt to level 2 on D10, blocked by timeline until then.";
            Assert.Equal(why, StockUiReservationPredicates.ExplainFacilityUpgrade(index, LaunchPadId, 500, Fmt).Body);
            Assert.Equal(why, RebuildWhy(500));
            Assert.Equal("Rebuilt on D10",
                StockUiReservationPredicates.ExplainFacilityUpgrade(index, LaunchPadId, 500, Fmt).Title);

            // The Upgrade button reads the same: greyed, with the rebuild's sentence.
            var menu = StockUiDecorationQuery.ForFacilityMenu(index, 500, LaunchPadId, null, false, Fmt);
            Assert.True(menu.Blocked);
            Assert.Equal(why, menu.Why);
            Assert.Equal(1000, menu.UT);
        }

        [Theory]
        [InlineData(1000.0)]
        [InlineData(5000.0)]
        public void APastRow_BlocksNothing(double now)
        {
            Ledger.AddAction(Upgrade(400, LaunchPadId, 2));
            Ledger.AddAction(Rebuild(1000, LaunchPadId, 1));
            var index = CommittedFutureIndexCache.Current;

            Assert.False(RebuildBlocked(now));
            Assert.False(StockUiReservationPredicates.IsFacilityUpgradeBlocked(index, LaunchPadId, now));
            Assert.False(StockUiDecorationQuery.ForFacilityMenuRebuild(
                index, now, LaunchPadId, DownPad(), false, Fmt).Blocked);
        }

        [Fact]
        public void AnotherFacilitysRow_BlocksNothing()
        {
            Ledger.AddAction(Upgrade(1000, VabId, 2));
            Ledger.AddAction(Rebuild(1000, AdminId, 1));
            var index = CommittedFutureIndexCache.Current;

            Assert.False(RebuildBlocked(500));
            Assert.False(StockUiReservationPredicates.IsFacilityUpgradeBlocked(index, LaunchPadId, 500));
            Assert.False(StockUiReservationPredicates.IsFacilityRebuildBlocked(index, LaunchPadId, null, 500));
            Assert.False(StockUiReservationPredicates.IsFacilityRebuildBlocked(null, LaunchPadId, DownPad(), 500));
        }

        [Fact]
        public void MixedRows_AreListedEarliestFirst_EachWithItsOwnParticiple()
        {
            Ledger.AddAction(Upgrade(1000, LaunchPadId, 3));
            Ledger.AddAction(Rebuild(2000, LaunchPadId, 2));
            Ledger.AddAction(Upgrade(3000, LaunchPadId, 3));

            Assert.Equal("Upgraded to level 3 on D10, rebuilt to level 2 on D20 and upgraded to level 3 on D30, "
                + "blocked by timeline until then.", RebuildWhy(500));
            // Once the clock passes the first row, the rebuild is named first.
            Assert.Equal("Rebuilt to level 2 on D20 and upgraded to level 3 on D30, blocked by timeline until then.",
                RebuildWhy(1500));
            // The upgrade-only wording is unchanged.
            Assert.Equal("Upgraded to level 3 on D30, blocked by timeline until then.", RebuildWhy(2500));
        }

        // ---------------- point 3: the Rebuild's free repair ----------------

        // catches: a Rebuild of a facility whose current destruction a committed row repairs
        // later - the Rebuild repairs it now for free and rewrites the recorded repair.
        [Fact]
        public void ACommittedRepairCoveringTheDestruction_BlocksTheRebuild_WithTheRepairsWords()
        {
            Ledger.AddAction(Destroy(100, PadBuilding));
            Ledger.AddAction(Repair(1000, PadBuilding));
            var index = CommittedFutureIndexCache.Current;

            Assert.True(RebuildBlocked(500));
            Assert.Equal("Repaired on D10, blocked by timeline until then.", RebuildWhy(500));
            // The same text the Repair button shows.
            Assert.Equal(StockUiReservationPredicates.ExplainFacilityRepair(index, DownPad(), 500, Fmt).Body,
                RebuildWhy(500));
            // No level change ahead, but the Upgrade repairs for free as well, so the covering
            // repair holds it too; only the SetLevel backstop's level-change read stays clear.
            Assert.True(StockUiReservationPredicates.IsFacilityUpgradeBlocked(index, LaunchPadId, DownPad(), 500));
            Assert.False(StockUiReservationPredicates.IsFacilityUpgradeBlocked(index, LaunchPadId, 500));
            // A committed destruction first means the live destruction is a new one: no block.
            Assert.False(RebuildBlocked(50));
            // Past the committed repair: no block.
            Assert.False(RebuildBlocked(1000));
        }

        [Fact]
        public void ARepairAndALevelChangeAhead_AreBothNamed()
        {
            Ledger.AddAction(Destroy(100, PadBuilding));
            Ledger.AddAction(Repair(1000, PadBuilding));
            Ledger.AddAction(Upgrade(2000, LaunchPadId, 2));

            Assert.Equal("Repaired on D10 and upgraded to level 2 on D20, blocked by timeline until then.",
                RebuildWhy(500));
        }

        // ---------------- the Upgrade's free repair (FACILITY-UPGRADE-FREE-REPAIR-REWRITES-COMMITTED-REPAIR) ----------------

        // catches: an Upgrade of an out-of-service facility (stock leaves Upgrade clickable
        // there) whose ResetStructures repairs the building now, for free, ahead of the
        // committed repair that covers that destruction - the recorded repair is rewritten.
        [Fact]
        public void ACommittedRepairCoveringTheDestruction_RefusesAndGreysTheUpgrade_WithTheRepairSentence()
        {
            Ledger.AddAction(Destroy(100, PadBuilding));
            Ledger.AddAction(Repair(1000, PadBuilding));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 500;
            var index = CommittedFutureIndexCache.Current;
            const string why = "Repaired on D10, blocked by timeline until then.";

            Assert.True(StockUiReservationPredicates.IsFacilityUpgradeBlocked(index, LaunchPadId, DownPad(), 500));
            var text = StockUiReservationPredicates.ExplainFacilityUpgrade(index, LaunchPadId, DownPad(), 500, Fmt);
            Assert.Equal(why, text.Body);
            Assert.Equal("Repaired on D10", text.Title);
            // The Repair button's own sentence, word for word.
            Assert.Equal(StockUiReservationPredicates.ExplainFacilityRepair(index, DownPad(), 500, Fmt).Body, why);

            // Greyed: the Upgrade control, marked and blocked with that sentence.
            var menu = StockUiDecorationQuery.ForFacilityMenu(index, 500, LaunchPadId, DownPad(), false, Fmt);
            Assert.Equal(StockUiDecorationQuery.FacilityMenuTab, menu.Tab);
            Assert.Equal(StockUiDecorationKind.FacilityUpgrade, menu.Kind);
            Assert.True(menu.Marked && menu.Blocked);
            Assert.Equal(why, menu.Why);
            Assert.Equal(1000, menu.UT);
            Assert.True(StockUiFacilityDecoration.Decide(menu).DisableUpgrade);

            // Refused, before stock's debit, with the same text.
            Assert.True(FacilityUpgradePatch.TryBlockFacilityUpgradeFor(LaunchPadId, DownPad()));
            var dialog = dialogs.Single();
            Assert.Equal("Cannot upgrade \"Launch Pad\"", dialog.title);
            Assert.StartsWith("Repaired on ", dialog.reason);
            Assert.Equal(StockUiDecorationQuery.ForFacilityMenu(index, 500, LaunchPadId, DownPad(), false,
                ReservationExplanation.DefaultDateFormatter).Why, dialog.reason);
            Assert.Equal(StockUiReservationPredicates.ExplainFacilityRepair(index, DownPad(), 500,
                ReservationExplanation.DefaultDateFormatter).Body, dialog.reason);
            Assert.Contains(logLines, l => l.Contains("[INFO][FacilityUpgradePatch]")
                && l.Contains("Blocking facility upgrade: 'SpaceCenter/LaunchPad' - no committed future level change")
                && l.Contains("first building='" + PadBuilding + "' ut=1000")
                && l.Contains("nowUT=500") && l.Contains("repairs=1"));

            // The SetLevel backstop: stock's SetLevel runs no structure reset, so it reads level
            // changes only, and inside an allowed UpgradeFacility the reset already ran.
            Assert.False(FacilityUpgradePatch.TryBlockFacilityUpgradeById(LaunchPadId));
        }

        [Fact]
        public void TheUpgradeSentence_ForALevelChangeAlone_IsUnchanged_AndARepairAheadOfItIsNamedFirst()
        {
            Ledger.AddAction(Destroy(100, PadBuilding));
            Ledger.AddAction(Repair(1000, PadBuilding));
            Ledger.AddAction(Upgrade(2000, LaunchPadId, 2));
            var index = CommittedFutureIndexCache.Current;

            // Building standing: the plain level-change sentence, byte for byte (KB-3 matches it).
            Assert.Equal("Upgraded to level 2 on D20, blocked by timeline until then.",
                StockUiReservationPredicates.ExplainFacilityUpgrade(index, LaunchPadId, IntactPad(), 500, Fmt).Body);
            Assert.Equal(StockUiReservationPredicates.ExplainFacilityUpgrade(index, LaunchPadId, 500, Fmt).Body,
                StockUiReservationPredicates.ExplainFacilityUpgrade(index, LaunchPadId, IntactPad(), 500, Fmt).Body);
            // Building down: the covering repair is named first, as on the Rebuild.
            const string mixed = "Repaired on D10 and upgraded to level 2 on D20, blocked by timeline until then.";
            Assert.Equal(mixed,
                StockUiReservationPredicates.ExplainFacilityUpgrade(index, LaunchPadId, DownPad(), 500, Fmt).Body);
            Assert.Equal(mixed, StockUiDecorationQuery.ForFacilityMenu(index, 500, LaunchPadId, DownPad(), false, Fmt).Why);
            Assert.Equal(1000, StockUiDecorationQuery.ForFacilityMenu(index, 500, LaunchPadId, DownPad(), false, Fmt).UT);

            CommittedFutureIndexCache.NowUtProviderForTesting = () => 500;
            Assert.True(FacilityUpgradePatch.TryBlockFacilityUpgradeFor(LaunchPadId, DownPad()));
            Assert.Equal(StockUiDecorationQuery.ForFacilityMenu(index, 500, LaunchPadId, DownPad(), false,
                ReservationExplanation.DefaultDateFormatter).Why, dialogs.Single().reason);
            Assert.StartsWith("Repaired on ", dialogs.Single().reason);
            Assert.Contains(" and upgraded to level 2 on ", dialogs.Single().reason);
            Assert.Contains(logLines, l => l.Contains("[INFO][FacilityUpgradePatch]")
                && l.Contains("Blocking facility upgrade: 'SpaceCenter/LaunchPad' - 1 committed future upgrade(s), "
                    + "last ut=2000 toLevel=2 nowUT=500 rebuilds=0 repairs=1"));
        }

        // catches: an over-block - an Upgrade held when no committed repair covers the
        // destruction it would repair now.
        [Theory]
        [InlineData("no-repair-row", 500.0)]
        [InlineData("destruction-first", 500.0)]
        [InlineData("clock-past-the-repair", 1000.0)]
        [InlineData("building-standing", 500.0)]
        public void NoCoveringRepair_TheUpgradeStillGoesThrough(string shape, double now)
        {
            var buildings = DownPad();
            switch (shape)
            {
                case "no-repair-row":
                    Ledger.AddAction(Destroy(100, PadBuilding));
                    break;
                case "destruction-first":
                    // A committed destruction after now and before the repair: the destruction
                    // the player sees is a new one, not the one the committed repair repairs.
                    Ledger.AddAction(Destroy(600, PadBuilding));
                    Ledger.AddAction(Repair(1000, PadBuilding));
                    break;
                case "clock-past-the-repair":
                    Ledger.AddAction(Destroy(100, PadBuilding));
                    Ledger.AddAction(Repair(1000, PadBuilding));
                    break;
                case "building-standing":
                    Ledger.AddAction(Destroy(100, PadBuilding));
                    Ledger.AddAction(Repair(1000, PadBuilding));
                    buildings = IntactPad();
                    break;
            }
            CommittedFutureIndexCache.NowUtProviderForTesting = () => now;
            var index = CommittedFutureIndexCache.Current;

            Assert.False(StockUiReservationPredicates.IsFacilityUpgradeBlocked(index, LaunchPadId, buildings, now));
            Assert.False(StockUiDecorationQuery.ForFacilityMenu(index, now, LaunchPadId, buildings, false, Fmt).Blocked);
            Assert.False(FacilityUpgradePatch.TryBlockFacilityUpgradeFor(LaunchPadId, buildings));
            Assert.Empty(dialogs);
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][FacilityUpgradePatch]")
                && l.Contains("Allowing facility upgrade: 'SpaceCenter/LaunchPad' - no committed future level change, "
                    + "and no committed future repair covers the destruction"));

            // The prefix core lets stock run and opens the upgrade scope.
            Assert.True(FacilityUpgradeSpendPatch.BlockOrOpenScope(LaunchPadId, buildings, LaunchPadId, true, 350000.0));
            Assert.True(FacilityUpgradeCapture.IsScopeActive);
        }

        [Fact]
        public void PrefixCore_ARepairHeldUpgrade_SkipsStock_AndOpensNoScope()
        {
            Ledger.AddAction(Destroy(100, PadBuilding));
            Ledger.AddAction(Repair(1000, PadBuilding));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 500;

            bool runOriginal = FacilityUpgradeSpendPatch.BlockOrOpenScope(
                LaunchPadId, DownPad(), LaunchPadId, true, 350000.0);

            Assert.False(runOriginal);
            Assert.False(FacilityUpgradeCapture.IsScopeActive);
            Assert.Single(dialogs);
            Assert.Contains(logLines, l => l.Contains("FacilityUpgrade scope not opened: facility='SpaceCenter/LaunchPad'"));
        }

        [Fact]
        public void Replay_LeavesARepairHeldUpgradeToStock_InTheMenuAndTheRefusal()
        {
            Ledger.AddAction(Destroy(100, PadBuilding));
            Ledger.AddAction(Repair(1000, PadBuilding));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 500;
            GameStateRecorder.IsReplayingActions = true;

            var menu = StockUiDecorationQuery.ForFacilityMenu(
                CommittedFutureIndexCache.Current, 500, LaunchPadId, DownPad(), true, Fmt);
            Assert.False(menu.Marked);
            Assert.False(menu.Blocked);
            Assert.False(FacilityUpgradePatch.TryBlockFacilityUpgradeFor(LaunchPadId, DownPad()));
            Assert.True(FacilityUpgradeSpendPatch.BlockOrOpenScope(LaunchPadId, DownPad(), LaunchPadId, true, 350000.0));
            Assert.Empty(dialogs);
            Assert.Contains(logLines, l => l.Contains("[FacilityUpgradePatch]")
                && l.Contains("Bypassing block for 'SpaceCenter/LaunchPad' - action replay in progress"));
        }

        // The greyed Upgrade is exactly the refused Upgrade, over level changes and covering
        // repairs, as the clock moves through every row.
        [Fact]
        public void Pairing_TheGreyedUpgradeIsExactlyTheRefusedUpgrade_OverRepairsAndLevelChanges()
        {
            Ledger.AddAction(Destroy(100, PadBuilding));
            Ledger.AddAction(Repair(1000, PadBuilding));
            Ledger.AddAction(Upgrade(2000, LaunchPadId, 2));
            Ledger.AddAction(Destroy(100, AdminBuilding));
            Ledger.AddAction(Repair(1500, AdminBuilding));
            var cases = new[]
            {
                (id: LaunchPadId, buildings: DownPad()),
                (id: AdminId, buildings: Down(AdminBuilding)),
                (id: VabId, buildings: Down(VabBuilding)),
            };

            foreach (double now in new[] { 0.0, 100.0, 500.0, 999.9, 1000.0, 1499.9, 1500.0, 1999.9, 2000.0, 5000.0 })
            {
                CommittedFutureIndexCache.NowUtProviderForTesting = () => now;
                foreach (var c in cases)
                {
                    dialogs.Clear();
                    var menu = StockUiDecorationQuery.ForFacilityMenu(CommittedFutureIndexCache.Current, now, c.id,
                        c.buildings, false, ReservationExplanation.DefaultDateFormatter);
                    bool greyed = StockUiFacilityDecoration.Decide(menu).DisableUpgrade;
                    bool refused = FacilityUpgradePatch.TryBlockFacilityUpgradeFor(c.id, c.buildings);

                    bool expected = c.id == LaunchPadId ? now < 2000.0
                        : c.id == AdminId ? now >= 100.0 && now < 1500.0
                        : false;
                    Assert.True(expected == greyed, c.id + " greyed at " + now.ToString(CultureInfo.InvariantCulture));
                    Assert.True(greyed == refused, c.id + " refused at " + now.ToString(CultureInfo.InvariantCulture));
                    if (refused) Assert.Equal(menu.Why, dialogs.Single().reason);
                    else Assert.Empty(dialogs);
                }
            }
        }

        // ---------------- menu decoration ----------------

        [Fact]
        public void ForFacilityMenuRebuild_MarksAndBlocks_TabRebuild_KindFacilityRebuild_AndReplayLeavesItStock()
        {
            Ledger.AddAction(Rebuild(1000, LaunchPadId, 2));
            var index = CommittedFutureIndexCache.Current;

            var d = StockUiDecorationQuery.ForFacilityMenuRebuild(index, 500, LaunchPadId, DownPad(), false, Fmt);
            Assert.Equal(StockUiScreen.FacilityMenu, d.Screen);
            Assert.Equal(StockUiDecorationQuery.FacilityMenuRebuildTab, d.Tab);
            Assert.Equal("Rebuild", d.Tab);
            Assert.Equal(StockUiDecorationKind.FacilityRebuild, d.Kind);
            Assert.Equal(LaunchPadId, d.Id);
            Assert.True(d.Marked);
            Assert.True(d.Blocked);
            Assert.Equal(1000, d.UT);
            Assert.Equal("Rebuilt to level 2 on D10, blocked by timeline until then.", d.Why);

            var replay = StockUiDecorationQuery.ForFacilityMenuRebuild(index, 500, LaunchPadId, DownPad(), true, Fmt);
            Assert.False(replay.Marked);
            Assert.False(replay.Blocked);
            Assert.Equal(StockUiDecorationKind.None, replay.Kind);
        }

        [Fact]
        public void DecideRebuild_GreysOnlyAShownButton()
        {
            var blocked = new StockUiDecoration { Marked = true, Blocked = true, Why = "the why" };
            Assert.True(StockUiFacilityDecoration.DecideRebuild(blocked, true).DisableUpgrade);
            Assert.Equal("the why", StockUiFacilityDecoration.DecideRebuild(blocked, true).Reason);
            // A hidden Rebuild (no Left Ctrl, an operational facility) carries no reason.
            Assert.False(StockUiFacilityDecoration.DecideRebuild(blocked, false).DisableUpgrade);
            Assert.False(StockUiFacilityDecoration.DecideRebuild(new StockUiDecoration(), true).DisableUpgrade);
        }

        [Fact]
        public void LogFacilityMenuRebuild_MarkedIsThePassItemLine_UnmarkedSaysWhy()
        {
            Ledger.AddAction(Rebuild(1000, LaunchPadId, 2));
            var index = CommittedFutureIndexCache.Current;

            StockUiDecorationQuery.LogFacilityMenuRebuild(
                StockUiDecorationQuery.ForFacilityMenuRebuild(index, 500, LaunchPadId, DownPad(), false, Fmt),
                false, "values modified");
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][StockUiOverlay]")
                && l.Contains("decorate screen=FacilityMenu tab=Rebuild item=SpaceCenter/LaunchPad kind=FacilityRebuild "
                    + "marked=true blocked=true why=\"Rebuilt to level 2 on D10, blocked by timeline until then."));

            StockUiDecorationQuery.LogFacilityMenuRebuild(
                StockUiDecorationQuery.ForFacilityMenuRebuild(index, 500, VabId, null, false, Fmt), false, "values modified");
            StockUiDecorationQuery.LogFacilityMenuRebuild(
                StockUiDecorationQuery.ForFacilityMenuRebuild(index, 500, LaunchPadId, DownPad(), true, Fmt),
                true, "timeline changed");
            Assert.Contains(logLines, l => l.Contains(
                "FacilityMenu SpaceCenter/VehicleAssemblyBuilding Rebuild left to stock (values modified): "
                + "no committed future level change, and no committed future repair covers the destruction"));
            Assert.Contains(logLines, l => l.Contains("Rebuild left to stock (timeline changed): action replay in progress"));
            Assert.DoesNotContain(logLines, l => l.Contains("Rebuild left to stock") && l.Contains("decorate "));
        }

        [Fact]
        public void RemoveReasonUnlessHeld_KeepsALineAnotherButtonStillShows()
        {
            string text = StockUiRnDDecoration.AppendReason("The pad.", "why");
            Assert.Equal(text, StockUiFacilityDecoration.RemoveReasonUnlessHeld(text, "why", new[] { "why" }));
            Assert.Equal("The pad.", StockUiFacilityDecoration.RemoveReasonUnlessHeld(text, "why", new[] { "other" }));
            Assert.Equal("The pad.", StockUiFacilityDecoration.RemoveReasonUnlessHeld(text, "why", null));
        }

        // ---------------- pairing: the greyed Rebuild is exactly the refused Rebuild ----------------

        [Fact]
        public void Pairing_TheGreyedRebuildIsExactlyTheRefusedRebuild_WithTheSameText()
        {
            Ledger.AddAction(Upgrade(1000, LaunchPadId, 3));
            Ledger.AddAction(Rebuild(50, VabId, 1));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 500;
            var ids = new[] { LaunchPadId, VabId, AdminId };

            var greyed = ids.Where(id => StockUiFacilityDecoration.DecideRebuild(StockUiDecorationQuery.ForFacilityMenuRebuild(
                CommittedFutureIndexCache.Current, 500, id, DownPad(), false,
                ReservationExplanation.DefaultDateFormatter), true).DisableUpgrade).ToArray();
            var refused = ids.Where(id => FacilityRebuildBlock.TryBlockFacilityRebuildFor(id, DownPad())).ToArray();

            // DownPad lists a pad building; no repair row covers it, so only the level change counts.
            Assert.Equal(new[] { LaunchPadId }, greyed);
            Assert.Equal(new[] { LaunchPadId }, refused);
            var dialog = dialogs.Single();
            Assert.Equal("Cannot rebuild \"Launch Pad\"", dialog.title);
            Assert.Equal(StockUiDecorationQuery.ForFacilityMenuRebuild(CommittedFutureIndexCache.Current, 500, LaunchPadId,
                DownPad(), false, ReservationExplanation.DefaultDateFormatter).Why, dialog.reason);
            Assert.DoesNotContain("SpaceCenter", dialog.reason);
            Assert.Contains(logLines, l => l.Contains("[INFO][FacilityRebuildPatch]")
                && l.Contains("Blocking facility rebuild: 'SpaceCenter/LaunchPad'")
                && l.Contains("earliest=upgrade") && l.Contains("ut=1000") && l.Contains("nowUT=500"));
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][FacilityRebuildPatch]")
                && l.Contains("Allowing facility rebuild: 'SpaceCenter/VehicleAssemblyBuilding'"));
        }

        [Fact]
        public void Pairing_TheRefusalLiftsWithTheGreyedButton_OnceTheClockPassesTheLastRow()
        {
            Ledger.AddAction(Rebuild(1000, LaunchPadId, 2));
            Ledger.AddAction(Upgrade(2000, LaunchPadId, 3));
            foreach (double now in new[] { 0.0, 999.9, 1000.0, 1999.9, 2000.0, 5000.0 })
            {
                CommittedFutureIndexCache.NowUtProviderForTesting = () => now;
                bool greyed = StockUiFacilityDecoration.DecideRebuild(StockUiDecorationQuery.ForFacilityMenuRebuild(
                    CommittedFutureIndexCache.Current, now, LaunchPadId, DownPad(), false, Fmt), true).DisableUpgrade;
                bool refused = FacilityRebuildBlock.TryBlockFacilityRebuildFor(LaunchPadId, DownPad());
                bool upgradeRefused = FacilityUpgradePatch.TryBlockFacilityUpgradeById(LaunchPadId);
                Assert.Equal(now < 2000.0, greyed);
                Assert.Equal(greyed, refused);
                Assert.Equal(greyed, upgradeRefused);
            }
        }

        [Fact]
        public void Pairing_DuringReplay_NeitherTheMenuNorTheRefusalBlocks()
        {
            Ledger.AddAction(Rebuild(1000, LaunchPadId, 2));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 500;
            GameStateRecorder.IsReplayingActions = true;

            Assert.False(StockUiDecorationQuery.ForFacilityMenuRebuild(
                CommittedFutureIndexCache.Current, 500, LaunchPadId, DownPad(), true, Fmt).Blocked);
            Assert.False(FacilityRebuildBlock.TryBlockFacilityRebuildFor(LaunchPadId, DownPad()));
            Assert.Empty(dialogs);
            Assert.Contains(logLines, l => l.Contains("[FacilityRebuildPatch]") && l.Contains("action replay in progress"));
        }

        [Fact]
        public void BlockedDialogTitle_NamesTheFacility()
        {
            Assert.Equal("Cannot rebuild \"Launch Pad\"", FacilityRebuildBlock.BlockedDialogTitle(LaunchPadId));
            Assert.Equal("Cannot rebuild \"Administration\"", FacilityRebuildBlock.BlockedDialogTitle(AdminId));
        }

        // ---------------- Harmony ordering: refusal first, no scope left open ----------------

        // catches: a refusing prefix that opened the downgrade scope first (a stray scope that
        // the next level change would read), or a finalizer that closes a scope it never opened.
        [Fact]
        public void PrefixCore_ARefusedRebuild_SkipsStock_AndOpensNoScope()
        {
            Ledger.AddAction(Upgrade(1000, LaunchPadId, 3));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 500;

            bool scopeOpened;
            bool runOriginal = FacilityDowngradeSpendPatch.BlockOrOpenScope(
                LaunchPadId, DownPad(), true, 350000.0, out scopeOpened);

            Assert.False(runOriginal);
            Assert.False(scopeOpened);
            Assert.False(FacilityUpgradeCapture.IsScopeActive);
            Assert.Single(dialogs);
            Assert.DoesNotContain(logLines, l => l.Contains("FacilityDowngrade scope open"));
        }

        [Fact]
        public void PrefixCore_AnAllowedRebuild_OpensTheDowngradeScope_AndTheFinalizerClosesIt()
        {
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 500;

            bool scopeOpened;
            bool runOriginal = FacilityDowngradeSpendPatch.BlockOrOpenScope(
                LaunchPadId, DownPad(), true, 350000.0, out scopeOpened);

            Assert.True(runOriginal);
            Assert.True(scopeOpened);
            Assert.True(FacilityUpgradeCapture.IsScopeActive);
            Assert.True(FacilityUpgradeCapture.IsDowngradeScopeOpenFor(LaunchPadId));
            Assert.Empty(dialogs);

            FacilityDowngradeSpendPatch.CloseScopeIfOpened(scopeOpened, null);
            Assert.False(FacilityUpgradeCapture.IsScopeActive);
            Assert.Contains(logLines, l => l.Contains("FacilityDowngrade scope close") && l.Contains("reason=downgrade-returned"));
        }

        [Fact]
        public void Finalizer_AfterARefusal_LeavesAScopeItDidNotOpenAlone()
        {
            // A scope some other call holds open (not this refused Rebuild's).
            FacilityUpgradeCapture.BeginScope(VabId, false, double.NaN);
            Assert.True(FacilityUpgradeCapture.IsScopeActive);

            FacilityDowngradeSpendPatch.CloseScopeIfOpened(false, null);
            Assert.True(FacilityUpgradeCapture.IsScopeActive);
            FacilityDowngradeSpendPatch.CloseScopeIfOpened(false, new InvalidOperationException("stock threw"));
            Assert.True(FacilityUpgradeCapture.IsScopeActive);

            FacilityDowngradeSpendPatch.CloseScopeIfOpened(true, new InvalidOperationException("stock threw"));
            Assert.False(FacilityUpgradeCapture.IsScopeActive);
            Assert.Contains(logLines, l => l.Contains("scope close") && l.Contains("reason=downgrade-threw"));
        }

        [Fact]
        public void Target_ThePrefixCanSkipTheOriginal_AndPassesItsStateToTheFinalizer()
        {
            var prefix = typeof(FacilityDowngradeSpendPatch).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(prefix);
            Assert.Equal(typeof(bool), prefix.ReturnType);
            var state = prefix.GetParameters().Single(p => p.Name == "__state");
            Assert.True(state.IsOut);
            Assert.Equal(typeof(bool), state.ParameterType.GetElementType());

            var finalizer = typeof(FacilityDowngradeSpendPatch).GetMethod("Finalizer", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(finalizer);
            Assert.Equal(typeof(Exception), finalizer.ReturnType);
            Assert.Equal(new[] { "__exception", "__state" }, finalizer.GetParameters().Select(p => p.Name).ToArray());
            Assert.Equal(typeof(bool), finalizer.GetParameters()[1].ParameterType);
        }

        [Fact]
        public void Target_TheRebuildButton_IsThePrivateDowngradeButtonField_AndItsFieldRefResolves()
        {
            var field = AccessTools.Field(typeof(KSCFacilityContextMenu), StockUiFacilityDecoration.DowngradeButtonFieldName);
            Assert.NotNull(field);
            Assert.True(field.IsPrivate);
            Assert.Equal("UnityEngine.UI.Button", field.FieldType.FullName);
            var resolve = typeof(StockUiFacilityDecoration).GetMethod("ResolveDowngradeButtonRefForTesting",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(resolve);
            Assert.NotNull(resolve.Invoke(null, null));
        }

        // ---------------- the Timeline row hover ----------------

        [Fact]
        public void TimelineRow_AFutureRebuildRow_HoldsUpgradeAndRebuild()
        {
            var rebuild = Rebuild(600, LaunchPadId, 1);
            var index = CommittedFutureIndex.Build(new[] { rebuild }, id => true, id => null, null);
            Assert.Equal("Holds Upgrade and Rebuild on this facility until D6.", ReservationExplanation.ForTimelineRow(
                new TimelineEntry { UT = 600, Action = rebuild, Source = TimelineSource.GameAction }, index, 500, Fmt));
            Assert.Null(ReservationExplanation.ForTimelineRow(
                new TimelineEntry { UT = 600, Action = rebuild, Source = TimelineSource.GameAction }, index, 600, Fmt));
        }

        // ---------------- the slot consumers read a future Rebuild as a lower level ----------------

        // catches: a slot forecast reading a committed Mission Control Rebuild as an upgrade
        // (or ignoring it): a contract accepted now that still holds its slot when the
        // facility drops to level 1 would leave no slot for the accept recorded after it.
        [Fact]
        public void ContractSlots_ACommittedMissionControlRebuild_LowersTheLimitFromItsUt()
        {
            string mc = ContractSlotReservation.MissionControlFacilityId;
            var index = CommittedFutureIndex.Build(new[]
            {
                Rebuild(300, mc, 1),
                new GameAction { UT = 400, Type = GameActionType.ContractAccept, ContractId = "c" },
            }, id => true, id => null, null);

            // Level 2 now (7 slots), one active: a new accept fits now, but after the Rebuild
            // (2 slots) "a" + the new one + "c" would be 3.
            var f = ContractSlotReservation.Forecast(index, new[] { "a" }, 7, 100.0);
            Assert.True(f.BlocksNewAcceptNow);
            Assert.Equal("c", f.FirstStarvedAccept.Key);

            // Without the Rebuild the same accept fits.
            var without = ContractSlotReservation.Forecast(CommittedFutureIndex.Build(new[]
            {
                new GameAction { UT = 400, Type = GameActionType.ContractAccept, ContractId = "c" },
            }, id => true, id => null, null), new[] { "a" }, 7, 100.0);
            Assert.False(without.BlocksNewAcceptNow);

            // A Rebuild then an upgrade back: the limit rises again from the upgrade.
            var back = ContractSlotReservation.Forecast(CommittedFutureIndex.Build(new[]
            {
                Rebuild(300, mc, 1),
                Upgrade(350, mc, 2),
                new GameAction { UT = 400, Type = GameActionType.ContractAccept, ContractId = "c" },
            }, id => true, id => null, null), new[] { "a" }, 7, 100.0);
            Assert.False(back.BlocksNewAcceptNow);
        }

        [Fact]
        public void StrategySlots_ACommittedAdministrationRebuild_LowersTheLimitFromItsUt()
        {
            var index = CommittedFutureIndex.Build(new[]
            {
                Rebuild(150, AdminId, 1),
                new GameAction { UT = 200, Type = GameActionType.StrategyActivate, StrategyId = "B", SetupCost = 1f },
            }, id => true, id => null, null);

            // Level 2 now (3 slots); after the Rebuild 1 slot, so A now + B at 200 overflow.
            var d = StrategyReservationPredicates.EvaluateActivation(index, "A", 100.0, new string[0], 3);
            Assert.True(d.Blocked);
            Assert.Equal(StrategyActivationBlockKind.SlotNeeded, d.Kind);
            Assert.Equal(1, d.LimitAtOverflow);
        }
    }
}
