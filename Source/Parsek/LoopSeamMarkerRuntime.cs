using System;
using System.Collections.Generic;
using System.Text;

namespace Parsek
{
    // Design authority: docs/dev/design-dock-event-graph.md 7.3 (emission: once per marker per unit
    // cycle), 10 edge cases 16 / 19, 14 (per member per frame = one double comparison, zero
    // allocations).
    //
    // The RUNTIME half of the loop seam markers: the per-member cursor + dedup the flight engine
    // drives, and the same-frame message accumulator the policy layer posts through. Both live here,
    // outside the engine, so the rules are headlessly testable - the engine is a MonoBehaviour-hosted
    // class that no unit test can construct.
    //
    // PURITY CONTRACT (mirroring LoopSeamMarkerBuilder.cs): no Unity types, no store reads. The frame
    // number and the marker lists arrive as PARAMETERS; the source-text gate in DockEventGraphTests
    // pins that, and the gate is a LITERAL scan, so RecordingStore / ParsekScenario / EffectiveState /
    // MissionStore may never be written with a trailing dot here, even in a comment.

    /// <summary>
    /// Per-unit-member seam-marker state: where the sorted-cursor walk has reached, which (marker,
    /// cycle) pairs already fired, and WHICH MARKER LIST that state belongs to.
    ///
    /// <para><b>The marker-list identity check is the load-bearing part.</b> A signature-gated
    /// loop-unit rebuild (toggling a partner-journey link or an interval checkbox mid-flight,
    /// committing a recording) swaps the unit's marker list for a different one WITHOUT a loop-cycle
    /// change. Carrying the old cursor and dedup across that swap silently loses markers: stale fired
    /// keys suppress whatever marker now sits at that index, an advanced cursor skips earlier new
    /// markers, and after a commit the committed-index space itself moved, so the member index no
    /// longer means the same recording. Resetting on a list-reference change makes every rebuild path
    /// correct regardless of where the swap came from; <see cref="Reset"/> (called by the engine when
    /// the whole unit set is replaced) is the belt to that suspenders.</para>
    /// </summary>
    internal sealed class LoopSeamMarkerRuntime
    {
        private sealed class MemberState
        {
            internal int Cursor;
            internal long Cycle = long.MinValue;
            internal object MarkerListIdentity;
            internal readonly HashSet<long> FiredKeys = new HashSet<long>();
        }

        private readonly Dictionary<int, MemberState> byMember = new Dictionary<int, MemberState>();

        /// <summary>Tracked members - a test/diagnostic surface, not consumed by behavior.</summary>
        internal int TrackedMemberCount => byMember.Count;

        /// <summary>
        /// Drops every member's cursor and dedup. Called when the engine is handed a DIFFERENT loop
        /// unit set (a rebuild) and on full ghost teardown, so no state outlives the index space it
        /// was keyed in.
        /// </summary>
        internal void Reset() => byMember.Clear();

        /// <summary>
        /// The per-frame check for one member. Returns the index into <paramref name="markers"/> of
        /// the marker to emit now (already recorded as fired), or -1.
        ///
        /// <para><paramref name="skippedUnfired"/> counts markers for THIS member whose whole window
        /// the clock stepped over without ever firing - the deliberate no-fire behavior at high time
        /// warp (a single frame can advance the loop clock past the entire display window). The
        /// caller logs it: a silent path must still leave evidence.
        /// <paramref name="firstSkippedIndex"/> names one of them for that line, or -1.</para>
        ///
        /// <para>Zero allocations on the steady-state path (dictionary lookup + the pure cursor's
        /// single double comparison); allocation happens once per member on first use.</para>
        /// </summary>
        internal int TryTake(
            IReadOnlyList<GhostPlaybackLogic.LoopSeamMarker> markers,
            int memberIndex,
            double spanLoopUT,
            long unitCycle,
            out int skippedUnfired,
            out int firstSkippedIndex)
        {
            skippedUnfired = 0;
            firstSkippedIndex = -1;
            if (markers == null || markers.Count == 0)
                return -1;

            if (!byMember.TryGetValue(memberIndex, out MemberState st))
            {
                st = new MemberState();
                byMember[memberIndex] = st;
            }
            // A new cycle replays the whole span, so every marker is due again; a new marker LIST is
            // a different unit entirely and its state must not inherit anything.
            if (st.Cycle != unitCycle || !ReferenceEquals(st.MarkerListIdentity, markers))
            {
                st.Cycle = unitCycle;
                st.MarkerListIdentity = markers;
                st.Cursor = 0;
                st.FiredKeys.Clear();
            }

            int index = GhostPlaybackLogic.TryResolveSeamMarkerToEmit(
                markers, memberIndex, spanLoopUT, ref st.Cursor, st.FiredKeys, unitCycle,
                out skippedUnfired, out firstSkippedIndex);
            if (index >= 0)
                st.FiredKeys.Add(GhostPlaybackLogic.SeamMarkerDedupKey(index, unitCycle));
            return index;
        }
    }

    /// <summary>
    /// Joins the seam messages raised within ONE frame into a single line.
    ///
    /// <para><b>Why.</b> Several markers can share one seam UT - a two-parent merge with the docked
    /// stretch excluded raises an R3 for each parent at the same instant - and they arrive in the
    /// same engine pass. Posting them as separate screen messages runs them into the emission floor
    /// (the real-time throttle that keeps a high-warp loop from stacking unreadable lines), which
    /// would drop the second one EVERY cycle: permanently invisible, not merely delayed. Joining
    /// first means one seam moment is one message, and the floor then only ever separates genuinely
    /// distinct moments.</para>
    ///
    /// <para>Pure: the frame number is a parameter. The caller flushes once per frame after the
    /// engine pass so a buffered line never sits unposted when no further event arrives.</para>
    /// </summary>
    internal sealed class SeamMessageAccumulator
    {
        internal const string Separator = "\n";

        private readonly List<string> buffered = new List<string>();
        private int bufferedFrame = int.MinValue;

        internal int BufferedCount => buffered.Count;

        /// <summary>
        /// Buffers <paramref name="text"/> for <paramref name="frame"/>. Returns the joined text of
        /// a DIFFERENT, now-closed frame that the caller must post first, or null when there is
        /// nothing owed. Empty / null text is ignored.
        /// </summary>
        internal string Offer(int frame, string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            string due = null;
            if (buffered.Count > 0 && frame != bufferedFrame)
                due = TakeJoined();
            bufferedFrame = frame;
            if (!buffered.Contains(text))     // the same sentence twice in one frame says nothing new
                buffered.Add(text);
            return due;
        }

        /// <summary>The joined buffered text (buffer cleared), or null when nothing is buffered.</summary>
        internal string Flush() => buffered.Count > 0 ? TakeJoined() : null;

        private string TakeJoined()
        {
            string joined;
            if (buffered.Count == 1)
            {
                joined = buffered[0];
            }
            else
            {
                var sb = new StringBuilder();
                for (int i = 0; i < buffered.Count; i++)
                {
                    if (i > 0)
                        sb.Append(Separator);
                    sb.Append(buffered[i]);
                }
                joined = sb.ToString();
            }
            buffered.Clear();
            return joined;
        }
    }
}
