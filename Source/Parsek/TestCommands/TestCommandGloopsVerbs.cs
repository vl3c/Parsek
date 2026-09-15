using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure decision + payload half of the <c>GloopsStart</c> / <c>GloopsStop</c> seam
    /// verb pair (M-A2). The Unity applier samples ParsekFlight's Gloops state around
    /// the EXISTING production calls (<c>ParsekFlight.StartGloopsRecording</c> /
    /// <c>StopGloopsRecording</c>, which both return void) and feeds the sampled
    /// booleans here.
    ///
    /// <para>
    /// WHY THIS PAIR EXISTS. The Gloops ghost-only recorder is the only producer in
    /// Parsek for two coverage cells that nothing unattended could reach: D1
    /// <c>manual-gloops</c> (the manual, career-invisible recorder lifecycle, which is
    /// NOT the auto-record lifecycle <c>StartRecording</c> drives) and D1
    /// <c>sub-2-point-drop</c> (a finalized recording with fewer than two trajectory
    /// points, which <c>RecordingStore.CreateRecordingFromFlightData</c> refuses to
    /// build). Its three buttons live in one window whose open flag is only ever
    /// written by a player click, so the entire path was seam-unreachable.
    ///
    /// <para>SCOPED HONESTLY on the second cell: this pair is the only seam VERB whose
    /// SUBJECT is the drop, not the only producer of it. The dock/undock chain-segment
    /// path reaches the same factory and is live - <c>ParsekFlight
    /// .HandleDockUndockCommitRestart</c> -&gt; <c>ChainSegmentManager
    /// .CommitDockUndockSegment</c> -&gt; <c>CommitSegmentCore</c>, no always-tree guard
    /// on that chain - and logs its own "segment too short" instead.</para>
    /// </para>
    ///
    /// <para>
    /// SCOPE. The pair drives the EXISTING entry points exactly as they are and adds no
    /// rule of its own. Every refusal below is a READ-BACK of what the production guard
    /// already decided (and already logged its own Warn for), never a second copy of
    /// that guard evaluated ahead of the call: <see cref="ClassifyStart"/> is handed
    /// what the world looked like before and after, and names the outcome. The
    /// sub-2-point DROP is deliberately NOT a refusal - the button behaves identically,
    /// and <c>CommitGloopsRecorderData</c> logs
    /// <c>StopGloopsRecording: not enough points (&lt; 2)</c> and discards - so it
    /// terminates OK with <c>committed=false</c>, and the production log line is what a
    /// lane gates on. That one Warn covers BOTH of the factory's refusals (the raw
    /// &lt; 2 test and the post-trim one), because it is written by the ParsekFlight
    /// caller after the factory returns null rather than at either test.
    /// </para>
    ///
    /// <para>
    /// BOTH VERBS ARE SINGLE-PHASE, and neither is a borderline call. The recorder
    /// attaches to the physics-frame patch INSIDE
    /// <c>FlightRecorder.StartRecording</c> (<c>PhysicsFramePatch.GloopsRecorderInstance
    /// = this</c>), so a read-back of <c>IsGloopsRecording</c> taken the instant the
    /// call returns is a final answer, not a value written a frame ago; and the stop
    /// half stops, builds, commits (or drops) and nulls the recorder inside one
    /// synchronous call. This is the SimulateStockSwitchClick / map-view row: nothing a
    /// later frame could change, so there is no settle to hold the head for. What a
    /// later frame DOES change is the recorder's POINT COUNT (sampling happens on the
    /// physics frame, gated by the density preset's max sample interval), and that is a
    /// property of the flight between the two verbs - the spec's business, not a
    /// completion criterion.
    /// </para>
    /// </summary>
    internal static class TestCommandGloopsVerbs
    {
        /// <summary>What the world did when <c>StartGloopsRecording</c> was driven.</summary>
        internal enum StartOutcome
        {
            /// <summary>The Gloops recorder went live.</summary>
            Started,

            /// <summary>A Gloops recorder was ALREADY sampling; production no-ops and warns
            /// (<c>StartGloopsRecording called while already recording</c>). No second
            /// recorder was forced.</summary>
            AlreadyRecording,

            /// <summary>No active vessel (production warns
            /// <c>StartGloopsRecording: no active vessel</c>).</summary>
            NoActiveVessel,

            /// <summary>The underlying <c>FlightRecorder.StartRecording</c> refused - paused,
            /// or the vessel was not recordable - and production cleared the recorder again
            /// (<c>StartGloopsRecording blocked (paused or no vessel)</c>).</summary>
            StartBlocked,
        }

        /// <summary>What the world did when <c>StopGloopsRecording</c> was driven.</summary>
        internal enum StopOutcome
        {
            /// <summary>The take was committed as a ghost-only recording.</summary>
            Committed,

            /// <summary>The take finalized to fewer than two trajectory points, so
            /// <c>CommitGloopsRecorderData</c> discarded it. NOT a refusal: the window's
            /// Stop button does exactly this.</summary>
            Dropped,

            /// <summary>There was no Gloops recorder to stop (production warns
            /// <c>StopGloopsRecording: no Gloops recorder</c>).</summary>
            NoRecorder,
        }

        // Refusal reason tokens. Grep-stable and verb-scoped; each one NAMES the
        // production guard whose decision it reports, so a spec author reading a
        // REJECTED line can find the Warn that produced it.
        internal const string AlreadyRecordingReason = "gloops-already-recording";
        internal const string NoActiveVesselReason = "gloops-no-active-vessel";
        internal const string StartBlockedReason = "gloops-start-blocked";
        internal const string NoRecorderReason = "no-gloops-recorder";

        /// <summary>The <c>dropped=</c> token on a sub-2-point stop. One spelling, used by
        /// the payload builder and pinned by a unit cell, because a lane reads it.</summary>
        internal const string DroppedTooShort = "too-short";

        /// <summary>
        /// Classify a driven <c>StartGloopsRecording</c> from the state sampled AROUND the
        /// call. Pure read-back, in the production method's own decision order:
        /// already-recording is checked first because that branch returns before touching
        /// anything, then the successful case, then the two ways the call can come back
        /// with no live recorder.
        /// </summary>
        internal static StartOutcome ClassifyStart(
            bool wasRecording, bool hadActiveVessel, bool isRecordingAfter)
        {
            if (wasRecording) return StartOutcome.AlreadyRecording;
            if (isRecordingAfter) return StartOutcome.Started;
            if (!hadActiveVessel) return StartOutcome.NoActiveVessel;
            return StartOutcome.StartBlocked;
        }

        /// <summary>
        /// Classify a driven <c>StopGloopsRecording</c>. <paramref name="hadRecorder"/> is
        /// the pre-call presence of the recorder OBJECT (not of an actively-sampling one:
        /// production accepts a recorder auto-stopped by a vessel switch and still commits
        /// it, so keying on <c>IsGloopsRecording</c> alone would report a refusal for a
        /// call that committed). <paramref name="committedIdChanged"/> is true when
        /// <c>LastGloopsRecording</c> names a DIFFERENT recording after the call than
        /// before it - the only observable that separates a commit from a drop, since both
        /// null the recorder.
        /// </summary>
        internal static StopOutcome ClassifyStop(bool hadRecorder, bool committedIdChanged)
        {
            if (!hadRecorder) return StopOutcome.NoRecorder;
            return committedIdChanged ? StopOutcome.Committed : StopOutcome.Dropped;
        }

        /// <summary>The REJECTED reason for a non-Started start outcome; null for Started.</summary>
        internal static string StartRejectReason(StartOutcome outcome)
        {
            switch (outcome)
            {
                case StartOutcome.AlreadyRecording: return AlreadyRecordingReason;
                case StartOutcome.NoActiveVessel: return NoActiveVesselReason;
                case StartOutcome.StartBlocked: return StartBlockedReason;
                default: return null;
            }
        }

        /// <summary>The REJECTED reason for a non-terminal stop outcome; null when the verb
        /// terminates OK (BOTH Committed and Dropped do - see the class header).</summary>
        internal static string StopRejectReason(StopOutcome outcome)
        {
            return outcome == StopOutcome.NoRecorder ? NoRecorderReason : null;
        }

        /// <summary>
        /// GloopsStart OK payload: <c>started=true</c> plus the vessel the recorder bound
        /// to, so a reading run can tell which craft a take belongs to without cross-
        /// referencing the log.
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildStartPayload(string vesselName)
        {
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("started", "true"),
                new KeyValuePair<string, string>("vessel", vesselName ?? string.Empty),
            };
        }

        /// <summary>
        /// GloopsStop OK payload. <c>committed</c> is the split; <c>recordingId</c> is
        /// present only on a commit and <c>dropped=too-short</c> only on a drop.
        ///
        /// <para><c>points</c> is supplied by the caller and means two different things by
        /// design: on a COMMIT, the committed recording's own <c>Points.Count</c>; on a
        /// DROP, the RECORDER COUNT BEFORE THE CALL, because the drop leaves no recording
        /// to read. Neither is "the number the &lt; 2 rule was applied to" - production can
        /// ADD a boundary sample after the pre-call reading
        /// (<c>FlightRecorder.FinalizeRecordingState</c> when the vessel is on rails at
        /// stop time) and can REMOVE several before the second test
        /// (<c>CreateRecordingFromFlightData</c> trims leading stationary points), so a
        /// pre-call 1 can commit and a pre-call 5 can drop.</para>
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildStopPayload(
            StopOutcome outcome, int points, string recordingId)
        {
            bool committed = outcome == StopOutcome.Committed;
            var p = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("committed", committed ? "true" : "false"),
                new KeyValuePair<string, string>(
                    "points", points.ToString(CultureInfo.InvariantCulture)),
            };
            if (committed)
                p.Add(new KeyValuePair<string, string>("recordingId", recordingId ?? string.Empty));
            else
                p.Add(new KeyValuePair<string, string>("dropped", DroppedTooShort));
            return p;
        }
    }
}
