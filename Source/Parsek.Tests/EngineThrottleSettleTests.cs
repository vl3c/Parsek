using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The settled-throttle rule of <see cref="FlightRecorder.CheckEngineTransition"/>: one
    /// EngineThrottle event at the value a moving throttle comes to rest on, on top of the 0.05
    /// step, for both recorders (todo ENGINE-THROTTLE-EVENT-STEP).
    /// </summary>
    [Collection("Sequential")]
    public class EngineThrottleSettleTests : IDisposable
    {
        private const double Dt = 0.02;
        private const uint Pid = 100000u;
        private static readonly ulong Key = FlightRecorder.EncodeEngineKey(Pid, 0);

        private readonly List<string> logLines = new List<string>();

        public EngineThrottleSettleTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose() => ParsekLog.ResetTestOverrides();

        /// <summary>One recorder's engine state, fed a power per physics frame.</summary>
        private sealed class Feed
        {
            internal readonly HashSet<ulong> Active = new HashSet<ulong>();
            internal readonly Dictionary<ulong, float> Last = new Dictionary<ulong, float>();
            internal readonly Dictionary<ulong, EngineThrottleStill> Still;
            internal readonly List<PartEvent> Events = new List<PartEvent>();
            internal double Ut = 1000.0;
            internal double Dt = EngineThrottleSettleTests.Dt;

            internal Feed(bool settle = true)
            {
                if (settle) Still = new Dictionary<ulong, EngineThrottleStill>();
            }

            internal void Frame(float power, bool ignited = true, ulong? key = null)
            {
                ulong k = key ?? Key;
                FlightRecorder.DecodeEngineKey(k, out uint pid, out int midx);
                FlightRecorder.CheckEngineTransition(
                    k, pid, midx, "engine", ignited, power, Active, Last, Ut, Events, Still);
            }

            internal void Hold(float power, double seconds)
            {
                int n = (int)Math.Round(seconds / Dt);
                for (int i = 0; i < n; i++) { Frame(power); Ut += Dt; }
            }

            /// <summary>Linear ramp from a to b over the given seconds (last frame short of b).</summary>
            internal void Ramp(float a, float b, double seconds)
            {
                int n = (int)Math.Round(seconds / Dt);
                for (int i = 0; i < n; i++) { Frame(a + (b - a) * i / n); Ut += Dt; }
            }

            /// <summary>Stock's engine response (ModuleEngines.UpdateThrottle, useEngineResponseTime).</summary>
            internal float Spool(float from, float to, float speed, double seconds)
            {
                float x = from;
                int n = (int)Math.Round(seconds / Dt);
                for (int i = 0; i < n; i++)
                {
                    x = Math.Abs(x - to) <= 0.0001f ? to : x + (to - x) * speed * (float)Dt;
                    Frame(x);
                    Ut += Dt;
                }
                return x;
            }

            internal List<PartEvent> Throttles =>
                Events.Where(e => e.eventType == PartEventType.EngineThrottle).ToList();
        }

        private List<string> SettledLines =>
            logLines.Where(l => l.Contains("[Recorder]") && l.Contains("Engine throttle settled:")).ToList();

        // --- the rule ------------------------------------------------------------------------

        [Fact]
        public void SpoolTo30Percent_EndsOnTheSettledValue_WithOneExtraEvent()
        {
            // The filed case: a RAPIER (0.2 /s) at 30 % ended on a recorded 0.25.
            var old = new Feed(settle: false);
            old.Spool(0f, 0.3f, 0.2f, 60.0);
            var feed = new Feed();
            feed.Spool(0f, 0.3f, 0.2f, 60.0);

            Assert.InRange(old.Events.Last().value, 0.24f, 0.26f);
            Assert.Equal(old.Events.Count + 1, feed.Events.Count);
            for (int i = 0; i < old.Events.Count; i++)
            {
                Assert.Equal(old.Events[i].ut, feed.Events[i].ut);
                Assert.Equal(old.Events[i].value, feed.Events[i].value);
            }
            PartEvent settled = feed.Events.Last();
            Assert.Equal(PartEventType.EngineThrottle, settled.eventType);
            Assert.Equal(Pid, settled.partPersistentId);
            Assert.Equal(0, settled.moduleIndex);
            Assert.Equal("engine", settled.partName);
            Assert.InRange(settled.value, 0.295f, 0.3f);
            string line = Assert.Single(SettledLines);
            Assert.Contains("pid=100000 midx=0 part='engine' last=0.25", line);
            Assert.Contains("settled=0.29", line);
        }

        [Theory]
        [InlineData(0.5f, 1)]   // Panther
        [InlineData(0.2f, 1)]   // RAPIER, Whiplash
        [InlineData(0.12f, 1)]  // Juno, Wheesley
        [InlineData(0.06f, 2)]  // Goliath: still creeping when first still, then one correction
        public void EveryStockJetSpool_WritesOneOrTwoSettledEvents(float speed, int expected)
        {
            foreach (float target in new[] { 0.3f, 1.0f })
            {
                logLines.Clear();
                var feed = new Feed();
                feed.Spool(0f, target, speed, 200.0);
                Assert.Equal(expected, SettledLines.Count);
                Assert.InRange(feed.Events.Last().value, target - 0.01f, target);
            }
        }

        [Fact]
        public void AnInstantEngine_SettlesOneSecondAfterItStops()
        {
            var feed = new Feed();
            feed.Hold(1.0f, 2.0);
            feed.Ramp(1.0f, 0.65f, 1.0);
            double stopUt = feed.Ut;
            int before = feed.Events.Count;
            feed.Hold(0.65f, 30.0);

            Assert.Equal(before + 1, feed.Events.Count);
            PartEvent settled = feed.Events.Last();
            Assert.Equal(0.65f, settled.value);
            Assert.InRange(settled.ut - stopUt, 0.9, 1.1);
        }

        [Fact]
        public void PhysicsWarpFrames_SettleOnGameTime()
        {
            var feed = new Feed { Dt = 0.08 };
            feed.Hold(1.0f, 2.0);
            feed.Hold(0.9f, 0.08);
            double stopUt = feed.Ut;
            feed.Hold(0.87f, 30.0);

            Assert.Equal(0.87f, feed.Events.Last().value);
            Assert.InRange(feed.Events.Last().ut - stopUt, 0.9, 1.2);
            Assert.Single(SettledLines);
        }

        [Theory]
        [InlineData(0.02f)]
        [InlineData(0.0012f)]
        public void AMovingThrottle_NeverSettles(float perSecond)
        {
            var feed = new Feed();
            feed.Hold(0.5f, 0.02);
            float span = Math.Min(0.4f, perSecond * 60f);
            feed.Ramp(0.5f, 0.5f + span, span / perSecond);

            Assert.Empty(SettledLines);
        }

        [Fact]
        public void JitterInsideTheNoise_StillSettles_AndOutsideItDoesNot()
        {
            foreach (float amplitude in new[] { 0.0009f, 0.0015f })
            {
                logLines.Clear();
                var feed = new Feed();
                feed.Hold(0.5f, 0.5);
                feed.Ramp(0.5f, 0.53f, 0.2);
                for (int i = 0; i < 200; i++)
                {
                    feed.Frame(0.53f + (i % 2 == 0 ? 0f : amplitude));
                    feed.Ut += Dt;
                }
                if (amplitude < FlightRecorder.EngineThrottleStillNoise) Assert.Single(SettledLines);
                else Assert.Empty(SettledLines);
            }
        }

        [Fact]
        public void QuickTaps_WriteNothing_UntilTheThrottleRests()
        {
            var feed = new Feed();
            feed.Hold(0.5f, 2.0);
            // Four 1 % taps half a second apart stay inside one 0.05 step.
            float value = 0.5f;
            for (int i = 0; i < 4; i++)
            {
                value += 0.011f;
                feed.Hold(value, 0.5);
            }
            Assert.Single(feed.Events);

            feed.Hold(value, 5.0);
            Assert.Equal(2, feed.Events.Count);
            Assert.Equal(value, feed.Events[1].value);
        }

        [Fact]
        public void ARestWithinTheEpsilon_WritesNothing()
        {
            var feed = new Feed();
            feed.Hold(0.5f, 2.0);
            feed.Hold(0.509f, 10.0);
            Assert.Single(feed.Events);
            Assert.Empty(SettledLines);
        }

        [Fact]
        public void ARest_IsWrittenOnce_AndInBothDirections()
        {
            var feed = new Feed();
            feed.Hold(0.5f, 2.0);
            feed.Hold(0.53f, 60.0);
            feed.Hold(0.50f, 60.0);
            Assert.Equal(new[] { 0.5f, 0.53f, 0.5f }, feed.Events.Select(e => e.value).ToArray());
        }

        [Fact]
        public void ThrottleCut_WritesOnlyTheShutdown()
        {
            var feed = new Feed();
            feed.Hold(0.5f, 2.0);
            feed.Ramp(0.5f, 0.53f, 0.2);
            for (int i = 0; i < 100; i++) { feed.Frame(0f, ignited: false); feed.Ut += Dt; }

            Assert.Equal(new[] { PartEventType.EngineIgnited, PartEventType.EngineShutdown },
                feed.Events.Select(e => e.eventType).ToArray());
            Assert.Empty(feed.Still);
            Assert.Empty(SettledLines);
        }

        [Fact]
        public void AShutdownInsideTheWindow_AndARelight_StartAFreshWindow()
        {
            var feed = new Feed();
            feed.Hold(0.5f, 2.0);
            feed.Hold(0.53f, 0.9);
            feed.Frame(0f, ignited: false);
            feed.Ut += Dt;
            double relightUt = feed.Ut;
            feed.Hold(0.53f, 0.2);
            feed.Hold(0.56f, 5.0);

            Assert.Equal(new[]
            {
                PartEventType.EngineIgnited, PartEventType.EngineShutdown,
                PartEventType.EngineIgnited, PartEventType.EngineThrottle,
            }, feed.Events.Select(e => e.eventType).ToArray());
            Assert.Equal(0.56f, feed.Events[3].value);
            Assert.InRange(feed.Events[3].ut - relightUt, 1.1, 1.3);
        }

        [Fact]
        public void TwoEngines_SettleOnTheirOwnValues()
        {
            ulong keyB = FlightRecorder.EncodeEngineKey(Pid, 1);
            var feed = new Feed();
            for (int i = 0; i < 400; i++)
            {
                float a = 0.50f + 0.03f * Math.Min(1f, i / 20f);
                float b = 0.80f - 0.02f * Math.Min(1f, i / 200f);
                feed.Frame(a);
                feed.Frame(b, key: keyB);
                feed.Ut += Dt;
            }

            PartEvent eventA = Assert.Single(feed.Throttles.Where(e => e.moduleIndex == 0));
            PartEvent eventB = Assert.Single(feed.Throttles.Where(e => e.moduleIndex == 1));
            Assert.InRange(eventA.value, 0.5299f, 0.5301f);
            Assert.InRange(eventB.value, 0.7799f, 0.7801f);
            Assert.True(eventB.ut - eventA.ut > 3.0);
        }

        [Fact]
        public void ASeededEngine_IsNotArmedUntilItWritesItsOwnEvent()
        {
            var feed = new Feed();
            feed.Active.Add(Key);
            feed.Last[Key] = 0.50f;
            feed.Hold(0.53f, 10.0);
            Assert.Empty(feed.Events);

            // 0.05 past the seed arms it; from there it settles.
            feed.Ramp(0.53f, 0.58f, 0.5);
            feed.Hold(0.58f, 5.0);
            Assert.Equal(2, feed.Events.Count);
            Assert.Equal(0.58f, feed.Events[1].value);
        }

        [Fact]
        public void ANonPositiveValueOnEitherSide_WritesNoSettledEvent()
        {
            // Ignited with power 0 is reachable (finalThrust with a zero maxThrust); a settled
            // event there would end or start a burn for the readers.
            var feed = new Feed();
            feed.Hold(0.04f, 1.0);
            feed.Hold(0f, 10.0);
            Assert.Single(feed.Events);

            feed.Last[Key] = 0f;
            feed.Hold(0.04f, 10.0);
            Assert.Single(feed.Events);
        }

        [Fact]
        public void AClockThatStepsBack_RestartsTheWindow()
        {
            var still = new Dictionary<ulong, EngineThrottleStill>
            {
                [Key] = new EngineThrottleStill { reference = 0.5f, sinceUT = 100.0 },
            };
            Assert.False(FlightRecorder.AdvanceEngineThrottleStill(Key, 0.5f, 90.0, still));
            Assert.Equal(90.0, still[Key].sinceUT);
            Assert.False(FlightRecorder.AdvanceEngineThrottleStill(Key, 0.5f, 90.98, still));
            Assert.True(FlightRecorder.AdvanceEngineThrottleStill(Key, 0.5f, 91.0, still));
        }

        [Fact]
        public void WithoutTheStillMap_TheOldEventsAreUnchanged()
        {
            var feed = new Feed(settle: false);
            feed.Hold(0.5f, 2.0);
            feed.Hold(0.53f, 30.0);
            feed.Hold(0.56f, 30.0);
            Assert.Equal(new[] { 0.5f, 0.56f }, feed.Events.Select(e => e.value).ToArray());
            Assert.Empty(SettledLines);
        }

        [Fact]
        public void TheConstants_AreTheDesignedOnes()
        {
            Assert.Equal(0.05f, FlightRecorder.EngineThrottleDeadband);
            Assert.Equal(1.0, FlightRecorder.EngineThrottleSettleSeconds);
            Assert.Equal(0.001f, FlightRecorder.EngineThrottleStillNoise);
            Assert.Equal(0.01f, FlightRecorder.EngineThrottleSettleEpsilon);
        }

        // --- the readers ---------------------------------------------------------------------

        private static Recording BurnThenCoast(bool settle)
        {
            var feed = new Feed(settle);
            feed.Spool(0f, 0.3f, 0.2f, 60.0);
            feed.Frame(0f, ignited: false);
            var rec = new Recording { RecordingId = "r", VesselName = "V" };
            rec.Points.Add(new TrajectoryPoint { ut = 1000.0, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = feed.Ut + 500.0, bodyName = "Kerbin" });
            rec.PartEvents.AddRange(feed.Events);
            return rec;
        }

        [Fact]
        public void TheTailTrim_ReadsTheSameLastInterestingUT()
        {
            Recording without = BurnThenCoast(settle: false), with = BurnThenCoast(settle: true);
            Assert.Equal(without.PartEvents.Count + 1, with.PartEvents.Count);
            Assert.Equal(RecordingOptimizer.FindLastInterestingUT(without),
                RecordingOptimizer.FindLastInterestingUT(with));
        }

        [Fact]
        public void TheNoOpClassifier_FirstSeesTheSameMeaningfulEvent()
        {
            PassiveFirst(BurnThenCoast(settle: false), out string a, out int firstA);
            PassiveFirst(BurnThenCoast(settle: true), out string b, out int firstB);
            Assert.Equal(a, b);
            Assert.Equal(firstA, firstB);
        }

        private static void PassiveFirst(Recording rec, out string type, out int index)
        {
            for (int i = 0; i < rec.PartEvents.Count; i++)
                if (SwitchSegmentNoOpClassifier.IsMeaningfulPartEvent(rec.PartEvents[i]))
                {
                    type = rec.PartEvents[i].eventType.ToString();
                    index = i;
                    return;
                }
            type = null;
            index = -1;
        }

        [Fact]
        public void EverySettledEvent_FollowsAnEventOfTheSameEngine_AndPrecedesItsShutdown()
        {
            var random = new Random(20261124);
            for (int run = 0; run < 40; run++)
            {
                var feed = new Feed();
                float power = 0.4f;
                for (int step = 0; step < 30; step++)
                {
                    float target = (float)random.NextDouble();
                    if (random.Next(4) == 0)
                    {
                        for (int i = 0; i < 10; i++) { feed.Frame(0f, ignited: false); feed.Ut += Dt; }
                        power = 0.01f + target * 0.5f;
                        continue;
                    }
                    if (random.Next(2) == 0) target = Math.Max(0.02f, Math.Min(1f, power + (target - 0.5f) * 0.08f));
                    feed.Ramp(power, target, 0.1 + random.NextDouble());
                    power = target;
                    feed.Hold(power, random.NextDouble() * 4.0);
                }
                feed.Frame(0f, ignited: false);

                bool on = false;
                double lastUt = double.MinValue;
                foreach (PartEvent e in feed.Events)
                {
                    Assert.True(e.ut >= lastUt);
                    lastUt = e.ut;
                    if (e.eventType == PartEventType.EngineIgnited) on = true;
                    else if (e.eventType == PartEventType.EngineShutdown) on = false;
                    else Assert.True(on, "EngineThrottle outside a burn");
                }
                if (feed.Events.Count > 0)
                    Assert.Equal(PartEventType.EngineShutdown, feed.Events.Last().eventType);
            }
            Assert.NotEmpty(SettledLines);
        }

        // --- the two recorders and the activity detector ------------------------------------

        private static string Source(params string[] parts) =>
            Regex.Replace(
                File.ReadAllText(Path.Combine(
                    new[] { SyntheticRecordingTests.ResolveProjectRoot(), "Source", "Parsek" }.Concat(parts).ToArray())),
                @"//[^\r\n]*", string.Empty);

        private static List<string> CallArguments(string text, string method)
        {
            var found = new List<string>();
            foreach (Match m in Regex.Matches(text, @"(?<!void )\b" + method + @"\("))
            {
                int i = m.Index + m.Length, depth = 1;
                int start = i;
                while (i < text.Length && depth > 0)
                {
                    if (text[i] == '(') depth++;
                    else if (text[i] == ')') depth--;
                    i++;
                }
                found.Add(Regex.Replace(text.Substring(start, i - start - 1), @"\s+", " "));
            }
            return found;
        }

        [Fact]
        public void BothRecorders_AndTheParitySweep_RunTheRuleOnTheirOwnState()
        {
            Assert.EndsWith(", engineThrottleStill", Assert.Single(
                CallArguments(Source("Recording", "FlightRecorder.EngineAndRcs.cs"), "CheckEngineTransition")));
            Assert.EndsWith(", state.engineThrottleStill", Assert.Single(
                CallArguments(Source("Recording", "BackgroundRecorder.PartEventPolling.cs"), "CheckEngineTransition")));
            Assert.EndsWith(", fx.EngineStill", Assert.Single(
                CallArguments(Source("TestCommands", "PartParityFxDriver.cs"), "CheckEngineTransition")));
        }

        [Fact]
        public void ThePostSwitchActivityWatch_DoesNotRunTheRule()
        {
            // That watch reads an emitted engine event as player activity.
            string args = Assert.Single(CallArguments(
                Source("Controllers", "ParsekFlightPostSwitchAutoRecord.cs"), "CheckEngineTransition"));
            Assert.DoesNotContain("Still", args);
            Assert.Equal(11, args.Split(',').Length);
        }

        [Fact]
        public void TheArmedKeys_AreClearedWithTheActiveEngines()
        {
            Assert.Matches(@"lastThrottle\.Clear\(\);\s*engineThrottleStill\.Clear\(\);",
                Source("Recording", "FlightRecorder.cs"));
            Assert.Matches(@"loadedState\.lastThrottle\.Clear\(\);\s*loadedState\.engineThrottleStill\.Clear\(\);",
                Source("Recording", "BackgroundRecorder.cs"));
            Assert.Matches(@"engineThrottleStill\.Clear\(\);\s*ResetPartEventTrackingState\(v",
                Source("Recording", "FlightRecorder.StartRecording.cs"));
        }
    }
}
