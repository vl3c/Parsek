namespace Parsek
{
    /// <summary>
    /// Playback policy for v12+ debris that carries the explicit
    /// parent-recording anchor contract.
    /// </summary>
    internal static class DebrisRelativePlaybackPolicy
    {
        /// <summary>
        /// Parent-anchored debris should disappear when its recorded parent
        /// anchor cannot be resolved. The v7 absolute shadow is not an
        /// independent fallback for this case because it can continue stale
        /// motion after the debris has left the parent's resolvable range.
        /// </summary>
        internal static bool ShouldRetireOnRecordedParentAnchorMiss(
            IPlaybackTrajectory traj)
        {
            return traj != null
                && traj.IsDebris
                && !string.IsNullOrEmpty(traj.DebrisParentRecordingId);
        }
    }
}
