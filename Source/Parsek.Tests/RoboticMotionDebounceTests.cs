using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The robotic transition machine must emit few events (CLAUDE.md "Visual and recording design
    /// principle": threshold crossings, debounced). Three review findings on the live servo / rotor
    /// reads (todo RECORDER-ROBOTIC-MOTION-DEBOUNCE):
    /// a slow servo (below ~6 deg/s) flooded the stream with Started / Stopped pairs every 2-3
    /// physics frames; a rotor spinning down could end "moving" at a fraction of an RPM because the
    /// value inference was OR'd with the spin signal; and the fixed 0.5 RPM deadband turned a
    /// hovering rotor's normal wobble into a 4 Hz sample stream.
    /// Every cell drives the same per-module poll both recorders run.
    /// </summary>
    [Collection("Sequential")]
    public class RoboticMotionDebounceTests : IDisposable
    {
        private const double FrameSeconds = 0.02;
        private const double SampleIntervalSeconds = 0.25;
        private const float AngularDeadband = 0.5f;

        private readonly List<string> logLines = new List<string>();

        public RoboticMotionDebounceTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        /// <summary>One recorder's per-module robotic state and its poll, in production order.</summary>
        private sealed class Poll
        {
            private readonly HashSet<ulong> moving = new HashSet<ulong>();
            private readonly Dictionary<ulong, float> lastPosition = new Dictionary<ulong, float>();
            private readonly Dictionary<ulong, double> lastSample = new Dictionary<ulong, double>();
            private readonly Dictionary<ulong, FlightRecorder.RoboticMotionWindow> windows =
                new Dictionary<ulong, FlightRecorder.RoboticMotionWindow>();
            private readonly string moduleName;
            private readonly string sourceField;
            private readonly ulong key;
            internal readonly List<PartEvent> Events = new List<PartEvent>();

            internal Poll(string moduleName, string sourceField)
            {
                this.moduleName = moduleName;
                this.sourceField = sourceField;
                key = FlightRecorder.EncodeEngineKey(500u, 0);
            }

            internal bool IsMoving => moving.Contains(key);

            internal void Frame(double ut, float value, float deadband = AngularDeadband)
            {
                Events.AddRange(FlightRecorder.EvaluateRoboticPoll(
                    key, 500u, 0, "part", moduleName, sourceField,
                    hasModuleFlag: false, moduleFlag: false,
                    positionValue: value, deadband: deadband, ut: ut,
                    movingSet: moving, lastPositionMap: lastPosition, lastSampleMap: lastSample,
                    motionWindows: windows, sampleIntervalSeconds: SampleIntervalSeconds,
                    movingSource: out _, sampleDeadband: out _));
            }

            internal int Count(PartEventType type) => Events.Count(e => e.eventType == type);
        }

        private static Poll Hinge() => new Poll("ModuleRoboticServoHinge", "servoTransformAngle");
        private static Poll Rotor() => new Poll("ModuleRoboticServoRotor", "servoRateOfMotion");

        // ---- 1. slow servo motion ----

        [Theory]
        [InlineData(0.1f)]  // 5 deg/s: the flood case, a Started/Stopped pair every 2-3 frames
        [InlineData(0.2f)]  // 10 deg/s
        [InlineData(0.05f)] // 2.5 deg/s
        public void SlowSteadyServo_EmitsOneStart_CappedSamples_OneStop(float stepPerFrame)
        {
            var poll = Hinge();
            double ut = 100.0;
            float value = 0f;
            for (int i = 0; i < 500; i++, ut += FrameSeconds)
            {
                poll.Frame(ut, value);
                value += stepPerFrame;
            }
            Assert.True(poll.IsMoving, "a servo still sweeping must read moving");

            // Then it arrives and holds.
            for (int i = 0; i < 50; i++, ut += FrameSeconds)
                poll.Frame(ut, value);

            Assert.Equal(1, poll.Count(PartEventType.RoboticMotionStarted));
            Assert.Equal(1, poll.Count(PartEventType.RoboticMotionStopped));
            int samples = poll.Count(PartEventType.RoboticPositionSample);
            // 10 s of sweep at the 4 Hz cap is at most 41 samples.
            Assert.InRange(samples, 1, 41);
            PartEvent stopped = poll.Events.Last();
            Assert.Equal(PartEventType.RoboticMotionStopped, stopped.eventType);
            Assert.Equal(value, stopped.value, 0.001f);
            Assert.False(poll.IsMoving);
        }

        [Fact]
        public void ServoSettlingTowardItsTarget_EmitsOneStartAndOneStop()
        {
            // Stock's drive eases in to the target: the per-frame step shrinks geometrically, which
            // walked the old machine through a long tail of Started / Stopped pairs.
            var poll = Hinge();
            double ut = 100.0;
            for (int i = 0; i < 400; i++, ut += FrameSeconds)
            {
                double t = i * FrameSeconds;
                float value = (float)(45.0 - 45.0 * Math.Exp(-t / 0.8));
                poll.Frame(ut, value);
            }

            Assert.Equal(1, poll.Count(PartEventType.RoboticMotionStarted));
            Assert.Equal(1, poll.Count(PartEventType.RoboticMotionStopped));
            Assert.InRange(poll.Count(PartEventType.RoboticPositionSample), 1, 32);
            Assert.Equal(PartEventType.RoboticMotionStopped, poll.Events.Last().eventType);
            Assert.True(Math.Abs(poll.Events.Last().value - 45f) <= AngularDeadband,
                "the stop must record the settled pose, got " + poll.Events.Last().value);
        }

        [Fact]
        public void ServoCreepingBelowTheDeadband_StaysSilent()
        {
            // 0.3 deg over 10 s is not a visible motion: nothing to record.
            var poll = Hinge();
            double ut = 100.0;
            for (int i = 0; i < 500; i++, ut += FrameSeconds)
                poll.Frame(ut, 10f + 0.3f * i / 500f);

            Assert.Empty(poll.Events);
        }

        [Fact]
        public void ServoCreepingJustBelowTheKeepMovingRate_IsBounded()
        {
            // The worst case of a stop-start rhythm: fast enough to cross the start deadband,
            // too slow to keep moving. Each pair moves the pose by a deadband, no faster.
            var poll = Hinge();
            double ut = 100.0;
            for (int i = 0; i < 3000; i++, ut += FrameSeconds) // 60 s at 0.45 deg/s
                poll.Frame(ut, 0.009f * i);

            Assert.True(poll.Events.Count <= 2 * 60 + 2,
                "creep emitted " + poll.Events.Count + " events in a minute");
        }

        [Fact]
        public void ServoStartingFromRest_StartsOnlyAfterRealMotion()
        {
            var poll = Hinge();
            double ut = 100.0;
            poll.Frame(ut, 10f);
            ut += FrameSeconds;
            poll.Frame(ut, 10.2f); // under the deadband: not yet motion
            Assert.Empty(poll.Events);
            ut += FrameSeconds;
            poll.Frame(ut, 10.6f);
            PartEvent started = Assert.Single(poll.Events);
            Assert.Equal(PartEventType.RoboticMotionStarted, started.eventType);
        }

        // ---- 2. rotor spin-down ----

        [Fact]
        public void RotorSpinningDownLinearlyToZero_EndsStopped()
        {
            var poll = Rotor();
            double ut = 100.0;
            // Spin at 100 RPM for 1 s, then fall linearly to 0 over 5 s at 50 Hz.
            for (int i = 0; i < 50; i++, ut += FrameSeconds)
                poll.Frame(ut, 100f);
            for (int i = 0; i <= 250; i++, ut += FrameSeconds)
                poll.Frame(ut, 100f * (250 - i) / 250f);

            Assert.Equal(1, poll.Count(PartEventType.RoboticMotionStarted));
            Assert.Equal(1, poll.Count(PartEventType.RoboticMotionStopped));
            Assert.Equal(PartEventType.RoboticMotionStopped, poll.Events.Last().eventType);
            Assert.False(poll.IsMoving);
        }

        [Theory]
        [InlineData(460f, 0.5)]
        [InlineData(460f, 3.0)]
        [InlineData(230f, 1.7)]
        [InlineData(100f, 5.0)]
        [InlineData(40f, 2.3)]
        [InlineData(12f, 0.9)]
        [InlineData(3f, 1.1)]
        [InlineData(0.9f, 0.4)]
        public void RotorSpinDownProfiles_AllEndStopped(float fromRpm, double seconds)
        {
            foreach (bool exponential in new[] { false, true })
            {
                foreach (double phase in new[] { 0.0, 0.007, 0.013 })
                {
                    var poll = Rotor();
                    double ut = 100.0 + phase;
                    for (int i = 0; i < 30; i++, ut += FrameSeconds)
                        poll.Frame(ut, fromRpm);
                    int frames = (int)Math.Round(seconds / FrameSeconds);
                    for (int i = 0; i <= frames; i++, ut += FrameSeconds)
                    {
                        double f = (double)(frames - i) / frames;
                        float rpm = exponential
                            ? (float)(fromRpm * (Math.Exp(4.0 * f) - 1.0) / (Math.Exp(4.0) - 1.0))
                            : (float)(fromRpm * f);
                        poll.Frame(ut, rpm);
                    }

                    string profile = $"from={fromRpm} over={seconds}s exp={exponential} phase={phase}";
                    Assert.True(poll.Count(PartEventType.RoboticMotionStarted) == 1, profile + " started");
                    Assert.True(poll.Count(PartEventType.RoboticMotionStopped) == 1, profile + " stopped");
                    Assert.True(poll.Events.Last().eventType == PartEventType.RoboticMotionStopped,
                        profile + " last=" + poll.Events.Last().eventType);
                }
            }
        }

        // ---- 3. rotor relative deadband ----

        [Fact]
        public void RotorHoveringWithNoisyRpm_EmitsABoundedSampleStream()
        {
            // A helicopter hover: 460 RPM with a +-3 RPM wobble every frame for a minute.
            var poll = Rotor();
            var rng = new Random(1234);
            double ut = 100.0;
            for (int i = 0; i < 3000; i++, ut += FrameSeconds)
                poll.Frame(ut, 460f + (float)(rng.NextDouble() * 6.0 - 3.0));

            Assert.Equal(1, poll.Count(PartEventType.RoboticMotionStarted));
            Assert.Equal(0, poll.Count(PartEventType.RoboticMotionStopped));
            Assert.True(poll.Count(PartEventType.RoboticPositionSample) <= 4,
                "hover noise emitted " + poll.Count(PartEventType.RoboticPositionSample) + " samples a minute");
        }

        [Fact]
        public void RotorChangingSpeedForReal_IsStillSampled()
        {
            // 230 -> 460 RPM over 2 s: a visible change of spin rate, sampled at the 4 Hz cap.
            var poll = Rotor();
            double ut = 100.0;
            for (int i = 0; i <= 100; i++, ut += FrameSeconds)
                poll.Frame(ut, 230f + 230f * i / 100f);
            // ...and then holds it: the next 4 Hz slot records the new speed.
            for (int i = 0; i < 25; i++, ut += FrameSeconds)
                poll.Frame(ut, 460f);

            Assert.InRange(poll.Count(PartEventType.RoboticPositionSample), 7, 10);
            Assert.Equal(460f, poll.Events.Last(e => e.eventType == PartEventType.RoboticPositionSample).value, 0.01f);
            Assert.Equal(0, poll.Count(PartEventType.RoboticMotionStopped));
        }

        [Theory]
        [InlineData(0f, 0.5f)]
        [InlineData(10f, 0.5f)]
        [InlineData(25f, 0.5f)]
        [InlineData(100f, 2f)]
        [InlineData(460f, 9.2f)]
        [InlineData(-460f, 9.2f)]
        public void RotorRpmSampleDeadband_IsTheLargerOfTheFloorAndTwoPercent(float rpm, float expected)
        {
            Assert.Equal(expected,
                FlightRecorder.ResolveRoboticSampleDeadband(FlightRecorder.RotorRpmMovingSource, 0.5f, rpm), 0.001f);
        }

        [Theory]
        [InlineData("inferred")]
        [InlineData("module-flag")]
        public void PoseServoSampleDeadband_IsTheModuleDeadband(string movingSource)
        {
            Assert.Equal(0.5f, FlightRecorder.ResolveRoboticSampleDeadband(movingSource, 0.5f, 460f), 0.0001f);
        }

        [Fact]
        public void RotorSampleDeadband_RejectsANonFiniteReading()
        {
            Assert.Equal(0.5f,
                FlightRecorder.ResolveRoboticSampleDeadband(FlightRecorder.RotorRpmMovingSource, 0.5f, float.NaN), 0.0001f);
        }

        [Fact]
        public void TrackingLine_NamesTheDebounceAndTheRotorDeadband()
        {
            string line = FlightRecorder.FormatRoboticTrackingLine(
                "rotor.01", 7u, 0, "ModuleRoboticServoRotor", "servoRateOfMotion", 0.5f,
                FlightRecorder.RotorRpmMovingSource, SampleIntervalSeconds, null);

            Assert.Contains("Robotics: tracking 'rotor.01' pid=7 midx=0 module=ModuleRoboticServoRotor", line);
            Assert.Contains("source=servoRateOfMotion", line);
            Assert.Contains("moving=rotor-rpm", line);
            Assert.Contains("stopHold=0.25s", line);
            Assert.Contains("deadband=max(0.500,2%rpm)", line);

            string bg = FlightRecorder.FormatRoboticTrackingLine(
                "hinge.01", 8u, 1, "ModuleRoboticServoHinge", "servoTransformAngle", 0.5f,
                "inferred", SampleIntervalSeconds, 99u);
            Assert.Contains("deadband=0.500", bg);
            Assert.Contains("(bg vessel 99)", bg);
        }

        [Fact]
        public void TrackingLine_IsTheWholeLiteralTheSrvLanesPin()
        {
            // SRV-1 / SRV-2 (harness/scenarios) regex these lines whole; reword one and the lane
            // reds on a missing token instead of here.
            Assert.Equal(
                "Robotics: tracking 'rotor.01' pid=7 midx=0 module=ModuleRoboticServoRotor source=servoRateOfMotion "
                + "deadband=max(0.500,2%rpm) sampleHz=4.0 moving=rotor-rpm stopHold=0.25s stop=signal",
                FlightRecorder.FormatRoboticTrackingLine(
                    "rotor.01", 7u, 0, "ModuleRoboticServoRotor", "servoRateOfMotion", 0.5f,
                    FlightRecorder.RotorRpmMovingSource, SampleIntervalSeconds, null));
            Assert.Equal(
                "Robotics: tracking 'hinge.01' pid=8 midx=0 module=ModuleRoboticServoHinge source=servoTransformAngle "
                + "deadband=0.500 sampleHz=4.0 moving=inferred stopHold=0.25s stop=debounced",
                FlightRecorder.FormatRoboticTrackingLine(
                    "hinge.01", 8u, 0, "ModuleRoboticServoHinge", "servoTransformAngle", 0.5f,
                    "inferred", SampleIntervalSeconds, null));
        }
    }
}
