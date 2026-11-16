using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Cells for <c>ListHandles kind=sites</c> (multiplayer T-07): the closed kind literal,
    /// the fixed sample-point table, the wire key sequence and formats, and the one Info
    /// line per fact a spec pins (RACE-0-stock-sites-baseline).
    /// </summary>
    public class TestCommandSitesReadbackTests
    {
        private static Dictionary<string, string> Map(IEnumerable<KeyValuePair<string, string>> payload)
        {
            var d = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> kv in payload)
                d[kv.Key] = kv.Value;
            return d;
        }

        private static LaunchSitePointRow Point(string site, string point, bool facility,
            string editor, double lat, double lon, double alt,
            SitePointSource src = SitePointSource.Cache)
            => new LaunchSitePointRow
            {
                SiteName = site,
                PointName = point,
                Facility = facility,
                Editor = editor,
                Source = src,
                Latitude = lat,
                Longitude = lon,
                Altitude = alt,
            };

        private static SitesReading StockLikeReading()
            => new SitesReading
            {
                BodyName = "Kerbin",
                SpaceCenterFound = true,
                SpaceCenterLatitude = -0.0912,
                SpaceCenterLongitude = -74.65,
                SpaceCenterAltitude = 66.25,
                RecoveryFound = true,
                RecoveryLatitude = -0.09,
                RecoveryLongitude = -74.64,
                LaunchSites = new List<LaunchSitePointRow>
                {
                    Point("Runway", "Runway", true, "SPH", -0.0486, -74.7247, 70.1),
                    Point("LaunchPad", "LaunchPad", true, "VAB", -0.0972, -74.5577, 72.4),
                },
                Samples = new List<TerrainSampleRow>
                {
                    new TerrainSampleRow { Name = "pad", Latitude = -0.0972, Longitude = -74.5575, Measured = true, Height = 72.42172950913664 },
                    new TerrainSampleRow { Name = "runwayWest", Latitude = -0.0502, Longitude = -74.73, Measured = false },
                },
            };

        // ----- the kind literal -----

        [Fact]
        public void ParseKind_accepts_sites_and_maps_it_to_its_own_member()
        {
            ListHandlesKind kind;
            string reason;
            Assert.True(TestCommandListHandles.ParseKind("sites", out kind, out reason));
            Assert.Equal(ListHandlesKind.Sites, kind);
            Assert.Null(reason);
            Assert.Equal("sites", TestCommandListHandles.KindToken(ListHandlesKind.Sites));
        }

        [Theory]
        [InlineData("Sites")]
        [InlineData("SITES")]
        [InlineData("site")]
        [InlineData(" sites")]
        public void ParseKind_sites_is_case_sensitive_and_exact(string raw)
        {
            ListHandlesKind kind;
            string reason;
            Assert.False(TestCommandListHandles.ParseKind(raw, out kind, out reason));
            Assert.Equal(TestCommandListHandles.KindArgInvalidReason, reason);
        }

        [Fact]
        public void ExpectDigest_on_sites_is_a_kind_mismatch()
        {
            string expected, reason;
            Assert.False(TestCommandListHandles.ParseExpectDigest(
                "811c9dc5", ListHandlesKind.Sites, out expected, out reason));
            Assert.Equal(TestCommandListHandles.ExpectDigestKindMismatchReason, reason);
        }

        // ----- the sample-point table -----

        [Fact]
        public void SamplePoints_are_the_pad_and_both_runway_ends_in_that_order()
        {
            SiteSamplePoint[] points = TestCommandSitesReadback.SamplePoints;
            Assert.Equal(new[] { "pad", "runwayWest", "runwayEast" }, points.Select(p => p.Name).ToArray());
            Assert.Equal(-0.0972, points[0].Latitude);
            Assert.Equal(-74.5575, points[0].Longitude);
            Assert.Equal(-0.0502, points[1].Latitude);
            Assert.Equal(-74.7300, points[1].Longitude);
            Assert.Equal(-0.0502, points[2].Latitude);
            Assert.Equal(-74.5000, points[2].Longitude);
        }

        [Fact]
        public void SamplePoints_pad_and_runway_threshold_are_the_stock_points_parsek_already_holds()
        {
            SiteSamplePoint[] points = TestCommandSitesReadback.SamplePoints;
            Assert.Equal(SpawnCollisionDetector.KscPadLatitude, points[0].Latitude);
            Assert.Equal(SpawnCollisionDetector.KscPadLongitude, points[0].Longitude);
            Assert.Equal(SpawnCollisionDetector.KscRunwayLatitude, points[1].Latitude);
            Assert.Equal(SpawnCollisionDetector.KscRunwayLongitude, points[1].Longitude);
        }

        [Fact]
        public void SamplePoints_runway_east_end_is_about_a_runway_length_east_on_the_same_latitude()
        {
            SiteSamplePoint west = TestCommandSitesReadback.SamplePoints[1];
            SiteSamplePoint east = TestCommandSitesReadback.SamplePoints[2];
            Assert.Equal(west.Latitude, east.Latitude);
            double metres = (east.Longitude - west.Longitude) * 600000.0 * System.Math.PI / 180.0;
            Assert.InRange(metres, 2300.0, 2500.0);
        }

        [Fact]
        public void SamplePoints_names_are_unique_and_log_safe()
        {
            var names = TestCommandSitesReadback.SamplePoints.Select(p => p.Name).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());
            Assert.All(names, n => Assert.Matches("^[A-Za-z]+$", n));
        }

        // ----- the payload -----

        [Fact]
        public void Payload_emits_the_documented_key_sequence()
        {
            List<KeyValuePair<string, string>> payload =
                TestCommandSitesReadback.BuildPayload(StockLikeReading());
            Assert.Equal(new[]
            {
                "kind", "count", "truncated", "body", "scLat", "scLon", "scAlt",
                "recoveryLat", "recoveryLon",
                "site0", "site0point", "site0kind", "site0editor", "site0lat", "site0lon", "site0alt", "site0src",
                "site1", "site1point", "site1kind", "site1editor", "site1lat", "site1lon", "site1alt", "site1src",
                "samples",
                "sample0", "sample0lat", "sample0lon", "sample0height",
                "sample1", "sample1lat", "sample1lon", "sample1height",
            }, payload.Select(kv => kv.Key).ToArray());
        }

        [Fact]
        public void Payload_values_use_fixed_invariant_formats_under_a_comma_culture()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Dictionary<string, string> m = Map(TestCommandSitesReadback.BuildPayload(StockLikeReading()));
                Assert.Equal("sites", m["kind"]);
                Assert.Equal("2", m["count"]);
                Assert.Equal("false", m["truncated"]);
                Assert.Equal("Kerbin", m["body"]);
                Assert.Equal("-0.0912000", m["scLat"]);
                Assert.Equal("-74.6500000", m["scLon"]);
                Assert.Equal("66.250", m["scAlt"]);
                Assert.Equal("-0.0900000", m["recoveryLat"]);
                Assert.Equal("-74.6400000", m["recoveryLon"]);
                Assert.Equal("72.422", m["sample0height"]);
                Assert.Equal("-74.5575000", m["sample0lon"]);
                Assert.Equal("2", m["samples"]);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void Payload_reports_none_for_every_value_it_could_not_read()
        {
            var r = new SitesReading
            {
                LaunchSites = new List<LaunchSitePointRow>
                {
                    Point("LaunchPad", "LaunchPad", true, null, 1, 2, 3, SitePointSource.None),
                },
                Samples = new List<TerrainSampleRow>
                {
                    new TerrainSampleRow { Name = "pad", Latitude = -0.0972, Longitude = -74.5575, Measured = false, Height = 12 },
                    new TerrainSampleRow { Name = "runwayWest", Latitude = -0.0502, Longitude = -74.73, Measured = true, Height = double.NaN },
                },
            };
            Dictionary<string, string> m = Map(TestCommandSitesReadback.BuildPayload(r));
            foreach (string key in new[] { "body", "scLat", "scLon", "scAlt", "recoveryLat", "recoveryLon",
                                           "site0editor", "site0lat", "site0lon", "site0alt", "site0src",
                                           "sample0height", "sample1height" })
                Assert.Equal("none", m[key]);
            Assert.Equal("-0.0972000", m["sample0lat"]);
        }

        [Fact]
        public void Payload_null_lists_read_as_empty()
        {
            Dictionary<string, string> m = Map(TestCommandSitesReadback.BuildPayload(default(SitesReading)));
            Assert.Equal("0", m["count"]);
            Assert.Equal("0", m["samples"]);
            Assert.False(m.ContainsKey("site0"));
        }

        [Fact]
        public void Payload_orders_facility_sites_first_then_alternates_by_ordinal_name()
        {
            var r = new SitesReading
            {
                LaunchSites = new List<LaunchSitePointRow>
                {
                    Point("Woomerang_Launch_Site", "a", false, "VAB", 0, 0, 0),
                    Point("Runway", "Runway", true, "SPH", 0, 0, 0),
                    Point("Desert_Launch_Site", "b", false, "VAB", 0, 0, 0),
                    Point("Desert_Launch_Site", "a", false, "VAB", 0, 0, 0),
                    Point("LaunchPad", "LaunchPad", true, "VAB", 0, 0, 0),
                },
            };
            Dictionary<string, string> m = Map(TestCommandSitesReadback.BuildPayload(r));
            Assert.Equal("LaunchPad", m["site0"]);
            Assert.Equal("facility", m["site0kind"]);
            Assert.Equal("Runway", m["site1"]);
            Assert.Equal("Desert_Launch_Site", m["site2"]);
            Assert.Equal("a", m["site2point"]);
            Assert.Equal("b", m["site3point"]);
            Assert.Equal("Woomerang_Launch_Site", m["site4"]);
            Assert.Equal("alternate", m["site4kind"]);
        }

        [Fact]
        public void Payload_caps_the_spawn_points_and_reports_the_true_total()
        {
            var rows = new List<LaunchSitePointRow>();
            for (int i = 0; i < TestCommandSitesReadback.MaxLaunchSitePoints + 3; i++)
                rows.Add(Point("Site" + i.ToString("D2", CultureInfo.InvariantCulture), "p", false, "VAB", 0, 0, 0));
            Dictionary<string, string> m = Map(TestCommandSitesReadback.BuildPayload(
                new SitesReading { LaunchSites = rows }));
            Assert.Equal("19", m["count"]);
            Assert.Equal("true", m["truncated"]);
            Assert.True(m.ContainsKey("site15"));
            Assert.False(m.ContainsKey("site16"));
        }

        // ----- the log lines -----

        [Fact]
        public void LogLines_write_one_line_per_fact_read_out_of_the_payload()
        {
            List<KeyValuePair<string, string>> payload =
                TestCommandSitesReadback.BuildPayload(StockLikeReading());
            List<string> lines = TestCommandSitesReadback.LogLines(payload, ListHandlesKind.Sites);
            Assert.Equal(new[]
            {
                "listhandles sites spaceCenter body=Kerbin lat=-0.0912000 lon=-74.6500000 alt=66.250",
                "listhandles sites recovery lat=-0.0900000 lon=-74.6400000",
                "listhandles sites launchSite index=0 name='LaunchPad' point='LaunchPad' kind=facility editor=VAB lat=-0.0972000 lon=-74.5577000 alt=72.400 src=cache",
                "listhandles sites launchSite index=1 name='Runway' point='Runway' kind=facility editor=SPH lat=-0.0486000 lon=-74.7247000 alt=70.100 src=cache",
                "listhandles sites terrain index=0 name=pad lat=-0.0972000 lon=-74.5575000 height=72.422",
                "listhandles sites terrain index=1 name=runwayWest lat=-0.0502000 lon=-74.7300000 height=none",
            }, lines.ToArray());
        }

        [Fact]
        public void LogLines_are_empty_for_every_other_family()
        {
            List<KeyValuePair<string, string>> payload =
                TestCommandSitesReadback.BuildPayload(StockLikeReading());
            foreach (ListHandlesKind kind in new[] { ListHandlesKind.Timeline, ListHandlesKind.Routes,
                                                     ListHandlesKind.Chains, ListHandlesKind.None })
                Assert.Empty(TestCommandSitesReadback.LogLines(payload, kind));
            Assert.Empty(TestCommandSitesReadback.LogLines(null, ListHandlesKind.Sites));
        }

        [Fact]
        public void Summary_line_reads_the_count_and_the_sites_tail_from_the_payload()
        {
            List<KeyValuePair<string, string>> payload =
                TestCommandSitesReadback.BuildPayload(StockLikeReading());
            Assert.Equal("2", TestCommandListHandles.CountFromPayload(payload, ListHandlesKind.Sites));
            Assert.Equal("false", TestCommandListHandles.TruncatedFromPayload(payload));
            Assert.Equal(" body=Kerbin samples=2",
                TestCommandListHandles.ChainsLogTail(payload, ListHandlesKind.Sites));
        }

        [Fact]
        public void The_transform_source_is_its_own_token()
        {
            Assert.Equal("cache", TestCommandSitesReadback.SourceToken(SitePointSource.Cache));
            Assert.Equal("transform", TestCommandSitesReadback.SourceToken(SitePointSource.Transform));
            Assert.Equal("none", TestCommandSitesReadback.SourceToken(SitePointSource.None));
        }
    }
}
