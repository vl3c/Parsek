using Xunit;

namespace Parsek.Tests
{
    public class LocationContextTests
    {
        // --- Serialization round-trip tests ---

        [Fact]
        public void SaveLoad_LocationFields_RoundTrip()
        {
            var source = new Recording
            {
                StartBodyName = "Mun",
                StartBiome = "Midlands",
                StartSituation = "Landed",
                EndBiome = "Highlands"
            };
            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal("Mun", loaded.StartBodyName);
            Assert.Equal("Midlands", loaded.StartBiome);
            Assert.Equal("Landed", loaded.StartSituation);
            Assert.Equal("Highlands", loaded.EndBiome);
        }

        [Fact]
        public void SaveLoad_NullLocationFields_DefaultsSafely()
        {
            // Simulate a legacy recording with no location fields
            var node = new ConfigNode("RECORDING");
            node.AddValue("loopPlayback", "False");

            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Null(loaded.StartBodyName);
            Assert.Null(loaded.StartBiome);
            Assert.Null(loaded.StartSituation);
            Assert.Null(loaded.EndBiome);
        }

        [Fact]
        public void SaveLoad_EmptyLocationFields_NotWritten()
        {
            var source = new Recording();  // all null
            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            // Verify keys were not written (saves space)
            Assert.Null(node.GetValue("startBodyName"));
            Assert.Null(node.GetValue("startBiome"));
            Assert.Null(node.GetValue("startSituation"));
            Assert.Null(node.GetValue("endBiome"));
        }

        // --- ApplyPersistenceArtifactsFrom tests ---

        [Fact]
        public void ApplyPersistenceArtifacts_CopiesEndBiome()
        {
            var source = new Recording { EndBiome = "Shores" };
            var target = new Recording();
            target.ApplyPersistenceArtifactsFrom(source);
            Assert.Equal("Shores", target.EndBiome);
        }

        [Fact]
        public void ApplyPersistenceArtifacts_DoesNotCopyStartFields()
        {
            var source = new Recording
            {
                StartBodyName = "Kerbin",
                StartBiome = "KSC",
                StartSituation = "Prelaunch"
            };
            var target = new Recording();
            target.ApplyPersistenceArtifactsFrom(source);

            // Start fields should NOT be copied — they're captured fresh per segment
            Assert.Null(target.StartBodyName);
            Assert.Null(target.StartBiome);
            Assert.Null(target.StartSituation);
        }

        // --- Display text tests ---

        [Fact]
        public void GetRecordingStartText_WithLaunchSite_PrefersSiteOverBiome()
        {
            string text = TimelineEntryDisplay.GetRecordingStartText(
                "Mun Lander", 3600, false, null, "Kerbin", "KSC", "Launch Pad");
            Assert.Equal("Launch: Mun Lander from Launch Pad on Kerbin (MET 1h)", text);
        }

        [Fact]
        public void GetRecordingStartText_WithBiome_NoLaunchSite()
        {
            string text = TimelineEntryDisplay.GetRecordingStartText(
                "Mun Lander", 3600, false, null, "Kerbin", "KSC", null);
            Assert.Equal("Launch: Mun Lander at KSC on Kerbin (MET 1h)", text);
        }

        [Fact]
        public void GetRecordingStartText_NullBiome_FallsBackToBody()
        {
            string text = TimelineEntryDisplay.GetRecordingStartText(
                "Mun Lander", 3600, false, null, "Kerbin", null, null);
            Assert.Equal("Launch: Mun Lander on Kerbin (MET 1h)", text);
        }

        [Fact]
        public void GetRecordingStartText_NullBody_OriginalFormat()
        {
            string text = TimelineEntryDisplay.GetRecordingStartText(
                "Mun Lander", 3600, false, null, null, null);
            Assert.Equal("Launch: Mun Lander (MET 1h)", text);
        }

        [Fact]
        public void GetRecordingStartText_Eva_WithBiome()
        {
            string text = TimelineEntryDisplay.GetRecordingStartText(
                "Jeb", 5, true, "Mun Lander", "Mun", "Midlands", null);
            Assert.Equal("EVA: Jeb from Mun Lander at Midlands on Mun (MET 5s)", text);
        }

        [Fact]
        public void GetRecordingStartText_MakingHistorySite()
        {
            string text = TimelineEntryDisplay.GetRecordingStartText(
                "Rocket", 600, false, null, "Kerbin", null, "Woomerang Launch Site");
            Assert.Equal("Launch: Rocket from Woomerang Launch Site on Kerbin (MET 10m)", text);
        }

        [Fact]
        public void GetVesselSpawnText_WithEndBiome_ShowsAtBiome()
        {
            string text = TimelineEntryDisplay.GetVesselSpawnText(
                "Mun Lander", TerminalState.Landed, "Landed Mun",
                false, null, null, null, "Midlands");
            Assert.Equal("Spawn: Mun Lander (Landed at Midlands on Mun)", text);
        }

