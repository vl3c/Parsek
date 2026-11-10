using System;
using System.Collections;
using System.Collections.Generic;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Characterization of the <c>ParsekFlight.DeferredJointBreakCheck</c> coroutine on the live
    /// FLIGHT scene. The coroutine reads <c>FlightGlobals.ActiveVessel</c> and
    /// <c>FlightGlobals.Vessels</c> after its one yield, so its body cannot run headless; the
    /// decision units it calls are unit-tested in <c>DeferredJointBreakCheckUnitsTests</c>.
    ///
    /// Each test drives the REAL coroutine over a synthetic parked split recorder, never the
    /// live one: a fresh <see cref="FlightRecorder"/> marked destroyed, so the resume the
    /// coroutine ends with takes the destroyed-vessel fallback, whose commit returns at once
    /// for a recorder with no stop-time capture. The split-state fields the coroutine's
    /// finally block clears are required empty before the run, so nothing live is lost. The
    /// decouple-only split cell feeds a dead pid to the live crash coalescer and resets the
    /// coalescer in the same frame, before any Update can tick it.
    ///
    /// Category JointBreakCheckCoroutine is pinned by no harness spec; run it from the test
    /// runner (Ctrl+Shift+T) in any FLIGHT scene.
    /// </summary>
    public class JointBreakCheckCoroutineInGameTests
    {
        private const string Category = "JointBreakCheckCoroutine";
        private const uint DeadChildPid = 0x7EAD0B01u;

        [InGameTest(Category = Category, Scene = GameScenes.FLIGHT,
            Description = "DeferredJointBreakCheck with no parked split recorder logs the skip and "
                + "clears every split-state field in its finally block")]
        public IEnumerator JointBreakCheck_NoPendingSplitRecorder_ClearsSplitState()
        {
            ParsekFlight flight = ParsekFlight.Instance;
            if (!TrySkipUnlessQuiet(flight, needQuietCoalescer: false, out string skipReason))
            {
                InGameAssert.Skip(skipReason);
                yield break;
            }

            var lines = new List<string>();
            Action<string> prevSink = ParsekLog.TestSinkForTesting;
            bool? prevVerbose = ParsekLog.VerboseOverrideForTesting;
            int frames = 0;
            SplitState after = null;
            try
            {
                ParsekLog.TestSinkForTesting = line => lines.Add(line);
                ParsekLog.VerboseOverrideForTesting = true;
                flight.pendingSplitInProgress = true;
                flight.pendingDeferredSplitCheckTrigger = ParsekFlight.DeferredSplitCheckTrigger.JointBreak;
                flight.pendingSplitTriggerUT = 123.0;
                flight.preBreakVesselPids = new HashSet<uint> { DeadChildPid };
                flight.decoupleOriginByNewPid[DeadChildPid] = DeadChildPid + 1u;
                IEnumerator it = flight.DeferredJointBreakCheck();
                while (it.MoveNext())
                {
                    frames++;
                    yield return it.Current;
                }
                after = SplitState.Capture(flight);
            }
            finally
            {
                ParsekLog.TestSinkForTesting = prevSink;
                ParsekLog.VerboseOverrideForTesting = prevVerbose;
                ClearSplitState(flight);
            }

            InGameAssert.AreEqual(1, frames, "the coroutine defers exactly one frame");
            InGameAssert.IsTrue(
                CountContaining(lines, "DeferredJointBreakCheck: no pending split recorder") == 1,
                "the no-recorder skip is logged once");
            InGameAssert.IsTrue(CountContaining(lines, "Deferred split classified") == 0,
                "nothing is classified without a parked recorder");
            after.AssertCleared();
        }

        [InGameTest(Category = Category, Scene = GameScenes.FLIGHT,
            Description = "A decouple-only DeferredJointBreakCheck that finds no new vessel classifies "
                + "WithinSegment, emits a PartDestroyed segment event for each Decoupled event among "
                + "the parked recorder's last five part events, newest first, and resumes the recorder")]
        public IEnumerator JointBreakCheck_DecoupleOnlyNoNewVessel_EmitsWithinSegmentBreakage()
        {
            ParsekFlight flight = ParsekFlight.Instance;
            if (!TrySkipUnlessQuiet(flight, needQuietCoalescer: false, out string skipReason))
            {
                InGameAssert.Skip(skipReason);
                yield break;
            }

            var parked = new FlightRecorder();
            parked.VesselDestroyedDuringRecording = true;
            parked.PartEvents.Add(Event(PartEventType.Decoupled, "outside.window", 101u));
            parked.PartEvents.Add(Event(PartEventType.Decoupled, "outside.window2", 102u));
            parked.PartEvents.Add(Event(PartEventType.EngineIgnited, "engine.a", 103u));
            parked.PartEvents.Add(Event(PartEventType.Decoupled, "dec.a", 104u));
            parked.PartEvents.Add(Event(PartEventType.Decoupled, "dec.b", 105u));
            parked.PartEvents.Add(Event(PartEventType.Decoupled, "dec.c", 106u));
            parked.PartEvents.Add(Event(PartEventType.Decoupled, "dec.d", 107u));

            var lines = new List<string>();
            Action<string> prevSink = ParsekLog.TestSinkForTesting;
            bool? prevVerbose = ParsekLog.VerboseOverrideForTesting;
            int frames = 0;
            SplitState after = null;
            try
            {
                ParsekLog.TestSinkForTesting = line => lines.Add(line);
                ParsekLog.VerboseOverrideForTesting = true;
                flight.pendingSplitInProgress = true;
                flight.pendingSplitRecorder = parked;
                flight.pendingDeferredSplitCheckTrigger =
                    ParsekFlight.DeferredSplitCheckTrigger.DecoupleCreatedVessel;
                IEnumerator it = flight.DeferredJointBreakCheck();
                while (it.MoveNext())
                {
                    frames++;
                    yield return it.Current;
                }
                after = SplitState.Capture(flight);
            }
            finally
            {
                ParsekLog.TestSinkForTesting = prevSink;
                ParsekLog.VerboseOverrideForTesting = prevVerbose;
                ClearSplitState(flight);
            }

            InGameAssert.AreEqual(1, frames, "the coroutine defers exactly one frame");
            InGameAssert.IsTrue(
                CountContaining(lines,
                    "Deferred split classified: source=decouple-only, result=WithinSegment, "
                    + "newVessels=0, hasController=False, recordedPid=0") == 1,
                "the decouple-only classification line is logged once");
            InGameAssert.IsTrue(CountContaining(lines, "Within-segment breakage") == 1,
                "the within-segment branch is taken once");
            InGameAssert.AreEqual(4, parked.SegmentEvents.Count,
                "one PartDestroyed event per Decoupled event among the last five part events");
            string[] expected = { "part=dec.d pid=107", "part=dec.c pid=106", "part=dec.b pid=105",
                "part=dec.a pid=104" };
            for (int i = 0; i < expected.Length; i++)
            {
                InGameAssert.AreEqual(SegmentEventType.PartDestroyed, parked.SegmentEvents[i].type,
                    "segment event " + i + " is PartDestroyed");
                InGameAssert.AreEqual(expected[i], parked.SegmentEvents[i].details,
                    "segment event " + i + " walks the part events newest first");
            }
            InGameAssert.IsTrue(
                CountContaining(lines,
                    "ResumeSplitRecorder: vessel was destroyed (joint break was within-segment "
                    + "(no vessel split))") == 1,
                "the parked recorder is resumed through the destroyed-vessel fallback");
            InGameAssert.IsTrue(CountContaining(lines, "Feeding split to coalescer") == 0,
                "nothing reaches the coalescer");
            after.AssertCleared();
        }

        [InGameTest(Category = Category, Scene = GameScenes.FLIGHT,
            Description = "A decouple-only DeferredJointBreakCheck over a child captured by the "
                + "decouple callback but already destroyed classifies a debris split and feeds the "
                + "child to the coalescer as a DECOUPLE seeded from the callback's trajectory point")]
        public IEnumerator JointBreakCheck_DecoupleOnlyDestroyedChild_FeedsCoalescerAsDecouple()
        {
            ParsekFlight flight = ParsekFlight.Instance;
            if (!TrySkipUnlessQuiet(flight, needQuietCoalescer: true, out string skipReason))
            {
                InGameAssert.Skip(skipReason);
                yield break;
            }

            var parked = new FlightRecorder();
            parked.VesselDestroyedDuringRecording = true;

            var lines = new List<string>();
            Action<string> prevSink = ParsekLog.TestSinkForTesting;
            bool? prevVerbose = ParsekLog.VerboseOverrideForTesting;
            Dictionary<uint, bool> prevStatus = flight.decoupleControllerStatus;
            Dictionary<uint, TrajectoryPoint> prevPoints = flight.decoupleCreatedTrajectoryPoints;
            int frames = 0;
            SplitState after = null;
            bool coalescerPendingAfter;
            int coalescerDebrisAfter;
            try
            {
                ParsekLog.TestSinkForTesting = line => lines.Add(line);
                ParsekLog.VerboseOverrideForTesting = true;
                double seedUT = Planetarium.GetUniversalTime();
                flight.decoupleControllerStatus = new Dictionary<uint, bool> { { DeadChildPid, false } };
                flight.decoupleCreatedTrajectoryPoints = new Dictionary<uint, TrajectoryPoint>
                {
                    { DeadChildPid, new TrajectoryPoint { ut = seedUT, bodyName = "Kerbin" } }
                };
                flight.pendingSplitInProgress = true;
                flight.pendingSplitRecorder = parked;
                flight.pendingDeferredSplitCheckTrigger =
                    ParsekFlight.DeferredSplitCheckTrigger.DecoupleCreatedVessel;
                flight.pendingSplitTriggerUT = seedUT;
                IEnumerator it = flight.DeferredJointBreakCheck();
                while (it.MoveNext())
                {
                    frames++;
                    yield return it.Current;
                }
                after = SplitState.Capture(flight);
                coalescerPendingAfter = flight.crashCoalescer.HasPendingBreakup;
                coalescerDebrisAfter = flight.crashCoalescer.CurrentDebrisCount;
            }
            finally
            {
                flight.crashCoalescer.Reset();
                ParsekLog.TestSinkForTesting = prevSink;
                ParsekLog.VerboseOverrideForTesting = prevVerbose;
                ClearSplitState(flight);
                flight.decoupleControllerStatus = prevStatus;
                flight.decoupleCreatedTrajectoryPoints = prevPoints;
            }

            string pid = DeadChildPid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            InGameAssert.AreEqual(1, frames, "the coroutine defers exactly one frame");
            InGameAssert.IsTrue(
                CountContaining(lines, "DeferredJointBreakCheck: 1 vessel(s) caught by onPartDeCouple") == 1,
                "the synchronously captured child is counted");
            InGameAssert.IsTrue(
                CountContaining(lines, "DeferredJointBreakCheck: pid=" + pid
                    + " captured synchronously but Vessel reference is destroyed") == 1,
                "the destroyed child is reported once");
            InGameAssert.IsTrue(
                CountContaining(lines,
                    "Deferred split classified: source=decouple-only, result=DebrisSplit, "
                    + "newVessels=1, hasController=False, recordedPid=0") == 1,
                "the decouple-only debris classification line is logged once");
            InGameAssert.IsTrue(
                CountContaining(lines,
                    "Feeding split to coalescer: childPid=" + pid + ", childHasController=False, "
                    + "cause=DECOUPLE, decoupleCreated=True, triggerDecoupleOnly=True, decouplerPartId=0, "
                    + "preSnapshot=False, preTrajectoryPoint=True pointSource=decouple-callback branchUT=") == 1,
                "the child is fed to the coalescer as a decouple seeded from the callback point");
            InGameAssert.IsTrue(coalescerPendingAfter, "the coalescer opened a breakup window");
            InGameAssert.AreEqual(1, coalescerDebrisAfter, "the coalescer counts the child as debris");
            InGameAssert.IsTrue(
                CountContaining(lines,
                    "ResumeSplitRecorder: vessel was destroyed (joint break fed to coalescer "
                    + "(classification=DebrisSplit))") == 1,
                "the parked recorder is resumed through the destroyed-vessel fallback");
            InGameAssert.IsTrue(CountContaining(lines, "handing focus over after split") == 0,
                "the recorded pid is unknown, so focus is not handed over");
            after.AssertCleared();
        }

        private static PartEvent Event(PartEventType type, string partName, uint partPid)
        {
            return new PartEvent
            {
                ut = 0.0,
                partPersistentId = partPid,
                eventType = type,
                partName = partName,
            };
        }

        private static bool TrySkipUnlessQuiet(ParsekFlight flight, bool needQuietCoalescer, out string reason)
        {
            reason = null;
            if (flight == null)
                reason = "needs ParsekFlight.Instance";
            else if (FlightGlobals.ActiveVessel == null)
                reason = "needs an active vessel";
            else if (flight.pendingSplitInProgress || flight.pendingSplitRecorder != null)
                reason = "a real split check is in progress; not touching it";
            else if (flight.preBreakVesselPids != null)
                reason = "a real pre-break vessel snapshot is armed";
            else if (flight.pendingDeferredSplitCheckTrigger != ParsekFlight.DeferredSplitCheckTrigger.None)
                reason = "a deferred split check trigger is armed";
            else if ((flight.decoupleControllerStatus != null && flight.decoupleControllerStatus.Count > 0)
                || (flight.decoupleCreatedVessels != null && flight.decoupleCreatedVessels.Count > 0)
                || (flight.decoupleCreatedTrajectoryPoints != null && flight.decoupleCreatedTrajectoryPoints.Count > 0)
                || flight.decoupleOriginByNewPid.Count > 0)
                reason = "decouple-captured vessels are waiting for a real split check";
            else if (needQuietCoalescer
                && (flight.crashCoalescer == null || flight.crashCoalescer.HasPendingBreakup))
                reason = "the crash coalescer has a real breakup window open";
            return reason == null;
        }

        private static void ClearSplitState(ParsekFlight flight)
        {
            flight.pendingSplitInProgress = false;
            flight.pendingSplitRecorder = null;
            flight.pendingDeferredSplitCheckTrigger = ParsekFlight.DeferredSplitCheckTrigger.None;
            flight.pendingSplitTriggerUT = double.NaN;
            flight.preBreakVesselPids = null;
            flight.decoupleOriginByNewPid.Clear();
        }

        private sealed class SplitState
        {
            private bool inProgress;
            private bool hasParkedRecorder;
            private ParsekFlight.DeferredSplitCheckTrigger trigger;
            private double triggerUT;
            private bool hasPreBreakPids;
            private int controllerStatusCount;
            private int trajectoryPointCount;
            private int createdVesselCount;
            private int originCount;

            internal static SplitState Capture(ParsekFlight flight)
            {
                return new SplitState
                {
                    inProgress = flight.pendingSplitInProgress,
                    hasParkedRecorder = flight.pendingSplitRecorder != null,
                    trigger = flight.pendingDeferredSplitCheckTrigger,
                    triggerUT = flight.pendingSplitTriggerUT,
                    hasPreBreakPids = flight.preBreakVesselPids != null,
                    controllerStatusCount = flight.decoupleControllerStatus?.Count ?? 0,
                    trajectoryPointCount = flight.decoupleCreatedTrajectoryPoints?.Count ?? 0,
                    createdVesselCount = flight.decoupleCreatedVessels?.Count ?? 0,
                    originCount = flight.decoupleOriginByNewPid.Count,
                };
            }

            internal void AssertCleared()
            {
                InGameAssert.IsFalse(inProgress, "pendingSplitInProgress is cleared");
                InGameAssert.IsFalse(hasParkedRecorder, "the parked split recorder is released");
                InGameAssert.AreEqual(ParsekFlight.DeferredSplitCheckTrigger.None, trigger,
                    "the deferred split check trigger is reset");
                InGameAssert.IsTrue(double.IsNaN(triggerUT), "the split trigger UT is reset to NaN");
                InGameAssert.IsFalse(hasPreBreakPids, "the pre-break vessel snapshot is dropped");
                InGameAssert.AreEqual(0, controllerStatusCount, "the decouple controller status is emptied");
                InGameAssert.AreEqual(0, trajectoryPointCount, "the decouple trajectory points are emptied");
                InGameAssert.AreEqual(0, createdVesselCount, "the decouple-created vessels are emptied");
                InGameAssert.AreEqual(0, originCount, "the decouple origin map is emptied");
            }
        }

        private static int CountContaining(List<string> lines, string text)
        {
            int n = 0;
            for (int i = 0; i < lines.Count; i++)
                if (lines[i] != null && lines[i].Contains(text))
                    n++;
            return n;
        }
    }
}
