using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The engine clips that move transforms: the variable nozzles of the Whiplash and the Panther
    /// (FXModuleAnimateThrottle) and the Goliath's fan (the same module with affectTime off).
    /// </summary>
    [Collection("Sequential")]
    public class GhostNozzlePoseTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostNozzlePoseTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose() => ParsekLog.ResetTestOverrides();

        private const uint Pid = 777u;
        private const int Dry = 0;
        private const int Wet = 1;

        private static Transform NewTransform()
            => (Transform)FormatterServices.GetUninitializedObject(typeof(Transform));

        private static NozzleGlowInfo Clip(
            string name, int layer, bool weighted, float responseSpeed, Transform transform, NozzlePoseTrack track,
            params int[] modules)
        {
            var glow = new NozzleGlowInfo
            {
                partPersistentId = Pid,
                moduleName = "FXModuleAnimateThrottle",
                animationName = name,
                approachRate = GhostPlaybackLogic.ComputeNozzleGlowRate(responseSpeed),
                moduleKeys = new ulong[modules.Length],
                modulePowers = new float[modules.Length],
                pose = new NozzlePoseClip
                {
                    layer = layer,
                    weighted = weighted,
                    weightRate = GhostPlaybackLogic.ComputeNozzleGlowRate(
                        responseSpeed * GhostVisualBuilder.NozzlePoseWeightResponseScale),
                },
            };
            for (int i = 0; i < modules.Length; i++)
                glow.moduleKeys[i] = FlightRecorder.EncodeEngineKey(Pid, modules[i]);
            glow.pose.transforms.Add(transform);
            glow.pose.tracks.Add(track);
            return glow;
        }

        private static NozzlePoseTrack Scales(params float[] values)
        {
            var track = new NozzlePoseTrack { scales = new Vector3[values.Length] };
            for (int i = 0; i < values.Length; i++) track.scales[i] = new Vector3(values[i], values[i], 1f);
            return track;
        }

        // turboJet.mu, TurboJet/Nozzle: dry 1 -> 0.6, wet 0.7728 -> 1; cfg layers 1 and 2,
        // responseSpeed 0.05 and 0.08, weightOnOperational.
        private static NozzleGlowInfo[] Panther()
        {
            Transform nozzle = NewTransform();
            return new[]
            {
                Clip("TurboJetNozzleWet", 2, true, 0.08f, nozzle, Scales(0.7728f, 1f), Wet),
                Clip("TurboJetNozzleDry", 1, true, 0.05f, nozzle, Scales(1f, 0.6f), Dry),
            };
        }

        private static GhostPlaybackState StateWith(double startUT, params NozzleGlowInfo[] glows)
        {
            var state = new GhostPlaybackState
            {
                heatInfos = new Dictionary<uint, HeatGhostInfo>
                {
                    [Pid] = new HeatGhostInfo { partPersistentId = Pid, nozzleGlows = new List<NozzleGlowInfo>(glows) },
                },
            };
            GhostPlaybackLogic.BindNozzleGlows(state,
                new Recording { ExplicitStartUT = startUT, ExplicitEndUT = startUT + 1000.0 }, _ => null);
            return state;
        }

        private static void Apply(GhostPlaybackState state, PartEventType type, double ut, int module, float value = 0f)
            => GhostPlaybackLogic.ApplyNozzleGlowEvent(state, new PartEvent
            {
                ut = ut,
                partPersistentId = Pid,
                moduleIndex = module,
                eventType = type,
                value = value,
            });

        private static float NozzleScale(GhostPlaybackState state, double ut)
        {
            GhostPlaybackLogic.UpdateNozzleGlows(state, ut);
            NozzlePoseInfo pose = state.nozzlePoses[0];
            Assert.True(GhostPlaybackLogic.BlendNozzlePosition(pose, 0, true, out Vector3 scale));
            return scale.x;
        }

        #region Poses

        [Fact]
        public void Pose_OnePerPart_LowestLayerFirst_AndSharedTransformsOnce()
        {
            NozzleGlowInfo[] p = Panther();
            GhostPlaybackState state = StateWith(100.0, p);
            NozzlePoseInfo pose = Assert.Single(state.nozzlePoses);
            Assert.Equal("TurboJetNozzleDry", pose.glows[0].animationName);
            Assert.Equal("TurboJetNozzleWet", pose.glows[1].animationName);
            Assert.Single(pose.targets);
            Assert.NotNull(pose.clipTracks[0][0]);
            Assert.NotNull(pose.clipTracks[1][0]);
        }

        [Fact]
        public void Pose_AnEngineThatHasNotRunStandsAtTheLowestLayersStart()
        {
            GhostPlaybackState state = StateWith(100.0, Panther());
            Assert.Equal((double)1f, NozzleScale(state, 500.0), 4);
            Assert.True(state.nozzlePoses[0].settled);
        }

        // PP-5 2026-11-24_0628: burn-full-m1 was 1971 px too wide, the dry nozzle closes to 0.6.
        [Theory]
        [InlineData(0.99f, 0.604f)]
        [InlineData(0.3f, 0.88f)]
        public void Pose_TheDryNozzleFollowsTheRecordedPower(float power, float expected)
        {
            GhostPlaybackState state = StateWith(100.0, Panther());
            Apply(state, PartEventType.EngineIgnited, 120.0, Dry, power);
            Assert.Equal((double)expected, NozzleScale(state, 140.0), 3);
            Assert.True(state.nozzlePoses[0].settled);
        }

        [Theory]
        [InlineData(0.99f, 0.9977f)]
        [InlineData(0.3f, 0.8410f)]
        public void Pose_TheAfterburnersClipTakesOver(float power, float expected)
        {
            GhostPlaybackState state = StateWith(100.0, Panther());
            Apply(state, PartEventType.EngineIgnited, 120.0, Dry, 1f);
            Assert.Equal((double)0.6f, NozzleScale(state, 140.0), 3);
            Apply(state, PartEventType.EngineShutdown, 140.0, Dry);
            Apply(state, PartEventType.EngineIgnited, 140.0, Wet, power);
            // Mid hand-over (the wet weight moves 0.12 a step) the pose is between the two.
            float mid = NozzleScale(state, 140.1);
            Assert.InRange(mid, 0.601f, 0.99f);
            Assert.False(state.nozzlePoses[0].settled);
            Assert.Equal((double)expected, NozzleScale(state, 160.0), 3);
            Assert.True(state.nozzlePoses[0].settled);
        }

        // The recording does not tell a shut-down engine from an idle one: the clip that ran last
        // keeps the pose and its time goes back to 0.
        [Fact]
        public void Pose_AfterAShutdownTheLastClipKeepsThePose()
        {
            GhostPlaybackState state = StateWith(100.0, Panther());
            Apply(state, PartEventType.EngineIgnited, 120.0, Wet, 1f);
            Assert.Equal((double)1f, NozzleScale(state, 140.0), 3);
            Apply(state, PartEventType.EngineShutdown, 140.0, Wet);
            Assert.Equal((double)0.7728f, NozzleScale(state, 170.0), 3);
        }

        [Fact]
        public void Pose_ARowThatStartsWithTheAfterburnerLitHasNoHandOver()
        {
            GhostPlaybackState state = StateWith(100.0, Panther());
            Apply(state, PartEventType.EngineIgnited, 100.1, Wet, 1f);
            GhostPlaybackLogic.UpdateNozzleGlows(state, 100.12);
            Assert.Equal(0f, state.nozzlePoses[0].weights[0]);
            Assert.Equal(1f, state.nozzlePoses[0].weights[1]);
        }

        [Fact]
        public void Pose_AReplayedCycleStartsOver()
        {
            GhostPlaybackState state = StateWith(100.0, Panther());
            Apply(state, PartEventType.EngineIgnited, 120.0, Wet, 1f);
            NozzleScale(state, 140.0);
            GhostPlaybackLogic.RestartNozzleGlows(state);
            Assert.False(state.nozzlePoses[0].settled);
            Assert.Equal((double)1f, NozzleScale(state, 100.0), 4);
            Assert.Equal(0f, state.nozzlePoses[0].weights[1]);
        }

        [Fact]
        public void Pose_IsAFunctionOfTheRecordedTime()
        {
            GhostPlaybackState a = StateWith(100.0, Panther());
            GhostPlaybackState b = StateWith(100.0, Panther());
            foreach (GhostPlaybackState s in new[] { a, b })
            {
                Apply(s, PartEventType.EngineIgnited, 120.0, Dry, 0.7f);
                if (s == b) for (double ut = 120.0; ut < 120.5; ut += 0.016) GhostPlaybackLogic.UpdateNozzleGlows(s, ut);
            }
            Assert.Equal((double)NozzleScale(a, 120.5), NozzleScale(b, 120.5), 4);
        }

        [Fact]
        public void Pose_ASettledPartIsNotWrittenAgain()
        {
            GhostPlaybackState state = StateWith(100.0, Panther());
            Apply(state, PartEventType.EngineIgnited, 120.0, Dry, 1f);
            NozzleScale(state, 160.0);
            Assert.Contains(logLines, l => l.Contains("[GhostFx]") && l.Contains("Nozzle pose pid=777 settled")
                && l.Contains("'TurboJetNozzleDry' at 0.000 weight 1.000, 'TurboJetNozzleWet' at 0.000 weight 0.000"));
            state.nozzlePoses[0].clips[0].writtenLevel = 0.123f;
            GhostPlaybackLogic.UpdateNozzleGlows(state, 200.0);
            Assert.Equal(0.123f, state.nozzlePoses[0].clips[0].writtenLevel);
            Apply(state, PartEventType.EngineThrottle, 200.0, Dry, 0.5f);
            GhostPlaybackLogic.UpdateNozzleGlows(state, 230.0);
            Assert.Equal((double)0.5f, state.nozzlePoses[0].clips[0].writtenLevel, 3);
        }

        // turboRamJet.mu, TurboRamJetNozzle: open at both ends, closed at 0.167; no weight of its own.
        [Fact]
        public void Pose_AnUnweightedClipIsAlwaysAtFullWeight()
        {
            NozzleGlowInfo whiplash = Clip("TurboRamJetNozzle", 1, false, 1f, NewTransform(), Scales(1.15f, 0.9f, 1.15f), 0);
            GhostPlaybackState state = StateWith(100.0, whiplash);
            Assert.Equal((double)1.15f, NozzleScale(state, 110.0), 4);
            Apply(state, PartEventType.EngineIgnited, 120.0, 0, 0.25f);
            Assert.Equal((double)1.025f, NozzleScale(state, 130.0), 3);
            Assert.Equal(1f, state.nozzlePoses[0].weights[0]);
            Apply(state, PartEventType.EngineShutdown, 130.0, 0);
            Assert.Equal((double)1.15f, NozzleScale(state, 140.0), 3);
            Assert.Equal(1f, state.nozzlePoses[0].weights[0]);
        }

        [Fact]
        public void Pose_AnotherPartsEventLeavesItSettled()
        {
            GhostPlaybackState state = StateWith(100.0, Panther());
            NozzleScale(state, 110.0);
            GhostPlaybackLogic.ApplyNozzleGlowEvent(state, new PartEvent
            {
                ut = 120.0, partPersistentId = Pid + 1, moduleIndex = Dry, eventType = PartEventType.EngineIgnited, value = 1f,
            });
            Assert.True(state.nozzlePoses[0].settled);
        }

        [Fact]
        public void Blend_IsUnitysLayerBlend()
        {
            NozzleGlowInfo[] p = Panther();
            GhostPlaybackState state = StateWith(100.0, p);
            NozzlePoseInfo pose = state.nozzlePoses[0];
            pose.levels = new[] { 1f, 1f };
            pose.weights = new[] { 1f, 0.25f };
            Assert.True(GhostPlaybackLogic.BlendNozzlePosition(pose, 0, true, out Vector3 v));
            Assert.Equal(0.25 * 1.0 + 0.75 * 0.6, v.x, 4);
            // What is left over is normalized away.
            pose.weights = new[] { 0f, 0.25f };
            Assert.True(GhostPlaybackLogic.BlendNozzlePosition(pose, 0, true, out v));
            Assert.Equal(1.0, v.x, 4);
            pose.weights = new[] { 0f, 0f };
            Assert.False(GhostPlaybackLogic.BlendNozzlePosition(pose, 0, true, out _));
            pose.weights = new[] { 1f, 1f };
            Assert.False(GhostPlaybackLogic.BlendNozzlePosition(pose, 0, false, out _));
            Assert.False(GhostPlaybackLogic.BlendNozzleRotation(pose, 0, out _));
        }

        [Fact]
        public void Blend_RotationsAreUnitAndOnOneSide()
        {
            // 0 and 4 degrees about z, the second as its negative quaternion.
            float s = (float)Math.Sin(Math.PI / 90.0), c = (float)Math.Cos(Math.PI / 90.0);
            var track = new NozzlePoseTrack { rotations = new[] { new Quaternion(0f, 0f, 0f, 1f), new Quaternion(0f, 0f, -s, -c) } };
            NozzleGlowInfo clip = Clip("Petal", 1, false, 1f, NewTransform(), track, 0);
            GhostPlaybackState state = StateWith(100.0, clip);
            state.nozzlePoses[0].levels = new[] { 0.5f };
            state.nozzlePoses[0].weights = new[] { 1f };
            Assert.True(GhostPlaybackLogic.BlendNozzleRotation(state.nozzlePoses[0], 0, out Quaternion q));
            Assert.Equal(Math.Sin(Math.PI / 180.0), Math.Abs(q.z), 4);
            Assert.Equal(1.0, Math.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w), 5);
        }

        [Theory]
        [InlineData(0f, 0, 0f)]
        [InlineData(-1f, 0, 0f)]
        [InlineData(float.NaN, 0, 0f)]
        [InlineData(0.5f, 2, 0f)]
        [InlineData(0.625f, 2, 0.5f)]
        [InlineData(1f, 4, 0f)]
        [InlineData(2f, 4, 0f)]
        public void SampleIndex_IsInsideTheSamples(float level, int index, float fraction)
        {
            GhostPlaybackLogic.LerpSampleIndex(5, level, out int i, out float f);
            Assert.Equal(index, i);
            Assert.Equal(fraction, f);
            GhostPlaybackLogic.LerpSampleIndex(1, level, out i, out f);
            Assert.Equal(0, i);
            Assert.Equal(0f, f);
        }

        // A clip that only moves transforms must not take the part's materials from the reentry glow.
        [Fact]
        public void Pose_APartThatOnlyMovesKeepsItsReentryGlow()
        {
            var info = new HeatGhostInfo { throttleGlowsOnly = true, nozzleGlows = new List<NozzleGlowInfo>(Panther()) };
            Assert.False(GhostVisualBuilder.HeatInfoOwnsPartMaterials(info));
            info.nozzleGlows[0].colors.Add(new NozzleGlowColor { property = "_EmissiveColor" });
            Assert.True(GhostVisualBuilder.HeatInfoOwnsPartMaterials(info));
        }

        #endregion

        #region Engine spin

        // turboFanSize2.cfg: responseSpeed 0.05, baseAnimSpeed 0.01, baseAnimSpeedMult 100; the clip
        // is one second, one turn.
        private static NozzleGlowInfo Fan()
            => GhostVisualBuilder.NewEngineSpin(Pid, new NozzleGlowPlan
            {
                moduleName = "FXModuleAnimateThrottle",
                animationName = "TF2FanSpin",
                moduleIndices = new[] { 0 },
                powerScale = 1f,
                responseSpeed = 0.05f,
                spins = true,
                spinBaseSpeed = 0.01f,
                spinSpeedMult = 100f,
            }, 1f, null);

        /// <summary>Stock's FixedUpdate (decompiled): the speed after <paramref name="seconds"/> and
        /// the turns made, at 0.02 s a step.</summary>
        private static void StockFan(float throttle, double seconds, ref float speed, ref double turns)
        {
            for (int i = 0; i < (int)Math.Round(seconds / 0.02); i++)
            {
                float target = throttle > 0f ? 0.01f + throttle * 100f : 0f;
                speed += (target - speed) * (0.05f * 0.02f);
                if (speed > 0.99995f) speed = 1f;
                if (speed < 5e-5f) speed = 0f;
                turns += speed * 0.02;
            }
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(0.3f)]
        public void Spin_RunsAtTheClipsOwnRateWhileTheEngineRuns(float power)
        {
            NozzleGlowInfo fan = Fan();
            GhostPlaybackState state = StateWith(100.0, fan);
            Apply(state, PartEventType.EngineIgnited, 120.0, 0, power);
            float speed = 0f;
            double turns = 0.0;
            StockFan(power, 30.0, ref speed, ref turns);
            Assert.Equal(1f, speed);
            Assert.Equal(1f, GhostPlaybackLogic.NozzleGlowScalarAt(fan, 150.0));
            double ghost = GhostPlaybackLogic.EngineSpinDistance(fan.startLevel, fan.targetLevel, fan.approachRate, 30.0);
            Assert.InRange(ghost, turns - 0.05, turns + 0.05);
        }

        [Fact]
        public void Spin_CoastsDownAfterTheShutdown()
        {
            NozzleGlowInfo fan = Fan();
            GhostPlaybackState state = StateWith(100.0, fan);
            Apply(state, PartEventType.EngineIgnited, 120.0, 0, 1f);
            Apply(state, PartEventType.EngineShutdown, 150.0, 0);
            float speed = 1f;
            double turns = 0.0;
            StockFan(0f, 20.0, ref speed, ref turns);
            Assert.Equal((double)speed, GhostPlaybackLogic.NozzleGlowScalarAt(fan, 170.0), 2);
            Assert.InRange(GhostPlaybackLogic.EngineSpinDistance(fan.startLevel, 0f, fan.approachRate, 20.0),
                turns - 0.05, turns + 0.05);
            Assert.Equal(0f, GhostPlaybackLogic.NozzleGlowScalarAt(fan, 400.0));
            Assert.Equal((double)GhostPlaybackLogic.EngineSpinPhaseAt(fan, 900.0), GhostPlaybackLogic.EngineSpinPhaseAt(fan, 901.0), 2);
        }

        [Fact]
        public void Spin_ThePhaseIsContinuousOverAnEvent()
        {
            NozzleGlowInfo fan = Fan();
            GhostPlaybackState state = StateWith(100.0, fan);
            Assert.Equal(0f, GhostPlaybackLogic.EngineSpinPhaseAt(fan, 110.0));
            Apply(state, PartEventType.EngineIgnited, 120.0, 0, 1f);
            foreach (double ut in new[] { 123.37, 151.8, 160.25 })
            {
                float before = GhostPlaybackLogic.EngineSpinPhaseAt(fan, ut);
                Apply(state, ut > 155.0 ? PartEventType.EngineShutdown : PartEventType.EngineThrottle, ut, 0, 0.4f);
                Assert.Equal((double)before, GhostPlaybackLogic.EngineSpinPhaseAt(fan, ut), 3);
            }
        }

        [Fact]
        public void Spin_AStillFanIsNotWritten_AndARestartWritesAgain()
        {
            NozzleGlowInfo fan = Fan();
            GhostPlaybackState state = StateWith(100.0, fan);
            GhostPlaybackLogic.UpdateNozzleGlows(state, 110.0);
            Assert.Equal(0f, fan.spin.writtenPhase);
            Apply(state, PartEventType.EngineIgnited, 120.0, 0, 1f);
            GhostPlaybackLogic.UpdateNozzleGlows(state, 130.4);
            Assert.NotEqual(0f, fan.spin.writtenPhase);
            GhostPlaybackLogic.RestartNozzleGlows(state);
            Assert.True(float.IsNaN(fan.spin.writtenPhase));
            GhostPlaybackLogic.UpdateNozzleGlows(state, 100.0);
            Assert.Equal(0f, fan.spin.writtenPhase);
        }

        [Theory]
        [InlineData(0.5f, 0.2f, 1f, 0.0)]
        [InlineData(0.5f, 0.2f, 1f, -3.0)]
        [InlineData(0.5f, 0.2f, 1f, double.NaN)]
        public void SpinDistance_NothingBeforeItsStart(float start, float target, float rate, double elapsed)
            => Assert.Equal(0.0, GhostPlaybackLogic.EngineSpinDistance(start, target, rate, elapsed));

        [Fact]
        public void SpinDistance_EveryRate()
        {
            Assert.Equal(2.0, GhostPlaybackLogic.EngineSpinDistance(0.5f, 50f, 0f, 4.0), 5);
            Assert.Equal(4.0, GhostPlaybackLogic.EngineSpinDistance(0.5f, 50f, float.PositiveInfinity, 4.0), 5);
            Assert.Equal(1.0, GhostPlaybackLogic.EngineSpinDistance(0.5f, 0.25f, float.PositiveInfinity, 4.0), 4);
            Assert.Equal(7.0, GhostPlaybackLogic.EngineSpinDistance(1f, 100f, 0.05f, 7.0), 5);
            Assert.Equal(7.0, GhostPlaybackLogic.EngineSpinDistance(3f, 100f, 0.05f, 7.0), 5);
            Assert.Equal(7.0, GhostPlaybackLogic.EngineSpinDistance(1f, 1f, 0.05f, 7.0), 5);
        }

        [Fact]
        public void SpinTarget_IsStocks()
        {
            EngineSpin spin = Fan().spin;
            Assert.Equal(0f, GhostPlaybackLogic.EngineSpinTarget(spin, 0f));
            Assert.Equal((double)30.01f, GhostPlaybackLogic.EngineSpinTarget(spin, 0.3f), 3);
        }

        #endregion

        [Fact]
        public void Wiring_TheBuilderTakesPosesAndLoopingClips()
        {
            string dir = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "Source", "Parsek", "Ghost"));
            string nozzle = File.ReadAllText(Path.Combine(dir, "GhostVisualBuilder.NozzleGlow.cs")).Replace("\r\n", "\n");
            Assert.Contains("if (!throttle.affectTime)\n                    {\n" +
                "                        if (throttle.animWrapMode != (int)WrapMode.Loop) continue;\n" +
                "                        plan.spins = true;", nozzle);
            Assert.Contains("plan.layer = throttle.layer;", nozzle);
            Assert.Contains("plan.weighted = throttle.dependOnEngineState && throttle.weightOnOperational;", nozzle);
            Assert.Contains("if (BuildEngineSpin(prefab, plan, ghostModelNode, ref heatInfo, persistentId)) spins++;", nozzle);
            Assert.Contains("if (firstAnimationOnly) SampleNozzlePoses(tempClone.transform, animations, states, clip.poses);", nozzle);
            Assert.Contains("throttleGlowsOnly = glow.isColorChanger || glow.MovesOnly,", nozzle);
        }
    }
}
