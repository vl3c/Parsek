using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for PartStateSeeder.EmitSeedEvents and FlightRecorder.DecodeEngineKey.
    /// Verifies that initial visual state is correctly captured as seed PartEvents
    /// at recording start (bugs #70/#65).
    /// </summary>
    [Collection("Sequential")]
    public class SeedEventTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SeedEventTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        #region DecodeEngineKey

        [Fact]
        public void DecodeEngineKey_RoundtripsWithEncodeEngineKey()
        {
            uint pid = 42000;
            int moduleIndex = 3;
            ulong key = FlightRecorder.EncodeEngineKey(pid, moduleIndex);

            uint decodedPid;
            int decodedMidx;
            FlightRecorder.DecodeEngineKey(key, out decodedPid, out decodedMidx);

            Assert.Equal(pid, decodedPid);
            Assert.Equal(moduleIndex, decodedMidx);
        }

        [Fact]
        public void DecodeEngineKey_ZeroModuleIndex()
        {
            uint pid = 100000;
            int moduleIndex = 0;
            ulong key = FlightRecorder.EncodeEngineKey(pid, moduleIndex);

            uint decodedPid;
            int decodedMidx;
            FlightRecorder.DecodeEngineKey(key, out decodedPid, out decodedMidx);

            Assert.Equal(pid, decodedPid);
            Assert.Equal(0, decodedMidx);
        }

        [Fact]
        public void DecodeEngineKey_MaxModuleIndex255()
        {
            uint pid = 999999;
            int moduleIndex = 255;
            ulong key = FlightRecorder.EncodeEngineKey(pid, moduleIndex);

            uint decodedPid;
            int decodedMidx;
            FlightRecorder.DecodeEngineKey(key, out decodedPid, out decodedMidx);

            Assert.Equal(pid, decodedPid);
            Assert.Equal(255, decodedMidx);
        }

        [Fact]
        public void DecodeEngineKey_LargePid()
        {
            uint pid = uint.MaxValue;
            int moduleIndex = 7;
            ulong key = FlightRecorder.EncodeEngineKey(pid, moduleIndex);

            uint decodedPid;
            int decodedMidx;
            FlightRecorder.DecodeEngineKey(key, out decodedPid, out decodedMidx);

            Assert.Equal(pid, decodedPid);
            Assert.Equal(moduleIndex, decodedMidx);
        }

        #endregion

        #region EmitSeedEvents — empty sets

        [Fact]
        public void EmitSeedEvents_EmptySets_ReturnsZeroEvents()
        {
            var sets = new PartTrackingSets
            {
                deployedFairings = new HashSet<uint>(),
                jettisonedShrouds = new HashSet<uint>(),
                parachuteStates = new Dictionary<uint, int>(),
                extendedDeployables = new HashSet<uint>(),
                lightsOn = new HashSet<uint>(),
                blinkingLights = new HashSet<uint>(),
                lightBlinkRates = new Dictionary<uint, float>(),
                deployedGear = new HashSet<uint>(),
                openCargoBays = new HashSet<uint>(),
                deployedLadders = new HashSet<ulong>(),
                deployedAnimationGroups = new HashSet<ulong>(),
                deployedAnimateGenericModules = new HashSet<ulong>(),
                deployedAeroSurfaceModules = new HashSet<ulong>(),
                deployedControlSurfaceModules = new HashSet<ulong>(),
                deployedRobotArmScannerModules = new HashSet<ulong>(),
                animateHeatLevels = new Dictionary<ulong, HeatLevel>(),
                activeEngineKeys = new HashSet<ulong>(),
                lastThrottle = new Dictionary<ulong, float>(),
                activeRcsKeys = new HashSet<ulong>(),
                lastRcsThrottle = new Dictionary<ulong, float>(),
            };
            var names = new Dictionary<uint, string>();

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 1000.0, "Test");

            Assert.Empty(result);
            Assert.Contains(logLines, l => l.Contains("Seed events emitted: 0"));
        }

        [Fact]
        public void EmitSeedEvents_NullSets_ReturnsZeroEvents()
        {
            var result = PartStateSeeder.EmitSeedEvents(null, new Dictionary<uint, string>(), 1000.0, "Test");
            Assert.Empty(result);
        }

        #endregion

        #region EmitSeedEvents — individual set types

        [Fact]
        public void EmitSeedEvents_OneExtendedDeployable_EmitsDeployableExtended()
        {
            var sets = MakeEmptySets();
            sets.extendedDeployables.Add(500u);
            var names = new Dictionary<uint, string> { { 500u, "solarPanel" } };

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 2000.0, "Test");

            Assert.Single(result);
            Assert.Equal(PartEventType.DeployableExtended, result[0].eventType);
            Assert.Equal(500u, result[0].partPersistentId);
            Assert.Equal(2000.0, result[0].ut);
            Assert.Equal("solarPanel", result[0].partName);
            Assert.Equal(0, result[0].moduleIndex);
            Assert.Contains(logLines, l => l.Contains("Seed event: DeployableExtended pid=500"));
        }

        [Fact]
        public void EmitSeedEvents_OneJettisonedShroud_EmitsShroudJettisoned()
        {
            var sets = MakeEmptySets();
            sets.jettisonedShrouds.Add(600u);
            var names = new Dictionary<uint, string> { { 600u, "engineShroud" } };

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 3000.0, "Test");

            Assert.Single(result);
            Assert.Equal(PartEventType.ShroudJettisoned, result[0].eventType);
            Assert.Equal(600u, result[0].partPersistentId);
            Assert.Equal(3000.0, result[0].ut);
            Assert.Equal("engineShroud", result[0].partName);
        }

        [Fact]
        public void EmitSeedEvents_DeployedFairing_EmitsFairingJettisoned()
        {
            var sets = MakeEmptySets();
            sets.deployedFairings.Add(700u);
            var names = new Dictionary<uint, string> { { 700u, "proceduralFairing" } };

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 4000.0, "Test");

            Assert.Single(result);
            Assert.Equal(PartEventType.FairingJettisoned, result[0].eventType);
            Assert.Equal(700u, result[0].partPersistentId);
        }

        [Fact]
        public void EmitSeedEvents_LightOn_EmitsLightOn()
        {
            var sets = MakeEmptySets();
            sets.lightsOn.Add(800u);
            var names = new Dictionary<uint, string> { { 800u, "spotLight" } };

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 5000.0, "Test");

            Assert.Single(result);
            Assert.Equal(PartEventType.LightOn, result[0].eventType);
            Assert.Equal(800u, result[0].partPersistentId);
        }

        [Fact]
        public void EmitSeedEvents_BlinkingLight_EmitsLightBlinkEnabledWithRate()
        {
            var sets = MakeEmptySets();
            sets.blinkingLights.Add(900u);
            sets.lightBlinkRates[900u] = 2.5f;
            var names = new Dictionary<uint, string> { { 900u, "navLight" } };

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 6000.0, "Test");

            Assert.Single(result);
            Assert.Equal(PartEventType.LightBlinkEnabled, result[0].eventType);
            Assert.Equal(900u, result[0].partPersistentId);
            Assert.Equal(2.5f, result[0].value);
            Assert.Contains(logLines, l => l.Contains("LightBlinkEnabled") && l.Contains("rate=2.50"));
        }

        [Fact]
        public void EmitSeedEvents_DeployedGear_EmitsGearDeployed()
        {
            var sets = MakeEmptySets();
            sets.deployedGear.Add(1000u);
            var names = new Dictionary<uint, string> { { 1000u, "gearFixed" } };

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 7000.0, "Test");

            Assert.Single(result);
            Assert.Equal(PartEventType.GearDeployed, result[0].eventType);
            Assert.Equal(1000u, result[0].partPersistentId);
        }

        [Fact]
        public void EmitSeedEvents_OpenCargoBay_EmitsCargoBayOpened()
        {
            var sets = MakeEmptySets();
            sets.openCargoBays.Add(1100u);
            var names = new Dictionary<uint, string> { { 1100u, "serviceBay" } };

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 8000.0, "Test");

            Assert.Single(result);
            Assert.Equal(PartEventType.CargoBayOpened, result[0].eventType);
            Assert.Equal(1100u, result[0].partPersistentId);
        }

        [Fact]
        public void EmitSeedEvents_ParachuteDeployed_EmitsParachuteDeployed()
        {
            var sets = MakeEmptySets();
            sets.parachuteStates[1200u] = 2; // fully deployed
            var names = new Dictionary<uint, string> { { 1200u, "parachuteMk1" } };

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 9000.0, "Test");

            Assert.Single(result);
            Assert.Equal(PartEventType.ParachuteDeployed, result[0].eventType);
            Assert.Equal(1200u, result[0].partPersistentId);
        }

        [Fact]
        public void EmitSeedEvents_ParachuteSemiDeployed_EmitsParachuteSemiDeployed()
        {
            var sets = MakeEmptySets();
            sets.parachuteStates[1300u] = 1; // semi-deployed
            var names = new Dictionary<uint, string> { { 1300u, "parachuteMk2" } };

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 9500.0, "Test");

            Assert.Single(result);
            Assert.Equal(PartEventType.ParachuteSemiDeployed, result[0].eventType);
            Assert.Equal(1300u, result[0].partPersistentId);
        }

        [Fact]
        public void EmitSeedEvents_ParachuteStowed_NoEvent()
        {
            var sets = MakeEmptySets();
            sets.parachuteStates[1400u] = 0; // stowed — should not emit
            var names = new Dictionary<uint, string> { { 1400u, "parachuteMk3" } };

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 9600.0, "Test");

            Assert.Empty(result);
        }

        #endregion

        #region EmitSeedEvents — ulong-keyed sets (decoded moduleIndex)

        [Fact]
        public void EmitSeedEvents_DeployedLadder_EmitsDeployableExtendedWithModuleIndex()
        {
            var sets = MakeEmptySets();
            uint pid = 2000u;
            int midx = 5;
            ulong key = FlightRecorder.EncodeEngineKey(pid, midx);
            sets.deployedLadders.Add(key);
            var names = new Dictionary<uint, string> { { pid, "ladder1" } };

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 10000.0, "Test");

            Assert.Single(result);
            Assert.Equal(PartEventType.DeployableExtended, result[0].eventType);
            Assert.Equal(pid, result[0].partPersistentId);
            Assert.Equal(midx, result[0].moduleIndex);
            Assert.Contains(logLines, l => l.Contains("ladder") && l.Contains("midx=5"));
        }

        [Fact]
        public void EmitSeedEvents_DeployedAnimationGroup_EmitsDeployableExtendedWithModuleIndex()
        {
            var sets = MakeEmptySets();
            uint pid = 2100u;
            int midx = 2;
            ulong key = FlightRecorder.EncodeEngineKey(pid, midx);
            sets.deployedAnimationGroups.Add(key);
            var names = new Dictionary<uint, string> { { pid, "drillS" } };

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 11000.0, "Test");

            Assert.Single(result);
            Assert.Equal(PartEventType.DeployableExtended, result[0].eventType);
            Assert.Equal(pid, result[0].partPersistentId);
            Assert.Equal(midx, result[0].moduleIndex);
        }

        [Fact]
        public void EmitSeedEvents_DeployedAnimateGeneric_EmitsDeployableExtendedWithModuleIndex()
        {
            var sets = MakeEmptySets();
            uint pid = 2200u;
            int midx = 3;
            ulong key = FlightRecorder.EncodeEngineKey(pid, midx);
            sets.deployedAnimateGenericModules.Add(key);
            var names = new Dictionary<uint, string> { { pid, "structuralWing" } };

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 12000.0, "Test");

            Assert.Single(result);
            Assert.Equal(PartEventType.DeployableExtended, result[0].eventType);
            Assert.Equal(pid, result[0].partPersistentId);
            Assert.Equal(midx, result[0].moduleIndex);
        }

        [Fact]
        public void EmitSeedEvents_DeployedAeroSurface_EmitsDeployableExtendedWithModuleIndex()
        {
            var sets = MakeEmptySets();
            uint pid = 2300u;
            int midx = 1;
            ulong key = FlightRecorder.EncodeEngineKey(pid, midx);
            sets.deployedAeroSurfaceModules.Add(key);
            var names = new Dictionary<uint, string> { { pid, "airbrake" } };

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 13000.0, "Test");

            Assert.Single(result);
            Assert.Equal(PartEventType.DeployableExtended, result[0].eventType);
            Assert.Equal(pid, result[0].partPersistentId);
            Assert.Equal(midx, result[0].moduleIndex);
        }

        [Fact]
        public void EmitSeedEvents_DeployedControlSurface_EmitsDeployableExtendedWithModuleIndex()
        {
            var sets = MakeEmptySets();
            uint pid = 2400u;
            int midx = 4;
            ulong key = FlightRecorder.EncodeEngineKey(pid, midx);
            sets.deployedControlSurfaceModules.Add(key);
            var names = new Dictionary<uint, string> { { pid, "elevon" } };

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 14000.0, "Test");

            Assert.Single(result);
            Assert.Equal(PartEventType.DeployableExtended, result[0].eventType);
            Assert.Equal(pid, result[0].partPersistentId);
            Assert.Equal(midx, result[0].moduleIndex);
        }

        [Fact]
        public void EmitSeedEvents_DeployedRobotArmScanner_EmitsDeployableExtendedWithModuleIndex()
        {
            var sets = MakeEmptySets();
            uint pid = 2500u;
            int midx = 6;
            ulong key = FlightRecorder.EncodeEngineKey(pid, midx);
            sets.deployedRobotArmScannerModules.Add(key);
            var names = new Dictionary<uint, string> { { pid, "robotArm" } };

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 15000.0, "Test");

            Assert.Single(result);
            Assert.Equal(PartEventType.DeployableExtended, result[0].eventType);
            Assert.Equal(pid, result[0].partPersistentId);
            Assert.Equal(midx, result[0].moduleIndex);
        }

        #endregion

        #region EmitSeedEvents — thermal, engine, RCS

        [Fact]
        public void EmitSeedEvents_HeatLevelHot_EmitsThermalAnimationHot()
        {
            var sets = MakeEmptySets();
            uint pid = 3000u;
            int midx = 2;
            ulong key = FlightRecorder.EncodeEngineKey(pid, midx);
            sets.animateHeatLevels[key] = HeatLevel.Hot;
            var names = new Dictionary<uint, string> { { pid, "engineBell" } };

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 16000.0, "Test");

            Assert.Single(result);
            Assert.Equal(PartEventType.ThermalAnimationHot, result[0].eventType);
            Assert.Equal(pid, result[0].partPersistentId);
            Assert.Equal(midx, result[0].moduleIndex);
        }

        [Fact]
        public void EmitSeedEvents_HeatLevelMedium_EmitsThermalAnimationMedium()
        {
            var sets = MakeEmptySets();
            uint pid = 3100u;
            int midx = 1;
            ulong key = FlightRecorder.EncodeEngineKey(pid, midx);
            sets.animateHeatLevels[key] = HeatLevel.Medium;
            var names = new Dictionary<uint, string> { { pid, "heatShield" } };

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 17000.0, "Test");

            Assert.Single(result);
            Assert.Equal(PartEventType.ThermalAnimationMedium, result[0].eventType);
            Assert.Equal(pid, result[0].partPersistentId);
            Assert.Equal(midx, result[0].moduleIndex);
        }

        [Fact]
        public void EmitSeedEvents_HeatLevelCold_NoEvent()
        {
            var sets = MakeEmptySets();
            uint pid = 3200u;
            int midx = 0;
            ulong key = FlightRecorder.EncodeEngineKey(pid, midx);
            sets.animateHeatLevels[key] = HeatLevel.Cold;
            var names = new Dictionary<uint, string> { { pid, "nosecone" } };

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 18000.0, "Test");

            Assert.Empty(result);
        }

        [Fact]
        public void EmitSeedEvents_ActiveEngine_EmitsEngineIgnitedWithThrottle()
        {
            var sets = MakeEmptySets();
            uint pid = 4000u;
            int midx = 1;
            ulong key = FlightRecorder.EncodeEngineKey(pid, midx);
            sets.activeEngineKeys.Add(key);
            sets.lastThrottle[key] = 0.75f;
            var names = new Dictionary<uint, string> { { pid, "liquidEngine" } };

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 19000.0, "Test");

            Assert.Single(result);
            Assert.Equal(PartEventType.EngineIgnited, result[0].eventType);
            Assert.Equal(pid, result[0].partPersistentId);
            Assert.Equal(midx, result[0].moduleIndex);
            Assert.Equal(0.75f, result[0].value);
            Assert.Contains(logLines, l => l.Contains("EngineIgnited") && l.Contains("throttle=0.75"));
        }

        [Fact]
        public void EmitSeedEvents_ActiveEngine_NoThrottleEntry_SkippedAsZeroThrottle()
        {
            // #165: engines with no throttle entry default to 0 and should be skipped
            var sets = MakeEmptySets();
            uint pid = 4100u;
            int midx = 0;
            ulong key = FlightRecorder.EncodeEngineKey(pid, midx);
            sets.activeEngineKeys.Add(key);
            // No lastThrottle entry — defaults to 0, should be skipped (#165)
            var names = new Dictionary<uint, string> { { pid, "solidBooster" } };

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 19500.0, "Test");

            Assert.DoesNotContain(result, e => e.eventType == PartEventType.EngineIgnited && e.partPersistentId == pid);
            Assert.Contains(logLines, l => l.Contains("Seed event skipped") && l.Contains("pid=4100") && l.Contains("#165"));
        }

        [Fact]
        public void EmitSeedEvents_ActiveEngine_ZeroThrottle_SkippedWithLog()
        {
            // #165: engine ignited at throttle=0 (staged but idle on pad) should NOT
            // emit a seed event — prevents plume flash-off at playback start
            var sets = MakeEmptySets();
            uint pid = 4200u;
            int midx = 0;
            ulong key = FlightRecorder.EncodeEngineKey(pid, midx);
            sets.activeEngineKeys.Add(key);
            sets.lastThrottle[key] = 0f; // explicitly zero throttle
            var names = new Dictionary<uint, string> { { pid, "liquidEngine" } };

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 19600.0, "Test");

            Assert.DoesNotContain(result, e => e.eventType == PartEventType.EngineIgnited && e.partPersistentId == pid);
            Assert.Contains(logLines, l => l.Contains("Seed event skipped") && l.Contains("pid=4200") && l.Contains("throttle=0") && l.Contains("#165"));
            Assert.Contains(logLines, l => l.Contains("Skipped 1 zero-throttle engine seed"));
        }

        [Fact]
        public void EmitSeedEvents_ActiveEngine_SmallPositiveThrottle_Emitted()
        {
            // #165: even a tiny positive throttle should emit the seed event
            var sets = MakeEmptySets();
            uint pid = 4300u;
            int midx = 0;
            ulong key = FlightRecorder.EncodeEngineKey(pid, midx);
            sets.activeEngineKeys.Add(key);
            sets.lastThrottle[key] = 0.01f;
            var names = new Dictionary<uint, string> { { pid, "ionEngine" } };

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 19700.0, "Test");

            Assert.Single(result);
            Assert.Equal(PartEventType.EngineIgnited, result[0].eventType);
            Assert.Equal(pid, result[0].partPersistentId);
            Assert.Equal(0.01f, result[0].value);
        }

        [Fact]
        public void EmitSeedEvents_MultipleEngines_MixedThrottle_OnlyNonZeroEmitted()
        {
            // #165: two engines — one at throttle=0 (skipped), one at throttle=0.5 (emitted)
            var sets = MakeEmptySets();
            uint pidA = 4400u;
            uint pidB = 4500u;
            ulong keyA = FlightRecorder.EncodeEngineKey(pidA, 0);
            ulong keyB = FlightRecorder.EncodeEngineKey(pidB, 0);
            sets.activeEngineKeys.Add(keyA);
            sets.activeEngineKeys.Add(keyB);
            sets.lastThrottle[keyA] = 0f;    // idle
            sets.lastThrottle[keyB] = 0.5f;  // thrusting
            var names = new Dictionary<uint, string>
            {
                { pidA, "boosterIdle" },
                { pidB, "boosterActive" },
            };

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 19800.0, "Test");

            // Only the non-zero engine should have a seed event
            Assert.Single(result.FindAll(e => e.eventType == PartEventType.EngineIgnited));
            Assert.Equal(pidB, result.Find(e => e.eventType == PartEventType.EngineIgnited).partPersistentId);
            Assert.Equal(0.5f, result.Find(e => e.eventType == PartEventType.EngineIgnited).value);
            Assert.Contains(logLines, l => l.Contains("Skipped 1 zero-throttle engine seed"));
        }

        #endregion

        #region ShouldSkipZeroThrottleEngineSeed — pure method (#165)

        [Theory]
        [InlineData(0f, true)]
        [InlineData(-0.01f, true)]
        [InlineData(-1f, true)]
        [InlineData(0.001f, false)]
        [InlineData(0.01f, false)]
        [InlineData(0.5f, false)]
        [InlineData(1f, false)]
        public void ShouldSkipZeroThrottleEngineSeed_CorrectForThrottleValues(float throttle, bool expectedSkip)
        {
            Assert.Equal(expectedSkip, PartStateSeeder.ShouldSkipZeroThrottleEngineSeed(throttle));
        }

        [Fact]
        public void EmitSeedEvents_ActiveRcs_EmitsRCSActivatedWithPower()
        {
            var sets = MakeEmptySets();
            uint pid = 5000u;
            int midx = 3;
            ulong key = FlightRecorder.EncodeEngineKey(pid, midx);
            sets.activeRcsKeys.Add(key);
            sets.lastRcsThrottle[key] = 0.6f;
            var names = new Dictionary<uint, string> { { pid, "rcsBlock" } };

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 20000.0, "Test");

            Assert.Single(result);
            Assert.Equal(PartEventType.RCSActivated, result[0].eventType);
            Assert.Equal(pid, result[0].partPersistentId);
            Assert.Equal(midx, result[0].moduleIndex);
            Assert.Equal(0.6f, result[0].value);
            Assert.Contains(logLines, l => l.Contains("RCSActivated") && l.Contains("power=0.60"));
        }

        #endregion

        #region EmitSeedEvents — multiple mixed states

        [Fact]
        public void EmitSeedEvents_MultipleMixedStates_CorrectCountAndAllAtStartUT()
        {
            var sets = MakeEmptySets();
            double startUT = 25000.0;

            // Add various states
            sets.extendedDeployables.Add(100u);
            sets.jettisonedShrouds.Add(200u);
            sets.deployedFairings.Add(300u);
            sets.lightsOn.Add(400u);
            sets.deployedGear.Add(500u);
            sets.parachuteStates[600u] = 2; // deployed
            sets.parachuteStates[700u] = 1; // semi-deployed

            ulong engineKey = FlightRecorder.EncodeEngineKey(800u, 0);
            sets.activeEngineKeys.Add(engineKey);
            sets.lastThrottle[engineKey] = 1.0f;

            ulong ladderKey = FlightRecorder.EncodeEngineKey(900u, 2);
            sets.deployedLadders.Add(ladderKey);

            var names = new Dictionary<uint, string>
            {
                { 100u, "solarPanel" },
                { 200u, "engineShroud" },
                { 300u, "fairing" },
                { 400u, "light" },
                { 500u, "gear" },
                { 600u, "chute1" },
                { 700u, "chute2" },
                { 800u, "engine" },
                { 900u, "ladder" },
            };

            var result = PartStateSeeder.EmitSeedEvents(sets, names, startUT, "Test");

            Assert.Equal(9, result.Count);

            // All events should be at startUT
            Assert.All(result, e => Assert.Equal(startUT, e.ut));

            // Verify each event type is present
            Assert.Contains(result, e => e.eventType == PartEventType.DeployableExtended && e.partPersistentId == 100u);
            Assert.Contains(result, e => e.eventType == PartEventType.ShroudJettisoned && e.partPersistentId == 200u);
            Assert.Contains(result, e => e.eventType == PartEventType.FairingJettisoned && e.partPersistentId == 300u);
            Assert.Contains(result, e => e.eventType == PartEventType.LightOn && e.partPersistentId == 400u);
            Assert.Contains(result, e => e.eventType == PartEventType.GearDeployed && e.partPersistentId == 500u);
            Assert.Contains(result, e => e.eventType == PartEventType.ParachuteDeployed && e.partPersistentId == 600u);
            Assert.Contains(result, e => e.eventType == PartEventType.ParachuteSemiDeployed && e.partPersistentId == 700u);
            Assert.Contains(result, e => e.eventType == PartEventType.EngineIgnited && e.partPersistentId == 800u);
            Assert.Contains(result, e => e.eventType == PartEventType.DeployableExtended && e.partPersistentId == 900u && e.moduleIndex == 2);

            // Summary log line
            Assert.Contains(logLines, l => l.Contains("Seed events emitted: 9"));
        }

        #endregion

        #region EmitSeedEvents — part name lookup

        [Fact]
        public void EmitSeedEvents_UnknownPid_UsesUnknownPartName()
        {
            var sets = MakeEmptySets();
            sets.lightsOn.Add(9999u);
            var names = new Dictionary<uint, string>(); // empty — pid not found

            var result = PartStateSeeder.EmitSeedEvents(sets, names, 30000.0, "Test");

            Assert.Single(result);
            Assert.Equal("unknown", result[0].partName);
        }

        [Fact]
        public void EmitSeedEvents_NullPartNamesByPid_UsesUnknown()
        {
            var sets = MakeEmptySets();
            sets.deployedGear.Add(8888u);

            var result = PartStateSeeder.EmitSeedEvents(sets, null, 31000.0, "Test");

            Assert.Single(result);
            Assert.Equal("unknown", result[0].partName);
        }

        #endregion

        #region Helpers

        private static PartTrackingSets MakeEmptySets()
        {
            return new PartTrackingSets
            {
                deployedFairings = new HashSet<uint>(),
                jettisonedShrouds = new HashSet<uint>(),
                parachuteStates = new Dictionary<uint, int>(),
                extendedDeployables = new HashSet<uint>(),
                lightsOn = new HashSet<uint>(),
                blinkingLights = new HashSet<uint>(),
                lightBlinkRates = new Dictionary<uint, float>(),
                deployedGear = new HashSet<uint>(),
                openCargoBays = new HashSet<uint>(),
                deployedLadders = new HashSet<ulong>(),
                deployedAnimationGroups = new HashSet<ulong>(),
                deployedAnimateGenericModules = new HashSet<ulong>(),
                deployedAeroSurfaceModules = new HashSet<ulong>(),
                deployedControlSurfaceModules = new HashSet<ulong>(),
                deployedRobotArmScannerModules = new HashSet<ulong>(),
                animateHeatLevels = new Dictionary<ulong, HeatLevel>(),
                activeEngineKeys = new HashSet<ulong>(),
                lastThrottle = new Dictionary<ulong, float>(),
                activeRcsKeys = new HashSet<ulong>(),
                lastRcsThrottle = new Dictionary<ulong, float>(),
            };
        }

        #endregion
    }
}
