using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;
using WaveCase = Parsek.TestCommands.PartParitySweepRunner.WaveCase;

namespace Parsek.Tests
{
    /// <summary>
    /// The P7 reading-flight instrument fixes (design-part-ghost-parity.md Appendix H.2, H.3):
    /// a motion case or motion control never joins a stowed wave (PP-14 / PP-15 flew the three
    /// orbit motion controls and the two canards through the state flow), the wave flow
    /// dispatch, the slow and late controls' normal routes, the airbrake's done signal waiting
    /// for its surface, the air real-vs-real control judged on its curves only, and an
    /// animation group with no deploy clip excluded instead of timing out.
    /// </summary>
    public class PartParityMotionRoutingTests
    {
        private static PartParityCaseKey MotionKey(string part, string family, string context = null)
            => new PartParityCaseKey
            {
                Part = part,
                MotionOption = new PartParityMotionOption
                {
                    Family = family, Context = context ?? PartParityStateRules.ContextAlone, State = PartParityMotionRules.StateDeployed,
                },
            };

        private static WaveCase Stowed(string id)
            => new WaveCase { CaseId = id, ControlId = id, Key = new PartParityCaseKey { Part = "fuelTankSmallFlat" } };

        private static WaveCase MotionControl(string id, string part, string family, bool twin)
        {
            var wc = new WaveCase { CaseId = id, ControlId = id, Key = MotionKey(part, family), Motion = new PartParityMotionRun { ControlId = id } };
            if (twin) wc.Motion.Twin = new WaveCase { CaseId = id + PartParityMotionPlan.TwinSuffix };
            return wc;
        }

        private static WaveCase MotionCase(string id, string part, string family, string context = null)
            => new WaveCase { CaseId = id, Key = MotionKey(part, family, context), Motion = new PartParityMotionRun() };

        /// <summary>The queue of the PP-14 step: the three stowed controls, the three motion
        /// controls, then the motion cases (the canards first).</summary>
        private static List<WaveCase> Pp14Queue() => new List<WaveCase>
        {
            Stowed(TestCommandPartParitySweep.ControlEmpty),
            Stowed(TestCommandPartParitySweep.ControlGhostAbsent),
            Stowed(TestCommandPartParitySweep.ControlRealVsReal),
            MotionControl(TestCommandPartParitySweep.ControlMotionRealVsReal, "longAntenna", PartParityMotionRules.FamilyDeployable, twin: true),
            MotionControl(TestCommandPartParitySweep.ControlMotionGhostSlow, "airbrake1", PartParityMotionRules.FamilyAirbrake, twin: false),
            MotionControl(TestCommandPartParitySweep.ControlMotionGhostLate, "longAntenna", PartParityMotionRules.FamilyDeployable, twin: false),
            MotionCase("advancedcanard--default--motion-deployed--radial--1eade9dc", "AdvancedCanard", PartParityMotionRules.FamilyControlSurface, PartParityMotionRules.ContextRadial),
            MotionCase("canardcontroller--default--motion-deployed--radial--446d1ae2", "CanardController", PartParityMotionRules.FamilyControlSurface, PartParityMotionRules.ContextRadial),
        };

        [Fact]
        public void AStowedWave_StopsAtTheFirstMotionCase_SoNoMotionControlRunsTheStateFlow()
        {
            var queue = Pp14Queue();
            int cursor = 0;
            List<int> picked = PartParitySweepRunner.PickWave(queue, cursor, false, out string kind, out _);
            Assert.Equal(PartParityStateWave.KindStowed, kind);
            Assert.Equal(new[] { 0, 1, 2 }, picked.ToArray());
            Assert.Equal(PartParitySweepRunner.FlowStowedOrState, PartParitySweepRunner.WaveFlow(kind, false));

            List<WaveCase> wave = PartParitySweepRunner.TakeWave(queue, ref cursor, kind, picked.Count, false);
            Assert.Equal(new[] { "ctl-empty", "ctl-ghost-absent", "ctl-real-vs-real" }, wave.Select(c => c.CaseId).ToArray());
            Assert.Equal(3, cursor);
            Assert.All(wave, c => Assert.Null(c.Motion));

            // The pre-fix take ignored the picked count and read P6's kind (stowed for a key
            // with no state option), so it took all 8 cases of the PP-14 log's wave 1. Even
            // asked for more, the take stops at the first motion case.
            int again = 0;
            List<WaveCase> wide = PartParitySweepRunner.TakeWave(Pp14Queue(), ref again, PartParityStateWave.KindStowed,
                PartParitySweepPlan.MaxWaveCases, false);
            Assert.Equal(3, wide.Count);
            Assert.Equal(3, again);
        }

