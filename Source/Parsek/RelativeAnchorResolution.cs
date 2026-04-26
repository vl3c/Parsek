using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Decision helpers for resolving the anchor vessel referenced by a
    /// <see cref="ReferenceFrame.Relative"/> track section during ghost
    /// playback. When the anchor is unresolvable in the current scene -- the
    /// most common cause is a Re-Fly rewind that erased the originally
    /// recorded anchor vessel -- the ghost must be retired (hidden) for the
    /// duration of the relative section rather than left frozen at its last
    /// world position. A freshly spawned ghost has no "last position", so
    /// freezing produced the symptom in bug B (Apr 2026): root=(0,0,0) with a
    /// huge reported distance.
    ///
    /// <para>
    /// Pure static; the resolver delegate is injectable so xUnit tests can
    /// drive both branches without a live KSP scene. Production callers
    /// supply a delegate backed by <c>FlightRecorder.FindVesselByPid</c>.
    /// </para>
    /// </summary>
    internal static class RelativeAnchorResolution
    {
        /// <summary>
        /// Outcome of an anchor-resolution decision.
        /// </summary>
        internal enum Outcome
        {
            /// <summary>The anchor pid resolves to a live vessel in the scene; positioning may proceed.</summary>
            Resolved,

            /// <summary>The anchor pid is zero or unresolvable; the ghost must be retired for this frame.</summary>
            Retired,
        }

        /// <summary>
        /// Decides whether the anchor referenced by a relative track section
        /// is resolvable. Returns <see cref="Outcome.Resolved"/> when the
        /// resolver reports a live vessel; <see cref="Outcome.Retired"/>
        /// otherwise (including when <paramref name="anchorPid"/> is 0).
        /// </summary>
        /// <param name="anchorPid">The recorded anchor pid (TrackSection.anchorVesselId or LoopAnchorVesselId).</param>
        /// <param name="resolver">Resolver returning true iff the pid maps to a live vessel; null = always unresolved.</param>
        internal static Outcome Decide(uint anchorPid, Func<uint, bool> resolver)
        {
            if (anchorPid == 0u) return Outcome.Retired;
            if (resolver == null) return Outcome.Retired;
            return resolver(anchorPid) ? Outcome.Resolved : Outcome.Retired;
        }

        /// <summary>
        /// Per-(recordingIndex, anchorPid) dedupe key used by the in-flight
        /// retirement WARN. Lets multiple recordings share an anchor pid
        /// without sharing the dedupe slot, and lets a single recording's
        /// retired anchor log multiple times across subsequent rewinds (the
        /// dedupe set is cleared on rewind via <see cref="HashSet{T}.Clear"/>).
        /// </summary>
        internal static long DedupeKey(int recordingIndex, uint anchorPid)
        {
            unchecked
            {
                long hi = ((long)(uint)recordingIndex) << 32;
                return hi | anchorPid;
            }
        }

        /// <summary>
        /// Returns the user-facing WARN message used when a relative section's
        /// anchor cannot be resolved. Pure: no ParsekLog side effect; callers
        /// emit at the appropriate site so dedupe state stays local. The
        /// emitted log line carries the literal <c>relative-anchor-retired</c>
        /// keyword to make it greppable from <c>KSP.log</c> and assertable
        /// from xUnit.
        /// </summary>
        internal static string FormatRetiredMessage(
            int recordingIndex,
            string vesselName,
            uint anchorPid,
            string callsite)
        {
            return
                $"relative-anchor-retired: callsite={callsite} recording=#{recordingIndex} " +
                $"vessel=\"{vesselName ?? "(unknown)"}\" anchorPid={anchorPid} -- " +
                "ghost hidden during relative section (anchor unresolvable; common cause: Re-Fly rewind erased the originally recorded anchor vessel)";
        }
    }
}
