using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the map-geometry dump's writer and bounds
    /// (<see cref="MapGeometryJson"/>) and its per-ghost decisions
    /// (<see cref="MapGeometryDecisions"/>): decimation, every hard cap and its explicit
    /// truncation marker, the JSON shape through a strict parser, the NaN / Infinity
    /// omission rule, and invariant-culture numbers under a comma-decimal culture.
    /// </summary>
    public class MapGeometryJsonTests
    {
        private static double[] P(double x, double y, double z) => new[] { x, y, z };

        private static List<double[]> Line(int n, double offset = 0.0)
        {
            var list = new List<double[]>(n);
            for (int i = 0; i < n; i++) list.Add(P(i + offset, -i, i * 0.5));
            return list;
        }

        // ----- decimation -----

        [Fact]
        public void Decimate_KeepsEveryFinitePoint_WhenUnderTheCap()
        {
            var pts = Line(10);
            var outPts = MapGeometryJson.Decimate(pts, 512);
            Assert.Equal(10, outPts.Count);
            Assert.Same(pts[0], outPts[0]);
            Assert.Same(pts[9], outPts[9]);
        }

        [Fact]
        public void Decimate_DropsNonFinitePoints_BeforeCounting()
        {
            var pts = new List<double[]>
            {
                P(1, 2, 3), P(double.NaN, 0, 0), P(0, double.PositiveInfinity, 0), null,
                new[] { 1.0, 2.0 }, P(4, 5, 6),
            };
            var outPts = MapGeometryJson.Decimate(pts, 512);
            Assert.Equal(2, outPts.Count);
            Assert.Equal(1.0, outPts[0][0]);
            Assert.Equal(4.0, outPts[1][0]);
        }

        [Theory]
        [InlineData(513, 512)]
        [InlineData(2000, 512)]
        [InlineData(100000, 512)]
        [InlineData(10, 3)]
        public void Decimate_HitsTheCapExactly_KeepingFirstAndLast_InOrder(int n, int max)
        {
            var pts = Line(n);
            var outPts = MapGeometryJson.Decimate(pts, max);
            Assert.Equal(max, outPts.Count);
            Assert.Same(pts[0], outPts[0]);
            Assert.Same(pts[n - 1], outPts[max - 1]);
            for (int i = 1; i < outPts.Count; i++)
                Assert.True(outPts[i][0] > outPts[i - 1][0], "decimated indices must strictly increase");
        }

        [Fact]
        public void Decimate_EvenSpread_IsRoundedIndexMapping()
        {
            // n=10, max=4: indices round(i*9/3) = 0, 3, 6, 9.
            var outPts = MapGeometryJson.Decimate(Line(10), 4);
            Assert.Equal(new[] { 0.0, 3.0, 6.0, 9.0 }, new[] { outPts[0][0], outPts[1][0], outPts[2][0], outPts[3][0] });
        }

        [Fact]
        public void Decimate_DegenerateCaps()
        {
            Assert.Empty(MapGeometryJson.Decimate(Line(5), 0));
            var one = MapGeometryJson.Decimate(Line(5), 1);
            Assert.Single(one);
            Assert.Equal(0.0, one[0][0]);
            Assert.Empty(MapGeometryJson.Decimate(null, 512));
        }

        // ----- caps -----

        private static MapGeometryDocument DocWithGhosts(int ghosts, int linesPerGhost, int pointsPerLine)
        {
            var doc = new MapGeometryDocument { Label = "cap", Scene = "TRACKSTATION" };
            doc.Bodies.Add(new MapGeometryBody { Name = "Kerbin" });
            for (int g = 0; g < ghosts; g++)
            {
                var ghost = new MapGeometryGhost { RecordingId = "rec" + g, Pid = (uint)(g + 1) };
                for (int l = 0; l < linesPerGhost; l++)
                    ghost.Lines.Add(new MapGeometryLine
                    {
                        Kind = "polyline", Body = "Kerbin", Active = true, Points = Line(pointsPerLine),
                    });
                doc.Ghosts.Add(ghost);
            }
            return doc;
        }

        [Fact]
        public void ApplyCaps_UnderEveryCap_IsNotTruncated_AndCountsTheDrawnGeometry()
        {
            var doc = DocWithGhosts(3, 2, 10);
            doc.Routes.Add(new MapGeometryRoute
            {
                RouteId = "r1",
                Legs = { new MapGeometryRouteLeg { Body = "Kerbin", RecordingId = "m", Points = Line(4) } },
            });
            doc.Bodies[0].Orbit = Line(64);
            var c = MapGeometryJson.ApplyCaps(doc);
            Assert.False(c.Truncated);
            Assert.Equal(3, c.Ghosts);
            Assert.Equal(7, c.Lines);              // 6 ghost lines + 1 route leg
            Assert.Equal(64, c.Points);            // 60 ghost points + 4 leg points; rings not counted
        }

        [Fact]
        public void ApplyCaps_DecimatesEveryLine_ToTheResolutionLimit_WithoutTruncating()
        {
            var doc = DocWithGhosts(1, 1, 5000);
            doc.Ghosts[0].Lines[0].Reference = Line(5000);
            var c = MapGeometryJson.ApplyCaps(doc);
            Assert.False(c.Truncated);
            Assert.Equal(MapGeometryJson.MaxPointsPerLine, doc.Ghosts[0].Lines[0].Points.Count);
            Assert.Equal(MapGeometryJson.MaxPointsPerLine, doc.Ghosts[0].Lines[0].Reference.Count);
        }

        [Fact]
        public void ApplyCaps_GhostCap_DropsWholeGhosts_AndSaysSo()
        {
            var doc = DocWithGhosts(MapGeometryJson.MaxGhosts + 3, 1, 2);
            var c = MapGeometryJson.ApplyCaps(doc);
            Assert.True(c.Truncated);
            Assert.Equal(MapGeometryJson.MaxGhosts, c.Ghosts);
            Assert.Equal(3, c.DroppedGhosts);
            Assert.Equal(3, c.DroppedLines);
            Assert.Equal(MapGeometryJson.MaxGhosts, doc.Ghosts.Count);
        }

        [Fact]
        public void ApplyCaps_LineCap_DropsTheExcessLines_AndSaysSo()
        {
            var doc = DocWithGhosts(MapGeometryJson.MaxLines / 8 + 1, 8, 1);
            var c = MapGeometryJson.ApplyCaps(doc);
            Assert.True(c.Truncated);
            Assert.Equal(MapGeometryJson.MaxLines, c.Lines);
            Assert.Equal(8, c.DroppedLines);
        }

        [Fact]
        public void ApplyCaps_PointCap_DropsWholeLines_NeverSplitsOne()
        {
            // 300 ghosts x 2 full lines = 600 lines of 512 points; the point cap holds 512 of them.
            int full = (int)(MapGeometryJson.MaxPoints / MapGeometryJson.MaxPointsPerLine);
            var doc = DocWithGhosts(300, 2, MapGeometryJson.MaxPointsPerLine);
            var c = MapGeometryJson.ApplyCaps(doc);
            Assert.True(c.Truncated);
            Assert.Equal(MapGeometryJson.MaxPoints, c.Points);
            Assert.Equal(full, c.Lines);
            Assert.Equal(600 - full, c.DroppedLines);
            Assert.Equal(300, c.Ghosts); // the ghosts stay; their lines went
            Assert.Empty(doc.Ghosts[299].Lines);
            Assert.Equal(0, c.DroppedGhosts);
        }

        [Fact]
        public void ApplyCaps_RouteAndLegCaps_AreExplicit()
        {
            var doc = new MapGeometryDocument();
            doc.Bodies.Add(new MapGeometryBody { Name = "Kerbin" });
            for (int r = 0; r < MapGeometryJson.MaxRoutes + 1; r++)
                doc.Routes.Add(new MapGeometryRoute { RouteId = "r" + r });
            for (int l = 0; l < MapGeometryJson.MaxLegsPerRoute + 2; l++)
                doc.Routes[0].Legs.Add(new MapGeometryRouteLeg { Body = "Kerbin", Points = Line(2) });
            var c = MapGeometryJson.ApplyCaps(doc);
            Assert.True(c.Truncated);
            Assert.Equal(1, c.DroppedRoutes);
            Assert.Equal(2, c.DroppedLegs);
            Assert.Equal(MapGeometryJson.MaxLegsPerRoute, doc.Routes[0].Legs.Count);
        }

        [Fact]
        public void ApplyCaps_BodyCap_IsExplicit()
        {
            var doc = new MapGeometryDocument();
            for (int b = 0; b < MapGeometryJson.MaxBodies + 1; b++)
                doc.Bodies.Add(new MapGeometryBody { Name = "b" + b });
            var c = MapGeometryJson.ApplyCaps(doc);
            Assert.True(c.Truncated);
            Assert.Equal(1, c.DroppedBodies);
        }

        // ----- the writer -----

        private static MapGeometryDocument RichDocument()
        {
            var doc = new MapGeometryDocument
            {
                Label = "ts-kerbin",
                Scene = "TRACKSTATION",
                Ut = 123456.789,
                Frame = 4242,
                WarpRate = 50.0,
                Camera = new MapGeometryCamera
                {
                    Target = "Kerbin", PitchDeg = 45.5, HeadingDeg = -12.25, Distance = 1.5e7,
                    Forward = P(0, -1, 0), Up = P(0, 0, 1), FovDeg = 60,
                },
                AxisUp = P(0, 1, 0),
                AxisRight = P(1, 0, 0),
                Root = "Sun",
            };
            doc.Bodies.Add(new MapGeometryBody
            {
                Name = "Sun", Radius = 261600000, Soi = double.PositiveInfinity, Pos = P(0, 0, 0),
            });
            doc.Bodies.Add(new MapGeometryBody
            {
                Name = "Kerbin", Parent = "Sun", Radius = 600000, Soi = 84159286.0,
                Pos = P(13599840256, 0, 1.5), Orbit = Line(64),
            });
            doc.Bodies.Add(new MapGeometryBody { Name = "Mun", Parent = "Kerbin", Radius = 200000 });
            var ghost = new MapGeometryGhost
            {
                RecordingId = "abc\"quote", VesselName = "Probe \u00e9", Pid = 4000000001u,
                CommittedIndex = 3, SampleUT = 100.5, LoopCycle = 2, Treatment = "StockConic",
                Body = "Kerbin",
                Icon = new MapGeometryIcon { Drawn = true, Pos = P(700000, 1, -2), Source = "proto" },
                MarkerDecision = new MapGeometryMarkerDecision { Draw = false, IconSuppressed = true },
            };
            ghost.Lines.Add(new MapGeometryLine
            {
                Kind = "orbit", Body = "Kerbin", Active = true, Points = Line(5), Ut0 = 10, Ut1 = 20,
                Conic = new MapGeometryConic
                {
                    Sma = 700000, Ecc = 0.01, IncDeg = 1, LanDeg = 2, ArgPeDeg = 3,
                    MeanAnomalyAtEpoch = 0.5, Epoch = 99,
                },
            });
            ghost.Lines.Add(new MapGeometryLine
            {
                Kind = "polyline", Body = "Kerbin", Active = false,
                Reference = Line(3), ReferenceKind = "recorded",
            });
            doc.Ghosts.Add(ghost);
            doc.Ghosts.Add(new MapGeometryGhost { RecordingId = "pidless", Treatment = "DriverOnly" });
            doc.Routes.Add(new MapGeometryRoute
            {
                RouteId = "route-1", RouteName = "Mun run",
                Legs = { new MapGeometryRouteLeg { Body = "Mun", RecordingId = "m1", Points = Line(2) } },
            });
            return doc;
        }

        [Fact]
        public void Write_IsAStrictlyParseableDocument_InTheContractShape()
        {
            string json = MapGeometryJson.Write(RichDocument(), out MapGeometryCounts counts);
            var root = (Dictionary<string, object>)MiniJson.ParseValue(json);

            Assert.Equal(MapGeometryJson.SchemaId, root["schema"]);
            Assert.Equal("parsek-map-geometry/1", root["schema"]);
            Assert.Equal("ts-kerbin", root["label"]);
            Assert.Equal("TRACKSTATION", root["scene"]);
            Assert.Equal(123456.789, root["ut"]);
            Assert.Equal(4242.0, root["frame"]);
            Assert.Equal(50.0, root["warpRate"]);

            var cam = (Dictionary<string, object>)root["camera"];
            Assert.Equal("Kerbin", cam["target"]);
            Assert.Equal(45.5, cam["pitchDeg"]);
            Assert.Equal(-12.25, cam["headingDeg"]);
            Assert.Equal(1.5e7, cam["distance"]);
            Assert.Equal(new List<object> { 0.0, -1.0, 0.0 }, (List<object>)cam["forward"]);
            Assert.Equal(new List<object> { 0.0, 0.0, 1.0 }, (List<object>)cam["up"]);
            Assert.Equal(60.0, cam["fovDeg"]);

            var axes = (Dictionary<string, object>)root["axes"];
            Assert.Equal(3, ((List<object>)axes["up"]).Count);
            Assert.Equal("Sun", root["root"]);

            var bodies = (List<object>)root["bodies"];
            Assert.Equal(3, bodies.Count);
            var sun = (Dictionary<string, object>)bodies[0];
            Assert.False(sun.ContainsKey("parent"));
            Assert.False(sun.ContainsKey("soi"));    // infinite SOI is omitted, never written
            Assert.False(sun.ContainsKey("orbit"));
            var kerbin = (Dictionary<string, object>)bodies[1];
            Assert.Equal("Sun", kerbin["parent"]);
            Assert.Equal(64, ((List<object>)kerbin["orbit"]).Count);
            Assert.Equal(13599840256.0, ((List<object>)kerbin["pos"])[0]);

            var ghosts = (List<object>)root["ghosts"];
            var g0 = (Dictionary<string, object>)ghosts[0];
            Assert.Equal("abc\"quote", g0["recordingId"]);
            Assert.Equal("Probe \u00e9", g0["vesselName"]);
            Assert.Equal(4000000001.0, g0["pid"]);
            Assert.Equal(3.0, g0["committedIndex"]);
            Assert.Equal(100.5, g0["sampleUT"]);
            Assert.Equal(2.0, g0["loopCycle"]);
            Assert.Equal("StockConic", g0["treatment"]);
            Assert.Equal("Kerbin", g0["body"]);
            var icon = (Dictionary<string, object>)g0["icon"];
            Assert.Equal(true, icon["drawn"]);
            Assert.Equal("proto", icon["source"]);
            var decision = (Dictionary<string, object>)g0["markerDecision"];
            Assert.Equal(true, decision["iconSuppressed"]);
            var lines = (List<object>)g0["lines"];
            var orbit = (Dictionary<string, object>)lines[0];
            Assert.Equal("orbit", orbit["kind"]);
            Assert.Equal(5, ((List<object>)orbit["points"]).Count);
            Assert.Equal(new List<object> { 10.0, 20.0 }, (List<object>)orbit["ut"]);
            Assert.Equal(700000.0, ((Dictionary<string, object>)orbit["conic"])["sma"]);
            var poly = (Dictionary<string, object>)lines[1];
            Assert.Equal(false, poly["active"]);
            Assert.Empty((List<object>)poly["points"]);
            Assert.False(poly.ContainsKey("ut"));
            Assert.False(poly.ContainsKey("conic"));
            Assert.Equal("recorded", poly["referenceKind"]);

            var g1 = (Dictionary<string, object>)ghosts[1];
            Assert.Equal(0.0, g1["pid"]);
            Assert.Equal(-1.0, g1["committedIndex"]);
            Assert.False(g1.ContainsKey("sampleUT"));
            Assert.False(g1.ContainsKey("loopCycle"));
            Assert.False(g1.ContainsKey("icon"));

            var routes = (List<object>)root["routeLines"];
            var r0 = (Dictionary<string, object>)routes[0];
            Assert.Equal("Mun run", r0["routeName"]);
            var leg = (Dictionary<string, object>)((List<object>)r0["legs"])[0];
            Assert.Equal("m1", leg["recordingId"]);

            var c = (Dictionary<string, object>)root["counts"];
            Assert.Equal(2.0, c["ghosts"]);
            Assert.Equal(3.0, c["lines"]);
            Assert.Equal(7.0, c["points"]);         // 5 orbit + 0 inactive polyline + 2 leg
            Assert.Equal(false, c["truncated"]);
            Assert.Equal(0.0, c["unknownBodyItems"]);
            Assert.False(c.ContainsKey("dropped"));
            Assert.Equal(7, counts.Points);
        }

        [Fact]
        public void Write_BridgeKindClippedFlagAndNoIconWorld()
        {
            Assert.Equal("bridge", MapGeometryDecisions.KindBridge);
            var doc = new MapGeometryDocument { Label = "b" };
            doc.Bodies.Add(new MapGeometryBody { Name = "Kerbin" });
            var ghost = new MapGeometryGhost
            {
                RecordingId = "r", Body = "Kerbin",
                Icon = new MapGeometryIcon { Drawn = true, Source = "marker", Pos = P(1, 2, 3), World = P(9, 9, 9) },
            };
            ghost.Lines.Add(new MapGeometryLine
            {
                Kind = MapGeometryDecisions.KindOrbit, Body = "Kerbin", Active = true, Points = Line(3),
                Ut0 = 1, Ut1 = 2, Clipped = true,
            });
            ghost.Lines.Add(new MapGeometryLine
            {
                Kind = MapGeometryDecisions.KindOrbit, Body = "Kerbin", Active = false,
            });
            ghost.Lines.Add(new MapGeometryLine
            {
                Kind = MapGeometryDecisions.KindBridge, Body = "Kerbin", Active = true, Points = Line(61),
            });
            doc.Ghosts.Add(ghost);

            string json = MapGeometryJson.Write(doc, out MapGeometryCounts counts);
            var root = (Dictionary<string, object>)MiniJson.ParseValue(json);
            var g = (Dictionary<string, object>)((List<object>)root["ghosts"])[0];
            var icon = (Dictionary<string, object>)g["icon"];
            Assert.Equal(new[] { "drawn", "pos", "source" }, new SortedSet<string>(icon.Keys));
            Assert.Equal(new List<object> { 1.0, 2.0, 3.0 }, (List<object>)icon["pos"]);
            var lines = (List<object>)g["lines"];
            var clipped = (Dictionary<string, object>)lines[0];
            Assert.Equal(true, clipped["clipped"]);
            Assert.False(((Dictionary<string, object>)lines[1]).ContainsKey("clipped"));
            Assert.Equal("bridge", ((Dictionary<string, object>)lines[2])["kind"]);
            Assert.Equal(61, ((List<object>)((Dictionary<string, object>)lines[2])["points"]).Count);
            Assert.Equal(3, counts.Lines);
            Assert.Equal(64, counts.Points);
        }

        [Fact]
        public void Write_NeverEmitsNaNOrInfinity_TheFieldOrPointIsOmitted()
        {
            var doc = new MapGeometryDocument
            {
                Label = "nan", Ut = double.NaN, WarpRate = double.PositiveInfinity,
                Camera = new MapGeometryCamera { Target = "Kerbin", PitchDeg = double.NaN, Distance = 5 },
                AxisUp = P(double.NaN, 1, 0),
            };
            doc.Bodies.Add(new MapGeometryBody { Name = "Mun" });
            var ghost = new MapGeometryGhost
            {
                RecordingId = "r", SampleUT = double.NegativeInfinity,
                Icon = new MapGeometryIcon { Drawn = false, Pos = P(0, double.NaN, 0) },
            };
            ghost.Lines.Add(new MapGeometryLine
            {
                Kind = "tail", Body = "Mun", Active = true,
                Points = new List<double[]> { P(1, 1, 1), P(double.NaN, 2, 2), P(3, 3, 3) },
                Ut0 = 1, Ut1 = double.NaN,
                Conic = new MapGeometryConic { Sma = double.NaN, Ecc = 0.5 },
            });
            doc.Ghosts.Add(ghost);

            string json = MapGeometryJson.Write(doc, out MapGeometryCounts counts);
            Assert.DoesNotContain("NaN", json);
            Assert.DoesNotContain("Infinity", json);
            var root = (Dictionary<string, object>)MiniJson.ParseValue(json);
            Assert.False(root.ContainsKey("ut"));
            Assert.False(root.ContainsKey("warpRate"));
            Assert.False(root.ContainsKey("axes"));
            var cam = (Dictionary<string, object>)root["camera"];
            Assert.False(cam.ContainsKey("pitchDeg"));
            Assert.Equal(5.0, cam["distance"]);
            var g = (Dictionary<string, object>)((List<object>)root["ghosts"])[0];
            Assert.False(g.ContainsKey("sampleUT"));
            Assert.False(((Dictionary<string, object>)g["icon"]).ContainsKey("pos"));
            var line = (Dictionary<string, object>)((List<object>)g["lines"])[0];
            Assert.Equal(2, ((List<object>)line["points"]).Count);
            Assert.False(line.ContainsKey("ut"));     // half a range is not a range
            var conic = (Dictionary<string, object>)line["conic"];
            Assert.False(conic.ContainsKey("sma"));
            Assert.Equal(0.5, conic["ecc"]);
            Assert.Equal(2, counts.Points);
        }

        [Fact]
        public void Write_TruncatedDocument_CarriesTheMarkerAndTheDroppedTotals()
        {
            var doc = DocWithGhosts(MapGeometryJson.MaxGhosts + 1, 1, 1);
            string json = MapGeometryJson.Write(doc, out MapGeometryCounts counts);
            var c = (Dictionary<string, object>)((Dictionary<string, object>)MiniJson.ParseValue(json))["counts"];
            Assert.Equal(true, c["truncated"]);
            var dropped = (Dictionary<string, object>)c["dropped"];
            Assert.Equal(1.0, dropped["ghosts"]);
            Assert.Equal(1.0, dropped["lines"]);
            Assert.True(counts.Truncated);
        }

        [Fact]
        public void Write_EmptyDocument_StillParses()
        {
            string json = MapGeometryJson.Write(new MapGeometryDocument(), out _);
            var root = (Dictionary<string, object>)MiniJson.ParseValue(json);
            Assert.Empty((List<object>)root["bodies"]);
            Assert.Empty((List<object>)root["ghosts"]);
            Assert.Empty((List<object>)root["routeLines"]);
        }

        [Fact]
        public void Write_NullEntries_AreNeitherWrittenNorCounted_AndTheDocumentStillParses()
        {
            var doc = new MapGeometryDocument();
            doc.Bodies.Add(null);
            doc.Bodies.Add(new MapGeometryBody { Name = "Kerbin" });
            doc.Ghosts.Add(null);
            doc.Ghosts.Add(new MapGeometryGhost { RecordingId = "r", Body = "Kerbin" });
            doc.Routes.Add(null);
            string json = MapGeometryJson.Write(doc, out MapGeometryCounts counts);
            var root = (Dictionary<string, object>)MiniJson.ParseValue(json);
            Assert.Single((List<object>)root["bodies"]);
            Assert.Single((List<object>)root["ghosts"]);
            Assert.Empty((List<object>)root["routeLines"]);
            Assert.Equal(1, counts.Ghosts);
            Assert.False(counts.Truncated);
        }

        [Fact]
        public void Write_NumbersAreInvariant_UnderACommaDecimalCulture()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var doc = new MapGeometryDocument { Label = "de", Ut = 1234.5, WarpRate = 0.25 };
                doc.Bodies.Add(new MapGeometryBody { Name = "Kerbin", Radius = 600000.125, Pos = P(1.5, -2.25, 3e-7) });
                string json = MapGeometryJson.Write(doc, out _);
                Assert.Contains("\"ut\": 1234.5", json);
                Assert.Contains("\"warpRate\": 0.25", json);
                Assert.Contains("[1.5,-2.25,3E-07]", json);
                Assert.DoesNotContain("1234,5", json);
                var root = (Dictionary<string, object>)MiniJson.ParseValue(json);
                Assert.Equal(1234.5, root["ut"]);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void Write_RoundTripsADoubleExactly()
        {
            double v = 0.1 + 0.2; // 0.30000000000000004: "R" keeps every bit
            var doc = new MapGeometryDocument { Ut = v };
            var root = (Dictionary<string, object>)MiniJson.ParseValue(MapGeometryJson.Write(doc, out _));
            Assert.Equal(v, (double)root["ut"]);
        }

        // ----- every named body is listed; lines in time order -----

        [Fact]
        public void ApplyCaps_SkipsAndCountsEveryItemNamingAnUnlistedBody()
        {
            var doc = new MapGeometryDocument();
            doc.Bodies.Add(new MapGeometryBody { Name = "Kerbin" });
            var ghost = new MapGeometryGhost
            {
                RecordingId = "r", Body = "Jool",
                Icon = new MapGeometryIcon { Drawn = true, Pos = P(1, 2, 3), Source = "marker" },
            };
            ghost.Lines.Add(new MapGeometryLine { Kind = "orbit", Body = "Kerbin", Active = true, Points = Line(2) });
            ghost.Lines.Add(new MapGeometryLine { Kind = "polyline", Body = "Eve", Active = true, Points = Line(2) });
            ghost.Lines.Add(new MapGeometryLine { Kind = "tail", Body = null, Active = true, Points = Line(2) });
            doc.Ghosts.Add(ghost);
            doc.Routes.Add(new MapGeometryRoute
            {
                RouteId = "x",
                Legs =
                {
                    new MapGeometryRouteLeg { Body = "Kerbin", Points = Line(2) },
                    new MapGeometryRouteLeg { Body = "Dres", Points = Line(2) },
                },
            });

            string json = MapGeometryJson.Write(doc, out MapGeometryCounts counts);
            Assert.Equal(4, counts.UnknownBodyItems);   // ghost body, 2 lines, 1 leg
            Assert.False(counts.Truncated);
            Assert.DoesNotContain("Jool", json);
            Assert.DoesNotContain("Eve", json);
            Assert.DoesNotContain("Dres", json);
            var root = (Dictionary<string, object>)MiniJson.ParseValue(json);
            var g = (Dictionary<string, object>)((List<object>)root["ghosts"])[0];
            Assert.False(g.ContainsKey("body"));
            var icon = (Dictionary<string, object>)g["icon"];
            Assert.Equal(true, icon["drawn"]);
            Assert.False(icon.ContainsKey("pos"));      // relative to a body we cannot name
            Assert.Single((List<object>)g["lines"]);
            Assert.Equal(4.0, ((Dictionary<string, object>)root["counts"])["unknownBodyItems"]);
        }

        [Fact]
        public void OrderLines_TimedByStartThenEnd_ThenUntimedInCaptureOrder_Stable()
        {
            var a = new MapGeometryLine { Kind = "orbit", Body = "Kerbin" };                 // untimed
            var b = new MapGeometryLine { Kind = "polyline", Body = "Kerbin", Ut0 = 50, Ut1 = 60 };
            var c = new MapGeometryLine { Kind = "polyline", Body = "Kerbin", Ut0 = 10, Ut1 = 20 };
            var d = new MapGeometryLine { Kind = "tail", Body = "Kerbin", Ut0 = 10, Ut1 = 15 };
            var e = new MapGeometryLine { Kind = "tail", Body = "Kerbin", Ut0 = 50, Ut1 = 60 };
            var f = new MapGeometryLine { Kind = "tail", Body = "Kerbin", Ut0 = 5, Ut1 = double.NaN }; // untimed
            var ordered = MapGeometryDecisions.OrderLines(new List<MapGeometryLine> { a, b, c, d, e, f });
            Assert.Equal(new[] { d, c, b, e, a, f }, ordered.ToArray());
        }

        // ----- decisions -----

        [Theory]
        [InlineData(true, true, true, "StockConic", false, "StockConic")]
        [InlineData(true, true, true, "TracedPath", true, "TracedPath")]
        [InlineData(true, true, false, "None", true, "SuppressedMarker")]
        [InlineData(true, false, false, null, true, "SuppressedMarker")]
        [InlineData(true, true, false, "None", false, "None")]
        [InlineData(true, false, false, null, false, "NoIntent")]
        [InlineData(false, false, false, null, false, "DriverOnly")]
        public void TreatmentToken_Precedence(
            bool hasPid, bool hasIntent, bool visible, string treatment, bool suppressed, string expected)
        {
            Assert.Equal(expected, MapGeometryDecisions.TreatmentToken(hasPid, hasIntent, visible, treatment, suppressed));
        }

        [Theory]
        [InlineData("rec-1", "rec-1", null)]
        [InlineData("rec-1#3", "rec-1", 3)]
        [InlineData("rec#weird#12", "rec#weird", 12)]
        [InlineData("rec#x", "rec#x", null)]
        [InlineData("rec#", "rec#", null)]
        public void TryParseMarkerKey_SplitsTheCycle(string key, string recId, int? cycle)
        {
            Assert.True(MapGeometryDecisions.TryParseMarkerKey(key, out string r, out int? c));
            Assert.Equal(recId, r);
            Assert.Equal(cycle, c);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("preview")]
        public void TryParseMarkerKey_RefusesNonRecordingKeys(string key)
        {
            Assert.False(MapGeometryDecisions.TryParseMarkerKey(key, out _, out _));
        }

        [Fact]
        public void MarkerKeyFor_MirrorsTheParse()
        {
            Assert.Equal("r#4", MapGeometryDecisions.MarkerKeyFor("r", 4));
            Assert.Equal("r", MapGeometryDecisions.MarkerKeyFor("r", null));
            Assert.Null(MapGeometryDecisions.MarkerKeyFor(null, 1));
            Assert.True(MapGeometryDecisions.TryParseMarkerKey(MapGeometryDecisions.MarkerKeyFor("r", 4), out string id, out int? c));
            Assert.Equal("r", id);
            Assert.Equal(4, c);
        }

        [Theory]
        [InlineData(10, 0, 9, true, 0, 9)]
        [InlineData(10, 2, 5, true, 2, 5)]
        [InlineData(10, -3, 50, true, 0, 9)]
        [InlineData(10, 6, 5, false, 6, 5)]
        [InlineData(0, 0, 0, false, 0, -1)]
        public void TryClampDrawRange(int count, int ds, int de, bool ok, int s, int e)
        {
            Assert.Equal(ok, MapGeometryDecisions.TryClampDrawRange(count, ds, de, out int start, out int end));
            Assert.Equal(s, start);
            Assert.Equal(e, end);
        }

        // An inactive line is reported (the sweep's window) but NEVER as drawn: only an active
        // mesh carries points (review finding 3: an inactive line was written with its stale
        // vertices).
        [Theory]
        [InlineData(true, 0, 100, "Drawn")]
        [InlineData(true, 100, 100, "Drawn")]
        [InlineData(false, 100, 100, "Inactive")]
        [InlineData(false, 99, 100, "Inactive")]
        [InlineData(false, 98, 100, "Skip")]
        public void ClassifyCachedLine(bool active, int last, int frame, string expected)
        {
            Assert.Equal(expected, MapGeometryDecisions.ClassifyCachedLine(active, last, frame).ToString());
        }

        [Fact]
        public void TrySelectOrbitLineWindow_PrefersTheClipDrawnThisFrame()
        {
            Assert.True(MapGeometryDecisions.TrySelectOrbitLineWindow(
                true, 10, 20, true, 5, 50, out double s, out double e, out bool fromClip));
            Assert.Equal(10, s);
            Assert.Equal(20, e);
            Assert.True(fromClip);

            // Stock drew the line (a full period or more): the visible window, not a clip.
            Assert.True(MapGeometryDecisions.TrySelectOrbitLineWindow(
                false, 10, 20, true, 5, 50, out s, out e, out fromClip));
            Assert.Equal(5, s);
            Assert.Equal(50, e);
            Assert.False(fromClip);

            Assert.False(MapGeometryDecisions.TrySelectOrbitLineWindow(
                false, 10, 20, false, 5, 50, out _, out _, out fromClip));
            Assert.False(fromClip);
        }

        [Fact]
        public void OrbitLineUt_IsTheDrawnWindowMinusTheShift()
        {
            // The live-frame window the arc clip drew, mapped back to recording time the way the
            // clip maps it (effUT = liveUT - shift), whichever drive mode the clip used.
            Assert.True(MapGeometryDecisions.TrySelectOrbitLineWindow(
                true, 5000, 5600, true, 4000, 9000, out double s, out double e, out _));
            Assert.True(MapGeometryDecisions.TryRecordingTimeRange(s, e, 3000, out double ut0, out double ut1));
            Assert.Equal(2000, ut0);
            Assert.Equal(2600, ut1);
        }

        private static MapGeometryDecisions.SoiCandidate Soi(string name, double x, double soi)
            => new MapGeometryDecisions.SoiCandidate { Name = name, Centre = new[] { x, 0, 0 }, Soi = soi };

        [Fact]
        public void ContainingSoiBody_PicksTheInnermostSoiThatContainsThePoint()
        {
            var bodies = new List<MapGeometryDecisions.SoiCandidate>
            {
                Soi("Sun", 0, double.PositiveInfinity),
                Soi("Kerbin", 1e10, 8.4e7),
                Soi("Mun", 1e10 + 1.2e7, 2.4e6),
            };
            Assert.Equal("Mun", MapGeometryDecisions.ContainingSoiBody(new[] { 1e10 + 1.2e7 + 1e5, 0, 0 }, bodies));
            Assert.Equal("Kerbin", MapGeometryDecisions.ContainingSoiBody(new[] { 1e10 + 1e6, 0, 0 }, bodies));
            // Outside every finite SOI: the root (unbounded SOI).
            Assert.Equal("Sun", MapGeometryDecisions.ContainingSoiBody(new[] { 5e9, 0, 0 }, bodies));
            // On the boundary counts as inside.
            Assert.Equal("Kerbin", MapGeometryDecisions.ContainingSoiBody(new[] { 1e10 - 8.4e7, 0, 0 }, bodies));
        }

        [Fact]
        public void ContainingSoiBody_RefusesWhatItCannotPlace()
        {
            var bodies = new List<MapGeometryDecisions.SoiCandidate> { Soi("Kerbin", 0, 100) };
            Assert.Null(MapGeometryDecisions.ContainingSoiBody(new[] { double.NaN, 0, 0 }, bodies));
            Assert.Null(MapGeometryDecisions.ContainingSoiBody(new[] { 1000.0, 0, 0 }, bodies)); // no root listed
            Assert.Null(MapGeometryDecisions.ContainingSoiBody(new[] { 0.0, 0, 0 }, null));
            // A zero / negative / non-finite-centre candidate is ignored.
            var odd = new List<MapGeometryDecisions.SoiCandidate>
            {
                Soi("Zero", 0, 0),
                new MapGeometryDecisions.SoiCandidate { Name = "NoCentre", Centre = null, Soi = 1e9 },
                Soi("Root", 0, double.PositiveInfinity),
            };
            Assert.Equal("Root", MapGeometryDecisions.ContainingSoiBody(new[] { 0.0, 0, 0 }, odd));
        }

        [Fact]
        public void MinDistanceToPolyline_MeasuresSegmentsNotOnlyVertices()
        {
            var line = new List<double[]> { new double[] { 0, 0, 0 }, new double[] { 100, 0, 0 } };
            // Above the middle of the segment: 10 from the segment, ~51 from either vertex.
            Assert.Equal(10.0, MapGeometryDecisions.MinDistanceToPolyline(new double[] { 50, 10, 0 }, line).Value, 9);
            // Past the end: distance to the end vertex.
            Assert.Equal(5.0, MapGeometryDecisions.MinDistanceToPolyline(new double[] { 105, 0, 0 }, line).Value, 9);
            // A lone finite point measures to itself; non-finite points are skipped.
            var lone = new List<double[]> { new[] { double.NaN, 0, 0 }, new double[] { 3, 4, 0 } };
            Assert.Equal(5.0, MapGeometryDecisions.MinDistanceToPolyline(new double[] { 0, 0, 0 }, lone).Value, 9);
            Assert.Null(MapGeometryDecisions.MinDistanceToPolyline(new double[] { 0, 0, 0 }, new List<double[]>()));
            Assert.Null(MapGeometryDecisions.MinDistanceToPolyline(new[] { double.NaN, 0, 0 }, line));
        }

        [Fact]
        public void FormatCaptureSummary_IsInvariantAndNamesTheOffsetAndIconDistance()
        {
            CultureInfo saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                string line = MapGeometryDecisions.FormatCaptureSummary(new MapGeometryDecisions.CaptureSummary
                {
                    Frame = 42, Scene = "FLIGHT", Bodies = 17, Ghosts = 3, ProtoGhosts = 2, Legs = 4,
                    LegRefs = 1, Arcs = 2, Bridges = 1, MarkersDrawn = 1, MarkerBodiesBySoi = 1,
                    Routes = 0, RouteLegs = 0,
                    ScaledOffset = new[] { 1.5, -2.25, 0.0 },
                    MaxScaledCentreSkewM = 0.125,
                    IconLineMeasured = 2, MaxIconToLineM = 1234.56, MaxIconToLinePid = 77,
                });
                Assert.Contains("frame=42 scene=FLIGHT", line);
                Assert.Contains("bridges=1", line);
                Assert.Contains("markerBodiesBySoi=1", line);
                Assert.Contains("scaledOffset=(1.5,-2.25,0)", line);
                Assert.Contains("maxScaledCentreSkewM=0.125", line);
                Assert.Contains("iconLineMeasured=2 maxIconToOrbitLineM=1234.6 pid=77", line);
                Assert.DoesNotContain("1234,6", line);

                string empty = MapGeometryDecisions.FormatCaptureSummary(new MapGeometryDecisions.CaptureSummary());
                Assert.Contains("scaledOffset=unknown", empty);
                Assert.Contains("maxScaledCentreSkewM=unknown", empty);
                Assert.Contains("maxIconToOrbitLineM=none", empty);
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }

        [Fact]
        public void TryRecordingTimeRange_SubtractsTheLoopShift()
        {
            Assert.True(MapGeometryDecisions.TryRecordingTimeRange(1100, 1200, 1000, out double a, out double b));
            Assert.Equal(100, a);
            Assert.Equal(200, b);
            Assert.False(MapGeometryDecisions.TryRecordingTimeRange(double.NaN, 1, 0, out _, out _));
        }

        [Fact]
        public void CycleToInt_RefusesWhatDoesNotFit()
        {
            Assert.Equal(7, MapGeometryDecisions.CycleToInt(7));
            Assert.Null(MapGeometryDecisions.CycleToInt((long)int.MaxValue + 1));
        }

        [Fact]
        public void RingSampleUT_IsAnOpenRing()
        {
            int n = MapGeometryJson.BodyOrbitRingPoints;
            Assert.Equal(500.0, MapGeometryDecisions.RingSampleUT(500, 1000, 0, n));
            // Open ring: the last point stops one step short of the period (the viewer closes it).
            Assert.Equal(500.0 + 1000.0 * (n - 1) / n, MapGeometryDecisions.RingSampleUT(500, 1000, n - 1, n), 9);
            Assert.Equal(500.0, MapGeometryDecisions.RingSampleUT(500, 1000, 3, 0));
        }
    }
}
