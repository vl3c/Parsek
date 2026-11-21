using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Todo RECORDER-GEAR-SUSPENSION-AS-ROBOTICS: a plane rolling on its landing gear recorded the
    /// gear's ModuleWheelSuspension as robotics motion, about 22 RoboticMotionStarted / Stopped
    /// events a second. The suspension signal is the magnitude of the suspension transform's
    /// absolute local position; millimetre jitter crosses CheckRoboticTransition's inferred-motion
    /// threshold (a quarter of the 2.5 mm deadband) on one frame and not the next. The fix stops
    /// recording suspension in both recorders (the module stays cached so the robotic module
    /// index keeps its lockstep with the ghost builder), and playback ignores the suspension
    /// events legacy recordings carry instead of posing the wheel from an absolute magnitude.
    /// </summary>
    [Collection("Sequential")]
    public class GearSuspensionRoboticGateTests : IDisposable
    {
        private const double SampleIntervalSeconds = 0.25;
        private const float SuspensionDeadband = 0.0025f;
        private const float HingeDeadbandDegrees = 0.5f;

        private readonly List<string> logLines = new List<string>();

        public GearSuspensionRoboticGateTests()
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

        /// <summary>
        /// One recorder's robotic poll loop over a single module, in production order: the shared
        /// emission gate first, then the transition core with the recorder's own state maps.
        /// </summary>
        private sealed class PollLoop
        {
            private readonly HashSet<ulong> moving = new HashSet<ulong>();
            private readonly Dictionary<ulong, float> lastPosition = new Dictionary<ulong, float>();
            private readonly Dictionary<ulong, double> lastSample = new Dictionary<ulong, double>();
            private readonly Dictionary<ulong, FlightRecorder.RoboticMotionWindow> windows =
                new Dictionary<ulong, FlightRecorder.RoboticMotionWindow>();
            private readonly HashSet<ulong> loggedKeys = new HashSet<ulong>();
            private readonly string subsystem;
            private readonly uint? backgroundVesselPid;

            internal PollLoop(string subsystem, uint? backgroundVesselPid)
            {
                this.subsystem = subsystem;
                this.backgroundVesselPid = backgroundVesselPid;
            }

            internal List<PartEvent> Run(
                string moduleName, string partName, uint pid, int moduleIndex,
                float deadband, double startUT, double frameSeconds, IList<float> values)
            {
                var events = new List<PartEvent>();
                ulong key = FlightRecorder.EncodeEngineKey(pid, moduleIndex);
                for (int i = 0; i < values.Count; i++)
                {
                    if (!FlightRecorder.ShouldEmitRoboticEvents(
                            subsystem, moduleName, key, loggedKeys, partName, pid, moduleIndex,
                            backgroundVesselPid))
                        continue;

                    events.AddRange(FlightRecorder.CheckRoboticTransition(
                        key, pid, moduleIndex, partName, false, values[i], deadband,
                        startUT + i * frameSeconds, moving, lastPosition, lastSample,
                        SampleIntervalSeconds, windows));
                }
                return events;
            }
        }

        /// <summary>
        /// The AIR-1 shape: a GearMedium suspension magnitude around 1.4 wobbling by about a
        /// millimetre every physics frame (0.02 s) for 22 seconds.
        /// </summary>
        private static List<float> RunwayRollJitter(int frames)
        {
            var values = new List<float>(frames);
            for (int i = 0; i < frames; i++)
            {
                // Alternates 0 / +1.0 mm / -0.4 mm around the rest magnitude: every step is
                // above the 0.6 mm inferred-motion threshold or back under it.
                float wobble = (i % 3) == 0 ? 0f : ((i % 3) == 1 ? 0.0010f : -0.0004f);
                values.Add(1.4200f + wobble);
            }
            return values;
        }

        [Fact]
        public void TheTransitionCoreAloneNoLongerTurnsMillimetreJitterIntoAStartStopStorm()
        {
            // The defect the gate was added for: the transition core, fed the jitter, emitted a
            // Started / Stopped pair every few frames (over 200 events). Since the motion debounce
            // (todo RECORDER-ROBOTIC-MOTION-DEBOUNCE) a resting module starts only on a whole
            // deadband of change, so sub-deadband jitter is silent even ungated; the gate stays
            // because suspension travel is not recorded at all.
            var moving = new HashSet<ulong>();
            var last = new Dictionary<ulong, float>();
            var sample = new Dictionary<ulong, double>();
            var windows = new Dictionary<ulong, FlightRecorder.RoboticMotionWindow>();
            ulong key = FlightRecorder.EncodeEngineKey(4001u, 0);
            List<float> values = RunwayRollJitter(1100);
            int events = 0;
            for (int i = 0; i < values.Count; i++)
            {
                events += FlightRecorder.CheckRoboticTransition(
                    key, 4001u, 0, "GearMedium", false, values[i], SuspensionDeadband,
                    100.0 + i * 0.02, moving, last, sample, SampleIntervalSeconds, windows).Count;
            }

            Assert.Equal(0, events);
        }

        [Fact]
        public void ActiveRecorder_SuspensionJitterOnATakeoffRoll_EmitsNoEvents_AndLogsTheSkipOnce()
        {
            var loop = new PollLoop("Recorder", null);

            List<PartEvent> events = loop.Run(
                "ModuleWheelSuspension", "GearMedium", 4001u, 0, SuspensionDeadband,
                100.0, 0.02, RunwayRollJitter(1100));

            Assert.Empty(events);
            var skipLines = logLines.Where(l => l.Contains("[Recorder]")
                && l.Contains("Robotics: wheel suspension not recorded")).ToList();
            Assert.Single(skipLines);
            Assert.Contains("'GearMedium' pid=4001 midx=0 module=ModuleWheelSuspension;", skipLines[0]);
            Assert.Contains("the ghost holds the rest pose", skipLines[0]);
            Assert.DoesNotContain(logLines, l => l.Contains("RoboticMotion"));
        }

        [Fact]
        public void BackgroundRecorder_SuspensionJitter_EmitsNoEvents_AndNamesTheBackgroundVessel()
        {
            var loop = new PollLoop("BgRecorder", 77u);

            List<PartEvent> events = loop.Run(
                "ModuleWheelSuspension", "roverWheel1", 5002u, 1, SuspensionDeadband,
                100.0, 0.02, RunwayRollJitter(500));

            Assert.Empty(events);
            Assert.Single(logLines, l => l.Contains("[BgRecorder]")
                && l.Contains("Robotics: wheel suspension not recorded for 'roverWheel1' pid=5002 midx=1 "
                    + "module=ModuleWheelSuspension (bg vessel 77);"));
        }

        [Fact]
        public void RealHinge_StillRecordsStartedSamplesAndStopped_AsBefore()
        {
            var loop = new PollLoop("Recorder", null);
            // Rest, a 2 deg/frame sweep from 0 to 40, then holding at 40.
            var values = new List<float> { 0f };
            for (int i = 1; i <= 20; i++) values.Add(i * 2f);
            for (int i = 0; i < 10; i++) values.Add(40f);

            List<PartEvent> events = loop.Run(
                "ModuleRoboticServoHinge", "hinge.01", 6003u, 0, HingeDeadbandDegrees,
                200.0, 0.1, values);

            Assert.Equal(PartEventType.RoboticMotionStarted, events.First().eventType);
            Assert.Equal(2f, events.First().value);
            Assert.Equal(PartEventType.RoboticMotionStopped, events.Last().eventType);
            Assert.Equal(40f, events.Last().value);
            Assert.Equal(1, events.Count(e => e.eventType == PartEventType.RoboticMotionStarted));
            Assert.Equal(1, events.Count(e => e.eventType == PartEventType.RoboticMotionStopped));
            Assert.Contains(events, e => e.eventType == PartEventType.RoboticPositionSample);
            Assert.DoesNotContain(logLines, l => l.Contains("not recorded"));
        }

        [Theory]
        [InlineData("ModuleRoboticServoHinge")]
        [InlineData("ModuleRoboticServoPiston")]
        [InlineData("ModuleRoboticRotationServo")]
        [InlineData("ModuleRoboticServoRotor")]
        public void BreakingGroundServos_PassTheEmissionGate(string moduleName)
        {
            Assert.False(FlightRecorder.IsRoboticEmissionSkippedModuleName(moduleName));
            Assert.True(FlightRecorder.ShouldEmitRoboticEvents(
                "Recorder", moduleName, 1UL, new HashSet<ulong>(), "p", 1u, 0, null));
            Assert.Empty(logLines);
        }

        [Theory]
        [InlineData("ModuleWheelSuspension")]
        [InlineData("ModuleWheelSteering")]
        [InlineData("ModuleWheelMotor")]
        [InlineData("ModuleWheelMotorSteering")]
        public void EveryWheelModule_IsSkipped_ButStaysARoboticModuleForIndexLockstep(string moduleName)
        {
            Assert.True(FlightRecorder.IsRoboticEmissionSkippedModuleName(moduleName));
            // The skipped modules must stay in CacheRoboticModules / TryBuildRoboticInfos so the
            // sequential moduleIndex matches between recorder and ghost builder.
            Assert.True(FlightRecorder.IsRoboticModuleName(moduleName));
        }

        [Fact]
        public void SuspensionPredicate_NamesOnlyTheSuspensionModule()
        {
            Assert.True(FlightRecorder.IsWheelSuspensionModuleName("ModuleWheelSuspension"));
            Assert.False(FlightRecorder.IsWheelSuspensionModuleName("ModuleWheelSteering"));
            Assert.False(FlightRecorder.IsWheelSuspensionModuleName("ModuleRoboticServoPiston"));
            Assert.False(FlightRecorder.IsWheelSuspensionModuleName(null));
            Assert.False(FlightRecorder.IsRoboticEmissionSkippedModuleName(null));
            // The derived-visual gate keeps its own meaning: suspension is not derived.
            Assert.False(FlightRecorder.IsDerivedWheelVisualModuleName("ModuleWheelSuspension"));
        }

        [Fact]
        public void DerivedWheelVisualSkipLine_KeepsItsWording()
        {
            Assert.False(FlightRecorder.ShouldEmitRoboticEvents(
                "Recorder", "ModuleWheelMotor", 9UL, new HashSet<ulong>(), "roverWheel2", 9u, 2, null));

            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("Robotics: derived wheel visual not recorded for 'roverWheel2' pid=9 "
                    + "midx=2 module=ModuleWheelMotor; derived from ghost ground motion at playback"));
        }

        private static GhostPlaybackState StateWith(uint pid, int moduleIndex, RoboticGhostInfo info)
        {
            return new GhostPlaybackState
            {
                roboticInfos = new Dictionary<ulong, RoboticGhostInfo>
                {
                    { FlightRecorder.EncodeEngineKey(pid, moduleIndex), info },
                },
            };
        }

        private static PartEvent RoboticEvent(PartEventType type, uint pid, int moduleIndex, float value)
        {
            return new PartEvent
            {
                ut = 10.0,
                partPersistentId = pid,
                eventType = type,
                partName = "GearMedium",
                value = value,
                moduleIndex = moduleIndex,
            };
        }

        [Fact]
        public void Playback_LegacySuspensionEvent_IsIgnored_AndLeavesTheServoAtRest()
        {
            var info = new RoboticGhostInfo
            {
                partPersistentId = 4001u,
                moduleIndex = 0,
                moduleName = "ModuleWheelSuspension",
                visualMode = RoboticVisualMode.Linear,
                currentValue = 0f,
                active = false,
                lastUpdateUT = double.NaN,
            };
            GhostPlaybackState state = StateWith(4001u, 0, info);

            GhostPartEventOutcome outcome = GhostPlaybackLogic.ApplyRoboticEvent(
                state, RoboticEvent(PartEventType.RoboticMotionStarted, 4001u, 0, 1.42f), 10.0);

            Assert.Equal(GhostPartEventOutcome.IgnoredRecordedSignal, outcome);
            Assert.Equal(0f, info.currentValue);
            Assert.False(info.active);
            Assert.True(double.IsNaN(info.lastUpdateUT));
            Assert.False(GhostPartEventApplyLog.CountsAsApplied(outcome));
            Assert.Equal("ignored-recorded-signal", GhostPartEventApplyLog.OutcomeToken(outcome));
        }

        [Fact]
        public void Playback_HingeEvent_StillApplies()
        {
            var info = new RoboticGhostInfo
            {
                partPersistentId = 6003u,
                moduleIndex = 0,
                moduleName = "ModuleRoboticServoHinge",
                visualMode = RoboticVisualMode.Rotational,
            };
            GhostPlaybackState state = StateWith(6003u, 0, info);

            GhostPartEventOutcome outcome = GhostPlaybackLogic.ApplyRoboticEvent(
                state, RoboticEvent(PartEventType.RoboticMotionStarted, 6003u, 0, 12f), 10.0);

            Assert.Equal(GhostPartEventOutcome.Applied, outcome);
            Assert.Equal(12f, info.currentValue);
            Assert.True(info.active);
        }
    }
}