        [Fact]
        public void GetVesselSpawnText_NullEndBiome_OriginalFormat()
        {
            string text = TimelineEntryDisplay.GetVesselSpawnText(
                "Mun Lander", TerminalState.Landed, "Landed Mun",
                false, null, null, null, null);
            Assert.Equal("Spawn: Mun Lander (Landed Mun)", text);
        }

        [Fact]
        public void GetVesselSpawnText_Orbiting_NoBiome_Unchanged()
        {
            string text = TimelineEntryDisplay.GetVesselSpawnText(
                "Station", TerminalState.Orbiting, "ORBITING Kerbin",
                false, null, "Kerbin", null, "KSC");
            // Orbital situations should NOT inject biome
            Assert.Equal("Spawn: Station (ORBITING Kerbin)", text);
        }

        [Fact]
        public void GetVesselSpawnText_Splashed_WithBiome()
        {
            string text = TimelineEntryDisplay.GetVesselSpawnText(
                "Capsule", TerminalState.Splashed, "Splashed Kerbin",
                false, null, null, null, "Shores");
            Assert.Equal("Spawn: Capsule (Splashed at Shores on Kerbin)", text);
        }

        [Fact]
        public void GetVesselSpawnText_FallbackPath_WithBiome()
        {
            // No VesselSituation, using terminal state fallback
            string text = TimelineEntryDisplay.GetVesselSpawnText(
                "Probe", TerminalState.Landed, null,
                false, null, null, "Mun", "Midlands");
            Assert.Equal("Spawn: Probe (Landed at Midlands on Mun)", text);
        }

        // --- InjectBiomeIntoSituation tests ---

        [Fact]
        public void InjectBiome_LandedSituation()
        {
            Assert.Equal("Landed at Midlands on Mun",
                TimelineEntryDisplay.InjectBiomeIntoSituation("Landed Mun", "Midlands"));
        }

        [Fact]
        public void InjectBiome_OrbitingSituation_Unchanged()
        {
            Assert.Equal("ORBITING Kerbin",
                TimelineEntryDisplay.InjectBiomeIntoSituation("ORBITING Kerbin", "KSC"));
        }

        [Fact]
        public void InjectBiome_NullInputs_ReturnsSituation()
        {
            Assert.Equal("Landed Mun",
                TimelineEntryDisplay.InjectBiomeIntoSituation("Landed Mun", null));
            Assert.Null(TimelineEntryDisplay.InjectBiomeIntoSituation(null, "Midlands"));
        }

        // --- HumanizeSituation tests ---

        [Fact]
        public void HumanizeSituation_AllValues()
        {
            Assert.Equal("Prelaunch", VesselSpawner.HumanizeSituation(Vessel.Situations.PRELAUNCH));
            Assert.Equal("Landed", VesselSpawner.HumanizeSituation(Vessel.Situations.LANDED));
            Assert.Equal("Splashed", VesselSpawner.HumanizeSituation(Vessel.Situations.SPLASHED));
            Assert.Equal("Flying", VesselSpawner.HumanizeSituation(Vessel.Situations.FLYING));
            Assert.Equal("Sub-orbital", VesselSpawner.HumanizeSituation(Vessel.Situations.SUB_ORBITAL));
            Assert.Equal("Orbiting", VesselSpawner.HumanizeSituation(Vessel.Situations.ORBITING));
            Assert.Equal("Escaping", VesselSpawner.HumanizeSituation(Vessel.Situations.ESCAPING));
            Assert.Equal("Docked", VesselSpawner.HumanizeSituation(Vessel.Situations.DOCKED));
        }

        // --- Launch site tests ---

        [Fact]
        public void SaveLoad_LaunchSiteName_RoundTrip()
        {
            var source = new Recording { LaunchSiteName = "Launch Pad" };
            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);
            Assert.Equal("Launch Pad", loaded.LaunchSiteName);
        }

        [Fact]
        public void SaveLoad_NullLaunchSiteName_NotWritten()
        {
            var source = new Recording();
            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);
            Assert.Null(node.GetValue("launchSiteName"));
        }

        [Fact]
        public void HumanizeLaunchSiteName_StockNames()
        {
            Assert.Equal("Launch Pad", FlightRecorder.HumanizeLaunchSiteName("LaunchPad"));
            Assert.Equal("Runway", FlightRecorder.HumanizeLaunchSiteName("Runway"));
        }

        [Fact]
        public void HumanizeLaunchSiteName_MakingHistoryNames_PassThrough()
        {
            Assert.Equal("Desert Airfield", FlightRecorder.HumanizeLaunchSiteName("Desert Airfield"));
            Assert.Equal("Woomerang Launch Site", FlightRecorder.HumanizeLaunchSiteName("Woomerang Launch Site"));
            Assert.Equal("Island Airfield", FlightRecorder.HumanizeLaunchSiteName("Island Airfield"));
        }

        [Fact]
        public void HumanizeLaunchSiteName_NullEmpty()
        {
            Assert.Null(FlightRecorder.HumanizeLaunchSiteName(null));
            Assert.Null(FlightRecorder.HumanizeLaunchSiteName(""));
        }
    }
}
