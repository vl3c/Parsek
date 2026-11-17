using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // The flight scene's IGhostPositioner: places ghosts from recorded trajectories (points,
    // orbits, surface, relative and body-fixed sections, loops) and owns the positioning caches
    // nothing else reads. ParsekFlight builds the playback engine with it and keeps the state its
    // other paths share (orbit cache, log-once sets, the Re-Fly dense-frame cache).
    internal sealed partial class ParsekFlightGhostPositioner : IGhostPositioner
    {
        private readonly ParsekFlight host;

        // The recordings this positioner resolves by id (focus tree, diagnostic parent lookup,
        // the pending tree of the anchor resolver context). Never null.
        private readonly IPlaybackCatalog catalog;

        internal ParsekFlightGhostPositioner(ParsekFlight host, IPlaybackCatalog catalog = null)
        {
            this.host = host;
            this.catalog = catalog ?? RecordingPlaybackCatalog.Own;
        }

        // M4b phasing-knob body-fixed derotation: the time shift (seconds) of the positioning call
        // currently on the stack, read from GhostPlaybackState.bodyFixedShiftSeconds at the
        // IGhostPositioner entries (InterpolateAndPosition / PositionAtPoint / PositionLoop) and
        // consumed by the private InterpolateAndPosition / PositionGhostAt when resolving Absolute
        // body-fixed points (vacuum burn arcs replayed off the rotation-aligned grid render rotated
        // with the planet otherwise; the 2026-06-11 playtest's 46-degree teleports). Per-call
        // ambient with try/finally reset rather than threading a parameter through the six-deep
        // positioning chain; single-threaded Unity main loop, and 0 (the identity) for every
        // non-knob caller.
        private double activeBodyFixedShiftSeconds;

        // Per-member last re-aim ghost frame, for seam-teleport detection (segment change -> log the jump).
        private readonly Dictionary<int, (int segIdx, Vector3 pos, double loopUT)> reaimLastGhostFrame
            = new Dictionary<int, (int, Vector3, double)>();

        // Per-member last orbit-positioned ghost frame for the mode-agnostic
        // loop-seam teleport instrument (Tier-C, ghostRenderTracing-gated;
        // faithful AND re-aimed members). Unity frame number rides along so a
        // hide/show gap suppresses the stale-prev comparison.
        private readonly Dictionary<int, (int segIdx, Vector3 pos, double loopUT, double currentUT, int frame)>
            loopSeamLastGhostFrame
                = new Dictionary<int, (int, Vector3, double, double, int)>();

        // Generous upper bound on a loop ghost's own speed (m/s), added to the
        // segment body's orbital speed for the warp-scaled expected-motion term
        // of the teleport threshold. Interplanetary coast speeds run ~3-12 km/s.
        internal const double LoopSeamGhostSpeedAllowanceMps = 15_000.0;

        private readonly Dictionary<string, ParsekFlight.PredictedOrbitTailContinuityCacheEntry> predictedOrbitTailContinuityCache =
            new Dictionary<string, ParsekFlight.PredictedOrbitTailContinuityCacheEntry>();

        private readonly HashSet<string> recordedRelativeAnchorVisited =
            new HashSet<string>(StringComparer.Ordinal);

        // ===== Recorded-coordinate playback helpers =====
        //
        private const double PointHermiteMaxDeviationCapMeters = 250.0;

        private const double PointHermiteMinDeviationMeters = 10.0;

        private const double PointHermiteDeviationFraction = 0.05;

        private const bool NormalPlaybackSplinePositioningEnabled = false;

        private const long CheckpointOrbitCacheBase = long.MinValue;

        private const long CheckpointOrbitCacheRecordingStride = 1000000000000L;

        private const long CheckpointOrbitCacheSectionStride = 1000000L;

        private const long CheckpointOrbitCacheMaxRecordingIndex =
            (long.MaxValue - CheckpointOrbitCacheRecordingStride + 1) / CheckpointOrbitCacheRecordingStride;

        void IGhostPositioner.InterpolateAndPosition(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, double ut, bool suppressFx)
        {
            if (state?.ghost == null || traj?.Points == null) return;
            activeBodyFixedShiftSeconds = state.bodyFixedShiftSeconds;
            try
            {
                InterpolateAndPositionEntryCore(index, traj, state, ut);
            }
            finally
            {
                activeBodyFixedShiftSeconds = 0.0;
            }
        }

        private void InterpolateAndPositionEntryCore(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, double ut)
        {
            int playbackIdx = state.playbackIndex;
            InterpolationResult interpResult;

            if (TryInterpolateAndPositionCheckpointSection(
                index,
                traj,
                state.ghost,
                ref playbackIdx,
                ut,
                ParsekFlight.ShouldAutoActivateGhost(state),
                out interpResult))
            {
                state.SetInterpolated(interpResult);
                state.playbackIndex = playbackIdx;
                return;
            }

            int splineSectionIdx = traj.TrackSections != null && traj.TrackSections.Count > 0
                ? TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, ut)
                : -1;

            if (splineSectionIdx >= 0
                && ParsekFlight.TryGetAbsoluteSectionPlaybackFrames(
                    traj.TrackSections[splineSectionIdx],
                    out List<TrajectoryPoint> bodyFixedFrames))
            {
                string pointFrameSource = "section-frames";
                if (ParsekFlight.TryGetAbsoluteSectionPlaybackFramesForPlayback(
                        traj.TrackSections,
                        splineSectionIdx,
                        out List<TrajectoryPoint> bridgedFrames,
                        out string bridgedFrameSource))
                {
                    bodyFixedFrames = bridgedFrames;
                    pointFrameSource = bridgedFrameSource;
                }

                int absolutePlaybackIdx = playbackIdx;
                InterpolateAndPosition(
                    state.ghost,
                    bodyFixedFrames,
                    null,
                    ref absolutePlaybackIdx,
                    ut,
                    index * 10000,
                    out interpResult,
                    allowActivation: ParsekFlight.ShouldAutoActivateGhost(state),
                    skipOrbitSegments: true,
                    recordingId: traj.RecordingId,
                    sectionIndex: splineSectionIdx,
                    pointFrameSource: pointFrameSource);
                state.SetInterpolated(interpResult);
                state.playbackIndex = absolutePlaybackIdx;
                return;
            }

            bool surfaceSkip = TrajectoryMath.IsSurfaceAtUT(traj.TrackSections, ut);
            // Phase 1: pass section index so the body-fixed spline lookup can
            // gate per-section. Falls through to legacy lerp when sections
            // are absent (-1) - older flat-point recordings remain bit-exact.
            InterpolateAndPosition(state.ghost, traj.Points, traj.OrbitSegments,
                ref playbackIdx, ut, index * 10000, out interpResult,
                allowActivation: ParsekFlight.ShouldAutoActivateGhost(state), skipOrbitSegments: surfaceSkip,
                recordingId: traj.RecordingId, sectionIndex: splineSectionIdx,
                pointFrameSource: "flat-points",
                flatPointSections: traj.TrackSections);
            state.SetInterpolated(interpResult);
            state.playbackIndex = playbackIdx;
        }

        void IGhostPositioner.PositionAtPoint(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, TrajectoryPoint point)
        {
            if (state?.ghost == null) return;

            // Bug #282: for Landed/Splashed recordings, clamp the rendered
            // altitude up so the ghost mesh sits above terrain. The recording's
            // raw altitude is the root-part origin — for a Mk1-3 pod the lowest
            // mesh vertex is ~1.77 m below the root, so any recorded clearance
            // under ~2 m buries the pod. Applies only to the PositionAtPoint
            // call sites (past-end hold, loop cycle boundary, loop pause hold,
            // overlap expired): these are the "stuck at final point" paths.
            // Normal in-flight playback already gets the distance-aware terrain
            // floor from ClampGhostsToTerrain in LateUpdate; mirror that same
            // minimum here so long-range watched landed ghosts do not sink below
            // the visual terrain right as playback reaches its final point.
            TrajectoryPoint positioned = point;
            // P1 review (PR #706): when ApplyLandedGhostClearance rewrites the
            // altitude to currentTerrain + clearance, mark the altitude as
            // authoritative so PositionGhostAt skips tail-lift. Otherwise the
            // wrapper would add Δ on top of the already-clamped altitude and
            // produce currentTerrain + clearance + Δ — the same end-of-descent
            // pop the tail-lift was meant to remove, in the opposite direction.
            bool altitudeAuthoritative = false;
            if (traj != null)
            {
                if (ParsekFlight.ShouldApplyImmediatePointSurfaceClearance(
                        traj.TerminalStateValue,
                        traj.SurfacePos.HasValue))
                {
                    positioned = ParsekFlight.ApplyLandedGhostClearance(
                        point, index, traj.VesselName, traj.TerrainHeightAtEnd,
                        ResolveImmediateLandedGhostClearanceMeters(
                            index, traj.VesselName,
                            point.bodyName, point.latitude, point.longitude, point.altitude));
                    altitudeAuthoritative = true;
                }
            }

            activeBodyFixedShiftSeconds = state.bodyFixedShiftSeconds;
            try
            {
                PositionGhostAt(state.ghost, positioned, traj?.RecordingId, altitudeAuthoritative);
            }
            finally
            {
                activeBodyFixedShiftSeconds = 0.0;
            }

            // Keep the watch-mode overlay + diagnostics consistent with the
            // clamped position. WatchModeController reads state.lastInterpolatedAltitude
            // for its "ghost at alt N m on Kerbin" line — if we don't update it,
            // the log reports the buried raw altitude even after the visual is fixed.
            state.SetInterpolated(new InterpolationResult(
                positioned.velocity,
                positioned.bodyName,
                positioned.altitude));
        }

        bool IGhostPositioner.TryResolveExplosionAnchorPosition(int index,
            IPlaybackTrajectory traj, GhostPlaybackState state, out Vector3 worldPosition)
        {
            if (state?.ghost == null)
            {
                worldPosition = Vector3.zero;
                string missingGhostVesselName = traj != null && !string.IsNullOrEmpty(traj.VesselName)
                    ? traj.VesselName
                    : state?.vesselName ?? "?";
                ParsekLog.Warn("TerrainCorrect",
                    $"Explosion anchor resolve #{index} (\"{missingGhostVesselName}\") skipped: ghost root is null");
                return false;
            }

            Vector3 rawWorldPos = state.ghost.transform.position;
            worldPosition = rawWorldPos;
            string safeVesselName = traj != null && !string.IsNullOrEmpty(traj.VesselName)
                ? traj.VesselName
                : state.vesselName ?? "?";
            string bodyName = ParsekFlight.ResolveExplosionAnchorBodyName(
                state.lastInterpolatedBodyName, traj);
            if (string.IsNullOrEmpty(bodyName))
                return true;

            CelestialBody body = FindBodyByNameOrNull(FlightGlobals.Bodies, bodyName);
            if (body == null || body.pqsController == null)
                return true;

            double latitude = body.GetLatitude(rawWorldPos);
            double longitude = body.GetLongitude(rawWorldPos);
            double rawAltitude = body.GetAltitude(rawWorldPos);
            double terrainHeight = body.TerrainAltitude(latitude, longitude, true);

            double minClearance = ParsekFlight.ImmediateLandedGhostClearanceFallbackMeters;
            if (FlightGlobals.ActiveVessel != null)
            {
                double distanceToVessel = Vector3d.Distance(
                    rawWorldPos, FlightGlobals.ActiveVessel.GetWorldPos3D());
                minClearance = ParsekFlight.ComputeTerrainClearance(distanceToVessel);
            }
            else
            {
                ParsekLog.Verbose("TerrainCorrect",
                    $"Explosion anchor clearance fallback #{index} (\"{safeVesselName}\"): " +
                    $"active vessel unavailable — using legacy floor " +
                    $"{ParsekFlight.ImmediateLandedGhostClearanceFallbackMeters.ToString("F1", CultureInfo.InvariantCulture)}m");
            }

            double clampedAltitude = TerrainCorrector.ClampAltitude(
                rawAltitude, terrainHeight, minClearance);
            double resolvedAltitude = clampedAltitude > rawAltitude ? clampedAltitude : rawAltitude;
            worldPosition = clampedAltitude > rawAltitude
                ? body.GetWorldSurfacePosition(latitude, longitude, clampedAltitude)
                : rawWorldPos;

            // Keep the watch overlay / diagnostics aligned with the anchor we
            // actually returned, not just the "had to clamp" branch.
            state.lastInterpolatedBodyName = body.name;
            state.lastInterpolatedAltitude = resolvedAltitude;

            if (clampedAltitude <= rawAltitude)
                return true;

            string cycleText = state.loopCycleIndex >= 0
                ? $" cycle={state.loopCycleIndex}"
                : "";
            ParsekLog.Verbose("TerrainCorrect",
                $"Explosion anchor clamp #{index} (\"{safeVesselName}\"): " +
                $"alt={rawAltitude.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"terrain={terrainHeight.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"-> {clampedAltitude.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"(clearance={minClearance.ToString("F1", CultureInfo.InvariantCulture)}m){cycleText}");
            return true;
        }

        private static double ResolveImmediateLandedGhostClearanceMeters(
            int index, string vesselName,
            string bodyName, double latitude, double longitude, double altitude)
        {
            CelestialBody body = FindBodyByNameOrNull(FlightGlobals.Bodies, bodyName);
            bool hasBody = body != null;
            bool hasActiveVessel = FlightGlobals.ActiveVessel != null;
            string fallbackReason = ParsekFlight.ResolveImmediateLandedGhostClearanceFallbackReason(
                hasBody, hasActiveVessel);
            if (fallbackReason != null)
            {
                // #373: Silent regression to the legacy 0.5 m floor when the
                // distance-aware resolver cannot run. Rate-limit per
                // ghost+reason so scene-transition cold-start paths surface
                // without spamming the log on every frame.
                string safeVessel = string.IsNullOrEmpty(vesselName) ? "?" : vesselName;
                string safeBody = string.IsNullOrEmpty(bodyName) ? "?" : bodyName;
                ParsekLog.WarnRateLimited("TerrainCorrect",
                    $"landed-ghost-clearance-fallback-{index}-{fallbackReason}",
                    $"Landed ghost clearance fallback #{index} (\"{safeVessel}\") on body='{safeBody}': " +
                    $"{fallbackReason} — using legacy floor " +
                    $"{ParsekFlight.ImmediateLandedGhostClearanceFallbackMeters.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}m " +
                    "(distance-aware #262 path skipped)");
                return ParsekFlight.ImmediateLandedGhostClearanceFallbackMeters;
            }

            Vector3d pointWorld = body.GetWorldSurfacePosition(latitude, longitude, altitude);
            double distToVessel = Vector3d.Distance(pointWorld, FlightGlobals.ActiveVessel.GetWorldPos3D());
            return ParsekFlight.ComputeTerrainClearance(distToVessel);
        }

        void IGhostPositioner.PositionAtSurface(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state)
        {
            if (state?.ghost == null || traj?.SurfacePos == null) return;

            SurfacePosition positioned = traj.SurfacePos.Value;
            if (ParsekFlight.ShouldApplyImmediateSurfacePositionClearance(
                    traj.TerminalStateValue,
                    traj.TerrainHeightAtEnd,
                    traj.SurfacePos.HasValue))
            {
                var syntheticPoint = new TrajectoryPoint
                {
                    latitude = positioned.latitude,
                    longitude = positioned.longitude,
                    altitude = positioned.altitude,
                    rotation = positioned.rotation,
                    bodyName = positioned.body,
                    // Phase 7: synthetic terminal point for landed ghost clamp;
                    // playback path uses recorded altitude directly, no clearance.
                    recordedGroundClearance = double.NaN,
                };
                syntheticPoint = ParsekFlight.ApplyLandedGhostClearance(
                    syntheticPoint, index, traj.VesselName, traj.TerrainHeightAtEnd,
                    ResolveImmediateLandedGhostClearanceMeters(
                        index, traj.VesselName,
                        positioned.body, positioned.latitude, positioned.longitude, positioned.altitude));
                positioned.altitude = syntheticPoint.altitude;
            }

            PositionGhostAtSurface(state.ghost, positioned, ParsekFlight.ShouldAutoActivateGhost(state),
                traj?.RecordingId);
            // Surface holds are stationary. Keep watch-mode diagnostics and
            // downstream state consumers from inheriting stale in-flight velocity.
            state.SetInterpolated(new InterpolationResult(
                Vector3.zero,
                positioned.body,
                positioned.altitude).WithSurfaceVelocity(
                    Vector3.zero, GhostSurfaceVelocitySource.SurfaceHold));
        }

        ZoneRenderingResult IGhostPositioner.ApplyZoneRendering(int index, GhostPlaybackState state,
            IPlaybackTrajectory traj, double distance, double playbackUT, int protectedIndex)
        {
            // Delegates to the private ApplyZoneRendering which has the same signature
            return host.ApplyZoneRenderingImpl(index, state, traj, distance, playbackUT, protectedIndex);
        }

        // The body lookups below run per frame for every ghost. Each matcher is the predicate of
        // the List.Find lambda it replaced, with the name passed as an argument instead of
        // captured, so a lookup allocates no closure. The delegates are built once.
        internal static readonly Func<CelestialBody, string, bool> BodyNameMatches =
            (b, name) => b.name == name;
        internal static readonly Func<CelestialBody, string, bool> LiveBodyBodyNameMatches =
            (b, name) => b != null && b.bodyName == name;
        internal static readonly Func<CelestialBody, string, bool> TailLiftTerminalBodyNameMatches =
            (b, name) => !object.ReferenceEquals(b, null)
                && (string.Equals(b.bodyName, name, StringComparison.Ordinal)
                    || string.Equals(b.name, name, StringComparison.Ordinal));

        /// <summary>
        /// <c>List.Find</c> semantics without a capturing predicate: the first item, in list
        /// order, for which <paramref name="matches"/> returns true, or <c>default</c> when none
        /// does. The matcher sees every item up to the first match, a null entry included (so a
        /// matcher that dereferences it throws there, as the replaced lambda did), and a null
        /// list throws <see cref="NullReferenceException"/> like a call on a null receiver.
        /// </summary>
        internal static T FindFirstMatching<T>(IList<T> items, string name, Func<T, string, bool> matches)
        {
            for (int i = 0; i < items.Count; i++)
            {
                T item = items[i];
                if (matches(item, name))
                    return item;
            }
            return default(T);
        }

        // FlightGlobals.Bodies.Find(b => b.name == name)
        private static CelestialBody FindBodyByName(List<CelestialBody> bodies, string name)
        {
            return FindFirstMatching(bodies, name, BodyNameMatches);
        }

        // FlightGlobals.Bodies?.Find(b => b.name == name)
        private static CelestialBody FindBodyByNameOrNull(List<CelestialBody> bodies, string name)
        {
            return bodies == null ? null : FindFirstMatching(bodies, name, BodyNameMatches);
        }

        // FlightGlobals.Bodies?.Find(b => b != null && b.bodyName == name)
        private static CelestialBody FindLiveBodyByBodyNameOrNull(List<CelestialBody> bodies, string name)
        {
            return bodies == null ? null : FindFirstMatching(bodies, name, LiveBodyBodyNameMatches);
        }

        // Runs per frame for every RELATIVE-section ghost: a plain loop, no predicate closure.
        private static CelestialBody FindBodyByNameNoAlloc(string bodyName)
        {
            List<CelestialBody> bodies = FlightGlobals.Bodies;
            if (bodies == null || string.IsNullOrEmpty(bodyName))
                return null;
            for (int i = 0; i < bodies.Count; i++)
            {
                CelestialBody candidate = bodies[i];
                if (candidate != null && candidate.name == bodyName)
                    return candidate;
            }
            return null;
        }

        internal double ResolvePlaybackDistanceForEngine(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT)
        {
            Camera sceneCamera = FlightCamera.fetch?.mainCamera;
            Vector3d? cameraWorldPosition = sceneCamera != null
                ? (Vector3d?)sceneCamera.transform.position
                : null;
            Vector3d referencePosition;
            if (!ParsekFlight.TryResolvePlaybackDistanceReferencePosition(
                    ResolveCameraAnchorWorldPosition(),
                    cameraWorldPosition,
                    out referencePosition))
            {
                return double.NaN;
            }

            return ResolvePlaybackDistanceFromReferencePosition(
                index, traj, state, playbackUT, referencePosition);
        }

        /// <summary>
        /// World position the flight camera is anchored on: the watched ghost while in watch
        /// mode, otherwise the active (real) vessel. The ghost render-distance LOD radius is
        /// centered here so zooming the camera in/out does not toggle ghost FX (see
        /// <see cref="ParsekFlight.TryResolvePlaybackDistanceReferencePosition"/>). A non-finite watched-ghost
        /// anchor falls through to the active vessel; an unavailable active vessel returns null.
        /// The caller treats null or a non-finite result as "no anchor" and falls back to the
        /// camera, so the active-vessel branch is intentionally not finiteness-checked here.
        /// </summary>
        Vector3d? ResolveCameraAnchorWorldPosition()
        {
            if (host.watchMode != null
                && host.watchMode.IsWatchingGhost
                && host.watchMode.TryGetWatchedGhostAnchorWorldPosition(out Vector3d watchedAnchor)
                && ParsekFlight.IsFiniteVector3d(watchedAnchor))
            {
                return watchedAnchor;
            }

            Vessel activeVessel = FlightGlobals.ActiveVessel;
            Transform activeTransform = activeVessel != null ? activeVessel.transform : null;
            if (activeTransform != null)
                return (Vector3d)activeTransform.position;

            return null;
        }

        internal double ResolvePlaybackActiveVesselDistanceForEngine(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT)
        {
            Vector3d? activeVesselWorldPosition = FlightGlobals.ActiveVessel != null
                ? (Vector3d?)FlightGlobals.ActiveVessel.transform.position
                : null;
            if (!activeVesselWorldPosition.HasValue
                || !ParsekFlight.IsFiniteVector3d(activeVesselWorldPosition.Value))
            {
                return ResolvePlaybackDistanceForEngine(index, traj, state, playbackUT);
            }

            return ResolvePlaybackDistanceFromReferencePosition(
                index, traj, state, playbackUT, activeVesselWorldPosition.Value);
        }

        double ResolvePlaybackDistanceFromReferencePosition(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state,
            double playbackUT, Vector3d referencePosition)
        {
            Vector3d worldPos;
            if (TryResolvePlaybackWorldPosition(index, traj, state, playbackUT, out worldPos))
            {
                return Vector3d.Distance(worldPos, referencePosition);
            }

            return ResolveUnresolvedPlaybackDistanceFallback(
                traj, state, playbackUT, referencePosition);
        }

        internal static double ResolveUnresolvedPlaybackDistanceFallback(
            IPlaybackTrajectory traj,
            GhostPlaybackState state,
            double playbackUT,
            Vector3d referencePosition)
        {
            double? relativeDistance = ParsekFlight.ResolveUnresolvedRelativeSectionDistanceFallback(
                traj, playbackUT);
            if (relativeDistance.HasValue)
                return relativeDistance.Value;

            if (state != null && state.ghost != null)
            {
                return Vector3d.Distance(
                    (Vector3d)state.ghost.transform.position,
                    referencePosition);
            }

            return double.NaN;
        }
    }
}
