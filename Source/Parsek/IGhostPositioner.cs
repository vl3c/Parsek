using UnityEngine;

namespace Parsek
{
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
            uint anchorVesselId);

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
            IPlaybackTrajectory traj, double distance, int protectedIndex);

        void ClearOrbitCache();

        /// <summary>
        /// Resolves the world-space position of a vessel by persistent ID,
        /// for diagnostic / observability code that wants to surface
        /// "where is the anchor right now" alongside the ghost's own
        /// position. Pure host concern (recorder lookup), exposed via the
        /// positioner so the playback engine doesn't need to take a
        /// FlightRecorder dependency just for log enrichment.
        ///
        /// <para>
        /// Returns false when the pid is 0, the vessel is not loaded, or
        /// the resulting world position is non-finite. Implementations are
        /// expected to be cheap (single PID hash lookup + one position
        /// read) — call sites in the engine assume O(1) cost.
        /// </para>
        /// </summary>
        bool TryGetLiveAnchorWorldPosition(uint anchorVesselId, out Vector3d worldPosition);
    }
}
