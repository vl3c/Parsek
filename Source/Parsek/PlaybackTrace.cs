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
    /// <para><b>Reset on session boundaries.</b> Cached structural-event UT
    /// lists and per-ghost trace state must be cleared on scene exit / save
    /// load / DestroyAllGhosts so a re-spawned ghost does not inherit a
    /// stale "previous frame" pose for delta computation.</para>
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

        private struct TraceState
        {
            public double lastEmittedUT;
            public Vector3 lastRenderedPos;
            public int lastSectionIdx;
            // UT of the structural event whose window the last emitted frame
            // belonged to. Used to suppress loop-wraparound re-entry: once
            // the 5-second window for an event has fully played out (or the
            // ghost loops back to before the event), further frames for the
            // same event UT are skipped — one trace per unique event is
            // enough to diagnose separation jitter, and looping showcase
            // recordings would otherwise multiply the INFO line count
            // unboundedly.
            public double lastTracedEventUT;
        }

        /// <summary>
        /// Per-(recordingId, ghostIdx) trace cursor — last emission UT,
        /// last rendered world position (for delta), and last selected
        /// section index (for boundary-cross detection).
        /// </summary>
        private static readonly Dictionary<string, TraceState> traceStates
            = new Dictionary<string, TraceState>();

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
            // but keeps the resolved value so the loop-wraparound guard
            // below can key on it.
            List<double> events = GetOrBuildStructuralEventUTs(traj);
            if (events == null || events.Count == 0) return;
            int eIdx = events.BinarySearch(currentUT);
            if (eIdx < 0) eIdx = ~eIdx - 1;
            if (eIdx < 0) return;
            double mostRecentEventUT = events[eIdx];
            if ((currentUT - mostRecentEventUT) > PostEventWindowSeconds) return;

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

            string stateKey = recId + "|" + ghostIdx.ToString(CultureInfo.InvariantCulture);
            bool hadPrev = traceStates.TryGetValue(stateKey, out TraceState prev);

            // Loop-wraparound dedup: when the gate reopens on the same
            // event UT after the previous frame's window has aged out
            // (or after currentUT jumps backwards on loop restart),
            // suppress further emissions for this event. The first
            // window across the event still emits in full because
            // consecutive frames stay monotonically forward within the
            // 5-second PostEventWindow, so the gap check below remains
            // false until the recording loops.
            if (hadPrev
                && prev.lastTracedEventUT == mostRecentEventUT
                && (currentUT < prev.lastEmittedUT
                    || (currentUT - prev.lastEmittedUT) > PostEventWindowSeconds))
            {
                return;
            }

            float deltaMeters = 0f;
            float deltaSpeedMps = 0f;
            bool sectionCrossed = false;
            if (hadPrev)
            {
                deltaMeters = Vector3.Distance(prev.lastRenderedPos, renderedPos);
                double dt = currentUT - prev.lastEmittedUT;
                if (dt > 1e-6) deltaSpeedMps = (float)(deltaMeters / dt);
                sectionCrossed = sectionIdx != prev.lastSectionIdx;
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

            traceStates[stateKey] = new TraceState
            {
                lastEmittedUT = currentUT,
                lastRenderedPos = renderedPos,
                lastSectionIdx = sectionIdx,
                lastTracedEventUT = mostRecentEventUT,
            };
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
        /// Test-only: returns the last structural-event UT a trace was
        /// emitted for under the given (recordingId, ghostIdx), or NaN
        /// when no trace has been recorded yet for that pair.
        /// </summary>
        internal static double GetLastTracedEventUTForTesting(string recordingId, int ghostIdx)
        {
            if (string.IsNullOrEmpty(recordingId)) return double.NaN;
            string stateKey = recordingId + "|" + ghostIdx.ToString(CultureInfo.InvariantCulture);
            return traceStates.TryGetValue(stateKey, out TraceState state)
                ? state.lastTracedEventUT
                : double.NaN;
        }
    }
}
