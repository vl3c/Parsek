using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Todo RESCUE-REGEN-COUNTS-PROTOVESSELS. Stock's <c>Game.AddVessel</c> registers the
    /// regenerated stranded vessel with <c>FlightGlobals</c> and never appends it to
    /// <c>flightState.protoVessels</c> (decompiled KSP 1.12.5), so the regeneration witness
    /// and the double-spawn world index must read the live vessels too. The shapes below are
    /// RSQ-1's Space Center crossing: three saved debris vessels, then Rayvan's Shipwreck
    /// live only.
    /// </summary>
    public class ContractAcceptWorldIndexTests
    {
        private const uint ShipwreckPartId = 268551258u;

        private static WorldVesselFact Fact(object key, Guid id, string name, bool live,
            uint pid = 0, uint[] parts = null, string[] crew = null)
        {
            var f = new WorldVesselFact { Key = key, Id = id, Name = name, Live = live, PersistentId = pid };
            if (parts != null) f.PartFlightIds.AddRange(parts);
            if (crew != null) f.Crew.AddRange(crew);
            return f;
        }

        private sealed class World
        {
            internal readonly List<WorldVesselFact> Saved = new List<WorldVesselFact>();
            internal readonly List<WorldVesselFact> Live = new List<WorldVesselFact>();
            internal readonly List<object> Keys = new List<object>();
        }

        // flightState.protoVessels and FlightGlobals.Vessels before the generator runs: the
        // same three debris vessels, one ProtoVessel object and one Vessel object each.
        private static World DebrisWorld()
        {
            var w = new World();
            string[] names = { "CS Mun Flyby Debris", "CS Mun Flyby Debris", "CS Mun Science Debris" };
            for (int i = 0; i < names.Length; i++)
            {
                Guid id = Guid.NewGuid();
                object proto = new object(), vessel = new object();
                w.Saved.Add(Fact(proto, id, names[i], false, 1000u + (uint)i, new[] { 10u + (uint)i }));
                w.Live.Add(Fact(vessel, id, names[i], true, 1000u + (uint)i, new[] { 10u + (uint)i }));
                w.Keys.Add(proto);
                w.Keys.Add(vessel);
            }
            return w;
        }

        private static WorldVesselFact Shipwreck(bool live, object key = null, Guid? id = null)
        {
            return Fact(key ?? new object(), id ?? Guid.NewGuid(), "Rayvan's Shipwreck", live,
                4242u, new[] { ShipwreckPartId }, new[] { "Rayvan Kerman" });
        }

        [Fact]
        public void Index_SeesAVesselStockCreatedThisScene_ThatFlightStateDoesNotHoldYet()
        {
            World w = DebrisWorld();
            w.Live.Add(Shipwreck(live: true));

            ContractAcceptWorldIndex index = ContractAcceptWorldIndexer.Build(w.Saved, w.Live);

            Assert.Contains("Rayvan Kerman", index.CrewAboard);
            Assert.Contains(ShipwreckPartId, index.PartFlightIds);
            Assert.Equal(4, index.VesselCount);
            Assert.Equal(1, index.LiveOnlyCount);
        }

        [Fact]
        public void Index_CountsAVesselInBothListsOnce_ButNeverMergesUnknownIds()
        {
            Guid shared = Guid.NewGuid();
            var saved = new List<WorldVesselFact> { Shipwreck(false, id: shared), Fact(new object(), Guid.Empty, "a", false) };
            var live = new List<WorldVesselFact> { Shipwreck(true, id: shared), Fact(new object(), Guid.Empty, "b", true), null };

            ContractAcceptWorldIndex index = ContractAcceptWorldIndexer.Build(saved, live);

            Assert.Equal(3, index.VesselCount);
            Assert.Equal(1, index.LiveOnlyCount);
            Assert.Single(index.CrewAboard);
        }

        [Fact]
        public void Index_GainsNothingFromAGhostMapVessel()
        {
            // GhostMapPresence's ProtoVessel: one sensorBarometer made with flight id 0, no crew.
            var live = new List<WorldVesselFact>
            {
                Fact(new object(), Guid.NewGuid(), "Ghost: Rayvan's Shipwreck", true, 99u, new[] { 0u })
            };

            ContractAcceptWorldIndex index = ContractAcceptWorldIndexer.Build(null, live);

            Assert.Empty(index.PartFlightIds);
            Assert.Empty(index.CrewAboard);
        }

        [Fact]
        public void Index_ToleratesMissingSources()
        {
            ContractAcceptWorldIndex index = ContractAcceptWorldIndexer.Build(null, null);

            Assert.Equal(0, index.VesselCount);
            Assert.Empty(index.CrewAboard);
        }

        [Fact]
        public void Witness_CountsTheLiveVesselGameAddVesselMade_WhenFlightStateDidNotGrow()
        {
            // RSQ-1 / RSQ-2: the flightState count before and after the call read 3 and 3
            // ("added=0 []"), while FlightGlobals gained Rayvan's Shipwreck.
            World w = DebrisWorld();
            var keysBefore = new HashSet<object>(w.Keys);
            w.Live.Add(Shipwreck(live: true));

            List<WorldVesselFact> added = ContractAcceptWorldIndexer.SelectAdded(keysBefore, w.Saved, w.Live);

            Assert.Single(added);
            Assert.Equal("Rayvan's Shipwreck", added[0].Name);
            Assert.True(added[0].Live);
            Assert.Equal(new[] { "Rayvan Kerman" }, added[0].Crew);
        }

        [Fact]
        public void Witness_CountsAVesselNewInBothListsOnce_AndANewSavedOnlyVesselToo()
        {
            World w = DebrisWorld();
            var keysBefore = new HashSet<object>(w.Keys);
            Guid id = Guid.NewGuid();
            w.Saved.Add(Shipwreck(false, id: id));
            w.Live.Add(Shipwreck(true, id: id));
            w.Saved.Add(Fact(new object(), Guid.NewGuid(), "Saved only", false));

            List<WorldVesselFact> added = ContractAcceptWorldIndexer.SelectAdded(keysBefore, w.Saved, w.Live);

            Assert.Equal(2, added.Count);
            Assert.True(added[0].Live);
            Assert.Equal("Saved only", added[1].Name);
        }

        [Fact]
        public void Witness_ReadsNothingAdded_WhenTheGeneratorMadeNoVessel()
        {
            World w = DebrisWorld();
            var keysBefore = new HashSet<object>(w.Keys);

            Assert.Empty(ContractAcceptWorldIndexer.SelectAdded(keysBefore, w.Saved, w.Live));
            Assert.Empty(ContractAcceptWorldIndexer.SelectAdded(keysBefore, null, null));
        }

        [Fact]
        public void Witness_DescribesTheAddedVesselsAndTheirCrew()
        {
            var added = new List<WorldVesselFact>
            {
                Fact(new object(), Guid.NewGuid(), "Rayvan's Shipwreck", true, 4242u,
                    new[] { ShipwreckPartId }, new[] { "Rayvan Kerman" }),
                Fact(new object(), Guid.NewGuid(), "Part", false, 7u, new[] { 1u, 2u })
            };

            Assert.Equal("'Rayvan's Shipwreck' pid=4242 crew=(Rayvan Kerman) parts=1 live, 'Part' pid=7 crew=() parts=2 saved",
                ContractAcceptWorldIndexer.Describe(added));
            Assert.Equal("", ContractAcceptWorldIndexer.Describe(new List<WorldVesselFact>()));
        }

        [Fact]
        public void SecondRebuildInTheSameScene_FindsTheRegeneratedVessel_AndDoesNotRegenerateAgain()
        {
            // After a regeneration the patch rebuilds the index for the next contract (and the
            // drain builds its own). Read from flightState alone, Rayvan's Shipwreck is missing
            // until the next save, and the guard would regenerate him a second time.
            World w = DebrisWorld();
            w.Live.Add(Shipwreck(live: true));

            ContractAcceptWorldEffectPlan savedOnly = PlanRescue(ContractAcceptWorldIndexer.Build(w.Saved, null));
            ContractAcceptWorldEffectPlan withLive = PlanRescue(ContractAcceptWorldIndexer.Build(w.Saved, w.Live));

            Assert.Equal(ContractAcceptRepair.RegenerateStrandedAsset, savedOnly.Repair);
            Assert.Equal(ContractAcceptRepair.None, withLive.Repair);
            Assert.False(withLive.WorldEffectMissing);
            Assert.Equal("TargetPresent", withLive.Outcome);
        }

        private static ContractAcceptWorldEffectPlan PlanRescue(ContractAcceptWorldIndex index)
        {
            var snapshot = new ConfigNode("CONTRACT");
            snapshot.AddValue("type", "RecoverAsset");
            snapshot.AddValue("kerbalName", "Rayvan Kerman");
            snapshot.AddValue("partName", "mk1pod.v2");
            snapshot.AddValue("partID", "4161328136");
            snapshot.AddValue("recoveryLocation", "1");
            snapshot.AddValue("recoveryType", "1");
            var facts = new StrandedAssetWorldFacts
            {
                PartInAnyVessel = id => index.PartFlightIds.Contains(id),
                KerbalAboardAnyVessel = name => index.CrewAboard.Contains(name),
                KerbalInRoster = name => name == "Rayvan Kerman",
                KerbalUnowned = name => name == "Rayvan Kerman",
                KerbalReserved = name => false,
                CommittedResolution = false
            };
            return ContractAcceptWorldEffects.Plan("RecoverAsset", snapshot, facts, null, GameScenes.SPACECENTER);
        }
    }
}
