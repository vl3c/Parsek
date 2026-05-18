using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Gated render-path observability for ghost placement. Detailed rows open
    /// around first appearance, structural-event
    /// windows, section changes, large transform deltas, and retire/hide guard
    /// paths only when the diagnostics setting is enabled.
    /// </summary>
    internal static class GhostRenderTrace
    {
        internal const double InitialWindowSeconds = 4.0;
        internal const double SectionChangeWindowSeconds = 2.0;
        internal const double AnomalyWindowSeconds = 5.0;
        internal const double ActivationTransitionWindowSeconds = 1.0;
        internal const double LargePoseDeltaMeters = 25.0;
        internal const double VelocityDeltaMultiplier = 4.0;
        internal const double VelocityDeltaSlackMeters = 25.0;

        // The expected-delta-vs-actual-delta detector assumes the recording
        // carries a per-point velocity field that approximates the ghost's
        // motion between frames. Two classes of frame violate that assumption:
        //
        // 1. Frames with a zero rendering velocity. The detector computes
        //    `expectedDM = lastInterpolatedVelocity.magnitude * dt`, so a zero
        //    velocity forces `expectedDM = 0` and every non-trivial position
        //    delta trips `reason=large-delta`. Several legitimate playback
        //    paths leave `GhostPlaybackState.lastInterpolatedVelocity` at zero
        //    (default(Vector3)) on purpose:
        //      - Orbital playback (`mode=Orbit` in the UpdatePath emit). Orbit
        //        segments are Kepler-element summaries with no per-point
        //        velocity sample; Kepler propagation still moves the ghost at
        //        orbital speed (~1400 m/s for low Kerbin orbit).
        //      - Surface-held / landed positioning paths in `ParsekFlight`
        //        and the loop-pause / endpoint / fallback branches in
        //        `GhostPlaybackEngine` that build `InterpolationResult` with
        //        `Vector3.zero` velocity by construction.
        //    On those paths the detector's expected-vs-actual comparison has
        //    no signal to work with, so suppress it. Note this is broader
        //    than orbital playback alone: a genuine teleport on a stationary
        //    landed ghost (rare, but e.g. a terrain-clamp re-snap) will be
        //    swallowed too. The detector is diagnostic only; if a real
        //    surface-hold teleport needs investigating in the future, prefer
        //    plumbing a positive `playback-path` token through here over
        //    re-tightening this heuristic.
        // 2. Floating-origin shift frames. When stock KSP rebases world
        //    coordinates via `FloatingOrigin.setOffset`, every ghost in the
        //    scene shifts by the same magnitude on the same frame. The
        //    detector reads pre-shift and post-shift positions in adjacent
        //    `EmitPostUpdate` passes and counts the rebase as the FO
        //    magnitude in metres of motion. Detected by comparing the
        //    current Unity `Time.frameCount` against the most recent
        //    `ReFlySettleStabilityTracker.LastFloatingOriginShiftFrame`.
        //
        // Both shapes appear in `logs/2026-05-18_1953_pr889-rotation-trace/`
        // (1296 orbital false positives + 13 FO false positives across 1309
        // total `reason=large-delta` events, zero genuine anomalies). True
        // teleports with non-zero velocity off the FO frame still flag
        // correctly, confirmed by the 34km orbit-mode transition jump in
        // `logs/2026-05-18_2023_kerbalx-debris-instability-and-probe-dup/`.
        internal const float ZeroPlaybackVelocityEpsilonSqrMetresPerSecond = 1e-6f;
        internal const int FloatingOriginSuppressionFrameWindow = 1;

        private struct TraceState
        {
            public bool initialized;
            public double firstSeenUT;
            public int lastSectionIndex;
            public bool hasSectionIndex;
            public bool hasLastRenderedPose;
            public Vector3 lastRenderedPosition;
            public double lastPlaybackUT;
            // Last position observed while the deferred ghost was activation-
            // hidden. Read on the first-visible transition to compute the
            // hidden-pose delta so investigators can attribute slides to the
            // catch-up jump versus downstream artifacts. Default-init zero is
            // not meaningful; gate every read on hasLastHiddenPosition.
            public bool hasLastHiddenPosition;
            public Vector3 lastHiddenPosition;
            public int lastHiddenPositionFrame;
            // Sticky "we have already seen a hidden activation frame" flag so
            // that the first non-hidden EmitActivationDecision call can emit
            // transition=first-visible exactly once. Cleared on Reset() with
            // the rest of TraceState; survives across frames within a single
            // activation lifecycle.
            public bool wasHiddenLastActivationDecision;
        }

        internal struct GateDecision
        {
            public bool Emit;
            public bool Important;
            public string Reason;
        }

        /// <summary>
        /// The actual rendering surface used to position the ghost on a
        /// frame. Surfaces every frame's path in the same log line so a
        /// post-hoc grep can attribute behaviour to one surface without
        /// stitching together state flags. Used in the <c>AfterUpdate</c>
        /// trace line emitted by <see cref="EmitPostUpdate"/>.
        /// </summary>
        internal enum RenderSurface : byte
        {
            /// <summary>
            /// Default — caller did not specify; surface is unknown to the
            /// trace. Logged as "unknown".
            /// </summary>
            Unknown = 0,

            /// <summary>
            /// Standard positioner chain (relative-offset reconstruction,
            /// flat-points, surface, or orbit-tail). Default for most
            /// recordings outside the parent-anchored debris path.
            /// </summary>
            Legacy = 1,

            /// <summary>
            /// Recording's <c>bodyFixedFrames</c> body-fixed primary lerp.
            /// Used for parent-anchored debris (v13+) whenever the loop-chain
            /// relative surface is not deliberately selected.
            /// </summary>
            BodyFixedPrimary = 2,

            /// <summary>
            /// Mesh inactive — ghost retired or hidden by the parent-anchored
            /// coverage / tumbling-parent fallback paths.
            /// </summary>
            Hidden = 3,
        }

        private static string RenderSurfaceToken(RenderSurface surface)
        {
            switch (surface)
            {
                case RenderSurface.Legacy: return "legacy";
                case RenderSurface.BodyFixedPrimary: return "body-fixed-primary";
                case RenderSurface.Hidden: return "hidden";
                default: return "unknown";
            }
        }

        private struct SectionContext
        {
            public int Index;
            public ReferenceFrame Frame;
            public SegmentEnvironment Environment;
            public TrackSectionSource Source;
            public double StartUT;
            public double EndUT;
            public int FrameCount;
            public int bodyFixedFrameCount;
            public int CheckpointCount;
            public string AnchorRecordingId;
            public float BoundaryDiscontinuityMeters;
            public bool HasSection;
            // Debris-frame bracket span at the current playback UT: the time
            // gap between the two recorded `frames` entries that contain
            // playbackUT. NaN when not Relative or fewer than two frames are
            // available. This surfaces the wide-bracket pattern that format v13
            // avoids for parent-anchored debris by rendering body-fixed primary
            // frames first.
            public double DebrisBracketSeconds;
        }

        private static readonly Dictionary<string, TraceState> states =
            new Dictionary<string, TraceState>(StringComparer.Ordinal);

        private static readonly Dictionary<string, double> detailedUntilByRecording =
            new Dictionary<string, double>(StringComparer.Ordinal);

        internal static bool ForceEnabledForTesting;

        /// <summary>
        /// Test seam for the ambient Unity frame counter. Production reads
        /// <c>Time.frameCount</c>; xUnit cannot call into Unity natives so
        /// tests override this to a deterministic value. Reset to <c>null</c>
        /// in test teardown.
        /// </summary>
        internal static System.Func<int> FrameCounterOverrideForTesting;

        internal static void Reset()
        {
            states.Clear();
            detailedUntilByRecording.Clear();
        }

        private static int CurrentFrameCount()
        {
            var ovr = FrameCounterOverrideForTesting;
            if (ovr != null)
                return ovr();
            return UnityFrameCount();
        }

        // Isolated in its own method so xUnit JIT verification of
        // CurrentFrameCount does not have to walk into a Unity ECall site.
        // Test runs always go through the override above; this method is only
        // ever JIT-compiled when the override is null, which only happens
        // inside the live KSP runtime where the ECall is legal.
        private static int UnityFrameCount()
        {
            return Time.frameCount;
        }

        // Same isolation pattern for Transform.position / Transform.rotation:
        // these are Unity ECalls and the JIT verifier in the xUnit runtime
        // rejects any method whose IL references them, even on unreachable
        // branches. Tests pass playbackState=null, so these helpers are never
        // invoked there; production calls them only when state.ghost is
        // non-null, where the ECall is legal.
        private static Vector3 ReadGhostPosition(GhostPlaybackState playbackState)
        {
            return playbackState.ghost.transform.position;
        }

        private static Quaternion ReadGhostRotation(GhostPlaybackState playbackState)
        {
            return playbackState.ghost.transform.rotation;
        }

        private static bool IsGhostActiveSelf(GhostPlaybackState playbackState)
        {
            return playbackState.ghost.activeSelf;
        }

        internal static void OpenDetailedWindow(
            string recordingId, double currentUT, double seconds, string reason)
        {
            if (!IsEnabled)
                return;
            if (string.IsNullOrEmpty(recordingId))
                return;
            if (double.IsNaN(currentUT) || double.IsInfinity(currentUT))
                return;

            double until = currentUT + Math.Max(0.0, seconds);
            double existing;
            if (!detailedUntilByRecording.TryGetValue(recordingId, out existing)
                || until > existing)
            {
                detailedUntilByRecording[recordingId] = until;
            }
        }

        internal static void BeginFrame(
            IPlaybackTrajectory trajectory,
            int ghostIndex,
            double currentUT,
            double playbackUT,
            string path)
        {
            if (!IsEnabled)
                return;
            if (trajectory == null || string.IsNullOrEmpty(trajectory.RecordingId))
                return;

            string recordingId = trajectory.RecordingId;
            string key = BuildStateKey(recordingId, ghostIndex);
            TraceState state;
            bool hadState = states.TryGetValue(key, out state);
            bool firstSeen = !hadState || !state.initialized;
            if (firstSeen)
            {
                state.initialized = true;
                state.firstSeenUT = currentUT;
                OpenDetailedWindow(recordingId, currentUT, InitialWindowSeconds, "first-seen");
            }

            SectionContext section = ResolveSection(trajectory, playbackUT);
            bool sectionChanged = state.hasSectionIndex && section.Index != state.lastSectionIndex;
            if (sectionChanged)
                OpenDetailedWindow(recordingId, currentUT, SectionChangeWindowSeconds, "section-change");

            bool structuralWindow = PlaybackTrace.IsInPostStructuralEventWindow(trajectory, currentUT);
            if (structuralWindow)
                OpenDetailedWindow(recordingId, currentUT, PlaybackTrace.PostEventWindowSeconds, "structural-event");

            GateDecision gate = EvaluateGateForTesting(
                currentUT,
                firstSeen ? currentUT : state.firstSeenUT,
                firstSeen,
                structuralWindow,
                sectionChanged,
                force: false,
                resolverMissOrRetired: false,
                reFlyWindow: IsDetailedWindowOpen(recordingId, currentUT),
                deltaMeters: 0.0,
                expectedDeltaMeters: 0.0);

            if (gate.Emit)
            {
                EmitRaw(
                    gate.Important,
                    recordingId,
                    ghostIndex,
                    currentUT,
                    playbackUT,
                    "FrameStart",
                    "path=" + Token(path)
                    + " reason=" + Token(gate.Reason)
                    + " " + FormatSection(section));
            }

            state.lastSectionIndex = section.Index;
            state.hasSectionIndex = true;
            states[key] = state;
        }

        internal static void EmitPostUpdate(
            IPlaybackTrajectory trajectory,
            int ghostIndex,
            double currentUT,
            double playbackUT,
            GhostPlaybackState playbackState,
            string path,
            bool retired,
            RenderSurface surface = RenderSurface.Unknown,
            double rawPlaybackUT = double.NaN,
            double activationStartUT = double.NaN)
        {
            if (!IsEnabled)
                return;
            if (trajectory == null || string.IsNullOrEmpty(trajectory.RecordingId))
                return;

            string recordingId = trajectory.RecordingId;
            string key = BuildStateKey(recordingId, ghostIndex);
            TraceState state;
            states.TryGetValue(key, out state);

            bool hasGhost = playbackState?.ghost != null;
            Vector3 position = hasGhost ? ReadGhostPosition(playbackState) : default(Vector3);
            Quaternion rotation = hasGhost ? ReadGhostRotation(playbackState) : default(Quaternion);

            double deltaMeters = state.hasLastRenderedPose
                ? Vector3.Distance(state.lastRenderedPosition, position)
                : 0.0;
            double expectedDeltaMeters = 0.0;
            if (state.hasLastRenderedPose && playbackState != null)
            {
                double playbackDt = Math.Abs(playbackUT - state.lastPlaybackUT);
                expectedDeltaMeters = playbackState.lastInterpolatedVelocity.magnitude * playbackDt;
            }

            Vector3 lastVelocity = playbackState != null
                ? playbackState.lastInterpolatedVelocity
                : default(Vector3);
            bool largeDeltaSuppressed = IsLargeDeltaSignalSuppressed(
                lastVelocity,
                CurrentFrameCount(),
                LastFloatingOriginShiftFrame());
            bool anomaly = !largeDeltaSuppressed
                && IsLargePoseDelta(deltaMeters, expectedDeltaMeters);
            if (anomaly)
                OpenDetailedWindow(recordingId, currentUT, AnomalyWindowSeconds, "large-delta");

            GateDecision gate = EvaluateGateForTesting(
                currentUT,
                state.initialized ? state.firstSeenUT : currentUT,
                firstSeen: !state.initialized,
                structuralWindow: PlaybackTrace.IsInPostStructuralEventWindow(trajectory, currentUT),
                sectionChanged: false,
                force: false,
                resolverMissOrRetired: retired,
                reFlyWindow: IsDetailedWindowOpen(recordingId, currentUT),
                deltaMeters: deltaMeters,
                expectedDeltaMeters: expectedDeltaMeters,
                largeDeltaSuppressed: largeDeltaSuppressed);

            if (gate.Emit)
            {
                // rawPlaybackUT defaults to NaN from callers that have not yet
                // migrated to the new signature; in that case treat raw == visible
                // and emit clampFired=false. Migrated callers (RenderInRangeGhost
                // non-loop passes ctx.currentUT; loop sites pass loopUT) drive
                // the actual clamp attribution. Loop paths never call
                // ResolveVisiblePlaybackUT today, so loopUT == playbackUT and
                // clampFired stays false there as well — verified by source walk
                // (only :999, :1138, :5094 invoke ResolveVisiblePlaybackUT).
                bool hasRawUT = !double.IsNaN(rawPlaybackUT);
                double effectiveRawUT = hasRawUT ? rawPlaybackUT : playbackUT;
                bool clampFired = hasRawUT
                    && Math.Abs(effectiveRawUT - playbackUT) > 1e-9;
                double visibleLead = double.IsNaN(activationStartUT)
                    ? double.NaN
                    : playbackUT - activationStartUT;

                EmitRaw(
                    gate.Important,
                    recordingId,
                    ghostIndex,
                    currentUT,
                    playbackUT,
                    "AfterUpdate",
                    "path=" + Token(path)
                    + " reason=" + Token(gate.Reason)
                    + " retired=" + Bool(retired)
                    + " active=" + Bool(hasGhost && IsGhostActiveSelf(playbackState))
                    + " surface=" + RenderSurfaceToken(surface)
                    + " pos=" + FormatVector3(position)
                    + " rot=" + FormatQuaternion(rotation)
                    + " dM=" + FormatDouble(deltaMeters, "F2")
                    + " expectedDM=" + FormatDouble(expectedDeltaMeters, "F2")
                    + " velocity=" + FormatVector3(playbackState != null
                        ? playbackState.lastInterpolatedVelocity
                        : default(Vector3))
                    + " body=" + Token(playbackState?.lastInterpolatedBodyName)
                    + " alt=" + FormatDouble(playbackState != null
                        ? playbackState.lastInterpolatedAltitude
                        : double.NaN, "F2")
                    + " rawPlaybackUT=" + FormatDouble(effectiveRawUT, "F3")
                    + " visibleLead=" + FormatDouble(visibleLead, "F3")
                    + " clampFired=" + Bool(clampFired));
            }

            state.initialized = true;
            if (double.IsNaN(state.firstSeenUT) || state.firstSeenUT == 0.0)
                state.firstSeenUT = currentUT;
            state.hasLastRenderedPose = hasGhost;
            state.lastRenderedPosition = position;
            state.lastPlaybackUT = playbackUT;
            states[key] = state;
        }

        /// <summary>
        /// Structured activation-decision emit for deferred ghosts. Called from
        /// every engine path that runs the activation hide/activate split:
        /// <c>RenderInRangeGhost</c> (non-loop) and
        /// <c>SynchronizeLoadedGhostForWatch</c> (watch resume). Logs the
        /// decision (hidden vs visible), the reason from
        /// <c>ShouldHoldInitialActivationHiddenThisFrame</c>, the activation lead
        /// against both raw and visible playback UTs, the clamp state, the
        /// frames-remaining counter, and the transition flag. On the
        /// first-visible transition, opens an <c>activation-transition</c>
        /// detailed window of <see cref="ActivationTransitionWindowSeconds"/>
        /// so subsequent <c>AfterUpdate</c> / <c>LateUpdate</c> rows stay
        /// ungated even when the <c>first-seen</c> window has expired (e.g.
        /// for warp-end deferred spawns or watch-resume activations late in a
        /// session).
        ///
        /// <para>Carve-out: callers MUST skip this emit on retired frames.
        /// <c>RenderInRangeGhost</c>'s retired short-circuit at
        /// <c>GhostPlaybackEngine.cs:1231-1236</c> bypasses the activation
        /// branch entirely — emitting an activation decision there would lie
        /// about a decision that did not run.</para>
        ///
        /// <para>FX-flag-agnostic: this emit logs the activation decision, not
        /// the FX decisions downstream of it. Watch-sync and the non-loop
        /// hidden branch take different <c>skipPartEvents</c> paths; that
        /// asymmetry is captured in subsequent log lines (e.g. engine FX logs),
        /// not here.</para>
        /// </summary>
        internal static void EmitActivationDecision(
            IPlaybackTrajectory trajectory,
            int ghostIndex,
            double currentUT,
            double rawPlaybackUT,
            double visiblePlaybackUT,
            double activationStartUT,
            int framesRemaining,
            bool hidden,
            string hideReason,
            string callSite,
            Vector3 currentPosition,
            bool hasCurrentPosition)
        {
            if (!IsEnabled)
                return;
            if (trajectory == null || string.IsNullOrEmpty(trajectory.RecordingId))
                return;

            string recordingId = trajectory.RecordingId;
            string key = BuildStateKey(recordingId, ghostIndex);
            TraceState state;
            states.TryGetValue(key, out state);

            // Transition flag derived from the per-state sticky
            // wasHiddenLastActivationDecision: hidden→visible on a state that
            // has previously emitted at least one hidden decision is the
            // first-visible transition. A visible decision with no prior
            // hidden frames means the ghost activated immediately (no hide
            // ever held it back) — emit transition=visible without firing the
            // window-open path.
            string transition;
            if (hidden)
                transition = "hidden";
            else if (state.wasHiddenLastActivationDecision)
                transition = "first-visible";
            else
                transition = "visible";

            // Steady visible state — every non-loop non-retired render frame
            // would otherwise emit one ActivationDecision row for the rest of
            // the ghost's lifetime, which floods KSP.log when ghost render
            // tracing is enabled. The activation flow is fully described by
            // the hidden frames + the first-visible transition; subsequent
            // visible frames carry no new activation-decision information.
            // Early-return preserves all hidden / first-visible logging while
            // dropping the steady-state noise. State updates below are also
            // skipped: wasHiddenLastActivationDecision was already set to
            // false on the first-visible emit, so the state stays correct to
            // observe a future re-hide (loop wrap, scene reload) without
            // further bookkeeping here.
            if (transition == "visible")
                return;

            bool clampFired = Math.Abs(rawPlaybackUT - visiblePlaybackUT) > 1e-9;
            double activationLead = double.IsNaN(activationStartUT)
                ? double.NaN
                : rawPlaybackUT - activationStartUT;
            double visibleLead = double.IsNaN(activationStartUT)
                ? double.NaN
                : visiblePlaybackUT - activationStartUT;

            double hiddenPoseDelta = double.NaN;
            if (transition == "first-visible"
                && state.hasLastHiddenPosition
                && hasCurrentPosition)
            {
                hiddenPoseDelta = Vector3.Distance(
                    state.lastHiddenPosition, currentPosition);
            }

            // Open the activation-transition window before the emit so that
            // any other emitter (e.g. the immediately-following LateUpdate
            // pass on the same currentUT) sees it open. The emit below uses
            // force: true and so does not consult the window itself —
            // activation decisions are rare-per-ghost events and skipping
            // them on a closed gate would defeat the purpose of the
            // structured trace. ShouldEmitPhase is consulted only as
            // defence-in-depth against a future settings flip.
            if (transition == "first-visible")
            {
                OpenDetailedWindow(
                    recordingId,
                    currentUT,
                    ActivationTransitionWindowSeconds,
                    "activation-transition");
            }

            if (ShouldEmitPhase(recordingId, currentUT, important: false, force: true))
            {
                string prevPosToken = state.hasLastHiddenPosition
                    ? FormatVector3(state.lastHiddenPosition)
                    : "<none>";
                EmitRaw(
                    important: transition == "first-visible",
                    recordingId: recordingId,
                    ghostIndex: ghostIndex,
                    currentUT: currentUT,
                    playbackUT: visiblePlaybackUT,
                    phase: "ActivationDecision",
                    details: "callSite=" + Token(callSite)
                    + " rawPlaybackUT=" + FormatDouble(rawPlaybackUT, "F3")
                    + " visiblePlaybackUT=" + FormatDouble(visiblePlaybackUT, "F3")
                    + " activationStart=" + FormatDouble(activationStartUT, "F3")
                    + " activationLead=" + FormatDouble(activationLead, "F3")
                    + " visibleLead=" + FormatDouble(visibleLead, "F3")
                    + " clampFired=" + Bool(clampFired)
                    + " hidden=" + Bool(hidden)
                    + " hideReason=" + Token(hideReason)
                    + " framesRemaining=" + framesRemaining.ToString(CultureInfo.InvariantCulture)
                    + " transition=" + transition
                    + " prevHiddenPos=" + prevPosToken
                    + " hiddenPoseDelta=" + FormatDouble(hiddenPoseDelta, "F3"));
            }

            // Update sticky hidden-frame tracking AFTER emitting so the
            // emitted line reflects the previous frame's state.
            if (hidden && hasCurrentPosition)
            {
                state.hasLastHiddenPosition = true;
                state.lastHiddenPosition = currentPosition;
                state.lastHiddenPositionFrame = CurrentFrameCount();
                state.wasHiddenLastActivationDecision = true;
            }
            else if (!hidden)
            {
                state.wasHiddenLastActivationDecision = false;
            }
            states[key] = state;
        }

        internal static void EmitPhase(
            string recordingId,
            int ghostIndex,
            double currentUT,
            double playbackUT,
            string phase,
            string details,
            bool important = false,
            bool force = false)
        {
            if (!IsEnabled)
                return;
            if (string.IsNullOrEmpty(recordingId))
                return;

            if (!ShouldEmitPhase(recordingId, currentUT, important, force))
                return;

            EmitRaw(important, recordingId, ghostIndex, currentUT, playbackUT, phase, details);
        }

        internal static bool ShouldEmitPhase(
            string recordingId,
            double currentUT,
            bool important = false,
            bool force = false)
        {
            if (!IsEnabled)
                return false;
            if (string.IsNullOrEmpty(recordingId))
                return false;
            return force || important || IsDetailedWindowOpen(recordingId, currentUT);
        }

        internal static void EmitGuardSkip(
            IPlaybackTrajectory trajectory,
            int ghostIndex,
            double currentUT,
            string reason)
        {
            if (!IsEnabled)
                return;
            string recordingId = trajectory?.RecordingId;
            if (string.IsNullOrEmpty(recordingId))
                return;
            if (!ParsekLog.IsVerboseEnabled)
                return;

            OpenDetailedWindow(recordingId, currentUT, AnomalyWindowSeconds, "guard-skip");
            if (!ShouldEmitPhase(recordingId, currentUT))
                return;

            string key = "guard-skip-"
                + ShortId(recordingId)
                + "-"
                + ghostIndex.ToString(CultureInfo.InvariantCulture)
                + "-"
                + GuardSkipReasonKey(reason);
            ParsekLog.VerboseRateLimited(
                "GhostRenderTrace",
                key,
                () => BuildPrefix(
                    recordingId,
                    ghostIndex,
                    currentUT,
                    currentUT,
                    "GuardSkip",
                    CurrentFrameCount())
                + " reason=" + Token(reason)
                + " vessel=" + Token(trajectory?.VesselName)
                + " startUT=" + FormatDouble(trajectory.StartUT, "F3")
                + " endUT=" + FormatDouble(trajectory.EndUT, "F3"),
                1.0);
        }

        internal static void EmitReapply(
            string recordingId,
            int ghostIndex,
            double currentUT,
            double playbackUT,
            string phase,
            string mode,
            Vector3d before,
            Vector3d after,
            Quaternion rotation,
            string reason = null)
        {
            if (!IsEnabled)
                return;
            double deltaMeters = Vector3d.Distance(before, after);
            bool important = IsLargePoseDelta(deltaMeters, 0.0);
            if (important)
                OpenDetailedWindow(recordingId, currentUT, AnomalyWindowSeconds, "reapply-large-delta");
            if (!ShouldEmitPhase(recordingId, currentUT, important, force: false))
                return;

            EmitPhase(
                recordingId,
                ghostIndex,
                currentUT,
                playbackUT,
                phase,
                "mode=" + Token(mode)
                + " reason=" + Token(reason)
                + " before=" + FormatVector3d(before)
                + " after=" + FormatVector3d(after)
                + " deltaMeters=" + FormatDouble(deltaMeters, "F2")
                + " rot=" + FormatQuaternion(rotation),
                important: important,
                force: false);
        }

        internal static void EmitTerrainClamp(
            string recordingId,
            int ghostIndex,
            double currentUT,
            double playbackUT,
            string mode,
            Vector3d before,
            Vector3d after,
            double altitudeBefore,
            double terrainHeight,
            double altitudeAfter,
            double clearance)
        {
            if (!IsEnabled)
                return;
            OpenDetailedWindow(recordingId, currentUT, AnomalyWindowSeconds, "terrain-clamp");
            EmitPhase(
                recordingId,
                ghostIndex,
                currentUT,
                playbackUT,
                "TerrainClamp",
                "mode=" + Token(mode)
                + " before=" + FormatVector3d(before)
                + " after=" + FormatVector3d(after)
                + " altBefore=" + FormatDouble(altitudeBefore, "F2")
                + " terrain=" + FormatDouble(terrainHeight, "F2")
                + " altAfter=" + FormatDouble(altitudeAfter, "F2")
                + " clearance=" + FormatDouble(clearance, "F2"),
                important: true,
                force: true);
        }

        internal static void EmitRelativeResolver(
            string recordingId,
            int ghostIndex,
            double currentUT,
            double playbackUT,
            string resolver,
            string reason,
            uint anchorVesselId,
            string anchorRecordingId,
            bool success,
            bool fromRecordedTrajectory,
            Vector3d anchorPosition,
            Quaternion anchorRotation,
            Vector3d localOffset,
            Vector3d outputPosition)
        {
            if (!IsEnabled)
                return;
            if (!success)
                OpenDetailedWindow(recordingId, currentUT, AnomalyWindowSeconds, "relative-resolver-miss");
            if (!ShouldEmitPhase(recordingId, currentUT, important: !success, force: !success))
                return;

            EmitPhase(
                recordingId,
                ghostIndex,
                currentUT,
                playbackUT,
                "RelativeResolver",
                "resolver=" + Token(resolver)
                + " reason=" + Token(reason)
                + " success=" + Bool(success)
                + " anchorPid=" + anchorVesselId.ToString(CultureInfo.InvariantCulture)
                + " anchorRec=" + ShortId(anchorRecordingId)
                + " source=" + Token(fromRecordedTrajectory ? "recorded" : "live")
                + " anchorPos=" + FormatVector3d(anchorPosition)
                + " anchorRot=" + FormatQuaternion(anchorRotation)
                + " localOffset=" + FormatVector3d(localOffset)
                + " output=" + FormatVector3d(outputPosition),
                important: !success,
                force: !success);
        }

        internal static GateDecision EvaluateGateForTesting(
            double currentUT,
            double firstSeenUT,
            bool firstSeen,
            bool structuralWindow,
            bool sectionChanged,
            bool force,
            bool resolverMissOrRetired,
            bool reFlyWindow,
            double deltaMeters,
            double expectedDeltaMeters,
            bool largeDeltaSuppressed = false)
        {
            if (force)
                return Decision(true, true, "force");
            if (resolverMissOrRetired)
                return Decision(true, true, "resolver-miss-or-retired");
            if (!largeDeltaSuppressed && IsLargePoseDelta(deltaMeters, expectedDeltaMeters))
                return Decision(true, true, "large-delta");
            if (firstSeen)
                return Decision(true, false, "first-seen");
            if (currentUT - firstSeenUT <= InitialWindowSeconds)
                return Decision(true, false, "initial-window");
            if (reFlyWindow)
                return Decision(true, false, "refly-window");
            if (structuralWindow)
                return Decision(true, false, "structural-window");
            if (sectionChanged)
                return Decision(true, false, "section-change");
            return Decision(false, false, "closed");
        }

        /// <summary>
        /// Returns true when the expected-delta-vs-actual-delta signal cannot
        /// be trusted on the current frame, so the caller should NOT raise a
        /// `large-delta` anomaly. Two cases trigger suppression: any playback
        /// path that signals a zero rendering velocity (orbital propagation,
        /// surface-held / landed positioning, loop-pause / endpoint / fallback
        /// branches in the engine), and frames where stock KSP rebased world
        /// coordinates via `FloatingOrigin.setOffset`. See the comment block
        /// above <see cref="ZeroPlaybackVelocityEpsilonSqrMetresPerSecond"/>
        /// for the detailed rationale, the enumerated zero-velocity paths,
        /// and the log evidence. Pure helper for testability.
        /// </summary>
        internal static bool IsLargeDeltaSignalSuppressed(
            Vector3 lastInterpolatedVelocity,
            int currentUnityFrame,
            int lastFloatingOriginShiftFrame)
        {
            // Zero-rendering-velocity signature: orbit segments, surface
            // holds, and the engine's endpoint / fallback paths leave
            // `lastInterpolatedVelocity` at default(Vector3) on purpose.
            // expectedDM = 0 on those frames provides no signal for the
            // detector, so suppress.
            if (lastInterpolatedVelocity.sqrMagnitude
                < ZeroPlaybackVelocityEpsilonSqrMetresPerSecond)
                return true;

            // Floating-origin shift frame: every ghost in the scene shifts on
            // the same frame; the position delta the detector sees is the
            // rebase magnitude, not a real teleport. Suppress for the shift
            // frame itself plus one frame of slack so a Postfix logged at
            // frame N is honoured by an EmitPostUpdate that happens to fire
            // on frame N+1 in the same physics step.
            if (lastFloatingOriginShiftFrame != int.MinValue
                && currentUnityFrame >= lastFloatingOriginShiftFrame
                && currentUnityFrame - lastFloatingOriginShiftFrame
                    <= FloatingOriginSuppressionFrameWindow)
                return true;

            return false;
        }

        // Production reads `ReFlySettleStabilityTracker.LastFloatingOriginShiftFrame`;
        // xUnit overrides via the static seam below so the suppression test
        // can drive the floating-origin frame without going through the
        // tracker's logging path.
        private static int LastFloatingOriginShiftFrame()
        {
            var ovr = FloatingOriginFrameOverrideForTesting;
            if (ovr != null)
                return ovr();
            return ReFlySettleStabilityTracker.LastFloatingOriginShiftFrame;
        }

        /// <summary>
        /// Test seam for the floating-origin shift frame counter. Production
        /// reads <see cref="ReFlySettleStabilityTracker.LastFloatingOriginShiftFrame"/>;
        /// tests override this to a deterministic value so the suppression
        /// logic can be exercised without invoking the tracker's logging
        /// path. Reset to <c>null</c> in test teardown.
        /// </summary>
        internal static System.Func<int> FloatingOriginFrameOverrideForTesting;

        private static bool IsEnabled =>
            ForceEnabledForTesting
            || (ParsekSettings.Current != null && ParsekSettings.Current.ghostRenderTracing);

        internal static bool IsLargePoseDelta(double deltaMeters, double expectedDeltaMeters)
        {
            if (double.IsNaN(deltaMeters) || double.IsInfinity(deltaMeters))
                return false;
            if (deltaMeters <= LargePoseDeltaMeters)
                return false;

            double expected = double.IsNaN(expectedDeltaMeters) || double.IsInfinity(expectedDeltaMeters)
                ? 0.0
                : Math.Max(0.0, expectedDeltaMeters);
            double threshold = Math.Max(
                LargePoseDeltaMeters,
                expected * VelocityDeltaMultiplier + VelocityDeltaSlackMeters);
            return deltaMeters > threshold;
        }

        internal static string FormatTracePrefixForTesting(
            string recordingId, int ghostIndex, double currentUT, double playbackUT, string phase)
        {
            return BuildPrefix(recordingId, ghostIndex, currentUT, playbackUT, phase, frame: 0);
        }

        internal static string FormatVector3d(Vector3d value)
        {
            return "("
                + FormatDouble(value.x, "F2") + ","
                + FormatDouble(value.y, "F2") + ","
                + FormatDouble(value.z, "F2") + ")";
        }

        internal static string FormatVector3(Vector3 value)
        {
            return "("
                + value.x.ToString("F2", CultureInfo.InvariantCulture) + ","
                + value.y.ToString("F2", CultureInfo.InvariantCulture) + ","
                + value.z.ToString("F2", CultureInfo.InvariantCulture) + ")";
        }

        internal static string FormatQuaternion(Quaternion value)
        {
            return "("
                + value.x.ToString("F4", CultureInfo.InvariantCulture) + ","
                + value.y.ToString("F4", CultureInfo.InvariantCulture) + ","
                + value.z.ToString("F4", CultureInfo.InvariantCulture) + ","
                + value.w.ToString("F4", CultureInfo.InvariantCulture) + ")";
        }

        private static GateDecision Decision(bool emit, bool important, string reason)
        {
            return new GateDecision
            {
                Emit = emit,
                Important = important,
                Reason = reason
            };
        }

        private static bool IsDetailedWindowOpen(string recordingId, double currentUT)
        {
            if (string.IsNullOrEmpty(recordingId))
                return false;
            double until;
            return detailedUntilByRecording.TryGetValue(recordingId, out until)
                && currentUT <= until;
        }

        /// <summary>
        /// Test seam exposing the otherwise-private detailed-window predicate.
        /// Used by Phase 1 unit tests to verify the activation-transition
        /// window opens on the first-visible transition.
        /// </summary>
        internal static bool IsDetailedWindowOpenForTesting(
            string recordingId, double currentUT)
        {
            return IsDetailedWindowOpen(recordingId, currentUT);
        }

        private static void EmitRaw(
            bool important,
            string recordingId,
            int ghostIndex,
            double currentUT,
            double playbackUT,
            string phase,
            string details)
        {
            string message = BuildPrefix(
                    recordingId,
                    ghostIndex,
                    currentUT,
                    playbackUT,
                    phase,
                    CurrentFrameCount())
                + " " + details;
            if (important)
                ParsekLog.Info("GhostRenderTrace", message);
            else
                ParsekLog.Verbose("GhostRenderTrace", message);
        }

        private static string BuildPrefix(
            string recordingId,
            int ghostIndex,
            double currentUT,
            double playbackUT,
            string phase,
            int frame)
        {
            return "phase=" + Token(phase)
                + " rec=" + ShortId(recordingId)
                + " recId=" + Token(recordingId)
                + " ghostIndex=" + ghostIndex.ToString(CultureInfo.InvariantCulture)
                + " frame=" + frame.ToString(CultureInfo.InvariantCulture)
                + " currentUT=" + FormatDouble(currentUT, "F3")
                + " playbackUT=" + FormatDouble(playbackUT, "F3");
        }

        private static SectionContext ResolveSection(IPlaybackTrajectory trajectory, double playbackUT)
        {
            SectionContext context = new SectionContext
            {
                Index = -1,
                Frame = ReferenceFrame.Absolute,
                Environment = SegmentEnvironment.ExoBallistic,
                Source = TrackSectionSource.Active,
                StartUT = double.NaN,
                EndUT = double.NaN,
                DebrisBracketSeconds = double.NaN
            };

            var sections = trajectory?.TrackSections;
            if (sections == null || sections.Count == 0)
                return context;

            int sectionIndex = TrajectoryMath.FindTrackSectionForUT(sections, playbackUT);
            context.Index = sectionIndex;
            if (sectionIndex < 0 || sectionIndex >= sections.Count)
                return context;

            TrackSection section = sections[sectionIndex];
            context.HasSection = true;
            context.Frame = section.referenceFrame;
            context.Environment = section.environment;
            context.Source = section.source;
            context.StartUT = section.startUT;
            context.EndUT = section.endUT;
            context.FrameCount = section.frames?.Count ?? 0;
            context.bodyFixedFrameCount = section.bodyFixedFrames?.Count ?? 0;
            context.CheckpointCount = section.checkpoints?.Count ?? 0;
            context.AnchorRecordingId = section.anchorRecordingId;
            context.BoundaryDiscontinuityMeters = section.boundaryDiscontinuityMeters;
            context.DebrisBracketSeconds = section.referenceFrame == ReferenceFrame.Relative
                ? ResolveDebrisBracketSeconds(section.frames, playbackUT)
                : double.NaN;
            return context;
        }

        /// <summary>
        /// Computes the time gap between the two recorded `frames` entries
        /// that bracket the playback UT in a Relative section. Linear scan;
        /// per-frame work is bounded by the section's debris frame count,
        /// which is typically tens to low hundreds. Returns NaN when no
        /// usable bracket is available (fewer than two frames, or playbackUT
        /// is outside the recorded range).
        /// </summary>
        private static double ResolveDebrisBracketSeconds(
            List<TrajectoryPoint> frames, double playbackUT)
        {
            if (frames == null || frames.Count < 2)
                return double.NaN;
            for (int i = 0; i < frames.Count - 1; i++)
            {
                double a = frames[i].ut;
                double b = frames[i + 1].ut;
                if (playbackUT >= a && playbackUT <= b)
                    return Math.Abs(b - a);
            }
            return double.NaN;
        }

        private static string FormatSection(SectionContext section)
        {
            return "sec=" + section.Index.ToString(CultureInfo.InvariantCulture)
                + " secUT=[" + FormatDouble(section.StartUT, "F3")
                + "," + FormatDouble(section.EndUT, "F3") + "]"
                + " ref=" + Token(section.HasSection ? section.Frame.ToString() : "none")
                + " env=" + Token(section.HasSection ? section.Environment.ToString() : "none")
                + " source=" + Token(section.HasSection ? section.Source.ToString() : "none")
                + " frames=" + section.FrameCount.ToString(CultureInfo.InvariantCulture)
                + " bodyFixedFrames=" + section.bodyFixedFrameCount.ToString(CultureInfo.InvariantCulture)
                + " checkpoints=" + section.CheckpointCount.ToString(CultureInfo.InvariantCulture)
                + " anchorRec=" + ShortId(section.AnchorRecordingId)
                + " boundaryDM=" + section.BoundaryDiscontinuityMeters.ToString("F2", CultureInfo.InvariantCulture)
                + " debrisBracketSec=" + FormatDouble(section.DebrisBracketSeconds, "F3");
        }

        private static string BuildStateKey(string recordingId, int ghostIndex)
        {
            return recordingId + "|" + ghostIndex.ToString(CultureInfo.InvariantCulture);
        }

        private static string ShortId(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "<none>";
            return value.Length > 8 ? value.Substring(0, 8) : value;
        }

        private static string Token(string value)
        {
            return string.IsNullOrEmpty(value) ? "<none>" : value.Replace(' ', '_');
        }

        private static string GuardSkipReasonKey(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "<none>";
            int firstSpace = value.IndexOf(' ');
            string stable = firstSpace >= 0 ? value.Substring(0, firstSpace) : value;
            return Token(stable);
        }

        private static string Bool(bool value)
        {
            return value ? "true" : "false";
        }

        private static string FormatDouble(double value, string format)
        {
            if (double.IsNaN(value))
                return "NaN";
            if (double.IsPositiveInfinity(value))
                return "Infinity";
            if (double.IsNegativeInfinity(value))
                return "-Infinity";
            return value.ToString(format, CultureInfo.InvariantCulture);
        }
    }
}
