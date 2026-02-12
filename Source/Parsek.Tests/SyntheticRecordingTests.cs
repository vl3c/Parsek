using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    public class SyntheticRecordingTests
    {
        private static string ProjectRoot => Path.GetFullPath(
            Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", ".."));

        #region Recording Builders

        // Offsets from baseUT for each recording
        // KSC Hopper:     +120s to +176s  (56s duration)
        // Suborbital Arc: +210s to +510s  (300s duration)
        // Orbit-1:        +560s to +3560s (3000s, ascent+orbit segment)
        // Island Probe:   +3610s to +3790s (180s duration)

        internal static RecordingBuilder KscHopper(double baseUT = 0)
        {
            double t = baseUT + 120;
            var b = new RecordingBuilder("KSC Hopper");
            double baseLat = -0.0972;
            double baseLon = -74.5575;

            b.AddPoint(t,    baseLat, baseLon, 77);
            b.AddPoint(t+4,  baseLat, baseLon, 150);
            b.AddPoint(t+8,  baseLat, baseLon, 300);
            b.AddPoint(t+12, baseLat, baseLon, 500);
            b.AddPoint(t+16, baseLat, baseLon + 0.002, 490);
            b.AddPoint(t+20, baseLat, baseLon + 0.004, 480);
            b.AddPoint(t+24, baseLat, baseLon + 0.006, 470);
            b.AddPoint(t+28, baseLat, baseLon + 0.008, 460);
            b.AddPoint(t+32, baseLat, baseLon + 0.009, 440);
            b.AddPoint(t+36, baseLat + 0.0005, baseLon + 0.009, 380);
            b.AddPoint(t+40, baseLat + 0.001, baseLon + 0.009, 300);
            b.AddPoint(t+44, baseLat + 0.001, baseLon + 0.009, 200);
            b.AddPoint(t+48, baseLat + 0.001, baseLon + 0.009, 120);
            b.AddPoint(t+52, baseLat + 0.001, baseLon + 0.009, 85);
            b.AddPoint(t+56, baseLat + 0.001, baseLon + 0.009, 77);

            // Vessel snapshot so playback can render as vessel geometry (not sphere)
            b.WithVesselSnapshot(
                VesselSnapshotBuilder.ProbeShip("KSC Hopper", pid: 11111111)
                    .AsLanded(baseLat + 0.001, baseLon + 0.009, 77));

            return b;
        }

        internal static RecordingBuilder SuborbitalArc(double baseUT = 0)
        {
            double t = baseUT + 210;
            var b = new RecordingBuilder("Suborbital Arc");
            double lat = -0.0972;
            double lon = -74.5575;

            b.AddPoint(t,     lat, lon, 77);
            b.AddPoint(t+5,   lat, lon, 300);
            b.AddPoint(t+10,  lat, lon, 800);
            b.AddPoint(t+15,  lat, lon + 0.001, 1800);
            b.AddPoint(t+20,  lat, lon + 0.003, 3500);
            b.AddPoint(t+25,  lat + 0.001, lon + 0.006, 6000);
            b.AddPoint(t+30,  lat + 0.002, lon + 0.01, 9500);
            b.AddPoint(t+35,  lat + 0.003, lon + 0.015, 14000);
            b.AddPoint(t+40,  lat + 0.004, lon + 0.02, 19000);
            b.AddPoint(t+50,  lat + 0.006, lon + 0.035, 28000);
            b.AddPoint(t+60,  lat + 0.008, lon + 0.055, 37000);
            b.AddPoint(t+70,  lat + 0.01, lon + 0.08, 46000);
            b.AddPoint(t+80,  lat + 0.012, lon + 0.11, 55000);
            b.AddPoint(t+90,  lat + 0.014, lon + 0.14, 63000);
            b.AddPoint(t+100, lat + 0.015, lon + 0.17, 68000);
            b.AddPoint(t+110, lat + 0.016, lon + 0.20, 70500);
            b.AddPoint(t+120, lat + 0.017, lon + 0.23, 71000);
            b.AddPoint(t+130, lat + 0.018, lon + 0.26, 70200);
            b.AddPoint(t+140, lat + 0.019, lon + 0.29, 67000);
            b.AddPoint(t+160, lat + 0.021, lon + 0.35, 55000);
            b.AddPoint(t+180, lat + 0.023, lon + 0.42, 40000);
            b.AddPoint(t+210, lat + 0.026, lon + 0.52, 22000);
            b.AddPoint(t+240, lat + 0.028, lon + 0.62, 8000);
            b.AddPoint(t+270, lat + 0.029, lon + 0.70, 1500);
            b.AddPoint(t+300, lat + 0.03, lon + 0.75, 0);

            // Vessel snapshot so playback can render as vessel geometry (not sphere)
            b.WithVesselSnapshot(
                VesselSnapshotBuilder.ProbeShip("Suborbital Arc", pid: 22222222)
                    .AsLanded(lat + 0.03, lon + 0.75, 0));

            return b;
        }

        internal static RecordingBuilder Orbit1(double baseUT = 0)
        {
            double t = baseUT + 560;
            var b = new RecordingBuilder("Orbit-1");
            double lat = -0.0972;
            double lon = -74.5575;

            // 11 ascent points, last at orbit segment start
            b.AddPoint(t,     lat, lon, 77);
            b.AddPoint(t+50,  lat, lon + 0.005, 5000);
            b.AddPoint(t+100, lat + 0.005, lon + 0.02, 15000);
            b.AddPoint(t+150, lat + 0.01, lon + 0.05, 30000);
            b.AddPoint(t+200, lat + 0.015, lon + 0.1, 45000);
            b.AddPoint(t+250, lat + 0.02, lon + 0.16, 58000);
            b.AddPoint(t+300, lat + 0.025, lon + 0.24, 68000);
            b.AddPoint(t+350, lat + 0.03, lon + 0.34, 75000);
            b.AddPoint(t+400, lat + 0.035, lon + 0.46, 79000);
            b.AddPoint(t+450, lat + 0.04, lon + 0.55, 80000);
            b.AddPoint(t+500, lat + 0.045, lon + 0.60, 80000);

            // Orbit segment starts at last ascent point
            double segStart = t + 500;
            double segEnd = t + 3000;
            b.AddOrbitSegment(segStart, segEnd,
                inc: 28.5, ecc: 0.001, sma: 700000,
                lan: 90, argPe: 45, mna: 0, epoch: segStart,
                body: "Kerbin");

            b.WithVesselSnapshot(
                VesselSnapshotBuilder.CrewedShip("Orbit-1", "Bill Kerman", pid: 12345678)
                    .AsOrbiting(sma: 700000, ecc: 0.001, inc: 28.5,
                        lan: 90, argPe: 45, mna: 0, epoch: segStart));

            return b;
        }

        internal static RecordingBuilder IslandProbe(double baseUT = 0)
        {
            double t = baseUT + 3610;
            var b = new RecordingBuilder("Island Probe");
            double startLat = -0.0972;
            double startLon = -74.5575;
            double endLat = -1.52;
            double endLon = -71.97;

            b.AddPoint(t,     startLat, startLon, 77);
            b.AddPoint(t+10,  startLat, startLon, 200);
            b.AddPoint(t+20,  -0.15, -74.40, 400);
            b.AddPoint(t+30,  -0.25, -74.20, 600);
            b.AddPoint(t+40,  -0.40, -73.95, 800);
            b.AddPoint(t+50,  -0.55, -73.70, 900);
            b.AddPoint(t+60,  -0.70, -73.45, 950);
            b.AddPoint(t+70,  -0.82, -73.20, 1000);
            b.AddPoint(t+80,  -0.93, -72.95, 1000);
            b.AddPoint(t+90,  -1.03, -72.70, 1000);
            b.AddPoint(t+100, -1.12, -72.50, 1000);
            b.AddPoint(t+110, -1.20, -72.30, 950);
            b.AddPoint(t+120, -1.28, -72.15, 900);
            b.AddPoint(t+130, -1.34, -72.05, 800);
            b.AddPoint(t+140, -1.39, -71.98, 600);
            b.AddPoint(t+150, -1.43, -71.97, 400);
            b.AddPoint(t+160, -1.47, -71.97, 200);
            b.AddPoint(t+170, -1.50, -71.97, 80);
            b.AddPoint(t+175, -1.51, -71.97, 50);
            b.AddPoint(t+180, endLat, endLon, 40);

            b.WithVesselSnapshot(
                VesselSnapshotBuilder.ProbeShip("Island Probe", pid: 87654321)
                    .AsLanded(endLat, endLon, 40));

            return b;
        }

        internal static RecordingBuilder TedorfEvaSwitch(double baseUT = 0)
        {
            // Mirrors the real-world case: a launch recording that ended around
            // vessel-switch/EVA timing and should replay as the vessel, not EVA.
            double t = baseUT + 8;
            var b = new RecordingBuilder("Tedorf Kerman");
            double baseLat = -0.09720776197;
            double baseLon = -74.55767853202;

            b.AddPoint(t + 0, baseLat, baseLon, 69.6, funds: 42469);
            b.AddPoint(t + 2, baseLat + 0.002, baseLon + 0.0001, 120, funds: 42469);
            b.AddPoint(t + 4, baseLat + 0.004, baseLon + 0.0003, 180, funds: 42469);
            b.AddPoint(t + 6, baseLat + 0.008, baseLon + 0.0005, 230, funds: 42469);
            b.AddPoint(t + 8, baseLat + 0.012, baseLon + 0.0007, 255, funds: 42469);
            b.AddPoint(t + 10, baseLat + 0.015, baseLon + 0.0008, 240, funds: 42469);
            b.AddPoint(t + 12, baseLat + 0.020, baseLon + 0.00085, 220, funds: 42469);
            b.AddPoint(t + 14, baseLat + 0.026, baseLon + 0.0009, 190, funds: 42469);
            b.AddPoint(t + 16, baseLat + 0.034, baseLon + 0.00095, 130, funds: 42469);
            b.AddPoint(t + 18, baseLat + 0.043, baseLon + 0.0010, 90, funds: 42469);
            b.AddPoint(t + 20, baseLat + 0.051, baseLon + 0.00102, 70, funds: 43429);
            b.AddPoint(t + 22, baseLat + 0.061, baseLon + 0.00105, 66, funds: 43429);

            // Snapshot deliberately points to the vessel, not kerbal EVA.
            b.WithVesselSnapshot(
                VesselSnapshotBuilder.CrewedShip("Untitled Space Craft", "Tedorf Kerman", pid: 314348688)
                    .AsLanded(baseLat + 0.061, baseLon + 0.00105, 66));

            return b;
        }

        internal static RecordingBuilder KscPadDestroyed(double baseUT = 0)
        {
            // Edge case: vessel destroyed near KSC pad. No vessel snapshot on purpose.
            double t = baseUT + 900;
            var b = new RecordingBuilder("KSC Pad Destroyed");
            double lat = -0.0972;
            double lon = -74.5576;

            b.AddPoint(t + 0, lat, lon, 70, funds: 43429);
            b.AddPoint(t + 2, lat, lon, 95, funds: 43429);
            b.AddPoint(t + 4, lat, lon + 0.0002, 150, funds: 43429);
            b.AddPoint(t + 6, lat, lon + 0.0005, 210, funds: 43429);
            b.AddPoint(t + 8, lat, lon + 0.0008, 160, funds: 43429);
            b.AddPoint(t + 10, lat, lon + 0.0011, 90, funds: 43429);
            b.AddPoint(t + 12, lat, lon + 0.0012, 65, funds: 43429);

            // No snapshot intentionally to force destroyed/no-snapshot fallback path.
            return b;
        }

        internal static RecordingBuilder EvaWalkSkinned(double baseUT = 0)
        {
            // Edge case: explicit EVA snapshot with kerbal part model.
            double t = baseUT + 980;
            var b = new RecordingBuilder("EVA Walk Test");
            double lat = -0.0969;
            double lon = -74.5580;

            b.AddPoint(t + 0, lat, lon, 66);
            b.AddPoint(t + 3, lat + 0.0001, lon + 0.0001, 66);
            b.AddPoint(t + 6, lat + 0.0002, lon + 0.0002, 66);
            b.AddPoint(t + 9, lat + 0.0003, lon + 0.0003, 66);
            b.AddPoint(t + 12, lat + 0.00035, lon + 0.0004, 66);

            b.WithVesselSnapshot(
                VesselSnapshotBuilder.CrewedShip("EVA Walk Test", "Jebediah Kerman", pid: 33333333)
                    .WithType("EVA")
                    .AsLanded(lat + 0.00035, lon + 0.0004, 66));

            return b;
        }

        internal static RecordingBuilder CloseSpawnConflict(double baseUT = 0)
        {
            // Edge case: landed vessel very near KSC to exercise spawn offset logic.
            double t = baseUT + 1060;
            var b = new RecordingBuilder("Close Spawn Conflict");
            double lat = -0.09718;
            double lon = -74.55755;

            b.AddPoint(t + 0, lat, lon, 70);
            b.AddPoint(t + 4, lat + 0.00002, lon + 0.00002, 70);
            b.AddPoint(t + 8, lat + 0.00003, lon + 0.00003, 70);
            b.AddPoint(t + 12, lat + 0.00003, lon + 0.00003, 70);

            b.WithVesselSnapshot(
                VesselSnapshotBuilder.ProbeShip("Close Spawn Conflict", pid: 44444444)
                    .AsLanded(lat + 0.00003, lon + 0.00003, 70));

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
            Assert.NotNull(node.GetNode("VESSEL_SNAPSHOT"));
        }

        [Fact]
        public void SuborbitalArc_BuildsValidRecording()
        {
            var node = SuborbitalArc().Build();
            Assert.Equal("Suborbital Arc", node.GetValue("vesselName"));
            Assert.Equal("25", node.GetValue("pointCount"));
            Assert.Equal(25, node.GetNodes("POINT").Length);
            Assert.NotNull(node.GetNode("VESSEL_SNAPSHOT"));

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
            Assert.Equal(11, node.GetNodes("POINT").Length);
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
        public void TedorfEvaSwitch_BuildsVesselSnapshotNotEVA()
        {
            var node = TedorfEvaSwitch().Build();
            Assert.Equal("Tedorf Kerman", node.GetValue("vesselName"));

            var snapshot = node.GetNode("VESSEL_SNAPSHOT");
            Assert.NotNull(snapshot);
            Assert.Equal("Untitled Space Craft", snapshot.GetValue("name"));
            Assert.Equal("Ship", snapshot.GetValue("type"));
            Assert.Equal("LANDED", snapshot.GetValue("sit"));

            var parts = snapshot.GetNodes("PART");
            Assert.True(parts.Length > 0);
            Assert.Equal("mk1pod.v2", parts[0].GetValue("name"));
            Assert.Equal("Tedorf Kerman", parts[0].GetValue("crew"));
        }

        [Fact]
        public void KscPadDestroyed_HasNoSnapshot()
        {
            var node = KscPadDestroyed().Build();
            Assert.Equal("KSC Pad Destroyed", node.GetValue("vesselName"));
            Assert.Equal(7, node.GetNodes("POINT").Length);
            Assert.Null(node.GetNode("VESSEL_SNAPSHOT"));
        }

        [Fact]
        public void EvaWalkSkinned_HasEvaTypeSnapshot()
        {
            var node = EvaWalkSkinned().Build();
            Assert.Equal("EVA Walk Test", node.GetValue("vesselName"));
            var snapshot = node.GetNode("VESSEL_SNAPSHOT");
            Assert.NotNull(snapshot);
            Assert.Equal("EVA", snapshot.GetValue("type"));
        }

        [Fact]
        public void CloseSpawnConflict_HasLandedSnapshotNearKsc()
        {
            var node = CloseSpawnConflict().Build();
            Assert.Equal("Close Spawn Conflict", node.GetValue("vesselName"));
            var snapshot = node.GetNode("VESSEL_SNAPSHOT");
            Assert.NotNull(snapshot);
            Assert.Equal("LANDED", snapshot.GetValue("sit"));
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

        /// <summary>
        /// Read UT from FLIGHTSTATE in a save file.
        /// </summary>
        private static double ReadUTFromSave(string savePath)
        {
            string content = File.ReadAllText(savePath);
            var match = Regex.Match(content, @"FLIGHTSTATE\s*\{[^}]*?UT\s*=\s*([0-9.eE+\-]+)",
                RegexOptions.Singleline);
            if (match.Success &&
                double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ut))
                return ut;
            return 0;
        }

        private static bool IsTruthy(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            return value == "1" || value.Equals("true", System.StringComparison.OrdinalIgnoreCase);
        }

        private static string RemoveTopLevelVesselBlocks(string content)
        {
            var lines = content.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None);
            var result = new System.Collections.Generic.List<string>(lines.Length);
            int i = 0;
            while (i < lines.Length)
            {
                string trimmed = lines[i].Trim();
                if (trimmed == "VESSEL")
                {
                    // Skip top-level world vessel block body.
                    i++; // move to opening brace line
                    int depth = 0;
                    while (i < lines.Length)
                    {
                        string t = lines[i].Trim();
                        if (t == "{") depth++;
                        else if (t == "}")
                        {
                            depth--;
                            if (depth <= 0)
                            {
                                i++;
                                break;
                            }
                        }
                        i++;
                    }
                    continue;
                }
                result.Add(lines[i]);
                i++;
            }

            string sep = content.Contains("\r\n") ? "\r\n" : "\n";
            return string.Join(sep, result);
        }

        private static string RemoveSpawnedPidLines(string content)
        {
            var lines = content.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None);
            var result = new System.Collections.Generic.List<string>(lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("spawnedPid =",
                    System.StringComparison.Ordinal))
                    continue;
                result.Add(lines[i]);
            }
            string sep = content.Contains("\r\n") ? "\r\n" : "\n";
            return string.Join(sep, result);
        }

        private static void CleanSaveStart(string savePath)
        {
            if (!File.Exists(savePath))
                return;

            string content = File.ReadAllText(savePath);
            content = RemoveTopLevelVesselBlocks(content);
            content = RemoveSpawnedPidLines(content);
            File.WriteAllText(savePath, content);
        }

        [Trait("Category", "Manual")]
        [Fact]
        public void InjectAllRecordings()
        {
            string saveName = System.Environment.GetEnvironmentVariable("PARSEK_INJECT_SAVE_NAME")
                ?? "test career";
            string targetSave = System.Environment.GetEnvironmentVariable("PARSEK_INJECT_TARGET_SAVE")
                ?? "1.sfs";
            bool cleanStart = IsTruthy(System.Environment.GetEnvironmentVariable("PARSEK_INJECT_CLEAN_START"));

            string saveDir = Path.Combine(ProjectRoot,
                "Kerbal Space Program", "saves", saveName);

            // Inject into both persistent.sfs and the target save — KSP loads
            // persistent first (sets initialLoadDone), so it must have the recordings too.
            string[] targets = { "persistent.sfs", targetSave };

            string targetPath = Path.Combine(saveDir, targetSave);
            if (!File.Exists(targetPath))
                return;

            double baseUT = ReadUTFromSave(targetPath);

            var writer = new ScenarioWriter();
            writer.AddRecording(KscHopper(baseUT));
            writer.AddRecording(SuborbitalArc(baseUT));
            writer.AddRecording(Orbit1(baseUT));
            writer.AddRecording(IslandProbe(baseUT));
            writer.AddRecording(TedorfEvaSwitch(baseUT));
            writer.AddRecording(KscPadDestroyed(baseUT));
            writer.AddRecording(EvaWalkSkinned(baseUT));
            writer.AddRecording(CloseSpawnConflict(baseUT));

            foreach (string file in targets)
            {
                string savePath = Path.Combine(saveDir, file);
                if (!File.Exists(savePath))
                    continue;

                if (cleanStart)
                    CleanSaveStart(savePath);

                string tempPath = savePath + ".tmp";
                try
                {
                    writer.InjectIntoSaveFile(savePath, tempPath);

                    string content = File.ReadAllText(tempPath);
                    Assert.Contains("name = ParsekScenario", content);
                    Assert.Contains("vesselName = KSC Hopper", content);
                    Assert.Contains("vesselName = Suborbital Arc", content);
                    Assert.Contains("vesselName = Orbit-1", content);
                    Assert.Contains("vesselName = Island Probe", content);
                    Assert.Contains("vesselName = Tedorf Kerman", content);
                    Assert.Contains("vesselName = KSC Pad Destroyed", content);
                    Assert.Contains("vesselName = EVA Walk Test", content);
                    Assert.Contains("vesselName = Close Spawn Conflict", content);
                    Assert.Contains("FLIGHTSTATE", content);

                    File.Copy(tempPath, savePath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
            }
        }

        #endregion
    }
}
