using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Engine / RCS nozzle glow (FXModuleAnimateThrottle / FXModuleAnimateRCS), the lamp light
    /// values and the missing heat clip: the pure decisions behind the ghost visuals.
    /// </summary>
    [Collection("Sequential")]
    public class GhostNozzleGlowTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostNozzleGlowTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose() => ParsekLog.ResetTestOverrides();

        private const uint Pid = 4242u;
        private const float BellRate = 0.025f;

        private static NozzleGlowInfo Glow(bool rcs, float rate, float scale, params int[] modules)
        {
            var g = new NozzleGlowInfo
            {
                partPersistentId = Pid,
                moduleName = rcs ? "FXModuleAnimateRCS" : "FXModuleAnimateThrottle",
                animationName = "Glow",
                isRcs = rcs,
                approachRate = rate,
                powerScale = scale,
                moduleKeys = new ulong[modules.Length],
                modulePowers = new float[modules.Length],
            };
            for (int i = 0; i < modules.Length; i++)
                g.moduleKeys[i] = FlightRecorder.EncodeEngineKey(Pid, modules[i]);
            return g;
        }

        private static GhostPlaybackState StateWith(double startUT, params NozzleGlowInfo[] glows)
        {
            var state = new GhostPlaybackState
            {
                heatInfos = new Dictionary<uint, HeatGhostInfo>
                {
                    [Pid] = new HeatGhostInfo
                    {
                        partPersistentId = Pid,
                        nozzleGlows = new List<NozzleGlowInfo>(glows),
                    },
                },
            };
            GhostPlaybackLogic.BindNozzleGlows(state,
                double.IsNaN(startUT) ? null : Row(startUT, startUT + 1000.0), _ => null);
            return state;
        }

        private static Recording Row(double startUT, double endUT, params PartEvent[] events)
        {
            var rec = new Recording { ExplicitStartUT = startUT, ExplicitEndUT = endUT };
            rec.PartEvents.AddRange(events);
            return rec;
        }

        private static PartEvent Evt(PartEventType type, double ut, float value = 0f, int module = 0, uint pid = Pid)
            => new PartEvent
            {
                ut = ut, eventType = type, partPersistentId = pid, moduleIndex = module, value = value, partName = "engine",
            };

        private static void Apply(GhostPlaybackState state, PartEvent evt)
            => GhostPlaybackLogic.ApplyNozzleGlowEvent(state, evt);

        // The appliers around the glow write Unity objects (ECalls), so the walk cannot be driven
        // headless: the one call that feeds it and the three places that advance it are pinned.
        [Fact]
        public void Wiring_TheWalkFeedsTheGlows_AndEveryFrameAdvancesThem()
        {
            string walk = Source("GhostPlaybackLogic.PartEvents.cs");
            int loop = walk.IndexOf("while (evtIdx < rec.PartEvents.Count", StringComparison.Ordinal);
            int restart = walk.IndexOf("RestartNozzleGlows(state);", StringComparison.Ordinal);
            int feed = walk.IndexOf("ApplyNozzleGlowEvent(state, evt);", StringComparison.Ordinal);
            int advance = walk.LastIndexOf("UpdateNozzleGlows(state, currentUT);", StringComparison.Ordinal);
            Assert.True(restart > 0 && restart < loop && loop < feed && feed < advance);
            string frame = Source("GhostPlaybackEngine.InRangeRender.cs");
            int body = frame.IndexOf("internal static void ApplyPartStateForFrame", StringComparison.Ordinal);
            int end = frame.IndexOf("internal static void StopVisualFxForFrame", StringComparison.Ordinal);
            int call = frame.IndexOf("GhostPlaybackLogic.UpdateNozzleGlows(state, ut);", StringComparison.Ordinal);
            Assert.True(body > 0 && call > body && call < end);
            Assert.Contains("BindNozzleGlows(state, traj);", Source("GhostPlaybackLogic.GhostInfo.cs"));
            // The Space Center passes no trajectory to the shared populate (no orphan auto-start
            // there), so it binds the glows to the recording itself.
            Assert.Contains("GhostPlaybackLogic.BindNozzleGlows(state, rec);",
                Source(System.IO.Path.Combine("..", "Controllers", "ParsekKSC.cs")));
            int early = walk.IndexOf("rec.PartEvents.Count == 0)", StringComparison.Ordinal);
            int earlyUpdate = walk.IndexOf("UpdateNozzleGlows(state, currentUT);", StringComparison.Ordinal);
            Assert.True(early > 0 && earlyUpdate > early && earlyUpdate < loop);
            string loopReset = Source("GhostPlaybackLogic.Visibility.cs");
            Assert.True(loopReset.IndexOf("RestartNozzleGlows(state);", StringComparison.Ordinal)
                > loopReset.IndexOf("ApplyHeatState(state, coldEvt, HeatLevel.Cold);", StringComparison.Ordinal));
        }

        private static string Source(string file) => System.IO.File.ReadAllText(System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "Source", "Parsek", "Ghost", file));

        #region Rate and level

        [Fact]
        public void Rate_IsStocksPerStepLerpAsAContinuousRate()
        {
            // 1000 stock steps of Lerp(x, 1, responseSpeed * 25 * 0.02) against the closed form.
            foreach (float response in new[] { 0.00025f, 0.001f, 0.002f, 0.01f, 0.5f, 1f })
            {
                double step = response * 25.0 * 0.02, level = 0.0;
                for (int i = 0; i < 1000; i++) level += (1.0 - level) * step;
                float rate = GhostPlaybackLogic.ComputeNozzleGlowRate(response);
                float got = GhostPlaybackLogic.ComputeNozzleGlowLevel(0f, 1f, rate, 20.0);
                Assert.Equal(level, got, 3);
            }
        }

        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(-1f, 0f)]
        [InlineData(float.NaN, 0f)]
        [InlineData(2f, float.PositiveInfinity)]
        [InlineData(5f, float.PositiveInfinity)]
        public void Rate_EdgeValues(float response, float expected)
            => Assert.Equal(expected, GhostPlaybackLogic.ComputeNozzleGlowRate(response));

        [Fact]
        public void Level_HoldsBeforeItsStart_SnapsWhenInfinite_AndSettlesOnTheTarget()
        {
            Assert.Equal(0.3f, GhostPlaybackLogic.ComputeNozzleGlowLevel(0.3f, 1f, BellRate, -5.0));
            Assert.Equal(0.3f, GhostPlaybackLogic.ComputeNozzleGlowLevel(0.3f, 1f, BellRate, double.NaN));
            Assert.Equal(0.3f, GhostPlaybackLogic.ComputeNozzleGlowLevel(0.3f, 1f, 0f, 100.0));
            Assert.Equal(1f, GhostPlaybackLogic.ComputeNozzleGlowLevel(0.3f, 1f, float.PositiveInfinity, 0.1));
            Assert.Equal(1f, GhostPlaybackLogic.ComputeNozzleGlowLevel(0f, 1f, BellRate, 1000.0));
            // Cooling: an infinitely old shutdown reads exactly 0, never a tiny glow.
            Assert.Equal(0f, GhostPlaybackLogic.ComputeNozzleGlowLevel(1f, 0f, BellRate, 1000.0));
            Assert.Equal(Math.Exp(-1.0), GhostPlaybackLogic.ComputeNozzleGlowLevel(1f, 0f, BellRate, 40.0), 4);
        }

        [Fact]
        public void Target_IsTheStrongestModuleThroughTheScale_Clamped()
        {
            Assert.Equal(0f, GhostPlaybackLogic.ResolveNozzleGlowTarget(null, 1f));
            Assert.Equal(0f, GhostPlaybackLogic.ResolveNozzleGlowTarget(new[] { 0f, 0f }, 1f));
            Assert.Equal(0.6f, GhostPlaybackLogic.ResolveNozzleGlowTarget(new[] { 0f, 0.6f }, 1f));
            Assert.Equal(0.444, GhostPlaybackLogic.ResolveNozzleGlowTarget(new[] { 0.222f }, 2f), 3);
            Assert.Equal(1f, GhostPlaybackLogic.ResolveNozzleGlowTarget(new[] { 1f }, 24f));
            Assert.Equal(0f, GhostPlaybackLogic.ResolveNozzleGlowTarget(new[] { 1f }, float.NaN));
        }

        [Fact]
        public void Color_InterpolatesBetweenTheSamples_AndTakesTheEnds()
        {
            var samples = new[] { new Color(0f, 0f, 0f, 1f), new Color(0.2f, 0f, 0f, 1f), new Color(1f, 0.5f, 0f, 1f) };
            Assert.Equal(samples[0], GhostPlaybackLogic.ResolveNozzleGlowColor(samples, 0f));
            Assert.Equal(samples[2], GhostPlaybackLogic.ResolveNozzleGlowColor(samples, 1f));
            Assert.Equal(samples[2], GhostPlaybackLogic.ResolveNozzleGlowColor(samples, 7f));
            Color quarter = GhostPlaybackLogic.ResolveNozzleGlowColor(samples, 0.25f);
            Assert.Equal(0.1, quarter.r, 4);
            Color upper = GhostPlaybackLogic.ResolveNozzleGlowColor(samples, 0.75f);
            Assert.Equal(0.6, upper.r, 4);
            Assert.Equal(0.25, upper.g, 4);
            Assert.Equal(samples[0], GhostPlaybackLogic.ResolveNozzleGlowColor(new[] { samples[0] }, 0.5f));
            Assert.Equal(Color.black, GhostPlaybackLogic.ResolveNozzleGlowColor(null, 0.5f));
        }

        [Fact]
        public void Write_FirstLevel_VisibleChange_AndTheLastStepOntoTheTarget()
        {
            Assert.True(GhostPlaybackLogic.ShouldWriteNozzleGlow(float.NaN, 0f, 0f));
            Assert.False(GhostPlaybackLogic.ShouldWriteNozzleGlow(0.5f, 0.5f, 1f));
            Assert.False(GhostPlaybackLogic.ShouldWriteNozzleGlow(0.5f, 0.501f, 1f));
            Assert.True(GhostPlaybackLogic.ShouldWriteNozzleGlow(0.5f, 0.51f, 1f));
            Assert.True(GhostPlaybackLogic.ShouldWriteNozzleGlow(0.9995f, 1f, 1f));
        }

        #endregion

        #region Which modules drive a clip

        [Fact]
        public void EngineIndices_ByName_ByIndex_AndMultiMode()
        {
            var panther = new List<string> { "Dry", "Wet" };
            Assert.Equal(new[] { 1 }, GhostVisualBuilder.ResolveNozzleGlowEngineIndices(true, false, "Wet", 0, panther, true));
            Assert.Equal(new[] { 0 }, GhostVisualBuilder.ResolveNozzleGlowEngineIndices(true, false, null, 0, panther, true));
            Assert.Equal(new[] { 1 }, GhostVisualBuilder.ResolveNozzleGlowEngineIndices(true, false, "", 1, panther, true));
            // RAPIER: preferMultiMode follows whichever mode runs.
            Assert.Equal(new[] { 0, 1 }, GhostVisualBuilder.ResolveNozzleGlowEngineIndices(true, true, null, 0, panther, true));
            // preferMultiMode without a MultiModeEngine falls through to the named / indexed engine.
            Assert.Equal(new[] { 0 }, GhostVisualBuilder.ResolveNozzleGlowEngineIndices(true, true, null, 0, panther, false));
        }

        [Fact]
        public void EngineIndices_UnresolvedOrThrottleDriven_AreThePartsEngines_AndNoEngineIsNone()
        {
            var two = new List<string> { "A", "B" };
            Assert.Equal(new[] { 0, 1 }, GhostVisualBuilder.ResolveNozzleGlowEngineIndices(true, false, "Missing", 0, two, false));
            Assert.Equal(new[] { 0, 1 }, GhostVisualBuilder.ResolveNozzleGlowEngineIndices(true, false, null, 9, two, false));
            Assert.Equal(new[] { 0, 1 }, GhostVisualBuilder.ResolveNozzleGlowEngineIndices(false, false, "A", 0, two, false));
            Assert.Empty(GhostVisualBuilder.ResolveNozzleGlowEngineIndices(true, false, "A", 0, null, true));
        }

        [Theory]
        [InlineData(4, 4, 4, true)]
        [InlineData(4, 3, 4, false)]
        [InlineData(4, 4, 5, false)]
        [InlineData(0, 0, 0, false)]
        public void RcsGlow_RunsOnlyWithOneClipPerThruster(int animations, int withClip, int thrusters, bool expected)
            => Assert.Equal(expected, GhostVisualBuilder.RcsGlowClipsMatchThrusters(animations, withClip, thrusters));

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, true)]
        [InlineData(2, false)]
        [InlineData(9, false)]
        public void RcsGlow_OnlyWhereTheRecordedPowerIsOneNozzles(int thrusters, bool expected)
            => Assert.Equal(expected, GhostVisualBuilder.RcsGlowPowerIsPerNozzle(thrusters));

        [Fact]
        public void PersistedLevel_IsTheNthModulesAnimState_OrTheMeanOfTheNozzles()
        {
            var part = new ConfigNode("PART");
            void Module(string name, string key, string value)
            {
                ConfigNode m = part.AddNode("MODULE");
                m.AddValue("name", name);
                if (key != null) m.AddValue(key, value);
            }
            Module("FXModuleAnimateThrottle", "animState", "0.25");
            Module("ModuleEngines", null, null);
            Module("FXModuleAnimateThrottle", "animState", "1.75");
            Module("FXModuleAnimateThrottle", "animState", "NaN");
            Module("FXModuleAnimateRCS", "animStates", "1,0,0.5,0.5");

            Assert.Equal(0.25f, GhostVisualBuilder.ReadPersistedGlowLevel(part, "FXModuleAnimateThrottle", 0, false));
            Assert.Equal(1f, GhostVisualBuilder.ReadPersistedGlowLevel(part, "FXModuleAnimateThrottle", 1, false));
            Assert.Equal(0f, GhostVisualBuilder.ReadPersistedGlowLevel(part, "FXModuleAnimateThrottle", 2, false));
            Assert.Equal(0f, GhostVisualBuilder.ReadPersistedGlowLevel(part, "FXModuleAnimateThrottle", 3, false));
            Assert.Equal(0.5f, GhostVisualBuilder.ReadPersistedGlowLevel(part, "FXModuleAnimateRCS", 0, true));
            Assert.Equal(0f, GhostVisualBuilder.ReadPersistedGlowLevel(null, "FXModuleAnimateRCS", 0, true));
        }

        [Fact]
        public void StampPersistedLevels_TakesTheSnapshotsUT_AndStartsAnEndSnapshotBuildCold()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("lastUT", "1234.5");
            NozzleGlowInfo g = Glow(false, BellRate, 1f, 0);
            g.initialLevel = 0.8f;
            var heat = new HeatGhostInfo { nozzleGlows = new List<NozzleGlowInfo> { g } };
            GhostVisualBuilder.StampPersistedGlowLevels(heat, snapshot, trusted: true);
            Assert.Equal(0.8f, g.initialLevel);
            Assert.Equal(1234.5, g.initialUT);
            GhostVisualBuilder.StampPersistedGlowLevels(heat, snapshot, trusted: false);
            Assert.Equal(0f, g.initialLevel);
            Assert.True(double.IsNaN(g.initialUT));
            GhostVisualBuilder.StampPersistedGlowLevels(null, snapshot, trusted: true);
            GhostVisualBuilder.StampPersistedGlowLevels(new HeatGhostInfo(), snapshot, trusted: true);
        }

        [Theory]
        [InlineData("203697.68848021631", 203697.68848021631)]
        [InlineData("0", double.NaN)]
        [InlineData("-1", double.NaN)]
        [InlineData("x", double.NaN)]
        [InlineData(null, double.NaN)]
        public void SnapshotUT_IsLastUT_OrUnknown(string raw, double expected)
        {
            var node = new ConfigNode("VESSEL");
            if (raw != null) node.AddValue("lastUT", raw);
            Assert.Equal(expected, GhostVisualBuilder.ReadSnapshotUT(node));
            Assert.True(double.IsNaN(GhostVisualBuilder.ReadSnapshotUT(null)));
        }

        #endregion

        #region Throttle glow (FXModuleThrottleEffects driving a ModuleColorChanger)

        // ReStock: heatColor at animRate 0.05 (aerospike) or 0.025, throttleColor at 1.
        private static NozzleGlowInfo Ramp(float animRate, bool kspcf, float initialState = 0f, bool animState = false, bool useRate = true)
            => GhostVisualBuilder.NewThrottleGlow(Pid,
                new ThrottleGlowDriver { moduleIds = new[] { "heatColor" }, moduleIndices = new[] { 0 }, responseSpeed = 1f },
                new ThrottleGlowCurve { moduleId = "heatColor", animRate = animRate, useRate = useRate, animState = animState },
                initialState, kspcf);

        [Fact]
        public void Ramp_IsStocksRateState_LinearAndClamped()
        {
            Assert.Equal((double)0.62f, GhostPlaybackLogic.ComputeNozzleGlowRamp(0f, 1f, 0.05f, 12.4), 4);
            Assert.Equal(1f, GhostPlaybackLogic.ComputeNozzleGlowRamp(0f, 1f, 0.05f, 25.0));
            Assert.Equal((double)0.5f, GhostPlaybackLogic.ComputeNozzleGlowRamp(0.75f, 0f, 0.025f, 10.0), 4);
            Assert.Equal(0f, GhostPlaybackLogic.ComputeNozzleGlowRamp(0.75f, 0f, 0.025f, 1000.0));
            // SetScalar switches on at 0.5 exactly.
            Assert.Equal(1f, GhostPlaybackLogic.ComputeNozzleGlowRamp(0f, 0.5f, 1f, 2.0));
            Assert.Equal(0f, GhostPlaybackLogic.ComputeNozzleGlowRamp(0f, 0.499f, 1f, 2.0));
            Assert.Equal(0.3f, GhostPlaybackLogic.ComputeNozzleGlowRamp(0.3f, 1f, 1f, -1.0));
            Assert.Equal(0.3f, GhostPlaybackLogic.ComputeNozzleGlowRamp(0.3f, 1f, 1f, double.NaN));
            Assert.Equal(0.3f, GhostPlaybackLogic.ComputeNozzleGlowRamp(0.3f, 1f, 0f, 5.0));
            Assert.Equal(0.3f, GhostPlaybackLogic.ComputeNozzleGlowRamp(0.3f, 1f, float.NaN, 5.0));
        }

        // PP-9 `2026-11-23_1050`, toroidalAerospike (KSPCommunityFixes installed): at full thrust
        // the real bell stood at 0.62 after 12.4 s, at 30 % at exactly 0.30.
        [Theory]
        [InlineData(true, 1f, 0.62f)]
        [InlineData(true, 0.3f, 0.3f)]
        [InlineData(false, 1f, 0.62f)]
        [InlineData(false, 0.3f, 0f)]
        public void Throttle_ShowsTheRampWhileItMoves_ThenTheSettersLevelUnderKspcf(bool kspcf, float power, float expected)
        {
            NozzleGlowInfo g = Ramp(0.05f, kspcf);
            GhostPlaybackState state = StateWith(100.0, g);
            Assert.Equal(0f, g.writtenLevel);
            Apply(state, Evt(PartEventType.EngineIgnited, 120.0, power));
            GhostPlaybackLogic.UpdateNozzleGlows(state, 132.4);
            Assert.Equal((double)expected, g.writtenLevel, 3);
        }

        [Theory]
        [InlineData(true, 0.7f)]
        [InlineData(false, 1f)]
        public void Throttle_ARampThatHasArrivedShowsTheSettersLevelOnlyUnderKspcf(bool kspcf, float expected)
        {
            NozzleGlowInfo g = Ramp(1f, kspcf);
            GhostPlaybackState state = StateWith(100.0, g);
            Apply(state, Evt(PartEventType.EngineIgnited, 120.0, 0.7f));
            GhostPlaybackLogic.UpdateNozzleGlows(state, 120.5);
            Assert.Equal((double)0.5f, g.writtenLevel, 3);
            GhostPlaybackLogic.UpdateNozzleGlows(state, 130.0);
            Assert.Equal((double)expected, g.writtenLevel, 4);
            Assert.Equal((double)expected, GhostPlaybackLogic.NozzleGlowRestLevel(g), 4);
            // Settled: a later frame writes nothing.
            g.colors.Clear();
            float before = g.writtenLevel;
            GhostPlaybackLogic.UpdateNozzleGlows(state, 900.0);
            Assert.Equal(before, g.writtenLevel);
            Assert.Equal(1f, g.startRamp);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Throttle_AShutdownCoolsFromWhereTheRampStood_HoweverThePlaybackGetsThere(bool kspcf)
        {
            float At(double ut, params double[] frames)
            {
                NozzleGlowInfo g = Ramp(0.025f, kspcf);
                GhostPlaybackState state = StateWith(100.0, g);
                Apply(state, Evt(PartEventType.EngineIgnited, 100.0, 1f));
                foreach (double f in frames) if (f <= 120.0) GhostPlaybackLogic.UpdateNozzleGlows(state, f);
                Apply(state, Evt(PartEventType.EngineShutdown, 120.0));
                foreach (double f in frames) if (f > 120.0) GhostPlaybackLogic.UpdateNozzleGlows(state, f);
                GhostPlaybackLogic.UpdateNozzleGlows(state, ut);
                return g.writtenLevel;
            }
            Assert.Equal((double)0.25f, At(130.0), 3);
            Assert.Equal((double)0.25f, At(130.0, 101.0, 110.0, 119.9, 121.0, 129.0), 3);
            Assert.Equal(0f, At(141.0));
            Assert.Equal(0f, At(141.0, 110.0, 139.9, 140.5));
        }

        [Fact]
        public void Throttle_AColourChangerWithoutARateShowsTheSettersLevel()
        {
            NozzleGlowInfo g = Ramp(1f, false, useRate: false);
            Assert.False(g.ramps);
            GhostPlaybackState state = StateWith(100.0, g);
            Apply(state, Evt(PartEventType.EngineIgnited, 120.0, 0.3f));
            GhostPlaybackLogic.UpdateNozzleGlows(state, 130.0);
            Assert.Equal((double)0.3f, g.writtenLevel, 4);
        }

        // Stock does not persist where the ramp stood: Start puts it at animState's end.
        [Fact]
        public void Throttle_StartsAtTheSnapshotsSwitch_AndRestartsThereOnALoop()
        {
            NozzleGlowInfo g = Ramp(0.025f, true, initialState: 1f, animState: true);
            Assert.Equal(1f, g.initialRamp);
            g.initialUT = 100.0;
            GhostPlaybackState state = StateWith(100.0, g);
            Assert.Equal(1f, g.writtenLevel);
            Apply(state, Evt(PartEventType.EngineShutdown, 110.0));
            GhostPlaybackLogic.UpdateNozzleGlows(state, 130.0);
            float cooled = g.writtenLevel;
            Assert.True(cooled < 1f);
            GhostPlaybackLogic.RestartNozzleGlows(state);
            Assert.Equal(1f, g.startRamp);
            GhostPlaybackLogic.UpdateNozzleGlows(state, 100.0);
            Assert.Equal(1f, g.writtenLevel);
        }

        [Fact]
        public void Throttle_AnEndSnapshotOrALaterStateStartsTheRampCold()
        {
            NozzleGlowInfo g = Ramp(0.025f, true, initialState: 1f, animState: true);
            GhostVisualBuilder.StampPersistedGlowLevels(
                new HeatGhostInfo { nozzleGlows = new List<NozzleGlowInfo> { g } }, new ConfigNode("VESSEL"), trusted: false);
            Assert.Equal(0f, g.initialRamp);
            Assert.Equal(0f, g.initialLevel);

            NozzleGlowInfo later = Ramp(0.025f, true, initialState: 1f, animState: true);
            later.initialUT = 500.0;
            StateWith(100.0, later);
            Assert.Equal(0f, later.initialRamp);
            Assert.Equal(0f, later.writtenLevel);
        }

        // A row cut off an earlier one: the engine ran 20 s of its 40 s ramp in the row before.
        [Fact]
        public void Throttle_ACutRowTakesTheRampItsEarlierRowReached()
        {
            NozzleGlowInfo g = Ramp(0.025f, true);
            g.initialUT = 100.0;
            Recording head = Row(100.0, 140.0, Evt(PartEventType.EngineIgnited, 100.0, 1f));
            Recording tail = Row(120.0, 1000.0);
            var state = new GhostPlaybackState
            {
                heatInfos = new Dictionary<uint, HeatGhostInfo>
                {
                    [Pid] = new HeatGhostInfo { partPersistentId = Pid, nozzleGlows = new List<NozzleGlowInfo> { g } },
                },
            };
            GhostPlaybackLogic.BindNozzleGlows(state, tail, r => r == tail ? head : null);
            Assert.Equal((double)0.5f, g.initialRamp, 3);
        }

        [Fact]
        public void Driver_NamesAreSplitAndTrimmed_AndTheFirstOneThatNamesTheModuleWins()
        {
            Assert.Equal(new[] { "heatColor", "throttleColor" }, GhostVisualBuilder.SplitFxModuleNames("heatColor, throttleColor"));
            Assert.Empty(GhostVisualBuilder.SplitFxModuleNames(""));
            Assert.Empty(GhostVisualBuilder.SplitFxModuleNames(null));
            var first = new ThrottleGlowDriver { moduleIds = new[] { "a", "b" } };
            var second = new ThrottleGlowDriver { moduleIds = new[] { "b", "c" } };
            var drivers = new List<ThrottleGlowDriver> { first, second };
            Assert.Same(first, GhostVisualBuilder.FindThrottleGlowDriver(drivers, "b"));
            Assert.Same(second, GhostVisualBuilder.FindThrottleGlowDriver(drivers, "c"));
            Assert.Null(GhostVisualBuilder.FindThrottleGlowDriver(drivers, "colorChanger"));
            Assert.Null(GhostVisualBuilder.FindThrottleGlowDriver(null, "a"));
        }

        // Only the modules an FXModuleThrottleEffects names become a glow: a cabin light and a
        // heat shield char keep the writer they had, named or not.
        [Theory]
        [InlineData(false, "_EmissiveColor", true, false, (int)ColorChangerPattern.ThrottleGlow)]
        [InlineData(false, "_TintColor", false, false, (int)ColorChangerPattern.ThrottleGlow)]
        [InlineData(true, "_EmissiveColor", true, false, (int)ColorChangerPattern.CabinLight)]
        [InlineData(false, "_BurnColor", false, true, (int)ColorChangerPattern.AblationChar)]
        [InlineData(false, "", true, false, (int)ColorChangerPattern.None)]
        public void ColorChanger_ANamedModuleIsAGlowUnlessAnotherWriterOwnsIt(
            bool toggleInFlight, string property, bool useRate, bool ablatorDriven, int expected)
            => Assert.Equal((ColorChangerPattern)expected, GhostVisualBuilder.ClassifyColorChangerPattern(
                toggleInFlight, property, useRate, ablatorDriven, throttleDriven: true));

        [Fact]
        public void ColorChanger_ModuleIdIsTheConfigsOrStocksDefault()
        {
            var node = new ConfigNode("MODULE");
            Assert.Equal("colorChanger", GhostVisualBuilder.ColorChangerModuleId(node));
            Assert.Equal("colorChanger", GhostVisualBuilder.ColorChangerModuleId(null));
            node.AddValue("moduleID", "heatColor");
            Assert.Equal("heatColor", GhostVisualBuilder.ColorChangerModuleId(node));
        }

        #endregion

        #region Playback

        [Theory]
        [InlineData(PartEventType.EngineIgnited, true, 0.7f, false)]
        [InlineData(PartEventType.EngineThrottle, true, 0.7f, false)]
        [InlineData(PartEventType.EngineShutdown, true, 0f, false)]
        [InlineData(PartEventType.RCSActivated, true, 0.7f, true)]
        [InlineData(PartEventType.RCSThrottle, true, 0.7f, true)]
        [InlineData(PartEventType.RCSStopped, true, 0f, true)]
        [InlineData(PartEventType.LightOn, false, 0f, false)]
        [InlineData(PartEventType.ThermalAnimationHot, false, 0f, false)]
        public void GlowPower_IsReadFromEngineAndRcsEventsOnly(PartEventType type, bool expected, float power, bool rcs)
        {
            Assert.Equal(expected, GhostPlaybackLogic.TryReadGlowPower(Evt(type, 1.0, 0.7f), out float p, out bool r));
            Assert.Equal(power, p);
            Assert.Equal(rcs, r);
        }

        [Fact]
        public void EngineEvents_HeatTheBell_AndTheShutdownLetsItCool()
        {
            NozzleGlowInfo g = Glow(false, BellRate, 1f, 0);
            GhostPlaybackState state = StateWith(100.0, g);
            GhostPlaybackLogic.UpdateNozzleGlows(state, 110.0);
            Assert.Equal(0f, g.writtenLevel);

            Apply(state, Evt(PartEventType.EngineIgnited, 120.0, 1f));
            GhostPlaybackLogic.UpdateNozzleGlows(state, 160.0);
            Assert.Equal(1.0 - Math.Exp(-1.0), g.writtenLevel, 3);

            Apply(state, Evt(PartEventType.EngineThrottle, 160.0, 0.5f));
            Assert.Equal(0.5f, g.targetLevel);
            Assert.Equal(1.0 - Math.Exp(-1.0), g.startLevel, 3);

            Apply(state, Evt(PartEventType.EngineShutdown, 160.0));
            GhostPlaybackLogic.UpdateNozzleGlows(state, 200.0);
            Assert.Equal((1.0 - Math.Exp(-1.0)) * Math.Exp(-1.0), g.writtenLevel, 3);
            GhostPlaybackLogic.UpdateNozzleGlows(state, 2000.0);
            Assert.Equal(0f, g.writtenLevel);
            Assert.Contains(logLines, l => l.Contains("[GhostFx]") && l.Contains("Nozzle glow pid=4242"));
        }

        [Fact]
        public void EngineEvents_OfAnotherPartOrModule_OrAnRcsEvent_MoveNothing()
        {
            NozzleGlowInfo g = Glow(false, BellRate, 1f, 0);
            GhostPlaybackState state = StateWith(100.0, g);
            Apply(state, Evt(PartEventType.EngineIgnited, 120.0, 1f, module: 1));
            Apply(state, Evt(PartEventType.EngineIgnited, 120.0, 1f, pid: 7u));
            Apply(state, Evt(PartEventType.RCSActivated, 120.0, 1f));
            Assert.Equal(0f, g.targetLevel);
        }

        [Fact]
        public void RcsEvents_FollowTheForceThroughTheScale()
        {
            NozzleGlowInfo g = Glow(true, GhostPlaybackLogic.ComputeNozzleGlowRate(0.01f), 2f, 0);
            GhostPlaybackState state = StateWith(100.0, g);
            Apply(state, Evt(PartEventType.EngineIgnited, 110.0, 1f));
            Assert.Equal(0f, g.targetLevel);
            Apply(state, Evt(PartEventType.RCSActivated, 110.0, 0.222f));
            Assert.Equal(0.444, g.targetLevel, 3);
            Apply(state, Evt(PartEventType.RCSThrottle, 111.0, 0.1f));
            Assert.Equal(0.2, g.targetLevel, 3);
            Apply(state, Evt(PartEventType.RCSStopped, 112.0));
            Assert.Equal(0f, g.targetLevel);
        }

        [Fact]
        public void MultiModeSwitch_InEitherEventOrder_KeepsTheRunningModesTarget()
        {
            NozzleGlowInfo g = Glow(false, BellRate, 1f, 0, 1);
            GhostPlaybackState state = StateWith(0.0, g);
            Apply(state, Evt(PartEventType.EngineIgnited, 1.0, 1f, module: 0));
            Apply(state, Evt(PartEventType.EngineIgnited, 2.0, 0.8f, module: 1));
            Apply(state, Evt(PartEventType.EngineShutdown, 2.0, module: 0));
            Assert.Equal(0.8f, g.targetLevel);
        }

        [Fact]
        public void PersistedLevel_CoolsFromTheRecordingStart_WithNoEventAtAll()
        {
            NozzleGlowInfo g = Glow(false, BellRate, 1f, 0);
            g.initialLevel = 0.8f;
            GhostPlaybackState state = StateWith(100.0, g);
            Assert.Equal(0.8f, g.writtenLevel);
            GhostPlaybackLogic.UpdateNozzleGlows(state, 140.0);
            Assert.Equal(0.8 * Math.Exp(-1.0), g.writtenLevel, 3);
        }

        [Fact]
        public void UnknownStart_HoldsThePersistedLevelUntilTheFirstFrame()
        {
            NozzleGlowInfo g = Glow(false, BellRate, 1f, 0);
            g.initialLevel = 0.8f;
            GhostPlaybackState state = StateWith(double.NaN, g);
            Assert.Equal(0.8f, g.writtenLevel);
            GhostPlaybackLogic.UpdateNozzleGlows(state, 140.0);
            Assert.Equal(0.8f, g.writtenLevel);
            GhostPlaybackLogic.UpdateNozzleGlows(state, 180.0);
            Assert.Equal(0.8 * Math.Exp(-1.0), g.writtenLevel, 3);
        }

        [Fact]
        public void UnknownStart_ARestartOfTheWalkKeepsTheLatchedStart()
        {
            NozzleGlowInfo g = Glow(false, BellRate, 1f, 0);
            g.initialLevel = 0.9f;
            GhostPlaybackState state = StateWith(double.NaN, g);
            GhostPlaybackLogic.UpdateNozzleGlows(state, 100.0);
            GhostPlaybackLogic.UpdateNozzleGlows(state, 160.0);
            float cooled = g.writtenLevel;
            GhostPlaybackLogic.RestartNozzleGlows(state);
            GhostPlaybackLogic.UpdateNozzleGlows(state, 160.0);
            Assert.Equal(cooled, g.writtenLevel);
            Assert.Equal(0.9 * Math.Exp(-1.5), cooled, 3);
        }

        private static GhostPlaybackState Bind(Recording rec, Func<Recording, Recording> predecessorOf, NozzleGlowInfo g)
        {
            var state = new GhostPlaybackState
            {
                heatInfos = new Dictionary<uint, HeatGhostInfo>
                {
                    [Pid] = new HeatGhostInfo { partPersistentId = Pid, nozzleGlows = new List<NozzleGlowInfo> { g } },
                },
            };
            GhostPlaybackLogic.BindNozzleGlows(state, rec, predecessorOf);
            return state;
        }

        [Fact]
        public void RowCutAtABurnsEnd_StartsAtTheLevelTheBurnReached()
        {
            // Pad start 100 (cold), burn 120 to 220, cut at 220: both rows carry the pad snapshot.
            Recording head = Row(100.0, 220.0, Evt(PartEventType.EngineIgnited, 120.0, 1f));
            Recording tail = Row(220.0, 900.0, Evt(PartEventType.EngineShutdown, 220.0));
            NozzleGlowInfo g = Glow(false, BellRate, 1f, 0);
            g.initialUT = 100.0;
            GhostPlaybackState state = Bind(tail, r => r == tail ? head : null, g);
            double reached = 1.0 - Math.Exp(-2.5);
            Assert.Equal(reached, g.writtenLevel, 3);
            GhostPlaybackLogic.RestartNozzleGlows(state);
            Apply(state, tail.PartEvents[0]);
            GhostPlaybackLogic.UpdateNozzleGlows(state, 260.0);
            Assert.Equal(reached * Math.Exp(-1.0), g.writtenLevel, 3);
            Assert.Contains(logLines, l => l.Contains("Nozzle glow history") && l.Contains("rows=1 events=1"));
        }

        [Fact]
        public void RowCutLongAfterAWarmStart_IsCold_WithOrWithoutTheEarlierRow()
        {
            foreach (bool known in new[] { true, false })
            {
                Recording head = Row(1000.0, 4600.0);
                Recording tail = Row(4600.0, 9000.0);
                NozzleGlowInfo g = Glow(false, BellRate, 1f, 0);
                g.initialLevel = 0.85f;
                g.initialUT = 1000.0;
                Bind(tail, r => known && r == tail ? head : null, g);
                Assert.Equal(0f, g.writtenLevel);
            }
        }

        [Fact]
        public void ThreeRows_ReplayInOrder_AndAFreshSnapshotIsNotReplayed()
        {
            Recording a = Row(100.0, 200.0, Evt(PartEventType.EngineIgnited, 100.0, 1f));
            Recording b = Row(200.0, 300.0, Evt(PartEventType.EngineIgnited, 200.0, 1f), Evt(PartEventType.EngineShutdown, 240.0));
            Recording c = Row(300.0, 400.0);
            Func<Recording, Recording> pred = r => r == c ? b : (r == b ? a : null);
            Assert.Equal(new[] { a, b }, GhostPlaybackLogic.CollectNozzleGlowHistory(c, 100.0, pred));
            Assert.Equal(new[] { b }, GhostPlaybackLogic.CollectNozzleGlowHistory(c, 200.0, pred));
            Assert.Empty(GhostPlaybackLogic.CollectNozzleGlowHistory(c, 299.0, pred));
            Assert.Empty(GhostPlaybackLogic.CollectNozzleGlowHistory(null, 100.0, pred));
            // A predecessor query that loops never hangs.
            Assert.Equal(2, GhostPlaybackLogic.CollectNozzleGlowHistory(c, 0.0, r => r == b ? a : b).Count);

            NozzleGlowInfo g = Glow(false, BellRate, 1f, 0);
            g.initialUT = 100.0;
            Bind(c, pred, g);
            Assert.Equal((1.0 - Math.Exp(-3.5)) * Math.Exp(-1.5), g.writtenLevel, 3);

            NozzleGlowInfo fresh = Glow(false, BellRate, 1f, 0);
            fresh.initialLevel = 0.3f;
            fresh.initialUT = 299.9;
            Bind(c, pred, fresh);
            Assert.Equal(0.3, fresh.writtenLevel, 2);
        }

        [Fact]
        public void ASnapshotLaterThanTheRecordingStart_IsDropped_AndOneInsideTheSlackIsKept()
        {
            NozzleGlowInfo late = Glow(false, BellRate, 1f, 0);
            late.initialLevel = 0.9f;
            late.initialUT = 5000.0;
            Bind(Row(100.0, 5000.0), _ => null, late);
            Assert.Equal(0f, late.writtenLevel);
            Assert.True(double.IsNaN(late.initialUT));

            NozzleGlowInfo near = Glow(false, BellRate, 1f, 0);
            near.initialLevel = 0.9f;
            near.initialUT = 102.0;
            Bind(Row(100.0, 5000.0), _ => null, near);
            Assert.Equal(0.9f, near.writtenLevel);
        }

        [Fact]
        public void AnEventBeforeThePersistedLevel_ChangesThePowerOnly()
        {
            NozzleGlowInfo g = Glow(false, BellRate, 1f, 0);
            g.initialLevel = 0.5f;
            g.initialUT = 102.0;
            GhostPlaybackState state = Bind(Row(100.0, 900.0), _ => null, g);
            Apply(state, Evt(PartEventType.EngineIgnited, 100.0, 1f));
            GhostPlaybackLogic.UpdateNozzleGlows(state, 102.0);
            Assert.Equal(0.5f, g.writtenLevel);
            GhostPlaybackLogic.UpdateNozzleGlows(state, 142.0);
            Assert.Equal(1.0 - 0.5 * Math.Exp(-1.0), g.writtenLevel, 3);
        }

        [Fact]
        public void ChainPredecessor_IsTheRowBeforeIt()
        {
            RecordingStore.ResetForTesting();
            try
            {
                Recording head = Row(100.0, 200.0);
                Recording tail = Row(200.0, 300.0);
                head.ChainId = tail.ChainId = "chain-glow";
                head.ChainIndex = 0;
                tail.ChainIndex = 1;
                RecordingStore.AddCommittedInternal(head);
                RecordingStore.AddCommittedInternal(tail);
                Assert.Same(head, RecordingChainQueries.GetChainPredecessor(tail));
                Assert.Null(RecordingChainQueries.GetChainPredecessor(head));
                Assert.Null(RecordingChainQueries.GetChainPredecessor(null));
            }
            finally
            {
                RecordingStore.ResetForTesting();
            }
        }

        [Fact]
        public void Restart_ReplaysToTheSameLevel_AfterALoopOrAScrub()
        {
            NozzleGlowInfo g = Glow(false, BellRate, 1f, 0);
            g.initialLevel = 0.4f;
            GhostPlaybackState state = StateWith(100.0, g);
            Apply(state, Evt(PartEventType.EngineIgnited, 120.0, 1f));
            GhostPlaybackLogic.UpdateNozzleGlows(state, 150.0);
            float first = g.writtenLevel;
            GhostPlaybackLogic.UpdateNozzleGlows(state, 400.0);

            GhostPlaybackLogic.RestartNozzleGlows(state);
            Assert.True(float.IsNaN(g.writtenLevel));
            GhostPlaybackLogic.UpdateNozzleGlows(state, 100.0);
            Assert.Equal(0.4f, g.writtenLevel);
            Apply(state, Evt(PartEventType.EngineIgnited, 120.0, 1f));
            GhostPlaybackLogic.UpdateNozzleGlows(state, 150.0);
            Assert.Equal(first, g.writtenLevel);
        }

        [Fact]
        public void OrphanAutoStart_HeatsFromTheRecordingStart_AndSurvivesARestart()
        {
            NozzleGlowInfo engine = Glow(false, BellRate, 1f, 0);
            NozzleGlowInfo rcs = Glow(true, BellRate, 1f, 0);
            GhostPlaybackState state = StateWith(100.0, engine, rcs);
            GhostPlaybackLogic.SetNozzleGlowBasePower(state, FlightRecorder.EncodeEngineKey(Pid, 0), 1f);
            GhostPlaybackLogic.RestartNozzleGlows(state);
            GhostPlaybackLogic.UpdateNozzleGlows(state, 140.0);
            Assert.Equal(1.0 - Math.Exp(-1.0), engine.writtenLevel, 3);
            Assert.Equal(0f, rcs.targetLevel);
        }

        [Fact]
        public void Update_WritesNothingOnceSettled_AndAGhostWithoutGlowsIsANoOp()
        {
            NozzleGlowInfo g = Glow(false, float.PositiveInfinity, 1f, 0);
            GhostPlaybackState state = StateWith(0.0, g);
            Apply(state, Evt(PartEventType.EngineIgnited, 1.0, 1f));
            GhostPlaybackLogic.UpdateNozzleGlows(state, 2.0);
            Assert.Equal(1f, g.writtenLevel);
            Assert.Equal(1f, g.startLevel);
            GhostPlaybackLogic.UpdateNozzleGlows(state, 3.0);
            Assert.Equal(1f, g.writtenLevel);

            GhostPlaybackLogic.UpdateNozzleGlows(null, 1.0);
            GhostPlaybackLogic.UpdateNozzleGlows(new GhostPlaybackState(), 1.0);
            GhostPlaybackLogic.ApplyNozzleGlowPower(new GhostPlaybackState(), Evt(PartEventType.EngineIgnited, 1.0), 1f, false);
            GhostPlaybackLogic.RestartNozzleGlows(new GhostPlaybackState());
            var empty = new GhostPlaybackState();
            GhostPlaybackLogic.BindNozzleGlows(empty, Row(5.0, 10.0));
            Assert.Null(empty.nozzleGlows);
        }

        [Fact]
        public void ClearLoadedVisualReferences_DropsTheGlows()
        {
            GhostPlaybackState state = StateWith(0.0, Glow(false, BellRate, 1f, 0));
            Assert.NotNull(state.nozzleGlows);
            state.ClearLoadedVisualReferences();
            Assert.Null(state.nozzleGlows);
            Assert.True(double.IsNaN(state.nozzleGlowStartUT));
        }

        #endregion

        #region Heat clip missing from the model

        [Theory]
        [InlineData("ModuleAnimateHeat", false, true)]
        [InlineData("ModuleAnimateHeat", true, false)]
        [InlineData("FXModuleAnimateThrottle", false, false)]
        [InlineData("FXModuleAnimateRCS", false, false)]
        public void HeatClipMissing_OnlyForAModuleAnimateHeatWithoutItsClip(string source, bool modelHoldsClip, bool expected)
            => Assert.Equal(expected, GhostVisualBuilder.IsHeatClipMissing(source, modelHoldsClip));

        [Fact]
        public void HeatEvents_OnAPartWhoseClipIsMissing_DrawNothing()
        {
            var state = new GhostPlaybackState
            {
                heatInfos = new Dictionary<uint, HeatGhostInfo>
                {
                    [Pid] = new HeatGhostInfo { partPersistentId = Pid, heatClipMissing = true },
                },
            };
            foreach (HeatLevel level in new[] { HeatLevel.Cold, HeatLevel.Medium, HeatLevel.Hot })
                Assert.Equal(GhostPartEventOutcome.NoResolvedVisual, GhostPlaybackLogic.ApplyHeatStateWithOutcome(
                    state, Evt(PartEventType.ThermalAnimationHot, 1.0), level));
        }

        [Fact]
        public void ReentryGlow_TakesAPartWhoseHeatClipIsMissing_AndLeavesEveryOtherHeatPart()
        {
            Assert.False(GhostVisualBuilder.HeatInfoOwnsPartMaterials(null));
            Assert.True(GhostVisualBuilder.HeatInfoOwnsPartMaterials(new HeatGhostInfo()));
            Assert.False(GhostVisualBuilder.HeatInfoOwnsPartMaterials(new HeatGhostInfo { heatClipMissing = true }));
            Assert.True(GhostVisualBuilder.HeatInfoOwnsPartMaterials(new HeatGhostInfo
            {
                heatClipMissing = true,
                nozzleGlows = new List<NozzleGlowInfo> { Glow(false, BellRate, 1f, 0) },
            }));
        }

        #endregion

        #region Lamp lights

        [Theory]
        [InlineData("spotlight", "spotlight", true)]
        [InlineData("Spotlight", "spotlight", false)]
        [InlineData("Point light", "spotlight", false)]
        [InlineData("anything", "", true)]
        [InlineData("anything", null, false)]
        public void LampLight_IsTheModules_ByExactObjectName(string objectName, string lightName, bool expected)
            => Assert.Equal(expected, GhostVisualBuilder.IsModuleLightDriven(objectName, lightName));

        [Fact]
        public void LampLight_SettlesAtStocksIntensity()
            => Assert.Equal(1f, GhostVisualBuilder.ModuleLightSettledIntensity);

        [Fact]
        public void LampLight_BlackPersistedColour_IsStocksDefaultGrey()
        {
            var module = new ConfigNode("MODULE");
            module.AddValue("lightR", "0");
            module.AddValue("lightG", "0");
            module.AddValue("lightB", "0");
            Color c = GhostVisualBuilder.ResolveLightOnColor(module, 1f, 1f, 1f);
            Assert.Equal(0.875, c.r, 4);
            Assert.Equal(0.875, c.g, 4);
            Assert.Equal(0.875, c.b, 4);
        }

        #endregion
    }
}
