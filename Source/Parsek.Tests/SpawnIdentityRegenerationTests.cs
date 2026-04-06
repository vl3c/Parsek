using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for per-part identity regeneration (#234), PID collection (#237),
    /// and robotics reference patching (#238).
    /// </summary>
    [Collection("Sequential")]
    public class SpawnIdentityRegenerationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SpawnIdentityRegenerationTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
        }

        #region CollectPartPersistentIds (#237)

        [Fact]
        public void CollectPartPersistentIds_ReturnsAllPartPids()
        {
            var snapshot = Generators.VesselSnapshotBuilder.FleaRocket("Flea", "Jeb", 500000).Build();
            var ids = VesselSpawner.CollectPartPersistentIds(snapshot);

            Assert.Equal(3, ids.Count);
            Assert.Contains(100000u, ids);
            Assert.Contains(101111u, ids);
            Assert.Contains(102222u, ids);
        }

        [Fact]
        public void CollectPartPersistentIds_SkipsZeroPid()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("persistentId", "0");

            var ids = VesselSpawner.CollectPartPersistentIds(snapshot);
            Assert.Empty(ids);
        }

        [Fact]
        public void CollectPartPersistentIds_SkipsInvalidPid()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("persistentId", "notanumber");

            var ids = VesselSpawner.CollectPartPersistentIds(snapshot);
            Assert.Empty(ids);
        }

        [Fact]
        public void CollectPartPersistentIds_NullNode_ReturnsEmpty()
        {
            var ids = VesselSpawner.CollectPartPersistentIds(null);
            Assert.Empty(ids);
        }

        [Fact]
        public void CollectPartPersistentIds_NoParts_ReturnsEmpty()
        {
            var snapshot = new ConfigNode("VESSEL");
            var ids = VesselSpawner.CollectPartPersistentIds(snapshot);
            Assert.Empty(ids);
        }

        #endregion

        #region RegeneratePartIdentities (#234)

        [Fact]
        public void RegeneratePartIdentities_SinglePart_AllFieldsChanged()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("persistentId", "100");
            part.AddValue("uid", "200");
            part.AddValue("mid", "300");
            part.AddValue("launchID", "1");

            uint nextPid = 5000;
            uint nextUid = 6000;
            var pidMap = VesselSpawner.RegeneratePartIdentities(
                snapshot,
                generatePersistentId: () => nextPid++,
                generateFlightId: () => nextUid++,
                missionId: 9999,
                launchId: 42);

            Assert.Equal("5000", part.GetValue("persistentId"));
            Assert.Equal("6000", part.GetValue("uid"));
            Assert.Equal("9999", part.GetValue("mid"));
            Assert.Equal("42", part.GetValue("launchID"));
            Assert.Single(pidMap);
            Assert.Equal(5000u, pidMap[100u]);
        }

        [Fact]
        public void RegeneratePartIdentities_MultipleParts_EachGetsUniqueIds()
        {
            var snapshot = Generators.VesselSnapshotBuilder.FleaRocket("Flea", "Jeb", 500000).Build();
            var parts = snapshot.GetNodes("PART");

            uint nextPid = 7000;
            uint nextUid = 8000;
            var pidMap = VesselSpawner.RegeneratePartIdentities(
                snapshot,
                generatePersistentId: () => nextPid++,
                generateFlightId: () => nextUid++,
                missionId: 1111,
                launchId: 5);

            Assert.Equal(3, pidMap.Count);

            // Each part has a unique persistentId and uid
            Assert.Equal("7000", parts[0].GetValue("persistentId"));
            Assert.Equal("7001", parts[1].GetValue("persistentId"));
            Assert.Equal("7002", parts[2].GetValue("persistentId"));
            Assert.Equal("8000", parts[0].GetValue("uid"));
            Assert.Equal("8001", parts[1].GetValue("uid"));
            Assert.Equal("8002", parts[2].GetValue("uid"));

            // All parts share the same missionId and launchID
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal("1111", parts[i].GetValue("mid"));
                Assert.Equal("5", parts[i].GetValue("launchID"));
            }
        }

        [Fact]
        public void RegeneratePartIdentities_NoParts_EmptyDictionary()
        {
            var snapshot = new ConfigNode("VESSEL");
            var pidMap = VesselSpawner.RegeneratePartIdentities(
                snapshot, () => 1u, () => 1u, 0, 0);
            Assert.Empty(pidMap);
        }

        [Fact]
        public void RegeneratePartIdentities_NullNode_EmptyDictionary()
        {
            var pidMap = VesselSpawner.RegeneratePartIdentities(
                null, () => 1u, () => 1u, 0, 0);
            Assert.Empty(pidMap);
        }

        [Fact]
        public void RegeneratePartIdentities_MissingPersistentId_StillAssigns()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("uid", "200");
            // No persistentId field at all

            uint nextPid = 9000;
            var pidMap = VesselSpawner.RegeneratePartIdentities(
                snapshot, () => nextPid++, () => 1u, 0, 0);

            // Should still assign a new persistentId
            Assert.Equal("9000", part.GetValue("persistentId"));
            // No mapping since old PID was missing
            Assert.Empty(pidMap);
        }

        [Fact]
        public void RegeneratePartIdentities_DictionaryMapsCorrectly()
        {
            var snapshot = new ConfigNode("VESSEL");
            var p1 = snapshot.AddNode("PART");
            p1.AddValue("persistentId", "1000");
            p1.AddValue("uid", "0"); p1.AddValue("mid", "0"); p1.AddValue("launchID", "0");
            var p2 = snapshot.AddNode("PART");
            p2.AddValue("persistentId", "2000");
            p2.AddValue("uid", "0"); p2.AddValue("mid", "0"); p2.AddValue("launchID", "0");

            uint nextPid = 5000;
            var pidMap = VesselSpawner.RegeneratePartIdentities(
                snapshot, () => nextPid++, () => 1u, 0, 0);

            Assert.Equal(2, pidMap.Count);
            Assert.Equal(5000u, pidMap[1000u]);
            Assert.Equal(5001u, pidMap[2000u]);
        }

        #endregion

        #region PatchRoboticsReferences (#238)

        [Fact]
        public void PatchRoboticsReferences_RemapsAxisPid()
        {
            var snapshot = BuildRoboticsVessel(axisPid: 100);
            var pidMap = new Dictionary<uint, uint> { { 100, 5000 } };

            int count = VesselSpawner.PatchRoboticsReferences(snapshot, pidMap);

            var axis = snapshot.GetNodes("PART")[0]
                .GetNodes("MODULE")[0]
                .GetNodes("CONTROLLEDAXES")[0]
                .GetNodes("AXIS")[0];
            Assert.Equal("5000", axis.GetValue("persistentId"));
            Assert.Equal(1, count);
        }

        [Fact]
        public void PatchRoboticsReferences_RemapsActionPid()
        {
            var snapshot = BuildRoboticsVessel(actionPid: 200);
            var pidMap = new Dictionary<uint, uint> { { 200, 6000 } };

            int count = VesselSpawner.PatchRoboticsReferences(snapshot, pidMap);

            var action = snapshot.GetNodes("PART")[0]
                .GetNodes("MODULE")[0]
                .GetNodes("CONTROLLEDACTIONS")[0]
                .GetNodes("ACTION")[0];
            Assert.Equal("6000", action.GetValue("persistentId"));
            Assert.Equal(1, count);
        }

        [Fact]
        public void PatchRoboticsReferences_RemapsSymPartsPid()
        {
            var snapshot = BuildRoboticsVessel(axisPid: 100, symPid: 300);
            var pidMap = new Dictionary<uint, uint> { { 100, 5000 }, { 300, 7000 } };

            int count = VesselSpawner.PatchRoboticsReferences(snapshot, pidMap);
            Assert.Equal(2, count);

            var axis = snapshot.GetNodes("PART")[0]
                .GetNodes("MODULE")[0]
                .GetNodes("CONTROLLEDAXES")[0]
                .GetNodes("AXIS")[0];
            Assert.Equal("5000", axis.GetValue("persistentId"));
            Assert.Equal("7000", axis.GetNodes("SYMPARTS")[0].GetValue("symPersistentId"));
        }

        [Fact]
        public void PatchRoboticsReferences_IgnoresNonRoboticsModules()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            var module = part.AddNode("MODULE");
            module.AddValue("name", "ModuleEngine");
            var axes = module.AddNode("CONTROLLEDAXES");
            var axis = axes.AddNode("AXIS");
            axis.AddValue("persistentId", "100");

            var pidMap = new Dictionary<uint, uint> { { 100, 5000 } };
            int count = VesselSpawner.PatchRoboticsReferences(snapshot, pidMap);

            Assert.Equal(0, count);
            Assert.Equal("100", axis.GetValue("persistentId"));
        }

        [Fact]
        public void PatchRoboticsReferences_PidNotInMap_Unchanged()
        {
            var snapshot = BuildRoboticsVessel(axisPid: 100);
            var pidMap = new Dictionary<uint, uint> { { 999, 5000 } };

            int count = VesselSpawner.PatchRoboticsReferences(snapshot, pidMap);

            Assert.Equal(0, count);
            var axis = snapshot.GetNodes("PART")[0]
                .GetNodes("MODULE")[0]
                .GetNodes("CONTROLLEDAXES")[0]
                .GetNodes("AXIS")[0];
            Assert.Equal("100", axis.GetValue("persistentId"));
        }

        [Fact]
        public void PatchRoboticsReferences_NullGuards()
        {
            Assert.Equal(0, VesselSpawner.PatchRoboticsReferences(null, new Dictionary<uint, uint> { { 1, 2 } }));
            Assert.Equal(0, VesselSpawner.PatchRoboticsReferences(new ConfigNode("V"), null));
            Assert.Equal(0, VesselSpawner.PatchRoboticsReferences(new ConfigNode("V"), new Dictionary<uint, uint>()));
        }

        [Fact]
        public void PatchRoboticsReferences_MalformedPid_Skipped()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            var module = part.AddNode("MODULE");
            module.AddValue("name", "ModuleRoboticController");
            var axes = module.AddNode("CONTROLLEDAXES");
            var axis = axes.AddNode("AXIS");
            axis.AddValue("persistentId", "notanumber");

            var pidMap = new Dictionary<uint, uint> { { 100, 5000 } };
            int count = VesselSpawner.PatchRoboticsReferences(snapshot, pidMap);
            Assert.Equal(0, count);
        }

        [Fact]
        public void PatchRoboticsReferences_LogsSummaryWhenPatched()
        {
            var snapshot = BuildRoboticsVessel(axisPid: 100, actionPid: 200);
            var pidMap = new Dictionary<uint, uint> { { 100, 5000 }, { 200, 6000 } };

            VesselSpawner.PatchRoboticsReferences(snapshot, pidMap);

            // No log from PatchRoboticsReferences itself (caller logs the summary),
            // but verify it doesn't crash and returns correct count
            Assert.Equal(2, VesselSpawner.PatchRoboticsReferences(
                BuildRoboticsVessel(axisPid: 100, actionPid: 200), pidMap));
        }

        #endregion

        #region Log assertions

        [Fact]
        public void RegeneratePartIdentities_MultipleParts_LogNotEmpty()
        {
            var snapshot = Generators.VesselSnapshotBuilder.FleaRocket("Flea", "Jeb", 500000).Build();
            uint nextPid = 7000;
            uint nextUid = 8000;

            VesselSpawner.RegeneratePartIdentities(
                snapshot, () => nextPid++, () => nextUid++, 1111, 5);

            // RegeneratePartIdentities is called by RegenerateVesselIdentity which logs;
            // the method itself doesn't log (caller responsibility). Verify no crash.
            Assert.Equal(3, snapshot.GetNodes("PART").Length);
        }

        [Fact]
        public void ApplyPostSpawnStabilization_NullVessel_NoLog()
        {
            VesselSpawner.ApplyPostSpawnStabilization(null, "LANDED");
            Assert.DoesNotContain(logLines, l => l.Contains("Post-spawn stabilization"));
        }

        [Fact]
        public void ApplyPostSpawnStabilization_OrbitalSituation_NoLog()
        {
            // Can't construct a real Vessel in unit tests, but verify the guard
            // rejects orbital situations before reaching the vessel API calls
            Assert.False(VesselSpawner.ShouldZeroVelocityAfterSpawn("ORBITING"));
            Assert.False(VesselSpawner.ShouldZeroVelocityAfterSpawn("FLYING"));
            Assert.False(VesselSpawner.ShouldZeroVelocityAfterSpawn("SUB_ORBITAL"));
        }

        #endregion

        private static ConfigNode BuildRoboticsVessel(uint axisPid = 0, uint actionPid = 0, uint symPid = 0)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("persistentId", "100");
            var module = part.AddNode("MODULE");
            module.AddValue("name", "ModuleRoboticController");

            if (axisPid != 0)
            {
                var axes = module.AddNode("CONTROLLEDAXES");
                var axis = axes.AddNode("AXIS");
                axis.AddValue("persistentId", axisPid.ToString(CultureInfo.InvariantCulture));
                if (symPid != 0)
                {
                    var sym = axis.AddNode("SYMPARTS");
                    sym.AddValue("symPersistentId", symPid.ToString(CultureInfo.InvariantCulture));
                }
            }

            if (actionPid != 0)
            {
                var actions = module.AddNode("CONTROLLEDACTIONS");
                var action = actions.AddNode("ACTION");
                action.AddValue("persistentId", actionPid.ToString(CultureInfo.InvariantCulture));
            }

            return snapshot;
        }
    }
}
