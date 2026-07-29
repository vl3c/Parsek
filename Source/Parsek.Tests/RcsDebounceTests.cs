using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit suite for <see cref="FlightRecorder.ProcessRcsDebounce"/>, the shared RCS
    /// debounce state machine driven from two call sites:
    /// <c>FlightRecorder.CheckRcsState</c> / <c>ParsekFlight.EvaluatePostSwitchRcsActivity</c>
    /// (loaded vessel) and <c>BackgroundRecorder.CheckRcsState</c> (background vessel).
    ///
    /// The method is pure over the four collections it is handed (frame-count map,
    /// active-key set, last-emitted-throttle map, event buffer), so this class touches
    /// no shared static state and deliberately carries no <c>[Collection("Sequential")]</c>.
    ///
    /// Contracts under test (not tuning constants):
    ///  - a run of consecutive active frames emits exactly one RCSActivated, and only
    ///    once the run reaches <see cref="FlightRecorder.RcsDebounceFrameThreshold"/>;
    ///  - a run that ends before the threshold emits nothing at all and leaves no state;
    ///  - deactivation of a sustained run emits exactly one RCSStopped and clears every
    ///    per-key entry, so the key is indistinguishable from one never seen;
    ///  - while recording, RCSThrottle is emitted only when power moves away from the
    ///    last EMITTED power by more than the deadband (a ratchet, not a per-frame diff);
    ///  - emitted power is always the clamped [0,1] normalized value;
    ///  - keys are fully independent;
    ///  - a caller may pre-seed the maps to suppress a spurious activation on resume.
    /// </summary>
    public class RcsDebounceTests
    {
        // Read symbolically - the suite asserts the SHAPE of the debounce window, not the
        // number of frames it happens to be tuned to.
        private const int Threshold = FlightRecorder.RcsDebounceFrameThreshold;

        // The throttle-change deadband is a bare literal inside CheckRcsTransition with no
        // exposed symbol. Mirrored here so the deadband tests read as "half the deadband is
        // suppressed / double the deadband is emitted" rather than as pinned magic numbers.
        private const float ThrottleDeadband = 0.05f;

        private const uint DefaultPid = 4242u;
        private const int DefaultModuleIndex = 3;
        private const string DefaultPartName = "RCSBlock";

        private static readonly ulong DefaultKey =
            FlightRecorder.EncodeEngineKey(DefaultPid, DefaultModuleIndex);

        // A single thruster at thrusterPower 1.0 makes ComputeRcsPower an identity on the
        // force value, so tests can talk in normalized power directly.
        private const float UnitThrusterPower = 1f;

        private static float[] Forces(float normalizedPower)
        {
            return new float[] { normalizedPower };
        }

        /// <summary>Drives ProcessRcsDebounce frame by frame over caller-owned collections.</summary>
        private sealed class RcsHarness
        {
            internal readonly Dictionary<ulong, int> FrameCounts = new Dictionary<ulong, int>();
            internal readonly HashSet<ulong> ActiveKeys = new HashSet<ulong>();
            internal readonly Dictionary<ulong, float> ThrottleMap = new Dictionary<ulong, float>();
            internal readonly List<PartEvent> Events = new List<PartEvent>();

            internal double Ut = 1000.0;
            internal double UtStep = 0.02;

            /// <summary>UT that the most recent Frame call stamped its events with.</summary>
            internal double LastFrameUt { get; private set; }

            internal List<PartEvent> Frame(
                bool active,
                float normalizedPower = 0.5f,
                ulong key = 0,
                uint pid = DefaultPid,
                int moduleIndex = DefaultModuleIndex,
                string partName = DefaultPartName)
            {
                return FrameRaw(
                    active,
                    Forces(normalizedPower),
                    UnitThrusterPower,
                    key == 0 ? DefaultKey : key,
                    pid,
                    moduleIndex,
                    partName);
            }

            internal List<PartEvent> FrameRaw(
                bool active,
                float[] thrustForces,
                float thrusterPower,
                ulong key,
                uint pid = DefaultPid,
                int moduleIndex = DefaultModuleIndex,
                string partName = DefaultPartName)
            {
                int before = Events.Count;
                LastFrameUt = Ut;
                FlightRecorder.ProcessRcsDebounce(
                    key, pid, moduleIndex, partName,
                    active, thrustForces, thrusterPower, Ut,
                    FrameCounts, ActiveKeys, ThrottleMap, Events);
                Ut += UtStep;
                return Events.GetRange(before, Events.Count - before);
            }

            /// <summary>Runs a full debounce window of active frames at a constant power.</summary>
            internal void RunToRecording(float normalizedPower = 0.5f, ulong key = 0)
            {
                for (int i = 0; i < Threshold; i++)
                    Frame(true, normalizedPower, key);
            }

            internal bool HasAnyStateFor(ulong key)
            {
                return FrameCounts.ContainsKey(key)
                    || ActiveKeys.Contains(key)
                    || ThrottleMap.ContainsKey(key);
            }
        }

        public static IEnumerable<object[]> SubThresholdFrameCounts()
        {
            for (int n = 1; n < Threshold; n++)
                yield return new object[] { n };
        }

        #region Activation - debounce window

        [Theory]
        [MemberData(nameof(SubThresholdFrameCounts))]
        public void Activation_EmitsNothingBeforeThresholdIsReached(int activeFrames)
        {
            var h = new RcsHarness();

            for (int i = 0; i < activeFrames; i++)
                Assert.Empty(h.Frame(true));

            Assert.Empty(h.Events);
            Assert.DoesNotContain(DefaultKey, h.ActiveKeys);
            Assert.False(h.ThrottleMap.ContainsKey(DefaultKey));
            // The frame counter is still running - the run has not been rejected, only debounced.
            Assert.Equal(activeFrames, h.FrameCounts[DefaultKey]);
        }

        [Fact]
        public void Activation_EmitsExactlyOneActivatedEventOnTheThresholdFrame()
        {
            var h = new RcsHarness();

            for (int i = 0; i < Threshold - 1; i++)
                Assert.Empty(h.Frame(true, 0.4f));

            var atThreshold = h.Frame(true, 0.4f);

            Assert.Single(atThreshold);
            Assert.Equal(PartEventType.RCSActivated, atThreshold[0].eventType);
            Assert.Single(h.Events);
        }

        [Fact]
        public void Activation_MarksKeyActiveAndRecordsEmittedPower()
        {
            var h = new RcsHarness();

            h.RunToRecording(0.4f);

            Assert.Contains(DefaultKey, h.ActiveKeys);
            Assert.Equal(0.4, h.ThrottleMap[DefaultKey], 5);
            Assert.Equal(Threshold, h.FrameCounts[DefaultKey]);
        }

        [Fact]
        public void Activation_EventCarriesCallerIdentityAndFrameUt()
        {
            var h = new RcsHarness();

            for (int i = 0; i < Threshold - 1; i++)
                h.Frame(true, 0.4f);

            var evt = h.Frame(true, 0.4f).Single();

            Assert.Equal(DefaultPid, evt.partPersistentId);
            Assert.Equal(DefaultModuleIndex, evt.moduleIndex);
            Assert.Equal(DefaultPartName, evt.partName);
            Assert.Equal(h.LastFrameUt, evt.ut, 6);
        }

        [Fact]
        public void Activation_EventValueIsTheNormalizedPowerAtTheThresholdFrame()
        {
            var h = new RcsHarness();

            // Power swings freely during the debounce window; only the crossing frame's
            // value is what gets recorded.
            for (int i = 0; i < Threshold - 1; i++)
                h.Frame(true, 0.9f);

            var evt = h.Frame(true, 0.25f).Single();

            Assert.Equal(0.25, evt.value, 5);
            Assert.Equal(0.25, h.ThrottleMap[DefaultKey], 5);
        }

        [Fact]
        public void Activation_SustainedFramesAtConstantPowerEmitNothingFurther()
        {
            var h = new RcsHarness();
            h.RunToRecording(0.6f);

            for (int i = 0; i < 50; i++)
                Assert.Empty(h.Frame(true, 0.6f));

            Assert.Single(h.Events);
            Assert.Equal(PartEventType.RCSActivated, h.Events[0].eventType);
        }

        [Fact]
        public void Activation_FrameCountIsStrictlyMonotonicWhileActive()
        {
            var h = new RcsHarness();

            int previous = 0;
            for (int i = 0; i < Threshold + 20; i++)
            {
                h.Frame(true, 0.5f);
                int current = h.FrameCounts[DefaultKey];
                Assert.True(current > previous,
                    $"frame count must advance every active frame (was {previous}, now {current})");
                previous = current;
            }
        }

        #endregion

        #region Deactivation

        [Fact]
        public void Deactivation_AfterSustainedRun_EmitsExactlyOneStoppedEvent()
        {
            var h = new RcsHarness();
            h.RunToRecording(0.6f);

            var stopped = h.Frame(false);

            Assert.Single(stopped);
            Assert.Equal(PartEventType.RCSStopped, stopped[0].eventType);
        }

        [Fact]
        public void Deactivation_StoppedEventCarriesIdentityUtAndNoPower()
        {
            var h = new RcsHarness();
            h.RunToRecording(0.6f);

            var evt = h.Frame(false).Single();

            Assert.Equal(DefaultPid, evt.partPersistentId);
            Assert.Equal(DefaultModuleIndex, evt.moduleIndex);
            Assert.Equal(DefaultPartName, evt.partName);
            Assert.Equal(h.LastFrameUt, evt.ut, 6);
            // A stop is a binary signal - it must not smuggle a stale power reading through.
            Assert.Equal(0f, evt.value);
        }

        [Fact]
        public void Deactivation_ClearsEveryPerKeyEntry()
        {
            var h = new RcsHarness();
            h.RunToRecording(0.6f);
            Assert.True(h.HasAnyStateFor(DefaultKey));

            h.Frame(false);

            Assert.False(h.HasAnyStateFor(DefaultKey));
        }

        [Fact]
        public void Deactivation_RepeatedInactiveFramesEmitOnlyTheFirstStopped()
        {
            var h = new RcsHarness();
            h.RunToRecording(0.6f);
            h.Frame(false);

            for (int i = 0; i < 25; i++)
                Assert.Empty(h.Frame(false));

            Assert.Equal(2, h.Events.Count);
            Assert.Equal(PartEventType.RCSStopped, h.Events[1].eventType);
        }

        [Theory]
        [MemberData(nameof(SubThresholdFrameCounts))]
        public void Deactivation_BeforeThreshold_EmitsNothingAndLeavesNoState(int activeFrames)
        {
            var h = new RcsHarness();

            for (int i = 0; i < activeFrames; i++)
                h.Frame(true, 0.7f);

            Assert.Empty(h.Frame(false));

            Assert.Empty(h.Events);
            Assert.False(h.HasAnyStateFor(DefaultKey));
        }

        [Fact]
        public void Deactivation_OfNeverSeenKey_IsAFullNoOp()
        {
            var h = new RcsHarness();

            Assert.Empty(h.Frame(false));

            Assert.Empty(h.Events);
            Assert.Empty(h.FrameCounts);
            Assert.Empty(h.ActiveKeys);
            Assert.Empty(h.ThrottleMap);
        }

        [Fact]
        public void Deactivation_MicroCorrectionBurstsNeverEmitAnything()
        {
            var h = new RcsHarness();

            // Attitude-hold jitter: repeated short pulses, each one frame short of the window.
            for (int burst = 0; burst < 6; burst++)
            {
                for (int i = 0; i < Threshold - 1; i++)
                    h.Frame(true, 0.8f);
                h.Frame(false);
            }

            Assert.Empty(h.Events);
            Assert.False(h.HasAnyStateFor(DefaultKey));
        }

        #endregion

        #region Debounce window reset and re-activation

        [Fact]
        public void DebounceWindow_InterruptedRunRestartsTheCounter()
        {
            var h = new RcsHarness();

            for (int i = 0; i < Threshold - 1; i++)
                h.Frame(true, 0.5f);
            h.Frame(false);

            // Second run: the counter must start over, so Threshold-1 more frames still emit nothing.
            for (int i = 0; i < Threshold - 1; i++)
                Assert.Empty(h.Frame(true, 0.5f));
            Assert.Empty(h.Events);

            var activated = h.Frame(true, 0.5f);
            Assert.Single(activated);
            Assert.Equal(PartEventType.RCSActivated, activated[0].eventType);
        }

        [Fact]
        public void Reactivation_AfterStop_RequiresAFullWindowAgain()
        {
            var h = new RcsHarness();
            h.RunToRecording(0.5f);
            h.Frame(false);
            int afterStop = h.Events.Count;

            for (int i = 0; i < Threshold - 1; i++)
                Assert.Empty(h.Frame(true, 0.5f));
            Assert.Equal(afterStop, h.Events.Count);

            var activated = h.Frame(true, 0.5f);
            Assert.Single(activated);
            Assert.Equal(PartEventType.RCSActivated, activated[0].eventType);
        }

        [Fact]
        public void Reactivation_ProducesAStrictlyAlternatingActivatedStoppedSequence()
        {
            var h = new RcsHarness();

            for (int cycle = 0; cycle < 4; cycle++)
            {
                h.RunToRecording(0.5f);
                for (int i = 0; i < 5; i++)
                    h.Frame(true, 0.5f);
                h.Frame(false);
                for (int i = 0; i < 3; i++)
                    h.Frame(false);
            }

            var kinds = h.Events.Select(e => e.eventType).ToList();
            Assert.Equal(8, kinds.Count);
            for (int i = 0; i < kinds.Count; i++)
            {
                Assert.Equal(
                    i % 2 == 0 ? PartEventType.RCSActivated : PartEventType.RCSStopped,
                    kinds[i]);
            }
        }

        [Fact]
        public void Reactivation_AfterStop_RecordsTheNewRunsPowerNotTheOldOne()
        {
            var h = new RcsHarness();
            h.RunToRecording(0.9f);
            h.Frame(false);

            h.RunToRecording(0.2f);

            var second = h.Events.Last();
            Assert.Equal(PartEventType.RCSActivated, second.eventType);
            Assert.Equal(0.2, second.value, 5);
            Assert.Equal(0.2, h.ThrottleMap[DefaultKey], 5);
        }

        #endregion

        #region Throttle tracking

        [Theory]
        [InlineData(0.5f, 0.5f + ThrottleDeadband * 2f)]   // rising well past the deadband
        [InlineData(0.5f, 0.5f - ThrottleDeadband * 2f)]   // falling well past the deadband
        [InlineData(0.5f, 1f)]                             // full swing up
        [InlineData(0.5f, 0f)]                             // full swing down, still firing
        public void Throttle_ChangeBeyondDeadband_EmitsThrottleEvent(float initial, float changed)
        {
            var h = new RcsHarness();
            h.RunToRecording(initial);

            var emitted = h.Frame(true, changed);

            Assert.Single(emitted);
            Assert.Equal(PartEventType.RCSThrottle, emitted[0].eventType);
            Assert.Equal((double)changed, emitted[0].value, 5);
            Assert.Equal((double)changed, h.ThrottleMap[DefaultKey], 5);
            // A throttle change is not a re-activation and must not disturb membership.
            Assert.Contains(DefaultKey, h.ActiveKeys);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(ThrottleDeadband * 0.5f)]
        [InlineData(-ThrottleDeadband * 0.5f)]
        [InlineData(ThrottleDeadband * 0.9f)]
        [InlineData(-ThrottleDeadband * 0.9f)]
        public void Throttle_ChangeWithinDeadband_IsSuppressed(float delta)
        {
            var h = new RcsHarness();
            h.RunToRecording(0.5f);

            Assert.Empty(h.Frame(true, 0.5f + delta));

            Assert.Single(h.Events);
            // The reference power is unchanged, so the suppressed reading is not adopted.
            Assert.Equal(0.5, h.ThrottleMap[DefaultKey], 5);
        }

        [Fact]
        public void Throttle_ChangeExactlyAtTheDeadband_IsSuppressed()
        {
            // The deadband is exclusive - a move of exactly the deadband is still noise.
            // Driven from a zero reference so the delta is exactly representable in float
            // (0.5f + 0.05f rounds to marginally MORE than a 0.05f step and would emit).
            var h = new RcsHarness();
            h.RunToRecording(0f);
            Assert.Equal(0f, h.ThrottleMap[DefaultKey]);

            Assert.Empty(h.Frame(true, ThrottleDeadband));

            Assert.Single(h.Events);
            Assert.Equal(0f, h.ThrottleMap[DefaultKey]);
        }

        [Fact]
        public void Throttle_ReferenceIsLastEmittedPowerNotLastObservedPower()
        {
            var h = new RcsHarness();
            h.RunToRecording(0.5f);

            // Two sub-deadband steps in the same direction. If the reference tracked the last
            // OBSERVED power, neither step would ever emit and the drift would go unrecorded.
            Assert.Empty(h.Frame(true, 0.5f + ThrottleDeadband * 0.7f));
            var emitted = h.Frame(true, 0.5f + ThrottleDeadband * 1.4f);

            Assert.Single(emitted);
            Assert.Equal(PartEventType.RCSThrottle, emitted[0].eventType);
            Assert.Equal(0.5 + ThrottleDeadband * 1.4, emitted[0].value, 5);
        }

        [Fact]
        public void Throttle_EmittedEventAdvancesTheReferenceSoTheNextSmallStepIsSuppressed()
        {
            var h = new RcsHarness();
            h.RunToRecording(0.5f);

            float raised = 0.5f + ThrottleDeadband * 2f;
            Assert.Single(h.Frame(true, raised));
            Assert.Equal((double)raised, h.ThrottleMap[DefaultKey], 5);

            Assert.Empty(h.Frame(true, raised + ThrottleDeadband * 0.5f));
            Assert.Equal(2, h.Events.Count);
        }

        [Fact]
        public void Throttle_NoEventsWhileTheRunIsStillDebouncing()
        {
            var h = new RcsHarness();

            // Wild power swings inside the debounce window must stay invisible.
            for (int i = 0; i < Threshold - 1; i++)
                Assert.Empty(h.Frame(true, i % 2 == 0 ? 0.05f : 0.95f));

            Assert.Empty(h.Events);
        }

        [Fact]
        public void Throttle_EventsAreSuppressedOnceTheRunStops()
        {
            var h = new RcsHarness();
            h.RunToRecording(0.5f);
            h.Frame(false);
            int afterStop = h.Events.Count;

            // Power changes while inactive are meaningless and must not surface.
            for (int i = 0; i < 5; i++)
                Assert.Empty(h.FrameRaw(false, Forces(1f), UnitThrusterPower, DefaultKey));

            Assert.Equal(afterStop, h.Events.Count);
        }

        [Fact]
        public void Throttle_ARunOfChangesEmitsMonotonicNonDecreasingUts()
        {
            var h = new RcsHarness();
            h.RunToRecording(0.1f);

            for (int i = 1; i <= 8; i++)
                h.Frame(true, 0.1f + i * ThrottleDeadband * 2f);

            Assert.True(h.Events.Count > 1);
            for (int i = 1; i < h.Events.Count; i++)
                Assert.True(h.Events[i].ut >= h.Events[i - 1].ut, "event UTs must be non-decreasing");
        }

        #endregion

        #region Power computation edges seen through the debounce

        public static IEnumerable<object[]> PowerEdgeCases()
        {
            // forces, thrusterPower, expected normalized power
            yield return new object[] { new float[] { 1f }, 1f, 1f };
            yield return new object[] { new float[] { 0f }, 1f, 0f };
            yield return new object[] { new float[] { 5f, 5f }, 10f, 0.5f };
            yield return new object[] { new float[] { -5f }, 1f, 0f };        // negative thrust clamps to 0
            yield return new object[] { new float[] { 10f }, 1f, 1f };        // over-unity clamps to 1
            yield return new object[] { null, 1f, 0f };                        // no force array
            yield return new object[] { new float[0], 1f, 0f };                // empty force array
            yield return new object[] { new float[] { 1f }, 0f, 0f };          // zero thruster power
            yield return new object[] { new float[] { 1f }, -1f, 0f };         // negative thruster power
        }

        [Theory]
        [MemberData(nameof(PowerEdgeCases))]
        public void Power_ActivationValueIsAlwaysTheClampedNormalizedPower(
            float[] forces, float thrusterPower, float expected)
        {
            var h = new RcsHarness();

            for (int i = 0; i < Threshold - 1; i++)
                h.FrameRaw(true, forces, thrusterPower, DefaultKey);
            var evt = h.FrameRaw(true, forces, thrusterPower, DefaultKey).Single();

            Assert.Equal(PartEventType.RCSActivated, evt.eventType);
            Assert.Equal((double)expected, evt.value, 5);
            Assert.InRange(evt.value, 0f, 1f);
        }

        [Fact]
        public void Power_ZeroPowerRunStillActivates_ActivationTracksTheFlagNotTheThrust()
        {
            var h = new RcsHarness();

            // rcs_active is true but the thrusters produce nothing (e.g. out of monoprop).
            for (int i = 0; i < Threshold - 1; i++)
                h.FrameRaw(true, new float[] { 0f }, UnitThrusterPower, DefaultKey);
            var evt = h.FrameRaw(true, new float[] { 0f }, UnitThrusterPower, DefaultKey).Single();

            Assert.Equal(PartEventType.RCSActivated, evt.eventType);
            Assert.Equal(0f, evt.value);
            Assert.Contains(DefaultKey, h.ActiveKeys);
        }

        [Fact]
        public void Power_EveryEmittedValueStaysWithinTheNormalizedRange()
        {
            var h = new RcsHarness();
            var wild = new float[] { -100f, 0f, 0.3f, 250f, -0.001f, 1f, 7.5f, 0.5f };

            for (int i = 0; i < Threshold + wild.Length * 4; i++)
                h.FrameRaw(true, new float[] { wild[i % wild.Length] }, UnitThrusterPower, DefaultKey);
            h.Frame(false);

            Assert.NotEmpty(h.Events);
            foreach (var e in h.Events)
                Assert.InRange(e.value, 0f, 1f);
        }

        #endregion

        #region Multiple keys

        [Fact]
        public void MultipleKeys_DebounceIndependently()
        {
            var h = new RcsHarness();
            ulong keyA = FlightRecorder.EncodeEngineKey(10u, 0);
            ulong keyB = FlightRecorder.EncodeEngineKey(11u, 0);

            // A fires a full window; B only jitters.
            for (int i = 0; i < Threshold; i++)
            {
                h.Frame(true, 0.5f, keyA, pid: 10u, moduleIndex: 0, partName: "A");
                if (i < Threshold - 1)
                    h.Frame(true, 0.5f, keyB, pid: 11u, moduleIndex: 0, partName: "B");
            }
            h.Frame(false, key: keyB, pid: 11u, moduleIndex: 0, partName: "B");

            Assert.Single(h.Events);
            Assert.Equal("A", h.Events[0].partName);
            Assert.Contains(keyA, h.ActiveKeys);
            Assert.DoesNotContain(keyB, h.ActiveKeys);
        }

        [Fact]
        public void MultipleKeys_StoppingOneLeavesTheOtherRecording()
        {
            var h = new RcsHarness();
            ulong keyA = FlightRecorder.EncodeEngineKey(10u, 0);
            ulong keyB = FlightRecorder.EncodeEngineKey(11u, 0);

            h.RunToRecording(0.5f, keyA);
            h.RunToRecording(0.5f, keyB);
            h.Frame(false, key: keyA);

            Assert.DoesNotContain(keyA, h.ActiveKeys);
            Assert.Contains(keyB, h.ActiveKeys);
            Assert.False(h.FrameCounts.ContainsKey(keyA));
            Assert.True(h.FrameCounts.ContainsKey(keyB));
        }

        [Fact]
        public void MultipleKeys_SamePartDifferentModuleIndexAreDistinct()
        {
            var h = new RcsHarness();
            ulong key0 = FlightRecorder.EncodeEngineKey(DefaultPid, 0);
            ulong key1 = FlightRecorder.EncodeEngineKey(DefaultPid, 1);
            Assert.NotEqual(key0, key1);

            h.RunToRecording(0.5f, key0);

            Assert.Contains(key0, h.ActiveKeys);
            Assert.DoesNotContain(key1, h.ActiveKeys);
            Assert.Single(h.Events);
        }

        [Fact]
        public void MultipleKeys_ThrottleReferencesDoNotBleedAcrossKeys()
        {
            var h = new RcsHarness();
            ulong keyA = FlightRecorder.EncodeEngineKey(10u, 0);
            ulong keyB = FlightRecorder.EncodeEngineKey(11u, 0);

            h.RunToRecording(0.2f, keyA);
            h.RunToRecording(0.9f, keyB);

            // A tiny move on A must not be judged against B's reference (which is far away).
            Assert.Empty(h.Frame(true, 0.2f + ThrottleDeadband * 0.5f, keyA));
            Assert.Equal(0.2, h.ThrottleMap[keyA], 5);
            Assert.Equal(0.9, h.ThrottleMap[keyB], 5);
        }

        #endregion

        #region Caller-seeded state (post-switch / resume baselines)

        [Fact]
        public void SeededBaseline_SuppressesASpuriousActivationOnResume()
        {
            // Mirrors ParsekFlight's post-switch baseline: an already-firing RCS module is
            // seeded at the threshold with its current power so resuming does not look like
            // a fresh activation.
            var h = new RcsHarness();
            h.FrameCounts[DefaultKey] = Threshold;
            h.ActiveKeys.Add(DefaultKey);
            h.ThrottleMap[DefaultKey] = 0.5f;

            for (int i = 0; i < 20; i++)
                Assert.Empty(h.Frame(true, 0.5f));

            Assert.Empty(h.Events);
        }

        [Fact]
        public void SeededBaseline_StillReportsARealThrottleChange()
        {
            var h = new RcsHarness();
            h.FrameCounts[DefaultKey] = Threshold;
            h.ActiveKeys.Add(DefaultKey);
            h.ThrottleMap[DefaultKey] = 0.5f;

            var emitted = h.Frame(true, 0.5f + ThrottleDeadband * 2f);

            Assert.Single(emitted);
            Assert.Equal(PartEventType.RCSThrottle, emitted[0].eventType);
        }

        [Fact]
        public void SeededBaseline_StillReportsTheStop()
        {
            var h = new RcsHarness();
            h.FrameCounts[DefaultKey] = Threshold;
            h.ActiveKeys.Add(DefaultKey);
            h.ThrottleMap[DefaultKey] = 0.5f;

            var stopped = h.Frame(false);

            Assert.Single(stopped);
            Assert.Equal(PartEventType.RCSStopped, stopped[0].eventType);
            Assert.False(h.HasAnyStateFor(DefaultKey));
        }

        [Fact]
        public void SeededCountWithoutMembership_ActivatesOnTheNextActiveFrame()
        {
            // Robustness: a count already past the threshold must not strand the key
            // un-activated just because it never landed exactly ON the threshold frame.
            var h = new RcsHarness();
            h.FrameCounts[DefaultKey] = Threshold + 5;

            var emitted = h.Frame(true, 0.35f);

            Assert.Single(emitted);
            Assert.Equal(PartEventType.RCSActivated, emitted[0].eventType);
            Assert.Equal(0.35, emitted[0].value, 5);
            Assert.Contains(DefaultKey, h.ActiveKeys);
        }

        [Fact]
        public void SeededSubThresholdCount_DeactivatingDropsStaleMembershipWithoutAnEvent()
        {
            // A key marked active but whose run never reached the threshold is stale state;
            // dropping it must be silent, not a phantom RCSStopped.
            var h = new RcsHarness();
            h.FrameCounts[DefaultKey] = Threshold - 1;
            h.ActiveKeys.Add(DefaultKey);
            h.ThrottleMap[DefaultKey] = 0.5f;

            Assert.Empty(h.Frame(false));

            Assert.Empty(h.Events);
            Assert.False(h.HasAnyStateFor(DefaultKey));
        }

        #endregion

        #region Event buffer and ordering

        [Fact]
        public void Events_AreAppendedWithoutDisturbingExistingBufferContents()
        {
            var h = new RcsHarness();
            var preexisting = new PartEvent
            {
                ut = 1.0,
                partPersistentId = 1u,
                eventType = PartEventType.EngineIgnited,
                partName = "prior",
                value = 1f,
                moduleIndex = 0
            };
            h.Events.Add(preexisting);

            h.RunToRecording(0.5f);

            Assert.Equal(2, h.Events.Count);
            Assert.Equal(PartEventType.EngineIgnited, h.Events[0].eventType);
            Assert.Equal("prior", h.Events[0].partName);
            Assert.Equal(PartEventType.RCSActivated, h.Events[1].eventType);
        }

        [Fact]
        public void Events_NeverExceedOnePerKeyPerFrame()
        {
            var h = new RcsHarness();
            // A deterministic but varied timeline: on/off runs of differing lengths with
            // power swinging across the deadband.
            var script = new (bool active, float power)[]
            {
                (true, 0.10f), (true, 0.90f), (true, 0.10f), (true, 0.50f),
                (true, 0.50f), (true, 0.51f), (true, 0.99f), (true, 0.00f),
                (true, 1.00f), (false, 0f),   (true, 0.30f), (true, 0.30f),
                (false, 0f),   (false, 0f),   (true, 0.70f), (true, 0.70f)
            };

            for (int rep = 0; rep < 6; rep++)
            {
                foreach (var step in script)
                {
                    var produced = h.Frame(step.active, step.power);
                    Assert.True(produced.Count <= 1,
                        $"one frame produced {produced.Count} events for a single key");
                }
            }
        }

        [Fact]
        public void Events_ShareTheFrameUtWhenTwoKeysTransitionOnTheSameFrame()
        {
            // Boundary-equal timestamps: both call sites pass one UT for the whole sweep,
            // so simultaneous transitions must still be two distinct, correctly tagged events.
            var h = new RcsHarness();
            h.UtStep = 0.0;
            ulong keyA = FlightRecorder.EncodeEngineKey(10u, 0);
            ulong keyB = FlightRecorder.EncodeEngineKey(11u, 0);

            for (int i = 0; i < Threshold; i++)
            {
                h.Frame(true, 0.5f, keyA, pid: 10u, moduleIndex: 0, partName: "A");
                h.Frame(true, 0.5f, keyB, pid: 11u, moduleIndex: 0, partName: "B");
            }

            Assert.Equal(2, h.Events.Count);
            Assert.Equal(h.Events[0].ut, h.Events[1].ut, 6);
            Assert.Equal(new[] { 10u, 11u }, h.Events.Select(e => e.partPersistentId).ToArray());
            Assert.All(h.Events, e => Assert.Equal(PartEventType.RCSActivated, e.eventType));
        }

        [Fact]
        public void Events_ForOneKeyAlwaysOpenWithActivatedAndCloseWithStopped()
        {
            var h = new RcsHarness();

            h.RunToRecording(0.3f);
            h.Frame(true, 0.3f + ThrottleDeadband * 3f);
            h.Frame(true, 0.3f);
            h.Frame(false);

            Assert.Equal(PartEventType.RCSActivated, h.Events.First().eventType);
            Assert.Equal(PartEventType.RCSStopped, h.Events.Last().eventType);
            Assert.All(h.Events.Skip(1).Take(h.Events.Count - 2),
                e => Assert.Equal(PartEventType.RCSThrottle, e.eventType));
        }

        [Fact]
        public void Events_ANeverFiringModuleProducesNothingOverALongIdleRun()
        {
            var h = new RcsHarness();

            for (int i = 0; i < 500; i++)
                Assert.Empty(h.Frame(false));

            Assert.Empty(h.Events);
            Assert.Empty(h.FrameCounts);
        }

        #endregion
    }
}