        [Fact]
        public void EveryMotionControl_GetsAMotionKind_AndTheOrbitMotionFlow()
        {
            var queue = Pp14Queue();
            for (int i = 3; i < queue.Count; i++)
            {
                string kind = PartParitySweepRunner.WaveKindOf(queue[i]);
                Assert.True(PartParityMotionPlan.IsMotionKind(kind), queue[i].CaseId + " kind=" + kind);
                Assert.Equal(PartParitySweepRunner.FlowMotion, PartParitySweepRunner.WaveFlow(kind, false));
                // An air run's motion wave runs the air flow, which hooks the motion phase itself.
                Assert.Equal(PartParitySweepRunner.FlowAir, PartParitySweepRunner.WaveFlow(kind, true));
            }
            // A motion-only control whose subject was not loaded (no motion run) still counts.
            var unloaded = new WaveCase { CaseId = "ctl-motion-ghost-late", ControlId = TestCommandPartParitySweep.ControlMotionGhostLate, Key = new PartParityCaseKey { Part = "longAntenna" } };
            Assert.True(PartParityMotionPlan.IsMotionKind(PartParitySweepRunner.WaveKindOf(unloaded)));

            // The waves after the stowed one: the pair alone, the airbrake control, then the late
            // control with the deployables of its family, the canards their own family.
            int cursor = 3;
            List<int> pair = PartParitySweepRunner.PickWave(queue, cursor, false, out string pairKind, out _);
            Assert.EndsWith(PartParityMotionPlan.KindPairSuffix, pairKind);
            Assert.Equal(new[] { 3 }, pair.ToArray());
            List<int> slow = PartParitySweepRunner.PickWave(queue, 4, false, out string slowKind, out _);
            Assert.Equal("motion:airbrake", slowKind);
            Assert.Equal(new[] { 4 }, slow.ToArray());
            List<int> canards = PartParitySweepRunner.PickWave(queue, 6, false, out string csKind, out _);
            Assert.Equal("motion:control-surface", csKind);
            Assert.Equal(new[] { 6, 7 }, canards.ToArray());
        }

        [Fact]
        public void TheFxAndStateKinds_KeepTheirFlows()
        {
            Assert.Equal(PartParitySweepRunner.FlowFx, PartParitySweepRunner.WaveFlow(PartParityStateWave.KindFxPrefix + "plume", false));
            Assert.Equal(PartParitySweepRunner.FlowFx, PartParitySweepRunner.WaveFlow(PartParityStateWave.KindFxPrefix + "plume", true));
            Assert.Equal(PartParitySweepRunner.FlowAir, PartParitySweepRunner.WaveFlow(PartParityStateWave.KindStowed, true));
            Assert.Equal(PartParitySweepRunner.FlowStowedOrState, PartParitySweepRunner.WaveFlow(PartParityStateWave.KindStowed, false));
            Assert.Equal(PartParitySweepRunner.FlowStowedOrState, PartParitySweepRunner.WaveFlow("state:quick", false));
        }

        [Fact]
        public void TheSlowAndLateControls_HaveANormalRoute_TheOthersNone()
        {
            Assert.True(PartParityMotionRules.HasNormalRoute(TestCommandPartParitySweep.ControlMotionGhostSlow));
            Assert.True(PartParityMotionRules.HasNormalRoute(TestCommandPartParitySweep.ControlMotionGhostLate));
            Assert.False(PartParityMotionRules.HasNormalRoute(TestCommandPartParitySweep.ControlMotionRealVsReal));
            Assert.False(PartParityMotionRules.HasNormalRoute(TestCommandPartParitySweep.ControlMotionRealVsRealAir));
            Assert.False(PartParityMotionRules.HasNormalRoute(null));
            Assert.Equal("ctl-motion-ghost-slow--normal", PartParityMotionRules.NormalRouteCaseId(TestCommandPartParitySweep.ControlMotionGhostSlow));
            Assert.Equal("ctl-motion-ghost-late--normal", PartParityMotionRules.NormalRouteCaseId(TestCommandPartParitySweep.ControlMotionGhostLate));
        }

        [Fact]
        public void AnAirbrake_IsDoneWhenItsSurfaceSettles_NotWhenItsFlagFlips()
        {
            // PP-14: "reached deployed after 0.00s (classifier=deployed)" on the press frame,
            // then "airbrake surface still ... after 3.3s".
            Assert.False(PartParityMotionPlan.P6MotionDone(PartParityMotionRules.KindSurface, true, false));
            Assert.True(PartParityMotionPlan.P6MotionDone(PartParityMotionRules.KindSurface, true, true));
            Assert.False(PartParityMotionPlan.P6MotionDone(PartParityMotionRules.KindSurface, false, true));
            // Every clip and canopy family keeps P6's reach.
            Assert.True(PartParityMotionPlan.P6MotionDone(PartParityMotionRules.KindClip, true, false));
            Assert.True(PartParityMotionPlan.P6MotionDone(PartParityMotionRules.KindCanopy, true, false));
            Assert.False(PartParityMotionPlan.P6MotionDone(PartParityMotionRules.KindClip, false, true));
        }

