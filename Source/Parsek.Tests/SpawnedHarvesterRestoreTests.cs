using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A spawned miner whose snapshot had its drills running gets them back on once it has
    /// settled landed (MINE-7 / MINE-8: stock's ground-contact check switched all four off
    /// while the spawn waited for ground contact).
    /// </summary>
    [Collection("Sequential")]
    public class SpawnedHarvesterRestoreTests : IDisposable
    {
        public SpawnedHarvesterRestoreTests()
        {
            ParsekLog.TestSinkForTesting = line => { };
            SpawnedHarvesterRestore.ResetForTesting();
        }

        public void Dispose()
        {
            SpawnedHarvesterRestore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static ConfigNode Part(ConfigNode vessel, uint pid, params (string name, string active)[] modules)
        {
            ConfigNode part = vessel.AddNode("PART");
            part.AddValue("persistentId", pid.ToString());
            foreach (var m in modules)
            {
                ConfigNode node = part.AddNode("MODULE");
                node.AddValue("name", m.name);
                if (m.active != null) node.AddValue("IsActivated", m.active);
            }
            return part;
        }

        [Fact]
        public void CollectActiveHarvesters_FindsOnlyRunningHarvesters()
        {
            var vessel = new ConfigNode("VESSEL");
            Part(vessel, 100u, ("ModuleCommand", null));
            Part(vessel, 200u, ("ModuleAnimationGroup", null), ("ModuleResourceHarvester", "True"));
            Part(vessel, 300u, ("ModuleResourceHarvester", "False"));
            Part(vessel, 400u, ("ModuleResourceConverter", "True"));
            Part(vessel, 500u, ("ModuleResourceHarvester", "False"), ("ModuleResourceHarvester", "True"));

            List<SpawnedHarvesterRestore.HarvesterKey> keys =
                SpawnedHarvesterRestore.CollectActiveHarvesters(vessel);

            Assert.Equal(2, keys.Count);
            Assert.Equal(1, keys[0].PartIndex);
            Assert.Equal(200u, keys[0].PartPersistentId);
            Assert.Equal(0, keys[0].HarvesterOrdinal);
            Assert.Equal(4, keys[1].PartIndex);
            Assert.Equal(500u, keys[1].PartPersistentId);
            Assert.Equal(1, keys[1].HarvesterOrdinal);
        }

        [Fact]
        public void CollectActiveHarvesters_NothingRunning_IsEmpty()
        {
            var vessel = new ConfigNode("VESSEL");
            Part(vessel, 200u, ("ModuleResourceHarvester", "False"));
            Assert.Empty(SpawnedHarvesterRestore.CollectActiveHarvesters(vessel));
            Assert.Empty(SpawnedHarvesterRestore.CollectActiveHarvesters(null));
        }

        [Fact]
        public void Decide_WaitsUntilLandedOffRailsForTheSettleTime()
        {
            double settled = double.NaN;
            // Waiting for ground contact: on rails.
            Assert.Equal(SpawnedHarvesterRestore.Step.Wait,
                SpawnedHarvesterRestore.Decide(true, true, true, true, 10.0, 10.0, ref settled));
            Assert.True(double.IsNaN(settled));
            // Off rails and landed: the settle clock starts.
            Assert.Equal(SpawnedHarvesterRestore.Step.Wait,
                SpawnedHarvesterRestore.Decide(true, true, false, true, 11.0, 10.0, ref settled));
            Assert.Equal(11.0, settled);
            // A bounce off the ground restarts it.
            Assert.Equal(SpawnedHarvesterRestore.Step.Wait,
                SpawnedHarvesterRestore.Decide(true, true, false, false, 12.0, 10.0, ref settled));
            Assert.True(double.IsNaN(settled));
            Assert.Equal(SpawnedHarvesterRestore.Step.Wait,
                SpawnedHarvesterRestore.Decide(true, true, false, true, 12.5, 10.0, ref settled));
            Assert.Equal(SpawnedHarvesterRestore.Step.Wait,
                SpawnedHarvesterRestore.Decide(true, true, false, true, 14.0, 10.0, ref settled));
            Assert.Equal(SpawnedHarvesterRestore.Step.Restore,
                SpawnedHarvesterRestore.Decide(true, true, false, true,
                    12.5 + SpawnedHarvesterRestore.SettleSeconds, 10.0, ref settled));
        }

        [Fact]
        public void Decide_GivesUpWhenTheVesselIsGoneOrNeverSettles()
        {
            double settled = double.NaN;
            Assert.Equal(SpawnedHarvesterRestore.Step.GiveUp,
                SpawnedHarvesterRestore.Decide(false, false, false, false, 11.0, 10.0, ref settled));
            Assert.Equal(SpawnedHarvesterRestore.Step.GiveUp,
                SpawnedHarvesterRestore.Decide(true, true, true, true,
                    10.0 + SpawnedHarvesterRestore.GiveUpSeconds + 1.0, 10.0, ref settled));
        }
    }
}
