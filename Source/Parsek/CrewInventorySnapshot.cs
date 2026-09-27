using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Parsek
{
    /// <summary>
    /// Outcome of <see cref="CrewInventorySnapshot.RestoreForSpawn"/>, returned so
    /// callers and tests can read what happened without parsing the log.
    /// </summary>
    internal struct CrewInventoryRestoreResult
    {
        /// <summary>True when the spawn snapshot carried a crew-inventory capture.</summary>
        public bool HadCapture;
        /// <summary>Seated kerbals named by the spawn snapshot's PART crew values.</summary>
        public int SeatedCrew;
        /// <summary>Seated kerbals whose roster inventory was replaced by the recorded one.</summary>
        public int Restored;
        /// <summary>Seated kerbals with no recorded entry (roster inventory kept).</summary>
        public int NoEntry;
        /// <summary>Seated kerbals whose recorded entry could not be applied (roster miss).</summary>
        public int ApplyFailed;
        /// <summary>Recorded entries whose kerbal is not seated in the spawn snapshot.</summary>
        public int Unseated;
        /// <summary>
        /// Each restored kerbal's roster inventory as it was BEFORE the restore (the node may
        /// be null: the roster held no inventory yet). <see cref="CrewInventorySnapshot.RollbackRestore"/>
        /// writes these back when the spawn fails after the restore ran.
        /// </summary>
        public List<KeyValuePair<string, ConfigNode>> Priors;
    }

    /// <summary>
    /// Crew inventory capture and restore for recorded vessel snapshots.
    ///
    /// <para>A crewed kerbal's inventory lives on his roster entry
    /// (<c>ProtoCrewMember.InventoryNode</c>, the roster CREW node's <c>INVENTORY</c>) or,
    /// while its UI is live, on a <c>KerbalInventoryScenario</c> module instance; it is
    /// NOT part of the vessel's PART nodes. An EVA kerbal's part module is reloaded from
    /// the same roster node in <c>ModuleInventoryPart.OnStart</c>, so the EVA vessel's
    /// own module values do not survive a load either. A vessel materialized from a
    /// recording's snapshot therefore used to give each kerbal his CURRENT roster
    /// inventory, duplicating cargo stowed during the flight or losing cargo taken
    /// out of the pod.</para>
    ///
    /// <para>Contract: <see cref="VesselSpawner.TryBackupSnapshot"/> (the single seam
    /// every recorded snapshot passes through) writes one
    /// <c>PARSEK_CREW_INVENTORY</c> child under the VESSEL node:
    /// <c>PARSEK_CREW_INVENTORY { KERBAL { name = X  INVENTORY { ...stock shape... } } }</c>,
    /// keyed by the snapshot's own crew names (so the stand-in reverse map and the KSC
    /// stand-in swap rename the keys with the seats). The node is additive: stock
    /// <c>ProtoVessel</c> ignores unknown VESSEL children and every snapshot copy
    /// (DeepClone, optimizer split / merge, sidecar round-trip) carries it with the
    /// snapshot. The spawn primitives (<see cref="VesselSpawner.RespawnVessel"/>,
    /// <see cref="VesselSpawner.SpawnAtPosition"/>) call <see cref="RestoreForSpawn"/>
    /// just before <c>ProtoVessel</c> construction: each kerbal still seated in the
    /// final spawn node gets the recorded inventory on his roster entry, and the node
    /// is stripped from the spawn copy. A snapshot without the node (recorded before
    /// this capture existed) keeps the roster inventory, as before.</para>
    /// </summary>
    internal static class CrewInventorySnapshot
    {
        internal const string NodeName = "PARSEK_CREW_INVENTORY";
        internal const string KerbalNodeName = "KERBAL";
        internal const string InventoryNodeName = "INVENTORY";
        private const string NameKey = "name";
        private const string Tag = "CrewInventory";

        /// <summary>
        /// Replaces any existing capture on <paramref name="snapshot"/> with the given
        /// name -> INVENTORY pairs. Entries with an empty name or a null inventory are
        /// skipped; the inventory node is deep-copied and renamed to <c>INVENTORY</c>.
        /// Writes the node even when the list is empty. Returns the number of entries written.
        /// </summary>
        internal static int WriteToSnapshot(
            ConfigNode snapshot, IList<KeyValuePair<string, ConfigNode>> inventories)
        {
            if (snapshot == null)
                return 0;

            snapshot.RemoveNodes(NodeName);
            ConfigNode root = snapshot.AddNode(NodeName);
            int written = 0;
            if (inventories == null)
                return 0;

            for (int i = 0; i < inventories.Count; i++)
            {
                string name = inventories[i].Key;
                ConfigNode inventory = inventories[i].Value;
                if (string.IsNullOrEmpty(name) || inventory == null)
                    continue;

                ConfigNode kerbal = root.AddNode(KerbalNodeName);
                kerbal.AddValue(NameKey, name);
                ConfigNode copy = inventory.CreateCopy();
                copy.name = InventoryNodeName;
                kerbal.AddNode(copy);
                written++;
            }
            return written;
        }

        /// <summary>True when the snapshot carries a crew-inventory capture node.</summary>
        internal static bool HasCapture(ConfigNode snapshot)
        {
            return snapshot != null && snapshot.HasNode(NodeName);
        }

        /// <summary>
        /// Reads the capture as name -> INVENTORY node (the live nodes, not copies).
        /// Returns null when the snapshot carries no capture node. A duplicate name
        /// keeps its first entry.
        /// </summary>
        internal static Dictionary<string, ConfigNode> ReadFromSnapshot(ConfigNode snapshot)
        {
            if (snapshot == null)
                return null;
            ConfigNode root = snapshot.GetNode(NodeName);
            if (root == null)
                return null;

            var result = new Dictionary<string, ConfigNode>(StringComparer.Ordinal);
            ConfigNode[] kerbals = root.GetNodes(KerbalNodeName);
            for (int i = 0; i < kerbals.Length; i++)
            {
                string name = kerbals[i].GetValue(NameKey);
                ConfigNode inventory = kerbals[i].GetNode(InventoryNodeName);
                if (string.IsNullOrEmpty(name) || inventory == null)
                    continue;
                if (!result.ContainsKey(name))
                    result[name] = inventory;
            }
            return result;
        }

        /// <summary>
        /// Renames capture keys through <paramref name="resolveNewName"/> (a null or
        /// identical result keeps the key). Used by the stand-in reverse map at capture
        /// time and by the KSC stand-in swap, so the inventory follows the seat's name.
        /// Returns the number of keys renamed.
        /// </summary>
        internal static int RenameKerbals(ConfigNode snapshot, Func<string, string> resolveNewName)
        {
            if (snapshot == null || resolveNewName == null)
                return 0;
            ConfigNode root = snapshot.GetNode(NodeName);
            if (root == null)
                return 0;

            int renamed = 0;
            ConfigNode[] kerbals = root.GetNodes(KerbalNodeName);
            for (int i = 0; i < kerbals.Length; i++)
            {
                string name = kerbals[i].GetValue(NameKey);
                if (string.IsNullOrEmpty(name))
                    continue;
                string newName = resolveNewName(name);
                if (string.IsNullOrEmpty(newName) || string.Equals(newName, name, StringComparison.Ordinal))
                    continue;
                kerbals[i].SetValue(NameKey, newName);
                renamed++;
            }
            return renamed;
        }

        /// <summary>Removes the capture node from a spawn copy. Returns true when one was present.</summary>
        internal static bool StripFromSnapshot(ConfigNode snapshot)
        {
            if (snapshot == null || !snapshot.HasNode(NodeName))
                return false;
            snapshot.RemoveNodes(NodeName);
            return true;
        }

        /// <summary>Seated crew names in PART order (the names the spawn will place).</summary>
        internal static List<string> CollectSeatedCrew(ConfigNode snapshot)
        {
            var names = new List<string>();
            if (snapshot == null)
                return names;
            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                string[] crew = partNode.GetValues("crew");
                for (int i = 0; i < crew.Length; i++)
                {
                    if (!string.IsNullOrEmpty(crew[i]))
                        names.Add(crew[i]);
                }
            }
            return names;
        }

        /// <summary>
        /// Restores the recorded inventory of every kerbal still seated in
        /// <paramref name="spawnNode"/> through <paramref name="applyInventory"/>
        /// (name, fresh INVENTORY copy) -> applied, then strips the capture node from the
        /// spawn copy. Call on the FINAL spawn node (after dead / excluded / duplicate crew
        /// were removed and stand-ins swapped) and before <c>ProtoVessel</c> construction.
        /// No capture node: nothing changes (roster inventory kept) and one line is logged.
        /// </summary>
        internal static CrewInventoryRestoreResult RestoreForSpawn(
            ConfigNode spawnNode,
            Func<string, ConfigNode, bool> applyInventory,
            string context)
        {
            return RestoreForSpawn(spawnNode, null, applyInventory, context);
        }

        /// <summary>
        /// <see cref="RestoreForSpawn(ConfigNode, Func{string, ConfigNode, bool}, string)"/> that
        /// also records each restored kerbal's prior roster inventory through
        /// <paramref name="readPrior"/> (read before applying) for <see cref="RollbackRestore"/>.
        /// </summary>
        internal static CrewInventoryRestoreResult RestoreForSpawn(
            ConfigNode spawnNode,
            Func<string, ConfigNode> readPrior,
            Func<string, ConfigNode, bool> applyInventory,
            string context)
        {
            var result = new CrewInventoryRestoreResult
            {
                Priors = new List<KeyValuePair<string, ConfigNode>>()
            };
            if (spawnNode == null)
                return result;

            List<string> seated = CollectSeatedCrew(spawnNode);
            result.SeatedCrew = seated.Count;
            Dictionary<string, ConfigNode> recorded = ReadFromSnapshot(spawnNode);
            string vesselName = spawnNode.GetValue("name") ?? "(unnamed)";

            if (recorded == null)
            {
                if (seated.Count > 0)
                {
                    ParsekLog.Info(Tag,
                        $"RestoreForSpawn: no recorded crew inventory on '{vesselName}' " +
                        $"(snapshot predates the capture); roster inventory kept for " +
                        $"{seated.Count} kerbal(s) ({context ?? "no-context"})");
                }
                return result;
            }

            result.HadCapture = true;
            var seatedSet = new HashSet<string>(seated, StringComparer.Ordinal);
            for (int i = 0; i < seated.Count; i++)
            {
                string name = seated[i];
                if (!recorded.TryGetValue(name, out ConfigNode inventory))
                {
                    result.NoEntry++;
                    ParsekLog.Verbose(Tag,
                        $"RestoreForSpawn: '{name}' has no recorded inventory on '{vesselName}'; " +
                        $"roster inventory kept ({context ?? "no-context"})");
                    continue;
                }

                bool applied = false;
                try
                {
                    // Read the prior state BEFORE applying, so a failed spawn can put it back.
                    ConfigNode prior = readPrior != null ? readPrior(name) : null;
                    applied = applyInventory != null && applyInventory(name, inventory.CreateCopy());
                    if (applied)
                        result.Priors.Add(new KeyValuePair<string, ConfigNode>(name, prior));
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(Tag,
                        $"RestoreForSpawn: applying '{name}' inventory threw {ex.GetType().Name}: {ex.Message}");
                }

                if (applied)
                    result.Restored++;
                else
                    result.ApplyFailed++;
            }

            foreach (string name in recorded.Keys)
            {
                if (!seatedSet.Contains(name))
                    result.Unseated++;
            }

            StripFromSnapshot(spawnNode);

            ParsekLog.Info(Tag,
                $"RestoreForSpawn: '{vesselName}' seated={result.SeatedCrew} restored={result.Restored} " +
                $"noEntry={result.NoEntry} applyFailed={result.ApplyFailed} unseated={result.Unseated} " +
                $"({context ?? "no-context"})");
            return result;
        }

        /// <summary>
        /// Live restore entry point for the spawn primitives: <see cref="RestoreForSpawn"/>
        /// with the roster applier.
        /// </summary>
        internal static CrewInventoryRestoreResult RestoreForSpawnLive(ConfigNode spawnNode, string context)
        {
            return RestoreForSpawn(spawnNode, ReadRosterPrior, ApplyToRosterKerbal, context);
        }

        /// <summary>
        /// Puts every restored kerbal's pre-restore roster inventory back through
        /// <paramref name="writePrior"/> (name, prior node or null). Called on the spawn
        /// primitives' failure paths after <see cref="RestoreForSpawn"/> ran, so a spawn
        /// that never produced a vessel does not leave the recorded inventory on the
        /// roster. Returns the number of kerbals rolled back.
        /// </summary>
        internal static int RollbackRestore(
            CrewInventoryRestoreResult result,
            Func<string, ConfigNode, bool> writePrior,
            string context)
        {
            if (result.Priors == null || result.Priors.Count == 0 || writePrior == null)
                return 0;

            int rolledBack = 0;
            int failed = 0;
            for (int i = 0; i < result.Priors.Count; i++)
            {
                bool ok = false;
                try
                {
                    ok = writePrior(result.Priors[i].Key, result.Priors[i].Value);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(Tag,
                        $"RollbackRestore: '{result.Priors[i].Key}' threw {ex.GetType().Name}: {ex.Message}");
                }
                if (ok) rolledBack++; else failed++;
            }
            ParsekLog.Info(Tag,
                $"RollbackRestore: spawn failed after the crew inventory restore; rolled back " +
                $"{rolledBack} kerbal(s), failed={failed} ({context ?? "no-context"})");
            return rolledBack;
        }

        /// <summary>Live rollback entry point: <see cref="RollbackRestore"/> with the roster writer.</summary>
        internal static int RollbackRestoreLive(CrewInventoryRestoreResult result, string context)
        {
            return RollbackRestore(result, WriteRosterPrior, context);
        }

        /// <summary>
        /// Captures every crew member's inventory from the live vessel into
        /// <paramref name="snapshot"/>, keyed by live kerbal name (the caller's stand-in
        /// reverse map renames the keys afterwards). Never throws: a failure logs and
        /// leaves the snapshot without a capture (spawn then keeps the roster inventory).
        /// Returns the number of kerbals captured, or -1 on failure.
        /// </summary>
        internal static int CaptureFromLiveVessel(Vessel vessel, ConfigNode snapshot)
        {
            if (vessel == null || snapshot == null)
                return 0;
            try
            {
                return CaptureFromLiveVesselCore(vessel, snapshot);
            }
            catch (Exception ex)
            {
                snapshot.RemoveNodes(NodeName);
                ParsekLog.Warn(Tag,
                    $"CaptureFromLiveVessel: failed for pid={vessel.persistentId}: " +
                    $"{ex.GetType().Name}: {ex.Message}; snapshot keeps no crew inventory");
                return -1;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int CaptureFromLiveVesselCore(Vessel vessel, ConfigNode snapshot)
        {
            List<ProtoCrewMember> crew = vessel.GetVesselCrew();
            var entries = new List<KeyValuePair<string, ConfigNode>>();
            int fromLiveModule = 0;
            if (crew != null)
            {
                for (int i = 0; i < crew.Count; i++)
                {
                    ProtoCrewMember pcm = crew[i];
                    if (pcm == null || string.IsNullOrEmpty(pcm.name))
                        continue;
                    ConfigNode inventory = CaptureOne(vessel, pcm, out bool liveModule);
                    if (inventory == null)
                        continue;
                    if (liveModule)
                        fromLiveModule++;
                    entries.Add(new KeyValuePair<string, ConfigNode>(pcm.name, inventory));
                }
            }

            if (entries.Count == 0)
            {
                // Crewless vessel: no capture node, so probe and debris snapshots stay unchanged.
                snapshot.RemoveNodes(NodeName);
                return 0;
            }

            int written = WriteToSnapshot(snapshot, entries);
            if (written > 0)
            {
                // TryBackupSnapshot runs on every periodic snapshot refresh, so this is
                // rate-limited per vessel rather than logged per call.
                ParsekLog.VerboseRateLimited(Tag, "capture-" + vessel.persistentId,
                    () => $"CaptureFromLiveVessel: captured {written} crew inventory(ies) " +
                          $"({fromLiveModule} from live module) for pid={vessel.persistentId} " +
                          $"vessel='{vessel.vesselName ?? "(unknown)"}'");
            }
            return written;
        }

        /// <summary>
        /// The authoritative current inventory of one kerbal, in the shape stock
        /// persists (<c>ProtoCrewMember.SaveInventory</c>): a loaded EVA kerbal's own part
        /// module first, then a live <c>KerbalInventoryScenario</c> instance (which stock
        /// writes back to the roster only on save), then the roster node.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ConfigNode CaptureOne(Vessel vessel, ProtoCrewMember pcm, out bool liveModule)
        {
            ConfigNode liveNode = ReadLiveModuleInventory(vessel, pcm);
            liveModule = liveNode != null;
            return SelectCapturedInventory(liveNode, ReadRosterBackingField(pcm));
        }

        /// <summary>
        /// Pure capture decision: a live module's saved node wins; otherwise a COPY of the
        /// roster's backing node; a null backing node (the kerbal never had an inventory
        /// materialized) records NO entry, so the restore leaves that kerbal alone. The
        /// capture never reads <c>ProtoCrewMember.InventoryNode</c>, whose getter writes a
        /// default inventory onto the roster when the backing field is null.
        /// </summary>
        internal static ConfigNode SelectCapturedInventory(ConfigNode liveModuleNode, ConfigNode rosterBackingNode)
        {
            if (liveModuleNode != null)
                return liveModuleNode;
            return rosterBackingNode != null ? rosterBackingNode.CreateCopy() : null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ConfigNode ReadLiveModuleInventory(Vessel vessel, ProtoCrewMember pcm)
        {
            if (vessel.loaded && vessel.isEVA && vessel.parts != null)
            {
                for (int p = 0; p < vessel.parts.Count; p++)
                {
                    Part part = vessel.parts[p];
                    if (part == null || part.protoModuleCrew == null || !part.protoModuleCrew.Contains(pcm))
                        continue;
                    ModuleInventoryPart module = part.FindModuleImplementing<ModuleInventoryPart>();
                    if (module != null)
                    {
                        var node = new ConfigNode(InventoryNodeName);
                        module.Save(node);
                        return node;
                    }
                }
            }

            return ReadScenarioInstanceInventory(pcm);
        }

        /// <summary>
        /// A live <c>KerbalInventoryScenario</c> instance's contents, or null when none
        /// exists. The instance check comes first so <c>KerbalInventoryModule</c>'s getter
        /// (which would create an instance) only ever returns the existing one.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ConfigNode ReadScenarioInstanceInventory(ProtoCrewMember pcm)
        {
            KerbalInventoryScenario scenario = KerbalInventoryScenario.Instance;
            if (scenario == null || !scenario.ContainsCrew(pcm.name))
                return null;
            ModuleInventoryPart module = pcm.KerbalInventoryModule;
            if (module == null)
                return null;
            var node = new ConfigNode(InventoryNodeName);
            module.Save(node);
            return node;
        }

        // ProtoCrewMember's private backing field behind InventoryNode (KSP 1.12.5,
        // decompiled: private ConfigNode inventoryNode). Read directly because the property
        // getter lazily runs SetDefaultInventory, which would mutate the roster on every
        // snapshot refresh; written directly on rollback so a prior null is restored as null.
        private static System.Reflection.FieldInfo inventoryBackingField;
        private static bool inventoryBackingFieldResolved;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static System.Reflection.FieldInfo ResolveInventoryBackingField()
        {
            if (!inventoryBackingFieldResolved)
            {
                inventoryBackingFieldResolved = true;
                inventoryBackingField = HarmonyLib.AccessTools.Field(typeof(ProtoCrewMember), "inventoryNode");
                if (inventoryBackingField == null || inventoryBackingField.FieldType != typeof(ConfigNode))
                {
                    inventoryBackingField = null;
                    ParsekLog.Warn(Tag,
                        "ProtoCrewMember.inventoryNode field not found; crew inventories are not " +
                        "captured from the roster and a failed spawn cannot roll one back");
                }
            }
            return inventoryBackingField;
        }

        /// <summary>The roster's stored inventory without the lazy default (null when none).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ConfigNode ReadRosterBackingField(ProtoCrewMember pcm)
        {
            System.Reflection.FieldInfo field = ResolveInventoryBackingField();
            if (field == null || pcm == null)
                return null;
            return field.GetValue(pcm) as ConfigNode;
        }

        /// <summary>
        /// Prior-state reader for the restore: a live instance's contents (the authoritative
        /// current state, which the applier drops), else a copy of the backing node, else
        /// null. Never runs the lazy default.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ConfigNode ReadRosterPrior(string name)
        {
            KerbalRoster roster = HighLogic.CurrentGame?.CrewRoster;
            ProtoCrewMember pcm = roster != null ? roster[name] : null;
            if (pcm == null)
                return null;
            ConfigNode live = ReadScenarioInstanceInventory(pcm);
            if (live != null)
                return live;
            ConfigNode backing = ReadRosterBackingField(pcm);
            return backing != null ? backing.CreateCopy() : null;
        }

        /// <summary>
        /// Rollback writer: sets the backing field to <paramref name="prior"/> (null allowed)
        /// and drops any live instance so it rebuilds from the restored field.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool WriteRosterPrior(string name, ConfigNode prior)
        {
            KerbalRoster roster = HighLogic.CurrentGame?.CrewRoster;
            ProtoCrewMember pcm = roster != null ? roster[name] : null;
            System.Reflection.FieldInfo field = ResolveInventoryBackingField();
            if (pcm == null || field == null)
                return false;
            field.SetValue(pcm, prior);
            KerbalInventoryScenario scenario = KerbalInventoryScenario.Instance;
            if (scenario != null && scenario.ContainsCrew(name))
                scenario.RemoveKerbalInventoryInstance(name);
            ParsekLog.Verbose(Tag,
                $"WriteRosterPrior: '{name}' roster inventory rolled back (null={prior == null}, " +
                $"storedParts={CountStoredParts(prior)})");
            return true;
        }

        /// <summary>
        /// Roster applier: replaces the named kerbal's roster inventory with
        /// <paramref name="inventory"/> and drops any live <c>KerbalInventoryScenario</c>
        /// instance so it neither shows the old contents nor writes them back on the
        /// next save (the same drop stock performs after an EVA board). Returns false
        /// when the roster or the kerbal is unavailable.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool ApplyToRosterKerbal(string name, ConfigNode inventory)
        {
            if (string.IsNullOrEmpty(name) || inventory == null)
                return false;
            KerbalRoster roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
            {
                ParsekLog.Warn(Tag, $"ApplyToRosterKerbal: crew roster unavailable; '{name}' not restored");
                return false;
            }
            ProtoCrewMember pcm = roster[name];
            if (pcm == null)
            {
                ParsekLog.Warn(Tag, $"ApplyToRosterKerbal: '{name}' not in roster; inventory not restored");
                return false;
            }

            inventory.name = InventoryNodeName;
            pcm.InventoryNode = inventory;

            bool droppedLiveInstance = false;
            KerbalInventoryScenario scenario = KerbalInventoryScenario.Instance;
            if (scenario != null && scenario.ContainsCrew(name))
            {
                scenario.RemoveKerbalInventoryInstance(name);
                droppedLiveInstance = true;
            }

            ParsekLog.Verbose(Tag,
                $"ApplyToRosterKerbal: '{name}' roster inventory replaced " +
                $"(storedParts={CountStoredParts(inventory)}, droppedLiveInstance={droppedLiveInstance})");
            return true;
        }

        /// <summary>Number of STOREDPART entries in an INVENTORY node (for logs and tests).</summary>
        internal static int CountStoredParts(ConfigNode inventory)
        {
            ConfigNode stored = inventory?.GetNode("STOREDPARTS");
            return stored != null ? stored.GetNodes("STOREDPART").Length : 0;
        }
    }
}
