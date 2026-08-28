using System;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    internal partial class WatchModeController
    {
        internal static string FormatWatchDistanceForLogs(double distanceMeters)
        {
            if (double.IsNaN(distanceMeters) || double.IsInfinity(distanceMeters) || distanceMeters < 0)
                return "?";
            return distanceMeters < 1000.0
                ? distanceMeters.ToString("F0", CultureInfo.InvariantCulture) + "m"
                : (distanceMeters / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + "km";
        }

        internal static string FormatVector3ForLogs(Vector3 value)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "({0:F1},{1:F1},{2:F1})", value.x, value.y, value.z);
        }

        // Rate-limit key is the recording id so two distinct chain transfers
        // do not collide when the committed-list index slot is reused (the
        // index can shift across deletes / supersede swaps in the same session).
        internal static void LogAutoFollowDeferred(int nextIndex, string recordingId)
        {
            string key = string.IsNullOrEmpty(recordingId)
                ? $"auto-follow-deferred-idx-{nextIndex}"
                : $"auto-follow-deferred-{recordingId}";
            ParsekLog.VerboseRateLimited(
                "CameraFollow",
                key,
                $"Auto-follow target #{nextIndex} has no active ghost - deferring transfer");
        }

        internal static bool IsWithinWatchEntryRange(double distanceMeters)
        {
            return IsFiniteWatchDistance(distanceMeters)
                && distanceMeters < WatchEnterCutoffMeters;
        }

        internal static bool IsWithinWatchExitRange(double distanceMeters)
        {
            return IsFiniteWatchDistance(distanceMeters)
                && distanceMeters < WatchExitCutoffMeters;
        }

        internal static bool ShouldExitWatchForDistance(double distanceMeters)
        {
            return IsFiniteWatchDistance(distanceMeters)
                && distanceMeters >= WatchExitCutoffMeters;
        }

        internal static bool ShouldForceWatchedFullFidelityAtDistance(
            bool isWatchedGhost,
            double distanceMeters)
        {
            return isWatchedGhost
                && IsFiniteWatchDistance(distanceMeters)
                && !ShouldExitWatchForDistance(distanceMeters);
        }

        internal static double ResolveWatchCutoffDistance(
            double activeVesselDistanceMeters,
            double renderDistanceMeters,
            bool includeRenderDistance)
        {
            bool activeValid = IsFiniteWatchDistance(activeVesselDistanceMeters);
            bool renderValid = IsFiniteWatchDistance(renderDistanceMeters);

            if (!includeRenderDistance)
                return activeValid ? activeVesselDistanceMeters : renderDistanceMeters;
            if (activeValid && renderValid)
                return Math.Max(activeVesselDistanceMeters, renderDistanceMeters);
            if (activeValid)
                return activeVesselDistanceMeters;
            return renderDistanceMeters;
        }

        // Pure predicate for the cutoff debounce: returns true when the
        // watched ghost has been over the exit cutoff for enough consecutive
        // frames that we should actually exit watch mode. Counter
        // increments per cutoff-tripped frame and resets to 0 on any
        // within-range frame, so a single-frame false positive (e.g.
        // FloatingOrigin / Krakensbane frame-seam staleness during warp)
        // never reaches threshold. Real cutoff crossings exit after
        // WatchExitCutoffDebounceFrames frames (~50 ms at 60 fps).
        internal static bool ShouldExitWatchAfterCutoffDebounce(int consecutiveFrames)
        {
            return consecutiveFrames >= WatchExitCutoffDebounceFrames;
        }

        private static bool IsFiniteWatchDistance(double distanceMeters)
        {
            return !double.IsNaN(distanceMeters)
                && !double.IsInfinity(distanceMeters)
                && distanceMeters >= 0.0;
        }

        internal static bool IsFiniteVector3(Vector3 value)
        {
            return !float.IsNaN(value.x)
                && !float.IsNaN(value.y)
                && !float.IsNaN(value.z)
                && !float.IsInfinity(value.x)
                && !float.IsInfinity(value.y)
                && !float.IsInfinity(value.z);
        }

        internal static Vector3 OrbitDirectionFromAngles(float pitch, float heading)
        {
            float pitchRad = pitch * Mathf.Deg2Rad;
            float headingRad = heading * Mathf.Deg2Rad;
            return new Vector3(
                Mathf.Sin(headingRad) * Mathf.Cos(pitchRad),
                Mathf.Sin(pitchRad),
                Mathf.Cos(headingRad) * Mathf.Cos(pitchRad));
        }

        internal static string FormatRotationBasisForLogs(Quaternion rotation)
        {
            Vector3 forward = RotateVectorByQuaternion(rotation, Vector3.forward);
            Vector3 up = RotateVectorByQuaternion(rotation, Vector3.up);
            Vector3 right = RotateVectorByQuaternion(rotation, Vector3.right);
            return
                $"fwd={FormatVector3ForLogs(forward)} " +
                $"up={FormatVector3ForLogs(up)} " +
                $"right={FormatVector3ForLogs(right)}";
        }

        internal static (bool lastMapViewEnabled, bool pendingMapFocusRestore)
            InitializeMapFocusRestoreState(bool mapViewEnabled)
        {
            return (mapViewEnabled, mapViewEnabled);
        }

        internal static (bool lastMapViewEnabled, bool pendingMapFocusRestore, bool shouldAttemptRestore)
            AdvanceMapFocusRestoreState(
                bool lastMapViewEnabled,
                bool pendingMapFocusRestore,
                bool mapViewEnabled)
        {
            if (!mapViewEnabled)
                return (false, false, false);

            if (!lastMapViewEnabled)
                pendingMapFocusRestore = true;

            return (true, pendingMapFocusRestore, pendingMapFocusRestore);
        }

        internal static bool CanRestoreMapFocus(
            uint ghostPid,
            bool hasGhostVessel,
            bool hasMapObject,
            bool hasPlanetariumCamera)
        {
            return ClassifyMapFocusRestore(
                ghostPid,
                hasGhostVessel,
                hasMapObject,
                hasPlanetariumCamera) == "ready";
        }

        internal static string ClassifyMapFocusRestore(
            uint ghostPid,
            bool hasGhostVessel,
            bool hasMapObject,
            bool hasPlanetariumCamera)
        {
            if (ghostPid == 0)
                return "no-ghost-pid";
            if (!hasGhostVessel)
                return "ghost-vessel-missing";
            if (!hasMapObject)
                return "map-object-missing";
            if (!hasPlanetariumCamera)
                return "planetarium-camera-missing";
            return "ready";
        }

        internal static string BuildMapFocusRestoreDecisionMessage(
            int recordingIndex,
            uint ghostPid,
            bool hasGhostVessel,
            bool hasMapObject,
            bool hasOrbitRenderer,
            bool hasPlanetariumCamera,
            string reason)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Map focus restore decision: rec=#{0} ghostPid={1} hasGhostVessel={2} " +
                "mapObj={3} orbitRenderer={4} planetariumCamera={5} reason={6}",
                recordingIndex,
                ghostPid,
                hasGhostVessel,
                hasMapObject,
                hasOrbitRenderer,
                hasPlanetariumCamera,
                string.IsNullOrEmpty(reason) ? "(none)" : reason);
        }

        internal static string ClassifyWatchCameraInfrastructure(
            bool hasFlightCamera,
            bool hasTransform,
            bool hasParent)
        {
            if (!hasFlightCamera)
                return "flight-camera-missing";
            if (!hasTransform)
                return "camera-transform-missing";
            if (!hasParent)
                return "camera-parent-missing";
            return "ready";
        }

        /// <summary>
        /// What the per-frame watch driver must do about the current frame's target state.
        /// </summary>
        internal enum WatchTargetLossAction
        {
            /// <summary>Camera infrastructure and ghost target both resolved - drive normally.</summary>
            Continue,
            /// <summary>Target unusable this frame, but the consecutive-frame budget is not spent.</summary>
            Wait,
            /// <summary>Target unusable for the whole budget - exit watch mode cleanly.</summary>
            ExitWatch,
        }

        /// <summary>
        /// One classifier for BOTH ways a watch frame can lose its camera target: the ghost
        /// side (no state / no camera pivot) and the KSP side (no <c>FlightCamera</c>, no
        /// camera transform, no camera parent).
        ///
        /// <para>WATCH-LOOPED-PARK-TARGET-LOSS-NRE-STORM: the camera-infrastructure case used
        /// to take an UNCOUNTED early return that sat ABOVE the ghost-side safety net, so a
        /// watch session whose <c>FlightCamera</c> never came back stayed armed for the rest
        /// of the scene with nothing bound to it. `V15M-gilly-player-loop` measured exactly
        /// that: one `reason=flight-camera-missing` Warn (rate-limited, so the return was
        /// re-entered every frame) and no camera restore at teardown, across ~86 frames of
        /// stock per-frame NREs. Both causes now share this classifier and one
        /// consecutive-frame budget, so watch mode can never outlive a camera it cannot
        /// resolve.</para>
        /// </summary>
        /// <param name="consecutiveLostFrames">
        /// Frames counted INCLUDING this one (the caller increments first, as the legacy
        /// no-target net did).
        /// </param>
        internal static WatchTargetLossAction ClassifyWatchTargetLoss(
            bool cameraInfrastructureReady,
            bool hasGhostState,
            bool hasCameraPivot,
            int consecutiveLostFrames,
            int exitAfterFrames)
        {
            if (cameraInfrastructureReady && hasGhostState && hasCameraPivot)
                return WatchTargetLossAction.Continue;
            return consecutiveLostFrames >= exitAfterFrames
                ? WatchTargetLossAction.ExitWatch
                : WatchTargetLossAction.Wait;
        }

        /// <summary>
        /// True when the watch camera is currently bound to a transform belonging to the ghost
        /// the engine is about to destroy, so the caller must move the camera onto a standalone
        /// bridge anchor BEFORE the GameObject dies.
        ///
        /// <para>A destroyed <c>FlightCamera.Target</c> is the documented trigger for the stock
        /// per-frame NRE storm (<c>FlightGlobals.UpdateInformation</c>, <c>Sun</c>,
        /// <c>CrewHatchController</c>, <c>UIPartActionController</c>, <c>Vessel.Update</c>) that
        /// #895 already guards on the failed-switch path. The loop-unit cycle change
        /// (<c>GhostPlaybackEngine</c>'s "chain-loop unit cycle change" destroy) tears down the
        /// watched primary with no such guard, which is the re-arm boundary
        /// WATCH-LOOPED-PARK-TARGET-LOSS-NRE-STORM measured.</para>
        /// </summary>
        internal static bool ShouldBridgeWatchCameraOffDestroyedGhost(
            int watchedRecordingIndex,
            int destroyedRecordingIndex,
            bool hasFlightCamera,
            bool hasCameraTarget,
            bool targetBelongsToDestroyedGhost)
        {
            if (watchedRecordingIndex < 0 || destroyedRecordingIndex != watchedRecordingIndex)
                return false;
            return hasFlightCamera && hasCameraTarget && targetBelongsToDestroyedGhost;
        }

        /// <summary>
        /// The Warn a watch session emits when it gives up on an unresolvable camera target.
        /// Pure so the token a future log grep depends on (<c>cause=</c>) is pinned by a test
        /// rather than only by the per-frame driver that emits it, which no headless cell can run.
        /// </summary>
        internal static string BuildWatchTargetLossExitMessage(
            int lostFrames,
            bool cameraInfrastructureReady,
            string cameraInfrastructureReason,
            int recordingIndex,
            string recordingId,
            long cycleIndex)
        {
            string cause = cameraInfrastructureReady
                ? "ghost-target-missing"
                : (string.IsNullOrEmpty(cameraInfrastructureReason) ? "(none)" : cameraInfrastructureReason);
            return string.Format(CultureInfo.InvariantCulture,
                "No valid camera target for {0} frames (cause={1} rec=#{2} id={3} cycle={4}) — exiting watch mode",
                lostFrames,
                cause,
                recordingIndex,
                string.IsNullOrEmpty(recordingId) ? "null" : recordingId,
                cycleIndex);
        }

        /// <summary>
        /// Whether a ghost's <c>lastInterpolatedBodyName</c> is a reading the affordances may
        /// describe as a body comparison, or a stale value that says nothing about where the
        /// ghost is now.
        ///
        /// <para>WATCH-ENTRY-REFUSED-INSIDE-QUOTED-RANGE, corrected mechanism: the field is
        /// seeded ONCE at spawn (<c>GhostPlaybackEngine.CreatePendingSpawnState</c> -&gt;
        /// <c>TryResolvePendingPlaybackInterpolation</c> -&gt; <c>SetInterpolated</c>) and then
        /// refreshed only on the POSITIONING path, which the render-zone hide skips. A ghost in
        /// <see cref="RenderingZone.Beyond"/> therefore keeps answering with whatever its spawn
        /// seed resolved, however wrong and however old - V7M measured <c>Kerbin</c> held across
        /// both refusals while the observer sat at Minmus. So the honest test is not "is there a
        /// body name" (there usually is) but "is this ghost being positioned at all".</para>
        ///
        /// <para>It is ALSO the dispatch for the body term itself (see
        /// <see cref="ResolveWatchSameBodyDecision"/>): a stale reading is never the deciding
        /// evidence, it only selects the fallback order.</para>
        /// </summary>
        internal static bool IsWatchBodyReadingCurrent(string bodyName, RenderingZone zone)
        {
            return !string.IsNullOrEmpty(bodyName) && zone != RenderingZone.Beyond;
        }

        /// <summary>Which reading actually decided the watch-entry body term.</summary>
        internal enum WatchBodyEvidence
        {
            /// <summary>No ghost state at that index - nothing to answer with.</summary>
            NoState,
            /// <summary>The ghost is being positioned, so its cached reading describes it NOW.</summary>
            CacheCurrent,
            /// <summary>Cache stale/absent; the recording's own trajectory answered at the playback UT.</summary>
            TrajectoryResolved,
            /// <summary>Cache stale/absent AND the trajectory could not resolve - the cache is all there is.</summary>
            CacheFallback,
        }

        /// <summary>The body term's answer plus the evidence that produced it.</summary>
        internal struct WatchBodyDecision
        {
            internal bool OnSameBody;
            internal WatchBodyEvidence Evidence;
            /// <summary>The ghost-side body name the comparison actually used (null when none).</summary>
            internal string GhostBodyName;
        }

        /// <summary>
        /// The watch-entry SAME-BODY term, resolved from the best available evidence.
        ///
        /// <para>WATCH-ENTRY-REFUSED-INSIDE-QUOTED-RANGE, the accepted coordinated decision:
        /// the term used to read <c>lastInterpolatedBodyName</c> unconditionally, and that field
        /// is a one-time spawn seed for any ghost the render-zone hide keeps un-positioned. V7M
        /// measured a ghost seeded <c>Kerbin</c> refusing entry 144 km from an observer whose
        /// replay was at Minmus the whole time. A STALE CACHE IS NEVER THE DECIDING EVIDENCE
        /// here: when <see cref="IsWatchBodyReadingCurrent"/> says the reading is not current,
        /// the caller resolves the ghost's body positioning-free from its own trajectory at the
        /// loop-mapped playback UT and that answer decides. The cache is consulted only when the
        /// trajectory cannot resolve at all (<see cref="WatchBodyEvidence.CacheFallback"/>),
        /// which pins the pre-change behaviour for genuinely unresolvable recordings.</para>
        ///
        /// <para>The term still REFUSES a genuinely cross-body ghost - that is the E5 design
        /// intent (FloatingOrigin is centred on the active vessel, so a ghost at another body
        /// has no usable camera frame). Distance is a SEPARATE term
        /// (<see cref="WatchEnterCutoffMeters"/> = 300 km), where the float-grid step is
        /// centimetres, so a same-body in-range ghost is exactly what E5 meant to accept.</para>
        /// </summary>
        internal static WatchBodyDecision ResolveWatchSameBodyDecision(
            bool hasState,
            string cachedBodyName,
            RenderingZone zone,
            bool trajectoryResolved,
            string trajectoryBodyName,
            string activeBodyName)
        {
            if (!hasState)
            {
                return new WatchBodyDecision
                {
                    OnSameBody = false,
                    Evidence = WatchBodyEvidence.NoState,
                    GhostBodyName = null,
                };
            }

            if (IsWatchBodyReadingCurrent(cachedBodyName, zone))
                return BuildWatchBodyDecision(cachedBodyName, activeBodyName, WatchBodyEvidence.CacheCurrent);

            if (trajectoryResolved && !string.IsNullOrEmpty(trajectoryBodyName))
            {
                return BuildWatchBodyDecision(
                    trajectoryBodyName, activeBodyName, WatchBodyEvidence.TrajectoryResolved);
            }

            return BuildWatchBodyDecision(cachedBodyName, activeBodyName, WatchBodyEvidence.CacheFallback);
        }

        private static WatchBodyDecision BuildWatchBodyDecision(
            string ghostBodyName, string activeBodyName, WatchBodyEvidence evidence)
        {
            bool same = !string.IsNullOrEmpty(ghostBodyName)
                && !string.IsNullOrEmpty(activeBodyName)
                && string.Equals(ghostBodyName, activeBodyName, StringComparison.Ordinal);
            return new WatchBodyDecision
            {
                OnSameBody = same,
                Evidence = evidence,
                GhostBodyName = ghostBodyName,
            };
        }

        /// <summary>
        /// Resolves the body term and emits ONE change-keyed diagnostic line naming the evidence
        /// that decided. Change-keyed, not per-frame: the term is evaluated once per UI row per
        /// frame by several affordances, so a rate-limited line would still be the loudest thing
        /// in the log on a busy timeline.
        /// </summary>
        internal static WatchBodyDecision ResolveAndLogWatchSameBodyDecision(
            int index,
            string recordingId,
            bool hasState,
            string cachedBodyName,
            RenderingZone zone,
            bool trajectoryResolved,
            string trajectoryBodyName,
            string activeBodyName)
        {
            WatchBodyDecision decision = ResolveWatchSameBodyDecision(
                hasState, cachedBodyName, zone, trajectoryResolved, trajectoryBodyName, activeBodyName);

            string identity = string.IsNullOrEmpty(recordingId)
                ? "watch-same-body-idx-" + index.ToString(CultureInfo.InvariantCulture)
                : "watch-same-body-" + recordingId;
            string stateKey = string.Format(CultureInfo.InvariantCulture,
                "{0}|{1}|{2}|{3}",
                decision.Evidence,
                decision.OnSameBody ? "T" : "F",
                decision.GhostBodyName ?? "(null)",
                activeBodyName ?? "(null)");

            ParsekLog.VerboseOnChange("CameraFollow", identity, stateKey,
                DescribeWatchSameBodyDecision(index, recordingId, decision, cachedBodyName, zone, activeBodyName));
            return decision;
        }

        internal static string DescribeWatchSameBodyDecision(
            int index,
            string recordingId,
            WatchBodyDecision decision,
            string cachedBodyName,
            RenderingZone zone,
            string activeBodyName)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Watch same-body term: rec=#{0} id={1} sameBody={2} evidence={3} ghostBody={4} "
                + "activeBody={5} cached={6} zone={7}",
                index,
                string.IsNullOrEmpty(recordingId) ? "null" : recordingId,
                decision.OnSameBody ? "T" : "F",
                DescribeWatchBodyEvidence(decision.Evidence),
                decision.GhostBodyName ?? "(null)",
                activeBodyName ?? "(null)",
                string.IsNullOrEmpty(cachedBodyName) ? "(null)" : cachedBodyName,
                zone);
        }

        /// <summary>
        /// Grep-stable evidence tokens. These are the strings the log is read by - keep them
        /// stable, and keep them distinct from the enum spelling so a rename cannot silently
        /// re-word every archived line.
        /// </summary>
        internal static string DescribeWatchBodyEvidence(WatchBodyEvidence evidence)
        {
            switch (evidence)
            {
                case WatchBodyEvidence.CacheCurrent: return "cache-current";
                case WatchBodyEvidence.TrajectoryResolved: return "trajectory-resolved";
                case WatchBodyEvidence.CacheFallback: return "cache-fallback";
                default: return "no-state";
            }
        }

        /// <summary>
        /// Whether <c>TryStartWatchSession</c> should reset a non-overlap looping ghost's loop
        /// phase to <c>EffectiveLoopStartUT</c> on watch entry.
        ///
        /// <para>The reset exists for an observer standing near the loop START: the ghost is
        /// mid-flight far away, so restarting it at the pad next to the player is what the
        /// player asked to watch. WATCH-ENTRY-REFUSED-INSIDE-QUOTED-RANGE opened a second shape
        /// the reset is actively wrong for - an observer at an arrival park with the ghost's
        /// CURRENT phase alongside them (same body, inside the 300 km entry cutoff) and the loop
        /// START at another body tens of thousands of km away. Resetting there teleports the
        /// camera cross-body and the 305 km exit debounce auto-exits within frames: worse than
        /// refusing, with a loop-phase reset left behind as a side effect. When the current
        /// phase is itself watchable, the current phase IS the thing to watch.</para>
        ///
        /// <para>Overlap loops never reset (each cycle ghost carries its own phase), which is
        /// the pre-existing <paramref name="usesOverlapLooping"/> term - unchanged.</para>
        /// </summary>
        internal static bool ShouldResetLoopPhaseForWatch(
            bool zoneBeyond,
            bool shouldLoopPlayback,
            bool usesOverlapLooping,
            bool currentPhaseOnSameBody,
            bool currentPhaseWithinEntryRange)
        {
            if (!zoneBeyond || !shouldLoopPlayback || usesOverlapLooping)
                return false;
            if (currentPhaseOnSameBody && currentPhaseWithinEntryRange)
                return false;
            return true;
        }

        /// <summary>What the per-frame Continue path must do about a live cycle bridge anchor.</summary>
        internal enum WatchCycleBridgeDisposition
        {
            /// <summary>No anchor to resolve.</summary>
            None,
            /// <summary>Something already rebound the camera off the anchor - destroy it.</summary>
            Release,
            /// <summary>Camera is still on the anchor and a real target exists - rebind, then destroy.</summary>
            RebindThenRelease,
            /// <summary>Camera is on the anchor and there is nothing to rebind to yet - keep it alive.</summary>
            Hold,
        }

        /// <summary>
        /// The bridge anchor's release rule. "Retired as soon as something rebinds" is only
        /// self-enforcing if the frame that notices ALSO rebinds: a destroy + respawn that does
        /// not change the watched cycle index never enters <c>FindWatchedGhostState</c>'s cycle
        /// fallback, so nothing else would take the camera off the anchor and the watch would
        /// freeze on a static GameObject for the rest of the session.
        /// </summary>
        internal static WatchCycleBridgeDisposition ClassifyWatchCycleBridgeDisposition(
            bool hasBridgeAnchor, bool cameraBoundToBridge, bool replacementTargetUsable)
        {
            if (!hasBridgeAnchor)
                return WatchCycleBridgeDisposition.None;
            if (!cameraBoundToBridge)
                return WatchCycleBridgeDisposition.Release;
            return replacementTargetUsable
                ? WatchCycleBridgeDisposition.RebindThenRelease
                : WatchCycleBridgeDisposition.Hold;
        }

        /// <summary>What <see cref="FindWatchedGhostState"/> may do when the tracked loop cycle is gone.</summary>
        internal enum WatchCycleFallbackDecision
        {
            /// <summary>No usable primary - release the target and let the safety net converge.</summary>
            NoPrimary,
            /// <summary>Primary usable but the camera refused the rebind - release rather than claim a bound fallback.</summary>
            ReleaseTarget,
            /// <summary>Camera is bound to the primary - commit the fallback (adopt its cycle, log it).</summary>
            Commit,
        }

        /// <summary>
        /// The fallback used to log "Watched cycle lost - falling back to primary" and adopt the
        /// primary's cycle whether or not the camera rebind actually took: when
        /// <c>FlightCamera</c> was gone the retarget was skipped outright and the very next line
        /// read <c>actualTarget=null targetMatches=False</c>. Announcing a fallback that bound
        /// nothing is what left the session armed with no target, so the commit now requires the
        /// rebind to have succeeded.
        /// </summary>
        internal static WatchCycleFallbackDecision ClassifyWatchCycleFallback(
            bool primaryUsable, bool retargetSucceeded)
        {
            if (!primaryUsable)
                return WatchCycleFallbackDecision.NoPrimary;
            return retargetSucceeded
                ? WatchCycleFallbackDecision.Commit
                : WatchCycleFallbackDecision.ReleaseTarget;
        }

        internal static string BuildWatchCameraInfrastructureMessage(
            int recordingIndex,
            string recordingId,
            string reason,
            string vesselName = null,
            long cycleIndex = -1,
            string scene = null,
            bool hasState = false,
            bool hasGhost = false,
            bool hasCameraPivot = false)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Watch camera infrastructure unavailable: rec=#{0} id={1} vessel=\"{2}\" " +
                "cycle={3} scene={4} targetState[state={5} ghost={6} pivot={7}] reason={8}",
                recordingIndex,
                string.IsNullOrEmpty(recordingId) ? "(none)" : recordingId,
                vesselName ?? "?",
                cycleIndex,
                string.IsNullOrEmpty(scene) ? "n/a" : scene,
                hasState,
                hasGhost,
                hasCameraPivot,
                string.IsNullOrEmpty(reason) ? "(none)" : reason);
        }
    }
}
