using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Phase 5 of Rewind-to-Staging (design §5.11). Virtual UI group surfacing
    /// every recording that currently satisfies
    /// <see cref="EffectiveState.IsUnfinishedFlight(Recording)"/>.
    ///
    /// <para>
    /// This group is NOT stored in <see cref="GroupHierarchyStore"/> — its
    /// membership is derived each frame from
    /// <see cref="EffectiveState.ComputeERS"/> filtered by
    /// <see cref="EffectiveState.IsUnfinishedFlight"/>. As a system group it
    /// cannot be hidden (design §7.30) and cannot be a drop target for
    /// manual group assignment (design §7.25). Rename / hide on an
    /// individual member row still works per design §7.33; the group
    /// itself has no X disband button (mirrors the precedent for chain
    /// blocks in <see cref="RecordingsTableUI.DrawChainBlock"/>).
    /// </para>
    ///
    /// <para>
    /// Membership compute is cheap — linear over ERS with an <c>O(RP)</c>
    /// check per recording inside <c>IsUnfinishedFlight</c>. The UI draw
    /// pass invokes this once per frame at most, so no dedicated version
    /// counter is plumbed in Phase 5; recomputation simply piggybacks on
    /// the existing ERS cache via <see cref="EffectiveState.ComputeERS"/>.
    /// </para>
    /// </summary>
    internal static class UnfinishedFlightsGroup
    {
        /// <summary>
        /// Display name of the virtual group. Chosen to not collide with any
        /// user-creatable group name (leading uppercase + space, matching the
        /// natural display text in the recordings table).
        /// </summary>
        public const string GroupName = "Unfinished Flights";

        public const string Tooltip =
            "Vessels and kerbals that ended up in a state where you might want to re-fly them -- crashed, abandoned in orbit, stranded on a surface. Click Play to take control at the separation moment; click Seal to close the slot permanently if you're done with it.";

        /// <summary>
        /// True iff <paramref name="name"/> equals the virtual group name.
        /// System groups are read-only, cannot be hidden, and reject
        /// drag-into operations (design §5.11 / §7.25 / §7.30).
        /// </summary>
        public static bool IsSystemGroup(string name)
        {
            return !string.IsNullOrEmpty(name)
                && string.Equals(name, GroupName, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// Computes the current member list: every recording in ERS that
        /// satisfies <see cref="EffectiveState.IsUnfinishedFlight"/>.
        /// Returns a fresh read-only list each call. The underlying ERS
        /// source is cached by <see cref="EffectiveState"/>, so repeated
        /// calls within a frame are cheap.
        /// </summary>
        public static IReadOnlyList<Recording> ComputeMembers()
        {
            var ers = EffectiveState.ComputeERS();
            var members = new List<Recording>();
            if (ers == null)
            {
                LogRecompute(0);
                return members;
            }

            for (int i = 0; i < ers.Count; i++)
            {
                var rec = ers[i];
                if (rec == null) continue;
                if (EffectiveState.IsUnfinishedFlight(rec))
                    members.Add(rec);
            }

            LogRecompute(members.Count);
            return members;
        }

        // Verbose-rate-limited recompute summary per design §10.5:
        //   [UnfinishedFlights] recompute: N entries
        // Rate-limited with shared key "unfinishedflights-recompute" so
        // per-frame UI draw does not spam the log.
        private static void LogRecompute(int count)
        {
            ParsekLog.VerboseRateLimited(
                "UnfinishedFlights",
                "unfinishedflights-recompute",
                $"recompute: {count} entries");
        }
    }
}
