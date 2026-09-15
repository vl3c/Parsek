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
    /// named. The point count is sampled before the call too, because the commit nulls the
    /// recorder and takes the count with it - and that count is the number the production
    /// &lt; 2 rule was applied to, which is the whole content of the drop.
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
            int points = recorder?.Recording?.Count ?? 0;
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
            ParsekLog.Info(Tag, $"gloopsstop committed={Bool(committed)} points={Int(points)} "
                + $"recordingId={(committed ? afterId : string.Empty)}");
            SetExecResult("OK",
                TestCommandGloopsVerbs.BuildStopPayload(outcome, points, afterId), null);
        }
    }
}
