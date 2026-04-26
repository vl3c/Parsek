using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Decision helpers for resolving the anchor vessel referenced by a
    /// <see cref="ReferenceFrame.Relative"/> track section during ghost
    /// playback. When the live anchor is not a safe frame source -- either it
    /// is gone, or it is the vessel currently being Re-Flown -- playback should
    /// use the recorded anchor trajectory as the ground-frame source. Only if
    /// that recorded pose is unavailable should the ghost be retired (hidden)
    /// for the duration of the relative section rather than left frozen at its
    /// last world position.
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
        /// Frame source selected for a relative-section anchor.
        /// </summary>
        internal enum AnchorFrameSource
        {
            /// <summary>Use the currently loaded live vessel as the anchor.</summary>
            Live,

            /// <summary>Use an exact recorded anchor pose at the requested UT.</summary>
            Recorded,

            /// <summary>Use a recorded endpoint/bracket fallback because exact coverage is unavailable.</summary>
            RecordedFallback,

            /// <summary>No safe anchor pose exists; hide the ghost for this frame.</summary>
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
        /// Returns true when a relative section's live anchor pid is the active
        /// Re-Fly target and therefore must not drive another recording's ghost.
        /// Those ghosts should be reconstructed from the recorded anchor
        /// trajectory (or the v7 absolute shadow) so their path remains
        /// ground-relative instead of becoming locked to the player's current
        /// Re-Fly vessel.
        ///
        /// <para>
        /// The bypass is unconditional on the victim's topological relationship
        /// to the active Re-Fly target: parent chains, sibling chains, and
        /// cousin chains can all carry sections anchored to the same physical
        /// vessel that is now being re-flown. Example from KSP.log
        /// 2026-04-26: recording <c>a0d14b08</c> (Kerbal X upper stage) is a
        /// sibling chain of the re-flown probe (<c>f3f1f2e6</c>), but its
        /// post-rail-exit Relative sections are anchored to the probe's PID.
        /// Without bypassing for sibling-chain victims, those sections decode
        /// against the player's live probe pose and produce sub-surface jumps
        /// of hundreds of metres. The earlier parent-chain-only gate was a
        /// missed-case from PR #594; the only legitimate exclusion is the
        /// active Re-Fly recording itself (the live vessel is correct for
        /// that case by definition).
        /// </para>
        ///
        /// <para>
        /// <paramref name="victimIsParentOfActiveReFly"/> is retained as an
        /// observability hint on the call site logs (parent-chain bypasses
        /// are still the most common case and worth distinguishing in
        /// telemetry); it does not gate the decision.
        /// </para>
        /// </summary>
        internal static bool ShouldBypassLiveAnchorForActiveReFly(
            uint anchorPid,
            uint activeReFlyPid,
            string victimRecordingId,
            string activeReFlyRecordingId,
            bool victimIsParentOfActiveReFly)
        {
            // victimIsParentOfActiveReFly is intentionally unused for the
            // gate — see the docstring for why.
            _ = victimIsParentOfActiveReFly;
            if (anchorPid == 0u || activeReFlyPid == 0u)
                return false;
            if (anchorPid != activeReFlyPid)
                return false;
            if (string.IsNullOrEmpty(victimRecordingId)
                || string.IsNullOrEmpty(activeReFlyRecordingId))
                return false;
            return !string.Equals(
                victimRecordingId,
                activeReFlyRecordingId,
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Pure source-selection helper for relative anchor playback. Active
        /// in-place Re-Fly parent-chain victims must never fall through to the
        /// live anchor: if recorded coverage is partial they may use the
        /// recorded fallback, and if even that is unavailable they retire.
        /// Unrelated anchors keep the older behavior: live anchor first, then
        /// exact recorded coverage only.
        /// </summary>
        internal static AnchorFrameSource SelectAnchorFrameSource(
            bool liveAnchorAvailable,
            bool bypassLiveAnchorForActiveReFly,
            bool recordedAnchorAvailable,
            bool recordedFallbackAvailable)
        {
            if (bypassLiveAnchorForActiveReFly)
            {
                if (recordedAnchorAvailable)
                    return AnchorFrameSource.Recorded;
                return recordedFallbackAvailable
                    ? AnchorFrameSource.RecordedFallback
                    : AnchorFrameSource.Retired;
            }

            if (liveAnchorAvailable)
                return AnchorFrameSource.Live;
            return recordedAnchorAvailable
                ? AnchorFrameSource.Recorded
                : AnchorFrameSource.Retired;
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

        /// <summary>
        /// Pure decision used by <see cref="GhostPlaybackEngine"/> after the
        /// relative-frame positioner runs to decide whether to skip the
        /// post-position pipeline (transient visual events,
        /// <c>ActivateGhostVisualsIfNeeded</c>, and <c>TrackGhostAppearance</c>).
        /// Returning true means the ghost was retired this frame and must
        /// stay hidden -- the engine still calls <c>ApplyFrameVisuals</c> with
        /// <c>skipPartEvents=true</c> and <c>suppressVisualFx=true</c> so any
        /// previously-emitting plumes/audio get cleanly stopped, but no new
        /// transient events fire from the stale (0,0,0) transform.
        ///
        /// <para>
        /// PR #594 P1 review surfaced the integration bug: without this gate,
        /// <c>ActivateGhostVisualsIfNeeded</c> unconditionally called
        /// <c>SetActive(true)</c> the same frame the positioner had hidden
        /// the ghost, defeating the retirement and re-rendering at the stale
        /// (0,0,0) transform. Lifting the gate into a named pure predicate
        /// makes the engine call site reviewable from xUnit.
        /// </para>
        /// </summary>
        /// <param name="anchorRetiredThisFrame">
        /// The per-state flag set by the relative positioner's retire branch;
        /// cleared by the engine before each frame's positioning step.
        /// </param>
        internal static bool ShouldSkipPostPositionPipeline(bool anchorRetiredThisFrame)
        {
            return anchorRetiredThisFrame;
        }
    }
}
