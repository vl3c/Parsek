namespace Parsek.TestCommands
{
    /// <summary>
    /// Gloops partial: the thin Unity appliers for the <c>GloopsStart</c> /
    /// <c>GloopsStop</c> verb pair. Every decision is delegated to the pure sibling
    /// <see cref="TestCommandGloopsVerbs"/>; this file only samples ParsekFlight's Gloops
    /// state around the two EXISTING production calls and reports what it observed.
    ///
    /// <para>
    /// NO GLOOPS CODE IS TOUCHED, by ruling. The recorder, its window and its commit path
    /// are exactly what a player's clicks reach - <c>GloopsRecorderUI</c>'s primary button
    /// calls <c>flight.StartGloopsRecording()</c> / <c>flight.StopGloopsRecording()</c>
    /// and this pair calls the same two internal members. That is also why there is no
    /// Discard or Preview verb here: the two coverage cells that had no seam-reachable
    /// producer are the manual LIFECYCLE and the sub-2-point DROP, and both live on the
    /// start/stop pair. A verb per button would be a wider surface than the gap.
    /// </para>
    ///
    /// <para>
    /// WHY THE PRE-CALL SAMPLES ARE TAKEN AT ALL. Both production methods return void and
    /// both can no-op behind a guard that only writes a Warn, so the verdict has to come
    /// from a before/after comparison. Three samples carry it: whether a recorder existed,
    /// whether an active vessel existed, and which recording <c>LastGloopsRecording</c>
    /// named.
    /// </para>
    ///
    /// <para>
    /// WHICH POINT COUNT <c>points=</c> REPORTS, and it is deliberately not one number.
    /// On a COMMIT it is the committed recording's own <c>Points.Count</c>; on a DROP it is
    /// the RECORDER COUNT BEFORE THE CALL, because the drop nulls the recorder and leaves
    /// no recording to read. The pre-call count is NOT "the number the &lt; 2 rule was
    /// applied to" and must not be described as one: production can still ADD a sample
    /// after it (<c>FlightRecorder.FinalizeRecordingState</c> takes a boundary sample when
    /// the vessel is on rails at stop time) and can still REMOVE several
    /// (<c>RecordingStore.CreateRecordingFromFlightData</c> trims leading stationary points
    /// and re-applies the &lt; 2 test to what is left), so a pre-call 1 can commit and a
    /// pre-call 5 can drop. What the two reported numbers are good for is describing the
    /// outcome that HAPPENED, not predicting it.
    /// </para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        // ----- GloopsStart -----
        private void GloopsStartImpl(ParsedCommand cmd)
        {
            ParsekFlight flight = ParsekFlight.Instance;
            if (flight == null)
            {
                // Dispatch guarantees FLIGHT, but the instance can still be absent for a
                // frame around a scene teardown. The StartRecordingImpl row exactly.
                ParsekLog.Warn(Tag, "gloopsstart no-flight-instance");
                SetExecResult("ERROR", null, "no-flight-instance");
                return;
            }

            bool wasRecording = flight.IsGloopsRecording;
            Vessel activeVessel = FlightGlobals.ActiveVessel;
            bool hadActiveVessel = activeVessel != null;
            string vesselName = hadActiveVessel ? activeVessel.vesselName : string.Empty;

            // The production call. Driven UNCONDITIONALLY so its own guards decide and log:
            // pre-checking here would be a second copy of those guards, which is the one
            // thing this pair must not add.
            flight.StartGloopsRecording();

            TestCommandGloopsVerbs.StartOutcome outcome = TestCommandGloopsVerbs.ClassifyStart(
                wasRecording, hadActiveVessel, flight.IsGloopsRecording);

            string reject = TestCommandGloopsVerbs.StartRejectReason(outcome);
            if (reject != null)
            {
                ParsekLog.Warn(Tag, $"gloopsstart rejected reason={reject} "
                    + $"wasRecording={Bool(wasRecording)} hadVessel={Bool(hadActiveVessel)}");
                SetExecResult("REJECTED", null, reject);
                return;
            }

            ParsekLog.Info(Tag, $"gloopsstart started=true vessel={vesselName}");
            SetExecResult("OK", TestCommandGloopsVerbs.BuildStartPayload(vesselName), null);
        }

        // ----- GloopsStop -----
        private void GloopsStopImpl(ParsedCommand cmd)
        {
            ParsekFlight flight = ParsekFlight.Instance;
            if (flight == null)
            {
                ParsekLog.Warn(Tag, "gloopsstop no-flight-instance");
                SetExecResult("ERROR", null, "no-flight-instance");
                return;
            }

            // Presence of the recorder OBJECT, not of an actively-sampling one: production
            // commits a recorder that a vessel switch already auto-stopped, so keying the
            // refusal on IsGloopsRecording would report `no-gloops-recorder` for a call
            // that went on to commit.
            FlightRecorder recorder = flight.GloopsRecorderForUI;
            bool hadRecorder = recorder != null;
            // The recorder count BEFORE the call. Reported only on a DROP, where the
            // recorder is gone and there is no recording to read instead; a commit reports
            // the committed recording's own count below. See the class header for why this
            // number is not the one the < 2 rule was applied to.
            int pointsBefore = recorder?.Recording?.Count ?? 0;
            string beforeId = flight.LastGloopsRecording?.RecordingId;

            flight.StopGloopsRecording();

            string afterId = flight.LastGloopsRecording?.RecordingId;
            bool committedIdChanged = !string.IsNullOrEmpty(afterId) && afterId != beforeId;

            TestCommandGloopsVerbs.StopOutcome outcome =
                TestCommandGloopsVerbs.ClassifyStop(hadRecorder, committedIdChanged);

            string reject = TestCommandGloopsVerbs.StopRejectReason(outcome);
            if (reject != null)
            {
                ParsekLog.Warn(Tag, $"gloopsstop rejected reason={reject}");
                SetExecResult("REJECTED", null, reject);
                return;
            }

            bool committed = outcome == TestCommandGloopsVerbs.StopOutcome.Committed;
            int points = committed
                ? (flight.LastGloopsRecording?.Points?.Count ?? 0)
                : pointsBefore;

            // The DROP token is on the LOG LINE as well as in the payload, and that is
            // load-bearing rather than tidy: a spec's logContracts are regexes over
            // KSP.log, the seam's own exec diagnostic carries no payload, and the response
            // file is not scanned - so a `dropped=too-short` that lived only in the payload
            // could not be gated by the lane whose whole subject it is.
            ParsekLog.Info(Tag, $"gloopsstop committed={Bool(committed)} points={Int(points)} "
                + (committed
                    ? $"recordingId={afterId}"
                    : $"dropped={TestCommandGloopsVerbs.DroppedTooShort}"));
            SetExecResult("OK",
                TestCommandGloopsVerbs.BuildStopPayload(outcome, points, afterId), null);
        }
    }
}
