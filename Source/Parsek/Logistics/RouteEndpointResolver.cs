using System.Collections.Generic;
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
        /// Production entry point. Tries PID lookup; on miss falls back to
        /// surface proximity when the endpoint is surface-typed. Returns
        /// <c>false</c> with a stable reason token on failure.
        /// </summary>
        internal static bool TryResolveEndpoint(
            RouteEndpoint endpoint,
            out Vessel vessel,
            out string reason)
        {
            vessel = null;
            reason = string.Empty;

            // 1. O(1) PID lookup (same wrapper GhostMapPresence uses).
            if (endpoint.VesselPersistentId != 0u)
            {
                Vessel byPid = ResolveByPid(endpoint.VesselPersistentId);
                HashSet<uint> ghostPids = GhostMapPresence.ghostMapVesselPids;
                if (byPid != null
                    && (ghostPids == null || !ghostPids.Contains(byPid.persistentId)))
                {
                    vessel = byPid;
                    return true;
                }
            }

            // 2. Surface proximity fallback.
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

                return TrySurfaceFallbackPure(
                    endpointWorldPos,
                    endpoint.BodyName,
                    snapshots,
                    GhostMapPresence.ghostMapVesselPids,
                    RouteOrchestrator.SurfaceProximityRadiusMeters,
                    out vessel,
                    out _,
                    out reason);
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
