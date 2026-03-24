using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Result of zone rendering evaluation.
    /// The engine uses this to skip positioning/events for hidden ghosts.
    /// </summary>
    internal struct ZoneRenderingResult
    {
        /// <summary>Ghost was hidden (Beyond zone) — engine should skip further work.</summary>
        public bool hiddenByZone;

        /// <summary>Part events should not be applied this frame (zone policy).</summary>
        public bool skipPartEvents;
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

        GameObject CreateSphere(string name, int index);

        ZoneRenderingResult ApplyZoneRendering(int index, GhostPlaybackState state,
            IPlaybackTrajectory traj, double distance, int protectedIndex);

        void ClearOrbitCache();
    }
}
