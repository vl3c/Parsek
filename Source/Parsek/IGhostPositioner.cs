using UnityEngine;

namespace Parsek
{
    internal readonly struct RelativeSectionPlaybackTarget
    {
        public readonly string RecordingId;
        public readonly int SectionIndex;
        public readonly TrackSection Section;
        public readonly string AnchorRecordingId;

        public RelativeSectionPlaybackTarget(
            string recordingId,
            int sectionIndex,
            TrackSection section)
        {
            RecordingId = recordingId;
            SectionIndex = sectionIndex;
            Section = section;
            AnchorRecordingId = string.IsNullOrWhiteSpace(section.anchorRecordingId)
                ? null
                : section.anchorRecordingId.Trim();
        }

        public bool HasAnchorRecordingId => !string.IsNullOrEmpty(AnchorRecordingId);
    }

    /// <summary>
    /// Result of zone rendering evaluation.
    /// The engine uses this to skip positioning/events for hidden ghosts.
    /// </summary>
    internal struct ZoneRenderingResult
    {
        /// <summary>Ghost was hidden by distance/zone render policy — engine should skip further work.</summary>
        public bool hiddenByZone;

        /// <summary>Part events should not be applied this frame.</summary>
        public bool skipPartEvents;

        /// <summary>Audio, engine/RCS FX, and reentry FX should be suppressed this frame.</summary>
        public bool suppressVisualFx;

        /// <summary>Apply reduced renderer fidelity while the ghost remains visible.</summary>
        public bool reduceFidelity;
    }

    /// <summary>
    /// Positions ghost GameObjects in the world. Implemented by the host
    /// scene controller (ParsekFlight for flight scene, ParsekKSC for KSC scene).
    ///
    /// The ghost playback engine calls these methods but does not know how
    /// positioning works — body lookups, floating-origin correction, orbit
    /// propagation, and surface-relative reconstruction are all host concerns.
    /// </summary>
    internal interface IGhostPositioner
    {
        void InterpolateAndPosition(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, double ut, bool suppressFx);

        void InterpolateAndPositionRelative(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, double ut, bool suppressFx,
            RelativeSectionPlaybackTarget target);

        /// <summary>
        /// Position the ghost from the recording's `absoluteFrames` shadow when
        /// the tumbling-parent gate has classified the parent-relative chain
        /// rotation as unreliable. Routes through the same `InterpolateAndPosition`
        /// path the legacy v11 shadow gate uses, so body / altitude / GhostPosEntry
        /// FloatingOrigin reapply / InterpolationResult population are all reused.
        /// Returns false when the section has no shadow data or the playback UT
        /// is outside coverage (caller falls back to <see cref="AnchorRotationUnreliableRoute.Hidden"/>).
        /// </summary>
        /// <remarks>
        /// Phase D: this path is recorded-data-only, never a substitute for live
        /// anchors, and is reached only after the gate has positively classified
        /// the parent chain as visually unreliable.
        /// </remarks>
        bool TryPositionFromRelativeAbsoluteShadow(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, double playbackUT, RelativeSectionPlaybackTarget target,
            out double bracketBeforeUT, out double bracketAfterUT);

        void PositionAtPoint(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, TrajectoryPoint point);

        void PositionAtSurface(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state);

        void PositionFromOrbit(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, double ut);

        void PositionLoop(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, double ut, bool suppressFx);

        bool TryResolveExplosionAnchorPosition(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, out Vector3 worldPosition);

        ZoneRenderingResult ApplyZoneRendering(int index, GhostPlaybackState state,
            IPlaybackTrajectory traj, double distance, double playbackUT, int protectedIndex);

        void ClearOrbitCache();
    }
}
