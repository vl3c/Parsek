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
        internal const double LargePoseDeltaMeters = 25.0;
        internal const double VelocityDeltaMultiplier = 4.0;
        internal const double VelocityDeltaSlackMeters = 25.0;

        private struct TraceState
        {
            public bool initialized;
            public double firstSeenUT;
            public int lastSectionIndex;
            public bool hasSectionIndex;
            public bool hasLastRenderedPose;
            public Vector3 lastRenderedPosition;
            public double lastPlaybackUT;
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
            /// Recording's <c>absoluteFrames</c> shadow lerp. Used for
            /// parent-anchored debris (v12+) whenever shadow data covers
            /// the playback UT, regardless of the tumbling-parent gate.
            /// </summary>
            Shadow = 2,

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
                case RenderSurface.Shadow: return "shadow";
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
            public int AbsoluteFrameCount;
            public int CheckpointCount;
            public string AnchorRecordingId;
            public float BoundaryDiscontinuityMeters;
            public bool HasSection;
            // Debris-frame bracket span at the current playback UT: the time
            // gap between the two recorded `frames` entries that contain
            // playbackUT. NaN when not Relative or fewer than two frames are
            // available. Surfaces the wide-bracket pattern (thin-tail
            // optimiser cuts that produce 2 s+ gaps) which the legacy
            // relative-offset reconstruction lerps through linearly while
            // the parent rotates underneath. The dominant pre-shadow-route
            // failure mode that PR #803 / docs/dev/plans/debris-always-shadow.md
            // resolves; logged so post-hoc analysis can attribute frames to it.
            public double DebrisBracketSeconds;
        }

        private static readonly Dictionary<string, TraceState> states =
            new Dictionary<string, TraceState>(StringComparer.Ordinal);

        private static readonly Dictionary<string, double> detailedUntilByRecording =
            new Dictionary<string, double>(StringComparer.Ordinal);

        internal static bool ForceEnabledForTesting;

        internal static void Reset()
        {
            states.Clear();
            detailedUntilByRecording.Clear();
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
            RenderSurface surface = RenderSurface.Unknown)
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
            Vector3 position = hasGhost ? playbackState.ghost.transform.position : Vector3.zero;
            Quaternion rotation = hasGhost ? playbackState.ghost.transform.rotation : Quaternion.identity;

            double deltaMeters = state.hasLastRenderedPose
                ? Vector3.Distance(state.lastRenderedPosition, position)
                : 0.0;
            double expectedDeltaMeters = 0.0;
            if (state.hasLastRenderedPose && playbackState != null)
            {
                double playbackDt = Math.Abs(playbackUT - state.lastPlaybackUT);
                expectedDeltaMeters = playbackState.lastInterpolatedVelocity.magnitude * playbackDt;
            }

            bool anomaly = IsLargePoseDelta(deltaMeters, expectedDeltaMeters);
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
                expectedDeltaMeters: expectedDeltaMeters);

            if (gate.Emit)
            {
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
                    + " active=" + Bool(hasGhost && playbackState.ghost.activeSelf)
                    + " surface=" + RenderSurfaceToken(surface)
                    + " pos=" + FormatVector3(position)
                    + " rot=" + FormatQuaternion(rotation)
                    + " dM=" + FormatDouble(deltaMeters, "F2")
                    + " expectedDM=" + FormatDouble(expectedDeltaMeters, "F2")
                    + " velocity=" + FormatVector3(playbackState != null
                        ? playbackState.lastInterpolatedVelocity
                        : Vector3.zero)
                    + " body=" + Token(playbackState?.lastInterpolatedBodyName)
                    + " alt=" + FormatDouble(playbackState != null
                        ? playbackState.lastInterpolatedAltitude
                        : double.NaN, "F2"));
            }

            state.initialized = true;
            if (double.IsNaN(state.firstSeenUT) || state.firstSeenUT == 0.0)
                state.firstSeenUT = currentUT;
            state.hasLastRenderedPose = hasGhost;
            state.lastRenderedPosition = position;
            state.lastPlaybackUT = playbackUT;
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
                    Time.frameCount)
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
            double expectedDeltaMeters)
        {
            if (force)
                return Decision(true, true, "force");
            if (resolverMissOrRetired)
                return Decision(true, true, "resolver-miss-or-retired");
            if (IsLargePoseDelta(deltaMeters, expectedDeltaMeters))
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
                    Time.frameCount)
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
            context.AbsoluteFrameCount = section.absoluteFrames?.Count ?? 0;
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
                + " absFrames=" + section.AbsoluteFrameCount.ToString(CultureInfo.InvariantCulture)
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
