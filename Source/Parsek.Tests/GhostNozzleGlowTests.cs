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
            Assert.Same(g, GhostPlaybackLogic.RestingNozzleGlow(g, out float rest));
            Assert.Equal((double)expected, rest, 4);
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

        // A 40 s ramp whose engine the split's parent recorded; startsAt is the recording's start
        // and the UT its snapshot was taken at.
        private NozzleGlowInfo AtSplit(
            Recording child, bool running, Func<Recording, Recording> predecessorOf,
            Func<Recording, Recording> splitParentOf, double snapshotUT = double.NaN)
        {
            NozzleGlowInfo g = Ramp(0.025f, true, initialState: running ? 1f : 0f, animState: running);
            g.initialUT = double.IsNaN(snapshotUT) ? child.StartUT : snapshotUT;
            GhostPlaybackLogic.BindNozzleGlows(StateOf(g), child, predecessorOf, splitParentOf);
            return g;
        }

        private static GhostPlaybackState StateOf(params NozzleGlowInfo[] glows)
            => new GhostPlaybackState
            {
                heatInfos = new Dictionary<uint, HeatGhostInfo>
                {
                    [Pid] = new HeatGhostInfo { partPersistentId = Pid, nozzleGlows = new List<NozzleGlowInfo>(glows) },
                },
            };

        // The ordinary staging: a booster that burned 20 s is dropped with its engine off. The
        // snapshot's switch is off (cold), the real bell is half hot and cools.
        [Fact]
        public void Split_ADroppedStageStartsAsWarmAsItsBurnLeftIt()
        {
            Recording parent = Row(100.0, 500.0,
                Evt(PartEventType.EngineShutdown, 100.0), Evt(PartEventType.EngineIgnited, 100.0, 1f),
                Evt(PartEventType.EngineShutdown, 120.0), Evt(PartEventType.EngineIgnited, 300.0, 1f));
            Recording child = Row(124.0, 200.0, Evt(PartEventType.EngineShutdown, 124.0));
            NozzleGlowInfo g = AtSplit(child, false, r => null, r => r == child ? parent : null);
            Assert.Equal((double)0.4f, g.initialRamp, 3);
            Assert.Equal((double)0.4f, g.writtenLevel, 3);
            GhostPlaybackState state = StateOf(g);
            GhostPlaybackLogic.BindNozzleGlows(state, child, r => null, r => r == child ? parent : null);
            Apply(state, child.PartEvents[0]);
            GhostPlaybackLogic.UpdateNozzleGlows(state, 130.0);
            Assert.Equal((double)0.25f, g.writtenLevel, 3);
            Assert.Contains(logLines, l => l.Contains("[GhostFx]") && l.Contains("Nozzle glow at a split")
                && l.Contains("rows=1 ramps=1/1 hottest=0.400 headUT=100.0 splitUT=124.0"));
        }

        // A recorded Kerbal X ascent: three of its boosters, through the tree's own branch points.
        // Ignited at 34.258, shut down at 47.458 / 63.096 / 82.116 (the 40 s ramp is full by then).
        [Theory]
        [InlineData("6dae41d1b2584f0cbc49afdd587cbdfe", 2502517217u, 0.287f)]
        [InlineData("b06a8b5f81274995879f42b67d24eae8", 3684832787u, 0.6925f)]
        [InlineData("70e9c28bd20947d0afeaa1deb9215e34", 3105750609u, 0.9655f)]
        public void Split_ARecordedBoosterTakesItsRampFromTheAscent(string childId, uint enginePid, float expected)
        {
            RecordingTree tree = StructureListBdockFixtureTests.LoadFixtureTrees("duna-one-recorded", -1)
                .Find(t => t.Recordings.ContainsKey(childId));
            Recording child = tree.Recordings[childId];
            NozzleGlowInfo g = Ramp(0.025f, true);
            g.moduleKeys = new[] { FlightRecorder.EncodeEngineKey(enginePid, 0) };
            g.initialUT = child.StartUT;
            GhostPlaybackLogic.BindNozzleGlows(StateOf(g), child, r => null,
                r => GhostPlaybackLogic.ResolveNozzleGlowSplitParent(r, tree));
            Assert.InRange(g.initialRamp, expected - 0.001f, expected + 0.001f);
        }

        // The reported case: a split in the middle of a burn. The snapshot's switch is on (hot end).
        [Fact]
        public void Split_ARunningEngineStartsWhereItsRampStood()
        {
            Recording parent = Row(100.0, 500.0,
                Evt(PartEventType.EngineShutdown, 100.0), Evt(PartEventType.EngineIgnited, 110.0, 1f));
            Recording child = Row(120.0, 200.0, Evt(PartEventType.EngineIgnited, 120.0, 1f));
            NozzleGlowInfo g = AtSplit(child, true, r => null, r => r == child ? parent : null);
            Assert.Equal((double)0.25f, g.initialRamp, 3);
            // The setter's level is persisted as it stood; only the ramp is not.
            Assert.Equal(1f, g.initialLevel);
            // The level counts from the snapshot, taken a little after the split.
            g = AtSplit(child, true, r => null, r => r == child ? parent : null, snapshotUT: 121.0);
            Assert.Equal((double)0.275f, g.initialRamp, 3);
        }

        // The parent is a row the optimizer cut, and it was itself dropped: the line goes back
        // through the cut and the earlier split to the launch.
        [Fact]
        public void Split_TheLineGoesBackThroughCutsAndEarlierSplits()
        {
            Recording launch = Row(100.0, 110.0,
                Evt(PartEventType.EngineShutdown, 100.0), Evt(PartEventType.EngineIgnited, 102.0, 1f));
            Recording head = Row(110.0, 114.0);
            Recording cut = Row(114.0, 300.0);
            Recording child = Row(118.0, 200.0);
            Func<Recording, Recording> before = r => r == cut ? head : null;
            Func<Recording, Recording> splitOf = r => r == child ? cut : r == head ? launch : null;
            Assert.Equal(new[] { launch, head, cut }, GhostPlaybackLogic.CollectNozzleGlowSplitLine(child, before, splitOf));
            Assert.Equal((double)0.4f, AtSplit(child, true, before, splitOf).initialRamp, 3);

            // A cut of the child: its snapshot is the child's, so the split settles the child first.
            Recording tail = Row(138.0, 200.0);
            NozzleGlowInfo g = Ramp(0.025f, true, initialState: 1f, animState: true);
            g.initialUT = 118.0;
            child.PartEvents.Add(Evt(PartEventType.EngineIgnited, 118.0, 1f));
            GhostPlaybackLogic.BindNozzleGlows(StateOf(g), tail, r => r == tail ? child : before(r), splitOf);
            Assert.Equal((double)0.9f, g.initialRamp, 3);
        }

        // What the events do not tell keeps the snapshot's end, as stock's own load does.
        [Fact]
        public void Split_WithoutTheEventsTheSnapshotsSwitchStands()
        {
            Recording child = Row(120.0, 200.0);
            Recording empty = Row(100.0, 500.0);
            Recording other = Row(100.0, 500.0, Evt(PartEventType.EngineIgnited, 110.0, 1f, pid: Pid + 1));
            Recording later = Row(100.0, 500.0, Evt(PartEventType.EngineIgnited, 130.0, 1f));
            foreach (Recording parent in new[] { null, empty, other, later })
            {
                Assert.Equal(1f, AtSplit(child, true, r => null, r => r == child ? parent : null).initialRamp);
                Assert.Equal(0f, AtSplit(child, false, r => null, r => r == child ? parent : null).initialRamp);
            }
            // No split at all, and the same without a lookup.
            Assert.Equal(1f, AtSplit(child, true, r => null, r => null).initialRamp);
            Assert.Equal(1f, AtSplit(child, true, r => null, null).initialRamp);
        }

        // The line's first row begins with the engine running: its snapshot starts at the hot end.
        // An engine lit later than the seeds was cold.
        [Theory]
        [InlineData(99.98, 0.75f)]
        [InlineData(100.18, 0.75f)]
        [InlineData(102.0, 0f)]
        public void Split_AnEngineSeededRunningStartsHotInTheLinesFirstRow(double litUT, float expected)
        {
            Recording child = Row(120.0, 200.0);
            Recording head = Row(100.0, 500.0,
                Evt(PartEventType.EngineIgnited, litUT, 1f), Evt(PartEventType.EngineShutdown, 110.0));
            Assert.Equal((double)expected, AtSplit(child, false, r => null, r => r == child ? head : null).initialRamp, 3);
        }

        // A clip's level is persisted whole, so a split changes nothing for it.
        [Fact]
        public void Split_AClipKeepsItsPersistedLevel()
        {
            NozzleGlowInfo clip = Glow(false, BellRate, 1f, 0);
            clip.initialLevel = 0.3f;
            clip.initialUT = 124.0;
            NozzleGlowInfo ramp = Ramp(0.025f, true);
            ramp.initialUT = 124.0;
            Recording parent = Row(100.0, 500.0,
                Evt(PartEventType.EngineIgnited, 100.0, 1f), Evt(PartEventType.EngineShutdown, 120.0));
            Recording child = Row(124.0, 200.0);
            GhostPlaybackLogic.BindNozzleGlows(StateOf(clip, ramp), child, r => null, r => r == child ? parent : null);
            Assert.Equal(0.3f, clip.initialLevel);
            Assert.Equal(0.3f, clip.startLevel);
            Assert.Equal(124.0, clip.startUT);
            Assert.Equal((double)0.9f, ramp.initialRamp, 3);
        }

        [Theory]
        [InlineData(BranchPointType.Undock, null, 1, true)]
        [InlineData(BranchPointType.EVA, null, 1, true)]
        [InlineData(BranchPointType.JointBreak, null, 1, true)]
        [InlineData(BranchPointType.Breakup, "", 1, true)]
        // Two parents, or a start no split made.
        [InlineData(BranchPointType.JointBreak, null, 2, false)]
        [InlineData(BranchPointType.Dock, null, 1, false)]
        [InlineData(BranchPointType.Board, null, 1, false)]
        [InlineData(BranchPointType.Launch, null, 1, false)]
        [InlineData(BranchPointType.VesselSwitchContinuation, null, 1, false)]
        // A Re-Fly attempt's vessel was loaded from the rewind point.
        [InlineData(BranchPointType.JointBreak, "sess", 1, false)]
        public void Split_OnlyAVesselThatCameOutOfOneIsNotReloaded(
            BranchPointType type, string session, int parents, bool expected)
        {
            var parent = new Recording { RecordingId = "p0" };
            var child = new Recording { RecordingId = "c", ParentBranchPointId = "bp", CreatingSessionId = session };
            var bp = new BranchPoint { Id = "bp", Type = type };
            var tree = new RecordingTree { Id = "t" };
            for (int i = 0; i < parents; i++) bp.ParentRecordingIds.Add("p" + i);
            tree.BranchPoints.Add(bp);
            tree.Recordings["p0"] = parent;
            tree.Recordings["c"] = child;
            Assert.Same(expected ? parent : null, GhostPlaybackLogic.ResolveNozzleGlowSplitParent(child, tree));
            Assert.Null(GhostPlaybackLogic.ResolveNozzleGlowSplitParent(child, null));
            Assert.Null(GhostPlaybackLogic.ResolveNozzleGlowSplitParent(null, tree));
            Assert.Null(GhostPlaybackLogic.ResolveNozzleGlowSplitParent(parent, tree));
        }

        [Fact]
        public void Wiring_AGhostSpawnLooksForTheSplit()
            => Assert.Contains(
                "BindNozzleGlows(state, traj, RecordingChainQueries.GetChainPredecessor, GetNozzleGlowSplitParent);",
                Source("GhostPlaybackLogic.NozzleGlow.cs"));

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

        // The curves the glow plays: ReStock's heatColor ramps all four channels, so its emission
        // goes with the square of the level.
        [Fact]
        public void ColorChanger_TheGlowPlaysTheModulesOwnCurves()
        {
            var node = new ConfigNode("MODULE");
            foreach (string curve in new[] { "redCurve", "greenCurve", "blueCurve", "alphaCurve" })
            {
                ConfigNode c = node.AddNode(curve);
                c.AddValue("key", curve == "blueCurve" ? "0 1" : "0 0 1 1");
                c.AddValue("key", "1 1 1 1");
            }
            Color[] samples = GhostVisualBuilder.SampleColorChangerCurves(node);
            Assert.Equal(GhostVisualBuilder.NozzleGlowSampleCount, samples.Length);
            for (int n = 0; n < samples.Length; n++)
                Assert.Equal(GhostVisualBuilder.EvaluateColorChangerColor(node, n / (float)(samples.Length - 1)), samples[n]);
            Assert.Equal((double)0.3f, GhostPlaybackLogic.ResolveNozzleGlowColor(samples, 0.3f).r, 3);
            Assert.Equal((double)0.3f, GhostPlaybackLogic.ResolveNozzleGlowColor(samples, 0.3f).a, 3);
        }

        // The prefab's modules and materials are Unity objects, so the path from the part to a
        // written glow is pinned by text.
        [Fact]
        public void Wiring_TheBuilderTurnsNamedColourChangersIntoGlows()
        {
            string build = Source("GhostVisualBuilder.cs");
            int collect = build.IndexOf("throttleGlowDrivers = CollectThrottleGlowDrivers(prefab, partNode);", StringComparison.Ordinal);
            int named = build.IndexOf("ThrottleDrivenModuleIds(throttleGlowDrivers));", StringComparison.Ordinal);
            int glows = build.IndexOf("colorChangerInfos, throttleGlowDrivers, persistentId, partName);", StringComparison.Ordinal);
            Assert.True(collect > 0 && named > collect && glows > named);
            string nozzle = Source("GhostVisualBuilder.NozzleGlow.cs");
            Assert.Contains("int throttleGlows = BuildThrottleGlows(", nozzle);
            Assert.Contains("ColorChangerWritesOnlyWhileRamping());", nozzle);
            Assert.Contains("patches.Owners.Contains(KspCommunityFixesHarmonyId)", nozzle);
            Assert.Contains("typeof(ModuleColorChanger), \"Update\"", nozzle);
            Assert.Contains("samples = info.throttleGlow.samples,", nozzle);
            string changers = Source("GhostVisualBuilder.ColorChangers.cs");
            Assert.Contains("bool throttleDriven = driven != null && driven.Remove(moduleId);", changers);
            Assert.Contains("samples = SampleColorChangerCurves(node),", changers);
            Assert.Contains("throttleGlow = plan.throttleGlow,", changers);
            Assert.Contains("throttleGlowUnder = under,", changers);
            Assert.Contains("MaterialsOf(plan, under).Add(", changers);
            Assert.Contains("if (under.Count > 0) glow.under = under.ToArray();", nozzle);
            // KSPCommunityFixes.dll: new Harmony("KSPCommunityFixes").
            Assert.Equal("KSPCommunityFixes", GhostVisualBuilder.KspCommunityFixesHarmonyId);
        }

        // ReStock's boosters (Mite): the plural includedRenderers / excludedRenderers are no key
        // OnLoad reads, so heatColor (0.025 /s) and throttleColor (1 /s) write the whole bell.
        private static NozzleGlowInfo[] BoosterGlows(bool kspcf)
        {
            Color[] Solid(float v)
            {
                var samples = new Color[GhostVisualBuilder.NozzleGlowSampleCount];
                for (int i = 0; i < samples.Length; i++) samples[i] = new Color(v, v, v, v);
                return samples;
            }
            NozzleGlowInfo heat = GhostVisualBuilder.NewThrottleGlow(Pid,
                new ThrottleGlowDriver { moduleIds = new[] { "heatColor" }, moduleIndices = new[] { 0 }, responseSpeed = 1f, ordinal = 0 },
                new ThrottleGlowCurve { moduleId = "heatColor", animRate = 0.025f, useRate = true, samples = Solid(0.25f) }, 0f, kspcf);
            NozzleGlowInfo throttle = GhostVisualBuilder.NewThrottleGlow(Pid,
                new ThrottleGlowDriver { moduleIds = new[] { "throttleColor" }, moduleIndices = new[] { 0 }, responseSpeed = 1f, ordinal = 1 },
                new ThrottleGlowCurve { moduleId = "throttleColor", animRate = 1f, useRate = true, samples = Solid(0.75f) }, 0f, kspcf);
            throttle.under = new[] { heat };
            return new[] { heat, throttle };
        }

        // PPS-12b `2026-11-23_1656`, Mite at full: the real bell read 0.157 of full 6.3 s into the
        // burn, PP-9's 0.316 at 12.6 s; the ghost showed throttleColor's 1.
        [Theory]
        [InlineData(true, 0.5, 0.5f, false)]
        [InlineData(true, 6.3, 0.1575f, true)]
        [InlineData(true, 12.6, 0.315f, true)]
        [InlineData(true, 60.0, 1f, false)]
        [InlineData(false, 0.5, 0.5f, false)]
        [InlineData(false, 6.3, 1f, false)]
        [InlineData(false, 60.0, 1f, false)]
        public void Overlap_TheLastModuleThatWritesShows_AndUnderKspcfOnlyAMovingRampWrites(
            bool kspcf, double after, float expected, bool heatShows)
        {
            NozzleGlowInfo[] g = BoosterGlows(kspcf);
            GhostPlaybackState state = StateWith(100.0, g[0], g[1]);
            Apply(state, Evt(PartEventType.EngineIgnited, 120.0, 1f));
            GhostPlaybackLogic.UpdateNozzleGlows(state, 120.0 + after);
            Assert.Equal((double)expected, g[1].writtenLevel, 3);
            Assert.Same(heatShows ? g[0] : g[1], g[1].writtenBy);
        }

        // The spool: 12 throttle events from 0.05 up. Below 0.5 nothing ramps (the setter shows);
        // from the 0.5 crossing the ramps run whatever the number of events.
        [Fact]
        public void Overlap_TheSpoolEventsNeverRestartTheRamp()
        {
            NozzleGlowInfo[] g = BoosterGlows(true);
            GhostPlaybackState state = StateWith(100.0, g[0], g[1]);
            for (int i = 0; i < 12; i++)
            {
                double ut = 120.0 + i * 0.04;
                Apply(state, Evt(i == 0 ? PartEventType.EngineIgnited : PartEventType.EngineThrottle, ut, (i + 1) / 12f));
                GhostPlaybackLogic.UpdateNozzleGlows(state, ut);
            }
            // 0.5 is reached by the sixth event, at 120.20.
            GhostPlaybackLogic.UpdateNozzleGlows(state, 125.2);
            Assert.Equal((double)0.125f, g[1].writtenLevel, 3);
            Assert.Same(g[0], g[1].writtenBy);
        }

        [Fact]
        public void Overlap_APartThrottleShowsTheLastSettersLevel_AndTheShutdownTheCoolingHeatRamp()
        {
            NozzleGlowInfo[] g = BoosterGlows(true);
            GhostPlaybackState state = StateWith(100.0, g[0], g[1]);
            Apply(state, Evt(PartEventType.EngineIgnited, 120.0, 0.3f));
            GhostPlaybackLogic.UpdateNozzleGlows(state, 130.0);
            Assert.Equal((double)0.3f, g[1].writtenLevel, 4);
            Assert.Same(g[1], g[1].writtenBy);

            Apply(state, Evt(PartEventType.EngineThrottle, 130.0, 1f));
            GhostPlaybackLogic.UpdateNozzleGlows(state, 150.0);
            Assert.Equal((double)0.5f, g[1].writtenLevel, 3);
            Apply(state, Evt(PartEventType.EngineShutdown, 150.0));
            GhostPlaybackLogic.UpdateNozzleGlows(state, 150.5);
            Assert.Equal((double)0.5f, g[1].writtenLevel, 3);
            Assert.Same(g[1], g[1].writtenBy);
            GhostPlaybackLogic.UpdateNozzleGlows(state, 160.0);
            Assert.Equal((double)0.25f, g[1].writtenLevel, 3);
            Assert.Same(g[0], g[1].writtenBy);
            GhostPlaybackLogic.UpdateNozzleGlows(state, 171.0);
            Assert.Equal(0f, g[1].writtenLevel);
        }

        [Fact]
        public void Overlap_OnceBothRampsStandNothingIsWrittenAgain_AndARestartWritesAgain()
        {
            NozzleGlowInfo[] g = BoosterGlows(true);
            GhostPlaybackState state = StateWith(100.0, g[0], g[1]);
            Apply(state, Evt(PartEventType.EngineIgnited, 120.0, 1f));
            GhostPlaybackLogic.UpdateNozzleGlows(state, 200.0);
            GhostPlaybackLogic.UpdateNozzleGlows(state, 201.0);
            Assert.Equal(1f, g[1].writtenLevel);
            Assert.Equal(1f, g[0].startRamp);
            Assert.Equal(1f, g[1].startRamp);
            NozzleGlowInfo by = g[1].writtenBy;
            GhostPlaybackLogic.UpdateNozzleGlows(state, 900.0);
            Assert.Same(by, g[1].writtenBy);
            Assert.Equal(1f, g[1].writtenLevel);
            GhostPlaybackLogic.RestartNozzleGlows(state);
            GhostPlaybackLogic.UpdateNozzleGlows(state, 100.0);
            Assert.Equal(0f, g[1].writtenLevel);
        }

        // With the setters in the other order the heat module's setter comes last in the frame.
        [Fact]
        public void Overlap_TheRestingLevelIsTheLastSettersOne()
        {
            NozzleGlowInfo[] g = BoosterGlows(true);
            g[0].setterOrder = 5000;
            g[0].targetLevel = 0.7f;
            g[1].targetLevel = 0.6f;
            Assert.Same(g[0], GhostPlaybackLogic.RestingNozzleGlow(g[1], out float rest));
            Assert.Equal(0.7f, rest);
            NozzleGlowInfo[] stock = BoosterGlows(false);
            stock[1].targetLevel = 0.6f;
            Assert.Same(stock[1], GhostPlaybackLogic.RestingNozzleGlow(stock[1], out rest));
            Assert.Equal(1f, rest);
        }

        [Fact]
        public void Overlap_TheSetterOrderIsTheDriversModuleOrderThenItsNames()
        {
            var first = new ThrottleGlowDriver { moduleIds = new[] { "a", "b" }, ordinal = 0 };
            var second = new ThrottleGlowDriver { moduleIds = new[] { "c" }, ordinal = 1 };
            int a = GhostVisualBuilder.ThrottleGlowSetterOrder(first, "a");
            int b = GhostVisualBuilder.ThrottleGlowSetterOrder(first, "b");
            int c = GhostVisualBuilder.ThrottleGlowSetterOrder(second, "c");
            Assert.True(a < b && b < c);
        }

        [Fact]
        public void Overlap_OnlyThrottleGlowsOfTheSamePropertyThatWriteTheRendererCount()
        {
            var properties = new List<string> { "_EmissiveColor", "_EmissiveColor", "_TintColor", "_EmissiveColor" };
            var keeps = new List<bool[]>
            {
                new[] { true, true, false }, new[] { true, false, true }, new[] { true, true, true }, new[] { true, true, true },
            };
            var glow = new List<bool> { true, true, true, true };
            Assert.Equal(new[] { 0, 1 }, GhostVisualBuilder.ResolveThrottleGlowWritersUnder(properties, keeps, glow, 3, 0));
            Assert.Equal(new[] { 0 }, GhostVisualBuilder.ResolveThrottleGlowWritersUnder(properties, keeps, glow, 3, 1));
            Assert.Equal(new[] { 1 }, GhostVisualBuilder.ResolveThrottleGlowWritersUnder(properties, keeps, glow, 3, 2));
            Assert.Empty(GhostVisualBuilder.ResolveThrottleGlowWritersUnder(properties, keeps, glow, 2, 0));
            Assert.Empty(GhostVisualBuilder.ResolveThrottleGlowWritersUnder(properties, keeps, glow, 0, 0));
            // A renderer that only carries the block's value, not written by the module itself.
            Assert.Empty(GhostVisualBuilder.ResolveThrottleGlowWritersUnder(properties, keeps, glow, 1, 1));
            glow[0] = false;
            Assert.Equal(new[] { 1 }, GhostVisualBuilder.ResolveThrottleGlowWritersUnder(properties, keeps, glow, 3, 0));
            glow[3] = false;
            Assert.Empty(GhostVisualBuilder.ResolveThrottleGlowWritersUnder(properties, keeps, glow, 3, 0));
        }

        // A colour changer's glow alone leaves the part to the reentry glow it had before.
        [Fact]
        public void ReentryGlow_KeepsAPartWhoseOnlyGlowIsAColourChanger()
        {
            NozzleGlowInfo throttle = Ramp(0.025f, true);
            Assert.True(throttle.isColorChanger);
            var only = new HeatGhostInfo { throttleGlowsOnly = true, nozzleGlows = new List<NozzleGlowInfo> { throttle } };
            Assert.False(GhostVisualBuilder.HeatInfoOwnsPartMaterials(only));
            only.nozzleGlows.Add(Glow(false, BellRate, 1f, 0));
            Assert.True(GhostVisualBuilder.HeatInfoOwnsPartMaterials(only));
            var missing = new HeatGhostInfo { heatClipMissing = true, nozzleGlows = new List<NozzleGlowInfo> { throttle } };
            Assert.False(GhostVisualBuilder.HeatInfoOwnsPartMaterials(missing));
            Assert.True(GhostVisualBuilder.HeatInfoOwnsPartMaterials(
                new HeatGhostInfo { nozzleGlows = new List<NozzleGlowInfo> { throttle } }));
            Assert.False(GhostVisualBuilder.HeatInfoOwnsPartMaterials(null));
        }

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

        #region Vessel throttle

        // Stock's two clips without dependOnThrottle: TRJ_Heat (Whiplash), TurboJetHeat (Panther).
        [Theory]
        [InlineData(true, false, true, false)]
        [InlineData(true, true, false, false)]
        [InlineData(true, false, false, true)]
        [InlineData(false, false, true, true)]
        [InlineData(false, true, false, true)]
        public void VesselThrottle_IsReadWithoutDependOnThrottleOrOutput(
            bool dependOnEngineState, bool dependOnOutput, bool dependOnThrottle, bool expected)
            => Assert.Equal(expected, GhostVisualBuilder.GlowFollowsVesselThrottle(
                dependOnEngineState, dependOnOutput, dependOnThrottle));

        private static ConfigNode EngineNode(string independent, string limiter)
        {
            var node = new ConfigNode("MODULE");
            node.AddValue("name", "ModuleEnginesFX");
            if (independent != null) node.AddValue("independentThrottle", independent);
            if (limiter != null) node.AddValue("thrustPercentage", limiter);
            return node;
        }

        [Theory]
        [InlineData("False", "100", false, false, 1f)]
        [InlineData("True", "100", false, true, 1f)]
        [InlineData("False", "40", false, false, 0.4f)]
        [InlineData("False", "100", true, true, 1f)]
        [InlineData(null, null, false, false, 1f)]
        [InlineData("junk", "NaN", false, false, 1f)]
        [InlineData("False", "-3", false, false, 1f)]
        public void EngineThrottle_IsReadFromTheSavedModule(
            string independent, string limiter, bool locked, bool ignores, float limit)
        {
            EngineThrottleSetup setup = GhostVisualBuilder.ReadEngineThrottleSetup(EngineNode(independent, limiter), locked);
            Assert.Equal(ignores, setup.ignoresVesselThrottle);
            Assert.Equal((double)limit, setup.limit, 4);
            Assert.False(GhostVisualBuilder.ReadEngineThrottleSetup(null, false).ignoresVesselThrottle);
        }

        [Fact]
        public void VesselThrottle_ComesFromARunningMainThrottleEngine_ElseNoRecordedPowerTellsIt()
        {
            var main = new EngineThrottleSetup { limit = 1f };
            var limited = new EngineThrottleSetup { limit = 0.5f };
            var independent = new EngineThrottleSetup { limit = 1f, ignoresVesselThrottle = true };
            void Check(bool follows, int[] indices, EngineThrottleSetup[] engines, float scale, bool unrecorded)
            {
                GhostVisualBuilder.ResolveVesselThrottleSource(follows, indices, engines, out float s, out bool u);
                Assert.Equal((double)scale, s, 4);
                Assert.Equal(unrecorded, u);
            }
            // A glow on its engine's own throttle takes the power as it is.
            Check(false, new[] { 0 }, new[] { independent }, 1f, false);
            Check(false, new[] { 0 }, new[] { limited }, 1f, false);
            Check(true, new[] { 0 }, new[] { main }, 1f, false);
            Check(true, new[] { 0 }, new[] { limited }, 2f, false);
            Check(true, new[] { 0 }, new[] { independent }, 1f, true);
            Check(true, new[] { 1 }, new[] { main, independent }, 1f, true);
            Check(true, new[] { 0, 1 }, new[] { limited, independent }, 2f, false);
            Check(true, new[] { 0 }, new[] { new EngineThrottleSetup { limit = 0f } }, 1f, false);
            Check(true, new int[0], new EngineThrottleSetup[0], 1f, false);
        }

        [Theory]
        [InlineData("0", 0f)]
        [InlineData("0.65", 0.65f)]
        [InlineData("7", 1f)]
        [InlineData("-1", 0f)]
        [InlineData("x", float.NaN)]
        [InlineData("NaN", float.NaN)]
        [InlineData(null, float.NaN)]
        public void SnapshotMainThrottle_IsTheControlStates_OrUnknown(string raw, float expected)
        {
            var node = new ConfigNode("VESSEL");
            ConfigNode ctrl = node.AddNode("CTRLSTATE");
            if (raw != null) ctrl.AddValue("mainThrottle", raw);
            Assert.Equal(expected, GhostVisualBuilder.ReadSnapshotMainThrottle(node));
            Assert.True(float.IsNaN(GhostVisualBuilder.ReadSnapshotMainThrottle(new ConfigNode("VESSEL"))));
            Assert.True(float.IsNaN(GhostVisualBuilder.ReadSnapshotMainThrottle(null)));
        }

        // `2026-11-23_1201` Whiplash: independent throttle at 100 %, vessel throttle 0, 39 s in.
        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(0.4f, 0.4f)]
        [InlineData(1f, 1f)]
        [InlineData(float.NaN, 1f)]
        public void IndependentEngine_TheGlowFollowsTheSnapshotsVesselThrottle_WhileItRuns(float vesselThrottle, float target)
        {
            NozzleGlowInfo g = Glow(false, 0.0125f, 1f, 0);
            g.vesselThrottleUnrecorded = true;
            g.vesselThrottle = vesselThrottle;
            GhostPlaybackState state = StateWith(100.0, g);
            Assert.Equal(0f, g.targetLevel);
            Apply(state, Evt(PartEventType.EngineIgnited, 120.0, 1f));
            Assert.Equal(target, g.targetLevel);
            GhostPlaybackLogic.UpdateNozzleGlows(state, 159.0);
            Assert.Equal(target * (1.0 - Math.Exp(-0.0125 * 39.0)), g.writtenLevel, 3);
            Apply(state, Evt(PartEventType.EngineShutdown, 159.0));
            Assert.Equal(0f, g.targetLevel);
        }

        // The ordinary flight: the engine is on the main throttle, so its power tells it.
        [Fact]
        public void MainThrottleEngine_TheBellStillHeats_AndALimitedOneHeatsToTheThrottle()
        {
            NozzleGlowInfo g = Glow(false, 0.0125f, 1f / 0.4f, 0);
            GhostPlaybackState state = StateWith(100.0, g);
            Apply(state, Evt(PartEventType.EngineIgnited, 120.0, 0.4f));
            Assert.Equal((double)1f, g.targetLevel, 4);
            Apply(state, Evt(PartEventType.EngineThrottle, 121.0, 0.2f));
            Assert.Equal((double)0.5f, g.targetLevel, 4);
        }

        [Fact]
        public void StampPersistedLevels_GivesOnlyAnUnrecordedVesselThrottleTheSnapshots()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddNode("CTRLSTATE").AddValue("mainThrottle", "0.25");
            NozzleGlowInfo unrecorded = Glow(false, BellRate, 1f, 0);
            unrecorded.vesselThrottleUnrecorded = true;
            NozzleGlowInfo recorded = Glow(false, BellRate, 1f, 0);
            var heat = new HeatGhostInfo { nozzleGlows = new List<NozzleGlowInfo> { unrecorded, recorded } };
            GhostVisualBuilder.StampPersistedGlowLevels(heat, snapshot, trusted: true);
            Assert.Equal(0.25f, unrecorded.vesselThrottle);
            Assert.True(float.IsNaN(recorded.vesselThrottle));
            GhostVisualBuilder.StampPersistedGlowLevels(heat, snapshot, trusted: false);
            Assert.True(float.IsNaN(unrecorded.vesselThrottle));
        }

        [Fact]
        public void Wiring_BothGlowModulesResolveTheirThrottleSource()
        {
            string nozzle = Source("GhostVisualBuilder.NozzleGlow.cs");
            Assert.Contains("GlowFollowsVesselThrottle(throttle.dependOnEngineState, throttle.dependOnOutput,", nozzle);
            Assert.Contains("GlowFollowsVesselThrottle(effects.dependOnEngineState, effects.dependOnOutput,", nozzle);
            Assert.Contains("vesselThrottleUnrecorded = plan.vesselThrottleUnrecorded,", nozzle);
            Assert.Contains("vesselThrottleUnrecorded = driver.vesselThrottleUnrecorded,", nozzle);
        }

        #endregion

        #region ReStock RCS glow

        private static ConfigNode RcsGlowNode()
        {
            var node = new ConfigNode("MODULE");
            node.AddValue("name", "ModuleRestockRCSGlow");
            node.AddValue("shaderColorParameter", "_TintColor");
            ConfigNode alpha = node.AddNode("alphaCurve");
            alpha.AddValue("key", "0 0");
            alpha.AddValue("key", "1 1");
            foreach (string curve in new[] { "redCurve", "greenCurve", "blueCurve" })
                node.AddNode(curve).AddValue("key", "0 1");
            return node;
        }

        // PPS-12b `2026-11-23_1656` linearRcs: the nozzle's white glow disc, the recorded power 1.
        [Fact]
        public void ReStockRcsGlow_ShowsTheForceAtOnce_InTheModulesCurve()
        {
            NozzleGlowInfo g = GhostVisualBuilder.NewReStockRcsGlow(Pid, 0, RcsGlowNode(), out string property);
            Assert.Equal("_TintColor", property);
            Assert.True(g.isRcs);
            GhostPlaybackState state = StateWith(100.0, g);
            Assert.Equal(0f, g.writtenLevel);
            Assert.Equal(new Color(1f, 1f, 1f, 0f), GhostPlaybackLogic.ResolveNozzleGlowColor(g.samples, 0f));
            Apply(state, Evt(PartEventType.EngineIgnited, 110.0, 1f));
            Assert.Equal(0f, g.targetLevel);
            Apply(state, Evt(PartEventType.RCSActivated, 110.0, 1f));
            GhostPlaybackLogic.UpdateNozzleGlows(state, 110.02);
            Assert.Equal(1f, g.writtenLevel);
            Assert.Equal(new Color(1f, 1f, 1f, 1f), GhostPlaybackLogic.ResolveNozzleGlowColor(g.samples, 1f));
            Apply(state, Evt(PartEventType.RCSThrottle, 111.0, 0.4f));
            GhostPlaybackLogic.UpdateNozzleGlows(state, 111.02);
            Assert.Equal(0.4f, g.writtenLevel);
            Assert.Equal((double)0.4f, GhostPlaybackLogic.ResolveNozzleGlowColor(g.samples, 0.4f).a, 4);
            Apply(state, Evt(PartEventType.RCSStopped, 112.0));
            GhostPlaybackLogic.UpdateNozzleGlows(state, 112.02);
            Assert.Equal(0f, g.writtenLevel);
        }

        [Fact]
        public void ReStockRcsGlow_WithoutAPropertyWritesTheModulesDefault_AndWithoutAConfigHasNoCurve()
        {
            NozzleGlowInfo g = GhostVisualBuilder.NewReStockRcsGlow(Pid, 0, new ConfigNode("MODULE"), out string property);
            Assert.Equal("_EmissiveColor", property);
            Assert.NotNull(g.samples);
            Assert.Null(GhostVisualBuilder.NewReStockRcsGlow(Pid, 0, null, out _).samples);
        }

        // Its material is its own, so the part keeps the reentry glow and that glow keeps the material.
        [Fact]
        public void ReStockRcsGlow_LeavesThePartToTheReentryGlow()
        {
            NozzleGlowInfo g = GhostVisualBuilder.NewReStockRcsGlow(Pid, 0, RcsGlowNode(), out _);
            Assert.True(g.isColorChanger);
            Assert.False(GhostVisualBuilder.HeatInfoOwnsPartMaterials(
                new HeatGhostInfo { throttleGlowsOnly = true, nozzleGlows = new List<NozzleGlowInfo> { g } }));
            string owned = Source("GhostPlaybackLogic.HeatDeployablesLights.cs");
            Assert.Contains("if (!glow.isColorChanger) continue;", owned);
            Assert.Contains("owned[m] = glow.colors[i].property;", owned);
            string nozzle = Source("GhostVisualBuilder.NozzleGlow.cs");
            Assert.Contains("throttleGlowsOnly = glow.isColorChanger,", nozzle);
            Assert.Contains("BuildReStockRcsGlow(prefab, modelRoot, ghostModelNode, cloneMap, ref heatInfo, persistentId, partName);", nozzle);
            Assert.Contains("if (RcsGlowPowerIsPerNozzle(thrusters.Length))", nozzle);
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
