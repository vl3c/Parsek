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
    /// KSC-REPAIR-AFTER-REWIND-DOUBLE-CHARGE: a facility whose destroyed building's
    /// destruction a committed row already repairs later has its Repair refused
    /// (<see cref="FacilityRepairBlock"/>, run first in the <c>RepairFacility</c> prefix) and
    /// its facility menu Repair button greyed with the same text
    /// (<see cref="StockUiDecorationQuery.ForFacilityMenuRepair"/>). The predicate, the
    /// explanation, the log lines, the pairing and the stock members read.
    /// </summary>
    [Collection("Sequential")]
    public class FacilityRepairBlockTests : IDisposable
    {
        private const string LaunchPadId = "SpaceCenter/LaunchPad";
        private const string PadBuilding = "SpaceCenter/LaunchPad/Facility/LaunchPadMedium/ksp_pad_launchPad";
        private const string PadTower = "SpaceCenter/LaunchPad/Facility/LaunchPadMedium/ksp_pad_waterTower";
        private const string VabId = "SpaceCenter/VehicleAssemblyBuilding";
        private const string VabBuilding = "SpaceCenter/VehicleAssemblyBuilding/Facility/mainBuilding";

        private readonly List<string> logLines = new List<string>();
        private readonly List<(string title, string reason)> dialogs = new List<(string, string)>();

        private static readonly Func<double, string> Fmt =
            ut => "D" + ((long)(ut / 100.0)).ToString(CultureInfo.InvariantCulture);

        public FacilityRepairBlockTests()
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

        private static GameAction Destroy(double ut, string buildingId) =>
            new GameAction { UT = ut, Type = GameActionType.FacilityDestruction, FacilityId = buildingId };

        private static GameAction Repair(double ut, string buildingId, float cost = 4000f) =>
            new GameAction { UT = ut, Type = GameActionType.FacilityRepair, FacilityId = buildingId, FacilityCost = cost };

        private static FacilityRepairCapture.BuildingRepairInput B(string id, bool destroyed) =>
            new FacilityRepairCapture.BuildingRepairInput { BuildingId = id, IsDestroyed = destroyed, RepairCost = 4000f };

        private static List<FacilityRepairCapture.BuildingRepairInput> Pad(bool padDestroyed, bool towerDestroyed = false) =>
            new List<FacilityRepairCapture.BuildingRepairInput> { B(PadBuilding, padDestroyed), B(PadTower, towerDestroyed) };

        private static string Expected(string date) =>
            "Repaired on " + date + " on your committed timeline. " + ReservationExplanation.TimelineRule
            + " The repair happens on that date.";

        // ---------------- index ----------------

        [Fact]
        public void Index_ClassifiesDestructionAndRepairRowsByBuildingId_AndCountsThem()
        {
            CommittedFutureKind kind;
            string key;
            Assert.True(CommittedFutureIndex.TryClassify(Repair(10, PadBuilding), out kind, out key));
            Assert.Equal(CommittedFutureKind.FacilityRepair, kind);
            Assert.Equal(PadBuilding, key);
            Assert.True(CommittedFutureIndex.TryClassify(Destroy(10, PadBuilding), out kind, out key));
            Assert.Equal(CommittedFutureKind.FacilityDestruction, kind);
            Assert.False(CommittedFutureIndex.TryClassify(Repair(10, null), out kind, out key));

            Ledger.AddAction(Destroy(100, PadBuilding));
            Ledger.AddAction(Repair(1000, PadBuilding, 4000f));
            var index = CommittedFutureIndexCache.Current;
            Assert.Equal(4000f, index.FirstFuture(CommittedFutureKind.FacilityRepair, PadBuilding, 500).Amount);
            Assert.Contains("destroy=1 repair=1 ", index.DescribeCounts());
        }

        // ---------------- predicate ----------------

        [Fact]
        public void Blocked_DestroyedBuilding_WithACommittedFutureRepair_ExplainsWithTheRepairDate()
        {
            Ledger.AddAction(Destroy(100, PadBuilding));
            Ledger.AddAction(Repair(1000, PadBuilding));
            var index = CommittedFutureIndexCache.Current;

            Assert.True(StockUiReservationPredicates.IsFacilityRepairBlocked(index, Pad(true), 500));
            var text = StockUiReservationPredicates.ExplainFacilityRepair(index, Pad(true), 500, Fmt);
            Assert.Equal("Repaired on D10", text.Title);
            Assert.Equal(Expected("D10"), text.Body);
            Assert.DoesNotContain("SpaceCenter", text.Body);
        }

        [Theory]
        [InlineData(1000.0)]
        [InlineData(5000.0)]
        public void NotBlocked_AtAndAfterTheCommittedRepair(double now)
        {
            Ledger.AddAction(Destroy(100, PadBuilding));
            Ledger.AddAction(Repair(1000, PadBuilding));
            Assert.False(StockUiReservationPredicates.IsFacilityRepairBlocked(
                CommittedFutureIndexCache.Current, Pad(true), now));
        }

        [Fact]
        public void NotBlocked_AnotherBuilding_AnIntactBuilding_NoRows_OrNoBuildings()
        {
            Ledger.AddAction(Destroy(100, PadBuilding));
            Ledger.AddAction(Repair(1000, PadBuilding));
            var index = CommittedFutureIndexCache.Current;

            // Another facility's destroyed building.
            Assert.False(StockUiReservationPredicates.IsFacilityRepairBlocked(index,
                new[] { B(VabBuilding, true) }, 500));
            // The same facility, but only the tower (no committed repair) is destroyed.
            Assert.False(StockUiReservationPredicates.IsFacilityRepairBlocked(index, Pad(false, true), 500));
            // The covered building is intact now (stock's Repair would not touch it).
            Assert.False(StockUiReservationPredicates.IsFacilityRepairBlocked(index, Pad(false), 500));
            Assert.False(StockUiReservationPredicates.IsFacilityRepairBlocked(index, null, 500));
            Assert.False(StockUiReservationPredicates.IsFacilityRepairBlocked(null, Pad(true), 500));

            CommittedFutureIndexCache.ResetForTesting();
            Ledger.ResetForTesting();
            Assert.False(StockUiReservationPredicates.IsFacilityRepairBlocked(
                CommittedFutureIndexCache.Current, Pad(true), 500));
        }

        [Fact]
        public void NotBlocked_WhenACommittedDestructionComesFirst_TheLiveDestructionIsANewOne()
        {
            // Rewound to before the committed destruction; the player destroyed the pad live.
            Ledger.AddAction(Destroy(300, PadBuilding));
            Ledger.AddAction(Repair(1000, PadBuilding));
            var index = CommittedFutureIndexCache.Current;
            Assert.False(StockUiReservationPredicates.IsFacilityRepairBlocked(index, Pad(true), 200));
            Assert.Null(StockUiReservationPredicates.CommittedRepairCoveringDestruction(index, PadBuilding, 200));
            // Past the committed destruction, the committed repair covers it again.
            Assert.True(StockUiReservationPredicates.IsFacilityRepairBlocked(index, Pad(true), 400));
            // A destruction and a repair at the same UT do not cover a destruction before them.
            Ledger.AddAction(Destroy(2000, PadBuilding));
            Ledger.AddAction(Repair(2000, PadBuilding));
            CommittedFutureIndexCache.Invalidate("test");
            Assert.Null(StockUiReservationPredicates.CommittedRepairCoveringDestruction(
                CommittedFutureIndexCache.Current, PadBuilding, 1500));
        }

        [Fact]
        public void Blocked_TwoBuildingsRepairedOnDifferentDates_ListsBoth_AndLiftsBuildingByBuilding()
        {
            Ledger.AddAction(Destroy(100, PadBuilding));
            Ledger.AddAction(Destroy(100, PadTower));
            Ledger.AddAction(Repair(1000, PadBuilding));
            Ledger.AddAction(Repair(2000, PadTower));
            var index = CommittedFutureIndexCache.Current;

            var both = StockUiReservationPredicates.ExplainFacilityRepair(index, Pad(true, true), 500, Fmt);
            Assert.Equal("Repaired on D10 and D20 on your committed timeline. " + ReservationExplanation.TimelineRule
                + " The repairs happen on those dates.", both.Body);
            // The pad is repaired by the walk at 1000; the tower is still covered.
            Assert.True(StockUiReservationPredicates.IsFacilityRepairBlocked(index, Pad(false, true), 1500));
            Assert.Equal(Expected("D20"),
                StockUiReservationPredicates.ExplainFacilityRepair(index, Pad(false, true), 1500, Fmt).Body);
        }

        [Fact]
        public void Explanation_NamesTheCommittedFlight_WhenOneFlightRepairedIt()
        {
            var entry = new CommittedFutureEntry(CommittedFutureKind.FacilityRepair, PadBuilding, 1000, "rec-1", "Pad Fixer");
            var text = ReservationExplanation.FacilityRepair(new[] { entry }, Fmt);
            Assert.Equal("Repaired on D10 by the committed flight 'Pad Fixer'. " + ReservationExplanation.TimelineRule
                + " The repair happens on that date.", text.Body);
            Assert.Equal("Repaired on your committed timeline. " + ReservationExplanation.TimelineRule,
                ReservationExplanation.FacilityRepair(new CommittedFutureEntry[0], Fmt).Body);
        }

        // ---------------- menu decoration ----------------

        [Fact]
        public void ForFacilityMenuRepair_MarksAndBlocks_AndReplayLeavesItStock()
        {
            Ledger.AddAction(Destroy(100, PadBuilding));
            Ledger.AddAction(Repair(1000, PadBuilding));
            var index = CommittedFutureIndexCache.Current;

            var d = StockUiDecorationQuery.ForFacilityMenuRepair(index, 500, LaunchPadId, Pad(true), false, Fmt);
            Assert.Equal(StockUiScreen.FacilityMenu, d.Screen);
            Assert.Equal(StockUiDecorationQuery.FacilityMenuRepairTab, d.Tab);
            Assert.Equal(StockUiDecorationKind.FacilityRepair, d.Kind);
            Assert.Equal(LaunchPadId, d.Id);
            Assert.True(d.Marked);
            Assert.True(d.Blocked);
            Assert.Equal(1000, d.UT);
            Assert.Equal(Expected("D10"), d.Why);
            Assert.True(StockUiFacilityDecoration.Decide(d).DisableUpgrade);

            var replay = StockUiDecorationQuery.ForFacilityMenuRepair(index, 500, LaunchPadId, Pad(true), true, Fmt);
            Assert.False(replay.Marked);
            Assert.False(replay.Blocked);
            var after = StockUiDecorationQuery.ForFacilityMenuRepair(index, 1000, LaunchPadId, Pad(true), false, Fmt);
            Assert.False(after.Blocked);
            Assert.Equal(StockUiDecorationKind.None, after.Kind);
        }

        [Fact]
        public void LogFacilityMenuRepair_MarkedIsThePassItemLine_UnmarkedSaysWhy()
        {
            Ledger.AddAction(Destroy(100, PadBuilding));
            Ledger.AddAction(Repair(1000, PadBuilding));
            var index = CommittedFutureIndexCache.Current;

            var d = StockUiDecorationQuery.ForFacilityMenuRepair(index, 500, LaunchPadId, Pad(true), false, Fmt);
            StockUiDecorationQuery.LogFacilityMenuRepair(d, false, true, "values modified");
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][StockUiOverlay]")
                && l.Contains("decorate screen=FacilityMenu tab=Repair item=SpaceCenter/LaunchPad kind=FacilityRepair "
                    + "marked=true blocked=true why=\"Repaired on D10 on your committed timeline."));

            StockUiDecorationQuery.LogFacilityMenuRepair(
                StockUiDecorationQuery.ForFacilityMenuRepair(index, 500, VabId, new[] { B(VabBuilding, false) }, false, Fmt),
                false, false, "values modified");
            StockUiDecorationQuery.LogFacilityMenuRepair(
                StockUiDecorationQuery.ForFacilityMenuRepair(index, 500, LaunchPadId, Pad(false, true), false, Fmt),
                false, true, "values modified");
            StockUiDecorationQuery.LogFacilityMenuRepair(
                StockUiDecorationQuery.ForFacilityMenuRepair(index, 500, LaunchPadId, Pad(true), true, Fmt),
                true, true, "timeline changed");
            Assert.Contains(logLines, l => l.Contains(
                "FacilityMenu SpaceCenter/VehicleAssemblyBuilding Repair left to stock (values modified): no destroyed building"));
            Assert.Contains(logLines, l => l.Contains(
                "FacilityMenu SpaceCenter/LaunchPad Repair left to stock (values modified): no committed future repair covers the destruction"));
            Assert.Contains(logLines, l => l.Contains(
                "Repair left to stock (timeline changed): action replay in progress"));
            // The unmarked sentence is not a `decorate` line, so the GUI mirror never reads it as a row.
            Assert.DoesNotContain(logLines, l => l.Contains("Repair left to stock") && l.Contains("decorate "));
        }

        // ---------------- pairing: the greyed set == the refused set ----------------

        [Fact]
        public void Pairing_TheGreyedRepairIsExactlyTheRefusedRepair_WithTheSameText()
        {
            Ledger.AddAction(Destroy(100, PadBuilding));
            Ledger.AddAction(Repair(1000, PadBuilding));
            Ledger.AddAction(Destroy(100, VabBuilding));
            Ledger.AddAction(Repair(50, VabBuilding));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 500;

            var cases = new[]
            {
                (id: LaunchPadId, buildings: Pad(true)),
                (id: VabId, buildings: new List<FacilityRepairCapture.BuildingRepairInput> { B(VabBuilding, true) }),
            };
            var greyed = cases.Where(c => StockUiFacilityDecoration.Decide(StockUiDecorationQuery.ForFacilityMenuRepair(
                CommittedFutureIndexCache.Current, 500, c.id, c.buildings, false,
                ReservationExplanation.DefaultDateFormatter)).DisableUpgrade).Select(c => c.id).ToArray();
            var refused = cases.Where(c => FacilityRepairBlock.TryBlockFacilityRepairForBuildings(c.id, c.buildings))
                .Select(c => c.id).ToArray();

            Assert.Equal(new[] { LaunchPadId }, greyed);
            Assert.Equal(new[] { LaunchPadId }, refused);
            var dialog = dialogs.Single();
            Assert.Equal("Cannot repair \"Launch Pad\"", dialog.title);
            Assert.Equal(StockUiDecorationQuery.ForFacilityMenuRepair(CommittedFutureIndexCache.Current, 500, LaunchPadId,
                Pad(true), false, ReservationExplanation.DefaultDateFormatter).Why, dialog.reason);
            Assert.DoesNotContain("SpaceCenter", dialog.reason);
            Assert.Contains(logLines, l => l.Contains("[INFO][FacilityRepairPatch]")
                && l.Contains("Blocking facility repair: 'SpaceCenter/LaunchPad' - 1 destroyed building(s) of 1")
                && l.Contains("ut=1000") && l.Contains("nowUT=500"));
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][FacilityRepairPatch]")
                && l.Contains("Allowing facility repair: 'SpaceCenter/VehicleAssemblyBuilding' - destroyedBuildings=1"));
        }

        [Fact]
        public void Pairing_TheRefusalLiftsWithTheGreyedButton_OnceTheClockPassesTheRepair()
        {
            Ledger.AddAction(Destroy(100, PadBuilding));
            Ledger.AddAction(Repair(1000, PadBuilding));
            foreach (double now in new[] { 150.0, 999.9, 1000.0, 3000.0 })
            {
                CommittedFutureIndexCache.NowUtProviderForTesting = () => now;
                bool greyed = StockUiFacilityDecoration.Decide(StockUiDecorationQuery.ForFacilityMenuRepair(
                    CommittedFutureIndexCache.Current, now, LaunchPadId, Pad(true), false, Fmt)).DisableUpgrade;
                bool refused = FacilityRepairBlock.TryBlockFacilityRepairForBuildings(LaunchPadId, Pad(true));
                Assert.Equal(now < 1000.0, greyed);
                Assert.Equal(greyed, refused);
            }
            Assert.Equal(2, dialogs.Count);
        }

        [Fact]
        public void Pairing_DuringReplay_NeitherTheMenuNorTheRefusalBlocks()
        {
            Ledger.AddAction(Destroy(100, PadBuilding));
            Ledger.AddAction(Repair(1000, PadBuilding));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 500;
            GameStateRecorder.IsReplayingActions = true;

            Assert.False(StockUiDecorationQuery.ForFacilityMenuRepair(
                CommittedFutureIndexCache.Current, 500, LaunchPadId, Pad(true), true, Fmt).Blocked);
            Assert.False(FacilityRepairBlock.TryBlockFacilityRepairForBuildings(LaunchPadId, Pad(true)));
            Assert.Empty(dialogs);
            Assert.Contains(logLines, l => l.Contains("[FacilityRepairPatch]") && l.Contains("action replay in progress"));
        }

        // ---------------- stock members ----------------

        [Fact]
        public void Target_RepairButton_IsTheProtectedButtonField_AndItsFieldRefResolves()
        {
            var field = AccessTools.Field(typeof(KSCFacilityContextMenu), StockUiFacilityDecoration.RepairButtonFieldName);
            Assert.NotNull(field);
            Assert.True(field.IsFamily, "stock declares RepairButton protected");
            Assert.Equal("UnityEngine.UI.Button", field.FieldType.FullName);
            // The test project does not reference UnityEngine.UI, so the accessor is invoked by name.
            var resolve = typeof(StockUiFacilityDecoration).GetMethod("ResolveRepairButtonRefForTesting",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(resolve);
            Assert.NotNull(resolve.Invoke(null, null));
        }

        [Fact]
        public void Target_TheRepairFacilityPrefix_CanSkipTheOriginal()
        {
            var prefix = typeof(FacilityRepairScopePatch).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(prefix);
            Assert.Equal(typeof(bool), prefix.ReturnType);
            Assert.NotNull(typeof(SpaceCenterBuilding).GetMethod("RepairFacility",
                BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(bool) }, null));
            Assert.NotNull(typeof(DestructibleBuilding).GetProperty("IsDestroyed"));
            Assert.NotNull(typeof(DestructibleBuilding).GetField("id"));
        }

        [Fact]
        public void BlockedDialogTitle_NamesTheFacility_FromAFacilityOrABuildingId()
        {
            Assert.Equal("Cannot repair \"Launch Pad\"", FacilityRepairBlock.BlockedDialogTitle(LaunchPadId));
            Assert.Equal("Cannot repair \"Launch Pad\"", FacilityRepairBlock.BlockedDialogTitle(PadBuilding));
        }
    }
}