        private static PartParityMotionTiming Same()
            => new PartParityMotionTiming
            {
                LagS = 0.0, OnsetLagS = 0.0, DurationRatio = 1.0, CurveShapeDev = 0.0,
                RealCrossings = new[] { 0.52, 1.0, 1.5, 2.0, 2.39 },
            };

        [Fact]
        public void TheAirRealVsRealControl_IsJudgedOnItsCurvesOnly()
        {
            // PP-5: copy A and copy B agree on lag, ratio and shape, but each canopy is aimed
            // along its own airflow, so the last frame differs by a few pixels (ends=0).
            var e = new PartParityMotionControlEvidence
            {
                Altered = Same(), FrameS = 0.127, PredictedS = 1.017, EndsWithinAllowance = false,
            };
            var flags = new List<string> { PartParityMotionControls.FormatEvidence(e) };
            Assert.Equal(TestCommandPartParitySweep.ControlsOk,
                PartParityMotionControls.Judge(TestCommandPartParitySweep.ControlMotionRealVsRealAir, flags, "ok", out _));
            // The orbit control still reads its end frames.
            Assert.Equal(TestCommandPartParitySweep.ControlsBad,
                PartParityMotionControls.Judge(TestCommandPartParitySweep.ControlMotionRealVsReal, flags, "ok", out string detail));
            Assert.Contains("end frames differ", detail);
            // A curve that disagrees still fails the air control.
            e.Altered.DurationRatio = 1.2;
            Assert.Equal(TestCommandPartParitySweep.ControlsBad, PartParityMotionControls.Judge(TestCommandPartParitySweep.ControlMotionRealVsRealAir,
                new List<string> { PartParityMotionControls.FormatEvidence(e) }, "ok", out detail));
            Assert.Contains("durationRatio", detail);
        }

        private static PartParityStatePart AnimationGroupPart(string name, string deployAnimationName)
        {
            var m = new PartParityStateModule { Index = 0, Name = PartParityMotionRules.ModuleAnimationGroup };
            m.TypeChain.Add(PartParityMotionRules.ModuleAnimationGroup);
            m.TypeChain.Add("PartModule");
            m.Config = new ConfigNode("MODULE");
            m.Config.AddValue("name", PartParityMotionRules.ModuleAnimationGroup);
            if (deployAnimationName != null) m.Config.AddValue(PartParityMotionRules.KeyDeployAnimationName, deployAnimationName);
            var p = new PartParityStatePart { Name = name };
            p.Modules.Add(m);
            return p;
        }

        private static PartParityCaseKey Base(string part)
            => new PartParityCaseKey { Part = part, Source = "Squad", Skin = "default" };

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void AnAnimationGroupWithNoDeployClip_IsExcluded_NotTimedOut(string deployAnimationName)
        {
            // The orbital scanner names only its running loop (activeAnimationName = miniscanner;
            // ReStock: Spin): stock flips isDeployed at once, so PP-14 / PP-15 read
            // "window rule: no real frame reached 0.9 (best 0)" over zero progress transforms.
            var r = PartParityMotionRules.Enumerate(AnimationGroupPart("OrbitalScanner", deployAnimationName), Base("OrbitalScanner"));
            Assert.DoesNotContain(r.Cases, k => k.MotionOption?.Family == PartParityMotionRules.FamilyAnimationGroup);
            Assert.Contains(r.Exclusions, x => x.Reason == PartParityStateRules.ExcludeNoAnimation
                && x.Family == PartParityMotionRules.FamilyAnimationGroup && x.ModuleIndex == 0);
            // Its running loop stays excluded as before.
            Assert.Contains(r.Exclusions, x => x.Reason == PartParityMotionRules.ExcludeLoopPhase);
            Assert.All(r.Exclusions, x => Assert.Contains(x.Reason, PartParityMotionRules.ExclusionReasons));
        }

        [Fact]
        public void AnAnimationGroupWithADeployClip_KeepsItsCase()
        {
            var r = PartParityMotionRules.Enumerate(AnimationGroupPart("MiniDrill", "Deploy"), Base("MiniDrill"));
            Assert.Single(r.Cases, k => k.MotionOption?.Family == PartParityMotionRules.FamilyAnimationGroup);
            Assert.DoesNotContain(r.Exclusions, x => x.Reason == PartParityStateRules.ExcludeNoAnimation);
            // A fixture line that read no config key keeps the case (the committed counts).
            var noConfig = AnimationGroupPart("MiniDrill", "Deploy");
            noConfig.Modules[0].Config = null;
            Assert.False(PartParityMotionRules.HasNoDeployAnimation(noConfig.Modules[0]));
            Assert.Single(PartParityMotionRules.Enumerate(noConfig, Base("MiniDrill")).Cases,
                k => k.MotionOption?.Family == PartParityMotionRules.FamilyAnimationGroup);
        }
    }
}
