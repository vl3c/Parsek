using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.Logistics
{
    /// <summary>
    /// Resolves a saved <see cref="RouteEndpoint"/> to a live <c>Vessel</c>.
    /// First tries the O(1) persistent-id lookup; if that misses and the
    /// endpoint is surface-typed, falls back to a great-circle proximity
    /// search bounded by <see cref="RouteOrchestrator.SurfaceProximityRadiusMeters"/>.
    /// Ghost map vessels are always excluded.
    ///
    /// The proximity search is split off into a pure helper
    /// (<see cref="TrySurfaceFallbackPure"/>) that takes a flat list of
    /// <see cref="SurfaceVesselSnapshot"/> records so xUnit can exercise the
    /// branch without constructing live <c>Vessel</c> instances.
    /// </summary>
    internal static class RouteEndpointResolver
    {
        /// <summary>
        /// Minimal POCO surface used by <see cref="TrySurfaceFallbackPure"/>.
        /// Production callers convert the live <c>Vessel</c> list to this shape
        /// before invoking the pure helper; tests construct records directly.
        /// </summary>
        /// <summary>
        /// Minimal POCO for the ROOT-PART identity step. Deliberately unfiltered by body
        /// or situation: a root-part id names one physical vessel wherever it is, so an
        /// orbital depot resolves through this step even though it can never reach the
        /// surface fallback.
        /// </summary>
        internal struct RootIdVesselSnapshot
        {
            public uint PersistentId;
            /// <summary>Root part flightID; 0 when neither the live parts nor the proto
            /// snapshot could supply one (the vessel is then never a root match).</summary>
            public uint RootPartFlightId;
            /// <summary>The live <c>Vessel</c> for the resolver to return; null in pure-test contexts.</summary>
            public Vessel Vessel;
        }

        internal struct SurfaceVesselSnapshot
        {
            public uint PersistentId;
            public string BodyName;
            public Vessel.Situations Situation;
            /// <summary>World-space position used for distance comparison.</summary>
            public Vector3d WorldPosition;
            /// <summary>The live <c>Vessel</c> reference for the resolver to return; null in pure-test contexts.</summary>
            public Vessel Vessel;
        }

        /// <summary>
        /// Production entry point. Three steps, in this order: ROOT PART ID, then PID,
        /// then surface proximity. Returns <c>false</c> with a stable reason token on
        /// failure. Logs which step resolved.
        ///
        /// <para>THE ROOT-PART STEP IS FIRST AND THAT ORDER IS THE CONTRACT. A start-docked
        /// origin carries no pid at all (the depot's <c>Vessel</c> is destroyed by
        /// <c>Part.Couple</c> at the dock), so without it the origin would fall straight
        /// through to proximity and resolve to WHATEVER surface vessel is nearest the
        /// recorded coordinates - which, for a shuttle route, is routinely the TRANSPORT
        /// parked back at the depot. That is exactly the "deducts from the origin depot,
        /// NOT the transport" case the design doc names, so the identity has to win before
        /// any positional guess is made.</para>
        ///
        /// <para>Guid-free BY CONSTRUCTION, not by omission: a part <c>flightID</c> is
        /// assigned per launch and is never written into the <c>.craft</c>, so it cannot
        /// collide across launches the way a <c>persistentId</c> does and there is nothing
        /// for a launch-guid gate to disambiguate. It also survives the split that creates
        /// the depot's own vessel: <c>Part.Undock(newVesselInfo)</c> looks the part up as
        /// <c>this.vessel[newVesselInfo.rootPartUId]</c>, calls
        /// <c>part.SetHierarchyRoot(part)</c> and builds the new <c>Vessel</c> on that
        /// part's GameObject (decompiled KSP 1.12.5), so the undocked half's
        /// <c>rootPart.flightID</c> IS the <c>rootPartUId</c> the proof recorded - while
        /// the same method assigns <c>vessel.id = Guid.NewGuid()</c>, which is why matching
        /// on a launch guid here would be actively wrong.</para>
        /// </summary>
        internal static bool TryResolveEndpoint(
            RouteEndpoint endpoint,
            out Vessel vessel,
            out string reason)
        {
            vessel = null;
            reason = string.Empty;

            // 1. ROOT PART ID: the identity step.
            if (endpoint.RootPartUId != 0u)
            {
                List<RootIdVesselSnapshot> rootSnapshots =
                    BuildRootIdSnapshots(FlightGlobals.Vessels);
                if (TryRootPartMatchPure(
                        endpoint.RootPartUId,
                        rootSnapshots,
                        GhostMapPresence.ghostMapVesselPids,
                        out Vessel byRoot,
                        out uint rootPickedPid,
                        out string rootReason))
                {
                    vessel = byRoot;
                    ParsekLog.Verbose("Logistics",
                        "Endpoint resolved: step=root-part rootPartUId="
                        + endpoint.RootPartUId.ToString(CultureInfo.InvariantCulture)
                        + " pid=" + rootPickedPid.ToString(CultureInfo.InvariantCulture));
                    return true;
                }
                ParsekLog.Verbose("Logistics",
                    "Endpoint root-part step missed: rootPartUId="
                    + endpoint.RootPartUId.ToString(CultureInfo.InvariantCulture)
                    + " reason=" + rootReason
                    + " candidates=" + rootSnapshots.Count.ToString(CultureInfo.InvariantCulture));
            }

            // 2. O(1) PID lookup (same wrapper GhostMapPresence uses).
            if (endpoint.VesselPersistentId != 0u)
            {
                Vessel byPid = ResolveByPid(endpoint.VesselPersistentId);
                HashSet<uint> ghostPids = GhostMapPresence.ghostMapVesselPids;
                if (byPid != null
                    && (ghostPids == null || !ghostPids.Contains(byPid.persistentId)))
                {
                    vessel = byPid;
                    ParsekLog.Verbose("Logistics",
                        "Endpoint resolved: step=pid pid="
                        + endpoint.VesselPersistentId.ToString(CultureInfo.InvariantCulture));
                    return true;
                }
            }

            // 3. Surface proximity fallback.
            if (endpoint.IsSurface
                && !string.IsNullOrEmpty(endpoint.BodyName)
                && FlightGlobals.Vessels != null)
            {
                CelestialBody body = ResolveBodyByName(endpoint.BodyName);
                if (body == null)
                {
                    reason = "body-unresolved";
                    return false;
                }

                Vector3d endpointWorldPos = body.GetWorldSurfacePosition(
                    endpoint.Latitude, endpoint.Longitude, endpoint.Altitude);

                List<SurfaceVesselSnapshot> snapshots = BuildSurfaceSnapshots(
                    FlightGlobals.Vessels, endpoint.BodyName);

                bool resolved = TrySurfaceFallbackPure(
                    endpointWorldPos,
                    endpoint.BodyName,
                    snapshots,
                    GhostMapPresence.ghostMapVesselPids,
                    RouteOrchestrator.SurfaceProximityRadiusMeters,
                    out vessel,
                    out uint proximityPid,
                    out reason);
                ParsekLog.Verbose("Logistics",
                    "Endpoint proximity step: resolved=" + (resolved ? "1" : "0")
                    + " pid=" + proximityPid.ToString(CultureInfo.InvariantCulture)
                    + " reason=" + (string.IsNullOrEmpty(reason) ? "-" : reason)
                    + " body=" + endpoint.BodyName);
                return resolved;
            }

            reason = "pid-miss-no-surface-fallback";
            return false;
        }

        /// <summary>
        /// Pure surface-fallback search. Takes the endpoint's world position +
        /// body name and a flat list of candidate vessel snapshots; picks the
        /// closest surface-classified candidate within
        /// <paramref name="radiusMeters"/> whose PID is not in
        /// <paramref name="excludePids"/>. <c>out vessel</c> may be null when
        /// the snapshot has no live <c>Vessel</c> reference (pure-test mode);
        /// production callers always populate it. <c>out pickedPid</c> exposes
        /// the chosen snapshot's <see cref="SurfaceVesselSnapshot.PersistentId"/>
        /// for diagnostic clarity and pure-test assertions; it is <c>0</c> on
        /// every miss path.
        /// </summary>
        internal static bool TrySurfaceFallbackPure(
            Vector3d endpointWorldPos,
            string bodyName,
            IReadOnlyList<SurfaceVesselSnapshot> liveSnapshots,
            HashSet<uint> excludePids,
            double radiusMeters,
            out Vessel vessel,
            out uint pickedPid,
            out string reason)
        {
            vessel = null;
            pickedPid = 0u;
            reason = string.Empty;

            if (liveSnapshots == null || liveSnapshots.Count == 0)
            {
                reason = "no-live-vessels";
                return false;
            }

            int bestIdx = -1;
            double bestDist = double.MaxValue;
            for (int i = 0; i < liveSnapshots.Count; i++)
            {
                SurfaceVesselSnapshot snap = liveSnapshots[i];

                // Body match (case-sensitive — KSP body names are stable).
                if (snap.BodyName != bodyName)
                    continue;

                // Surface-class situations only.
                if (!IsSurfaceSituation(snap.Situation))
                    continue;

                // Exclude ghosts.
                if (excludePids != null && excludePids.Contains(snap.PersistentId))
                    continue;

                double dist = (snap.WorldPosition - endpointWorldPos).magnitude;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0)
            {
                reason = "no-surface-candidate";
                return false;
            }
            if (bestDist > radiusMeters)
            {
                reason = "no-vessel-within-radius";
                return false;
            }

            vessel = liveSnapshots[bestIdx].Vessel;
            pickedPid = liveSnapshots[bestIdx].PersistentId;
            return true;
        }

        /// <summary>
        /// Pure root-part identity search. Returns the single vessel whose ROOT part
        /// flightID equals <paramref name="rootPartUId"/>, excluding ghost map vessels.
        /// A zero <paramref name="rootPartUId"/> never matches (it is the "unknown"
        /// sentinel, and a snapshot that could not supply a root id also carries 0 - so
        /// admitting it would pair every unknown with every other unknown). Two vessels
        /// carrying one root flightID is impossible in a healthy save; if it happens the
        /// FIRST is taken and the reason token records the collision so a log names it.
        /// </summary>
        internal static bool TryRootPartMatchPure(
            uint rootPartUId,
            IReadOnlyList<RootIdVesselSnapshot> snapshots,
            HashSet<uint> excludePids,
            out Vessel vessel,
            out uint pickedPid,
            out string reason)
        {
            vessel = null;
            pickedPid = 0u;
            reason = string.Empty;

            if (rootPartUId == 0u)
            {
                reason = "root-id-unknown";
                return false;
            }
            if (snapshots == null || snapshots.Count == 0)
            {
                reason = "no-root-candidate";
                return false;
            }

            int found = 0;
            int firstIdx = -1;
            for (int i = 0; i < snapshots.Count; i++)
            {
                if (snapshots[i].RootPartFlightId != rootPartUId) continue;
                if (excludePids != null && excludePids.Contains(snapshots[i].PersistentId)) continue;
                found++;
                if (firstIdx < 0) firstIdx = i;
            }

            if (firstIdx < 0)
            {
                reason = "no-root-match";
                return false;
            }

            vessel = snapshots[firstIdx].Vessel;
            pickedPid = snapshots[firstIdx].PersistentId;
            reason = found > 1 ? "root-match-ambiguous" : string.Empty;
            return true;
        }

        /// <summary>
        /// Convert the live <see cref="FlightGlobals.Vessels"/> list to root-part
        /// snapshots. Unloaded vessels have no instantiated <c>rootPart</c>, so the root
        /// flightID comes from the <c>ProtoVessel</c>'s own root snapshot - without that
        /// half an unloaded depot (the normal state of a depot at dispatch time) would
        /// never match. NOT filtered by body or situation: identity does not depend on
        /// where the vessel is.
        /// </summary>
        private static List<RootIdVesselSnapshot> BuildRootIdSnapshots(
            IReadOnlyList<Vessel> liveVessels)
        {
            var snapshots = new List<RootIdVesselSnapshot>();
            if (liveVessels == null) return snapshots;

            for (int i = 0; i < liveVessels.Count; i++)
            {
                Vessel v = liveVessels[i];
                if (v == null) continue;
                snapshots.Add(new RootIdVesselSnapshot
                {
                    PersistentId = v.persistentId,
                    RootPartFlightId = ResolveRootPartFlightId(v),
                    Vessel = v,
                });
            }

            return snapshots;
        }

        private static uint ResolveRootPartFlightId(Vessel v)
        {
            try
            {
                if (v.rootPart != null)
                    return v.rootPart.flightID;
                ProtoVessel pv = v.protoVessel;
                if (pv?.protoPartSnapshots == null) return 0u;
                int rootIndex = pv.rootIndex;
                if (rootIndex < 0 || rootIndex >= pv.protoPartSnapshots.Count) return 0u;
                return pv.protoPartSnapshots[rootIndex]?.flightID ?? 0u;
            }
            catch
            {
                // Defensive, same rationale as ResolveByPid: a stock-side null during
                // scene teardown surfaces as an endpoint miss, not a crash.
                return 0u;
            }
        }

        /// <summary>
        /// Convert the live <see cref="FlightGlobals.Vessels"/> list to a flat
        /// snapshot list filtered to the matching body. Encapsulates every
        /// <c>Vessel.*</c> field read so the pure path above stays KSP-free.
        /// </summary>
        private static List<SurfaceVesselSnapshot> BuildSurfaceSnapshots(
            IReadOnlyList<Vessel> liveVessels,
            string bodyName)
        {
            var snapshots = new List<SurfaceVesselSnapshot>();
            if (liveVessels == null) return snapshots;

            for (int i = 0; i < liveVessels.Count; i++)
            {
                Vessel v = liveVessels[i];
                if (v == null) continue;
                if (v.mainBody == null) continue;
                if (v.mainBody.bodyName != bodyName) continue;
                if (!IsSurfaceSituation(v.situation)) continue;

                snapshots.Add(new SurfaceVesselSnapshot
                {
                    PersistentId = v.persistentId,
                    BodyName = v.mainBody.bodyName,
                    Situation = v.situation,
                    WorldPosition = v.GetWorldPos3D(),
                    Vessel = v,
                });
            }

            return snapshots;
        }

        private static bool IsSurfaceSituation(Vessel.Situations situation)
        {
            return situation == Vessel.Situations.LANDED
                || situation == Vessel.Situations.SPLASHED
                || situation == Vessel.Situations.PRELAUNCH;
        }

        private static Vessel ResolveByPid(uint pid)
        {
            try
            {
                // FlightGlobals.FindVessel is an O(1) wrapper around
                // FlightGlobals.PersistentVesselIds (matches the canonical
                // GhostMapPresence pattern).
                if (FlightGlobals.fetch != null
                    && FlightGlobals.FindVessel(pid, out Vessel found))
                {
                    return found;
                }
            }
            catch
            {
                // Defensive: a stock-side null-deref during scene teardown should
                // surface as a benign endpoint-miss rather than a hard crash.
            }
            return null;
        }

        private static CelestialBody ResolveBodyByName(string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName)) return null;
            if (FlightGlobals.Bodies == null) return null;
            for (int i = 0; i < FlightGlobals.Bodies.Count; i++)
            {
                CelestialBody body = FlightGlobals.Bodies[i];
                if (body != null && body.bodyName == bodyName)
                    return body;
            }
            return null;
        }
    }
}
