using UnityEngine;

namespace Parsek
{
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
            IPlaybackTrajectory anchorTraj, double anchorUT);

        void PositionAtPoint(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, TrajectoryPoint point);

        void PositionAtSurface(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state);

        void PositionFromOrbit(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, double ut);

        void PositionLoop(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, double ut, bool suppressFx);

        GameObject CreateSphere(string name, int index);

        void ApplyZoneRendering(int index, GhostPlaybackState state,
            IPlaybackTrajectory traj, double distance, int protectedIndex);

        void ClearOrbitCache();
    }
}
