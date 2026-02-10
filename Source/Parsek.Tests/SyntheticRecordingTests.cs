using System.Globalization;
using System.IO;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    public class SyntheticRecordingTests
    {
        private static string ProjectRoot => Path.GetFullPath(
            Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", ".."));

        #region Recording Builders

        internal static RecordingBuilder KscHopper()
        {
            // 15 points: launchpad → 500m up → drift east 1km → descend near VAB
            // UT 127000–127056, 4-second intervals
            var b = new RecordingBuilder("KSC Hopper");
            double baseLat = -0.0972;
            double baseLon = -74.5575;

            // Liftoff (0-12s): straight up to 500m
            b.AddPoint(127000, baseLat, baseLon, 77);        // pad
            b.AddPoint(127004, baseLat, baseLon, 150);
            b.AddPoint(127008, baseLat, baseLon, 300);
            b.AddPoint(127012, baseLat, baseLon, 500);

            // Hover + drift east (12-32s): move ~1km east, hold ~500m
            b.AddPoint(127016, baseLat, baseLon + 0.002, 490);
            b.AddPoint(127020, baseLat, baseLon + 0.004, 480);
            b.AddPoint(127024, baseLat, baseLon + 0.006, 470);
            b.AddPoint(127028, baseLat, baseLon + 0.008, 460);
            b.AddPoint(127032, baseLat, baseLon + 0.009, 440);

            // Descend near VAB (32-56s): come down, slight north drift
            b.AddPoint(127036, baseLat + 0.0005, baseLon + 0.009, 380);
            b.AddPoint(127040, baseLat + 0.001, baseLon + 0.009, 300);
            b.AddPoint(127044, baseLat + 0.001, baseLon + 0.009, 200);
            b.AddPoint(127048, baseLat + 0.001, baseLon + 0.009, 120);
            b.AddPoint(127052, baseLat + 0.001, baseLon + 0.009, 85);
            b.AddPoint(127056, baseLat + 0.001, baseLon + 0.009, 77);  // landed

            return b;
        }

        internal static RecordingBuilder SuborbitalArc()
        {
            // 25 points: launchpad → gravity turn east → 70km apex → splashdown
            // UT 128000–128300, variable spacing
            var b = new RecordingBuilder("Suborbital Arc");
            double lat = -0.0972;
            double lon = -74.5575;

            // Launch phase (0-40s, dense sampling)
            b.AddPoint(128000, lat, lon, 77);
            b.AddPoint(128005, lat, lon, 300);
            b.AddPoint(128010, lat, lon, 800);
            b.AddPoint(128015, lat, lon + 0.001, 1800);
            b.AddPoint(128020, lat, lon + 0.003, 3500);
            b.AddPoint(128025, lat + 0.001, lon + 0.006, 6000);
            b.AddPoint(128030, lat + 0.002, lon + 0.01, 9500);
            b.AddPoint(128035, lat + 0.003, lon + 0.015, 14000);
            b.AddPoint(128040, lat + 0.004, lon + 0.02, 19000);

            // Gravity turn (40-100s, sparser)
            b.AddPoint(128050, lat + 0.006, lon + 0.035, 28000);
            b.AddPoint(128060, lat + 0.008, lon + 0.055, 37000);
            b.AddPoint(128070, lat + 0.01, lon + 0.08, 46000);
            b.AddPoint(128080, lat + 0.012, lon + 0.11, 55000);
            b.AddPoint(128090, lat + 0.014, lon + 0.14, 63000);
            b.AddPoint(128100, lat + 0.015, lon + 0.17, 68000);

            // Apex (100-140s)
            b.AddPoint(128110, lat + 0.016, lon + 0.20, 70500);
            b.AddPoint(128120, lat + 0.017, lon + 0.23, 71000);  // peak
            b.AddPoint(128130, lat + 0.018, lon + 0.26, 70200);
            b.AddPoint(128140, lat + 0.019, lon + 0.29, 67000);

            // Descent (140-300s, sparser)
            b.AddPoint(128160, lat + 0.021, lon + 0.35, 55000);
            b.AddPoint(128180, lat + 0.023, lon + 0.42, 40000);
            b.AddPoint(128210, lat + 0.026, lon + 0.52, 22000);
            b.AddPoint(128240, lat + 0.028, lon + 0.62, 8000);
            b.AddPoint(128270, lat + 0.029, lon + 0.70, 1500);
            b.AddPoint(128300, lat + 0.03, lon + 0.75, 0);  // splashdown

            return b;
        }

        internal static RecordingBuilder Orbit1()
        {
            // 10 ascent points + 1 orbit segment, crewed vessel
            // UT 129000–132000
            var b = new RecordingBuilder("Orbit-1");
            double lat = -0.0972;
            double lon = -74.5575;

            // Ascent phase: 10 points from pad to ~80km
            b.AddPoint(129000, lat, lon, 77);
            b.AddPoint(129020, lat, lon + 0.005, 5000);
            b.AddPoint(129040, lat + 0.005, lon + 0.02, 15000);
            b.AddPoint(129060, lat + 0.01, lon + 0.05, 30000);
            b.AddPoint(129080, lat + 0.015, lon + 0.1, 45000);
            b.AddPoint(129100, lat + 0.02, lon + 0.16, 58000);
            b.AddPoint(129120, lat + 0.025, lon + 0.24, 68000);
            b.AddPoint(129140, lat + 0.03, lon + 0.34, 75000);
            b.AddPoint(129160, lat + 0.035, lon + 0.46, 79000);
            b.AddPoint(129180, lat + 0.04, lon + 0.60, 80000);

            // Orbital segment: ~80km circular, from 129500 to 132000
            b.AddOrbitSegment(129500, 132000,
                inc: 28.5, ecc: 0.001, sma: 700000,
                lan: 90, argPe: 45, mna: 0, epoch: 129500,
                body: "Kerbin");

            // Vessel snapshot: crewed pod with Bill
            b.WithVesselSnapshot(
                VesselSnapshotBuilder.CrewedShip("Orbit-1", "Bill Kerman", pid: 12345678)
                    .AsOrbiting(sma: 700000, ecc: 0.001, inc: 28.5,
                        lan: 90, argPe: 45, mna: 0, epoch: 129500));

            return b;
        }

        internal static RecordingBuilder IslandProbe()
        {
            // 20 points: launchpad → fly SE → island airfield
            // UT 133000–133180
            var b = new RecordingBuilder("Island Probe");
            double startLat = -0.0972;
            double startLon = -74.5575;
            double endLat = -1.52;
            double endLon = -71.97;

            // Takeoff (0-20s)
            b.AddPoint(133000, startLat, startLon, 77);
            b.AddPoint(133010, startLat, startLon, 200);

            // Climb + turn SE (20-60s)
            b.AddPoint(133020, -0.15, -74.40, 400);
            b.AddPoint(133030, -0.25, -74.20, 600);
            b.AddPoint(133040, -0.40, -73.95, 800);
            b.AddPoint(133050, -0.55, -73.70, 900);
            b.AddPoint(133060, -0.70, -73.45, 950);

            // Cruise toward island (60-120s)
            b.AddPoint(133070, -0.82, -73.20, 1000);
            b.AddPoint(133080, -0.93, -72.95, 1000);
            b.AddPoint(133090, -1.03, -72.70, 1000);
            b.AddPoint(133100, -1.12, -72.50, 1000);
            b.AddPoint(133110, -1.20, -72.30, 950);
            b.AddPoint(133120, -1.28, -72.15, 900);

            // Approach + descent (120-180s)
            b.AddPoint(133130, -1.34, -72.05, 800);
            b.AddPoint(133140, -1.39, -71.98, 600);
            b.AddPoint(133150, -1.43, -71.97, 400);
            b.AddPoint(133160, -1.47, -71.97, 200);
            b.AddPoint(133170, -1.50, -71.97, 80);
            b.AddPoint(133175, -1.51, -71.97, 50);
            b.AddPoint(133180, endLat, endLon, 40);  // landed

            // Vessel: unmanned probe
            b.WithVesselSnapshot(
                VesselSnapshotBuilder.ProbeShip("Island Probe", pid: 87654321)
                    .AsLanded(endLat, endLon, 40));

            return b;
        }

        #endregion

        #region Unit Tests

        [Fact]
        public void KscHopper_BuildsValidRecording()
        {
            var node = KscHopper().Build();
            Assert.Equal("KSC Hopper", node.GetValue("vesselName"));
            Assert.Equal("15", node.GetValue("pointCount"));
            Assert.Equal(15, node.GetNodes("POINT").Length);
            Assert.Empty(node.GetNodes("ORBIT_SEGMENT"));
            Assert.Null(node.GetNode("VESSEL_SNAPSHOT"));
        }

        [Fact]
        public void SuborbitalArc_BuildsValidRecording()
        {
            var node = SuborbitalArc().Build();
            Assert.Equal("Suborbital Arc", node.GetValue("vesselName"));
            Assert.Equal("25", node.GetValue("pointCount"));
            Assert.Equal(25, node.GetNodes("POINT").Length);

            // Verify UT ordering
            var points = node.GetNodes("POINT");
            double prevUT = 0;
            foreach (var pt in points)
            {
                double ut = double.Parse(pt.GetValue("ut"), CultureInfo.InvariantCulture);
                Assert.True(ut > prevUT, $"UT {ut} should be > {prevUT}");
                prevUT = ut;
            }
        }

        [Fact]
        public void Orbit1_HasOrbitSegmentAndSnapshot()
        {
            var node = Orbit1().Build();
            Assert.Equal("Orbit-1", node.GetValue("vesselName"));
            Assert.Equal(10, node.GetNodes("POINT").Length);
            Assert.Single(node.GetNodes("ORBIT_SEGMENT"));

            var seg = node.GetNodes("ORBIT_SEGMENT")[0];
            Assert.Equal("Kerbin", seg.GetValue("body"));

            var snapshot = node.GetNode("VESSEL_SNAPSHOT");
            Assert.NotNull(snapshot);
            Assert.Equal("Orbit-1", snapshot.GetValue("name"));
            Assert.Equal("ORBITING", snapshot.GetValue("sit"));

            // Verify crew in snapshot
            var parts = snapshot.GetNodes("PART");
            Assert.True(parts.Length > 0);
            Assert.Equal("Bill Kerman", parts[0].GetValue("crew"));
        }

        [Fact]
        public void IslandProbe_HasSnapshotNoCrew()
        {
            var node = IslandProbe().Build();
            Assert.Equal("Island Probe", node.GetValue("vesselName"));
            Assert.Equal(20, node.GetNodes("POINT").Length);

            var snapshot = node.GetNode("VESSEL_SNAPSHOT");
            Assert.NotNull(snapshot);
            Assert.Equal("LANDED", snapshot.GetValue("sit"));
            Assert.Equal("Probe", snapshot.GetValue("type"));

            // No crew values
            var parts = snapshot.GetNodes("PART");
            Assert.True(parts.Length > 0);
            Assert.Null(parts[0].GetValue("crew"));
        }

        [Fact]
        public void ScenarioWriter_SerializesCorrectly()
        {
            var writer = new ScenarioWriter();
            writer.AddRecording(KscHopper());

            var scenarioNode = writer.BuildScenarioNode();
            Assert.Equal("ParsekScenario", scenarioNode.GetValue("name"));
            Assert.Single(scenarioNode.GetNodes("RECORDING"));

            string text = writer.SerializeConfigNode(scenarioNode, "SCENARIO", 1);
            Assert.Contains("name = ParsekScenario", text);
            Assert.Contains("RECORDING", text);
            Assert.Contains("POINT", text);
        }

        [Fact]
        public void ScenarioWriter_InjectIntoSave_InsertsBeforeFlightstate()
        {
            string fakeSave =
                "GAME\n{\n" +
                "\tSCENARIO\n\t{\n\t\tname = SomeOther\n\t\tscene = 5\n\t}\n" +
                "\tFLIGHTSTATE\n\t{\n\t\tversion = 1.12.5\n\t}\n" +
                "}\n";

            var writer = new ScenarioWriter();
            writer.AddRecording(KscHopper());

            string result = writer.InjectIntoSave(fakeSave);

            // ParsekScenario appears before FLIGHTSTATE
            int scenarioIdx = result.IndexOf("name = ParsekScenario");
            int flightstateIdx = result.IndexOf("FLIGHTSTATE");
            Assert.True(scenarioIdx > 0);
            Assert.True(scenarioIdx < flightstateIdx);

            // Original scenario still present
            Assert.Contains("name = SomeOther", result);
        }

        [Fact]
        public void ScenarioWriter_InjectIntoSave_ReplacesExistingParsekScenario()
        {
            string saveWithParsek =
                "GAME\n{\n" +
                "\tSCENARIO\n\t{\n\t\tname = ParsekScenario\n\t\tscene = 5\n\t\tRECORDING\n\t\t{\n\t\t\tvesselName = Old\n\t\t}\n\t}\n" +
                "\tFLIGHTSTATE\n\t{\n\t\tversion = 1.12.5\n\t}\n" +
                "}\n";

            var writer = new ScenarioWriter();
            writer.AddRecording(KscHopper());

            string result = writer.InjectIntoSave(saveWithParsek);

            // Only one ParsekScenario
            int firstIdx = result.IndexOf("name = ParsekScenario");
            int secondIdx = result.IndexOf("name = ParsekScenario", firstIdx + 1);
            Assert.True(firstIdx > 0);
            Assert.Equal(-1, secondIdx);

            // Old recording gone, new one present
            Assert.DoesNotContain("vesselName = Old", result);
            Assert.Contains("vesselName = KSC Hopper", result);
        }

        [Fact]
        public void ScenarioWriter_InjectIntoSave_Idempotent()
        {
            string fakeSave =
                "GAME\n{\n" +
                "\tFLIGHTSTATE\n\t{\n\t\tversion = 1.12.5\n\t}\n" +
                "}\n";

            var writer = new ScenarioWriter();
            writer.AddRecording(KscHopper());

            string first = writer.InjectIntoSave(fakeSave);
            string second = writer.InjectIntoSave(first);

            // Count occurrences of ParsekScenario
            int count = 0;
            int idx = 0;
            while ((idx = second.IndexOf("name = ParsekScenario", idx)) >= 0)
            {
                count++;
                idx += 1;
            }
            Assert.Equal(1, count);
        }

        [Fact]
        public void ScenarioWriter_InjectIntoSave_HandlesVariousWhitespace()
        {
            // Tabs + extra spacing, CRLF line endings, extra values in scenario
            string saveWithParsek =
                "GAME\r\n{\r\n" +
                "\tSCENARIO\r\n\t{\r\n" +
                "\t\tname = ParsekScenario\r\n" +
                "\t\tscene = 5, 6, 7, 8\r\n" +
                "\t\tRECORDING\r\n\t\t{\r\n" +
                "\t\t\tvesselName = Old\r\n" +
                "\t\t\tpointCount = 5\r\n" +
                "\t\t\tPOINT\r\n\t\t\t{\r\n\t\t\t\tut = 100\r\n\t\t\t}\r\n" +
                "\t\t}\r\n" +
                "\t}\r\n" +
                "\tFLIGHTSTATE\r\n\t{\r\n\t\tversion = 1.12.5\r\n\t}\r\n" +
                "}\r\n";

            var writer = new ScenarioWriter();
            writer.AddRecording(KscHopper());

            string result = writer.InjectIntoSave(saveWithParsek);

            // Only one ParsekScenario
            int firstIdx = result.IndexOf("name = ParsekScenario");
            int secondIdx = result.IndexOf("name = ParsekScenario", firstIdx + 1);
            Assert.True(firstIdx > 0);
            Assert.Equal(-1, secondIdx);

            // Old data gone
            Assert.DoesNotContain("vesselName = Old", result);
            Assert.Contains("vesselName = KSC Hopper", result);
        }

        [Fact]
        public void VesselSnapshotBuilder_DeterministicPid()
        {
            var v1 = VesselSnapshotBuilder.ProbeShip("Test", pid: 42).Build();
            var v2 = VesselSnapshotBuilder.ProbeShip("Test", pid: 42).Build();
            Assert.Equal(v1.GetValue("pid"), v2.GetValue("pid"));
        }

        #endregion

        #region Save File Injection (manual — requires save file)

        [Trait("Category", "Manual")]
        [Fact]
        public void InjectAllRecordings()
        {
            string savePath = Path.Combine(ProjectRoot,
                "Kerbal Space Program", "saves", "4", "1.sfs");
            if (!File.Exists(savePath))
                return;

            // Work on a temp copy, then replace the original
            string tempPath = savePath + ".tmp";
            try
            {
                var writer = new ScenarioWriter();
                writer.AddRecording(KscHopper());
                writer.AddRecording(SuborbitalArc());
                writer.AddRecording(Orbit1());
                writer.AddRecording(IslandProbe());

                writer.InjectIntoSaveFile(savePath, tempPath);

                // Verify the temp output before overwriting
                string content = File.ReadAllText(tempPath);
                Assert.Contains("name = ParsekScenario", content);
                Assert.Contains("vesselName = KSC Hopper", content);
                Assert.Contains("vesselName = Suborbital Arc", content);
                Assert.Contains("vesselName = Orbit-1", content);
                Assert.Contains("vesselName = Island Probe", content);
                Assert.Contains("FLIGHTSTATE", content);

                // Only replace if verification passed
                File.Copy(tempPath, savePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        #endregion
    }
}
