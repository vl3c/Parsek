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
    /// <para>Both cells self-skip without a crewed active vessel. The harness lane
    /// <c>H72-kerbal-inventory-spawn</c> pins this category's <c>BATCH_COMPLETE</c>
    /// <c>total=</c>, so adding a cell here moves that pin in the same commit.</para>
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
            Description = "A backed-up snapshot captures each stored crew inventory without writing a default onto the roster")]
        public void SnapshotCapturesEverySeatedKerbalsInventory()
        {
            if (!TryGetCrewedActiveVessel(out Vessel vessel, out List<ProtoCrewMember> crew))
            {
                InGameAssert.Skip("Needs a crewed active vessel (fly a crewed craft, then rerun).");
                return;
            }

            // A kerbal with no stored inventory (null roster backing field) records no entry;
            // a loaded EVA kerbal always does (its own module is read).
            var storedBefore = new Dictionary<string, bool>();
            int expected = 0;
            for (int i = 0; i < crew.Count; i++)
            {
                bool stored = CrewInventorySnapshot.ReadRosterPrior(crew[i].name) != null;
                storedBefore[crew[i].name] = stored;
                if (stored || vessel.isEVA) expected++;
            }
            if (expected == 0)
            {
                InGameAssert.Skip("No crew member of the active vessel has a stored inventory yet " +
                    "(open a kerbal's inventory or fly a craft launched from the editor, then rerun).");
                return;
            }

            ConfigNode snapshot = VesselSpawner.TryBackupSnapshot(vessel);
            InGameAssert.IsNotNull(snapshot, "TryBackupSnapshot returned null for the active vessel");
            Dictionary<string, ConfigNode> captured = CrewInventorySnapshot.ReadFromSnapshot(snapshot);
            InGameAssert.IsNotNull(captured,
                $"snapshot of '{vessel.vesselName}' carries no {CrewInventorySnapshot.NodeName} node");
            InGameAssert.AreEqual(expected, captured.Count, "captured entries vs crew with a stored inventory");

            // Keys follow the snapshot's own (stand-in reverse-mapped) crew names.
            var seated = new HashSet<string>(CrewInventorySnapshot.CollectSeatedCrew(snapshot));
            foreach (var kvp in captured)
            {
                InGameAssert.IsTrue(seated.Contains(kvp.Key), $"captured key '{kvp.Key}' is not a seated kerbal");
                InGameAssert.AreEqual(CrewInventorySnapshot.InventoryNodeName, kvp.Value.name,
                    $"captured node name for '{kvp.Key}'");
            }

            // The capture must not write a default inventory onto a kerbal who had none.
            for (int i = 0; i < crew.Count; i++)
            {
                if (storedBefore[crew[i].name]) continue;
                InGameAssert.IsNull(CrewInventorySnapshot.ReadRosterPrior(crew[i].name),
                    $"capture mutated '{crew[i].name}''s roster inventory");
            }
        }

        [InGameTest(Category = "KerbalInventorySpawn", Scene = GameScenes.FLIGHT,
            Description = "The spawn restore replaces a kerbal's roster inventory, drops the live instance, and the rollback puts the prior back")]
        public void RosterApplyReplacesTheRosterInventory()
        {
            if (!TryGetCrewedActiveVessel(out Vessel vessel, out List<ProtoCrewMember> crew))
            {
                InGameAssert.Skip("Needs a crewed active vessel (fly a crewed craft, then rerun).");
                return;
            }

            ProtoCrewMember pcm = crew[0];
            // The prior state through the same reader the spawn rollback uses (a live
            // instance's contents, else the roster backing field, else null) - never the
            // InventoryNode getter, which writes a default inventory when none exists.
            ConfigNode prior = CrewInventorySnapshot.ReadRosterPrior(pcm.name);
            int priorCount = CrewInventorySnapshot.CountStoredParts(prior);

            try
            {
                bool applied = CrewInventorySnapshot.ApplyToRosterKerbal(pcm.name, new ConfigNode("INVENTORY"));
                InGameAssert.IsTrue(applied, $"ApplyToRosterKerbal refused '{pcm.name}'");
                ConfigNode after = CrewInventorySnapshot.ReadRosterPrior(pcm.name);
                InGameAssert.IsNotNull(after, "roster inventory is null after the restore");
                InGameAssert.AreEqual(0, CrewInventorySnapshot.CountStoredParts(after),
                    "roster inventory after applying an empty recorded inventory");
                KerbalInventoryScenario scenario = KerbalInventoryScenario.Instance;
                if (scenario != null)
                    InGameAssert.IsFalse(scenario.ContainsCrew(pcm.name),
                        "live KerbalInventoryScenario instance survived the restore");
            }
            finally
            {
                // The spawn-failure rollback writer: restores the prior node, null included.
                CrewInventorySnapshot.WriteRosterPrior(pcm.name, prior);
            }

            ConfigNode restored = CrewInventorySnapshot.ReadRosterPrior(pcm.name);
            InGameAssert.AreEqual(prior == null, restored == null, "rollback preserved a null prior");
            InGameAssert.AreEqual(priorCount, CrewInventorySnapshot.CountStoredParts(restored),
                "roster inventory after rolling back to the prior state");
        }
    }
}
