using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    /// <summary>Result of the per-tick drone state validation (design-far-watch.md 6.3 step 4).</summary>
    internal enum CameraDroneStateCheck
    {
        Valid,
        NonFinite,
        BelowBody,
        Parabolic,
    }

    /// <summary>
    /// The far-watch camera drone (design-far-watch.md section 6): a one-part, unowned,
    /// invisible, collider-less Debris vessel that becomes the active vessel while a far ghost is
    /// watched, so the player ship can stay packed on rails. Pure helpers (node shape, placement,
    /// terrain floor, state validation) are internal static and unit-tested; the live calls are
    /// thin and keep every Unity / FlightGlobals read inside a NoInlining core.
    /// </summary>
    internal static class CameraDrone
    {
        private const string Tag = "CameraDrone";
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

        internal const string PartName = "strutCube";
        internal const double SpawnOffsetMeters = 50.0;
        internal const double HandbackOffsetMeters = 300.0;
        internal const double HandbackRetryOffsetMeters = 100.0;
        internal const double FloorBaseMeters = 20.0;
        internal const double FloorCapMeters = 1000.0;
        internal const double OriginShiftMeters = 500.0;
        internal const double BelowRadiusToleranceMeters = 1000.0;
        internal const double ParabolicEccentricityBand = 1e-6;

        /// <summary>
        /// The stock part modules the strutCube carries (KOSNameTag is a mod's module and is
        /// install-dependent, so it is allowed but not required). A new stock module on the part
        /// reds <c>CameraDroneBuildNodeTests</c> and the in-game build check.
        /// </summary>
        internal static readonly string[] ExpectedStockPartModules = { "ModuleCargoPart" };

        /// <summary>Module names allowed on the drone part on top of the stock list (mods).</summary>
        internal static readonly string[] AllowedModPartModules = { "KOSNameTag" };

        internal static readonly string[] ExpectedVesselModules =
            { "FlightIntegrator", "AxisGroupsModule", "SuspensionLoadBalancer" };

        // ------------------------------------------------------------------ identity

        /// <summary>True when <paramref name="v"/> is the drone (pid AND guid).</summary>
        internal static bool Is(Vessel v)
        {
            if (ReferenceEquals(v, null))
                return false;
            return CameraDroneIdentity.IsDrone(v.persistentId, v.id);
        }

        /// <summary>True while the drone exists and is the active vessel.</summary>
        internal static bool IsActive()
        {
            if (!CameraDroneIdentity.Exists)
                return false;
            return IsActiveCore();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsActiveCore()
        {
            try
            {
                Vessel active = FlightGlobals.ActiveVessel;
                return active != null && Is(active);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ node shape (pure)

        /// <summary>
        /// The DISCOVERY node of the drone: level None with an infinite unobserved and reference
        /// lifetime, written with the keys <c>DiscoveryInfo.Save</c> writes. A finite lifetime
        /// would let <c>ScenarioDiscoverableObjects</c> kill the active drone (section 5).
        /// </summary>
        internal static ConfigNode BuildDiscoveryNode(double ut)
        {
            var node = new ConfigNode("DISCOVERY");
            node.AddValue("state", ((int)DiscoveryLevels.None).ToString(ic));
            node.AddValue("lastObservedTime", ut.ToString("G17", ic));
            node.AddValue("lifetime", double.PositiveInfinity.ToString("G17", ic));
            node.AddValue("refTime", double.PositiveInfinity.ToString("G17", ic));
            node.AddValue("size", ((int)UntrackedObjectClass.A).ToString(ic));
            return node;
        }

        /// <summary>
        /// Applies the drone's flags over a stock <c>ProtoVessel.CreateVesselNode</c> node
        /// (section 6.2): no spawn settle, no ground positioning, persistent, never autocleaned,
        /// never landed or splashed, the given non-landed situation, the drone name and type
        /// Debris, no crew, and the discovery node of <see cref="BuildDiscoveryNode"/>.
        /// </summary>
        internal static void ApplyDroneNodeFlags(ConfigNode vesselNode, string situation, double ut)
        {
            if (vesselNode == null)
                return;
            vesselNode.SetValue("name", CameraDroneIdentity.DroneVesselName, true);
            vesselNode.SetValue("type", VesselType.Debris.ToString(), true);
            vesselNode.SetValue("vesselSpawning", "False", true);
            vesselNode.SetValue("skipGroundPositioning", "True", true);
            vesselNode.SetValue("prst", "True", true);
            vesselNode.SetValue("cln", "False", true);
            vesselNode.SetValue("landed", "False", true);
            vesselNode.SetValue("splashed", "False", true);
            vesselNode.SetValue("landedAt", "", true);
            vesselNode.SetValue("sit", string.IsNullOrEmpty(situation) ? "ORBITING" : situation, true);
            vesselNode.RemoveNodes("DISCOVERY");
            vesselNode.AddNode(BuildDiscoveryNode(ut));
            ConfigNode[] parts = vesselNode.GetNodes("PART");
            for (int i = 0; i < parts.Length; i++)
                parts[i].RemoveValues("crew");
        }

        /// <summary>
        /// Every way <paramref name="vesselNode"/> differs from the drone contract of section 6.2;
        /// empty when it matches. The live build refuses a node with any problem, and the unit
        /// test pins the same function.
        /// </summary>
        internal static List<string> DescribeNodeProblems(ConfigNode vesselNode)
        {
            var problems = new List<string>();
            if (vesselNode == null)
            {
                problems.Add("node-null");
                return problems;
            }
            Expect(problems, vesselNode, "name", CameraDroneIdentity.DroneVesselName);
            Expect(problems, vesselNode, "type", VesselType.Debris.ToString());
            Expect(problems, vesselNode, "vesselSpawning", "False");
            Expect(problems, vesselNode, "skipGroundPositioning", "True");
            Expect(problems, vesselNode, "prst", "True");
            Expect(problems, vesselNode, "cln", "False");
            Expect(problems, vesselNode, "landed", "False");
            Expect(problems, vesselNode, "splashed", "False");
            string sit = vesselNode.GetValue("sit");
            if (sit == "LANDED" || sit == "SPLASHED" || sit == "PRELAUNCH")
                problems.Add("sit=" + sit);
            ConfigNode[] discs = vesselNode.GetNodes("DISCOVERY");
            if (discs.Length != 1)
            {
                problems.Add("discovery-count=" + discs.Length.ToString(ic));
            }
            else
            {
                ConfigNode d = discs[0];
                if (d.GetValue("state") != ((int)DiscoveryLevels.None).ToString(ic))
                    problems.Add("discovery-state=" + (d.GetValue("state") ?? "(none)"));
                if (!IsPositiveInfinity(d.GetValue("lifetime")))
                    problems.Add("discovery-lifetime=" + (d.GetValue("lifetime") ?? "(none)"));
                if (!IsPositiveInfinity(d.GetValue("refTime")))
                    problems.Add("discovery-refTime=" + (d.GetValue("refTime") ?? "(none)"));
            }
            ConfigNode[] parts = vesselNode.GetNodes("PART");
            if (parts.Length != 1)
                problems.Add("part-count=" + parts.Length.ToString(ic));
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].HasValue("crew"))
                    problems.Add("crew-on-part-" + i.ToString(ic));
                string pn = parts[i].GetValue("name");
                if (pn != PartName && pn != "sensorBarometer")
                    problems.Add("part-name=" + (pn ?? "(none)"));
            }
            return problems;
        }

        private static void Expect(List<string> problems, ConfigNode node, string key, string want)
        {
            string got = node.GetValue(key);
            if (!string.Equals(got, want, StringComparison.Ordinal))
                problems.Add(key + "=" + (got ?? "(none)"));
        }

        private static bool IsPositiveInfinity(string value)
        {
            double d;
            return value != null
                && double.TryParse(value, NumberStyles.Float, ic, out d)
                && double.IsPositiveInfinity(d);
        }

        /// <summary>
        /// Module names present on the built drone that the contract does not allow, plus the
        /// expected stock part and vessel modules that are missing. Empty when the build matches.
        /// </summary>
        internal static List<string> DescribeModuleProblems(
            IList<string> partModules, IList<string> vesselModules)
        {
            var problems = new List<string>();
            var part = new HashSet<string>(partModules ?? new string[0], StringComparer.Ordinal);
            var vessel = new HashSet<string>(vesselModules ?? new string[0], StringComparer.Ordinal);
            foreach (string m in part)
            {
                if (Array.IndexOf(ExpectedStockPartModules, m) < 0 && Array.IndexOf(AllowedModPartModules, m) < 0)
                    problems.Add("unexpected-part-module=" + m);
            }
            foreach (string m in ExpectedStockPartModules)
                if (!part.Contains(m)) problems.Add("missing-part-module=" + m);
            foreach (string m in vessel)
                if (Array.IndexOf(ExpectedVesselModules, m) < 0) problems.Add("unexpected-vessel-module=" + m);
            foreach (string m in ExpectedVesselModules)
                if (!vessel.Contains(m)) problems.Add("missing-vessel-module=" + m);
            return problems;
        }

        // ------------------------------------------------------------------ placement (pure)

        /// <summary>
        /// Where the drone stands beside the ship (sections 6.2 and 7.2 step 1): in flight,
        /// <paramref name="offsetMeters"/> along the ship's orbit normal with the ship's velocity;
        /// landed or splashed, straight up along the body radius and surface-held, because the
        /// orbit normal is horizontal there and a free Kepler arc falls into the ground.
        /// Returns false on a degenerate input (zero radius, zero or radial velocity in flight).
        /// </summary>
        internal static bool TryComputeBesideShip(
            Vector3d shipWorldPos, Vector3d bodyWorldPos, Vector3d shipObtVelWorld,
            bool shipLandedOrSplashed, double offsetMeters,
            out Vector3d droneWorldPos, out bool surfaceHeld)
        {
            droneWorldPos = shipWorldPos;
            surfaceHeld = shipLandedOrSplashed;
            Vector3d r = new Vector3d(
                shipWorldPos.x - bodyWorldPos.x, shipWorldPos.y - bodyWorldPos.y, shipWorldPos.z - bodyWorldPos.z);
            double rLen = Length(r);
            if (!(rLen > 0.0) || double.IsNaN(rLen) || double.IsInfinity(rLen))
                return false;
            Vector3d dir;
            if (shipLandedOrSplashed)
            {
                dir = Scale(r, 1.0 / rLen);
            }
            else
            {
                Vector3d n = Cross(r, shipObtVelWorld);
                double nLen = Length(n);
                if (!(nLen > 1e-9 * rLen) || double.IsNaN(nLen) || double.IsInfinity(nLen))
                    return false;
                dir = Scale(n, 1.0 / nLen);
            }
            droneWorldPos = new Vector3d(
                shipWorldPos.x + dir.x * offsetMeters,
                shipWorldPos.y + dir.y * offsetMeters,
                shipWorldPos.z + dir.z * offsetMeters);
            return true;
        }

        private static Vector3d Cross(Vector3d a, Vector3d b)
        {
            return new Vector3d(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        }

        private static double Length(Vector3d a)
        {
            return Math.Sqrt(a.x * a.x + a.y * a.y + a.z * a.z);
        }

        private static Vector3d Scale(Vector3d a, double s)
        {
            return new Vector3d(a.x * s, a.y * s, a.z * s);
        }

        // ------------------------------------------------------------------ terrain floor (pure)

        /// <summary>
        /// The floor above the ground the drone is kept at (section 6.3 step 3):
        /// <c>20 m + max(0, -v_up * dt) + 0.5 * g * dt^2</c>, with <paramref name="gameDtSeconds"/>
        /// the game time until the next write. A non-finite input reads as 0.
        /// </summary>
        internal static double TerrainFloorMeters(double verticalSpeed, double gameDtSeconds, double gravity)
        {
            if (double.IsNaN(verticalSpeed) || double.IsInfinity(verticalSpeed)) verticalSpeed = 0.0;
            if (double.IsNaN(gameDtSeconds) || double.IsInfinity(gameDtSeconds) || gameDtSeconds < 0.0) gameDtSeconds = 0.0;
            if (double.IsNaN(gravity) || double.IsInfinity(gravity) || gravity < 0.0) gravity = 0.0;
            return FloorBaseMeters
                + Math.Max(0.0, -verticalSpeed * gameDtSeconds)
                + 0.5 * gravity * gameDtSeconds * gameDtSeconds;
        }

        /// <summary>
        /// The highest warp rate (from <paramref name="rates"/>, ascending) at which the floor stays
        /// within <see cref="FloorCapMeters"/> for this vertical speed and gravity; 1 when none does.
        /// </summary>
        internal static float MaxWarpRateForFloorCap(
            IList<float> rates, double fixedDeltaTime, double verticalSpeed, double gravity)
        {
            float best = 1f;
            if (rates == null) return best;
            for (int i = 0; i < rates.Count; i++)
            {
                double dt = rates[i] * fixedDeltaTime;
                if (TerrainFloorMeters(verticalSpeed, dt, gravity) <= FloorCapMeters && rates[i] > best)
                    best = rates[i];
            }
            return best;
        }

        /// <summary>
        /// The altitude written for the drone: the target altitude raised to the floor above the
        /// ground (sea level over an ocean). <paramref name="clamped"/> is true when it was raised.
        /// </summary>
        internal static double ClampAltitudeToFloor(
            double targetAltitude, double terrainAltitude, bool overOcean, double floorMeters, out bool clamped)
        {
            double ground = overOcean ? Math.Max(0.0, terrainAltitude) : terrainAltitude;
            if (double.IsNaN(ground) || double.IsInfinity(ground)) ground = 0.0;
            double min = ground + floorMeters;
            clamped = !(targetAltitude >= min);
            return clamped ? min : targetAltitude;
        }

        // ------------------------------------------------------------------ validation (pure)

        /// <summary>
        /// Section 6.3 step 4: a state is written only when position and velocity are finite,
        /// the radius is above the body radius less 1 km, and the orbit is not within 1e-6 of
        /// parabolic (a NaN orbit makes <c>OrbitDriver</c> destroy the active vessel).
        /// </summary>
        internal static CameraDroneStateCheck ValidateState(
            Vector3d relPos, Vector3d relVel, double bodyRadius, double gravParameter)
        {
            if (!IsFinite(relPos) || !IsFinite(relVel) || double.IsNaN(bodyRadius) || double.IsNaN(gravParameter))
                return CameraDroneStateCheck.NonFinite;
            double r = Length(relPos);
            if (r <= bodyRadius - BelowRadiusToleranceMeters)
                return CameraDroneStateCheck.BelowBody;
            if (gravParameter > 0.0 && r > 0.0)
            {
                double ecc = Eccentricity(relPos, relVel, gravParameter);
                if (double.IsNaN(ecc) || double.IsInfinity(ecc))
                    return CameraDroneStateCheck.NonFinite;
                if (Math.Abs(ecc - 1.0) < ParabolicEccentricityBand)
                    return CameraDroneStateCheck.Parabolic;
            }
            return CameraDroneStateCheck.Valid;
        }

        internal static double Eccentricity(Vector3d r, Vector3d v, double mu)
        {
            double rLen = Length(r);
            double v2 = v.x * v.x + v.y * v.y + v.z * v.z;
            double rv = r.x * v.x + r.y * v.y + r.z * v.z;
            double a = v2 - mu / rLen;
            double ex = (a * r.x - rv * v.x) / mu;
            double ey = (a * r.y - rv * v.y) / mu;
            double ez = (a * r.z - rv * v.z) / mu;
            return Math.Sqrt(ex * ex + ey * ey + ez * ez);
        }

        private static bool IsFinite(Vector3d a)
        {
            return !(double.IsNaN(a.x) || double.IsNaN(a.y) || double.IsNaN(a.z)
                || double.IsInfinity(a.x) || double.IsInfinity(a.y) || double.IsInfinity(a.z));
        }

        // ------------------------------------------------------------------ live build / inert / write / die

        /// <summary>
        /// Builds and loads the drone at <paramref name="worldPos"/> with world orbital velocity
        /// <paramref name="worldObtVel"/>. The identity is registered BEFORE <c>pv.Load</c>, so
        /// the vessel GameEvents the load fires already skip it. Returns null with a reason on
        /// failure, leaving no identity behind.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Vessel TryBuild(
            CelestialBody body, Vector3d worldPos, Vector3d worldObtVel, bool surfaceHeld, out string why)
        {
            why = null;
            ProtoVessel pv = null;
            try
            {
                Game game = HighLogic.CurrentGame;
                if (game == null || game.flightState == null || body == null)
                {
                    why = "no-game";
                    return null;
                }
                double ut = Planetarium.GetUniversalTime();
                var orbit = new Orbit();
                orbit.UpdateFromStateVectors((worldPos - body.position).xzy, worldObtVel.xzy, body, ut);
                string part = PartLoader.getPartInfoByName(PartName) != null ? PartName : "sensorBarometer";
                ConfigNode partNode = ProtoVessel.CreatePartNode(part, 0);
                ConfigNode vn = ProtoVessel.CreateVesselNode(
                    CameraDroneIdentity.DroneVesselName, VesselType.Debris, orbit, 0,
                    new[] { partNode }, BuildDiscoveryNode(ut));
                ApplyDroneNodeFlags(vn, surfaceHeld ? "FLYING" : "ORBITING", ut);
                GhostMapPresence.EnsureDefensiveVesselNodes(vn);
                List<string> problems = DescribeNodeProblems(vn);
                if (problems.Count > 0)
                {
                    why = "node-contract:" + string.Join(",", problems.ToArray());
                    ParsekLog.Error(Tag, "build refused: " + why);
                    return null;
                }
                pv = new ProtoVessel(vn, game);
                CameraDroneIdentity.SetDrone(pv.persistentId, pv.vesselID);
                game.flightState.protoVessels.Add(pv);
                pv.Load(game.flightState);
                if (pv.vesselRef == null)
                {
                    why = "no-vesselRef";
                    game.flightState.protoVessels.Remove(pv);
                    CameraDroneIdentity.Clear("build-failed");
                    return null;
                }
                pv.vesselRef.vesselType = VesselType.Debris;
                ParsekLog.Info(Tag, string.Format(ic,
                    "built drone pid={0} guid={1} part={2} body={3} surfaceHeld={4} sit={5}",
                    pv.persistentId, pv.vesselID.ToString("N"), part, body.bodyName, surfaceHeld,
                    vn.GetValue("sit")));
                return pv.vesselRef;
            }
            catch (Exception ex)
            {
                why = "threw:" + ex.GetType().Name;
                ParsekLog.Error(Tag, "build threw " + ex.GetType().Name + ": " + ex.Message);
                try
                {
                    if (pv != null && HighLogic.CurrentGame != null && HighLogic.CurrentGame.flightState != null)
                        HighLogic.CurrentGame.flightState.protoVessels.Remove(pv);
                }
                catch (Exception) { }
                CameraDroneIdentity.Clear("build-threw");
                return null;
            }
        }

        /// <summary>Colliders and renderers off, parts hardened (the inert twin of the ghost marker's).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void MakeInert(Vessel v, string context)
        {
            if (v == null || v.parts == null) return;
            int colliders = 0, renderers = 0;
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
                Renderer[] rs = p.GetComponentsInChildren<Renderer>(true);
                for (int r = 0; r < rs.Length; r++)
                {
                    if (rs[r] == null || !rs[r].enabled) continue;
                    rs[r].enabled = false;
                    renderers++;
                }
            }
            int parts = GhostMapPresence.HardenGhostVesselPartPhysics(v, "CameraDrone." + context);
            ParsekLog.Info(Tag, string.Format(ic,
                "drone made inert pid={0} context={1} parts={2} collidersDisabled={3} renderersDisabled={4}",
                v.persistentId, context ?? "(none)", parts, colliders, renderers));
        }

        /// <summary>The module names on the live drone, for the build contract check.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void CollectModuleNames(Vessel v, List<string> partModules, List<string> vesselModules)
        {
            if (v == null) return;
            if (v.parts != null)
            {
                for (int i = 0; i < v.parts.Count; i++)
                {
                    Part p = v.parts[i];
                    if (p == null || p.Modules == null) continue;
                    for (int m = 0; m < p.Modules.Count; m++)
                        if (p.Modules[m] != null) partModules.Add(p.Modules[m].GetType().Name);
                }
            }
            if (v.vesselModules != null)
            {
                for (int i = 0; i < v.vesselModules.Count; i++)
                    if (v.vesselModules[i] != null) vesselModules.Add(v.vesselModules[i].GetType().Name);
            }
        }

        /// <summary>
        /// Writes a state onto the drone (section 6.3 step 5): orbit from Zup body-relative state
        /// vectors, orbit driver update, then the floating-origin shift and reference-frame check
        /// when the drone is the active vessel and far from the origin.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool WriteState(Vessel drone, CelestialBody body, Vector3d worldPos, Vector3d worldObtVel)
        {
            if (drone == null || drone.orbitDriver == null || drone.orbit == null || body == null)
                return false;
            double ut = Planetarium.GetUniversalTime();
            CelestialBody before = drone.orbit.referenceBody;
            drone.orbit.UpdateFromStateVectors((worldPos - body.position).xzy, worldObtVel.xzy, body, ut);
            drone.orbitDriver.updateFromParameters();
            bool active = drone == FlightGlobals.ActiveVessel;
            if (active && drone.transform.position.magnitude > OriginShiftMeters)
            {
                FloatingOrigin.SetOffset(drone.transform.position);
                OrbitPhysicsManager.CheckReferenceFrame();
            }
            else if (active && before != body)
            {
                OrbitPhysicsManager.CheckReferenceFrame();
            }
            if (before != body)
            {
                ParsekLog.Info(Tag, string.Format(ic, "drone body {0} -> {1} pid={2}",
                    before != null ? before.bodyName : "(none)", body.bodyName, drone.persistentId));
            }
            return true;
        }

        /// <summary>Destroys the drone. Only called once the ship is active again (section 6.5).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Destroy(Vessel drone, string reason)
        {
            if (drone == null) return;
            uint pid = drone.persistentId;
            bool active = drone == FlightGlobals.ActiveVessel;
            ParsekLog.Info(Tag, string.Format(ic, "destroying drone pid={0} active={1} reason={2}",
                pid, active, reason ?? "(none)"));
            try
            {
                drone.Die();
            }
            catch (Exception ex)
            {
                ParsekLog.Error(Tag, "drone Die threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
