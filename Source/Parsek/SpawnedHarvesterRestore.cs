using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Keeps a Parsek-spawned miner mining (MINE-7 / MINE-8, 2026-10-17). A drill that was
    /// running in a recording's end snapshot spawns running, but stock
    /// <c>ModuleResourceHarvester.PrepareRecipe</c> switches it off on any frame where its
    /// ground-contact raycast misses at normal time rate (decompiled, KSP 1.12.5:
    /// <c>!CheckForImpact() &amp;&amp; deltaTime / 2 &lt; fixedDeltaTime</c> sets
    /// <c>IsActivated = false</c>). A landed spawn is held above the ground while KSP waits for
    /// ground contact (and Parsek's landed clamp can lift it a little), so the drill turns
    /// itself off before the vessel settles and never mines again. This restores, once the
    /// spawned vessel is loaded, off rails and has stood landed for a moment, every harvester
    /// that the snapshot had running and that is now off. A drill that really cannot reach the
    /// ground is switched off again by stock on its next frame.
    /// A save taken before the restore (leaving the scene right after the spawn, a quicksave)
    /// would otherwise write the drills off, and the KSC reloads persistent.sfs from disk, so
    /// <see cref="CarryIntoSavedFlightState"/> marks them running in the saved snapshot too.
    /// </summary>
    internal static class SpawnedHarvesterRestore
    {
        private const string Tag = "Spawner";
        internal const string HarvesterModuleName = "ModuleResourceHarvester";
        internal const double SettleSeconds = 2.0;
        internal const double GiveUpSeconds = 120.0;
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

        /// <summary>One running harvester in a spawn snapshot: its part, and which harvester
        /// module of that part it is (the n-th <c>ModuleResourceHarvester</c>).</summary>
        internal struct HarvesterKey
        {
            internal int PartIndex;
            internal uint PartPersistentId;
            internal int HarvesterOrdinal;
        }

        internal enum Step
        {
            Wait,
            Restore,
            GiveUp
        }

        /// <summary>One module of a saved part: its class name and its saved values.</summary>
        internal struct SavedModule
        {
            internal string Name;
            internal ConfigNode Values;
        }

        private sealed class Pending
        {
            internal uint VesselPid;
            internal Guid VesselGuid;
            internal string VesselName;
            internal List<HarvesterKey> Harvesters;
            internal double ArmedAt;
            internal double SettledSince = double.NaN;
        }

        private static readonly List<Pending> pending = new List<Pending>();

        internal static int PendingCount => pending.Count;

        internal static void ResetForTesting()
        {
            pending.Clear();
        }

        /// <summary>Pure: the harvesters a VESSEL node has running (<c>IsActivated = True</c>).</summary>
        internal static List<HarvesterKey> CollectActiveHarvesters(ConfigNode vesselNode)
        {
            var result = new List<HarvesterKey>();
            if (vesselNode == null) return result;
            ConfigNode[] parts = vesselNode.GetNodes("PART");
            for (int pi = 0; pi < parts.Length; pi++)
            {
                uint pid;
                uint.TryParse(parts[pi].GetValue("persistentId"), NumberStyles.Integer, ic, out pid);
                ConfigNode[] modules = parts[pi].GetNodes("MODULE");
                int ordinal = 0;
                for (int m = 0; m < modules.Length; m++)
                {
                    if (!string.Equals(modules[m].GetValue("name"), HarvesterModuleName, StringComparison.Ordinal))
                        continue;
                    bool active;
                    if (bool.TryParse(modules[m].GetValue("IsActivated"), out active) && active)
                        result.Add(new HarvesterKey { PartIndex = pi, PartPersistentId = pid, HarvesterOrdinal = ordinal });
                    ordinal++;
                }
            }
            return result;
        }

        /// <summary>
        /// Pure: what to do this frame for one armed vessel. Waits while it is not loaded, on
        /// rails or not landed (restarting the settle clock), restores after
        /// <see cref="SettleSeconds"/> landed off rails, gives up after
        /// <see cref="GiveUpSeconds"/> or when the vessel is gone.
        /// </summary>
        internal static Step Decide(bool vesselFound, bool loaded, bool packed, bool landed,
            double now, double armedAt, ref double settledSince)
        {
            if (!vesselFound || now - armedAt > GiveUpSeconds)
                return Step.GiveUp;
            if (!loaded || packed || !landed)
            {
                settledSince = double.NaN;
                return Step.Wait;
            }
            if (double.IsNaN(settledSince))
            {
                settledSince = now;
                return Step.Wait;
            }
            return now - settledSince >= SettleSeconds ? Step.Restore : Step.Wait;
        }

        /// <summary>Runtime: arms a just-spawned vessel whose snapshot had harvesters running.</summary>
        internal static void Arm(Vessel v, List<HarvesterKey> harvesters, string context)
        {
            if (v == null || harvesters == null || harvesters.Count == 0) return;
            pending.RemoveAll(p => p.VesselPid == v.persistentId);
            pending.Add(new Pending
            {
                VesselPid = v.persistentId,
                VesselGuid = v.id,
                VesselName = Recording.ResolveLocalizedName(v.vesselName),
                Harvesters = harvesters,
                ArmedAt = UnityEngine.Time.realtimeSinceStartup
            });
            ParsekLog.Verbose(Tag,
                string.Format(ic, "Spawned harvester restore armed: {0} vessel={1} pid={2} harvesters={3}",
                    context ?? "?", Recording.ResolveLocalizedName(v.vesselName), v.persistentId, harvesters.Count));
        }

        /// <summary>Runtime, once per frame in FLIGHT.</summary>
        internal static void Tick()
        {
            if (pending.Count == 0) return;
            double now = UnityEngine.Time.realtimeSinceStartup;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                Pending p = pending[i];
                Vessel v = FindVessel(p.VesselPid);
                Step step = Decide(v != null, v != null && v.loaded, v != null && v.packed,
                    v != null && v.Landed, now, p.ArmedAt, ref p.SettledSince);
                if (step == Step.Wait) continue;
                pending.RemoveAt(i);
                if (step == Step.GiveUp)
                {
                    ParsekLog.Info(Tag,
                        string.Format(ic,
                            "Spawned harvester restore given up: vessel={0} pid={1} found={2} harvesters={3}",
                            p.VesselName, p.VesselPid, v != null, p.Harvesters.Count));
                    continue;
                }
                RestoreOnVessel(v, p);
            }
        }

        /// <summary>
        /// Pure: marks each noted harvester running in a saved vessel's part modules
        /// (<paramref name="partPids"/> and <paramref name="partModules"/> are parallel, one entry
        /// per part in save order). Matches a part by persistent id, else by index.
        /// </summary>
        internal static void MarkSavedHarvestersActive(List<uint> partPids, List<List<SavedModule>> partModules,
            List<HarvesterKey> harvesters, out int marked, out int alreadyOn, out int missing)
        {
            marked = 0;
            alreadyOn = 0;
            missing = 0;
            if (harvesters == null) return;
            for (int h = 0; h < harvesters.Count; h++)
            {
                HarvesterKey key = harvesters[h];
                int partIndex = partPids == null ? -1 : partPids.IndexOf(key.PartPersistentId);
                if (partIndex < 0 && partModules != null && key.PartIndex >= 0 && key.PartIndex < partModules.Count)
                    partIndex = key.PartIndex;
                ConfigNode values = null;
                if (partIndex >= 0 && partModules != null && partIndex < partModules.Count && partModules[partIndex] != null)
                {
                    int ordinal = 0;
                    List<SavedModule> modules = partModules[partIndex];
                    for (int m = 0; m < modules.Count; m++)
                    {
                        if (!string.Equals(modules[m].Name, HarvesterModuleName, StringComparison.Ordinal))
                            continue;
                        if (ordinal == key.HarvesterOrdinal)
                        {
                            values = modules[m].Values;
                            break;
                        }
                        ordinal++;
                    }
                }
                if (values == null)
                {
                    missing++;
                    continue;
                }
                bool active;
                if (bool.TryParse(values.GetValue("IsActivated"), out active) && active)
                {
                    alreadyOn++;
                    continue;
                }
                if (values.HasValue("IsActivated"))
                    values.SetValue("IsActivated", "True");
                else
                    values.AddValue("IsActivated", "True");
                marked++;
            }
        }

        /// <summary>
        /// Runtime, from <c>ParsekScenario.OnSave</c>: stock builds the game's FLIGHTSTATE from the
        /// live vessels before it asks the scenario modules to save, and writes the file after,
        /// so marking a pending vessel's harvesters running here puts them running in the save.
        /// The live modules are left to <see cref="Tick"/>.
        /// </summary>
        internal static void CarryIntoSavedFlightState(string context)
        {
            if (pending.Count == 0) return;
            FlightState flightState = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.flightState : null;
            if (flightState == null || flightState.protoVessels == null) return;
            for (int i = 0; i < pending.Count; i++)
            {
                Pending p = pending[i];
                ProtoVessel saved = null;
                for (int v = 0; v < flightState.protoVessels.Count; v++)
                {
                    ProtoVessel pv = flightState.protoVessels[v];
                    if (pv != null && pv.persistentId == p.VesselPid && pv.vesselID == p.VesselGuid)
                    {
                        saved = pv;
                        break;
                    }
                }
                if (saved == null || saved.protoPartSnapshots == null)
                {
                    ParsekLog.Verbose(Tag,
                        string.Format(ic, "Spawned harvester restore saved: context={0} vessel={1} pid={2} not in flight state",
                            context ?? "?", p.VesselName, p.VesselPid));
                    continue;
                }
                var partPids = new List<uint>(saved.protoPartSnapshots.Count);
                var partModules = new List<List<SavedModule>>(saved.protoPartSnapshots.Count);
                for (int pi = 0; pi < saved.protoPartSnapshots.Count; pi++)
                {
                    ProtoPartSnapshot part = saved.protoPartSnapshots[pi];
                    partPids.Add(part != null ? part.persistentId : 0u);
                    var modules = new List<SavedModule>();
                    if (part != null && part.modules != null)
                    {
                        for (int m = 0; m < part.modules.Count; m++)
                        {
                            ProtoPartModuleSnapshot module = part.modules[m];
                            if (module == null) continue;
                            if (module.moduleValues == null) module.moduleValues = new ConfigNode("MODULE");
                            modules.Add(new SavedModule { Name = module.moduleName, Values = module.moduleValues });
                        }
                    }
                    partModules.Add(modules);
                }
                int marked, alreadyOn, missing;
                MarkSavedHarvestersActive(partPids, partModules, p.Harvesters, out marked, out alreadyOn, out missing);
                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Spawned harvester restore saved: context={0} vessel={1} pid={2} marked={3} alreadyOn={4} missing={5}",
                        context ?? "?", p.VesselName, p.VesselPid, marked, alreadyOn, missing));
            }
        }

        internal static void Clear(string reason)
        {
            if (pending.Count == 0) return;
            ParsekLog.Verbose(Tag,
                string.Format(ic, "Spawned harvester restore cleared: count={0} reason={1}", pending.Count, reason ?? "?"));
            pending.Clear();
        }

        private static Vessel FindVessel(uint pid)
        {
            if (FlightGlobals.Vessels == null) return null;
            for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
            {
                Vessel v = FlightGlobals.Vessels[i];
                if (v != null && v.persistentId == pid) return v;
            }
            return null;
        }

        private static void RestoreOnVessel(Vessel v, Pending p)
        {
            int restored = 0, alreadyOn = 0, missing = 0;
            for (int h = 0; h < p.Harvesters.Count; h++)
            {
                HarvesterKey key = p.Harvesters[h];
                ModuleResourceHarvester harvester = FindHarvester(v, key);
                if (harvester == null)
                {
                    missing++;
                    continue;
                }
                if (harvester.IsActivated)
                {
                    alreadyOn++;
                    continue;
                }
                harvester.StartResourceConverter();
                restored++;
            }
            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Spawned harvester restore: vessel={0} pid={1} restored={2} alreadyOn={3} missing={4}",
                    p.VesselName, p.VesselPid, restored, alreadyOn, missing));
        }

        private static ModuleResourceHarvester FindHarvester(Vessel v, HarvesterKey key)
        {
            if (v.parts == null) return null;
            Part part = null;
            for (int i = 0; i < v.parts.Count; i++)
            {
                if (v.parts[i] != null && v.parts[i].persistentId == key.PartPersistentId)
                {
                    part = v.parts[i];
                    break;
                }
            }
            if (part == null && key.PartIndex >= 0 && key.PartIndex < v.parts.Count)
                part = v.parts[key.PartIndex];
            if (part == null || part.Modules == null) return null;
            int ordinal = 0;
            for (int m = 0; m < part.Modules.Count; m++)
            {
                var harvester = part.Modules[m] as ModuleResourceHarvester;
                if (harvester == null
                    || !string.Equals(part.Modules[m].moduleName, HarvesterModuleName, StringComparison.Ordinal))
                    continue;
                if (ordinal == key.HarvesterOrdinal) return harvester;
                ordinal++;
            }
            return null;
        }
    }
}
