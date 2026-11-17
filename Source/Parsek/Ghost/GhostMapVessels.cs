using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// The ghost map vessel primitive's guard state: the set of persistentIds that belong to a
    /// Parsek ghost map ProtoVessel, and the depth of the Parsek ghost teardown scopes on the
    /// stack. <see cref="GhostMapPresence"/> writes and reads both through its own entry points
    /// (<c>ghostMapVesselPids</c>, <c>IsGhostMapVessel</c>, <c>BeginGhostTeardown</c> and the
    /// rest), which forward here, so every caller keeps the same object and the same calls.
    /// Every pid also has an owner (<see cref="GhostMapVesselOwner"/>): the own timeline, or a
    /// foreign lane recorded in <see cref="foreignOwners"/>. The own-timeline teardown
    /// (<see cref="UnregisterAllOwnTimeline"/>) leaves a foreign lane's pids registered.
    /// It also owns the ProtoVessel mechanics of a ghost map vessel: the node build
    /// (<see cref="TryLoadGhostProtoVessel"/>: build, pre-register, load, harden), the part
    /// hardening, the orbit-driver identity, the removal and the save strip. It never composes
    /// a vessel name (the caller passes the finished name) and runs no per-tick pass.
    /// </summary>
    internal static class GhostMapVessels
    {
        private const string Tag = "GhostMap";
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

        /// <summary>Test seam: when set and it returns a body, <see cref="FindBodyByName"/>
        /// answers with it before reading <c>FlightGlobals.Bodies</c>.</summary>
        internal static Func<string, CelestialBody> FindBodyByNameForTesting;

        /// <summary>
        /// PID tracking set: the canonical ghost vessel identification.
        /// Every guard in the codebase checks this for O(1) exclusion.
        /// </summary>
        internal static readonly HashSet<uint> Pids = new HashSet<uint>();

        /// <summary>
        /// Owner of every registered pid that does not belong to the own timeline. A pid with
        /// no entry here is an own-timeline pid. Empty in single player, where every ghost map
        /// vessel is the own timeline's.
        /// </summary>
        private static readonly Dictionary<uint, GhostMapVesselOwner> foreignOwners =
            new Dictionary<uint, GhostMapVesselOwner>();

        /// <summary>
        /// How many Parsek ghost ProtoVessel <c>Die()</c> calls are on the stack right now.
        /// Nested because <see cref="GhostMapPresence.RemoveAllGhostVessels"/> wraps a whole
        /// loop while the single-ghost removers wrap one call each.
        ///
        /// WHY IT EXISTS: <c>Vessel.Die()</c> fires stock <c>onVesselDestroy</c> SYNCHRONOUSLY,
        /// and <c>SpaceTracking</c> handles that event by rebuilding its vessel list. When that
        /// rebuild throws, <see cref="Parsek.Patches.GhostTrackingBuildVesselsListPatch"/> has to
        /// decide whether the failure is attributable to our ghosts, and its live
        /// <c>FlightGlobals.Vessels</c> scan can itself throw during teardown, leaving the
        /// classifier with zeroed counts and no evidence. This flag is the scan-INDEPENDENT
        /// attribution signal: it is set only while OUR teardown is running.
        /// </summary>
        private static int teardownDepth;

        /// <summary>
        /// O(1) check used by all guard code throughout the codebase.
        /// Returns true if the given persistentId belongs to a ghost map ProtoVessel.
        /// </summary>
        internal static bool IsGhostMapVessel(uint persistentId)
        {
            return Pids.Contains(persistentId);
        }

        /// <summary>
        /// Registers <paramref name="pid"/> as a ghost map vessel of <paramref name="owner"/>.
        /// An own-timeline owner is the absence of an entry in <see cref="foreignOwners"/>, so
        /// only a foreign owner is recorded there.
        /// </summary>
        internal static void Register(uint pid, GhostMapVesselOwner owner)
        {
            Pids.Add(pid);
            if (!owner.IsOwnTimeline)
                foreignOwners[pid] = owner;
        }

        /// <summary>Drops <paramref name="pid"/> from the guard set and its owner record.</summary>
        internal static void Unregister(uint pid)
        {
            Pids.Remove(pid);
            foreignOwners.Remove(pid);
        }

        /// <summary>
        /// The own timeline's teardown: drops every pid that has no foreign owner and keeps the
        /// foreign lanes' pids and owners. With no foreign owner (single player) it is a whole
        /// clear of the guard set.
        /// </summary>
        internal static void UnregisterAllOwnTimeline()
        {
            if (foreignOwners.Count == 0)
            {
                Pids.Clear();
                return;
            }

            // Keeps exactly the pids that have a foreign owner; never enumerates Pids while
            // removing from it.
            Pids.IntersectWith(foreignOwners.Keys);
        }

        /// <summary>Test seam: registers <paramref name="pid"/> under a foreign lane's owner.</summary>
        internal static void RegisterForeignForTesting(uint pid, string laneId, string recordingId)
        {
            Register(pid, new GhostMapVesselOwner(laneId, recordingId));
        }

        /// <summary>Test seam: the foreign owner recorded for <paramref name="pid"/>, if any.</summary>
        internal static bool TryGetForeignOwnerForTesting(uint pid, out GhostMapVesselOwner owner)
        {
            return foreignOwners.TryGetValue(pid, out owner);
        }

        /// <summary>Test seam: how many pids have a foreign owner.</summary>
        internal static int ForeignOwnerCountForTesting
        {
            get { return foreignOwners.Count; }
        }

        /// <summary>Enter a Parsek ghost-teardown scope. Pair with <see cref="EndTeardown"/>
        /// in a <c>finally</c>; never let an exception leave the depth raised.</summary>
        internal static void BeginTeardown()
        {
            teardownDepth++;
        }

        /// <summary>Leave a Parsek ghost-teardown scope. Floors at zero so an unbalanced
        /// decrement can never latch the flag inverted for the rest of the session.</summary>
        internal static void EndTeardown()
        {
            if (teardownDepth > 0)
                teardownDepth--;
        }

        /// <summary>True while a Parsek ghost ProtoVessel <c>Die()</c> is on the stack.</summary>
        internal static bool IsTeardownInProgress
        {
            get { return teardownDepth > 0; }
        }

        /// <summary>Live count of registered ghost map ProtoVessels. Read by the
        /// buildVesselsList finalizer as evidence that survives a failed context scan.</summary>
        internal static int RegisteredCount
        {
            get { return Pids.Count; }
        }

        /// <summary>Reset the guard set, its owner record, the teardown depth and the body
        /// lookup seam for testing.</summary>
        internal static void ResetForTesting()
        {
            teardownDepth = 0;
            Pids.Clear();
            foreignOwners.Clear();
            FindBodyByNameForTesting = null;
        }

        // ------------------------------------------------------------------
        // ProtoVessel mechanics
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds a ghost ProtoVessel named <paramref name="vesselName"/> on
        /// <paramref name="orbit"/>, registers its pid under <paramref name="owner"/> BEFORE
        /// <c>ProtoVessel.Load</c> (Load fires <c>onVesselCreate</c>, and every guard must see
        /// the pid as a ghost during that cascade), adds it to the flight state, loads it,
        /// hardens its parts, normalizes its orbit driver identity, force-enables its orbit
        /// renderer and restores its vessel type. On success <paramref name="load"/> carries
        /// the vessel and what the caller's creation line reports. On failure (a null
        /// <c>vesselRef</c> or an exception) the ProtoVessel is unregistered and removed, the
        /// Error line is written here, and false is returned.
        /// </summary>
        internal static bool TryLoadGhostProtoVessel(
            IPlaybackTrajectory traj,
            Orbit orbit,
            string vesselName,
            GhostMapVesselOwner owner,
            string logContext,
            out GhostMapVesselLoad load)
        {
            load = default(GhostMapVesselLoad);
            ProtoVessel pv = null;
            try
            {
                ConfigNode vesselNode = BuildGhostProtoVesselNode(
                    traj,
                    orbit,
                    vesselName,
                    out VesselType vtype);

                pv = new ProtoVessel(vesselNode, HighLogic.CurrentGame);

                // PRE-REGISTER PID before Load - pv.Load fires onVesselCreate and guards
                // must see this PID as a ghost vessel during the event cascade.
                Register(pv.persistentId, owner);

                HighLogic.CurrentGame.flightState.protoVessels.Add(pv);

                // Load - creates Vessel GO, OrbitDriver, MapObject, OrbitRenderer,
                // registers in FlightGlobals, fires GameEvents.onVesselCreate
                pv.Load(HighLogic.CurrentGame.flightState);

                if (pv.vesselRef == null)
                {
                    ParsekLog.Error(Tag,
                        string.Format(ic,
                            "BuildAndLoadGhostProtoVessel: vesselRef is null after Load for {0}",
                            logContext));
                    RemoveGhostProtoVessel(pv, nullSafeFlightState: false);
                    return false;
                }

                // Log creation + OrbitDriver state for diagnostics (#172)
                Vessel v = pv.vesselRef;
                // KSP promotes a single-part root with PhysicsSignificance=1 back to
                // FULL, and the thermal explode site checks temperature > maxTemp
                // independent of physical significance. Without this pass the marker's
                // sensorBarometer (maxTemp=1200) overheats and Part.explode() destroys
                // the vessel mid-Re-Fly whenever the active vessel passes through dense
                // atmosphere at orbital speed (repro at logs/2026-05-19_1847_refly-
                // booster-explosion, log line 18:44:50.128).
                HardenGhostVesselPartPhysics(v, logContext);
                NormalizeGhostOrbitDriverTargetIdentity(v, logContext);
                string driverState = "no-orbitDriver";
                if (v.orbitDriver != null)
                {
                    Orbit drv = v.orbitDriver.orbit;

                    driverState = string.Format(ic,
                        "updateMode={0} sma={1:F0} ecc={2:F6} inc={3:F4} " +
                        "argPe={4:F4} mna={5:F6} epoch={6:F1} vesselPos=({7:F1},{8:F1},{9:F1}) {10} registered={11}",
                        v.orbitDriver.updateMode,
                        drv.semiMajorAxis, drv.eccentricity, drv.inclination,
                        drv.argumentOfPeriapsis, drv.meanAnomalyAtEpoch, drv.epoch,
                        v.GetWorldPos3D().x, v.GetWorldPos3D().y, v.GetWorldPos3D().z,
                        BuildGhostOrbitDriverIdentity(v),
                        IsVesselRegistered(v));
                }

                // Ensure OrbitRenderer is enabled - in Tracking Station, pv.Load()
                // may create the renderer in a disabled state.
                bool rendererForceEnabled = false;
                if (v.orbitRenderer != null)
                {
                    v.orbitRenderer.drawMode = OrbitRendererBase.DrawMode.REDRAW_AND_RECALCULATE;
                    v.orbitRenderer.drawIcons = OrbitRendererBase.DrawIcons.ALL;
                    if (!v.orbitRenderer.enabled)
                    {
                        rendererForceEnabled = true;
                        v.orbitRenderer.enabled = true;
                        ParsekLog.Verbose(Tag, string.Format(ic,
                            "Force-enabled OrbitRenderer for ghost '{0}'", vesselName));
                    }
                }

                // Force correct VesselType - KSP may override for single-part vessels
                if (v.vesselType != vtype)
                {
                    ParsekLog.Verbose(Tag, string.Format(ic,
                        "Ghost vessel type overridden by KSP: {0} → {1}, restoring to {2}",
                        vtype, v.vesselType, vtype));
                    v.vesselType = vtype;
                }

                load = new GhostMapVesselLoad(
                    v, pv, vtype, vesselName, driverState, rendererForceEnabled);
                return true;
            }
            catch (Exception ex)
            {
                if (pv != null)
                {
                    RemoveGhostProtoVessel(pv, nullSafeFlightState: true);
                }
                ParsekLog.Error(Tag,
                    string.Format(ic,
                        "BuildAndLoadGhostProtoVessel failed for {0}: {1}",
                        logContext, ex.Message));
                return false;
            }
        }

        private static ConfigNode BuildGhostProtoVesselNode(
            IPlaybackTrajectory traj,
            Orbit orbit,
            string vesselName,
            out VesselType vtype)
        {
            // Single antenna-free part: the ghost's CommNet node is GhostCommNetManager's, not this marker's.
            // Aero/thermal tolerances are hardened post-load in HardenGhostVesselPartPhysics:
            // the partNode is loaded into a real Part with prefab maxTemp=1200, which
            // would otherwise overheat and explode the marker vessel during low-altitude
            // playback (see TryLoadGhostProtoVessel).
            ConfigNode partNode = ProtoVessel.CreatePartNode("sensorBarometer", 0);

            // Discovery: fully visible, infinite lifetime
            ConfigNode discovery = ProtoVessel.CreateDiscoveryNode(
                DiscoveryLevels.Owned, UntrackedObjectClass.C,
                double.PositiveInfinity, double.PositiveInfinity);

            vtype = ResolveVesselType(traj.VesselSnapshot);

            ConfigNode vesselNode = ProtoVessel.CreateVesselNode(
                vesselName, vtype, orbit, 0,
                new ConfigNode[] { partNode }, discovery);

            // Critical settings: prevent ground positioning and KSC cleanup
            vesselNode.SetValue("vesselSpawning", "False", true);
            vesselNode.SetValue("prst", "True", true);
            vesselNode.SetValue("cln", "False", true);

            EnsureDefensiveVesselNodes(vesselNode);

            return vesselNode;
        }

        /// <summary>
        /// Adds the sub-nodes that SpaceTracking.buildVesselsList and other KSP internals
        /// assume exist on a VESSEL node. ProtoVessel.CreateVesselNode adds ACTIONGROUPS
        /// but omits these three, and a missing node can NRE inside tracking-station code
        /// paths. Idempotent: an already-present node is left alone. Split out of
        /// the ghost ProtoVessel node builder so the insertion can be driven headlessly.
        /// </summary>
        internal static void EnsureDefensiveVesselNodes(ConfigNode vesselNode)
        {
            if (vesselNode == null) return;

            if (vesselNode.GetNode("FLIGHTPLAN") == null)
                vesselNode.AddNode("FLIGHTPLAN");
            if (vesselNode.GetNode("CTRLSTATE") == null)
                vesselNode.AddNode("CTRLSTATE");
            if (vesselNode.GetNode("VESSELMODULES") == null)
                vesselNode.AddNode("VESSELMODULES");
        }

        /// <summary>
        /// Read VesselType from vessel snapshot ConfigNode.
        /// Falls back to VesselType.Ship if snapshot is null or type is missing.
        /// </summary>
        internal static VesselType ResolveVesselType(ConfigNode vesselSnapshot)
        {
            if (vesselSnapshot == null) return VesselType.Ship;

            string typeStr = vesselSnapshot.GetValue("type");
            if (string.IsNullOrEmpty(typeStr)) return VesselType.Ship;

            if (Enum.TryParse(typeStr, true, out VesselType vtype))
                return vtype;

            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "ResolveVesselType: unrecognized type '{0}' — defaulting to Ship",
                    typeStr));
            return VesselType.Ship;
        }

        /// <summary>
        /// Override aero/thermal/structural tolerances on every part of a freshly-loaded
        /// ghost-owned ProtoVessel (map-presence marker, replay flag, future single-part
        /// ghost vessels) so the vessel behaves as a render-only presence rather than a
        /// physical body. Without this, KSP's FlightIntegrator runs aerothermal sim on
        /// the vessel (single-part root parts are promoted to PhysicalSignificance.FULL
        /// regardless of prefab settings) and the prefab maxTemp / crashTolerance values
        /// trigger Part.explode() when something hot or fast happens nearby.
        /// </summary>
        internal static int HardenGhostVesselPartPhysics(Vessel v, string logContext)
        {
            if (v == null || v.parts == null) return 0;
            int count = 0;
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;
                p.maxTemp = double.PositiveInfinity;
                p.skinMaxTemp = double.PositiveInfinity;
                p.crashTolerance = float.PositiveInfinity;
                p.gTolerance = double.PositiveInfinity;
                p.breakingForce = float.PositiveInfinity;
                p.breakingTorque = float.PositiveInfinity;
                count++;
            }
            ParsekLog.Verbose(Tag, string.Format(ic,
                "Ghost vessel parts hardened: vessel='{0}' parts={1} for {2}",
                v.vesselName ?? "(null)", count, logContext));
            return count;
        }

        /// <summary>
        /// Called when KSP loads a ghost map vessel's parts (<c>Vessel.Load</c> postfix):
        /// hardens the now-real parts and disables every collider under them, so the
        /// render-only marker can neither explode nor collide with a real vessel that
        /// shares its position inside physics range.
        /// </summary>
        internal static void MakeLoadedGhostVesselPhysicsInert(Vessel v, string logContext)
        {
            if (v == null) return;
            int parts = HardenGhostVesselPartPhysics(v, logContext);
            int colliders = 0;
            if (v.parts != null)
            {
                for (int i = 0; i < v.parts.Count; i++)
                {
                    Part p = v.parts[i];
                    if (p == null) continue;
                    Collider[] cs = p.GetComponentsInChildren<Collider>(true);
                    for (int c = 0; c < cs.Length; c++)
                    {
                        if (cs[c] == null || !cs[c].enabled) continue;
                        cs[c].enabled = false;
                        colliders++;
                    }
                }
            }
            LogGhostVesselMadeInert(v.persistentId, v.vesselName, parts, colliders, logContext);
        }

        internal static void LogGhostVesselMadeInert(
            uint pid, string vesselName, int parts, int colliders, string logContext)
        {
            ParsekLog.Verbose(Tag, string.Format(ic,
                "Ghost vessel made physics-inert on load: vessel='{0}' pid={1} parts={2} collidersDisabled={3} for {4}",
                vesselName ?? "(null)", pid, parts, colliders, logContext));
        }

        internal static void NormalizeGhostOrbitDriverTargetIdentity(
            Vessel vessel,
            string context)
        {
            if (vessel == null || vessel.orbitDriver == null)
                return;

            OrbitDriver driver = vessel.orbitDriver;
            bool changed = false;
            string before = BuildGhostOrbitDriverIdentity(vessel);

            if (!IsSameVessel(driver.vessel, vessel))
            {
                driver.vessel = vessel;
                changed = true;
            }

            if (driver.celestialBody != null)
            {
                driver.celestialBody = null;
                changed = true;
            }

            if (changed)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "Normalized ghost target identity for {0}: before=[{1}] after=[{2}]",
                        string.IsNullOrEmpty(context) ? "(none)" : context,
                        before,
                        BuildGhostOrbitDriverIdentity(vessel)));
            }
        }

        internal static string BuildGhostOrbitDriverIdentity(Vessel vessel)
        {
            if (vessel == null)
                return "driver=(ghost-null)";
            OrbitDriver driver = vessel.orbitDriver;
            if (driver == null)
                return "driver=(null)";

            string driverVessel = driver.vessel != null
                ? string.Format(ic,
                    "{0}/pid={1}",
                    driver.vessel.vesselName ?? "(null)",
                    driver.vessel.persistentId)
                : "(null)";
            string driverBody = driver.celestialBody != null
                ? (driver.celestialBody.name ?? "(unnamed-body)")
                : "(null)";
            string referenceBody = driver.referenceBody != null
                ? (driver.referenceBody.name ?? "(unnamed-body)")
                : "(null)";

            return string.Format(ic,
                "driverVessel={0} driverCelestialBody={1} referenceBody={2} orbit={3} mapObj={4} orbitRenderer={5}",
                driverVessel,
                driverBody,
                referenceBody,
                driver.orbit != null,
                vessel.mapObject != null,
                vessel.orbitRenderer != null);
        }

        internal static bool IsVesselRegistered(Vessel vessel)
        {
            if (vessel == null || FlightGlobals.fetch == null)
                return false;

            // O(1) persistent-id lookup against FlightGlobals.PersistentVesselIds
            // (stock FlightGlobals.FindVessel is a thin wrapper around it).
            if (vessel.persistentId != 0u)
                return FlightGlobals.FindVessel(vessel.persistentId, out _);

            // pid==0 vessels (unregistered / mid-construction) fall back to an
            // identity scan so callers still get a meaningful answer.
            if (FlightGlobals.Vessels == null)
                return false;
            for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
            {
                if (ReferenceEquals(FlightGlobals.Vessels[i], vessel))
                    return true;
            }
            return false;
        }

        internal static bool IsSameVessel(Vessel a, Vessel b)
        {
            if (a == null || b == null)
                return false;
            return ReferenceEquals(a, b)
                || (a.persistentId != 0u && a.persistentId == b.persistentId);
        }

        internal static void RemoveGhostProtoVessel(ProtoVessel pv, bool nullSafeFlightState)
        {
            Unregister(pv.persistentId);
            if (nullSafeFlightState)
                HighLogic.CurrentGame?.flightState?.protoVessels?.Remove(pv);
            else
                HighLogic.CurrentGame.flightState.protoVessels.Remove(pv);
        }

        /// <summary>
        /// Strip ghost ProtoVessels from flightState before save.
        /// Ghost vessels are transient - reconstructed from recording data on load.
        /// Called from ParsekScenario.OnSave.
        /// </summary>
        internal static int StripFromSave(FlightState flightState)
        {
            if (flightState == null || Pids.Count == 0)
                return 0;

            int stripped = flightState.protoVessels.RemoveAll(
                pv => Pids.Contains(pv.persistentId));

            if (stripped > 0)
                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Stripped {0} ghost ProtoVessel(s) from save", stripped));

            return stripped;
        }

        /// <summary>
        /// Find a CelestialBody by name without LINQ allocation.
        /// Returns null if FlightGlobals.Bodies is null or name not found.
        /// </summary>
        internal static CelestialBody FindBodyByName(string bodyName)
        {
            if (FindBodyByNameForTesting != null)
            {
                CelestialBody testBody = FindBodyByNameForTesting(bodyName);
                if (!object.ReferenceEquals(testBody, null))
                    return testBody;
            }

            var bodies = FlightGlobals.Bodies;
            if (bodies == null) return null;
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i].name == bodyName || bodies[i].bodyName == bodyName) return bodies[i];
            return null;
        }
    }

    /// <summary>
    /// Who a ghost map vessel pid belongs to: the own timeline (<see cref="OwnTimeline"/>, a
    /// null lane) or a foreign lane's recording. Fields, not properties.
    /// </summary>
    internal readonly struct GhostMapVesselOwner
    {
        /// <summary>The own timeline's owner: no lane, no recording.</summary>
        public static readonly GhostMapVesselOwner OwnTimeline = new GhostMapVesselOwner(null, null);

        /// <summary>The owning lane; null for the own timeline.</summary>
        public readonly string LaneId;

        /// <summary>The owning lane's recording id; null for the own timeline.</summary>
        public readonly string RecordingId;

        public GhostMapVesselOwner(string laneId, string recordingId)
        {
            LaneId = laneId;
            RecordingId = recordingId;
        }

        /// <summary>True for the own timeline (no lane).</summary>
        public bool IsOwnTimeline => LaneId == null;
    }

    /// <summary>
    /// What <see cref="GhostMapVessels.TryLoadGhostProtoVessel"/> hands back on success: the
    /// loaded vessel and its ProtoVessel, the vessel type it was built with, its name, the
    /// orbit-driver state string and whether the orbit renderer had to be force-enabled.
    /// Fields, not properties.
    /// </summary>
    internal readonly struct GhostMapVesselLoad
    {
        public readonly Vessel Vessel;
        public readonly ProtoVessel ProtoVessel;
        public readonly VesselType VesselType;
        public readonly string VesselName;
        public readonly string DriverState;
        public readonly bool RendererForceEnabled;

        public GhostMapVesselLoad(
            Vessel vessel,
            ProtoVessel protoVessel,
            VesselType vesselType,
            string vesselName,
            string driverState,
            bool rendererForceEnabled)
        {
            Vessel = vessel;
            ProtoVessel = protoVessel;
            VesselType = vesselType;
            VesselName = vesselName;
            DriverState = driverState;
            RendererForceEnabled = rendererForceEnabled;
        }
    }
}
