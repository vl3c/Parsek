using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    // Map-presence ghost source resolution: source kinds, orbit-data predicates, endpoint
    // tails, chain tips and source detail lines.
    internal static partial class GhostMapPresence
    {
        internal enum TrackingStationGhostSource
        {
            None = 0,
            Segment = 1,
            TerminalOrbit = 2,
            StateVector = 3,
            StateVectorSoiGap = 4,
            EndpointTail = 5
        }

        internal static bool IsStateVectorGhostSource(TrackingStationGhostSource source)
        {
            return source == TrackingStationGhostSource.StateVector
                || source == TrackingStationGhostSource.StateVectorSoiGap;
        }

        /// <summary>
        /// Pure: a resolved ghost source the deferred map-create pass is willing to materialize
        /// (everything except <see cref="TrackingStationGhostSource.None"/>): a covering Segment,
        /// either state-vector flavor, the loop-synthesis TerminalOrbit fallback, or the EndpointTail
        /// consistency fix. Extracted byte-for-byte from the inline create-acceptance check.
        /// </summary>
        internal static bool IsMapCreateAcceptedSource(TrackingStationGhostSource source)
        {
            return source == TrackingStationGhostSource.Segment
                || IsStateVectorGhostSource(source)
                || source == TrackingStationGhostSource.TerminalOrbit
                || source == TrackingStationGhostSource.EndpointTail;
        }

        /// <summary>
        /// Pure: a resolved ghost source that populates a valid <c>segment</c> out-param (Segment,
        /// the loop-synthesis no-segment TerminalOrbit fallback, or EndpointTail, which populates the
        /// segment exactly like TerminalOrbit). Used by the state-vector update pass to decide whether
        /// to consume the segment branch instead of falling through to the flat point path.
        /// </summary>
        internal static bool IsSegmentBearingGhostSource(TrackingStationGhostSource source)
        {
            return source == TrackingStationGhostSource.Segment
                || source == TrackingStationGhostSource.TerminalOrbit
                || source == TrackingStationGhostSource.EndpointTail;
        }

        /// <summary>
        /// Tracking-station per-tick orbit-source precedence for an already-created ghost:
        /// a wrapped/closed OrbitSegment covering the effective UT is the trusted source and
        /// wins over a co-located OrbitalCheckpoint state-vector. The checkpoint state-vector is
        /// distrusted (stale-frame interpretation reintroduced the #571/#584 wrong-position class)
        /// and is only valid for a genuine segment gap, so it is consumed only when no segment
        /// covers effUT. This mirrors the create-path <see cref="ResolveTrackingStationGhostSource"/>
        /// and the flight-scene <c>ParsekPlaybackPolicy.CheckPendingMapVessels</c> precedence;
        /// without it a Segment-created looped ghost freezes on its last segment the moment effUT
        /// advances into a transfer/coast OrbitalCheckpoint section.
        /// </summary>
        internal static bool ShouldConsumeCheckpointStateVectorForExistingGhost(
            bool fromCheckpoint, bool segmentCoversEffUT)
        {
            return fromCheckpoint && !segmentCoversEffUT;
        }

        /// <summary>
        /// True when at least one OrbitSegment begins strictly after <paramref name="ut"/>, i.e. the
        /// recording still has orbital playback AHEAD of the current effective UT. Used by the
        /// tracking-station update path to distinguish a mid-recording gap (between the parking
        /// orbit and a later destination orbit, e.g. during a transfer burn / coast) from the
        /// genuine terminal region. The endpoint-tail (terminal-orbit) fallback must fire only at
        /// the terminal region; firing it in a mid-recording gap reseeds the looped proto-vessel
        /// onto the FINAL orbit for the whole rest of the replay and suppresses the non-proto
        /// atmospheric position marker. Mirrors the flight-scene gap check in
        /// <c>ParsekPlaybackPolicy.CheckPendingMapVessels</c>.
        /// </summary>
        internal static bool HasOrbitSegmentStartingAfter(List<OrbitSegment> segments, double ut)
        {
            if (segments == null)
                return false;
            for (int i = 0; i < segments.Count; i++)
            {
                if (segments[i].startUT > ut)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// True when the synthetic endpoint-tail (terminal-orbit) fallback may be applied in the
        /// tracking-station per-tick update for this ghost. The endpoint tail is a NON-LOOP concept
        /// (it shows where a finished flight ended up, seeded from the recorded historical body
        /// rotation/position); for a loop member it does not survive the loop epoch shift for a
        /// cross-body terminal (a Mun orbit seeded from the Mun's RECORDED position lands tens of
        /// millions of metres off the live Mun). A loop member's effUT is always inside the
        /// recording, so its covering OrbitSegment is the correct current orbit and must win; when
        /// none covers effUT the loop removes the proto-vessel instead, matching the flight scene's
        /// segment-update path. Loop membership is read from the per-tick epoch shift
        /// (<c>liveUT - effUT</c>): zero for non-loop members, non-zero for loop replay.
        ///
        /// INVARIANT (load-bearing): this "shift == 0 means non-loop" signal is sound only because
        /// the first-play floor in <c>MissionLoopUnitBuilder.TryBuildMissionUnit</c> clamps every
        /// looping mission's <c>phaseAnchorUT</c> to at least <c>spanEndUT</c> (&gt; spanStartUT), so a
        /// loop member's effUT can never equal liveUT (it always maps to a past recorded UT, giving a
        /// non-zero shift). If that floor is ever weakened/removed, a cycle-0 member with
        /// <c>phaseAnchorUT == spanStartUT</c> would produce shift 0 and silently re-enable the
        /// endpoint tail here. Keep the two in sync.
        /// </summary>
        internal static bool EndpointTailAllowedInTrackingStationUpdate(double loopEpochShiftSeconds)
        {
            return loopEpochShiftSeconds == 0.0;
        }

        private static string BuildTrackingStationGhostSourceSummaryKey(
            TrackingStationGhostSource source,
            string reason)
        {
            if (source == TrackingStationGhostSource.None)
                return "skip-" + (string.IsNullOrEmpty(reason) ? "unspecified" : reason);

            return "source-" + FormatTrackingStationGhostSource(source);
        }

        private static string FormatTrackingStationGhostSource(TrackingStationGhostSource source)
        {
            switch (source)
            {
                case TrackingStationGhostSource.Segment:
                    return "visible-segment";
                case TrackingStationGhostSource.TerminalOrbit:
                    return "terminal-orbit";
                case TrackingStationGhostSource.EndpointTail:
                    return "endpoint-tail";
                case TrackingStationGhostSource.StateVector:
                    return "state-vector";
                case TrackingStationGhostSource.StateVectorSoiGap:
                    return "soi-gap-state-vector";
                default:
                    return "none";
            }
        }

        private static string BuildTrackingStationGhostSourceLogLine(
            string context,
            int recordingIndex,
            Recording rec,
            double currentUT,
            TrackingStationGhostSource source,
            string reason,
            string detail)
        {
            string recId = rec?.RecordingId ?? "(null)";
            string vesselName = rec?.VesselName ?? "(null)";
            string terminal = rec?.TerminalStateValue?.ToString() ?? "(null)";
            string endpointPhase = rec != null ? rec.EndpointPhase.ToString() : "(null)";
            string endpointBody = string.IsNullOrEmpty(rec?.EndpointBodyName)
                ? "(none)"
                : rec.EndpointBodyName;
            string terminalBody = string.IsNullOrEmpty(rec?.TerminalOrbitBody)
                ? "(none)"
                : rec.TerminalOrbitBody;

            return string.Format(ic,
                "ResolveTrackingStationGhostSource: Tracking-station orbit source: context={0} recIndex={1} rec={2} vessel=\"{3}\" " +
                "currentUT={4:F1} source={5} orbitSource={6} reason={7} terminal={8} terminalBody={9} terminalSma={10:F0} " +
                "endpoint=({11},{12}) hasSegments={13} vesselSpawned={14} spawnedPid={15}{16}",
                string.IsNullOrEmpty(context) ? "unspecified" : context,
                recordingIndex,
                recId,
                vesselName,
                currentUT,
                source,
                FormatTrackingStationGhostSource(source),
                string.IsNullOrEmpty(reason) ? "(none)" : reason,
                terminal,
                terminalBody,
                rec?.TerminalOrbitSemiMajorAxis ?? 0.0,
                endpointPhase,
                endpointBody,
                rec?.HasOrbitSegments ?? false,
                rec?.VesselSpawned ?? false,
                rec?.SpawnedVesselPersistentId ?? 0u,
                string.IsNullOrEmpty(detail) ? string.Empty : " " + detail);
        }

        private static void LogTrackingStationGhostSourceDecision(
            string context,
            int recordingIndex,
            Recording rec,
            double currentUT,
            TrackingStationGhostSource source,
            string reason,
            string detail,
            TrackingStationGhostSourceBatch batch)
        {
            if (batch != null)
            {
                batch.Observe(recordingIndex, rec, currentUT, source, reason, detail);
                return;
            }

            ParsekLog.Verbose(
                Tag,
                BuildTrackingStationGhostSourceLogLine(
                    context,
                    recordingIndex,
                    rec,
                    currentUT,
                    source,
                    reason,
                    detail));
        }

        private static bool HasTerminalOrbitData(IPlaybackTrajectory traj)
        {
            return traj != null
                && !string.IsNullOrEmpty(traj.TerminalOrbitBody)
                && traj.TerminalOrbitSemiMajorAxis > 0;
        }

        // ------------------------------------------------------------------
        // Pure data layer (unchanged from original)
        // ------------------------------------------------------------------

        /// <summary>
        /// Pure: does this recording have orbital data suitable for map presence?
        /// True if terminal orbit body is set and SMA > 0.
        /// </summary>
        /// <remarks>
        /// Logs via <see cref="ParsekLog.VerboseOnChange"/> keyed on
        /// <c>(recordingId, body, smaBucket, result)</c> so per-frame stable
        /// callers do not flood KSP.log: the 2026-04-25 playtest recorded
        /// ~1678 redundant emissions in a 27-minute session before this gate.
        /// </remarks>
        internal static bool HasOrbitData(Recording rec)
        {
            if (rec == null)
            {
                ParsekLog.VerboseOnChange(
                    Tag,
                    "has-orbit-data-rec|null",
                    "null",
                    "HasOrbitData(Recording): null recording — returning false");
                return false;
            }

            bool hasOrbit = HasTerminalOrbitData(rec);

            string recId = rec.RecordingId ?? "(null)";
            string body = rec.TerminalOrbitBody ?? "(null)";
            double sma = rec.TerminalOrbitSemiMajorAxis;
            ParsekLog.VerboseOnChange(
                Tag,
                string.Format(ic, "has-orbit-data-rec|{0}", recId),
                BuildHasOrbitDataStateKey(body, sma, hasOrbit),
                string.Format(ic,
                    "HasOrbitData(Recording): rec={0} body={1} sma={2} result={3}",
                    recId,
                    body,
                    sma,
                    hasOrbit));

            return hasOrbit;
        }

        /// <summary>
        /// Pure: does this trajectory have orbital data suitable for map presence?
        /// Overload accepting IPlaybackTrajectory for engine-side use.
        /// </summary>
        /// <remarks>
        /// Per-frame map-view callers (<see cref="ParsekPlaybackPolicy"/> + the
        /// shared map-presence resolver) hammered this method ~11/sec in the
        /// 2026-04-25 playtest. Logging is gated through
        /// <see cref="ParsekLog.VerboseOnChange"/> so a stable
        /// <c>(recordingId, body, smaBucket)</c> tuple emits exactly once per
        /// state change with <c>| suppressed=N</c> on the next flip.
        /// </remarks>
        internal static bool HasOrbitData(IPlaybackTrajectory traj)
        {
            if (traj == null)
            {
                ParsekLog.VerboseOnChange(
                    Tag,
                    "has-orbit-data-traj|null",
                    "null",
                    "HasOrbitData(IPlaybackTrajectory): null trajectory — returning false");
                return false;
            }

            bool hasOrbit = HasTerminalOrbitData(traj);

            if (hasOrbit)
            {
                string recId = traj.RecordingId;
                string identityScope = !string.IsNullOrEmpty(recId)
                    ? string.Format(ic, "has-orbit-data-traj|rec={0}", recId)
                    : string.Format(ic, "has-orbit-data-traj|name={0}",
                        traj.VesselName ?? "(unnamed)");
                ParsekLog.VerboseOnChange(
                    Tag,
                    identityScope,
                    BuildHasOrbitDataStateKey(traj.TerminalOrbitBody, traj.TerminalOrbitSemiMajorAxis, true),
                    string.Format(ic,
                        "HasOrbitData(IPlaybackTrajectory): body={0} sma={1} result=True",
                        traj.TerminalOrbitBody,
                        traj.TerminalOrbitSemiMajorAxis));
            }

            return hasOrbit;
        }

        /// <summary>
        /// Stable state-key builder for HasOrbitData log emissions. Buckets
        /// SMA to 1km so trivial floating-point drift between frames does not
        /// pop the on-change gate; <see cref="HasTerminalOrbitData"/>
        /// classifies "has orbit" as <c>SMA &gt; 0</c>, so a 1km bucket is
        /// far below any meaningful Recording terminal orbit.
        /// </summary>
        private static string BuildHasOrbitDataStateKey(string body, double sma, bool hasOrbit)
        {
            string safeBody = string.IsNullOrEmpty(body) ? "(null)" : body;
            long smaBucket = double.IsNaN(sma) || double.IsInfinity(sma)
                ? long.MinValue
                : (long)Math.Round(sma / 1000.0);
            return string.Format(ic, "{0}|sma={1}|res={2}",
                safeBody, smaBucket, hasOrbit);
        }

        internal static bool IsTerminalStateEligibleForMapPresence(TerminalState? terminal)
        {
            return !terminal.HasValue
                || terminal.Value == TerminalState.Orbiting
                || terminal.Value == TerminalState.Docked
                || terminal.Value == TerminalState.SubOrbital;
        }

        private static bool IsTerminalStateEligibleForTerminalOrbitMapPresence(TerminalState? terminal)
        {
            return !terminal.HasValue
                || terminal.Value == TerminalState.Orbiting
                || terminal.Value == TerminalState.Docked;
        }

        internal static bool StartsInOrbit(IPlaybackTrajectory traj, double ut)
        {
            if (!traj.HasOrbitSegments)
                return false;
            if (traj.Points == null || traj.Points.Count == 0)
                return true;
            return TrajectoryMath.FindOrbitSegment(traj.OrbitSegments, ut) != null;
        }

        /// <summary>
        /// Emit the structured "source-resolve" decision line. Pulled out of
        /// <see cref="ResolveMapPresenceGhostSource"/>'s inner local function
        /// because C# 7 forbids closing over <c>out</c>/<c>ref</c> parameters,
        /// and the segment / state-vector data we want to log are passed in
        /// through such parameters.
        /// </summary>
        private static void EmitSourceResolveLine(
            IPlaybackTrajectory traj,
            string recId,
            int recordingIndex,
            TrackingStationGhostSource source,
            string reason,
            double currentUT,
            string logOperationName,
            OrbitSegment resolvedSegment,
            TrajectoryPoint resolvedStatePoint)
        {
            var srf = NewDecisionFields("source-resolve");
            srf.RecordingId = recId;
            // `recordingIndex` is `-1` when the caller did not (or could not)
            // know the index — that's an explicit "unknown" sentinel rather
            // than the misleading `idx=0` `NewDecisionFields` would produce.
            srf.RecordingIndex = recordingIndex;
            srf.VesselName = traj?.VesselName;
            srf.Source = source.ToString();
            srf.Branch = "(n/a)";
            srf.UT = currentUT;
            srf.Reason = string.IsNullOrEmpty(reason) ? logOperationName : reason;
            if (source == TrackingStationGhostSource.Segment
                || source == TrackingStationGhostSource.EndpointTail)
            {
                srf.Body = resolvedSegment.bodyName;
                srf.Segment = resolvedSegment;
            }
            else if (IsStateVectorGhostSource(source))
            {
                srf.Body = resolvedStatePoint.bodyName;
                srf.StateVecAlt = resolvedStatePoint.altitude;
                srf.StateVecSpeed = resolvedStatePoint.velocity.magnitude;
            }
            else if (source == TrackingStationGhostSource.TerminalOrbit)
            {
                srf.Body = traj?.TerminalOrbitBody;
                srf.TerminalBody = traj?.TerminalOrbitBody;
                srf.TerminalSma = traj?.TerminalOrbitSemiMajorAxis ?? double.NaN;
                srf.TerminalEcc = traj?.TerminalOrbitEccentricity ?? double.NaN;
            }
            // State-change-driven so a stuck (source, reason) decision doesn't
            // re-emit on a wall-clock cadence. Identity scopes the cache per
            // (operation, recording); the state key encodes (source, reason)
            // so any decision flip reopens the gate.
            ParsekLog.VerboseOnChange(
                Tag,
                string.Format(ic,
                    "gm-source-resolve-{0}-{1}",
                    logOperationName ?? "(none)",
                    recId),
                string.Format(ic,
                    "{0}|{1}",
                    source,
                    string.IsNullOrEmpty(reason) ? "none" : reason),
                BuildGhostMapDecisionLine(srf));
        }

        internal static bool ShouldOverrideVisibleSegmentWithEndpointTail(
            OrbitSegment selectedSegment,
            string preferredEndpointBody,
            string endpointSeedSource,
            TailDerivedOrbitSeed tailSeed,
            bool terminalMapPresenceRegion,
            bool loopMemberInWindow = false)
        {
            // A loop member replaying INSIDE its window is mid-flight by definition: the covering
            // segment at the loop effUT is the truth, and the endpoint-tail "fresher than the
            // stored segments" staleness comparison is meaningless against a loop-mapped sample
            // UT (the 2026-06-12 playtest: a docked-ending recording's garbage tail orbit -
            // ecc 0.94, retrograde - overrode the correct first parking segment at every proto
            // re-create, snapping the map icon off its line at warp transitions). This makes the
            // long-documented "unconditionally suppressed for loop members" contract REAL on the
            // create-resolver path; the TS update pass already enforced it via
            // EndpointTailAllowedInTrackingStationUpdate.
            if (loopMemberInWindow)
                return false;
            if (!terminalMapPresenceRegion)
                return false;
            if (!tailSeed.Accepted || !tailSeed.UsedHistoricalBodyRotation)
                return false;
            if (string.IsNullOrEmpty(preferredEndpointBody)
                || !string.Equals(selectedSegment.bodyName, preferredEndpointBody, StringComparison.Ordinal)
                || !string.Equals(tailSeed.BodyName, preferredEndpointBody, StringComparison.Ordinal))
            {
                return false;
            }
            if (!string.Equals(endpointSeedSource, "endpoint-segment", StringComparison.Ordinal))
                return false;
            if (!IsFinite(tailSeed.TailUT)
                || !IsFinite(tailSeed.LatestStoredSegmentEndUT)
                || tailSeed.TailUT <= tailSeed.LatestStoredSegmentEndUT + OrbitSeedResolver.TailDerivedOrbitFreshnessEpsilon)
            {
                return false;
            }
            if (selectedSegment.endUT > tailSeed.LatestStoredSegmentEndUT + OrbitSeedResolver.TailDerivedOrbitFreshnessEpsilon)
                return false;
            if (SegmentContainsUT(selectedSegment, tailSeed.TailUT))
                return false;

            return true;
        }

        internal static bool IsTerminalMapPresenceRegion(
            IPlaybackTrajectory traj,
            double currentUT)
        {
            if (traj == null)
                return false;
            return IsTerminalStateEligibleForTerminalOrbitMapPresence(traj.TerminalStateValue)
                && currentUT >= PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(traj);
        }

        /// <summary>
        /// Pure: should the terminal-orbit synthesis be allowed for this recording
        /// under a non-zero loop epoch shift?
        ///
        /// Compares the recording's last sampled point's body against the
        /// terminal-orbital reference body (<see cref="IPlaybackTrajectory.TerminalOrbitBody"/>
        /// DIRECTLY, not via <c>TryGetPreferredEndpointBodyName</c> which for a
        /// no-OrbitSegment recording would return the last-point body and make this
        /// predicate tautological).
        ///
        /// Same-body terminals (recording ended in body B's orbit having last
        /// sampled in body B's SOI) are safe: the synthesized orbit is around the
        /// same body the recording is anchored to at its end, so the loop epoch
        /// shift propagates inertially (inertial Keplerian elements survive the
        /// shift; only the epoch advances). Cross-body terminals (last sampled in
        /// body A but TerminalOrbitBody = B) are the 181 Mm bug class and stay
        /// suppressed.
        ///
        /// Defensive guard: a recording could have non-empty
        /// <see cref="IPlaybackTrajectory.TerminalOrbitBody"/> but
        /// <see cref="IPlaybackTrajectory.TerminalOrbitSemiMajorAxis"/> = 0
        /// (uninitialised). The downstream <c>endpoint-terminal-orbit</c> source
        /// path requires <c>HasRecordedTerminalOrbit</c>; suppress the
        /// loop-accept here so the predicate's truth value matches downstream
        /// behaviour.
        /// </summary>
        internal static bool IsTerminalOrbitSynthesisSafeForLoopMember(IPlaybackTrajectory traj)
        {
            // This is a PURE per-frame predicate called from the flight pending-create and
            // TS lifecycle passes for every loop member every tick. Its result is stable for
            // a given recording, so all six log paths are rate-limited per recording (shared
            // key) to one line per interval — without this it produces thousands of identical
            // lines per session (5212 in the 2026-05-29 capture). Callers that care about the
            // bool log the acted-on decision separately.
            if (traj == null)
            {
                ParsekLog.VerboseRateLimited(Tag,
                    "terminal-orbit-safe-null",
                    "IsTerminalOrbitSynthesisSafeForLoopMember: result=false reason=null-trajectory",
                    10.0);
                return false;
            }
            string recId = traj.RecordingId ?? "(null)";
            string safeKey = "terminal-orbit-safe-" + recId;
            if (traj.Points == null || traj.Points.Count == 0)
            {
                ParsekLog.VerboseRateLimited(Tag, safeKey,
                    string.Format(ic,
                        "IsTerminalOrbitSynthesisSafeForLoopMember: rec={0} result=false reason=empty-points",
                        recId),
                    10.0);
                return false;
            }
            string terminalBody = traj.TerminalOrbitBody;
            if (string.IsNullOrEmpty(terminalBody))
            {
                ParsekLog.VerboseRateLimited(Tag, safeKey,
                    string.Format(ic,
                        "IsTerminalOrbitSynthesisSafeForLoopMember: rec={0} result=false reason=no-terminal-orbit-body",
                        recId),
                    10.0);
                return false;
            }
            if (traj.TerminalOrbitSemiMajorAxis <= 0.0)
            {
                ParsekLog.VerboseRateLimited(Tag, safeKey,
                    string.Format(ic,
                        "IsTerminalOrbitSynthesisSafeForLoopMember: rec={0} result=false reason=zero-terminal-sma terminalBody={1}",
                        recId,
                        terminalBody),
                    10.0);
                return false;
            }
            string lastPointBody = traj.Points[traj.Points.Count - 1].bodyName;
            if (string.IsNullOrEmpty(lastPointBody))
            {
                ParsekLog.VerboseRateLimited(Tag, safeKey,
                    string.Format(ic,
                        "IsTerminalOrbitSynthesisSafeForLoopMember: rec={0} result=false reason=no-last-point-body terminalBody={1}",
                        recId,
                        terminalBody),
                    10.0);
                return false;
            }
            bool sameBody = string.Equals(lastPointBody, terminalBody, StringComparison.Ordinal);
            ParsekLog.VerboseRateLimited(Tag, safeKey,
                string.Format(ic,
                    "IsTerminalOrbitSynthesisSafeForLoopMember: rec={0} result={1} reason={2} terminalBody={3} lastPointBody={4}",
                    recId,
                    sameBody,
                    sameBody ? "same-body" : "cross-body-terminal",
                    terminalBody,
                    lastPointBody),
                10.0);
            return sameBody;
        }

        /// <summary>
        /// Defense-in-depth check used inside <see cref="TryResolveEndpointTailForMapPresence"/>
        /// when the persisted-phase relaxation accepts a <c>TrajectoryPoint</c> phase.
        /// Compares the persisted endpoint body against the recording's terminal-orbit
        /// reference body. The outer call site predicate
        /// (<see cref="IsTerminalOrbitSynthesisSafeForLoopMember"/>) compares last-point body
        /// against terminal-orbit body; this inner check compares the persisted endpoint body
        /// against terminal-orbit body. Both must agree before the relaxed source is accepted
        /// so a future <c>RefreshEndpointDecision</c> that diverges the persisted body from the
        /// last-point body cannot cause cross-body synthesis.
        /// </summary>
        internal static bool IsTrackingTerminalOrbitBody(IPlaybackTrajectory traj, string endpointBodyName)
        {
            if (traj == null) return false;
            string terminalBody = traj.TerminalOrbitBody;
            if (string.IsNullOrEmpty(terminalBody) || string.IsNullOrEmpty(endpointBodyName))
                return false;
            return string.Equals(endpointBodyName, terminalBody, StringComparison.Ordinal);
        }

        internal static bool TryResolveEndpointTailForMapPresence(
            IPlaybackTrajectory traj,
            double currentUT,
            OrbitSegment? selectedSegment,
            bool terminalMapPresenceRegion,
            out OrbitSegment endpointTailSegment,
            out TailDerivedOrbitSeed tailSeed,
            out string detail,
            bool acceptTerminalOrbitSource = false,
            bool loopMemberInWindow = false)
        {
            endpointTailSegment = default(OrbitSegment);
            tailSeed = default(TailDerivedOrbitSeed);
            detail = null;

            if (traj == null || !terminalMapPresenceRegion)
                return false;

            if (!RecordingEndpointResolver.TryGetPreferredEndpointBodyName(
                    traj,
                    out string preferredEndpointBody))
            {
                return false;
            }

            if (OrbitSeedResolver.TailSeedResolverForTesting == null
                && !OrbitSeedResolver.TryFindLatestCoastTrajectoryFrame(
                    traj,
                    preferredEndpointBody,
                    out _,
                    out _))
            {
                return false;
            }

            CelestialBody body = FindBodyByName(preferredEndpointBody);
            bool tailAccepted = OrbitSeedResolver.TryDeriveTailOrbitSeed(
                traj,
                body,
                currentUT,
                TailSeedUse.MapPresence,
                out tailSeed);
            if (!tailAccepted)
            {
                detail = FormatEndpointTailSeedDetail(tailSeed, accepted: false);
                return false;
            }

            string endpointSeedSource = null;
            RecordingEndpointResolver.TryGetEndpointAlignedOrbitSeed(
                traj,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                out RecordingEndpointResolver.EndpointOrbitSeedDiagnostics endpointDiagnostics);
            endpointSeedSource = endpointDiagnostics.Source;

            RecordingEndpointResolver.TryGetPersistedEndpointDecision(
                traj,
                out RecordingEndpointPhase endpointPhase,
                out string endpointBodyName);

            // section 1.1 relaxed inner gate (split into source-half and persisted-phase-half).
            // Default (acceptTerminalOrbitSource=false) collapses to the original
            // "endpoint-segment source AND OrbitSegment persisted phase with matching body"
            // contract; passing true additionally accepts the same-body
            // "endpoint-terminal-orbit" source plus a TrajectoryPoint persisted phase whose
            // body matches the recording's terminal-orbit body. Defence in depth: the
            // persisted-phase relaxation requires IsTrackingTerminalOrbitBody so a future
            // RefreshEndpointDecision that diverges the persisted body from the last-point
            // body cannot accidentally cross-body-synthesise.
            bool sourceAccepted =
                string.Equals(endpointSeedSource, "endpoint-segment", StringComparison.Ordinal)
                || (acceptTerminalOrbitSource
                    && string.Equals(endpointSeedSource, "endpoint-terminal-orbit", StringComparison.Ordinal));

            bool persistedPhaseAccepted =
                (endpointPhase == RecordingEndpointPhase.OrbitSegment
                    && string.Equals(endpointBodyName, preferredEndpointBody, StringComparison.Ordinal))
                || (acceptTerminalOrbitSource
                    && endpointPhase == RecordingEndpointPhase.TrajectoryPoint
                    && IsTrackingTerminalOrbitBody(traj, endpointBodyName));

            if (!sourceAccepted || !persistedPhaseAccepted)
            {
                TailDerivedOrbitSeed declinedSeed = tailSeed;
                declinedSeed.DeclineReason =
                    !sourceAccepted
                        ? "endpoint-seed-not-segment"
                        : "endpoint-family-not-segment";
                detail = CombineSourceDetails(
                    FormatEndpointTailSeedDetail(declinedSeed, accepted: false),
                    string.Format(ic,
                        "endpointSeedSource={0} endpointPhase={1} endpointBody={2} acceptTerminalOrbitSource={3}",
                        endpointSeedSource ?? "(null)",
                        endpointPhase,
                        endpointBodyName ?? "(null)",
                        acceptTerminalOrbitSource));
                return false;
            }

            if (acceptTerminalOrbitSource
                && (!string.Equals(endpointSeedSource, "endpoint-segment", StringComparison.Ordinal)
                    || endpointPhase == RecordingEndpointPhase.TrajectoryPoint))
            {
                // Log the relaxation acceptance so a KSP.log capture can confirm the
                // loop-aware path actually fired (vs. the legacy endpoint-segment route).
                // Rate-limited per recording: a non-zero loop epoch shift is the STEADY
                // STATE for a member's whole loop window (not an edge), and this resolver
                // runs every refresh tick (now 4 Hz in the TS), so a raw Verbose here
                // would fire every frame for a same-body loop member sitting in its
                // terminal-orbit phase.
                ParsekLog.VerboseRateLimited(Tag,
                    "endpoint-tail-loop-accept-" + (traj.RecordingId ?? "(null)"),
                    string.Format(ic,
                        "endpoint-tail-synthesis-loop-accept rec={0} terminalBody={1} endpointSeedSource={2} endpointPhase={3} endpointBody={4} tailUT={5:F2} tailSma={6:F1}",
                        traj.RecordingId ?? "(null)",
                        traj.TerminalOrbitBody ?? "(null)",
                        endpointSeedSource ?? "(null)",
                        endpointPhase,
                        endpointBodyName ?? "(null)",
                        tailSeed.TailUT,
                        tailSeed.Segment.semiMajorAxis),
                    5.0);
            }

            if (selectedSegment.HasValue
                && !ShouldOverrideVisibleSegmentWithEndpointTail(
                    selectedSegment.Value,
                    preferredEndpointBody,
                    endpointSeedSource,
                    tailSeed,
                    terminalMapPresenceRegion,
                    loopMemberInWindow))
            {
                TailDerivedOrbitSeed declinedSeed = tailSeed;
                declinedSeed.DeclineReason = "visible-segment-not-stale-endpoint";
                detail = CombineSourceDetails(
                    FormatEndpointTailSeedDetail(declinedSeed, accepted: false),
                    string.Format(ic,
                        "endpointSeedSource={0} endpointPhase={1} endpointBody={2}",
                        endpointSeedSource ?? "(null)",
                        endpointPhase,
                        endpointBodyName ?? "(null)"));
                return false;
            }

            endpointTailSegment = tailSeed.Segment;
            detail = FormatEndpointTailSeedDetail(tailSeed, accepted: true);
            return true;
        }

        private static bool SegmentContainsUT(OrbitSegment segment, double ut)
        {
            return IsFinite(ut)
                && ut >= segment.startUT - OrbitSeedResolver.TailDerivedOrbitFreshnessEpsilon
                && ut <= segment.endUT + OrbitSeedResolver.TailDerivedOrbitFreshnessEpsilon;
        }

        private static string FormatEndpointTailSeedDetail(
            TailDerivedOrbitSeed seed,
            bool accepted)
        {
            return string.Format(ic,
                "endpointTailSeed={0} tailDecline={1} tailUT={2:F2} tailSma={3:F1} tailEcc={4:F6} latestSegmentEndUT={5:F2} drift={6:F2}s tailFrame={7} historicalRotation={8} historicalLon={9:F4} tailVelFrame={10} tailYaw={11:F3}deg tailResidual={12:F3} tailInc={13:F4}",
                accepted ? "accept" : "decline",
                seed.DeclineReason ?? "(none)",
                seed.TailUT,
                seed.Segment.semiMajorAxis,
                seed.Segment.eccentricity,
                seed.LatestStoredSegmentEndUT,
                seed.RotationDriftSeconds,
                seed.TailFrameSource ?? "(none)",
                seed.UsedHistoricalBodyRotation,
                seed.HistoricalLongitude,
                double.IsNaN(seed.VelocityFrameYawDegrees)
                    ? "(unresolved)"
                    : (seed.VelocityWasRotatingFrame ? "rotating" : "inertial"),
                seed.VelocityFrameYawDegrees,
                seed.VelocityFrameResidual,
                accepted ? seed.Segment.inclination : double.NaN);
        }

        private static string FormatEndpointTailBypassDetail(
            string reason,
            string endpointBodyName,
            RecordingEndpointPhase endpointPhase,
            string persistedEndpointBodyName)
        {
            return string.Format(ic,
                "endpointTailSeed=bypass tailDecline={0} tailUT=NaN tailSma=NaN tailEcc=NaN latestSegmentEndUT=NaN drift=NaNs tailFrame=(none) historicalRotation=False endpointPhase={1} endpointBody={2} preferredEndpointBody={3}",
                reason ?? "(none)",
                endpointPhase,
                persistedEndpointBodyName ?? "(null)",
                endpointBodyName ?? "(none)");
        }

        private static string BuildEndpointTailBypassDetail(
            IPlaybackTrajectory traj,
            double currentUT,
            bool terminalMapPresenceRegion)
        {
            if (traj == null)
            {
                return FormatEndpointTailBypassDetail(
                    "null-trajectory",
                    null,
                    RecordingEndpointPhase.Unknown,
                    null);
            }

            RecordingEndpointResolver.TryGetPersistedEndpointDecision(
                traj,
                out RecordingEndpointPhase endpointPhase,
                out string persistedEndpointBodyName);

            if (!terminalMapPresenceRegion)
            {
                return FormatEndpointTailBypassDetail(
                    "not-terminal-map-presence",
                    null,
                    endpointPhase,
                    persistedEndpointBodyName);
            }

            if (!RecordingEndpointResolver.TryGetPreferredEndpointBodyName(
                    traj,
                    out string preferredEndpointBody))
            {
                return FormatEndpointTailBypassDetail(
                    "no-endpoint-body",
                    null,
                    endpointPhase,
                    persistedEndpointBodyName);
            }

            if (OrbitSeedResolver.TailSeedResolverForTesting == null
                && !OrbitSeedResolver.TryFindLatestCoastTrajectoryFrame(
                    traj,
                    preferredEndpointBody,
                    out _,
                    out _))
            {
                return FormatEndpointTailBypassDetail(
                    "no-absolute-coast-tail",
                    preferredEndpointBody,
                    endpointPhase,
                    persistedEndpointBodyName);
            }

            return FormatEndpointTailBypassDetail(
                "not-evaluated",
                preferredEndpointBody,
                endpointPhase,
                persistedEndpointBodyName);
        }

        private static string BuildEndpointTailTrackingStationDetail(
            Recording rec,
            double currentUT,
            TrackingStationGhostSource source,
            OrbitSegment segment)
        {
            bool terminalMapPresenceRegion = IsTerminalMapPresenceRegion(rec, currentUT);
            OrbitSegment? selectedSegment = source == TrackingStationGhostSource.EndpointTail
                ? (OrbitSegment?)null
                : segment;

            TryResolveEndpointTailForMapPresence(
                rec,
                currentUT,
                selectedSegment,
                terminalMapPresenceRegion,
                out _,
                out _,
                out string endpointTailDetail);

            if (!string.IsNullOrEmpty(endpointTailDetail))
                return endpointTailDetail;

            return BuildEndpointTailBypassDetail(
                rec,
                currentUT,
                terminalMapPresenceRegion);
        }

        /// <summary>
        /// Seam for <see cref="ResolveMapPresenceChainSegments"/>: given a trajectory that carries
        /// no orbit segments of its own, resolve its chain's EFFECTIVE TIP and that tip's segment
        /// list. The live implementation walks
        /// <see cref="EffectiveState.EffectiveTipRecordingId(string, IReadOnlyList{RecordingSupersedeRelation})"/>
        /// (HEAD -&gt; chain -&gt; TIP -&gt; supersede -&gt; fork); xUnit injects a synthetic one.
        /// </summary>
        internal delegate bool MapPresenceChainTipResolver(
            IPlaybackTrajectory traj, out string tipRecordingId, out List<OrbitSegment> tipSegments);

        /// <summary>
        /// PURE (PREDICTED-TAIL defect B, 2026-09-08): which orbit-segment list answers map
        /// presence for this recording?
        ///
        /// An ordinary optimizer environment split cuts one flight into HEAD + TIP and the
        /// scene-exit predicted tail lands on the TIP. Map-presence source resolution reads the
        /// recording it was handed, so a HEAD answered <c>hasOrbitSegments=False</c> at every tick,
        /// both segment-seeded ghost sources failed, and the ghost was re-sourced
        /// <c>orbitSource=state-vector-fallback</c> - an instantaneous ellipse the vessel never
        /// flew. This resolves the SEGMENT LOOKUP through the chain's effective tip.
        ///
        /// Strictly additive: a recording with segments of its own NEVER consults the tip, so every
        /// non-chain recording is byte-identical. Callers must keep every OTHER branch
        /// (<c>considerStateVector</c>, the skip-reason split) on the recording's OWN
        /// <c>HasOrbitSegments</c> - widening those would retire a HEAD's own state-vector ghost
        /// inside its own recorded span.
        /// </summary>
        internal static List<OrbitSegment> ResolveMapPresenceChainSegments(
            IPlaybackTrajectory traj,
            MapPresenceChainTipResolver resolveChainTip,
            out string tipRecordingId,
            out int tipSegmentCount)
        {
            tipRecordingId = null;
            tipSegmentCount = 0;
            if (traj == null) return null;

            List<OrbitSegment> own = traj.OrbitSegments;
            if (own != null && own.Count > 0) return own;
            if (resolveChainTip == null) return own;

            if (!resolveChainTip(traj, out string tipId, out List<OrbitSegment> tipSegments))
                return own;
            if (tipSegments == null || tipSegments.Count == 0) return own;

            tipRecordingId = tipId;
            tipSegmentCount = tipSegments.Count;
            return tipSegments;
        }

        /// <summary>
        /// Live <see cref="MapPresenceChainTipResolver"/>. Only a CHAIN MEMBER consults the walker -
        /// a standalone recording with no segments has no tip to hop to and must not pay the scan.
        /// [ERS-exempt] rationale: this file is already allowlisted; the tip walk itself routes
        /// through <see cref="EffectiveState"/>, which is the supersede authority.
        /// </summary>
        private static bool ResolveLiveChainTipSegments(
            IPlaybackTrajectory traj, out string tipRecordingId, out List<OrbitSegment> tipSegments)
        {
            tipRecordingId = null;
            tipSegments = null;
            Recording tip = ResolveLiveSegmentBearingChainTip(traj);
            if (tip == null) return false;
            tipRecordingId = tip.RecordingId;
            tipSegments = tip.OrbitSegments;
            return true;
        }

        /// <summary>
        /// Live <see cref="MapPresenceChainTipTerminalResolver"/>: the SAME tip
        /// <see cref="ResolveLiveChainTipSegments"/> lends its segments from, reporting its terminal.
        /// </summary>
        private static bool ResolveLiveChainTipTerminal(
            IPlaybackTrajectory traj, out string tipRecordingId, out TerminalState? tipTerminal)
        {
            tipRecordingId = null;
            tipTerminal = null;
            Recording tip = ResolveLiveSegmentBearingChainTip(traj);
            if (tip == null) return false;
            tipRecordingId = tip.RecordingId;
            tipTerminal = tip.TerminalStateValue;
            return true;
        }

        /// <summary>
        /// The committed effective tip of <paramref name="traj"/>'s chain when it is a different
        /// recording that carries orbit segments, else null. Shared by the segment lender and the
        /// eligibility lender so the two can never name different tips.
        /// </summary>
        private static Recording ResolveLiveSegmentBearingChainTip(IPlaybackTrajectory traj)
        {
            var rec = traj as Recording;
            if (rec == null || string.IsNullOrEmpty(rec.ChainId)
                || string.IsNullOrEmpty(rec.RecordingId))
                return null;

            int liveSupersedeVersion = ParsekScenario.Instance?.SupersedeStateVersion ?? 0;
            if (chainTipSegmentsCacheVersion != RecordingStore.StateVersion
                || chainTipSegmentsCacheSupersedeVersion != liveSupersedeVersion)
            {
                chainTipSegmentsCache.Clear();
                chainTipSegmentsCacheVersion = RecordingStore.StateVersion;
                chainTipSegmentsCacheSupersedeVersion = liveSupersedeVersion;
            }

            if (!chainTipSegmentsCache.TryGetValue(rec.RecordingId, out string tipId))
            {
                IReadOnlyList<RecordingSupersedeRelation> supersedes =
                    ParsekScenario.Instance?.RecordingSupersedes;
                // A rendering read, not a slot question: borrow the tip's orbit
                // segments across chain / switch hops only, never across the vessel's
                // own EVA / Board branch points the slot walk follows.
                tipId = EffectiveState.EffectiveTipRecordingId(
                    rec.RecordingId, supersedes, recById: null, treeContext: null,
                    followOwnEvaBoard: false);
                chainTipSegmentsCache[rec.RecordingId] = tipId ?? string.Empty;
            }

            if (string.IsNullOrEmpty(tipId)
                || string.Equals(tipId, rec.RecordingId, StringComparison.Ordinal))
                return null;

            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            if (committed == null) return null;
            for (int i = 0; i < committed.Count; i++)
            {
                Recording candidate = committed[i];
                if (candidate == null) continue;
                if (!string.Equals(candidate.RecordingId, tipId, StringComparison.Ordinal)) continue;
                if (candidate.OrbitSegments == null || candidate.OrbitSegments.Count == 0)
                    return null;
                return candidate;
            }
            return null;
        }

        /// <summary>
        /// Test seam / alternate source for the chain tip's terminal, mirroring
        /// <see cref="MapPresenceChainTipResolver"/>: false when the recording borrows no tip.
        /// </summary>
        internal delegate bool MapPresenceChainTipTerminalResolver(
            IPlaybackTrajectory traj, out string tipRecordingId, out TerminalState? tipTerminal);

        /// <summary>
        /// PURE (PREDICTED-TAIL-CHAIN-HEAD-HOLDS-MAP-PRESENCE): the terminal the map-presence
        /// eligibility gate (<see cref="IsTerminalStateEligibleForMapPresence"/>) reads.
        ///
        /// A chain member with no orbit segments of its own borrows its effective tip's segments
        /// (<see cref="ResolveMapPresenceChainSegments"/>), so it stands in for that tip on the map
        /// and must take the tip's ending along with its orbit. Without that, the climb HEAD of a
        /// flight the optimizer split at the atmosphere edge (terminal moved to the TIP, HEAD's
        /// nulled) passed the gate the Destroyed TIP fails, and drew a stock map vessel along the
        /// crash coast that the same flight stored unsplit never gets.
        ///
        /// Same additive condition as the segment lender: a recording with segments of its own
        /// never consults the tip and keeps its own terminal, so a middle member with its own orbit
        /// keeps its map vessel whatever the flight's ending. A recording whose own terminal already
        /// fails the gate stays refused.
        /// </summary>
        internal static TerminalState? ResolveMapPresenceEligibilityTerminal(
            IPlaybackTrajectory traj,
            MapPresenceChainTipTerminalResolver resolveChainTip,
            out string tipRecordingId)
        {
            tipRecordingId = null;
            if (traj == null) return null;
            TerminalState? own = traj.TerminalStateValue;
            if (!IsTerminalStateEligibleForMapPresence(own)) return own;
            if (traj.OrbitSegments != null && traj.OrbitSegments.Count > 0) return own;
            if (resolveChainTip == null) return own;
            if (!resolveChainTip(traj, out string tipId, out TerminalState? tipTerminal))
                return own;
            if (IsTerminalStateEligibleForMapPresence(tipTerminal)) return own;
            tipRecordingId = tipId;
            return tipTerminal;
        }

        /// <summary>
        /// Live form of <see cref="ResolveMapPresenceEligibilityTerminal"/> over the committed store.
        /// </summary>
        internal static TerminalState? ResolveMapPresenceEligibilityTerminal(
            IPlaybackTrajectory traj, out string tipRecordingId)
        {
            return ResolveMapPresenceEligibilityTerminal(
                traj, ResolveLiveChainTipTerminal, out tipRecordingId);
        }

        internal static TrackingStationGhostSource ResolveMapPresenceGhostSource(
            IPlaybackTrajectory traj,
            bool isSuppressed,
            bool alreadyMaterialized,
            double currentUT,
            bool allowTerminalOrbitFallback,
            string logOperationName,
            ref int stateVectorCachedIndex,
            out OrbitSegment segment,
            out TrajectoryPoint stateVectorPoint,
            out string skipReason,
            int recordingIndex = -1,
            bool allowSoiGapStateVectorFallback = false,
            string expectedSoiGapBody = null,
            bool acceptTerminalOrbitForLoopSynthesis = false,
            bool loopMemberInWindow = false,
            bool liveLaunchMatchedAnchorOfActiveMember = false,
            bool transferMemberDescentContinuation = false)
        {
            segment = default(OrbitSegment);
            stateVectorPoint = default(TrajectoryPoint);
            skipReason = null;
            string recId = traj?.RecordingId ?? "(null)";

            // These mirrors of the out parameters exist solely so the ReturnDecision
            // local function can read the resolved segment / state point at the
            // moment a decision is finalised. C# 7 forbids capturing `out` /
            // `ref` parameters in nested closures, so we keep these locals in sync
            // immediately before each ReturnDecision call. Both the unstructured
            // diagnostic summary (CombineSourceDetails / BuildGhostSourceStructuredDetail)
            // and the structured GhostMap decision-line emission
            // (EmitSourceResolveLine) read these mirrors.
            OrbitSegment resolvedSegment = default(OrbitSegment);
            TrajectoryPoint resolvedStatePoint = default(TrajectoryPoint);

            TrackingStationGhostSource ReturnDecision(
                TrackingStationGhostSource source,
                string reason,
                string detail = null)
            {
                string combinedDetail = CombineSourceDetails(
                    detail,
                    BuildGhostSourceStructuredDetail(
                        traj,
                        currentUT,
                        source,
                        resolvedSegment,
                        resolvedStatePoint));
                if (!string.IsNullOrEmpty(logOperationName))
                {
                    // Per-frame caller — emit only when the (source, reason) decision
                    // flips for this (operation, recording) pair. A stable
                    // (None, state-vector-threshold) loop in the pending-create queue
                    // would otherwise pour ~one line per recording per second; here
                    // we emit once on entry into the state and again only when the
                    // state actually changes. Suppressed-count is preserved on the
                    // next emission so post-hoc audits can reconstruct the volume.
                    ParsekLog.VerboseOnChange(
                        Tag,
                        string.Format(ic,
                            "map-ghost-source-{0}-{1}",
                            logOperationName,
                            recId),
                        string.Format(ic,
                            "{0}|{1}",
                            source,
                            reason ?? "none"),
                        string.Format(ic,
                            "{0}: rec={1} currentUT={2:F1} source={3} reason={4}{5}",
                            logOperationName,
                            recId,
                            currentUT,
                            source,
                            reason ?? "(none)",
                            string.IsNullOrEmpty(combinedDetail) ? string.Empty : " " + combinedDetail));

                    // Structured-line emission so the post-hoc reader can grep one
                    // canonical shape across every decision in this file. Local
                    // copies of segment / stateVectorPoint are captured into
                    // resolvedSegment / resolvedStatePoint at each ReturnDecision
                    // call site since the C# spec forbids closing over `out`/`ref`
                    // parameters.
                    EmitSourceResolveLine(
                        traj,
                        recId,
                        recordingIndex,
                        source,
                        reason,
                        currentUT,
                        logOperationName,
                        resolvedSegment,
                        resolvedStatePoint);
                }
                return source;
            }

            if (traj == null)
            {
                skipReason = "null";
                return ReturnDecision(TrackingStationGhostSource.None, skipReason, "null trajectory");
            }

            if (traj.IsDebris)
            {
                skipReason = "debris";
                return ReturnDecision(TrackingStationGhostSource.None, skipReason, "isDebris=True");
            }

            // GUI-P1: the player's per-recording playback tick box is OFF. Decline the whole
            // map presence here - no ProtoVessel means no map icon, no orbit line and no
            // Tracking Station row (a stock TS row exists iff the proto does), on BOTH the
            // flight-map create path (which passes isSuppressed:false unconditionally) and the
            // Tracking Station one. Placed BEFORE the covering-segment / EndpointTail
            // resolution per Appendix A: a decline inside the endpoint-tail resolver falls
            // through to a stale launch/transfer segment. The retire side of the same gate is
            // GetTrackingStationGhostRemovalReason, so an existing proto goes away on the next
            // lifecycle tick rather than lingering until the recording expires.
            if (IsMapPresenceHiddenByPlaybackToggle(traj))
            {
                skipReason = TrackingStationGhostSkipPlaybackDisabled;
                LogMapPresencePlaybackSuppressedOnChange(
                    "map-presence", traj.RecordingId, (traj as Recording)?.VesselName);
                return ReturnDecision(
                    TrackingStationGhostSource.None, skipReason, "playbackEnabled=False");
            }

            if (isSuppressed)
            {
                skipReason = TrackingStationGhostSkipSuppressed;
                return ReturnDecision(TrackingStationGhostSource.None, skipReason, "isSuppressed=True");
            }

            // A persisted real vessel (a completed mission's craft materialized at its
            // terminal, e.g. parked in orbit) normally suppresses a duplicate map ghost.
            // But when this recording is a Mission-loop member replaying inside its loop
            // window (loopMemberInWindow), that real vessel sits frozen at the mission's
            // FINAL state while the loop replays an EARLIER phase somewhere else, so
            // suppressing here leaves the looped leg with no map trajectory following the
            // ghost (the "no trajectory after the Mun takeoff" report). Per the loop-render
            // decision we draw the animated loop ghost ALONGSIDE the real vessel in that
            // case (both icons may show). Re-fly / active-session duplicates stay blocked
            // because isSuppressed is evaluated above this gate.
            // Step 2 (Logistics route live-anchor bind): when this recording's
            // guid-gated launch-matched live vessel is loaded AND it is the LIVE docking
            // anchor a relative member is binding to this-or-last frame (the Step-1
            // live-bind event), its own loop ghost is a pure duplicate of the live
            // station at that moment (the dependent member docks against the live vessel,
            // not this recorded position ~20 km away). Suppress it like a non-loop
            // already-spawned vessel even though loopMemberInWindow is true (the caller
            // feeds the live-bind-event term here, not a whole-loop existence check).
            // Every other loopMemberInWindow case still draws alongside the real vessel.
            if (alreadyMaterialized && (!loopMemberInWindow || liveLaunchMatchedAnchorOfActiveMember))
            {
                skipReason = liveLaunchMatchedAnchorOfActiveMember
                    ? TrackingStationGhostSkipLiveAnchorDouble
                    : TrackingStationGhostSkipAlreadySpawned;
                if (liveLaunchMatchedAnchorOfActiveMember)
                {
                    ParsekLog.VerboseRateLimited(Tag,
                        string.Format(ic, "anchor-double-suppressed-{0}", recId),
                        string.Format(ic,
                            "anchor-double-suppressed (live-bind event): rec={0} anchorPid={1} "
                            + "reason=launch-matched-live-vessel-loaded-and-live-bound",
                            recId,
                            (traj as Recording)?.VesselPersistentId ?? 0u),
                        5.0);
                    return ReturnDecision(
                        TrackingStationGhostSource.None,
                        skipReason,
                        "live-anchor double suppressed");
                }
                return ReturnDecision(TrackingStationGhostSource.None, skipReason, "already spawned");
            }
            if (alreadyMaterialized)
            {
                ParsekLog.VerboseRateLimited(Tag,
                    string.Format(ic, "loop-ghost-alongside-real-{0}", recId),
                    string.Format(ic,
                        "ResolveMapPresenceGhostSource: loop member rec={0} drawing map ghost "
                        + "alongside persisted real vessel (loopMemberInWindow)",
                        recId),
                    5.0);
            }

            var terminal = ResolveMapPresenceEligibilityTerminal(traj, out string terminalTipId);
            if (!IsTerminalStateEligibleForMapPresence(terminal))
            {
                skipReason = "terminal-" + terminal.Value;
                return ReturnDecision(
                    TrackingStationGhostSource.None,
                    skipReason,
                    string.IsNullOrEmpty(terminalTipId)
                        ? string.Format(ic, "terminal={0}", terminal.Value)
                        : string.Format(ic, "terminal={0} chainTip={1}", terminal.Value, terminalTipId));
            }

            double activationStartUT = PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(traj);
            bool terminalMapPresenceRegion = IsTerminalMapPresenceRegion(traj, currentUT);

            // COSMETIC fix (re-aim descent "post-landing suborbital looping ghost"): the descent-trigger
            // TRANSFER member, once the shared descent has handed off / landed (descent phase Descent or Done,
            // computed at the LIVE clock by the caller via
            // GhostPlaybackLogic.IsTransferMemberDescentContinuation), has finished its recorded journey at the
            // shifted parking deorbit point. With no covering OrbitSegment past that conic it would otherwise
            // synthesize an EndpointTail coast from the recorded deorbit endpoint = a sub-surface ellipse drawn
            // as a closed loop. RETIRE the ghost cleanly here, BEFORE the covering-segment / EndpointTail /
            // Segment resolution below, so there is NOTHING to fall back to (this is the J2 trap the reverted
            // 9fecdfcb6 hit: a decline INSIDE the endpoint-tail resolver fell through to a stale launch/transfer
            // Segment - Sun/Ike - and put a clickable ghost on the wrong body). The pre-seam loiter parking conic
            // is preserved because the flag is FALSE in Inert/Loiter, so the normal covering-segment branch runs
            // unchanged. Default false keeps every non-opted-in caller byte-identical.
            if (transferMemberDescentContinuation)
            {
                skipReason = "transfer-member-descent-continuation";
                return ReturnDecision(
                    TrackingStationGhostSource.None,
                    skipReason,
                    "descent-trigger transfer member handed off / landed: retire cleanly; "
                        + "the descent set owns the visual (no sub-surface endpoint-tail, no wrong-segment fallback)");
            }

            string checkpointFallbackDetail = null;
            string checkpointFallbackRejectReason = null;

            // PREDICTED-TAIL defect B: the SEGMENT LOOKUP (only) resolves through the chain's
            // effective tip, so a HEAD whose optimizer split moved every conic onto the TIP can
            // still reach the segment-seeded ghost sources instead of falling to
            // state-vector-fallback. Everything below keeps reading traj.HasOrbitSegments.
            List<OrbitSegment> mapSegments = ResolveMapPresenceChainSegments(
                traj,
                ResolveLiveChainTipSegments,
                out string chainTipRecordingId,
                out int chainTipSegmentCount);
            bool hasMapSegments = mapSegments != null && mapSegments.Count > 0;
            if (!string.IsNullOrEmpty(chainTipRecordingId))
            {
                ParsekLog.VerboseOnChange(
                    Tag,
                    string.Format(ic, "map-presence-chain-tip-{0}", recId),
                    chainTipRecordingId,
                    string.Format(ic,
                        "map-presence chain-tip segments: head={0} tip={1} tipSegments={2} "
                        + "headSegments={3}",
                        recId,
                        chainTipRecordingId,
                        chainTipSegmentCount,
                        traj.OrbitSegments?.Count ?? 0));
            }

            if (hasMapSegments)
            {
                OrbitSegment? currentSegment =
                    TrajectoryMath.FindOrbitSegmentForMapDisplay(mapSegments, currentUT);
                if (currentSegment.HasValue)
                {
                    segment = currentSegment.Value;
                    resolvedSegment = segment;

                    if (TryResolveCheckpointStateVectorMapPoint(
                        traj,
                        currentUT,
                        ref stateVectorCachedIndex,
                        out TrajectoryPoint checkpointPoint,
                        out _,
                        out TrackSection checkpointSection))
                    {
                        OrbitalCheckpointStateVectorFallbackDecision checkpointDecision =
                            EvaluateOrbitalCheckpointStateVectorFallback(
                                traj,
                                currentUT,
                                checkpointPoint,
                                segmentSourceAvailable: true,
                                allowSoiGapRecovery: allowSoiGapStateVectorFallback,
                                expectedSoiGapBody: expectedSoiGapBody);
                        checkpointFallbackDetail = string.Format(ic,
                            "stateVectorSource=OrbitalCheckpoint sectionUT={0:F1}-{1:F1} pointUT={2:F1} stateVectorBody={3} alt={4:F0} speed={5:F1} {6}",
                            checkpointSection.startUT,
                            checkpointSection.endUT,
                            checkpointPoint.ut,
                            checkpointPoint.bodyName ?? "(null)",
                            checkpointPoint.altitude,
                            checkpointPoint.velocity.magnitude,
                            FormatOrbitalCheckpointStateVectorFallbackDecision(checkpointDecision));
                    }

                    OrbitSegment visibleSegment = segment;
                    string endpointTailDetail = null;
                    // FIRST endpoint-tail branch (covering-segment override). This is the
                    // actual 181 Mm cross-body bug class and must stay suppressed for loop
                    // members - which the loopMemberInWindow argument now ENFORCES (the prior
                    // comment claimed unconditional suppression, but no gate existed on this
                    // create-resolver path; only the TS update pass had one. 2026-06-12
                    // playtest: a docked-ending loop member's garbage endpoint-tail orbit
                    // overrode its correct covering segment at every proto re-create). The
                    // loop-aware ACCEPTANCE hint is still not threaded here -- the relaxation
                    // only applies to the no-covering-segment fallback (the SECOND branch and
                    // the create-path's terminal-orbit fallback below). See plan section 1.5.
                    if (TryResolveEndpointTailForMapPresence(
                            traj,
                            currentUT,
                            visibleSegment,
                            terminalMapPresenceRegion,
                            out OrbitSegment endpointTailSegment,
                            out _,
                            out endpointTailDetail,
                            loopMemberInWindow: loopMemberInWindow))
                    {
                        segment = endpointTailSegment;
                        resolvedSegment = segment;
                        return ReturnDecision(
                            TrackingStationGhostSource.EndpointTail,
                            skipReason,
                            CombineSourceDetails(string.Format(ic,
                                "endpointTailOverride=stale-visible-segment visibleSegmentBody={0} visibleSegmentUT={1:F1}-{2:F1}",
                                visibleSegment.bodyName ?? "(null)",
                                visibleSegment.startUT,
                                visibleSegment.endUT),
                                CombineSourceDetails(endpointTailDetail, checkpointFallbackDetail)));
                    }

                    return ReturnDecision(
                        TrackingStationGhostSource.Segment,
                        skipReason,
                        CombineSourceDetails(string.Format(ic,
                            "segmentBody={0} segmentUT={1:F1}-{2:F1}",
                            segment.bodyName ?? "(null)",
                            segment.startUT,
                            segment.endUT),
                            CombineSourceDetails(endpointTailDetail, checkpointFallbackDetail)));
                }
            }

            // Dense OrbitalCheckpoint frames are only safe as a map-presence
            // state-vector source when a caller explicitly marks the create/update
            // as recovery from an orbit-segment gap. Normal checkpoint windows keep
            // using segment or terminal-orbit sources so stale/wrong-frame checkpoint
            // data cannot reintroduce the #571 / #584 wrong-position class.
            if (TryResolveCheckpointStateVectorMapPoint(
                traj,
                currentUT,
                ref stateVectorCachedIndex,
                out stateVectorPoint,
                out _,
                out TrackSection fallbackCheckpointSection))
            {
                OrbitalCheckpointStateVectorFallbackDecision checkpointDecision =
                    EvaluateOrbitalCheckpointStateVectorFallback(
                        traj,
                        currentUT,
                        stateVectorPoint,
                        segmentSourceAvailable: false,
                        allowSoiGapRecovery: allowSoiGapStateVectorFallback,
                        expectedSoiGapBody: expectedSoiGapBody);
                string detail = string.Format(ic,
                    "stateVectorSource=OrbitalCheckpoint sectionUT={0:F1}-{1:F1} pointUT={2:F1} stateVectorBody={3} alt={4:F0} speed={5:F1} {6}",
                    fallbackCheckpointSection.startUT,
                    fallbackCheckpointSection.endUT,
                    stateVectorPoint.ut,
                    stateVectorPoint.bodyName ?? "(null)",
                    stateVectorPoint.altitude,
                    stateVectorPoint.velocity.magnitude,
                    FormatOrbitalCheckpointStateVectorFallbackDecision(checkpointDecision));

                if (checkpointDecision.Accepted)
                {
                    resolvedStatePoint = stateVectorPoint;
                    skipReason = checkpointDecision.Reason;
                    return ReturnDecision(
                        TrackingStationGhostSource.StateVectorSoiGap,
                        checkpointDecision.Reason,
                        detail);
                }

                checkpointFallbackRejectReason = checkpointDecision.Reason;
                checkpointFallbackDetail = detail;
            }

            string stateVectorSkipReason = null;
            // #583: try state-vector resolution when there are no orbit segments
            // (the original physics-only-suborbital case) OR when the current UT
            // sits inside a Relative-frame section. The latter widens the gate
            // so a recording with OrbitalCheckpoint segments elsewhere can still
            // get a map ghost while the playback head is in the docking /
            // rendezvous section; CreateGhostVesselFromStateVectors handles
            // Relative world-position resolution against the recorded anchor
            // pose for the target UT.
            bool inRelativeFrame = IsInRelativeFrame(traj, currentUT);
            bool hasPastOrbitSegment = false;
            bool hasFutureOrbitSegment = false;
            if (traj.HasOrbitSegments && inRelativeFrame && traj.OrbitSegments != null)
            {
                for (int i = 0; i < traj.OrbitSegments.Count; i++)
                {
                    OrbitSegment candidate = traj.OrbitSegments[i];
                    if (candidate.endUT < currentUT)
                        hasPastOrbitSegment = true;
                    if (candidate.startUT > currentUT)
                        hasFutureOrbitSegment = true;
                    if (hasPastOrbitSegment && hasFutureOrbitSegment)
                        break;
                }
            }

            bool deferRelativeStateVectorForSegmentGap =
                traj.HasOrbitSegments
                && inRelativeFrame
                && hasPastOrbitSegment
                && hasFutureOrbitSegment;
            if (deferRelativeStateVectorForSegmentGap)
            {
                // A Relative section with pending bounded orbit coverage should not
                // create a no-bounds state-vector ProtoVessel. During rewind/map
                // time warp that briefly exposed stock's full proto-orbit instead
                // of waiting for the next Parsek-bounded segment. The past+future
                // bracket is intentional: future-only Relative windows still use
                // the #583 state-vector path until a recorded segment gap exists.
                stateVectorSkipReason = TrackingStationGhostSkipRelativeStateVectorSegmentGap;
            }
            string relativeSegmentGapDetail = deferRelativeStateVectorForSegmentGap
                ? "relativeFrame=True orbitSegmentGap=True"
                : null;

            bool considerStateVector =
                !traj.HasOrbitSegments
                || (inRelativeFrame && !deferRelativeStateVectorForSegmentGap);
            if (considerStateVector)
            {
                if (TryResolveStateVectorMapPoint(
                    traj,
                    currentUT,
                    ref stateVectorCachedIndex,
                    out stateVectorPoint,
                    out stateVectorSkipReason))
                {
                    resolvedStatePoint = stateVectorPoint;
                    return ReturnDecision(
                        TrackingStationGhostSource.StateVector,
                        skipReason,
                        string.Format(ic,
                            "stateVectorBody={0} alt={1:F0} speed={2:F1}",
                            stateVectorPoint.bodyName ?? "(null)",
                            stateVectorPoint.altitude,
                            stateVectorPoint.velocity.magnitude));
                }
            }

            if (!allowTerminalOrbitFallback)
            {
                skipReason = checkpointFallbackRejectReason
                    ?? ResolveStateVectorOrSegmentSkipReason(
                        traj,
                        considerStateVector || deferRelativeStateVectorForSegmentGap,
                        stateVectorSkipReason);
                string detail = string.Format(ic,
                    "terminalFallback=False hasOrbitSegments={0}",
                    traj.HasOrbitSegments);
                detail = CombineSourceDetails(detail, relativeSegmentGapDetail);
                return ReturnDecision(
                    TrackingStationGhostSource.None,
                    skipReason,
                    CombineSourceDetails(detail, checkpointFallbackDetail));
            }

            if (!HasOrbitData(traj))
            {
                skipReason = checkpointFallbackRejectReason
                    ?? ResolveStateVectorOrSegmentSkipReason(
                        traj,
                        considerStateVector || deferRelativeStateVectorForSegmentGap,
                        stateVectorSkipReason);
                string detail = string.Format(ic, "hasOrbitSegments={0}", traj.HasOrbitSegments);
                detail = CombineSourceDetails(detail, relativeSegmentGapDetail);
                return ReturnDecision(
                    TrackingStationGhostSource.None,
                    skipReason,
                    CombineSourceDetails(detail, checkpointFallbackDetail));
            }

            if (!IsTerminalStateEligibleForTerminalOrbitMapPresence(terminal))
            {
                skipReason = "terminal-" + terminal.Value;
                return ReturnDecision(
                    TrackingStationGhostSource.None,
                    skipReason,
                    CombineSourceDetails(
                        string.Format(ic, "terminal={0} terminalOrbitFallback=True", terminal.Value),
                        checkpointFallbackDetail));
            }

            if (currentUT < activationStartUT)
            {
                skipReason = checkpointFallbackRejectReason ?? "before-activation";
                return ReturnDecision(
                    TrackingStationGhostSource.None,
                    skipReason,
                    CombineSourceDetails(
                        string.Format(ic, "activationStartUT={0:F1}", activationStartUT),
                        checkpointFallbackDetail));
            }

            bool allowSparseOrbitGapFallback =
                traj.HasOrbitSegments
                && currentUT < traj.EndUT
                && !HasRecordedTrackCoverageAtUT(traj, currentUT);
            if (currentUT < traj.EndUT && !allowSparseOrbitGapFallback)
            {
                skipReason = checkpointFallbackRejectReason
                    ?? (deferRelativeStateVectorForSegmentGap
                        ? stateVectorSkipReason
                        : "before-terminal-orbit");
                string detail = string.Format(ic, "endUT={0:F1}", traj.EndUT);
                detail = CombineSourceDetails(detail, relativeSegmentGapDetail);
                return ReturnDecision(
                    TrackingStationGhostSource.None,
                    skipReason,
                    CombineSourceDetails(detail, checkpointFallbackDetail));
            }

            // Create-path no-covering-segment terminal-orbit fallback. The
            // acceptTerminalOrbitForLoopSynthesis hint propagates here so a loop-aware
            // caller (non-zero loop epoch shift on a same-body terminal recording) can
            // synthesise a terminal orbit even when the recording has no recorded
            // OrbitSegments and its persisted endpoint phase resolved to TrajectoryPoint
            // (the no-segment terminal-Orbiting case). Default false preserves
            // byte-identical behaviour for every existing call site that does not opt
            // in. See plan section 1.4 and section 1.5.
            if (TryResolveEndpointTailForMapPresence(
                    traj,
                    currentUT,
                    selectedSegment: null,
                    terminalMapPresenceRegion: terminalMapPresenceRegion,
                    out OrbitSegment terminalEndpointTailSegment,
                    out _,
                    out string terminalEndpointTailDetail,
                    acceptTerminalOrbitSource: acceptTerminalOrbitForLoopSynthesis))
            {
                segment = terminalEndpointTailSegment;
                resolvedSegment = segment;
                return ReturnDecision(
                    TrackingStationGhostSource.EndpointTail,
                    skipReason,
                    CombineSourceDetails(terminalEndpointTailDetail, checkpointFallbackDetail));
            }

            if (!TryResolveGhostProtoOrbitSeed(
                traj,
                out double inclination,
                out double eccentricity,
                out double semiMajorAxis,
                out double lan,
                out double argumentOfPeriapsis,
                out double meanAnomalyAtEpoch,
                out double epoch,
                out string seedBodyName,
                out GhostProtoOrbitSeedDiagnostics seedDiagnostics))
            {
                PopulateTailSeedDiagnostics(traj, currentUT, ref seedDiagnostics);
                skipReason = seedDiagnostics.FailureReason == TrackingStationGhostSkipEndpointConflict
                    ? TrackingStationGhostSkipEndpointConflict
                    : TrackingStationGhostSkipUnseedableTerminalOrbit;
                return ReturnDecision(
                    TrackingStationGhostSource.None,
                    skipReason,
                    CombineSourceDetails(string.Format(ic,
                        "terminalBody={0} endUT={1:F1} seedFailure={2} endpointBody={3} {4}",
                        traj.TerminalOrbitBody ?? "(null)",
                        traj.EndUT,
                        seedDiagnostics.FailureReason ?? "(none)",
                        seedDiagnostics.EndpointBodyName ?? "(none)",
                        FormatGhostProtoOrbitSeedDiagnostics(seedDiagnostics)),
                        checkpointFallbackDetail));
            }
            PopulateTailSeedDiagnostics(traj, currentUT, ref seedDiagnostics);

            segment = new OrbitSegment
            {
                startUT = activationStartUT,
                endUT = traj.EndUT,
                inclination = inclination,
                eccentricity = eccentricity,
                semiMajorAxis = semiMajorAxis,
                longitudeOfAscendingNode = lan,
                argumentOfPeriapsis = argumentOfPeriapsis,
                meanAnomalyAtEpoch = meanAnomalyAtEpoch,
                epoch = epoch,
                bodyName = seedBodyName
            };
            resolvedSegment = segment;

            return ReturnDecision(
                TrackingStationGhostSource.TerminalOrbit,
                skipReason,
                CombineSourceDetails(string.Format(ic,
                    "terminalBody={0} endUT={1:F1} seedBody={2} seedSource={3} endpointBody={4} seedFallback={5} {6}",
                    traj.TerminalOrbitBody ?? "(null)",
                    traj.EndUT,
                    seedBodyName ?? "(null)",
                    seedDiagnostics.Source ?? "(none)",
                    seedDiagnostics.EndpointBodyName ?? "(none)",
                    seedDiagnostics.FallbackReason ?? "(none)",
                    FormatGhostProtoOrbitSeedDiagnostics(seedDiagnostics)),
                    checkpointFallbackDetail));
        }

        private static string BuildTrackingStationGhostSourceDetail(
            Recording rec,
            double currentUT,
            TrackingStationGhostSource source,
            string skipReason,
            OrbitSegment segment,
            TrajectoryPoint stateVectorPoint)
        {
            if (rec == null)
                return "null recording sourceKind=None rec=(null) body=(none) sourceUT=(none) world=(none)";

            string structuredDetail = BuildGhostSourceStructuredDetail(
                rec,
                currentUT,
                source,
                segment,
                stateVectorPoint);
            string WithStructured(string detail)
            {
                return CombineSourceDetails(detail, structuredDetail);
            }

            switch (source)
            {
                case TrackingStationGhostSource.Segment:
                case TrackingStationGhostSource.EndpointTail:
                    return WithStructured(CombineSourceDetails(
                        string.Format(ic,
                            "segmentBody={0} segmentUT={1:F1}-{2:F1}",
                            segment.bodyName ?? "(null)",
                            segment.startUT,
                            segment.endUT),
                        BuildEndpointTailTrackingStationDetail(
                            rec,
                            currentUT,
                            source,
                            segment)));

                case TrackingStationGhostSource.StateVector:
                case TrackingStationGhostSource.StateVectorSoiGap:
                    return WithStructured(string.Format(ic,
                        "stateVectorBody={0} alt={1:F0} speed={2:F1}",
                        stateVectorPoint.bodyName ?? "(null)",
                        stateVectorPoint.altitude,
                        stateVectorPoint.velocity.magnitude));

                case TrackingStationGhostSource.TerminalOrbit:
                    if (TryResolveGhostProtoOrbitSeed(
                        rec,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out string seedBodyName,
                        out GhostProtoOrbitSeedDiagnostics seedDiagnostics))
                    {
                        PopulateTailSeedDiagnostics(rec, currentUT, ref seedDiagnostics);
                        return WithStructured(string.Format(ic,
                            "terminalBody={0} endUT={1:F1} seedBody={2} seedSource={3} endpointBody={4} seedFallback={5} {6}",
                            rec.TerminalOrbitBody ?? "(null)",
                            rec.EndUT,
                            seedBodyName ?? "(null)",
                            seedDiagnostics.Source ?? "(none)",
                            seedDiagnostics.EndpointBodyName ?? "(none)",
                            seedDiagnostics.FallbackReason ?? "(none)",
                            FormatGhostProtoOrbitSeedDiagnostics(seedDiagnostics)));
                    }
                    break;
            }

            switch (skipReason)
            {
                case "debris":
                    return WithStructured("isDebris=True");
                case TrackingStationGhostSkipSuppressed:
                    return WithStructured("isSuppressed=True");
                case TrackingStationGhostSkipAlreadySpawned:
                    return WithStructured("already spawned");
                case "before-activation":
                    return WithStructured(string.Format(ic, "activationStartUT={0:F1}",
                        PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(rec)));
                case "before-terminal-orbit":
                    return WithStructured(string.Format(ic, "endUT={0:F1}", rec.EndUT));
                case TrackingStationGhostSkipEndpointConflict:
                case TrackingStationGhostSkipUnseedableTerminalOrbit:
                    TryResolveGhostProtoOrbitSeed(
                        rec,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out GhostProtoOrbitSeedDiagnostics seedDiagnostics);
                    PopulateTailSeedDiagnostics(rec, currentUT, ref seedDiagnostics);
                    return WithStructured(string.Format(ic,
                        "terminalBody={0} endUT={1:F1} seedFailure={2} endpointBody={3} {4}",
                        rec.TerminalOrbitBody ?? "(null)",
                        rec.EndUT,
                        seedDiagnostics.FailureReason ?? "(none)",
                        seedDiagnostics.EndpointBodyName ?? "(none)",
                        FormatGhostProtoOrbitSeedDiagnostics(seedDiagnostics)));
            }

            if (skipReason != null && skipReason.StartsWith("terminal"))
                return WithStructured(string.Format(ic, "terminal={0}",
                    rec.TerminalStateValue.HasValue ? rec.TerminalStateValue.Value.ToString() : "(none)"));

            if (skipReason == "no-current-segment" || skipReason == "no-orbit-data")
                return WithStructured(string.Format(ic, "hasOrbitSegments={0}", rec.HasOrbitSegments));

            if (skipReason == TrackingStationGhostSkipStateVectorThreshold)
                return WithStructured(string.Format(ic, "hasOrbitSegments={0} stateVectorThreshold=True", rec.HasOrbitSegments));

            if (skipReason == TrackingStationGhostSkipRelativeFrame)
                return WithStructured("relativeFrame=True");

            if (skipReason == TrackingStationGhostSkipRelativeStateVectorSegmentGap)
                return WithStructured(string.Format(ic,
                    "relativeFrame=True orbitSegmentGap=True endUT={0:F1}",
                    rec.EndUT));

            if (skipReason == "no-state-vector-point")
                return WithStructured(string.Format(ic, "stateVectorPointMissing=True hasOrbitSegments={0}", rec.HasOrbitSegments));

            return structuredDetail;
        }

        private static string CombineSourceDetails(string detail, string structuredDetail)
        {
            if (string.IsNullOrEmpty(detail))
                return structuredDetail;
            if (string.IsNullOrEmpty(structuredDetail))
                return detail;
            return detail + " " + structuredDetail;
        }

        private static string BuildGhostSourceStructuredDetail(
            IPlaybackTrajectory traj,
            double currentUT,
            TrackingStationGhostSource source,
            OrbitSegment segment,
            TrajectoryPoint stateVectorPoint)
        {
            string recId = traj?.RecordingId ?? "(null)";
            switch (source)
            {
                case TrackingStationGhostSource.Segment:
                    return BuildOrbitSourceStructuredDetail("Segment", recId, currentUT, segment);

                case TrackingStationGhostSource.TerminalOrbit:
                    return BuildOrbitSourceStructuredDetail("TerminalOrbit", recId, currentUT, segment);

                case TrackingStationGhostSource.EndpointTail:
                    return BuildOrbitSourceStructuredDetail("EndpointTail", recId, currentUT, segment);

                case TrackingStationGhostSource.StateVector:
                    return BuildStateVectorSourceStructuredDetail("StateVector", recId, stateVectorPoint);

                case TrackingStationGhostSource.StateVectorSoiGap:
                    return BuildStateVectorSourceStructuredDetail("SoiGapStateVector", recId, stateVectorPoint);

                default:
                    return string.Format(ic,
                        "sourceKind=None rec={0} body=(none) sourceUT=(none) world=(none)",
                        recId);
            }
        }

        private static string BuildOrbitSourceStructuredDetail(
            string sourceKind,
            string recId,
            double currentUT,
            OrbitSegment segment)
        {
            string world = TryResolveOrbitWorldPosition(segment, currentUT, out Vector3d worldPos)
                ? FormatWorldPosition(worldPos)
                : "(unresolved)";
            return string.Format(ic,
                "sourceKind={0} rec={1} body={2} sourceUT={3:F1}-{4:F1} epoch={5:F1} sma={6:F0} ecc={7:F6} world={8}",
                sourceKind,
                recId,
                segment.bodyName ?? "(null)",
                segment.startUT,
                segment.endUT,
                segment.epoch,
                segment.semiMajorAxis,
                segment.eccentricity,
                world);
        }

        private static string BuildStateVectorSourceStructuredDetail(
            string sourceKind,
            string recId,
            TrajectoryPoint point)
        {
            string world = TryResolvePointWorldPosition(point, out Vector3d worldPos)
                ? FormatWorldPosition(worldPos)
                : "(unresolved)";
            return string.Format(ic,
                "sourceKind={0} rec={1} body={2} sourceUT={3:F1} pointUT={4:F1} alt={5:F0} speed={6:F1} world={7}",
                sourceKind,
                recId,
                point.bodyName ?? "(null)",
                point.ut,
                point.ut,
                point.altitude,
                point.velocity.magnitude,
                world);
        }

        /// <summary>
        /// Pure: resolve which orbital representation, if any, should back a
        /// tracking-station ghost at the current UT. Current visible orbit
        /// segments win over eventual terminal-orbit data so the tracking
        /// station never advertises a future body/SOI before the recording
        /// has actually reached that phase.
        /// </summary>
        internal static TrackingStationGhostSource ResolveTrackingStationGhostSource(
            Recording rec,
            bool isSuppressed,
            double currentUT,
            out OrbitSegment segment,
            out string skipReason)
        {
            int stateVectorCachedIndex = -1;
            return ResolveTrackingStationGhostSource(
                rec,
                isSuppressed,
                false,
                currentUT,
                ref stateVectorCachedIndex,
                out segment,
                out _,
                out skipReason);
        }

        internal static TrackingStationGhostSource ResolveTrackingStationGhostSource(
            Recording rec,
            bool isSuppressed,
            bool realVesselExists,
            double currentUT,
            ref int stateVectorCachedIndex,
            out OrbitSegment segment,
            out TrajectoryPoint stateVectorPoint,
            out string skipReason)
        {
            return ResolveTrackingStationGhostSourceCore(
                rec,
                isSuppressed,
                realVesselExists,
                currentUT,
                ref stateVectorCachedIndex,
                out segment,
                out stateVectorPoint,
                out skipReason,
                batch: null,
                recordingIndex: -1,
                context: "direct");
        }

        private static TrackingStationGhostSource ResolveTrackingStationGhostSourceCore(
            Recording rec,
            bool isSuppressed,
            bool realVesselExists,
            double currentUT,
            ref int stateVectorCachedIndex,
            out OrbitSegment segment,
            out TrajectoryPoint stateVectorPoint,
            out string skipReason,
            TrackingStationGhostSourceBatch batch,
            int recordingIndex,
            string context,
            bool loopMemberInWindow = false,
            bool liveLaunchMatchedAnchorOfActiveMember = false,
            bool transferMemberDescentContinuation = false)
        {
            // Both create callers (the per-frame lifecycle pass AND the one-shot startup create)
            // now sample at the loop-mapped effUT and pass loopMemberInWindow, so for an in-window
            // loop member the resolver's currentUT < EndUT gate returns before-terminal-orbit and
            // never reaches a terminal-orbit branch. acceptTerminalOrbitForLoopSynthesis stays false
            // for BOTH create paths per plan section 1.4: a no-segment terminal-orbit synthesis at
            // a raw recorded UT would seed at the wrong position (the PR #967 class), and a
            // no-segment loop member's proto-vessel instead comes up on a later loop-aware refresh
            // tick (the refresh path is the one that opts into the synthesis when it is safe).
            // loopMemberInWindow (true whenever effUT != live currentUT) lets a looped member draw
            // its map ghost alongside a persisted real terminal vessel; false on the pure-predicate
            // wrapper keeps it unchanged.
            TrackingStationGhostSource source = ResolveMapPresenceGhostSource(
                rec,
                isSuppressed,
                IsTrackingStationRecordingMaterialized(rec, realVesselExists),
                currentUT,
                true,
                logOperationName: null,
                ref stateVectorCachedIndex,
                out segment,
                out stateVectorPoint,
                out skipReason,
                recordingIndex: recordingIndex,
                acceptTerminalOrbitForLoopSynthesis: false,
                loopMemberInWindow: loopMemberInWindow,
                liveLaunchMatchedAnchorOfActiveMember: liveLaunchMatchedAnchorOfActiveMember,
                transferMemberDescentContinuation: transferMemberDescentContinuation);

            // BUG-B: in the live tracking-station create paths (batch != null), a committed recording
            // that WOULD seed a map icon (source != None) but is purely historical (the player only ever
            // progressed past it in normal forward time and never rewound to replay it) is suppressed so
            // it does not draw a duplicate ghost of a still-live vessel. Placed AFTER source resolution
            // so the existing, more-specific skip reasons (already-spawned, no-orbit, before-activation)
            // are preserved; only an otherwise-renderable historical recording is overridden. The
            // pure-predicate "direct" wrapper passes batch == null and is left unchanged. Loop members
            // (loopMemberInWindow) and the active re-fly session are exempt, and rec.LoopPlayback covers
            // a per-recording loop (whose sample UT is its own loop clock's, not the live UT); for every
            // other recording reaching here in a create path currentUT is the live UT (effUT ==
            // currentUT), so the latch sees live time.
            if (source != TrackingStationGhostSource.None
                && batch != null
                && !loopMemberInWindow
                && !rec.LoopPlayback
                && SessionSuppressionState.ActiveMarker == null)
            {
                double historicalActivationStartUT = GhostPlaybackEngine.ResolveGhostActivationStartUT(rec);
                PlaybackScopeTracker.NotePlayhead(rec.RecordingId, currentUT, historicalActivationStartUT);
                if (PlaybackScopeTracker.IsHistoricalNeverReplayed(
                        rec.RecordingId, currentUT, historicalActivationStartUT))
                {
                    source = TrackingStationGhostSource.None;
                    segment = default(OrbitSegment);
                    stateVectorPoint = default(TrajectoryPoint);
                    skipReason = "historical-not-replayed";
                }
            }

            LogTrackingStationGhostSourceDecision(
                context,
                recordingIndex,
                rec,
                currentUT,
                source,
                skipReason,
                BuildTrackingStationGhostSourceDetail(
                    rec,
                    currentUT,
                    source,
                    skipReason,
                    segment,
                    stateVectorPoint),
                batch);

            return source;
        }
    }
}
