using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// How the active recorder's stops end a recording: the forced stop (scene exit, every tree
    /// commit) emits the terminal engine / RCS / robotic shutdowns from the recorder's own
    /// tracking sets, the same as the normal stops do (todo FORCESTOP-LEAVES-ENGINES-LIT).
    /// </summary>
    [Collection("Sequential")]
    public class RecorderStopEndingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RecorderStopEndingTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            FlightRecorder.StopUTProviderForTesting = () => 500.0;
        }

        public void Dispose()
        {
            FlightRecorder.StopUTProviderForTesting = null;
            Patches.PhysicsFramePatch.ActiveRecorder = null;
            ParsekLog.ResetTestOverrides();
        }

        private static void SetField(FlightRecorder rec, string name, object value)
        {
            typeof(FlightRecorder)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(rec, value);
        }

        private static FlightRecorder BurningRecorder(uint enginePid, uint rcsPid, uint roboticPid)
        {
            var rec = new FlightRecorder();
            rec.IsRecording = true;
            Patches.PhysicsFramePatch.ActiveRecorder = rec;
            SetField(rec, "activeEngineKeys", new HashSet<ulong> { FlightRecorder.EncodeEngineKey(enginePid, 0) });
            SetField(rec, "activeRcsKeys", new HashSet<ulong> { FlightRecorder.EncodeEngineKey(rcsPid, 1) });
            SetField(rec, "activeRoboticKeys", new HashSet<ulong> { FlightRecorder.EncodeEngineKey(roboticPid, 0) });
            SetField(rec, "lastRoboticPosition", new Dictionary<ulong, float>
            {
                { FlightRecorder.EncodeEngineKey(roboticPid, 0), 42f }
            });
            return rec;
        }

        [Fact]
        public void ForceStop_EnginesStillBurning_EmitsTerminalShutdownsAtTheStopUT()
        {
            var rec = BurningRecorder(enginePid: 1001, rcsPid: 2002, roboticPid: 3003);

            rec.ForceStop();

            Assert.False(rec.IsRecording);
            Assert.Contains(rec.PartEvents, e => e.eventType == PartEventType.EngineShutdown
                && e.partPersistentId == 1001 && e.ut == 500.0);
            Assert.Contains(rec.PartEvents, e => e.eventType == PartEventType.RCSStopped
                && e.partPersistentId == 2002 && e.moduleIndex == 1 && e.ut == 500.0);
            Assert.Contains(rec.PartEvents, e => e.eventType == PartEventType.RoboticMotionStopped
                && e.partPersistentId == 3003 && e.value == 42f && e.ut == 500.0);
            Assert.Equal(3, rec.lastEmittedTerminalEvents.Count);
            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("FinalizeRecordingState (force stop): emitted 3 terminal event(s)"));
        }

        [Fact]
        public void ForceStop_NothingRunning_EmitsNothing()
        {
            var rec = new FlightRecorder();
            rec.IsRecording = true;
            Patches.PhysicsFramePatch.ActiveRecorder = rec;

            rec.ForceStop();

            Assert.Empty(rec.PartEvents);
            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("FinalizeRecordingState (force stop): emitted 0 terminal event(s)"));
        }

        // ---- final sample (todo STOP-TAKES-NO-FINAL-SAMPLE) and the stop vessel ----

        [Theory]
        [InlineData(true, false, true, false, null)]
        [InlineData(false, false, true, false, "recorded-vessel-not-found")]
        [InlineData(true, true, true, false, "vessel-destroyed")]
        [InlineData(true, false, false, false, "vessel-unloaded")]
        [InlineData(true, false, true, true, "vessel-packed")]
        public void DecideFinalPhysicsSample_TruthTable(
            bool hasVessel, bool destroyed, bool loaded, bool packed, string expected)
        {
            Assert.Equal(expected,
                FlightRecorder.DecideFinalPhysicsSample(hasVessel, destroyed, loaded, packed));
            // A last sample older than the stop UT does not change the verdict.
            Assert.Equal(expected,
                FlightRecorder.DecideFinalPhysicsSample(hasVessel, destroyed, loaded, packed,
                    lastSampleUT: 495.0, stopUT: 500.0));
        }

        [Theory]
        [InlineData(500.0)]
        [InlineData(501.0)]
        public void DecideFinalPhysicsSample_LastSampleAtOrPastStop_Skips(double lastSampleUT)
        {
            Assert.Equal("already-sampled-at-stop",
                FlightRecorder.DecideFinalPhysicsSample(true, false, true, false,
                    lastSampleUT: lastSampleUT, stopUT: 500.0));
        }

        [Fact]
        public void ForceStop_RecordedVesselGone_LogsTheSkippedFinalSample()
        {
            var rec = new FlightRecorder();
            rec.IsRecording = true;
            Patches.PhysicsFramePatch.ActiveRecorder = rec;

            rec.ForceStop();

            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("FinalizeRecordingState (force stop): final physics sample skipped reason=recorded-vessel-not-found"));
        }

        [Fact]
        public void ForceStop_SampleNotRequested_TakesNoSample()
        {
            var rec = new FlightRecorder();
            rec.IsRecording = true;
            Patches.PhysicsFramePatch.ActiveRecorder = rec;

            rec.ForceStop(sampleFinalPoint: false);

            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("ForceStop: final physics sample not requested"));
            Assert.DoesNotContain(logLines, l => l.Contains("final physics sample skipped"));
            Assert.Empty(rec.Recording);
        }
    }
}
