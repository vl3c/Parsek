using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Parsek
{
    // KSC end-of-flight retirement: the retirement decision, its zone text and the retired-final-flight predicates.
    public static partial class VesselSpawner
    {
        /// <summary>Outcome of <see cref="EvaluateKscEndOfFlightRetirement"/>.</summary>
        internal struct KscRetirementDecision
        {
            public KscExclusionZone Zone;
            /// <summary>Stock site name of the circle hit ("KSC" for the KSC pair).</summary>
            public string SiteName;
            public bool IsKsc;
            public double Latitude;
            public double Longitude;
            public string BodyName;
            public TerminalState? EffectiveTerminal;
            public bool Retire => Zone != KscExclusionZone.None;
        }

        /// <summary>
        /// The terminal state a spawn would honour: the stamped
        /// <see cref="Recording.TerminalStateValue"/>, or for an un-finalized recording
        /// the snapshot situation mapped the way the finalize path would have stamped it
        /// (<see cref="GhostPlaybackLogic.TryMapSituationNameToTerminalState"/>; PRELAUNCH
        /// maps to Landed). Null when neither is available.
        /// </summary>
        internal static TerminalState? ResolveEffectiveTerminalForRetirement(Recording rec)
        {
            if (rec == null)
                return null;
            if (rec.TerminalStateValue.HasValue)
                return rec.TerminalStateValue.Value;
            TerminalState mapped;
            if (rec.VesselSnapshot != null
                && GhostPlaybackLogic.TryMapSituationNameToTerminalState(
                    rec.VesselSnapshot.GetValue("sit"), out mapped))
                return mapped;
            return null;
        }

        /// <summary>
        /// The EVA exemption of the KSC end-of-flight retirement (an EVA kerbal has no vessel to
        /// park and its pad spawn was never blocked): true when the recording is an EVA branch
        /// (<see cref="Recording.EvaCrewName"/>) or its own vessel is a kerbal on EVA, read from
        /// the end vessel snapshot's stock vessel type (<c>type = EVA</c>). The second form
        /// covers kerbal recordings with no <c>EvaCrewName</c>: an adopted foreign cargo-partner
        /// kerbal's member recording and a recording started on a kerbal already out on EVA.
        /// Only the END snapshot is read (the spawn form), never the start situation or the
        /// ghost snapshot, so a recording whose spawn form is a vessel is never exempted. Pure
        /// over the recording; null snapshot reads "not a kerbal".
        /// </summary>
        internal static bool IsEvaKerbalRecordingForRetirement(Recording rec)
        {
            if (rec == null)
                return false;
            if (!string.IsNullOrEmpty(rec.EvaCrewName))
                return true;
            string snapshotType = rec.VesselSnapshot != null ? rec.VesselSnapshot.GetValue("type") : null;
            return string.Equals(snapshotType, "EVA", StringComparison.Ordinal);
        }

        /// <summary>
        /// Recording-level form of <see cref="SpawnCollisionDetector.DecideKscEndOfFlightRetirement"/>:
        /// resolves the effective terminal, the EVA flag, the spawn position
        /// (<see cref="SelectSpawnCoordinates(Recording, TrajectoryPoint?, out string, out double, out double, out double, out bool, out double, out double)"/>,
        /// i.e. where the vessel would be placed), the resolved trajectory endpoint, and the
        /// endpoint body (<see cref="RecordingEndpointResolver.TryGetPreferredEndpointBodyName"/>,
        /// the body the flight spawn uses, else the endpoint's own body) and asks the
        /// predicate. The spawn side and the crew side (<see cref="IsKscRetiredFinalFlight"/>)
        /// both call this, so they read the same position: when the trajectory endpoint lies
        /// in a KSC circle and the in-memory vessel snapshot was dropped, the snapshot is
        /// re-hydrated from its sidecar exactly as the spawn gate does (non-debris surface
        /// terminals only), because a snapshot-sourced position must agree with the endpoint
        /// before the flight retires (a stale snapshot on the pad of a flight that ended
        /// elsewhere is not retired). An endpoint outside both circles answers "not retired"
        /// without touching the disk. The body registry is read through
        /// <see cref="LiveVesselSnapshot.TryResolveBodyByName"/> (test seam <see cref="LiveVesselSnapshot.BodyResolverForTesting"/>);
        /// when the registry or a KSP-only resolver is unavailable the answer is "not
        /// retired", which keeps today's behaviour (spawn, crew stays reserved). Silent.
        /// </summary>
        internal static KscRetirementDecision EvaluateKscEndOfFlightRetirement(Recording rec)
        {
            var decision = new KscRetirementDecision { Zone = KscExclusionZone.None };
            if (rec == null)
                return decision;

            bool isEva = IsEvaKerbalRecordingForRetirement(rec);
            TerminalState? terminal = ResolveEffectiveTerminalForRetirement(rec);
            decision.EffectiveTerminal = terminal;
            // Cheap gates first: the predicate would say no anyway, and skipping them keeps
            // the body lookup and endpoint resolution off every non-parked recording.
            if (isEva || !IsSurfaceTerminal(terminal))
                return decision;

            try
            {
                TrajectoryPoint? lastPt = rec.Points != null && rec.Points.Count > 0
                    ? (TrajectoryPoint?)rec.Points[rec.Points.Count - 1]
                    : null;
                SpawnCoordinateSource source = SelectSpawnCoordinates(
                    rec, lastPt, out string endpointBodyName,
                    out double lat, out double lon, out _,
                    out bool haveEndpoint, out double endpointLat, out double endpointLon);
                if (source == SpawnCoordinateSource.None && !haveEndpoint)
                    return decision;

                string bodyName;
                if (!RecordingEndpointResolver.TryGetPreferredEndpointBodyName(rec, out bodyName)
                    || string.IsNullOrEmpty(bodyName))
                    bodyName = endpointBodyName;
                if (string.IsNullOrEmpty(bodyName)
                    || !LiveVesselSnapshot.TryResolveBodyByName(bodyName, out CelestialBody body)
                    || object.ReferenceEquals(body, null))
                    return decision;
                if (!body.isHomeWorld)
                    return decision;

                // A known endpoint outside every launch-site circle settles it: whatever the
                // snapshot says, the flight did not END on launch-site infrastructure.
                IList<LaunchSiteCircle> altCircles = LaunchSiteExclusionZones.GetAltSiteCircles();
                if (haveEndpoint
                    && !LaunchSiteExclusionZones.Classify(
                        endpointLat, endpointLon, body.Radius,
                        SpawnCollisionDetector.DefaultKscExclusionRadiusMeters, altCircles).IsHit)
                    return decision;

                // The spawn gate re-hydrates a dropped snapshot before it spawns; do the same
                // here so the crew side reads the snapshot position the spawn will use.
                if (rec.VesselSnapshot == null && !rec.IsDebris
                    && RecordingSidecarStore.TryHydrateVesselSnapshotFromSidecar(rec))
                {
                    // The re-hydrated snapshot may show the recording's vessel is a kerbal.
                    if (IsEvaKerbalRecordingForRetirement(rec))
                        return decision;
                    source = SelectSpawnCoordinates(
                        rec, lastPt, out endpointBodyName,
                        out lat, out lon, out _,
                        out haveEndpoint, out endpointLat, out endpointLon);
                }
                if (source == SpawnCoordinateSource.None)
                    return decision;

                decision.Latitude = lat;
                decision.Longitude = lon;
                decision.BodyName = bodyName;
                LaunchSiteZoneHit hit = SpawnCollisionDetector.DecideLaunchSiteEndOfFlightRetirement(
                    terminal, isEva, body.isHomeWorld, lat, lon, body.Radius, altCircles,
                    positionIsSnapshot: source == SpawnCoordinateSource.Snapshot,
                    endpointLatitude: haveEndpoint ? endpointLat : double.NaN,
                    endpointLongitude: haveEndpoint ? endpointLon : double.NaN);
                decision.Zone = hit.Kind;
                decision.SiteName = hit.SiteName;
                decision.IsKsc = hit.IsKsc;
                return decision;
            }
            catch (Exception ex)
            {
                if (!IsHeadlessKspAccessFailure(ex) && !LiveVesselSnapshot.IsHeadlessBodyRegistryFailure(ex))
                    throw;
                return new KscRetirementDecision
                {
                    Zone = KscExclusionZone.None,
                    EffectiveTerminal = terminal,
                };
            }
        }

        /// <summary>
        /// Spawn entry-point gate for the KSC end-of-flight retirement (operator ruling
        /// 2026-09-23). Every end-of-recording real-spawn entry point calls this right
        /// where the spawn would be attempted (after the source-vessel adoption check, so
        /// a real counterpart that still exists is adopted, never retired). When the
        /// recording's flight ended parked in a KSC exclusion zone it is settled as
        /// "spawned, no vessel" (<c>VesselSpawned = true</c> + <c>SpawnAbandoned = true</c>,
        /// <c>SpawnedVesselPersistentId</c> stays 0: the same transient settled state an
        /// abandoned spawn uses, which map presence, the vessel-gone check and every scene
        /// spawn predicate already honour, and which a rewind / revert resets), one Info
        /// line is logged, and true is returned so the caller neither spawns nor holds
        /// the ghost. No-op (false) for a recording that is already settled.
        /// <paramref name="requireNoMaterializedSource"/> is for callers that run BEFORE
        /// the adoption check (the warp-deferred completion): they retire only when no
        /// real counterpart exists, and otherwise leave the recording to the normal path.
        /// </summary>
        /// <summary>
        /// Zone phrase of the retirement log line. The KSC wording is unchanged
        /// ("KSC exclusion zone (pad)", pinned by EX-1); another stock site reads
        /// "launch-site exclusion zone (Desert Launch Site pad)".
        /// </summary>
        internal static string FormatRetirementZone(KscRetirementDecision decision)
        {
            string kind = SpawnCollisionDetector.DescribeKscExclusionZone(decision.Zone);
            if (decision.IsKsc || string.IsNullOrEmpty(decision.SiteName))
                return "KSC exclusion zone (" + kind + ")";
            return "launch-site exclusion zone ("
                + LaunchSiteExclusionZones.DescribeSite(decision.SiteName) + " " + kind + ")";
        }

        internal static bool TryRetireEndedFlightAtKsc(
            Recording rec,
            int index,
            bool requireNoMaterializedSource = false)
        {
            if (rec == null || rec.VesselSpawned || rec.SpawnedVesselPersistentId != 0)
                return false;

            KscRetirementDecision decision = EvaluateKscEndOfFlightRetirement(rec);
            if (!decision.Retire)
                return false;

            if (requireNoMaterializedSource && MaterializedSourceVesselExists(rec))
                return false;

            rec.VesselSpawned = true;
            rec.SpawnAbandoned = true;
            rec.CollisionBlockCount = 0;
            ParsekLog.Info("Spawner", string.Format(CultureInfo.InvariantCulture,
                "Spawn RETIRED for #{0} ({1}): flight ended within {2} - no vessel " +
                "lat={3:F4} lon={4:F4} body={5} terminal={6} rec={7}",
                index,
                rec.VesselName,
                FormatRetirementZone(decision),
                decision.Latitude,
                decision.Longitude,
                decision.BodyName ?? "(null)",
                decision.EffectiveTerminal.HasValue
                    ? decision.EffectiveTerminal.Value.ToString()
                    : "(none)",
                rec.RecordingId ?? "(null)"));
            return true;
        }

        /// <summary>
        /// True when <paramref name="rec"/> is settled as a KSC retirement: settled with no
        /// vessel (<c>VesselSpawned</c>, <c>SpawnAbandoned</c>, pid 0) AND its flight ended
        /// in a KSC exclusion zone. Lets callers word a retirement as Info rather than
        /// the Warn of an abandoned spawn. Silent.
        /// </summary>
        internal static bool IsSettledAsKscRetirement(Recording rec)
        {
            return rec != null
                && rec.VesselSpawned
                && rec.SpawnAbandoned
                && rec.SpawnedVesselPersistentId == 0
                && EvaluateKscEndOfFlightRetirement(rec).Retire;
        }

        /// <summary>
        /// The crew side of the KSC retirement ruling: true when the kerbals aboard
        /// <paramref name="rec"/> at its end are freed at its EndUT, as if recovered.
        /// Uses the SAME final-segment notion as the spawn side
        /// (<see cref="GhostPlaybackLogic.IsFinalSpawnSegment"/>: only where the vessel
        /// ends its WHOLE flight counts) and the same position predicate. Two mirror
        /// guards keep a kerbal who is aboard a REAL vessel reserved: a recording already
        /// materialized as a real vessel (spawned or adopted, pid != 0) is not retired,
        /// and neither is one whose recorded launch still exists in the save (the real
        /// counterpart still sits on the pad; the spawn path would adopt it rather than
        /// retire it). When the spawn side has already settled the recording as a KSC
        /// retirement (<see cref="IsSettledAsKscRetirement"/>) that answer is taken as is.
        /// Both sides evaluate the position through <see cref="EvaluateKscEndOfFlightRetirement"/>,
        /// which re-hydrates a dropped snapshot the same way the spawn gate does. Silent:
        /// the live-counterpart probe does not log a relaunch rejection (this runs on every
        /// ledger recalculation).
        /// </summary>
        internal static bool IsKscRetiredFinalFlight(Recording rec)
        {
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                return false;
            if (rec.SpawnedVesselPersistentId != 0)
                return false;
            if (IsSettledAsKscRetirement(rec))
                return true;
            if (!EvaluateKscEndOfFlightRetirement(rec).Retire)
                return false;
            if (!GhostPlaybackLogic.IsFinalSpawnSegment(rec))
                return false;
            if (MaterializedSourceVesselExists(rec, logAdoptionRejection: false))
                return false;
            return true;
        }
    }
}
