using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// The Logistics Model 1 follow-ups: the near-miss list grouped by reason, stored parts
    /// named by their title, and the pickup-aware Delivers cell / line / candidate cell.
    /// All pure; the window's layout side is pinned by the source cells in
    /// <c>TableRowInsetAlignmentTests</c>.
    /// </summary>
    [Collection("Sequential")]
    public class LogisticsFollowupsTests : IDisposable
    {
        private readonly CultureInfo originalCulture;

        public LogisticsFollowupsTests()
        {
            originalCulture = Thread.CurrentThread.CurrentCulture;
        }

        public void Dispose()
        {
            Thread.CurrentThread.CurrentCulture = originalCulture;
        }

        private static readonly int StripBudget =
            TooltipEchoBox.BudgetChars(LogisticsWindowUI.DefaultWindowWidth, TooltipEchoBox.SingleLine);

        private static NearMissInput Miss(string id, string name,
            RouteAnalysisStatus status = RouteAnalysisStatus.MissingRouteProof,
            bool notSealed = false, int reflyable = 0, string detail = null)
        {
            return new NearMissInput
            {
                TreeId = id, Name = name, Status = status, NotSealed = notSealed,
                ReflyableCount = reflyable, RejectDetail = detail,
            };
        }

        // ------------------------------------------------------------------
        // Near-miss grouping
        // ------------------------------------------------------------------

        // catches: the list drawing one line per mission again instead of one per reason,
        // a group losing a mission, or the count disagreeing with the members.
        [Fact]
        public void Group_FoldsOneLinePerReason_WithCountsAndLargestFirst()
        {
            var inputs = new List<NearMissInput>
            {
                Miss("t1", "Kerbal X"),
                Miss("t2", "Probe", RouteAnalysisStatus.NoDeliveryManifest),
                Miss("t3", "Kerbal X"),
                Miss("t4", "Duna Supply 1"),
                Miss("t5", "Lander", notSealed: true, reflyable: 2),
            };
            List<NearMissGroup> groups = LogisticsNearMissPresentation.Group(inputs, StripBudget);

            Assert.Equal(3, groups.Count);
            Assert.Equal("MissingRouteProof", groups[0].Key);
            Assert.Equal(3, groups[0].Members.Count);
            Assert.Equal("No dock was recorded (3): Kerbal X [1], Kerbal X [2], Duna Supply 1",
                groups[0].HeaderText);
            // Equal sizes keep input order: NoDeliveryManifest came before NotSealed.
            Assert.Equal("NoDeliveryManifest", groups[1].Key);
            Assert.Equal("NotSealed", groups[2].Key);
            Assert.Equal("Not finished yet (1): Lander", groups[2].HeaderText);
            int total = 0;
            foreach (NearMissGroup g in groups) total += g.Members.Count;
            Assert.Equal(inputs.Count, total);
        }

        // catches: Dismiss on a grouped row acting on another mission - each member keeps
        // its own tree id, in input order, so the row's Dismiss names exactly its mission.
        [Fact]
        public void Group_EveryMemberKeepsItsOwnTreeIdForDismiss()
        {
            var inputs = new List<NearMissInput>
            {
                Miss("a", "R.1-S.2"), Miss("b", "R.1-S.2"), Miss("c", "R.2-S.3"), Miss(null, "Orphan"),
            };
            NearMissGroup g = Assert.Single(LogisticsNearMissPresentation.Group(inputs, StripBudget));
            Assert.Equal(new[] { "a", "b", "c", null },
                g.Members.ConvertAll(m => m.TreeId).ToArray());
            Assert.Equal(new[] { "R.1-S.2 [1]", "R.1-S.2 [2]", "R.2-S.3", "Orphan" },
                g.Members.ConvertAll(m => m.Label).ToArray());
        }

        // catches: the full sentence being repeated per row when every mission shares it,
        // or a shared line hiding a per-mission detail (different amounts) when they do not.
        [Fact]
        public void Group_SharedReasonOnlyWhenEveryMemberSaysTheSameThing()
        {
            NearMissGroup same = Assert.Single(LogisticsNearMissPresentation.Group(
                new List<NearMissInput> { Miss("a", "A"), Miss("b", "B") }, StripBudget));
            Assert.Equal(LogisticsRejectClauses.MissingRouteProof, same.SharedReason);

            NearMissGroup differ = Assert.Single(LogisticsNearMissPresentation.Group(
                new List<NearMissInput>
                {
                    Miss("a", "A", RouteAnalysisStatus.FlowDoesNotClose, detail: "Ore: 30.0 over-delivered"),
                    Miss("b", "B", RouteAnalysisStatus.FlowDoesNotClose, detail: "Ore: 5.0 over-delivered"),
                }, StripBudget));
            Assert.Null(differ.SharedReason);
            Assert.Contains("(Ore: 30.0 over-delivered)", differ.Members[0].Reason);
            Assert.Contains("(Ore: 5.0 over-delivered)", differ.Members[1].Reason);
            Assert.Equal("Cargo does not add up (2): A, B", differ.HeaderText);
        }

        // catches: the group line growing with the list (18 names on one line) instead of
        // a short preview that ends " ..." when names were left out.
        [Fact]
        public void GroupHeader_PreviewsNamesAndEllipsesTheRest()
        {
            var names = new List<string>();
            for (int i = 1; i <= 18; i++) names.Add("Kerbal X #" + i.ToString(CultureInfo.InvariantCulture));
            string header = LogisticsNearMissPresentation.FormatGroupHeader("No dock was recorded", names,
                LogisticsNearMissPresentation.PreviewMaxChars);
            Assert.StartsWith("No dock was recorded (18): Kerbal X #1, Kerbal X #2, ", header);
            Assert.EndsWith(" ...", header);
            Assert.True(header.Length < "No dock was recorded (18): ".Length
                + LogisticsNearMissPresentation.PreviewMaxChars + 5, header);
            // One very long name still shows (never an empty preview).
            string single = LogisticsNearMissPresentation.FormatGroupHeader("R", new[] { new string('x', 90) }, 60);
            Assert.Equal("R (1): " + new string('x', 90), single);
        }

        // catches: the hover running past the single-line strip on a long list, or the
        // "+N more" count disagreeing with the names left out.
        [Fact]
        public void NamesTooltip_FitsTheStripAndCountsWhatItLeftOut()
        {
            var names = new List<string>();
            for (int i = 1; i <= 60; i++) names.Add("Duna Supply Mission " + i.ToString(CultureInfo.InvariantCulture));
            string tip = LogisticsNearMissPresentation.FormatNamesTooltip(names, StripBudget);
            Assert.True(tip.Length <= StripBudget, tip.Length + ": " + tip);
            Assert.DoesNotContain("\n", tip);
            Assert.StartsWith("Missions: Duna Supply Mission 1, ", tip);
            int shown = tip.Split(new[] { ", " }, StringSplitOptions.None).Length - 1;
            Assert.EndsWith(", +" + (60 - shown).ToString(CultureInfo.InvariantCulture) + " more", tip);

            Assert.Equal("Missions: A, B", LogisticsNearMissPresentation.FormatNamesTooltip(new[] { "A", "B" }, StripBudget));
            Assert.Equal("Missions: +2 more",
                LogisticsNearMissPresentation.FormatNamesTooltip(new[] { new string('y', 40), "B" }, 20));
            // Every group hover from Group() is within the budget it was given.
            var inputs = new List<NearMissInput>();
            for (int i = 0; i < 60; i++) inputs.Add(Miss("t" + i, names[i]));
            foreach (NearMissGroup g in LogisticsNearMissPresentation.Group(inputs, StripBudget))
                Assert.True(g.Tooltip.Length <= StripBudget);
        }

        // catches: a reject status with no short reason (the line would open with the
        // generic "Not eligible"), and short reasons drifting into Basic-banned words.
        [Fact]
        public void EveryRejectStatusHasItsOwnShortReason()
        {
            var seen = new HashSet<string>();
            foreach (RouteAnalysisStatus st in (RouteAnalysisStatus[])Enum.GetValues(typeof(RouteAnalysisStatus)))
            {
                if (st == RouteAnalysisStatus.Eligible) continue;
                string r = LogisticsNearMissPresentation.ShortReason(st, false);
                Assert.NotEqual("Not eligible", r);
                Assert.True(seen.Add(r), "duplicate short reason " + r);
                foreach (string banned in new[] { "tree", "recording", "cycle", "dispatch", "loop" })
                    Assert.DoesNotContain(banned, r.ToLowerInvariant());
            }
            Assert.True(seen.Add(LogisticsNearMissPresentation.ShortReason(RouteAnalysisStatus.MissingRouteProof, true)));
        }

        // catches: the shared numbering helper diverging from the Flights used line.
        [Fact]
        public void NumberRepeatedNames_MatchesFlightsUsed()
        {
            Assert.Equal(new[] { "A [1]", "B", "A [2]", "<unnamed>" },
                LogisticsNearMissPresentation.NumberRepeatedNames(new[] { "A", "B", "A", null }));
            Assert.Equal("Flights used: Depot [1], Probe, Depot [2]",
                LogisticsRoutePresentation.FormatFlightsUsed(new[] { "Depot", "Probe", "Depot" }, null));
        }

        // ------------------------------------------------------------------
        // Part titles
        // ------------------------------------------------------------------

        private static string Titles(string name)
        {
            return name == "evaChute" ? "EVA Parachute" : null;
        }

        // catches: the create dialog listing internal part names, or a part with no title
        // vanishing instead of falling back to its name.
        [Fact]
        public void FormatInventoryLine_UsesTheTitleAndFallsBackToTheName()
        {
            var chute = new InventoryPayloadItem { PartName = "evaChute", Quantity = 2 };
            var jet = new InventoryPayloadItem { PartName = "evaJetpack", VariantName = "white", Quantity = 1 };
            Assert.Equal("EVA Parachute x2", RouteCreationFormatters.FormatInventoryLine(chute, Titles));
            Assert.Equal("evaJetpack (white)", RouteCreationFormatters.FormatInventoryLine(jet, Titles));
            Assert.Equal("evaChute x2", RouteCreationFormatters.FormatInventoryLine(chute));
        }

        // catches: the window's hold cell and long clause naming a stored part by its
        // internal name while the Route History names it by title.
        [Fact]
        public void HoldClauses_NameTheStoredPartByTitle_InTheCellAndTheSentence()
        {
            const RouteDispatchEvaluator.EligibilityFailureKind full =
                RouteDispatchEvaluator.EligibilityFailureKind.DestinationFull;
            string detail = "destination-full-" + RouteDestinationCapacityCheck.StoredPartTokenPrefix + "evaChute";
            Assert.Equal("Held: no slot for 'EVA Parachute'",
                LogisticsHoldPresentation.StatusCellText(full, detail, 0.0, Titles));
            Assert.Contains("'EVA Parachute'", LogisticsHoldPresentation.DescribeHold(full, detail, 0.0, Titles));
            // Unresolved: the internal name stays.
            Assert.Equal("Held: no slot for 'evaChute'",
                LogisticsHoldPresentation.StatusCellText(full, detail, 0.0, _ => null));
            Assert.Equal("Held: no slot for 'evaChute'",
                LogisticsHoldPresentation.StatusCellText(full, detail, 0.0));

            const RouteDispatchEvaluator.EligibilityFailureKind lacks =
                RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo;
            Assert.Contains("EVA Parachute",
                LogisticsHoldPresentation.StatusCellText(lacks, "inventory:evaChute", 0.0, Titles));
            Assert.Contains("EVA Parachute",
                LogisticsHoldPresentation.StatusCellText(lacks, "source:42:Depot:inventory:evaChute", 0.0, Titles));
        }

        // ------------------------------------------------------------------
        // Pickup-aware Delivers
        // ------------------------------------------------------------------

        private static RouteStop Stop(Dictionary<string, double> deliver, Dictionary<string, double> pickup,
            int deliverParts = 0, int pickupParts = 0)
        {
            var stop = new RouteStop
            {
                DeliveryManifest = deliver ?? new Dictionary<string, double>(),
                InventoryDeliveryManifest = new List<InventoryPayloadItem>(),
                PickupManifest = pickup,
            };
            for (int i = 0; i < deliverParts; i++)
                stop.InventoryDeliveryManifest.Add(new InventoryPayloadItem { PartName = "evaChute", Quantity = 1 });
            if (pickupParts > 0)
            {
                stop.InventoryPickupManifest = new List<InventoryPayloadItem>();
                for (int i = 0; i < pickupParts; i++)
                    stop.InventoryPickupManifest.Add(new InventoryPayloadItem { PartName = "evaChute", Quantity = 1 });
            }
            return stop;
        }

        private static Dictionary<string, double> Res(string k, double v)
        {
            return new Dictionary<string, double> { { k, v } };
        }

        // catches: a pure pickup route reading "(nothing)" in the Delivers cell and line.
        [Fact]
        public void PurePickupRoute_NamesWhatItPicksUpAndWhere()
        {
            var stops = new List<RouteStop> { Stop(null, Res("LiquidFuel", 154.4)) };
            var names = new[] { "B" };
            Assert.Equal("picks up 154.4 LiquidFuel at B",
                LogisticsDeliveryPresentation.FormatRouteCargoCell(stops, names));
            Assert.Equal("Picks up each run: 154.4 LiquidFuel at B.",
                LogisticsDeliveryPresentation.FormatRouteCargoLine(stops, names, "B"));
        }

        // catches: a delivery-only route changing its text at all.
        [Fact]
        public void DeliveryOnlyRoute_ReadsExactlyAsBefore()
        {
            var stops = new List<RouteStop> { Stop(Res("LiquidFuel", 257.8), null, deliverParts: 3) };
            var names = new[] { "Depot" };
            Assert.Equal(LogisticsDeliveryPresentation.FormatRouteDeliveryPerCycle(stops),
                LogisticsDeliveryPresentation.FormatRouteCargoCell(stops, names));
            Assert.Equal("LiquidFuel 257.8, 3 inventory item(s)",
                LogisticsDeliveryPresentation.FormatRouteCargoCell(stops, names));
            Assert.Equal("Delivers each run: LiquidFuel 257.8, 3 inventory item(s) to Depot.",
                LogisticsDeliveryPresentation.FormatRouteCargoLine(stops, names, "Depot"));
            Assert.Equal("Delivers each run: (nothing).",
                LogisticsDeliveryPresentation.FormatRouteCargoLine(
                    new List<RouteStop> { Stop(null, null) }, names, null));
        }

        // catches: a relay that picks up and delivers losing either half, the delivery not
        // coming first in the cell, or the line not following the visit order.
        [Fact]
        public void RelayRoute_DeliveryFirstInTheCell_VisitOrderInTheLine()
        {
            var stops = new List<RouteStop>
            {
                Stop(null, Res("LiquidFuel", 154.4), pickupParts: 1),
                Stop(Res("LiquidFuel", 200.0), null),
            };
            var names = new[] { "B", "A" };
            Assert.Equal("LiquidFuel 200.0; picks up 154.4 LiquidFuel, 1 stored part at B",
                LogisticsDeliveryPresentation.FormatRouteCargoCell(stops, names));
            Assert.Equal("Picks up each run: 154.4 LiquidFuel, 1 stored part at B, then delivers LiquidFuel 200.0 to A.",
                LogisticsDeliveryPresentation.FormatRouteCargoLine(stops, names, "A (+1 stop)"));

            // Delivery visited first: the line opens with it.
            var reversed = new List<RouteStop> { stops[1], stops[0] };
            Assert.Equal("Delivers each run: LiquidFuel 200.0 to A, then picks up 154.4 LiquidFuel, 1 stored part at B.",
                LogisticsDeliveryPresentation.FormatRouteCargoLine(reversed, new[] { "A", "B" }, "A"));
            // A stop with no name reads "-", never blank.
            Assert.Equal("picks up 154.4 LiquidFuel at -",
                LogisticsDeliveryPresentation.FormatRouteCargoCell(
                    new List<RouteStop> { Stop(null, Res("LiquidFuel", 154.4)) }, null));
        }

        // catches: comma-locale digits leaking into the pickup text.
        [Fact]
        public void PickupText_IsInvariantUnderCommaLocale()
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            var stops = new List<RouteStop>
            {
                Stop(Res("Oxidizer", 1240.5), new Dictionary<string, double> { { "Ore", 30.26 }, { "LiquidFuel", 154.44 } }),
            };
            string cell = LogisticsDeliveryPresentation.FormatRouteCargoCell(stops, new[] { "B" });
            Assert.Equal("Oxidizer 1240.5; picks up 154.4 LiquidFuel, 30.3 Ore at B", cell);
            Assert.Equal("Picks up each run: 154.4 LiquidFuel, 30.3 Ore at B, then delivers Oxidizer 1240.5 to B.",
                LogisticsDeliveryPresentation.FormatRouteCargoLine(stops, new[] { "B" }, "B"));
            Assert.Equal("picks up 2.5 Ore at Mun (surface)",
                LogisticsDeliveryPresentation.FormatCandidateCargo(null, null, Res("Ore", 2.5), null, "Mun (surface)"));
        }

        // catches: a candidate that only loads cargo reading "(nothing)", or a delivery-only
        // candidate changing its Would deliver text.
        [Fact]
        public void CandidateCargo_NamesPickupsAfterTheDelivery()
        {
            Assert.Equal("LiquidFuel 97.6",
                LogisticsDeliveryPresentation.FormatCandidateCargo(Res("LiquidFuel", 97.6), null, null, null, "Depot"));
            Assert.Equal("LiquidFuel 97.6; picks up 30.0 Ore at Depot",
                LogisticsDeliveryPresentation.FormatCandidateCargo(Res("LiquidFuel", 97.6), null, Res("Ore", 30.0), null, "Depot"));
            Assert.Equal("picks up 1 stored part at Depot",
                LogisticsDeliveryPresentation.FormatCandidateCargo(null, null, null,
                    new List<InventoryPayloadItem> { new InventoryPayloadItem { PartName = "evaChute" } }, "Depot"));
        }

        // catches: the create dialog of a pickup run listing no cargo at all, or a
        // delivery-only dialog growing a pickup block.
        [Fact]
        public void CreateDialog_ListsPickupsByTitle_OnlyWhenTheRunLoadsCargo()
        {
            RouteAnalysisResult analysis = EligibleAnalysis();
            string plain = RouteCreationFormatters.BuildSummaryBlock(analysis, Game.Modes.SANDBOX);
            Assert.DoesNotContain("Picks up:", plain);

            analysis.ResourceLoadManifest = Res("LiquidFuel", 154.4);
            analysis.InventoryLoadManifest = new List<InventoryPayloadItem>
            {
                new InventoryPayloadItem { PartName = "evaChute", Quantity = 1 },
            };
            analysis.InventoryDeliveryManifest.Add(new InventoryPayloadItem { PartName = "evaChute", Quantity = 3 });
            string block = RouteCreationFormatters.BuildSummaryBlock(
                analysis, Game.Modes.SANDBOX, null, null, Titles);
            Assert.Contains("Inventory:\n  - EVA Parachute x3\n", block);
            Assert.Contains("Picks up:\n  - LiquidFuel: 154.4\n  - EVA Parachute\n", block);
        }

        private static RouteAnalysisResult EligibleAnalysis()
        {
            var source = new Recording
            {
                RecordingId = "src",
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                ExplicitStartUT = 0.0,
                ExplicitEndUT = 600.0,
                RouteConnectionWindows = new List<RouteConnectionWindow>()
            };
            var window = new RouteConnectionWindow
            {
                TransferTargetVesselPid = 9001,
                TransferKind = RouteConnectionKind.DockingPort,
                EndpointAtDock = new RouteEndpoint { BodyName = "Mun", Latitude = 1.0, Longitude = 2.0, Altitude = 100.0 }
            };
            source.RouteConnectionWindows.Add(window);
            return new RouteAnalysisResult
            {
                Status = RouteAnalysisStatus.Eligible,
                SourceRecording = source,
                ConnectionWindow = window,
                ResourceDeliveryManifest = Res("LiquidFuel", 50.0),
                InventoryDeliveryManifest = new List<InventoryPayloadItem>()
            };
        }

        // ------------------------------------------------------------------
        // Hover budget for the new runtime-composed / constant hovers
        // ------------------------------------------------------------------

        // catches: a new candidate or near-miss hover growing past the single-line strip.
        [Fact]
        public void NewLogisticsHovers_FitTheSingleLineStrip()
        {
            foreach (string t in new[]
                     {
                         LogisticsRoutePresentation.CreateRouteButtonTooltip,
                         LogisticsRoutePresentation.DismissButtonTooltip,
                         LogisticsRoutePresentation.CandidateTransitTooltip,
                     })
            {
                Assert.DoesNotContain("\n", t);
                Assert.True(t.Length <= StripBudget, t.Length + ": " + t);
            }
        }
    }
}
