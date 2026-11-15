using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    // Zone rendering shared by normal, background and looped ghosts. The IGhostPositioner the
    // playback engine calls is ParsekFlightGhostPositioner.
    public partial class ParsekFlight
    {
        /// <summary>
        // ZoneRenderingResult struct is now in IGhostPositioner.cs (namespace-level, shared with engine)

        /// <summary>
        /// Evaluates zone-based rendering for a ghost: computes distance, classifies zone,
        /// detects transitions, hides/shows mesh, exits watch mode if needed.
        /// Shared by normal, background, and looped ghost update paths.
        /// </summary>
        internal ZoneRenderingResult ApplyZoneRenderingImpl(int recIdx, GhostPlaybackState state, IPlaybackTrajectory rec,
            double renderDistance, double playbackUT, int protectedIndex)
        {
            double activeVesselDistance = state != null ? state.lastDistance : renderDistance;
            bool orbitTailPlayback = GhostPlaybackEngine.ShouldUseOrbitTailPlayback(rec, playbackUT);
            var zone = state != null
                ? RenderingZoneManager.ClassifyDistance(renderDistance, state.currentZone)
                : RenderingZoneManager.ClassifyDistance(renderDistance);
            var (isWatchedGhost, _, isWatchProtectedRecording) =
                ResolveZoneWatchState(recIdx, state, protectedIndex);

            // Cache render distance separately from active-vessel safety distance.
            if (state != null)
                state.lastRenderDistance = renderDistance;

            // Detect zone transition
            if (state != null)
            {
                string transDesc;
                if (GhostPlaybackLogic.DetectZoneTransition(state.currentZone, zone, out transDesc))
                {
                    RenderingZoneManager.LogZoneTransition(
                        $"#{recIdx} \"{rec.VesselName}\"", state.currentZone, zone, renderDistance);
                    state.currentZone = zone;
                }
            }

            var (shouldHideMesh, shouldSkipPartEvents, shouldSkipPositioning) =
                GhostPlaybackLogic.GetZoneRenderingPolicy(zone);

            // Ghost camera cutoff: exit watch mode when the watched ghost reaches or exceeds
            // the fixed watch cutoff distance. The cutoff applies uniformly to all ghosts.
            //
            // Debounce: requires WatchExitCutoffDebounceFrames consecutive
            // frames over the cutoff before exiting. Suppresses single-frame
            // false positives where state.lastDistance was computed across a
            // FloatingOrigin / Krakensbane frame seam during time warp (the
            // freshly-resolved ghost world position and the active vessel's
            // stale pre-shift transform.position end up in different
            // floating-origin frames and Vector3d.Distance picks up a phantom
            // hundreds-of-km gap). The counter is driven on every watched-
            // ghost frame regardless of zone hide/positioning order, so it
            // also can't get stuck rejecting in a "renderDistance is Beyond"
            // hidden-without-positioning loop. Real cutoff crossings exit
            // after the debounce window (~50 ms at 60 fps).
            bool forceWatchedFullFidelity = false;
            // Drive the cutoff debounce counter only when we're processing
            // the watched ghost. Every other ghost would register a
            // within-range sample and reset the counter, so a real cutoff
            // crossing on the watched ghost could never accumulate enough
            // consecutive samples to fire whenever any non-watched ghost
            // ran later in the same frame.
            bool cutoffTriggered;
            if (isWatchedGhost && watchMode != null)
            {
                double watchCutoffDistance = WatchModeController.ResolveWatchCutoffDistance(
                    activeVesselDistance,
                    renderDistance,
                    includeRenderDistance: orbitTailPlayback);
                bool cachedCutoffTripped =
                    WatchModeController.ShouldExitWatchForDistance(watchCutoffDistance);
                cutoffTriggered = watchMode.RegisterWatchCutoffSampleAndShouldExit(cachedCutoffTripped);
                if (cachedCutoffTripped && !cutoffTriggered)
                {
                    ParsekLog.VerboseRateLimited("Zone", $"watch-cutoff-debounce-{recIdx}",
                        $"Ghost #{recIdx} \"{rec.VesselName}\" cached cutoff tripped " +
                        $"({watchCutoffDistance.ToString("F0", CultureInfo.InvariantCulture)}m >= " +
                        $"{WatchModeController.WatchExitCutoffMeters.ToString("F0", CultureInfo.InvariantCulture)}m), " +
                        $"debounce={watchMode.WatchCutoffConsecutiveFramesForDiagnostics}/" +
                        $"{WatchModeController.WatchExitCutoffDebounceFrames} — staying in watch mode");
                }
            }
            else if (isWatchedGhost)
            {
                // No watch controller available (e.g. test stubs): fall
                // back to immediate exit on cached cutoff trip so we do
                // not silently drop real exits.
                double watchCutoffDistance = WatchModeController.ResolveWatchCutoffDistance(
                    activeVesselDistance,
                    renderDistance,
                    includeRenderDistance: orbitTailPlayback);
                cutoffTriggered =
                    WatchModeController.ShouldExitWatchForDistance(watchCutoffDistance);
            }
            else
            {
                cutoffTriggered = false;
            }
            if (isWatchedGhost || isWatchProtectedRecording)
            {
                if (cutoffTriggered)
                {
                    // Append ghost-world + active-section + anchor context so a
                    // "ghost flew off into space" cutoff is diagnosable from
                    // KSP.log alone. Common root cause is a Relative-anchor
                    // section whose live anchor is in a totally different
                    // physical location than at recording time (e.g.
                    // previously re-flown booster now in stable orbit).
                    // Without this context the user has to cross-reference
                    // prec.txt + flight state to figure out why the distance
                    // went absurd.
                    string cutoffContext = DescribeWatchCutoffContext(rec, state);
                    double watchCutoffDistance = WatchModeController.ResolveWatchCutoffDistance(
                        activeVesselDistance,
                        renderDistance,
                        includeRenderDistance: orbitTailPlayback);
                    ParsekLog.Info("Zone",
                        $"Ghost #{recIdx} \"{rec.VesselName}\" exceeded ghost camera cutoff " +
                        $"({watchCutoffDistance.ToString("F0", CultureInfo.InvariantCulture)}m cutoff distance >= " +
                        $"{WatchModeController.WatchExitCutoffMeters.ToString("F0", CultureInfo.InvariantCulture)}m; " +
                        $"active={activeVesselDistance.ToString("F0", CultureInfo.InvariantCulture)}m " +
                        $"render={renderDistance.ToString("F0", CultureInfo.InvariantCulture)}m) " +
                        $"after {WatchModeController.WatchExitCutoffDebounceFrames}-frame debounce — exiting watch mode" +
                        (string.IsNullOrEmpty(cutoffContext) ? string.Empty : " | " + cutoffContext));
                    ExitWatchModePreservingLineage();
                    // Don't return — let zone rendering continue (ghost will be hidden if Beyond)
                }
                else
                {
                    forceWatchedFullFidelity =
                        GhostPlaybackLogic.ShouldForceWatchProtectedFullFidelity(
                            isWatchProtectedRecording, orbitTailPlayback)
                        || WatchModeController.ShouldForceWatchedFullFidelityAtDistance(
                            isWatchedGhost, activeVesselDistance);
                    (shouldHideMesh, shouldSkipPartEvents, shouldSkipPositioning) =
                        GhostPlaybackLogic.ApplyWatchedFullFidelityOverride(
                            shouldHideMesh, shouldSkipPartEvents, shouldSkipPositioning,
                            forceWatchedFullFidelity);

                    if (forceWatchedFullFidelity)
                    {
                        GhostPlaybackLogic.RestoreWatchedFullFidelityState(state);
                        if (ShouldAutoActivateGhost(state)
                            && state != null && state.ghost != null && !state.ghost.activeSelf)
                            state.ghost.SetActive(true);
                        if (zone == RenderingZone.Beyond)
                        {
                            string reason = isWatchedGhost
                                ? (rec.HasOrbitSegments ? "watched orbital ghost" : "watched")
                                : "watch-protected debris";
                            ParsekLog.VerboseRateLimited("Zone", $"watch-protected-zone-exempt-{recIdx}",
                                $"Ghost #{recIdx} \"{rec.VesselName}\" beyond visual range " +
                                $"({renderDistance.ToString("F0", CultureInfo.InvariantCulture)}m render distance) but {reason} — exempt from full-fidelity LOD suppression",
                                5.0);
                        }
                    }
                }
            }

            var (distanceHideMesh, distanceSkipPartEvents, distanceSkipPositioning,
                distanceSuppressVisualFx, distanceReduceFidelity) =
                GhostPlaybackLogic.ApplyDistanceLodPolicy(
                    shouldHideMesh, shouldSkipPartEvents, shouldSkipPositioning,
                    renderDistance, forceWatchedFullFidelity, zone);
            shouldHideMesh = distanceHideMesh;
            shouldSkipPartEvents = distanceSkipPartEvents;
            shouldSkipPositioning = distanceSkipPositioning;

            // #171: During warp, exempt orbital ghosts from zone hiding — they travel far
            // from the player and would complete playback while invisible.
            if (GhostPlaybackLogic.ShouldApplyWarpZoneHideExemption(
                    shouldHideMesh,
                    zone,
                    GetCurrentWarpRateSafe(),
                    rec.HasOrbitSegments
                        && GhostPlaybackLogic.ShouldAllowWarpZoneHideExemption(
                            isWatchProtectedRecording, orbitTailPlayback)))
            {
                shouldHideMesh = false;
                ParsekLog.VerboseRateLimited("Zone", $"warp-zone-exempt-{recIdx}",
                    $"Ghost #{recIdx} \"{rec.VesselName}\" beyond visual range " +
                    $"({renderDistance.ToString("F0", CultureInfo.InvariantCulture)}m render distance) " +
                    $"but exempt during warp (orbital ghost)", 5.0);
            }

            if (shouldHideMesh)
            {
                if (state != null && state.ghost != null && state.ghost.activeSelf)
                {
                    state.ghost.SetActive(false);
                    ParsekLog.VerboseRateLimited("Zone", "zone-hide",
                        $"Ghost #{recIdx} \"{rec.VesselName}\" hidden by distance LOD " +
                        $"({renderDistance.ToString("F0", CultureInfo.InvariantCulture)}m render distance)");
                }
                return new ZoneRenderingResult
                {
                    hiddenByZone = true,
                    skipPartEvents = true,
                    suppressVisualFx = true,
                    reduceFidelity = false
                };
            }

            // Zone 1 or 2: ensure ghost is visible
            if (ShouldAutoActivateGhost(state)
                && state != null && state.ghost != null && !state.ghost.activeSelf)
            {
                state.ghost.SetActive(true);
                ParsekLog.VerboseRateLimited("Zone", "zone-show",
                    $"Ghost #{recIdx} \"{rec.VesselName}\" re-shown: entered visible distance tier " +
                    $"({renderDistance.ToString("F0", CultureInfo.InvariantCulture)}m render distance)");
            }

            return new ZoneRenderingResult
            {
                hiddenByZone = false,
                skipPartEvents = shouldSkipPartEvents,
                suppressVisualFx = distanceSuppressVisualFx,
                reduceFidelity = distanceReduceFidelity
            };
        }

        /// <summary>
        /// Contract for the `allowActivation` parameter threaded through
        /// IGhostPositioner / ParsekFlight positioning methods (bug #258 / #375):
        ///
        /// - `true` = positioner may call <c>ghost.SetActive(true)</c> after
        ///   world-space placement. Default for routine playback frames.
        /// - `false` = positioner leaves visibility untouched. Only use when a
        ///   downstream step (e.g. <c>ActivateGhostVisualsIfNeeded</c> during loop
        ///   sync or deferred-FF handoff) owns the SetActive decision and must
        ///   run AFTER world-space positioning so the first visible frame is
        ///   already aligned with playback.
        ///
        /// Every new positioning path must decide explicitly. Forgetting to thread
        /// the flag (and defaulting to <c>true</c> blind) can re-introduce the
        /// "first-visible-frame shows stale initial pose" regression that #258
        /// fixed. <c>ShouldAutoActivateGhost</c> is the canonical source for the
        /// value.
        ///
        /// </summary>
        internal static bool ShouldAutoActivateGhost(GhostPlaybackState state)
        {
            if (state == null) return true;
            if (state.deferVisibilityUntilPlaybackSync) return false;
            return true;
        }

        /// <summary>
        /// Diagnostic context appended to the watch-cutoff log line: ghost
        /// world position plus the active section under the current game UT.
        /// Recorded Relative sections are identified by anchor recording ID;
        /// non-loop playback must not enrich this diagnostic from live vessel
        /// PID lookups.
        /// </summary>
        internal static string DescribeWatchCutoffContext(
            IPlaybackTrajectory rec, GhostPlaybackState state)
        {
            if (state == null || state.ghost == null)
                return string.Empty;

            var ic = CultureInfo.InvariantCulture;
            Vector3d ghostWorld = state.ghost.transform.position;
            string ghostWorldStr = $"ghostWorld={FormatVector3d(ghostWorld)}";

            if (rec?.TrackSections == null || rec.TrackSections.Count == 0)
                return ghostWorldStr;

            // Probe the section under the current game UT for relative-anchor
            // context. If the trajectory is using a different playback UT
            // (loops, overlap shift), this lookup may pick a neighbouring
            // section, which is fine — the goal is "give the user a
            // starting point for diagnosing", not perfect attribution.
            double currentUT;
            try
            {
                currentUT = Planetarium.GetUniversalTime();
            }
            catch (System.Exception ex)
            {
                // Defensive — Planetarium is alive in flight scenes but the
                // catch keeps the cutoff log path noise-free if we ever get
                // called from an off-nominal scene. Surface the skip so the
                // next surprise is debuggable.
                ParsekLog.VerboseRateLimited("Zone", "watch-cutoff-context-skip",
                    $"DescribeWatchCutoffContext: Planetarium.GetUniversalTime threw {ex.GetType().Name} — skipping section/anchor context");
                return ghostWorldStr;
            }

            int sectionIdx = TrajectoryMath.FindTrackSectionForUT(rec.TrackSections, currentUT);
            if (sectionIdx < 0 || sectionIdx >= rec.TrackSections.Count)
                return ghostWorldStr + " section=none";

            TrackSection section = rec.TrackSections[sectionIdx];
            string sectionStr =
                $"section={section.referenceFrame} " +
                $"sectionUT=[{section.startUT.ToString("F1", ic)}," +
                $"{section.endUT.ToString("F1", ic)}]";

            if (section.referenceFrame != ReferenceFrame.Relative)
                return $"{ghostWorldStr} {sectionStr}";

            string anchorRec = string.IsNullOrWhiteSpace(section.anchorRecordingId)
                ? "anchorRec=missing"
                : $"anchorRec={section.anchorRecordingId.Trim()}";
            return $"{ghostWorldStr} {sectionStr} {anchorRec}";
        }

        internal (bool isWatchedGhost, int watchProtectionIndex, bool isWatchProtectedRecording)
            ResolveZoneWatchState(int recIdx, GhostPlaybackState state, int protectedIndex)
        {
            bool isWatchedGhost = watchMode != null
                ? watchMode.IsWatchedGhostState(recIdx, state)
                : GhostPlaybackLogic.IsProtectedGhost(protectedIndex, recIdx);
            int watchProtectionIndex = watchMode != null
                ? watchMode.WatchProtectionRecordingIndex
                : protectedIndex;
            bool isWatchProtectedRecording = GhostPlaybackLogic.IsWatchProtectedRecording(
                RecordingStore.CommittedRecordings, RecordingStore.CommittedTrees, watchProtectionIndex, recIdx);
            return (isWatchedGhost, watchProtectionIndex, isWatchProtectedRecording);
        }

        internal static bool BodyFixedPrimaryCoversPlaybackUT(
            TrackSection section,
            double playbackUT,
            out double firstBodyFixedUT,
            out double lastBodyFixedUT)
        {
            firstBodyFixedUT = double.NaN;
            lastBodyFixedUT = double.NaN;
            if (section.referenceFrame != ReferenceFrame.Relative
                || section.bodyFixedFrames == null
                || section.bodyFixedFrames.Count < 2)
            {
                return false;
            }

            int lastIdx = section.bodyFixedFrames.Count - 1;
            firstBodyFixedUT = section.bodyFixedFrames[0].ut;
            lastBodyFixedUT = section.bodyFixedFrames[lastIdx].ut;

            // Reject playback UTs outside the body-fixed primary's covered range.
            // Without this guard InterpolateAndPosition / TrajectoryMath.InterpolatePoints
            // would clamp at the nearest endpoint and silently freeze stale debris.
            const double coverageEpsilon = 1e-6;
            return playbackUT + coverageEpsilon >= firstBodyFixedUT
                && playbackUT - coverageEpsilon <= lastBodyFixedUT;
        }

        /// <summary>
        /// Pure decision helper: given whether a <see cref="CelestialBody"/>
        /// resolved for the ghost's body name and whether an active vessel is
        /// present, returns the reason the fallback fires (for logging), or
        /// <c>null</c> when both inputs are live and the distance-aware path
        /// can run. Extracted for unit testing without KSP singletons (#373).
        /// </summary>
        internal static string ResolveImmediateLandedGhostClearanceFallbackReason(
            bool hasBody, bool hasActiveVessel)
        {
            if (!hasBody && !hasActiveVessel) return "no-body-and-no-active-vessel";
            if (!hasBody) return "no-body";
            if (!hasActiveVessel) return "no-active-vessel";
            return null;
        }

        internal static string ResolveExplosionAnchorBodyName(
            string lastInterpolatedBodyName, IPlaybackTrajectory traj)
        {
            if (!string.IsNullOrEmpty(lastInterpolatedBodyName))
                return lastInterpolatedBodyName;
            return RecordingEndpointResolver.TryGetPreferredEndpointBodyName(
                traj, out string bodyName)
                ? bodyName
                : null;
        }

        internal static double ResolveNaNFallbackLandedGhostClearanceMeters(double minClearanceMeters)
        {
            return System.Math.Max(
                VesselSpawner.LandedGhostClearanceMeters,
                System.Math.Max(0.5, minClearanceMeters));
        }

        internal static bool ShouldApplyImmediateSurfacePositionClearance(
            TerminalState? terminalState,
            double recordedTerrainHeight,
            bool hasSurfacePosition)
        {
            return ShouldApplyImmediatePointSurfaceClearance(terminalState, hasSurfacePosition)
                || !double.IsNaN(recordedTerrainHeight);
        }

        internal static bool ShouldApplyImmediatePointSurfaceClearance(
            TerminalState? terminalState,
            bool hasSurfacePosition)
        {
            if (terminalState == TerminalState.Landed
                || terminalState == TerminalState.Splashed)
                return true;

            // A Recovered recording can still be a surface terminal recording
            // when Parsek captured the landed/splashed terminal pose before KSP
            // removed the vessel. Treat only those recovered recordings as
            // surface ghosts; orbit/flight recoveries do not get a terrain clamp.
            return terminalState == TerminalState.Recovered && hasSurfacePosition;
        }

        /// <summary>
        /// Position a surface-terminal ghost. The recorded altitude is the TRUTH —
        /// the vessel was literally at that world-space position when the terminal
        /// state was determined. For vessels recorded on mesh objects (Island
        /// Airfield, launchpad, KSC buildings), the altitude encodes the airfield
        /// surface height, which <c>body.TerrainAltitude()</c> cannot see (PQS-only).
        /// The old code applied terrain-relative correction via
        /// <c>ComputeCorrectedAltitude</c> which pulled mesh-object ghosts down to
        /// raw PQS terrain, burying them under the runway.
        ///
        /// <para>New behavior: preserve the recorded altitude. The only correction
        /// is an underground safety floor — push up if the recorded altitude is
        /// below the current PQS terrain floor. The caller supplies the minimum
        /// clearance to use for the current playback context; long-range watched
        /// ghosts use the same distance-aware floor as <c>ClampGhostsToTerrain</c>.
        /// For NaN-recorded-terrain legacy recordings we keep the historical 4 m
        /// minimum clearance above PQS, but allow callers to request a larger
        /// distance-aware floor at extreme watch ranges.</para>
        ///
        /// <para>Ghosts are kinematic — no physics damage concern — so the safety
        /// floor defaults to 0.5 m when the caller has no better context.
        /// <c>internal static</c> so in-game tests can exercise it without
        /// needing a full <c>IGhostPositioner</c> chain.</para>
        /// </summary>
        internal static TrajectoryPoint ApplyLandedGhostClearance(
            TrajectoryPoint p, int index, string vesselName, double recordedTerrainHeight,
            double minClearanceMeters = 0.5)
        {
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == p.bodyName);
            if (body == null)
                return p;
            if (body.pqsController == null)
            {
                ParsekLog.WarnRateLimited("TerrainCorrect",
                    $"pqs-unready-{body.name}",
                    $"Landed ghost clamp #{index} (\"{vesselName}\"): PQS not spun up " +
                    $"for body '{body.name}' — leaving recorded alt unchanged");
                return p;
            }

            var ic = System.Globalization.CultureInfo.InvariantCulture;
            double pqsTerrain = body.TerrainAltitude(p.latitude, p.longitude, true);

            double floorClearance = System.Math.Max(0.5, minClearanceMeters);

            // Legacy (NaN) path: no recorded surface height — keep the historical
            // 4 m fallback floor, but honor a larger caller-requested clearance so
            // long-range watched ghosts do not clip near the edge of visual range.
            if (double.IsNaN(recordedTerrainHeight))
            {
                double legacyClearance = ResolveNaNFallbackLandedGhostClearanceMeters(minClearanceMeters);
                double legacyTarget = pqsTerrain + legacyClearance;
                if (p.altitude >= legacyTarget)
                    return p;
                // delta is always > 0 here because we already verified p.altitude < legacyTarget.
                double delta = legacyTarget - p.altitude;
                ParsekLog.VerboseRateLimited("TerrainCorrect", $"landed-ghost-{index}",
                    $"Landed ghost clamp #{index} (\"{vesselName}\"): " +
                    $"alt={p.altitude.ToString("F1", ic)} pqsTerrain={pqsTerrain.ToString("F1", ic)} " +
                    $"-> {legacyTarget.ToString("F1", ic)} (delta=+{delta.ToString("F2", ic)}m, " +
                    $"body={body.name}, NaN fallback, clearance={legacyClearance.ToString("F1", ic)}m)");
                p.altitude = legacyTarget;
                return p;
            }

            // Primary path: trust the recorded altitude. Only push up if the
            // recorded altitude is below the current PQS terrain floor — which
            // only happens when PQS terrain has shifted UP since recording.
            double safetyFloor = pqsTerrain + floorClearance;
            if (p.altitude >= safetyFloor)
                return p;

            double upDelta = safetyFloor - p.altitude;
            ParsekLog.VerboseRateLimited("TerrainCorrect", $"landed-ghost-{index}",
                $"Landed ghost clamp #{index} (\"{vesselName}\"): " +
                $"alt={p.altitude.ToString("F1", ic)} pqsTerrain={pqsTerrain.ToString("F1", ic)} " +
                $"-> {safetyFloor.ToString("F1", ic)} (delta=+{upDelta.ToString("F2", ic)}m, " +
                $"body={body.name}, below-pqs-floor, clearance={floorClearance.ToString("F1", ic)}m, " +
                $"recSurface={recordedTerrainHeight.ToString("F1", ic)})");
            p.altitude = safetyFloor;
            return p;
        }
    }
}
