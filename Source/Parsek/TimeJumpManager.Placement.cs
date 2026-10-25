using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>One vessel the placement jump moves (the player's vessel, or a group member).</summary>
    internal sealed class PlacementMember
    {
        internal Vessel Vessel;
        internal uint Pid;
        internal string Name;
        internal bool IsActive;
        /// <summary>Local-frame state against the reference at T0 (frame-free), as captured.</summary>
        internal RelativeState CapturedRelative;
        /// <summary>The local-frame state the member is placed at: the captured one, slid along
        /// track by the spawn-room slide for a Ready move (design 4.4).</summary>
        internal RelativeState Relative;
        /// <summary>World attitude encoded against the reference's world orbital frame at T0.</summary>
        internal Quaternion OrbitalFrameAttitude;
        /// <summary>Distance to the DRAWN reference ghost at the click (NaN when not drawn).</summary>
        internal double RefDistanceT0 = double.NaN;
        /// <summary>Distance to the player's vessel at the click.</summary>
        internal double ToActiveT0 = double.NaN;
        internal bool UnpackChecked;
    }

    /// <summary>
    /// A placement jump between its two frames (design docs/dev/design-realspawn-time-jump-move.md,
    /// plan Step 1.3): built at the click, armed after the clock set, placed on the next frame.
    /// </summary>
    internal sealed class PlacementPlan
    {
        internal string RecordingId;
        internal string VesselName;
        internal string Kind;
        /// <summary>The reference at T0: a Leaves move's covering segment (also its T1), a
        /// Ready move's covering segment or recorded-pair orbit.</summary>
        internal OrbitSegment Segment;
        internal CelestialBody Body;
        /// <summary>A Ready move's reference from T1 on: the spawner's own terminal-orbit
        /// resolve (the click estimate until frame F+1 replaces it).</summary>
        internal OrbitSegment EndSegment;
        internal CelestialBody EndBody;
        internal string EndSeedSource;
        /// <summary>A Ready move: the placed distance from the player's vessel to the
        /// reference's spawn spot (metres, after the slide).</summary>
        internal double ExpectedSpawnDistance = double.NaN;
        internal bool ReferenceSpawnedBeforePlacement;
        internal bool SpawnChecked;
        internal double T0;
        internal double T1;
        internal double DepartureUT;
        internal List<PlacementMember> Members = new List<PlacementMember>();
        internal double RefGapToDrawn = double.NaN;
        internal double SlideMeters;
        internal int ArmedFrame;
        internal float ArmedFixedTime;
        internal double PlaceUT = double.NaN;
        internal bool DepartureChecked;
        internal Func<Vector3d?> DrawnReference;

        internal bool IsReady => Kind == TimeJumpManager.PlacementKindReady;
    }

    /// <summary>Builds a placement plan at the click (frame F), quiet; see
    /// <see cref="TimeJumpManager.TryStartLeavesPlacementJump"/>.</summary>
    internal delegate bool PlacementPlanBuilder(
        double t0, Vector3d? drawnRefWorldT0, out PlacementPlan plan, out MoveGateVerdict verdict);

    /// <summary>A Ready reference's T1 orbit as the scan caches it (plan Step 2 item 1).</summary>
    internal sealed class ReadyEndEstimate
    {
        internal int StateVersion;
        internal bool Ok;
        internal string Reason;
        internal OrbitSegment Segment;
        internal string BodyName;
        internal string SeedSource;
        internal double SpawnHalfExtent;
    }

    /// <summary>
    /// The reference ghost's states for a Leaves move (plan 1.2): the OrbitSegment covering
    /// T0, the one <see cref="SelectiveSpawnUI.ComputeDepartureInfo"/> reads, so T1 (at most
    /// its endUT) lies on the same conic. The Orbit is rebuilt on every call: its frame is
    /// tied to the tick's <c>Planetarium.Zup</c>, so it is never kept across frames.
    /// </summary>
    internal static class TimeJumpReference
    {
        internal static bool TryFindLeavesSegment(
            Recording rec, double t0, double t1, out OrbitSegment segment, out CelestialBody body,
            out string reason)
        {
            segment = default(OrbitSegment);
            body = null;
            reason = null;
            if (rec == null)
            {
                reason = "no-recording";
                return false;
            }
            OrbitSegment? found = TrajectoryMath.FindOrbitSegment(rec.OrbitSegments, t0);
            if (!found.HasValue)
            {
                reason = "no-segment-at-t0";
                return false;
            }
            segment = found.Value;
            if (t1 > segment.endUT + 1e-6)
            {
                reason = "t1-past-segment";
                return false;
            }
            body = MapRenderProbe.ResolveBodyByName(segment.bodyName);
            if (body == null)
            {
                reason = "no-body";
                return false;
            }
            return true;
        }

        internal const string ReadyT0SourceSegment = "segment";
        internal const string ReadyT0SourcePair = "recorded-pair";

        /// <summary>
        /// A Ready reference at T0 (plan Step 2 item 1): the OrbitSegment covering T0, else
        /// the orbit fitted to the two recorded coast samples bracketing T0
        /// (<see cref="OrbitReseed.TryFromRecordedTailPair"/>, which converts them with the
        /// body's live rotation; the node line is turned back to the later sample's own
        /// rotation, <see cref="TimeJumpPlacement.TailSeedLanCorrectionDegrees"/>). Never
        /// <c>TrajectoryPoint.velocity</c>. <paramref name="quiet"/> keeps the scan silent.
        /// </summary>
        internal static bool TryResolveReadyT0(
            Recording rec, double t0, double nowUT, bool quiet,
            out OrbitSegment segment, out CelestialBody body, out string source, out string reason)
        {
            segment = default(OrbitSegment);
            body = null;
            source = null;
            reason = null;
            if (rec == null)
            {
                reason = "no-recording";
                return false;
            }
            OrbitSegment? found = TrajectoryMath.FindOrbitSegment(rec.OrbitSegments, t0);
            if (found.HasValue)
            {
                segment = found.Value;
                body = MapRenderProbe.ResolveBodyByName(segment.bodyName);
                source = ReadyT0SourceSegment;
                if (body == null)
                {
                    reason = "no-body";
                    return false;
                }
                return true;
            }

            if (!TimeJumpPlacement.TryFindBracketingCoastPair(rec.TrackSections, t0,
                    out TrajectoryPoint previous, out TrajectoryPoint next, out reason))
                return false;
            body = MapRenderProbe.ResolveBodyByName(next.bodyName);
            if (body == null)
            {
                reason = "no-body";
                return false;
            }
            var fitted = new Orbit();
            if (!OrbitReseed.TryFromRecordedTailPair(fitted, body, previous, next,
                    out RecordedTailVelocityResolution resolution, logResult: !quiet))
            {
                reason = "pair-unresolved:" + (resolution.DeclineReason ?? "no-fit");
                return false;
            }
            double lanCorrection = TimeJumpPlacement.TailSeedLanCorrectionDegrees(
                true, body.rotates, body.rotationPeriod, nowUT, next.ut);
            segment = SegmentFromOrbit(fitted, body.bodyName, lanCorrection, previous.ut, next.ut);
            source = ReadyT0SourcePair;
            return true;
        }

        /// <summary>
        /// A Ready reference at the spawn (plan Step 2 item 1, T1): the orbit the spawner
        /// will build for this recording's end, through its own resolver with no jump lag
        /// (<see cref="VesselSpawner.TryBuildRecordedTerminalOrbitForSpawn(Recording, CelestialBody, double, out Orbit, out string)"/>),
        /// as elements. Built at <paramref name="nowUT"/>: a trajectory-tail seed turns with
        /// the body's live rotation, so its node line is advanced to the spawn UT's rotation;
        /// in frame F+1 (<paramref name="nowUT"/> == <paramref name="spawnUT"/>) that is no
        /// correction, exactly the spawn's own build. A recording that does not spawn on a
        /// recorded terminal orbit is refused (its endpoint state would need the recorded
        /// velocity).
        /// </summary>
        internal static bool TryResolveReadyEnd(
            Recording rec, double spawnUT, double nowUT,
            out OrbitSegment segment, out CelestialBody body, out string seedSource, out string reason)
        {
            segment = default(OrbitSegment);
            body = null;
            seedSource = null;
            reason = null;
            if (rec == null)
            {
                reason = "no-recording";
                return false;
            }
            if (!VesselSpawner.ShouldUseRecordedTerminalOrbitSpawnState(rec))
            {
                reason = "no-terminal-orbit-spawn";
                return false;
            }
            TrajectoryPoint? last = rec.Points != null && rec.Points.Count > 0
                ? (TrajectoryPoint?)rec.Points[rec.Points.Count - 1]
                : null;
            body = VesselSpawner.ResolveSpawnRotationBody(rec, last);
            if (body == null)
            {
                reason = "no-spawn-body";
                return false;
            }
            if (!VesselSpawner.TryBuildRecordedTerminalOrbitForSpawn(
                    rec, body, spawnUT, out Orbit orbit, out seedSource)
                || orbit == null)
            {
                reason = "spawn-orbit-unresolved";
                return false;
            }
            double lanCorrection = TimeJumpPlacement.TailSeedLanCorrectionDegrees(
                seedSource == VesselSpawner.TerminalOrbitSeedTrajectoryTail,
                body.rotates, body.rotationPeriod, nowUT, spawnUT);
            segment = SegmentFromOrbit(orbit, body.bodyName, lanCorrection, spawnUT, spawnUT);
            return true;
        }

        /// <summary>A stock orbit's elements as a segment (KSP-native units), its node line
        /// turned by <paramref name="lanCorrectionDegrees"/>.</summary>
        internal static OrbitSegment SegmentFromOrbit(
            Orbit orbit, string bodyName, double lanCorrectionDegrees, double startUT, double endUT)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                inclination = orbit.inclination,
                eccentricity = orbit.eccentricity,
                semiMajorAxis = orbit.semiMajorAxis,
                longitudeOfAscendingNode = TimeJumpPlacement.NormalizeDegrees(orbit.LAN + lanCorrectionDegrees),
                argumentOfPeriapsis = orbit.argumentOfPeriapsis,
                meanAnomalyAtEpoch = orbit.meanAnomalyAtEpoch,
                epoch = orbit.epoch,
                bodyName = bodyName,
            };
        }

        /// <summary>Zup body-relative state of the reference at <paramref name="ut"/>.</summary>
        internal static bool TryEvaluate(
            OrbitSegment segment, CelestialBody body, double ut, out Vector3d r, out Vector3d v)
        {
            r = Vector3d.zero;
            v = Vector3d.zero;
            Orbit orbit = MapRenderProbe.BuildOrbitFromSegment(segment, body);
            if (orbit == null)
                return false;
            r = orbit.getRelativePositionAtUT(ut);
            v = orbit.getOrbitalVelocityAtUT(ut);
            return TimeJumpPlacement.IsFinite(r.x) && TimeJumpPlacement.IsFinite(r.y)
                && TimeJumpPlacement.IsFinite(r.z) && TimeJumpPlacement.IsFinite(v.x)
                && TimeJumpPlacement.IsFinite(v.y) && TimeJumpPlacement.IsFinite(v.z);
        }
    }

    internal static partial class TimeJumpManager
    {
        private const string PlacementTag = "TimeJump";
        internal const string PlacementKindLeaves = "Leaves";
        internal const string PlacementKindReady = "Ready";

        /// <summary>Fixed updates the click holds stock's unpack (the placement re-holds 10).</summary>
        internal const int PlacementArmHoldFixedUpdates = 60;
        internal const int PlacementPlaceHoldFixedUpdates = 10;
        /// <summary>Attitude error at the first unpacked frame above which it is re-asserted.</summary>
        internal const double PlacementAttitudeReassertDeg = 2.0;
        /// <summary>The watch gives up this long after the departure (or the placement).</summary>
        internal const double PlacementWatchTimeoutSeconds = 120.0;
        internal const double DestinationPaddingMeters = VesselGhoster.SpawnCollisionPadding;
        internal const double DestinationFallbackRadiusMeters = 2.0;

        private static PlacementPlan pendingPlacement;
        private static PlacementPlan watchedPlacement;

        /// <summary>The scan's Ready T1 estimates, per recording id, valid while
        /// <see cref="RecordingStore.StateVersion"/> holds (plan Step 2 item 1, review 8).</summary>
        private static readonly Dictionary<string, ReadyEndEstimate> readyEndCache =
            new Dictionary<string, ReadyEndEstimate>(StringComparer.Ordinal);

        internal static bool HasPendingPlacement => pendingPlacement != null;
        internal static bool HasWatchedPlacement => watchedPlacement != null;
        internal static int ReadyEndCacheCount => readyEndCache.Count;

        /// <summary>
        /// Pure: whether the end-of-recording spawn of <paramref name="recordingId"/> waits for
        /// the armed Ready placement whose reference it is. Frame F sets the clock to the
        /// recording's end, so the playback loop can complete the ghost before frame F+1
        /// places the group; spawning then would test the spawn spot against the group's
        /// unplaced position (a blocked or walked-back spawn beside a slid group). The spawn
        /// is refused this frame, the policy holds the ghost and retries it.
        /// </summary>
        internal static bool ShouldDeferSpawnForPendingPlacement(
            string pendingRecordingId, bool pendingIsReady, string recordingId)
        {
            return pendingIsReady
                && !string.IsNullOrEmpty(recordingId)
                && string.Equals(pendingRecordingId, recordingId, StringComparison.Ordinal);
        }

        /// <summary>Live wrapper of <see cref="ShouldDeferSpawnForPendingPlacement"/>.</summary>
        internal static bool IsSpawnDeferredForPendingPlacement(string recordingId)
        {
            PlacementPlan plan = pendingPlacement;
            return plan != null
                && ShouldDeferSpawnForPendingPlacement(plan.RecordingId, plan.IsReady, recordingId);
        }

        /// <summary>Test seam: installs a plan as if frame F had armed it.</summary>
        internal static void ArmPendingPlacementForTesting(PlacementPlan plan)
        {
            pendingPlacement = plan;
        }

        internal static void ResetPlacementForTesting()
        {
            pendingPlacement = null;
            watchedPlacement = null;
            readyEndCache.Clear();
        }

        /// <summary>
        /// Pure: whether a cached Ready estimate still answers for the current store. Any
        /// committed-list mutation bumps <see cref="RecordingStore.StateVersion"/>, so a
        /// recording edited, replaced or re-trimmed is re-resolved on the next scan.
        /// </summary>
        internal static bool IsReadyEndEstimateCurrent(ReadyEndEstimate entry, int stateVersion)
        {
            return entry != null && entry.StateVersion == stateVersion;
        }

        /// <summary>
        /// The Ready reference's T1 orbit and spawn box for the scan, built once per recording
        /// and store version (the spawner's resolver logs at that build only).
        /// </summary>
        private static ReadyEndEstimate GetReadyEndEstimateForScan(Recording rec, double nowUT)
        {
            int version = RecordingStore.StateVersion;
            string key = rec.RecordingId ?? string.Empty;
            if (readyEndCache.TryGetValue(key, out ReadyEndEstimate cached)
                && IsReadyEndEstimateCurrent(cached, version))
                return cached;
            ReadyEndEstimate built = BuildReadyEndEstimate(rec, nowUT, hydrateSnapshot: false);
            built.StateVersion = version;
            readyEndCache[key] = built;
            return built;
        }

        private static ReadyEndEstimate BuildReadyEndEstimate(Recording rec, double nowUT, bool hydrateSnapshot)
        {
            var e = new ReadyEndEstimate();
            e.Ok = TimeJumpReference.TryResolveReadyEnd(rec, rec.EndUT, nowUT,
                out e.Segment, out CelestialBody body, out e.SeedSource, out e.Reason);
            e.BodyName = body != null ? body.bodyName : null;
            if (hydrateSnapshot)
                RecordingStore.TryHydrateVesselSnapshotFromSidecar(rec);
            e.SpawnHalfExtent = TimeJumpPlacement.EnclosingHalfExtent(
                SpawnCollisionDetector.ComputeVesselBounds(rec.VesselSnapshot));
            return e;
        }

        /// <summary>
        /// The scan's quiet gate for a Leaves row (plan 1.4): nothing logs here; the caller
        /// logs a change. Pass for a landed, splashed or prelaunch player vessel, which keeps
        /// today's jump.
        /// </summary>
        internal static MoveGateVerdict EvaluateLeavesMoveGate(
            Recording rec, double currentUT, double departureUT, Vessel active)
        {
            if (active == null || !TimeJumpPlacement.IsOrbitalPlayerVessel(active.situation))
                return MoveGateVerdict.Pass;
            double t1 = ResolveJumpLeadTarget(departureUT, currentUT);
            TryBuildLeavesPlacementPlan(rec, currentUT, t1, departureUT, active,
                captureAttitude: false, drawnRefWorldT0: null, out _, out MoveGateVerdict verdict);
            return verdict;
        }

        /// <summary>
        /// Builds the group and the gate verdict for a Leaves move, in one synchronous pass
        /// at <paramref name="t0"/> (the current UT). Quiet. The plan is non-null only when
        /// the gates pass.
        /// </summary>
        internal static bool TryBuildLeavesPlacementPlan(
            Recording rec, double t0, double t1, double departureUT, Vessel active,
            bool captureAttitude, Vector3d? drawnRefWorldT0,
            out PlacementPlan plan, out MoveGateVerdict verdict)
        {
            plan = null;
            if (active == null || active.orbit == null || active.mainBody == null)
            {
                verdict = new MoveGateVerdict { Refusal = MoveRefusal.Degenerate, Detail = "frame=no-active" };
                return false;
            }
            if (!TimeJumpReference.TryFindLeavesSegment(rec, t0, t1, out OrbitSegment seg,
                    out CelestialBody body, out string segReason))
            {
                verdict = new MoveGateVerdict
                {
                    Refusal = MoveRefusal.Degenerate,
                    Detail = "frame=" + segReason,
                };
                return false;
            }

            bool refT0Ok = TimeJumpReference.TryEvaluate(seg, body, t0, out Vector3d rRef0, out Vector3d vRef0);
            bool refT1Ok = TimeJumpReference.TryEvaluate(seg, body, t1, out Vector3d rRef1, out Vector3d vRef1);
            LocalFrame f0 = default(LocalFrame);
            LocalFrame f1 = default(LocalFrame);
            bool frameOk = refT0Ok && refT1Ok
                && TimeJumpPlacement.TryBuildFrame(rRef0, vRef0, out f0)
                && TimeJumpPlacement.TryBuildFrame(rRef1, vRef1, out f1);

            var members = new List<PlacementMember>();
            bool memberInAtmo = false;
            string memberInAtmoName = null;
            CollectGroup(active, rec, members, ref memberInAtmo, ref memberInAtmoName);

            var inputs = new MoveGateInputs
            {
                ActiveInsideAtmosphere = IsVesselInsideAtmosphere(active),
                MemberInsideAtmosphere = memberInAtmo,
                MemberInsideAtmosphereName = memberInAtmoName,
                ReferenceInsideAtmosphereT0 = refT0Ok
                    && TimeJumpPlacement.IsInsideAtmosphere(body.atmosphere, rRef0.magnitude - body.Radius, body.atmosphereDepth),
                ReferenceInsideAtmosphereT1 = refT1Ok
                    && TimeJumpPlacement.IsInsideAtmosphere(body.atmosphere, rRef1.magnitude - body.Radius, body.atmosphereDepth),
                ActiveBody = active.mainBody.name,
                ReferenceBodyT0 = body.name,
                ReferenceBodyT1 = body.name,
                BurnDeltaV = frameOk
                    ? TimeJumpPlacement.EstimateBurnDeltaV(rRef0, vRef0, rRef1, vRef1, body.gravParameter)
                    : 0.0,
                FrameDegenerate = !frameOk,
            };

            if (frameOk && ReferenceEquals(body, active.mainBody))
            {
                var placed = new List<PlacedBody>(members.Count);
                for (int i = 0; i < members.Count; i++)
                {
                    PlacementMember m = members[i];
                    m.Relative = TimeJumpPlacement.Capture(f0, rRef0, vRef0, m.Vessel.orbit.pos, m.Vessel.orbit.vel);
                    m.CapturedRelative = m.Relative;
                    TimeJumpPlacement.Place(f1, rRef1, vRef1, m.Relative, out Vector3d rPlaced, out _);
                    placed.Add(new PlacedBody
                    {
                        Position = rPlaced,
                        Radius = VesselRadius(m.Vessel),
                        Name = m.Name,
                    });
                }
                List<PlacedBody> others = CollectDestinationOthers(members, body, t1);
                inputs.DestinationTaken = TimeJumpPlacement.TryFindDestinationConflict(
                    placed, others, DestinationPaddingMeters, out inputs.DestinationBlocker,
                    out inputs.DestinationGap);
            }

            verdict = TimeJumpPlacement.EvaluateGates(inputs);
            if (!verdict.Passed)
                return false;

            plan = new PlacementPlan
            {
                RecordingId = rec.RecordingId,
                VesselName = rec.VesselName,
                Kind = PlacementKindLeaves,
                Segment = seg,
                Body = body,
                T0 = t0,
                T1 = t1,
                DepartureUT = departureUT,
                Members = members,
            };
            CompletePlanAtClick(plan, active, body, rRef0, vRef0, captureAttitude, drawnRefWorldT0);
            return true;
        }

        /// <summary>The click-time measurements both kinds keep: the reference-to-drawn gap,
        /// each member's distances, and (at the click) its orbital-frame attitude.</summary>
        private static void CompletePlanAtClick(
            PlacementPlan plan, Vessel active, CelestialBody body, Vector3d rRef0, Vector3d vRef0,
            bool captureAttitude, Vector3d? drawnRefWorldT0)
        {
            Vector3d activeWorld = active.GetWorldPos3D();
            if (drawnRefWorldT0.HasValue)
            {
                Vector3d refWorld = body.position + TrajectoryMath.SwizzleZupBodyRelativeToWorld(rRef0);
                plan.RefGapToDrawn = (refWorld - drawnRefWorldT0.Value).magnitude;
            }
            for (int i = 0; i < plan.Members.Count; i++)
            {
                PlacementMember m = plan.Members[i];
                Vector3d world = m.Vessel.GetWorldPos3D();
                m.ToActiveT0 = (world - activeWorld).magnitude;
                if (drawnRefWorldT0.HasValue)
                    m.RefDistanceT0 = (world - drawnRefWorldT0.Value).magnitude;
                if (captureAttitude)
                    m.OrbitalFrameAttitude = TimeJumpPlacement.EncodeAttitude(
                        m.Vessel.transform.rotation, rRef0, vRef0);
            }
        }

        /// <summary>
        /// The scan's quiet gate for a Ready row of an orbital player vessel (plan Step 2 item
        /// 3): the T1 orbit comes from the per-recording cache. Pass for a landed, splashed or
        /// prelaunch player vessel, which keeps today's jump.
        /// </summary>
        internal static MoveGateVerdict EvaluateReadyMoveGate(Recording rec, double currentUT, Vessel active)
        {
            if (active == null || !TimeJumpPlacement.IsOrbitalPlayerVessel(active.situation))
                return MoveGateVerdict.Pass;
            TryBuildReadyPlacementPlan(rec, currentUT, active, forClick: false,
                drawnRefWorldT0: null, out _, out MoveGateVerdict verdict);
            return verdict;
        }

        /// <summary>
        /// Builds the group and the gate verdict for a Ready move at <paramref name="t0"/> (the
        /// current UT), T1 being the recording's end (plan Step 2 items 1-2): the reference at
        /// T0 (<see cref="TimeJumpReference.TryResolveReadyT0"/>), its T1 orbit as the spawner
        /// will build it (<see cref="TimeJumpReference.TryResolveReadyEnd"/>; the scan's
        /// cached copy, or a fresh resolve at the click), the gates with the burn estimate
        /// between the two, and the spawn-room slide. Quiet unless <paramref name="forClick"/>.
        /// The plan is non-null only when the gates pass.
        /// </summary>
        internal static bool TryBuildReadyPlacementPlan(
            Recording rec, double t0, Vessel active, bool forClick, Vector3d? drawnRefWorldT0,
            out PlacementPlan plan, out MoveGateVerdict verdict)
        {
            plan = null;
            if (rec == null || active == null || active.orbit == null || active.mainBody == null)
            {
                verdict = new MoveGateVerdict { Refusal = MoveRefusal.Degenerate, Detail = "frame=no-active" };
                return false;
            }
            double t1 = rec.EndUT;
            if (!TimeJumpReference.TryResolveReadyT0(rec, t0, t0, quiet: !forClick,
                    out OrbitSegment seg0, out CelestialBody body0, out string t0Source, out string t0Reason))
            {
                verdict = new MoveGateVerdict { Refusal = MoveRefusal.Degenerate, Detail = "frame=" + t0Reason };
                return false;
            }
            ReadyEndEstimate end = forClick
                ? BuildReadyEndEstimate(rec, t0, hydrateSnapshot: true)
                : GetReadyEndEstimateForScan(rec, t0);
            CelestialBody body1 = end.Ok ? MapRenderProbe.ResolveBodyByName(end.BodyName) : null;
            if (!end.Ok || body1 == null)
            {
                verdict = new MoveGateVerdict
                {
                    Refusal = MoveRefusal.Degenerate,
                    Detail = "frame=" + (end.Ok ? "no-spawn-body" : end.Reason),
                };
                return false;
            }

            bool refT0Ok = TimeJumpReference.TryEvaluate(seg0, body0, t0, out Vector3d rRef0, out Vector3d vRef0);
            bool refT1Ok = TimeJumpReference.TryEvaluate(end.Segment, body1, t1, out Vector3d rRef1, out Vector3d vRef1);
            LocalFrame f0 = default(LocalFrame);
            LocalFrame f1 = default(LocalFrame);
            bool frameOk = refT0Ok && refT1Ok
                && TimeJumpPlacement.TryBuildFrame(rRef0, vRef0, out f0)
                && TimeJumpPlacement.TryBuildFrame(rRef1, vRef1, out f1);
            bool sameBody = ReferenceEquals(body0, body1);

            var members = new List<PlacementMember>();
            bool memberInAtmo = false;
            string memberInAtmoName = null;
            CollectGroup(active, rec, members, ref memberInAtmo, ref memberInAtmoName);

            var inputs = new MoveGateInputs
            {
                ActiveInsideAtmosphere = IsVesselInsideAtmosphere(active),
                MemberInsideAtmosphere = memberInAtmo,
                MemberInsideAtmosphereName = memberInAtmoName,
                ReferenceInsideAtmosphereT0 = refT0Ok
                    && TimeJumpPlacement.IsInsideAtmosphere(body0.atmosphere, rRef0.magnitude - body0.Radius, body0.atmosphereDepth),
                ReferenceInsideAtmosphereT1 = refT1Ok
                    && TimeJumpPlacement.IsInsideAtmosphere(body1.atmosphere, rRef1.magnitude - body1.Radius, body1.atmosphereDepth),
                ActiveBody = active.mainBody.name,
                ReferenceBodyT0 = body0.name,
                ReferenceBodyT1 = body1.name,
                // Both states built on this tick; the T1 orbit's node line already carries the
                // spawn UT's rotation, so a, e, the plane and the node line compare directly.
                BurnDeltaV = frameOk && sameBody
                    ? TimeJumpPlacement.EstimateBurnDeltaV(rRef0, vRef0, rRef1, vRef1, body0.gravParameter)
                    : 0.0,
                FrameDegenerate = !frameOk,
            };

            SlideResult slide = new SlideResult { Cleared = true };
            string firstOverlap = null;
            if (frameOk && sameBody && ReferenceEquals(body1, active.mainBody))
            {
                var room = new List<SpawnRoomMember>(members.Count);
                for (int i = 0; i < members.Count; i++)
                {
                    PlacementMember m = members[i];
                    m.CapturedRelative = TimeJumpPlacement.Capture(f0, rRef0, vRef0, m.Vessel.orbit.pos, m.Vessel.orbit.vel);
                    m.Relative = m.CapturedRelative;
                    room.Add(new SpawnRoomMember
                    {
                        Offset = m.CapturedRelative.Offset,
                        HalfExtent = VesselRadius(m.Vessel),
                        Name = m.Name,
                    });
                }
                float padding = (float)DestinationPaddingMeters;
                TimeJumpPlacement.OverlapsSpawnAtShift(end.SpawnHalfExtent, room, 0.0, padding, out firstOverlap);
                slide = TimeJumpPlacement.ComputeReadySpawnRoomSlide(
                    end.SpawnHalfExtent, room, members[0].CapturedRelative.Offset.y, padding);
                if (forClick)
                {
                    string line = TimeJumpPlacement.FormatSpawnRoomLine(
                        rec.RecordingId, slide, members.Count, end.SpawnHalfExtent, padding, firstOverlap);
                    if (slide.ShiftMeters != 0.0 || !slide.Cleared)
                        ParsekLog.Info(PlacementTag, line);
                    else
                        ParsekLog.Verbose(PlacementTag, line);
                }

                if (!slide.Cleared)
                {
                    inputs.DestinationTaken = true;
                    inputs.DestinationBlocker = (rec.VesselName ?? "?") + " spawn";
                    inputs.DestinationGap = 0.0;
                }
                else
                {
                    var placed = new List<PlacedBody>(members.Count);
                    for (int i = 0; i < members.Count; i++)
                    {
                        PlacementMember m = members[i];
                        m.Relative = TimeJumpPlacement.ShiftAlongTrack(m.CapturedRelative, slide.ShiftMeters);
                        TimeJumpPlacement.Place(f1, rRef1, vRef1, m.Relative, out Vector3d rPlaced, out _);
                        placed.Add(new PlacedBody
                        {
                            Position = rPlaced,
                            Radius = VesselRadius(m.Vessel),
                            Name = m.Name,
                        });
                    }
                    List<PlacedBody> others = CollectDestinationOthers(members, body1, t1);
                    inputs.DestinationTaken = TimeJumpPlacement.TryFindDestinationConflict(
                        placed, others, DestinationPaddingMeters, out inputs.DestinationBlocker,
                        out inputs.DestinationGap);
                }
            }

            verdict = TimeJumpPlacement.EvaluateGates(inputs);
            if (!verdict.Passed)
                return false;

            plan = new PlacementPlan
            {
                RecordingId = rec.RecordingId,
                VesselName = rec.VesselName,
                Kind = PlacementKindReady,
                Segment = seg0,
                Body = body0,
                EndSegment = end.Segment,
                EndBody = body1,
                EndSeedSource = end.SeedSource,
                T0 = t0,
                T1 = t1,
                DepartureUT = t1,
                Members = members,
                SlideMeters = slide.ShiftMeters,
                ExpectedSpawnDistance = members[0].Relative.Offset.magnitude,
            };
            CompletePlanAtClick(plan, active, body0, rRef0, vRef0, forClick, drawnRefWorldT0);
            if (forClick)
                ParsekLog.Verbose(PlacementTag,
                    string.Format(ic,
                        "Placement plan: ref={0} kind=Ready T0={1:F1} T1={2:F1} t0Source={3} endSource={4} burnDv={5:F3} group={6} expectedSpawnDistance={7:F1}m",
                        rec.RecordingId ?? "(null)", t0, t1, t0Source ?? "?", end.SeedSource ?? "?",
                        inputs.BurnDeltaV, members.Count, plan.ExpectedSpawnDistance));
            return true;
        }

        private static void CollectGroup(
            Vessel active, Recording rec, List<PlacementMember> members,
            ref bool memberInAtmo, ref string memberInAtmoName)
        {
            members.Add(NewMember(active, isActive: true));
            List<Vessel> loaded = FlightGlobals.VesselsLoaded;
            if (loaded == null)
                return;
            Vector3d aPos = active.orbit.pos;
            Vector3d aVel = active.orbit.vel;
            for (int i = 0; i < loaded.Count; i++)
            {
                Vessel v = loaded[i];
                if (v == null || ReferenceEquals(v, active) || v.orbit == null)
                    continue;
                bool ghostMap = GhostMapPresence.IsGhostMapVessel(v.persistentId);
                bool surface = !TimeJumpPlacement.IsOrbitalPlayerVessel(v.situation);
                bool sameBody = ReferenceEquals(v.mainBody, active.mainBody);
                double distance = sameBody ? (v.orbit.pos - aPos).magnitude : double.PositiveInfinity;
                double relSpeed = sameBody ? (v.orbit.vel - aVel).magnitude : double.PositiveInfinity;
                if (!TimeJumpPlacement.IsGroupMember(distance, relSpeed, surface, ghostMap, sameBody))
                    continue;
                members.Add(NewMember(v, isActive: false));
                if (!memberInAtmo && IsVesselInsideAtmosphere(v))
                {
                    memberInAtmo = true;
                    memberInAtmoName = Recording.ResolveLocalizedName(v.vesselName);
                }
            }
        }

        private static PlacementMember NewMember(Vessel v, bool isActive)
        {
            return new PlacementMember
            {
                Vessel = v,
                Pid = v.persistentId,
                Name = Recording.ResolveLocalizedName(v.vesselName),
                IsActive = isActive,
            };
        }

        private static bool IsVesselInsideAtmosphere(Vessel v)
        {
            CelestialBody b = v != null ? v.mainBody : null;
            if (b == null)
                return true;
            return TimeJumpPlacement.IsInsideAtmosphere(b.atmosphere, v.altitude, b.atmosphereDepth);
        }

        private static double VesselRadius(Vessel v)
        {
            if (v == null)
                return DestinationFallbackRadiusMeters;
            double r = v.vesselSize.magnitude * 0.5;
            if (!(r > 0.0) || double.IsInfinity(r))
                return DestinationFallbackRadiusMeters;
            return Math.Max(r, 1.0);
        }

        /// <summary>
        /// Every orbital vessel outside the group on the reference's body, at its own
        /// predicted spot at T1 (Zup body-relative, the orbit carries it). Surface vessels
        /// are not modelled: the atmosphere gate keeps the group out of reach of Kerbin's,
        /// and an airless body's surface is not a Leaves destination.
        /// </summary>
        private static List<PlacedBody> CollectDestinationOthers(
            List<PlacementMember> members, CelestialBody body, double t1)
        {
            var others = new List<PlacedBody>();
            List<Vessel> all = FlightGlobals.Vessels;
            if (all == null)
                return others;
            for (int i = 0; i < all.Count; i++)
            {
                Vessel v = all[i];
                if (v == null || v.orbit == null || !ReferenceEquals(v.mainBody, body))
                    continue;
                if (GhostMapPresence.IsGhostMapVessel(v.persistentId))
                    continue;
                if (!TimeJumpPlacement.IsOrbitalPlayerVessel(v.situation))
                    continue;
                bool isMember = false;
                for (int m = 0; m < members.Count; m++)
                {
                    if (ReferenceEquals(members[m].Vessel, v))
                    {
                        isMember = true;
                        break;
                    }
                }
                if (isMember)
                    continue;
                Vector3d pos = v.orbit.getRelativePositionAtUT(t1);
                if (!TimeJumpPlacement.IsFinite(pos.x))
                    continue;
                others.Add(new PlacedBody
                {
                    Position = pos,
                    Radius = VesselRadius(v),
                    Name = Recording.ResolveLocalizedName(v.vesselName),
                });
            }
            return others;
        }

        /// <summary>
        /// Frame F of a Leaves placement jump (plan 1.3 steps 1-5): re-gates at the click,
        /// packs every loaded vessel, holds stock's unpack, sets the clock to
        /// <paramref name="t1"/> and arms the placement for the next frame. Returns false when the
        /// move is refused (logged): a gate refusal changes nothing; a <c>pack-failed</c> refusal
        /// comes after the loaded vessels were packed (no unpack hold is set, so stock unpacks
        /// them on the next frame and a live recorder keeps a near-zero rails segment) and the
        /// clock has not moved.
        /// </summary>
        internal static bool TryStartLeavesPlacementJump(
            Recording rec, double t1, double departureUT, Vessel active,
            FlightRecorder recorder, Func<Vector3d?> drawnReference, out MoveGateVerdict verdict)
        {
            return TryStartPlacementJumpCore(rec, t1, recorder, drawnReference, out verdict,
                (double t0, Vector3d? drawn, out PlacementPlan p, out MoveGateVerdict v) =>
                    TryBuildLeavesPlacementPlan(rec, t0, t1, departureUT, active,
                        captureAttitude: true, drawnRefWorldT0: drawn, out p, out v));
        }

        /// <summary>
        /// Frame F of a Ready placement jump (plan Step 2 item 3): the clock goes to the
        /// recording's end and, on the next frame, the group is placed beside the reference's
        /// spawn spot. Same contract as <see cref="TryStartLeavesPlacementJump"/>.
        /// </summary>
        internal static bool TryStartReadyPlacementJump(
            Recording rec, Vessel active, FlightRecorder recorder, Func<Vector3d?> drawnReference,
            out MoveGateVerdict verdict)
        {
            double t1 = rec != null ? rec.EndUT : double.NaN;
            return TryStartPlacementJumpCore(rec, t1, recorder, drawnReference, out verdict,
                (double t0, Vector3d? drawn, out PlacementPlan p, out MoveGateVerdict v) =>
                    TryBuildReadyPlacementPlan(rec, t0, active, forClick: true,
                        drawnRefWorldT0: drawn, out p, out v));
        }

        private static bool TryStartPlacementJumpCore(
            Recording rec, double t1, FlightRecorder recorder, Func<Vector3d?> drawnReference,
            out MoveGateVerdict verdict, PlacementPlanBuilder buildPlan)
        {
            string recId = rec != null ? rec.RecordingId : null;
            if (pendingPlacement != null)
            {
                verdict = new MoveGateVerdict { Refusal = MoveRefusal.None, Detail = "pending" };
                ParsekLog.Warn(PlacementTag,
                    "Placement jump refused: reason=pending detail=a placement is already armed for ref="
                    + (pendingPlacement.RecordingId ?? "(null)") + " ref=" + (recId ?? "(null)"));
                return false;
            }

            if (TimeWarp.CurrentRateIndex > 0)
            {
                TimeWarp.SetRate(0, true);
                ParsekLog.Info(PlacementTag, "Placement jump: stopped time warp before jump");
            }

            double t0 = Planetarium.GetUniversalTime();
            if (!(t1 > t0))
            {
                verdict = new MoveGateVerdict { Refusal = MoveRefusal.None, Detail = "not-forward" };
                ParsekLog.Warn(PlacementTag,
                    string.Format(ic,
                        "Placement jump refused: reason=not-forward detail=t0={0:F1} t1={1:F1} ref={2}",
                        t0, t1, recId ?? "(null)"));
                return false;
            }

            Vector3d? drawnT0 = drawnReference != null ? drawnReference() : null;
            if (!buildPlan(t0, drawnT0, out PlacementPlan plan, out verdict))
            {
                ParsekLog.Info(PlacementTag,
                    "Placement jump refused: reason=" + verdict.Refusal + " detail=" + verdict.Detail
                    + " ref=" + (recId ?? "(null)"));
                return false;
            }
            plan.DrawnReference = drawnReference;

            isTimeJumpLaunchAutoRecordInProgress = true;
            try
            {
                NotePlayheadSweepBeforeJump(EffectiveState.ComputeERS(), t0);

                PutLoadedVesselsOnRails();
                for (int i = 0; i < plan.Members.Count; i++)
                {
                    Vessel v = plan.Members[i].Vessel;
                    if (v == null || !v.packed)
                    {
                        verdict = new MoveGateVerdict { Refusal = MoveRefusal.None, Detail = "pack-failed" };
                        ParsekLog.Warn(PlacementTag,
                            "Placement jump refused: reason=pack-failed detail=pid="
                            + plan.Members[i].Pid.ToString(ic) + " ref=" + (recId ?? "(null)"));
                        return false;
                    }
                }
                OrbitPhysicsManager.HoldVesselUnpack(PlacementArmHoldFixedUpdates);

                NotifyRecorder(recorder, t0, t1);
                Planetarium.SetUniversalTime(t1);
                ArmTimeJumpLaunchAutoRecordSuppression("placement");
                ConverterJumpPolicy.Apply(t0, t1, "placement");
                // The ledger catches up in this frame, so a placement dropped before the next
                // frame still leaves a complete plain forward jump.
                RecalculateLedgerAfterTimeJump("placement", t1);

                plan.ArmedFrame = Time.frameCount;
                plan.ArmedFixedTime = Time.fixedTime;
                pendingPlacement = plan;
                watchedPlacement = null;
                ParsekLog.Info(PlacementTag,
                    string.Format(ic,
                        "Placement jump armed: ref={0} kind={1} T0={2:F1} T1={3:F1} group={4} hold={5} frame={6}",
                        plan.RecordingId ?? "(null)", plan.Kind, t0, t1, plan.Members.Count,
                        PlacementArmHoldFixedUpdates, plan.ArmedFrame));
            }
            finally
            {
                isTimeJumpLaunchAutoRecordInProgress = false;
            }
            return true;
        }

        /// <summary>
        /// Pure: the placement runs on the first Update after the arming frame in which a
        /// fixed update has run (so <c>Planetarium.FixedUpdate</c> has rebuilt body rotation
        /// and Zup for the new clock).
        /// </summary>
        internal static bool IsPlacementDue(int armedFrame, float armedFixedTime, int frame, float fixedTime)
        {
            return frame > armedFrame && fixedTime > armedFixedTime;
        }

        /// <summary>
        /// Frame F+1 (plan 1.3 steps 6-8, 11). Called from the top of
        /// <c>ParsekFlight.Update</c>; a no-op until the placement is due.
        /// </summary>
        internal static void TickPendingPlacement(FlightRecorder recorder, BackgroundRecorder background)
        {
            PlacementPlan plan = pendingPlacement;
            if (plan == null)
                return;
            if (!IsPlacementDue(plan.ArmedFrame, plan.ArmedFixedTime, Time.frameCount, Time.fixedTime))
                return;
            pendingPlacement = null;

            isTimeJumpLaunchAutoRecordInProgress = true;
            try
            {
                ExecutePlacement(plan, recorder, background);
            }
            catch (Exception ex)
            {
                ParsekLog.Error(PlacementTag,
                    "Placement jump dropped: reason=exception ref=" + (plan.RecordingId ?? "(null)")
                    + " error=" + ex.Message);
            }
            finally
            {
                isTimeJumpLaunchAutoRecordInProgress = false;
            }
        }

        private static void ExecutePlacement(
            PlacementPlan plan, FlightRecorder recorder, BackgroundRecorder background)
        {
            Vessel active = FlightGlobals.ActiveVessel;
            PlacementMember activeMember = null;
            for (int i = 0; i < plan.Members.Count; i++)
                if (plan.Members[i].IsActive) activeMember = plan.Members[i];
            if (activeMember == null || active == null || !ReferenceEquals(active, activeMember.Vessel))
            {
                DropPendingPlacementCore(plan, "active-vessel-changed");
                return;
            }
            for (int i = 0; i < plan.Members.Count; i++)
            {
                Vessel v = plan.Members[i].Vessel;
                if (v == null || !v.packed || v.orbit == null || v.orbitDriver == null)
                {
                    DropPendingPlacementCore(plan, "member-not-packed pid=" + plan.Members[i].Pid.ToString(ic));
                    return;
                }
            }

            double placeUT = Planetarium.GetUniversalTime();
            if (!TryResolvePlacementReference(plan, placeUT, out Vector3d rRef1, out Vector3d vRef1,
                    out CelestialBody refBody, out string refReason))
            {
                DropPendingPlacementCore(plan, refReason);
                return;
            }
            if (!TimeJumpPlacement.TryBuildFrame(rRef1, vRef1, out LocalFrame f1))
            {
                DropPendingPlacementCore(plan, "reference-frame-degenerate");
                return;
            }
            plan.PlaceUT = placeUT;

            var moved = new List<uint>(plan.Members.Count);
            Quaternion activeWorldRot = Quaternion.identity;
            Vector3d activeR = Vector3d.zero;
            Vector3d activeV = Vector3d.zero;
            for (int i = 0; i < plan.Members.Count; i++)
            {
                PlacementMember m = plan.Members[i];
                Vessel v = m.Vessel;
                Quaternion worldRot = TimeJumpPlacement.DecodeAttitude(m.OrbitalFrameAttitude, rRef1, vRef1);
                TimeJumpPlacement.Place(f1, rRef1, vRef1, m.Relative, out Vector3d r, out Vector3d vel);
                // Rotation first: the orbit driver offsets the vessel by its rotated local CoM.
                v.SetRotation(worldRot);
                v.orbit.UpdateFromStateVectors(r, vel, refBody, placeUT);
                v.orbitDriver.updateFromParameters();

                Vector3d rRead = v.orbit.getRelativePositionAtUT(placeUT);
                Vector3d vRead = v.orbit.getOrbitalVelocityAtUT(placeUT);
                RelativeState atT1 = TimeJumpPlacement.Capture(f1, rRef1, vRef1, rRead, vRead);
                ParsekLog.Info(PlacementTag,
                    TimeJumpPlacement.FormatPlacedVesselLine(m.Pid, m.Name, m.CapturedRelative, atT1));
                moved.Add(m.Pid);
                if (m.IsActive)
                {
                    activeWorldRot = worldRot;
                    activeR = r;
                    activeV = vel;
                }
            }

            FloatingOrigin.SetOffset(active.transform.position);
            OrbitPhysicsManager.CheckReferenceFrame();
            OrbitPhysicsManager.HoldVesselUnpack(PlacementPlaceHoldFixedUpdates);
            for (int i = 0; i < plan.Members.Count; i++)
            {
                try
                {
                    plan.Members[i].Vessel.UpdatePosVel();
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(PlacementTag,
                        "Placement: UpdatePosVel failed pid=" + plan.Members[i].Pid.ToString(ic)
                        + " error=" + ex.Message);
                }
            }

            string recorderOutcome = "none";
            if (recorder != null)
            {
                // The recorder's own orbital-frame encode, against the PLACED state (the
                // vessel's cached CoM and velocity fields are still the pre-placement ones).
                Quaternion activeOfr = TrajectoryMath.ComputeOrbitalFrameRotation(
                    activeWorldRot,
                    TrajectoryMath.SwizzleZupBodyRelativeToWorld(activeV),
                    TrajectoryMath.SwizzleZupBodyRelativeToWorld(activeR).normalized);
                CheatTeleportAction action = recorder.OnCheatTeleport(
                    active, refBody, TeleportCause.TimeJump, activeOfr);
                recorderOutcome = action.ToString();
            }

            int bgRecaptured = 0;
            if (background != null)
            {
                for (int i = 0; i < plan.Members.Count; i++)
                {
                    if (plan.Members[i].IsActive)
                        continue;
                    BackgroundRecaptureOutcome o = background.RecaptureOnRailsOrbit(plan.Members[i].Pid, placeUT);
                    if (o == BackgroundRecaptureOutcome.Recaptured || o == BackgroundRecaptureOutcome.ReplacedSameUT)
                        bgRecaptured++;
                }
            }

            ParsekLog.Info(PlacementTag, TimeJumpPlacement.FormatPlacementSummary(
                plan.RecordingId, plan.Kind, plan.T0, plan.T1, placeUT, moved,
                plan.RefGapToDrawn, plan.SlideMeters, recorderOutcome, bgRecaptured));
            watchedPlacement = plan;
        }

        /// <summary>
        /// The reference in frame F+1 (plan 1.3 step 6). Leaves: its segment. Ready: the
        /// spawner's own terminal-orbit resolve at this UT, so the group is placed against the
        /// orbit the spawn will build (plan Step 2 item 1), logged against the click estimate.
        /// </summary>
        private static bool TryResolvePlacementReference(
            PlacementPlan plan, double placeUT, out Vector3d rRef1, out Vector3d vRef1,
            out CelestialBody refBody, out string reason)
        {
            rRef1 = Vector3d.zero;
            vRef1 = Vector3d.zero;
            refBody = plan.Body;
            reason = null;
            if (!plan.IsReady)
            {
                if (TimeJumpReference.TryEvaluate(plan.Segment, plan.Body, placeUT, out rRef1, out vRef1))
                    return true;
                reason = "reference-frame-degenerate";
                return false;
            }

            Recording rec = RecordingStore.TryFindCommittedRecordingById(plan.RecordingId);
            if (rec == null)
            {
                reason = "reference-recording-missing";
                return false;
            }
            plan.ReferenceSpawnedBeforePlacement = rec.VesselSpawned;
            Vector3d rEstimate = Vector3d.zero;
            bool haveEstimate = plan.EndBody != null
                && TimeJumpReference.TryEvaluate(plan.EndSegment, plan.EndBody, placeUT, out rEstimate, out _);
            if (!TimeJumpReference.TryResolveReadyEnd(rec, placeUT, placeUT,
                    out OrbitSegment seg, out CelestialBody body, out string seedSource, out string endReason))
            {
                reason = "reference-unresolved " + (endReason ?? "?");
                return false;
            }
            if (!ReferenceEquals(body, plan.EndBody))
            {
                reason = "reference-body-changed";
                return false;
            }
            if (!TimeJumpReference.TryEvaluate(seg, body, placeUT, out rRef1, out vRef1))
            {
                reason = "reference-frame-degenerate";
                return false;
            }
            plan.EndSegment = seg;
            plan.EndSeedSource = seedSource;
            refBody = body;
            ParsekLog.Info(PlacementTag, TimeJumpPlacement.FormatReadyReferenceLine(
                plan.RecordingId, seedSource, body.bodyName, placeUT, seg.semiMajorAxis,
                seg.eccentricity, haveEstimate ? (rRef1 - rEstimate).magnitude : double.NaN));
            return true;
        }

        /// <summary>The reference's state at <paramref name="ut"/> after the placement.</summary>
        private static bool TryEvaluateReferenceAfterPlacement(
            PlacementPlan plan, double ut, out Vector3d r, out Vector3d v)
        {
            if (plan.IsReady)
                return TimeJumpReference.TryEvaluate(plan.EndSegment, plan.EndBody, ut, out r, out v);
            return TimeJumpReference.TryEvaluate(plan.Segment, plan.Body, ut, out r, out v);
        }

        /// <summary>
        /// A Ready move's spawn check (design rule 4): once the reference is a real vessel and
        /// every member is unpacked, its distance from the player's vessel against the placed
        /// distance and its gap to the reference's T1 orbit. Logged once. A packed vessel's
        /// <c>GetWorldPos3D</c> (its cached CoM) can still read the pre-placement spot, so the
        /// world distance waits for the unpack, and the orbit distance is logged beside it.
        /// </summary>
        private static void TickReadySpawnCheck(PlacementPlan plan, Vessel active, double now, int pendingUnpack)
        {
            if (!plan.IsReady || plan.SpawnChecked || pendingUnpack > 0)
                return;
            Recording rec = RecordingStore.TryFindCommittedRecordingById(plan.RecordingId);
            if (rec == null || rec.SpawnAbandoned || rec.TerminalSpawnCannotSpawnSafely)
            {
                plan.SpawnChecked = true;
                ParsekLog.Info(PlacementTag,
                    string.Format(ic,
                        "Placement spawn check: ref={0} ut={1:F2} outcome=no-spawn found={2} abandoned={3}",
                        plan.RecordingId ?? "(null)", now, rec != null,
                        rec != null && (rec.SpawnAbandoned || rec.TerminalSpawnCannotSpawnSafely)));
                return;
            }
            if (!rec.VesselSpawned || rec.SpawnedVesselPersistentId == 0)
                return;
            plan.SpawnChecked = true;
            Vessel spawned = FlightRecorder.FindVesselByPid(rec.SpawnedVesselPersistentId);
            double toActive = double.NaN;
            double toActiveOrbit = double.NaN;
            double orbitGap = double.NaN;
            if (spawned != null)
            {
                if (active != null)
                {
                    toActive = (spawned.GetWorldPos3D() - active.GetWorldPos3D()).magnitude;
                    if (spawned.orbit != null && active.orbit != null
                        && ReferenceEquals(spawned.orbit.referenceBody, active.orbit.referenceBody))
                        toActiveOrbit = (spawned.orbit.getRelativePositionAtUT(now)
                            - active.orbit.getRelativePositionAtUT(now)).magnitude;
                }
                if (spawned.orbit != null
                    && TryEvaluateReferenceAfterPlacement(plan, now, out Vector3d rRef, out _))
                    orbitGap = (spawned.orbit.getRelativePositionAtUT(now) - rRef).magnitude;
            }
            ParsekLog.Info(PlacementTag, TimeJumpPlacement.FormatReadySpawnCheckLine(
                plan.RecordingId, rec.SpawnedVesselPersistentId, now, toActive,
                plan.ExpectedSpawnDistance, orbitGap, plan.ReferenceSpawnedBeforePlacement,
                toActiveOrbit));
        }

        /// <summary>
        /// After the placement (plan 1.3 step 9): the per-member check at its first unpacked
        /// frame, the attitude re-assert, and the departure check. Called from
        /// <c>ParsekFlight.Update</c> after playback, so the drawn ghost is this frame's.
        /// </summary>
        internal static void TickPlacementWatch()
        {
            PlacementPlan plan = watchedPlacement;
            if (plan == null)
                return;
            double now = Planetarium.GetUniversalTime();
            Vessel active = FlightGlobals.ActiveVessel;
            Vector3d? drawn = plan.DrawnReference != null ? plan.DrawnReference() : null;

            int pendingUnpack = 0;
            for (int i = 0; i < plan.Members.Count; i++)
            {
                PlacementMember m = plan.Members[i];
                if (m.UnpackChecked)
                    continue;
                if (m.Vessel == null)
                {
                    m.UnpackChecked = true;
                    continue;
                }
                if (m.Vessel.packed)
                {
                    pendingUnpack++;
                    continue;
                }
                m.UnpackChecked = true;
                Vector3d world = m.Vessel.GetWorldPos3D();
                double refNow = drawn.HasValue ? (world - drawn.Value).magnitude : double.NaN;
                double toActiveNow = active != null ? (world - active.GetWorldPos3D()).magnitude : double.NaN;
                double attErr = double.NaN;
                bool reasserted = false;
                if (TryEvaluateReferenceAfterPlacement(plan, now, out Vector3d rRef, out Vector3d vRef))
                {
                    Quaternion expected = TimeJumpPlacement.DecodeAttitude(m.OrbitalFrameAttitude, rRef, vRef);
                    attErr = Quaternion.Angle(m.Vessel.transform.rotation, expected);
                    if (attErr > PlacementAttitudeReassertDeg)
                    {
                        m.Vessel.SetRotation(expected);
                        reasserted = true;
                    }
                }
                ParsekLog.Info(PlacementTag, TimeJumpPlacement.FormatUnpackedLine(
                    m.Pid, m.Name, now, m.RefDistanceT0, refNow, m.ToActiveT0, toActiveNow,
                    attErr, reasserted));
            }

            TickReadySpawnCheck(plan, active, now, pendingUnpack);

            if (!plan.IsReady && !plan.DepartureChecked && now >= plan.DepartureUT)
            {
                plan.DepartureChecked = true;
                double refDistance = drawn.HasValue && active != null
                    ? (active.GetWorldPos3D() - drawn.Value).magnitude
                    : double.NaN;
                ParsekLog.Info(PlacementTag,
                    string.Format(ic,
                        "Placement departure check: ref={0} ut={1:F2} departureUT={2:F2} refDistance={3}m",
                        plan.RecordingId ?? "(null)", now, plan.DepartureUT,
                        TimeJumpPlacement.FormatMeters(refDistance)));
            }

            bool timedOut = now > Math.Max(plan.DepartureUT, plan.PlaceUT) + PlacementWatchTimeoutSeconds;
            bool referenceChecked = plan.IsReady ? plan.SpawnChecked : plan.DepartureChecked;
            if ((pendingUnpack == 0 && referenceChecked) || timedOut)
            {
                watchedPlacement = null;
                ParsekLog.Info(PlacementTag,
                    string.Format(ic,
                        "Placement watch complete: ref={0} ut={1:F2} reason={2} unpackPending={3}",
                        plan.RecordingId ?? "(null)", now, timedOut ? "timeout" : "done", pendingUnpack));
            }
        }

        /// <summary>
        /// A scene change between the two frames drops the armed placement (plan 1.3): the
        /// clock has moved, every vessel keeps its natural orbit, and the jump reads as a plain
        /// forward jump. Also ends an open watch.
        /// </summary>
        internal static void DropPendingPlacement(string reason)
        {
            // The scene's Ready estimates go with it (their bodies and store version do).
            readyEndCache.Clear();
            PlacementPlan plan = pendingPlacement;
            pendingPlacement = null;
            if (plan != null)
                DropPendingPlacementCore(plan, reason);
            if (watchedPlacement != null)
            {
                ParsekLog.Verbose(PlacementTag,
                    "Placement watch ended: reason=" + (reason ?? "unknown") + " ref="
                    + (watchedPlacement.RecordingId ?? "(null)"));
                watchedPlacement = null;
            }
        }

        private static void DropPendingPlacementCore(PlacementPlan plan, string reason)
        {
            ParsekLog.Warn(PlacementTag,
                string.Format(ic,
                    "Placement jump dropped: reason={0} ref={1} T0={2:F1} T1={3:F1} (plain forward jump)",
                    reason ?? "unknown", plan.RecordingId ?? "(null)", plan.T0, plan.T1));
        }
    }
}
