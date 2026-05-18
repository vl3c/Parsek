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
        /// Player-facing display name of the virtual group. The underlying
        /// feature/architectural concept is still "Unfinished Flights" — the
        /// group label is the shelf the items sit on, not the items
        /// themselves. All-caps is the visual signal that this is a
        /// system-controlled group (not user-typed) and is unlikely to
        /// collide with any natural user-created group name.
        /// </summary>
        public const string GroupName = "STASH";

        public const string Tooltip =
            "Vessels and kerbals that ended up in a state where you might want to re-fly them -- crashed, abandoned in orbit, stranded on a surface. Click Fly to take control at the separation moment; click Seal to close the slot permanently if you're done with it.";

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
        /// satisfies <see cref="EffectiveState.IsUnfinishedFlight"/>,
        /// deduplicated to one entry per chain (the chain head — lowest
        /// <see cref="Recording.ChainIndex"/>). Returns a fresh read-only
        /// list each call. The underlying ERS source is cached by
        /// <see cref="EffectiveState"/>, so repeated calls within a frame
        /// are cheap.
        /// <para>
        /// Chain dedupe: <c>RecordingOptimizer</c> phase-change splits
        /// (e.g. Atmospheric → ExoBallistic at the karman line) produce a
        /// chain HEAD + chain TIP from a single logical flight. Both
        /// qualify under <see cref="EffectiveState.IsUnfinishedFlight"/>
        /// because the classifier deliberately follows the shared chain
        /// walker — that lookup must keep working for
        /// <see cref="RewindPointReaper"/>, which routes a slot's
        /// effective recording (the chain TIP) through the same predicate
        /// to decide whether to retire the rewind point. The STASH should
        /// list one row per logical flight though: only the chain head
        /// owns the Fly / Seal buttons (its BPs anchor it to the rewind
        /// point), and surfacing both halves duplicates each Re-Fly slot
        /// in the UI.
        /// </para>
        /// </summary>
        public static IReadOnlyList<Recording> ComputeMembers()
        {
            var ers = EffectiveState.ComputeERS();
            var members = new List<Recording>();
            if (ers == null)
            {
                LogRecompute(0, 0);
                return members;
            }

            // First pass: per (ChainId, ChainBranch) bucket, remember the
            // qualifying member with the lowest ChainIndex (= the chain
            // head). Non-chain recordings (null/empty ChainId) bypass
            // bucketing and admit directly.
            var chainHeads = new Dictionary<string, Recording>(System.StringComparer.Ordinal);
            int chainDuplicatesSkipped = 0;
            for (int i = 0; i < ers.Count; i++)
            {
                var rec = ers[i];
                if (rec == null) continue;
                if (!EffectiveState.IsUnfinishedFlight(rec)) continue;

                if (string.IsNullOrEmpty(rec.ChainId))
                {
                    members.Add(rec);
                    continue;
                }

                string chainKey = rec.ChainId + "|" + rec.ChainBranch.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!chainHeads.TryGetValue(chainKey, out var existing))
                {
                    chainHeads[chainKey] = rec;
                    continue;
                }

                // Keep the lower ChainIndex (chain head). Both members
                // already passed IsUnfinishedFlight, so this purely picks
                // the row to surface — the loser is the chain continuation
                // that has no slot-anchoring BPs of its own.
                if (rec.ChainIndex < existing.ChainIndex)
                    chainHeads[chainKey] = rec;
                chainDuplicatesSkipped++;
            }
            foreach (var head in chainHeads.Values)
                members.Add(head);

            LogRecompute(members.Count, chainDuplicatesSkipped);
            return members;
        }

        // Verbose-rate-limited recompute summary per design §10.5:
        //   [UnfinishedFlights] recompute: N entries (chainDuplicatesSkipped=K)
        // Rate-limited with shared key "unfinishedflights-recompute" so
        // per-frame UI draw does not spam the log.
        private static void LogRecompute(int count, int chainDuplicatesSkipped)
        {
            ParsekLog.VerboseRateLimited(
                "UnfinishedFlights",
                "unfinishedflights-recompute",
                $"recompute: {count} entries chainDuplicatesSkipped={chainDuplicatesSkipped}");
        }
    }
}
