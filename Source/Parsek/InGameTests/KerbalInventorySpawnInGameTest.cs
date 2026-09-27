using System.Collections.Generic;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Runtime checks for crew inventory capture and restore (<see cref="CrewInventorySnapshot"/>).
    ///
    /// <para>Why in-game: the two live halves read and write stock state that cannot exist
    /// headlessly - the capture walks the vessel's <c>ProtoCrewMember</c>s (roster
    /// <c>InventoryNode</c>, live <c>KerbalInventoryScenario</c> instances, a loaded EVA
    /// kerbal's own <c>ModuleInventoryPart</c>), and the restore writes a roster entry and
    /// drops the live instance. The pure codec, stand-in renames, carry-over and restore
    /// planning are covered by <c>CrewInventorySnapshotTests</c>.</para>
    ///
    /// <para>Both cells self-skip without a crewed active vessel. The category is new and
    /// no committed harness spec pins it, so no <c>BATCH_COMPLETE</c> tally moves.</para>
    /// </summary>
    public class KerbalInventorySpawnInGameTest
    {
        private static bool TryGetCrewedActiveVessel(out Vessel vessel, out List<ProtoCrewMember> crew)
        {
            vessel = FlightGlobals.ActiveVessel;
            crew = vessel != null ? vessel.GetVesselCrew() : null;
            return vessel != null && crew != null && crew.Count > 0;
        }

        [InGameTest(Category = "KerbalInventorySpawn", Scene = GameScenes.FLIGHT,
            Description = "A backed-up snapshot carries one captured inventory per seated kerbal")]
        public void SnapshotCapturesEverySeatedKerbalsInventory()
        {
            if (!TryGetCrewedActiveVessel(out Vessel vessel, out List<ProtoCrewMember> crew))
            {
                InGameAssert.Skip("Needs a crewed active vessel (fly a crewed craft, then rerun).");
                return;
            }

            ConfigNode snapshot = VesselSpawner.TryBackupSnapshot(vessel);
            InGameAssert.IsNotNull(snapshot, "TryBackupSnapshot returned null for the active vessel");
            Dictionary<string, ConfigNode> captured = CrewInventorySnapshot.ReadFromSnapshot(snapshot);
            InGameAssert.IsNotNull(captured,
                $"snapshot of '{vessel.vesselName}' carries no {CrewInventorySnapshot.NodeName} node");

            // Keys follow the snapshot's own (stand-in reverse-mapped) crew names, so compare
            // against the seated names in the snapshot, not the live roster names.
            List<string> seated = CrewInventorySnapshot.CollectSeatedCrew(snapshot);
            InGameAssert.AreEqual(crew.Count, seated.Count, "seated crew in snapshot vs live crew count");
            for (int i = 0; i < seated.Count; i++)
            {
                InGameAssert.IsTrue(captured.ContainsKey(seated[i]),
                    $"no captured inventory for seated kerbal '{seated[i]}'");
                InGameAssert.AreEqual(CrewInventorySnapshot.InventoryNodeName, captured[seated[i]].name,
                    $"captured node name for '{seated[i]}'");
            }
        }

        [InGameTest(Category = "KerbalInventorySpawn", Scene = GameScenes.FLIGHT,
            Description = "The spawn restore replaces a kerbal's roster inventory and drops the live instance")]
        public void RosterApplyReplacesTheRosterInventory()
        {
            if (!TryGetCrewedActiveVessel(out Vessel vessel, out List<ProtoCrewMember> crew))
            {
                InGameAssert.Skip("Needs a crewed active vessel (fly a crewed craft, then rerun).");
                return;
            }

            ProtoCrewMember pcm = crew[0];
            ConfigNode snapshot = VesselSpawner.TryBackupSnapshot(vessel);
            Dictionary<string, ConfigNode> captured = CrewInventorySnapshot.ReadFromSnapshot(snapshot);
            ConfigNode original = null;
            if (captured != null)
            {
                foreach (var kvp in captured)
                {
                    // The live name may be a stand-in whose key was reverse-mapped; fall back
                    // to the roster node when the capture key differs.
                    if (kvp.Key == pcm.name) original = kvp.Value.CreateCopy();
                }
            }
            if (original == null)
                original = pcm.InventoryNode != null ? pcm.InventoryNode.CreateCopy() : new ConfigNode("INVENTORY");
            int originalCount = CrewInventorySnapshot.CountStoredParts(original);

            try
            {
                bool applied = CrewInventorySnapshot.ApplyToRosterKerbal(pcm.name, new ConfigNode("INVENTORY"));
                InGameAssert.IsTrue(applied, $"ApplyToRosterKerbal refused '{pcm.name}'");
                InGameAssert.AreEqual(0, CrewInventorySnapshot.CountStoredParts(pcm.InventoryNode),
                    "roster inventory after applying an empty recorded inventory");
                KerbalInventoryScenario scenario = KerbalInventoryScenario.Instance;
                if (scenario != null)
                    InGameAssert.IsFalse(scenario.ContainsCrew(pcm.name),
                        "live KerbalInventoryScenario instance survived the restore");
            }
            finally
            {
                CrewInventorySnapshot.ApplyToRosterKerbal(pcm.name, original);
            }

            InGameAssert.AreEqual(originalCount, CrewInventorySnapshot.CountStoredParts(pcm.InventoryNode),
                "roster inventory after restoring the captured original");
        }
    }
}
