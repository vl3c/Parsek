namespace Parsek
{
    /// <summary>
    /// Pure predicate that decides whether a Relative-section playback frame
    /// should bypass the resolver chain in favor of the absolute-shadow
    /// fallback path. Fires only for **legacy v11 debris** in existing
    /// broken saves: <see cref="IPlaybackTrajectory.IsDebris"/> is true,
    /// <see cref="IPlaybackTrajectory.DebrisParentRecordingId"/> is null,
    /// the section is Relative, and the section has a populated
    /// <c>absoluteFrames</c> shadow.
    ///
    /// Rationale (per `recording-and-ghost-policies-refactor-plan.md`
    /// §3c §"Legacy-debris playback gate"): legacy v11 debris was recorded
    /// with `section.anchorRecordingId` set to the nearest-vessel-at-sample-time
    /// rather than the parent recording. The resolver "succeeds" with the
    /// wrong anchor (some unrelated vessel that has since moved) and never
    /// reaches the existing post-failure absolute-shadow path. Moving the
    /// shadow check ahead of the resolver — gated on this predicate —
    /// retroactively fixes the broken saves without mutating sidecar data.
    ///
    /// v12+ debris (where the recorder populates
    /// <c>DebrisParentRecordingId</c> per the parent-anchor contract)
    /// fails condition #2 and skips the gate, falling through to the
    /// resolver per Decision §7. Non-debris recordings fail condition
    /// #1 and are unaffected.
    ///
    /// Pure function of two interface fields and two
    /// <see cref="TrackSection"/> fields; testable from xUnit without
    /// a Unity runtime.
    /// </summary>
    internal static class LegacyDebrisShadowGate
    {
        /// <summary>
        /// Returns <c>true</c> when this Relative section should bypass
        /// the resolver chain in favor of the absolute-shadow path.
        ///
        /// Conditions (all four required):
        /// <list type="number">
        /// <item><description><see cref="IPlaybackTrajectory.IsDebris"/> is true.</description></item>
        /// <item><description><see cref="IPlaybackTrajectory.DebrisParentRecordingId"/> is null or empty (legacy v11; v12+ debris is skipped).</description></item>
        /// <item><description><see cref="TrackSection.referenceFrame"/> is <see cref="ReferenceFrame.Relative"/> (Absolute sections need no special handling).</description></item>
        /// <item><description><c>absoluteFrames</c> is non-null and non-empty (graceful no-op when shadow data is unavailable).</description></item>
        /// </list>
        /// </summary>
        internal static bool IsLegacyDebrisShadowEligible(
            IPlaybackTrajectory traj,
            TrackSection section)
        {
            if (traj == null) return false;
            if (!traj.IsDebris) return false;
            if (!string.IsNullOrEmpty(traj.DebrisParentRecordingId)) return false;
            if (section.referenceFrame != ReferenceFrame.Relative) return false;
            if (section.absoluteFrames == null) return false;
            if (section.absoluteFrames.Count == 0) return false;
            return true;
        }
    }
}
