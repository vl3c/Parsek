using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Targeted playback observability: emits per-frame
    /// <c>[Parsek][INFO][PlaybackTrace]</c> log lines during the brief window
    /// after structural separation events (decouples that produce a
    /// controllable child / Rewind Point boundary), so we can correlate the
    /// rendered ghost world position with the active <see cref="TrackSection"/>
    /// and detect frame-to-frame jitter or section-boundary discontinuities.
    ///
    /// <para><b>Scope.</b> Decouples emit two structural-event point flags into
    /// the parent recording: at the joint-break UT and (for controlled-child
    /// splits) at the Rewind-Point seam. Debris splits also flag, but the
    /// trace gate fires for any structural-event flag — operators interested
    /// only in RP separations can filter the log to the focus ghost. The
    /// 5-second post-event window keeps log volume bounded to the moment
    /// where wobble / jitter is reported during separation.</para>
    ///
    /// <para><b>Gate.</b> Active only when <paramref name="currentUT"/> is
    /// within <see cref="PostEventWindowSeconds"/> of a
    /// <see cref="TrajectoryPoint"/> with
    /// <see cref="TrajectoryPointFlags.StructuralEventSnapshot"/> set in the
    /// trajectory's <c>Points</c> list. Outside the window the helper short-
    /// circuits without allocating or emitting anything.</para>
    ///
    /// <para><b>Per-frame line shape.</b>
    /// <code>
    /// rec=&lt;short&gt; #&lt;ghostIdx&gt; ut=&lt;F3&gt;
    ///   sec=&lt;idx&gt; [&lt;startUT&gt;,&lt;endUT&gt;] ref=&lt;Absolute|Relative|...&gt;
    ///   worldPos=(x,y,z) dM=&lt;F2&gt; dSpd=&lt;F1&gt; [sectionCrossed]
    /// </code>
    /// <c>dM</c> is the metres travelled since the previous trace frame for
    /// this same ghost; <c>dSpd</c> is <c>dM / (currentUT - prevUT)</c> in m/s.
    /// A large <c>dM</c> on a single frame inside a section, or a position
    /// discontinuity at a <c>sectionCrossed</c> frame, points at the source of
    /// visible jitter.</para>
    ///
    /// <para><b>Loop-replay dedup.</b> Each unique structural-event UT is
    /// traced in full exactly once per (recordingId, ghostIdx). A looping
    /// showcase recording re-enters the same post-event window every loop;
    /// without dedup the INFO line count would multiply by the loop count.
    /// An event UT is retired into a per-ghost <c>completedEventUTs</c> set
    /// the moment its window can no longer be in its first pass — when a
    /// gate-closed frame ages past it, when a frame for a different event
    /// UT shows, when <c>currentUT</c> jumps backwards onto it (loop wrap
    /// at the window edge), or when a frame lands before every flagged
    /// event UT (the recording looped past the event start). Once retired,
    /// every later frame for that event is suppressed. Retirement keys on
    /// set membership, not on a high-water UT, so the suppression holds
    /// even when a loop pass re-enters at or above the prior pass's
    /// last-emitted UT. The only residual is a ghost that stays hidden
    /// through a recording's entire pre-event region <i>and</i> into the
    /// event window on a loop pass — that loop re-emits a partial tail,
    /// then self-heals on the next loop.</para>
    ///
    /// <para><b>Reset on session boundaries.</b> Cached structural-event UT
    /// lists and per-ghost trace state (including the completed-event set)
    /// must be cleared on scene exit / save load / DestroyAllGhosts so a
    /// re-spawned ghost does not inherit a stale "previous frame" pose for
    /// delta computation or a stale completed-event set.</para>
    /// </summary>
    internal static class PlaybackTrace
    {
        /// <summary>
        /// How long after a structural-event UT the trace gate stays open.
        /// Five seconds covers the visually interesting separation window
        /// (decouple + immediate physics divergence + parachute / engine
        /// startup) without bleeding into the steady cruise phase.
        /// </summary>
        internal const double PostEventWindowSeconds = 5.0;

        /// <summary>
        /// Per-recording cache of UTs (sorted ascending) at which the
        /// trajectory has a <see cref="TrajectoryPointFlags.StructuralEventSnapshot"/>
        /// flagged point. Built lazily on first lookup per recording id.
        /// </summary>
        private static readonly Dictionary<string, List<double>> structuralEventUTCache
            = new Dictionary<string, List<double>>();

        private sealed class TraceState
        {
            // NaN sentinels mean "no emitted frame yet" — distinct from a
            // genuine UT of 0 (sandbox-epoch recordings start at UT 0).
            public double lastEmittedUT = double.NaN;
            public Vector3 lastRenderedPos;
            public int lastSectionIdx;
            // UT of the structural event whose window the last frame (emitted
            // OR skipped) belonged to. Drives the transition detection that
            // retires an event into completedEventUTs.
            public double lastTracedEventUT = double.NaN;
            // Structural-event UTs whose post-event window has already been
            // traced once for this (recordingId, ghostIdx). A looping
            // recording re-enters the same event window every loop; once an
            // event UT is in this set, every later frame for it is suppressed
            // until Reset() — one trace per unique event is enough to
            // diagnose separation jitter, and the set (not a high-water UT
            // comparison) is what makes the suppression hold even when a
            // loop pass re-enters at or above the previous high-water.
            public readonly HashSet<double> completedEventUTs = new HashSet<double>();
        }

        /// <summary>
        /// Per-(recordingId, ghostIdx) trace cursor. Nested by recording id
        /// then ghost index so the per-frame lookup never allocates a
        /// composite string key — the gate-closed path (the common case
        /// during cruise) does this lookup on every frame to retire events
        /// whose window has aged out.
        /// </summary>
        private static readonly Dictionary<string, Dictionary<int, TraceState>> traceStates
            = new Dictionary<string, Dictionary<int, TraceState>>();

        private static readonly List<double> EmptyEventList = new List<double>(0);

        /// <summary>
        /// Clears all cached structural-event UTs and per-ghost trace
        /// cursors. Call on scene exit, DestroyAllGhosts, and save load
        /// so a re-spawned ghost computes its first delta against its
        /// current frame, not against a stale pose from a prior session.
        /// </summary>
        internal static void Reset()
        {
            structuralEventUTCache.Clear();
            traceStates.Clear();
        }

        private static TraceState TryGetTraceState(string recId, int ghostIdx)
        {
            return traceStates.TryGetValue(recId, out Dictionary<int, TraceState> byGhost)
                && byGhost.TryGetValue(ghostIdx, out TraceState state)
                ? state
                : null;
        }

        private static TraceState GetOrCreateTraceState(string recId, int ghostIdx)
        {
            if (!traceStates.TryGetValue(recId, out Dictionary<int, TraceState> byGhost))
            {
                byGhost = new Dictionary<int, TraceState>();
                traceStates[recId] = byGhost;
            }
            if (!byGhost.TryGetValue(ghostIdx, out TraceState state))
            {
                state = new TraceState();
                byGhost[ghostIdx] = state;
            }
            return state;
        }

        /// <summary>
        /// Returns true when <paramref name="currentUT"/> is within
        /// <see cref="PostEventWindowSeconds"/> of a structural-event
        /// point in <paramref name="traj"/>'s <c>Points</c> list. Pure
        /// gate — no log side-effects.
        /// </summary>
        internal static bool IsInPostStructuralEventWindow(
            IPlaybackTrajectory traj, double currentUT)
        {
            if (traj == null) return false;
            string recId = traj.RecordingId;
            if (string.IsNullOrEmpty(recId)) return false;

            List<double> events = GetOrBuildStructuralEventUTs(traj);
            if (events == null || events.Count == 0) return false;

            // Largest event UT ≤ currentUT via binary search. List<T>.BinarySearch
            // returns ~insertionIndex when no exact match; insertionIndex - 1 is
            // the largest entry strictly less than the search key.
            int idx = events.BinarySearch(currentUT);
            if (idx < 0) idx = ~idx - 1;
            if (idx < 0) return false; // currentUT before any flagged event

            double mostRecentEventUT = events[idx];
            return (currentUT - mostRecentEventUT) <= PostEventWindowSeconds;
        }

        private static List<double> GetOrBuildStructuralEventUTs(IPlaybackTrajectory traj)
        {
            string recId = traj.RecordingId;
            if (structuralEventUTCache.TryGetValue(recId, out List<double> cached))
                return cached;

            List<double> events = null;
            var points = traj.Points;
            if (points != null)
            {
                for (int i = 0; i < points.Count; i++)
                {
                    if (((TrajectoryPointFlags)points[i].flags
                        & TrajectoryPointFlags.StructuralEventSnapshot) != 0)
                    {
                        if (events == null) events = new List<double>(2);
                        events.Add(points[i].ut);
                    }
                }
            }
            // Defensive sort: Points is normally appended in UT order, but
            // chain-merged or repaired recordings can interleave samples and
            // BinarySearch requires ascending input.
            if (events != null && events.Count > 1) events.Sort();
            List<double> stored = events ?? EmptyEventList;
            structuralEventUTCache[recId] = stored;
            return stored;
        }

        /// <summary>
        /// Emits a single per-frame trace line if the gate is open. Call
        /// from the engine's per-frame loop AFTER the positioner has placed
        /// the ghost transform — <paramref name="renderedPos"/> should be
        /// the final <c>state.ghost.transform.position</c>. No-op when the
        /// gate is closed (the common case during cruise).
        /// </summary>
        internal static void MaybeEmitFrame(
            IPlaybackTrajectory traj, int ghostIdx, double currentUT,
            Vector3 renderedPos)
        {
            if (traj == null) return;
            string recId = traj.RecordingId;
            if (string.IsNullOrEmpty(recId)) return;

            // Resolve the most-recent structural event UT for the gate
            // and per-event de-dup. Mirrors IsInPostStructuralEventWindow
            // but keeps the resolved value so the loop-replay guards below
            // can key on it.
            List<double> events = GetOrBuildStructuralEventUTs(traj);
            if (events == null || events.Count == 0) return;
            int eIdx = events.BinarySearch(currentUT);
            if (eIdx < 0) eIdx = ~eIdx - 1;
            if (eIdx < 0)
            {
                // currentUT is before every flagged event. If this ghost was
                // tracing an event, the recording has looped back past the
                // event start — an unambiguous wrap signal. Retire whatever
                // event was last traced so the upcoming re-entry of its
                // window is suppressed no matter where the loop's first
                // in-window frame lands (above or below the prior pass's
                // high-water). This closes the loop case where the recording
                // wraps through its pre-event region while the ghost is
                // visible; the only residual is a ghost that stays hidden
                // through the entire pre-event region AND into the window on
                // a loop pass, which self-heals on the next loop.
                TraceState preEventState = TryGetTraceState(recId, ghostIdx);
                if (preEventState != null
                    && !double.IsNaN(preEventState.lastTracedEventUT))
                {
                    preEventState.completedEventUTs.Add(preEventState.lastTracedEventUT);
                }
                return;
            }
            double mostRecentEventUT = events[eIdx];
            bool gateOpen = (currentUT - mostRecentEventUT) <= PostEventWindowSeconds;

            TraceState state = TryGetTraceState(recId, ghostIdx);

            if (!gateOpen)
            {
                // The window for mostRecentEventUT has aged out. If this
                // ghost was tracing exactly that event, retire it now so a
                // later loop pass that re-enters the same window is fully
                // suppressed — including the case where the loop's first
                // in-window frame lands at or above the prior pass's
                // high-water UT (a high-water comparison alone would resume
                // logging the tail there). Idempotent HashSet.Add; no
                // allocation on this common cruise-path branch.
                if (state != null && state.lastTracedEventUT == mostRecentEventUT)
                {
                    state.completedEventUTs.Add(mostRecentEventUT);
                }
                return;
            }

            // Resolve current section (if any) for context. Out-of-range UT
            // returns -1 from FindTrackSectionForUT; we still emit the line
            // with sec=-1 / ref=? so a cruise-then-event ghost is visible.
            int sectionIdx = -1;
            ReferenceFrame sectionFrame = ReferenceFrame.Absolute;
            double sectionStartUT = double.NaN;
            double sectionEndUT = double.NaN;
            var sections = traj.TrackSections;
            if (sections != null && sections.Count > 0)
            {
                sectionIdx = TrajectoryMath.FindTrackSectionForUT(sections, currentUT);
                if (sectionIdx >= 0 && sectionIdx < sections.Count)
                {
                    var s = sections[sectionIdx];
                    sectionFrame = s.referenceFrame;
                    sectionStartUT = s.startUT;
                    sectionEndUT = s.endUT;
                }
            }

            if (state == null)
                state = GetOrCreateTraceState(recId, ghostIdx);

            bool hadPrevFrame = !double.IsNaN(state.lastTracedEventUT);

            // Retire the *previous* event when a different event UT now
            // shows — the prior window is necessarily over. Covers both
            // forward progress to a new event and a loop wrap from a later
            // event back to an earlier one.
            if (hadPrevFrame && state.lastTracedEventUT != mostRecentEventUT)
            {
                state.completedEventUTs.Add(state.lastTracedEventUT);
            }
            // Retire the *current* event when currentUT jumps backwards
            // while still on the same event UT — the recording looped right
            // at the window edge, before any gate-closed frame could retire
            // it through the branch above.
            if (hadPrevFrame
                && state.lastTracedEventUT == mostRecentEventUT
                && currentUT < state.lastEmittedUT)
            {
                state.completedEventUTs.Add(mostRecentEventUT);
            }

            if (state.completedEventUTs.Contains(mostRecentEventUT))
            {
                // Keep lastTracedEventUT current so the transition check
                // above stays correct on the next call, but emit nothing.
                state.lastTracedEventUT = mostRecentEventUT;
                return;
            }

            bool hadPrevEmit = !double.IsNaN(state.lastEmittedUT);
            float deltaMeters = 0f;
            float deltaSpeedMps = 0f;
            bool sectionCrossed = false;
            if (hadPrevEmit)
            {
                deltaMeters = Vector3.Distance(state.lastRenderedPos, renderedPos);
                double dt = currentUT - state.lastEmittedUT;
                if (dt > 1e-6) deltaSpeedMps = (float)(deltaMeters / dt);
                sectionCrossed = sectionIdx != state.lastSectionIdx;
            }

            string sectionRange = double.IsNaN(sectionStartUT)
                ? "[?,?]"
                : "[" + sectionStartUT.ToString("F3", CultureInfo.InvariantCulture)
                    + "," + sectionEndUT.ToString("F3", CultureInfo.InvariantCulture) + "]";

            ParsekLog.Info("PlaybackTrace",
                "rec=" + ShortId(recId)
                + " #" + ghostIdx.ToString(CultureInfo.InvariantCulture)
                + " ut=" + currentUT.ToString("F3", CultureInfo.InvariantCulture)
                + " sec=" + sectionIdx.ToString(CultureInfo.InvariantCulture)
                + " " + sectionRange
                + " ref=" + sectionFrame
                + " worldPos=("
                    + renderedPos.x.ToString("F2", CultureInfo.InvariantCulture)
                    + "," + renderedPos.y.ToString("F2", CultureInfo.InvariantCulture)
                    + "," + renderedPos.z.ToString("F2", CultureInfo.InvariantCulture) + ")"
                + " dM=" + deltaMeters.ToString("F2", CultureInfo.InvariantCulture)
                + " dSpd=" + deltaSpeedMps.ToString("F1", CultureInfo.InvariantCulture)
                + (sectionCrossed ? " sectionCrossed" : ""));

            state.lastEmittedUT = currentUT;
            state.lastRenderedPos = renderedPos;
            state.lastSectionIdx = sectionIdx;
            state.lastTracedEventUT = mostRecentEventUT;
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "<null>";
            return id.Length > 8 ? id.Substring(0, 8) : id;
        }

        // ============ Test seams ============

        /// <summary>Test-only: count of cached recordings (for invariant checks).</summary>
        internal static int CachedRecordingCountForTesting => structuralEventUTCache.Count;

        /// <summary>Test-only: returns the cached structural-event UTs for a recording id, or null.</summary>
        internal static List<double> GetCachedStructuralEventUTsForTesting(string recordingId)
        {
            return recordingId != null
                && structuralEventUTCache.TryGetValue(recordingId, out List<double> uts)
                ? uts
                : null;
        }

        /// <summary>
        /// Test-only: returns the last structural-event UT a trace cursor
        /// recorded for the given (recordingId, ghostIdx), or NaN when no
        /// frame (emitted or skipped) has been seen yet for that pair.
        /// </summary>
        internal static double GetLastTracedEventUTForTesting(string recordingId, int ghostIdx)
        {
            if (string.IsNullOrEmpty(recordingId)) return double.NaN;
            TraceState state = TryGetTraceState(recordingId, ghostIdx);
            return state != null ? state.lastTracedEventUT : double.NaN;
        }

        /// <summary>
        /// Test-only: true when the given structural-event UT has been
        /// retired into the completed set for the (recordingId, ghostIdx)
        /// pair — i.e. further frames for that event are now suppressed.
        /// </summary>
        internal static bool IsEventCompletedForTesting(
            string recordingId, int ghostIdx, double eventUT)
        {
            if (string.IsNullOrEmpty(recordingId)) return false;
            TraceState state = TryGetTraceState(recordingId, ghostIdx);
            return state != null && state.completedEventUTs.Contains(eventUT);
        }
    }
}
