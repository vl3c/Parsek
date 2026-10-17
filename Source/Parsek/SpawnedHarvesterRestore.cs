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

        private sealed class Pending
        {
            internal uint VesselPid;
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
