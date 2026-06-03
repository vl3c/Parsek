using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the pure presentation helpers in
    /// <see cref="LogisticsDeliveryPresentation"/> backing Phase 2 H2 (realized
    /// delivery + cumulative total), H3 (delivery badge classify), H4 (destination
    /// name formatter), and H5 (source recording name formatter). Every helper is
    /// Unity-free and side-effect-free, so they are exercised directly without IMGUI
    /// or a live store. RouteStatus is internal, so the H3 badge tests compute the
    /// ghostDriving bool inside the test body from
    /// <see cref="RouteStatusPolicy.GhostDriving"/> rather than passing the enum
    /// through a public signature.
    /// </summary>
    public class LogisticsDeliveryPresentationTests
    {
        // ------------------------------------------------------------------
        // H2: FormatRealizedDelivery
        // ------------------------------------------------------------------

        // Full fill: requested is null (the recorder's "no shortfall" signal), so the
        // line is just "delivered N Resource" with no shortfall clause.
        [Fact]
        public void FormatRealizedDelivery_FullFill_NoShortfallClause()
        {
            var actual = new Dictionary<string, double> { { "LiquidFuel", 150.0 } };
            string s = LogisticsDeliveryPresentation.FormatRealizedDelivery(null, actual);
            Assert.Equal("delivered 150.0 LiquidFuel", s);
        }

        // Partial fill: requested set above actual; show delivered-of-requested and
        // the amount that did not fit.
        [Fact]
        public void FormatRealizedDelivery_PartialFill_ShowsShortfall()
        {
            var requested = new Dictionary<string, double> { { "LiquidFuel", 150.0 } };
            var actual = new Dictionary<string, double> { { "LiquidFuel", 40.0 } };
            string s = LogisticsDeliveryPresentation.FormatRealizedDelivery(requested, actual);
            Assert.Equal("delivered 40.0 of 150.0 LiquidFuel (110.0 did not fit)", s);
        }

        // A requested entry that matches the actual exactly is treated as a full fill
        // (no shortfall clause) even though a requested manifest was present.
        [Fact]
        public void FormatRealizedDelivery_RequestedEqualsActual_NoShortfall()
        {
            var requested = new Dictionary<string, double> { { "Ore", 20.0 } };
            var actual = new Dictionary<string, double> { { "Ore", 20.0 } };
            string s = LogisticsDeliveryPresentation.FormatRealizedDelivery(requested, actual);
            Assert.Equal("delivered 20.0 Ore", s);
        }

        // Empty / null actual: nothing was delivered this cycle.
        [Fact]
        public void FormatRealizedDelivery_EmptyActual_DeliveredNothing()
        {
            Assert.Equal("delivered nothing",
                LogisticsDeliveryPresentation.FormatRealizedDelivery(null, new Dictionary<string, double>()));
            Assert.Equal("delivered nothing",
                LogisticsDeliveryPresentation.FormatRealizedDelivery(null, null));
        }

        // Comma-decimal locale must not change the output: F1 + InvariantCulture, no
        // thousands separator on a large amount.
        [Fact]
        public void FormatRealizedDelivery_IsInvariantUnderCommaLocale()
        {
            CultureInfo prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var actual = new Dictionary<string, double> { { "LiquidFuel", 1240.0 } };
                string s = LogisticsDeliveryPresentation.FormatRealizedDelivery(null, actual);
                Assert.Equal("delivered 1240.0 LiquidFuel", s);
                Assert.DoesNotContain(",", s);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prev;
            }
        }

        [Fact]
        public void HasShortfall_OnlyTrueWhenRequestedExceedsActual()
        {
            var actual = new Dictionary<string, double> { { "LiquidFuel", 40.0 } };
            Assert.True(LogisticsDeliveryPresentation.HasShortfall(
                new Dictionary<string, double> { { "LiquidFuel", 150.0 } }, actual));
            Assert.False(LogisticsDeliveryPresentation.HasShortfall(
                new Dictionary<string, double> { { "LiquidFuel", 40.0 } }, actual));
            // Null requested is a full fill, never a shortfall.
            Assert.False(LogisticsDeliveryPresentation.HasShortfall(null, actual));
        }

        // A resource that was requested but fully blocked (absent from actual) is a
        // shortfall, not just a partial fill of a delivered resource.
        [Fact]
        public void HasShortfall_RequestedResourceFullyBlocked_IsShortfall()
        {
            var actual = new Dictionary<string, double> { { "LiquidFuel", 40.0 } };
            var requested = new Dictionary<string, double> { { "LiquidFuel", 40.0 }, { "Oxidizer", 20.0 } };
            // LiquidFuel fully filled, but Oxidizer was requested and 0 delivered.
            Assert.True(LogisticsDeliveryPresentation.HasShortfall(requested, actual));
        }

        // Multi-resource output is sorted by resource key (ordinal) so the per-resource
        // order is stable across cache refreshes regardless of dictionary insertion order.
        [Fact]
        public void FormatRealizedDelivery_MultiResource_OrdinalSortedStableOrder()
        {
            // Inserted Oxidizer-first; output must still list LiquidFuel first.
            var actual = new Dictionary<string, double> { { "Oxidizer", 10.0 }, { "LiquidFuel", 100.0 } };
            string s = LogisticsDeliveryPresentation.FormatRealizedDelivery(null, actual);
            Assert.Equal("delivered 100.0 LiquidFuel, 10.0 Oxidizer", s);
        }

        // Mixed partial cycle: one resource partially filled, another requested but
        // fully blocked. The fully-blocked resource is included as "0.0 of X (X did not fit)".
        [Fact]
        public void FormatRealizedDelivery_RequestedButFullyBlocked_IncludesZeroDeliveredClause()
        {
            var requested = new Dictionary<string, double> { { "LiquidFuel", 50.0 }, { "Oxidizer", 20.0 } };
            var actual = new Dictionary<string, double> { { "LiquidFuel", 40.0 } };
            string s = LogisticsDeliveryPresentation.FormatRealizedDelivery(requested, actual);
            Assert.Equal(
                "delivered 40.0 of 50.0 LiquidFuel (10.0 did not fit), 0.0 of 20.0 Oxidizer (20.0 did not fit)",
                s);
        }

        // ------------------------------------------------------------------
        // H2: SummarizeRouteDeliveries + FormatCumulativeTotal
        // ------------------------------------------------------------------

        [Fact]
        public void SummarizeRouteDeliveries_EmptyList_ZeroSummary()
        {
            var summary = LogisticsDeliveryPresentation.SummarizeRouteDeliveries(
                new List<LogisticsDeliveryPresentation.DeliveryRow>());
            Assert.False(summary.HasAny);
            Assert.Equal(0, summary.RowCount);
            Assert.Null(summary.LastActual);
            Assert.Empty(summary.CumulativeTotal);
        }

        [Fact]
        public void SummarizeRouteDeliveries_NullList_ZeroSummary()
        {
            var summary = LogisticsDeliveryPresentation.SummarizeRouteDeliveries(null);
            Assert.False(summary.HasAny);
            Assert.Equal(0, summary.RowCount);
        }

        // Latest row is the one with the maximum UT regardless of input order; the
        // cumulative total sums each row's actual per resource.
        [Fact]
        public void SummarizeRouteDeliveries_PicksLatestByUt_AndSumsCumulative()
        {
            var rows = new List<LogisticsDeliveryPresentation.DeliveryRow>
            {
                new LogisticsDeliveryPresentation.DeliveryRow(
                    new Dictionary<string, double> { { "LiquidFuel", 100.0 } }, null, ut: 100.0),
                // Latest by UT, but listed in the middle to prove ordering is by UT.
                new LogisticsDeliveryPresentation.DeliveryRow(
                    new Dictionary<string, double> { { "LiquidFuel", 40.0 } },
                    new Dictionary<string, double> { { "LiquidFuel", 150.0 } }, ut: 300.0),
                new LogisticsDeliveryPresentation.DeliveryRow(
                    new Dictionary<string, double> { { "LiquidFuel", 60.0 }, { "Oxidizer", 10.0 } }, null, ut: 200.0),
            };

            var summary = LogisticsDeliveryPresentation.SummarizeRouteDeliveries(rows);

            Assert.True(summary.HasAny);
            Assert.Equal(3, summary.RowCount);
            // Latest row (ut=300) had the partial fill.
            Assert.Equal(40.0, summary.LastActual["LiquidFuel"]);
            Assert.NotNull(summary.LastRequested);
            Assert.Equal(150.0, summary.LastRequested["LiquidFuel"]);
            // Cumulative sums across all three rows.
            Assert.Equal(200.0, summary.CumulativeTotal["LiquidFuel"]);
            Assert.Equal(10.0, summary.CumulativeTotal["Oxidizer"]);
        }

        [Fact]
        public void FormatCumulativeTotal_EmptyOrNull_ShowsNone()
        {
            Assert.Equal("(none)", LogisticsDeliveryPresentation.FormatCumulativeTotal(null));
            Assert.Equal("(none)", LogisticsDeliveryPresentation.FormatCumulativeTotal(new Dictionary<string, double>()));
        }

        // Cumulative total is sorted by resource key (ordinal) for stable display order.
        [Fact]
        public void FormatCumulativeTotal_MultiResource_OrdinalSortedStableOrder()
        {
            var total = new Dictionary<string, double> { { "Oxidizer", 10.0 }, { "LiquidFuel", 100.0 } };
            Assert.Equal("100.0 LiquidFuel, 10.0 Oxidizer",
                LogisticsDeliveryPresentation.FormatCumulativeTotal(total));
        }

        [Fact]
        public void FormatCumulativeTotal_IsInvariantNoSeparator()
        {
            CultureInfo prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var total = new Dictionary<string, double> { { "LiquidFuel", 1240.0 } };
                string s = LogisticsDeliveryPresentation.FormatCumulativeTotal(total);
                Assert.Equal("1240.0 LiquidFuel", s);
                Assert.DoesNotContain(",", s);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prev;
            }
        }

        // ------------------------------------------------------------------
        // H3: ClassifyDeliveryBadge + DeliveryBadgeLabel
        // ------------------------------------------------------------------

        // Ghost-driving with a delivered last cycle (full or partial) is Delivering.
        [Theory]
        [InlineData((int)LogisticsDeliveryPresentation.DeliveryOutcome.Full)]
        [InlineData((int)LogisticsDeliveryPresentation.DeliveryOutcome.Partial)]
        public void ClassifyDeliveryBadge_DrivingAndDelivered_IsDelivering(int outcomeOrdinal)
        {
            var badge = LogisticsDeliveryPresentation.ClassifyDeliveryBadge(
                ghostDriving: true,
                lastOutcome: (LogisticsDeliveryPresentation.DeliveryOutcome)outcomeOrdinal,
                completedCycles: 2,
                skippedCycles: 0);
            Assert.Equal(LogisticsDeliveryPresentation.DeliveryBadge.Delivering, badge);
        }

        // Ghost-driving, no fresh delivered row, and a cycle has been skipped before:
        // flying but not delivering.
        [Fact]
        public void ClassifyDeliveryBadge_DrivingBlockedAfterRunning_IsFlyingNotDelivering()
        {
            var badge = LogisticsDeliveryPresentation.ClassifyDeliveryBadge(
                ghostDriving: true,
                lastOutcome: LogisticsDeliveryPresentation.DeliveryOutcome.None,
                completedCycles: 0,
                skippedCycles: 2);
            Assert.Equal(LogisticsDeliveryPresentation.DeliveryBadge.FlyingNotDelivering, badge);
        }

        // Ghost-driving with prior completed cycles but no fresh row this cycle:
        // flying but not delivering (the latest cycle delivered nothing).
        [Fact]
        public void ClassifyDeliveryBadge_DrivingHadCompletedButNoFreshRow_IsFlyingNotDelivering()
        {
            var badge = LogisticsDeliveryPresentation.ClassifyDeliveryBadge(
                ghostDriving: true,
                lastOutcome: LogisticsDeliveryPresentation.DeliveryOutcome.None,
                completedCycles: 3,
                skippedCycles: 0);
            Assert.Equal(LogisticsDeliveryPresentation.DeliveryBadge.FlyingNotDelivering, badge);
        }

        // Ghost-driving but never run (no delivered row, no completed, no skipped): New.
        [Fact]
        public void ClassifyDeliveryBadge_DrivingNeverRun_IsNew()
        {
            var badge = LogisticsDeliveryPresentation.ClassifyDeliveryBadge(
                ghostDriving: true,
                lastOutcome: LogisticsDeliveryPresentation.DeliveryOutcome.None,
                completedCycles: 0,
                skippedCycles: 0);
            Assert.Equal(LogisticsDeliveryPresentation.DeliveryBadge.New, badge);
        }

        // Not ghost-driving (paused or hard-broken): Paused, regardless of any stale
        // outcome or completed-cycle history.
        [Theory]
        [InlineData((int)LogisticsDeliveryPresentation.DeliveryOutcome.None, 0, 0)]
        [InlineData((int)LogisticsDeliveryPresentation.DeliveryOutcome.Full, 5, 0)]
        public void ClassifyDeliveryBadge_NotDriving_IsPaused(int outcomeOrdinal, int completed, int skipped)
        {
            var badge = LogisticsDeliveryPresentation.ClassifyDeliveryBadge(
                ghostDriving: false,
                lastOutcome: (LogisticsDeliveryPresentation.DeliveryOutcome)outcomeOrdinal,
                completedCycles: completed,
                skippedCycles: skipped);
            Assert.Equal(LogisticsDeliveryPresentation.DeliveryBadge.Paused, badge);
        }

        // End-to-end: a flying WaitingForResources route (ghost-driving true, no fresh
        // row, a skipped cycle) reads "Flying, not delivering" per the H3 acceptance
        // criterion. ghostDriving is computed inside the body since RouteStatus is internal.
        [Fact]
        public void ClassifyDeliveryBadge_FlyingWaitingForResources_IsFlyingNotDelivering()
        {
            bool driving = RouteStatusPolicy.GhostDriving(RouteStatus.WaitingForResources);
            Assert.True(driving);
            var badge = LogisticsDeliveryPresentation.ClassifyDeliveryBadge(
                ghostDriving: driving,
                lastOutcome: LogisticsDeliveryPresentation.DeliveryOutcome.None,
                completedCycles: 0,
                skippedCycles: 1);
            Assert.Equal(LogisticsDeliveryPresentation.DeliveryBadge.FlyingNotDelivering, badge);
            Assert.Equal("Flying, not delivering",
                LogisticsDeliveryPresentation.DeliveryBadgeLabel(badge));
        }

        // A Paused route (not ghost-driving) reads "Paused".
        [Fact]
        public void ClassifyDeliveryBadge_PausedRoute_IsPaused()
        {
            bool driving = RouteStatusPolicy.GhostDriving(RouteStatus.Paused);
            Assert.False(driving);
            var badge = LogisticsDeliveryPresentation.ClassifyDeliveryBadge(
                ghostDriving: driving,
                lastOutcome: LogisticsDeliveryPresentation.DeliveryOutcome.None,
                completedCycles: 4,
                skippedCycles: 0);
            Assert.Equal(LogisticsDeliveryPresentation.DeliveryBadge.Paused, badge);
        }

        [Theory]
        [InlineData((int)LogisticsDeliveryPresentation.DeliveryBadge.Delivering, "Delivering")]
        [InlineData((int)LogisticsDeliveryPresentation.DeliveryBadge.FlyingNotDelivering, "Flying, not delivering")]
        [InlineData((int)LogisticsDeliveryPresentation.DeliveryBadge.New, "New (not yet run)")]
        [InlineData((int)LogisticsDeliveryPresentation.DeliveryBadge.Paused, "Paused")]
        public void DeliveryBadgeLabel_MatchesExpected(int badgeOrdinal, string expected)
        {
            Assert.Equal(expected,
                LogisticsDeliveryPresentation.DeliveryBadgeLabel(
                    (LogisticsDeliveryPresentation.DeliveryBadge)badgeOrdinal));
        }

        // ------------------------------------------------------------------
        // H4: FormatDestinationDisplay + FormatEndpointCoords
        // ------------------------------------------------------------------

        // Resolved: the supplied vessel name is returned verbatim, coords ignored.
        [Fact]
        public void FormatDestinationDisplay_Resolved_ReturnsVesselName()
        {
            var ep = new RouteEndpoint
            {
                BodyName = "Mun",
                IsSurface = false,
                Latitude = 12.34,
                Longitude = 56.78
            };
            Assert.Equal("Munar Station",
                LogisticsDeliveryPresentation.FormatDestinationDisplay("Munar Station", ep));
        }

        // Fallback (null and empty name): the coords string, equal to FormatEndpointCoords.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void FormatDestinationDisplay_Unresolved_FallsBackToCoords(string name)
        {
            var ep = new RouteEndpoint
            {
                BodyName = "Mun",
                IsSurface = true,
                Latitude = 12.34,
                Longitude = 56.78
            };
            string expected = LogisticsDeliveryPresentation.FormatEndpointCoords(ep);
            Assert.Equal(expected, LogisticsDeliveryPresentation.FormatDestinationDisplay(name, ep));
            Assert.Equal("Mun (surface) 12.34,56.78", expected);
        }

        // Orbit situation label and F2 + InvariantCulture coords.
        [Fact]
        public void FormatEndpointCoords_OrbitSituation_InvariantF2()
        {
            CultureInfo prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var ep = new RouteEndpoint
                {
                    BodyName = "Kerbin",
                    IsSurface = false,
                    Latitude = -0.5,
                    Longitude = 100.125
                };
                string s = LogisticsDeliveryPresentation.FormatEndpointCoords(ep);
                Assert.Equal("Kerbin (orbit) -0.50,100.13", s);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prev;
            }
        }

        // Empty body name plus an unresolved vessel: dash.
        [Fact]
        public void FormatDestinationDisplay_EmptyBodyAndNoName_ReturnsDash()
        {
            var ep = new RouteEndpoint { BodyName = null };
            Assert.Equal("-", LogisticsDeliveryPresentation.FormatDestinationDisplay(null, ep));
            Assert.Equal("-", LogisticsDeliveryPresentation.FormatEndpointCoords(ep));
        }

        // ------------------------------------------------------------------
        // H5: FormatSourceRecordingDisplay
        // ------------------------------------------------------------------

        // Resolved name + tree + position: full form.
        [Fact]
        public void FormatSourceRecordingDisplay_NameTreeAndPosition_FullForm()
        {
            string s = LogisticsDeliveryPresentation.FormatSourceRecordingDisplay(
                "abcd1234", "Mun Fuel Run", "Munar Logistics", 3);
            Assert.Equal("Mun Fuel Run (rec 3 of tree 'Munar Logistics')", s);
        }

        // Resolved name + position, standalone (no tree): rec clause only.
        [Fact]
        public void FormatSourceRecordingDisplay_NameAndPositionNoTree_RecClauseOnly()
        {
            string s = LogisticsDeliveryPresentation.FormatSourceRecordingDisplay(
                "abcd1234", "Mun Fuel Run", null, 3);
            Assert.Equal("Mun Fuel Run (rec 3)", s);
        }

        // Resolved name + tree, unknown position: tree clause only.
        [Fact]
        public void FormatSourceRecordingDisplay_NameAndTreeUnknownPosition_TreeClauseOnly()
        {
            string s = LogisticsDeliveryPresentation.FormatSourceRecordingDisplay(
                "abcd1234", "Mun Fuel Run", "Munar Logistics", 0);
            Assert.Equal("Mun Fuel Run (tree 'Munar Logistics')", s);
        }

        // Resolved name only, no tree, unknown position: bare name.
        [Fact]
        public void FormatSourceRecordingDisplay_NameOnly_BareName()
        {
            string s = LogisticsDeliveryPresentation.FormatSourceRecordingDisplay(
                "abcd1234", "Mun Fuel Run", null, -1);
            Assert.Equal("Mun Fuel Run", s);
        }

        // Unresolved (null/empty name): the short id verbatim.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void FormatSourceRecordingDisplay_Unresolved_FallsBackToShortId(string name)
        {
            Assert.Equal("abcd1234",
                LogisticsDeliveryPresentation.FormatSourceRecordingDisplay("abcd1234", name, "Munar Logistics", 3));
        }

        // Unresolved with no short id either: the "<none>" sentinel.
        [Fact]
        public void FormatSourceRecordingDisplay_UnresolvedNoShortId_ShowsNone()
        {
            Assert.Equal("<none>",
                LogisticsDeliveryPresentation.FormatSourceRecordingDisplay(null, null, null, -1));
        }

        // The rec number is InvariantCulture: a large position carries no separator.
        [Fact]
        public void FormatSourceRecordingDisplay_PositionIsInvariant()
        {
            CultureInfo prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string s = LogisticsDeliveryPresentation.FormatSourceRecordingDisplay(
                    "abcd1234", "Run", "Tree", 1000);
                Assert.Equal("Run (rec 1000 of tree 'Tree')", s);
                Assert.DoesNotContain(".", s.Substring(s.IndexOf("rec ", System.StringComparison.Ordinal)));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prev;
            }
        }
    }
}
