using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;
using WaveCase = Parsek.TestCommands.PartParitySweepRunner.WaveCase;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the live motion driver's decisions (design-part-ghost-parity.md
    /// Appendix H.2, H.3, piece M3): <see cref="PartParityMotionPlan"/> (wave kinds and
    /// admission, the framing growth, the render primes, the late route's shift, the window
    /// extensions, the surface done signal, the predicted time) and
    /// <see cref="PartParityMotionControls"/> (the evidence flag round trip, the judge
    /// dispatch, and the end-frame allowance and last-frame rule as partparity.py derives them).
    /// </summary>
    public class PartParityMotionPlanTests
    {
        private static PartParityCaseKey Key(string family, string context = null, string state = "motion-deployed")
            => new PartParityCaseKey
            {
                Part = "p",
                MotionOption = new PartParityMotionOption { Family = family, Context = context ?? PartParityStateRules.ContextAlone, State = state },
            };

        [Fact]
        public void WaveKind_HoldsOneFamily_TheAirStage_AndThePairApart()
        {
            Assert.Equal("motion:deployable", PartParityMotionPlan.WaveKind(Key("deployable"), false));
            Assert.Equal("motion:deployable:pair", PartParityMotionPlan.WaveKind(Key("deployable"), true));
            Assert.Equal("motion:parachute:motion-semi",
                PartParityMotionPlan.WaveKind(Key("parachute", PartParityStateRules.ContextAir, "motion-semi"), false));
            Assert.Equal("motion:unknown", PartParityMotionPlan.WaveKind(new PartParityCaseKey { Part = "p" }, false));
            Assert.True(PartParityMotionPlan.IsMotionKind("motion:gear"));
            Assert.False(PartParityMotionPlan.IsMotionKind("state:Short"));
            Assert.False(PartParityMotionPlan.IsMotionKind(null));
        }

        [Fact]
        public void TheWaveSize_IsTheStateWaveInOrbitAndTheAirWaveInTheAir()
        {
            Assert.Equal(PartParityMotionRules.MaxMotionWaveCases, PartParityMotionPlan.MaxWaveCases(false));
            Assert.Equal(PartParitySweepPlan.MaxAirWaveCases, PartParityMotionPlan.MaxWaveCases(true));
        }

        [Fact]
        public void AdmitWave_StartsTheFirstWave_AndALaterOneOnlyWhenItsWorstCaseFits()
        {
            double worst = PartParityMotionPlan.WaveWorstCaseSeconds(4);
            Assert.True(worst > PartParityMotionRules.WindowCapSeconds);
            Assert.Equal(worst + PartParityMotionPlan.CaptureSecondsPerCase, PartParityMotionPlan.WaveWorstCaseSeconds(5), 9);
            Assert.True(PartParityMotionPlan.AdmitWave(10_000.0, 60, true, 4));
            Assert.False(PartParityMotionPlan.AdmitWave(10_000.0, 60, false, 4));
            Assert.True(PartParityMotionPlan.AdmitWave(0.0, 100_000, false, 4));
        }

        [Fact]
        public void Grow_WidensTheBoxAboutItsCentre()
        {
            PartParityAabb box = PartParityAabb.FromMinMax(new[] { 0.0, -1.0, 2.0 }, new[] { 2.0, 1.0, 4.0 });
            PartParityAabb g = PartParityMotionPlan.Grow(box, 0.2);
            Assert.Equal(new[] { -0.2, -1.2, 1.8 }, g.Min.Select(v => System.Math.Round(v, 9)).ToArray());
            Assert.Equal(new[] { 2.2, 1.2, 4.2 }, g.Max.Select(v => System.Math.Round(v, 9)).ToArray());
            var empty = new PartParityAabb();
            Assert.Same(empty, PartParityMotionPlan.Grow(empty, 0.2));
            Assert.Null(PartParityMotionPlan.Grow(null, 0.2));
        }

        [Fact]
        public void RenderPrimes_AreInUtOrder_SameTimeFirst_WithSubPrimesUnderTheMaxStep()
        {
            var same = new List<double> { 100.0, 100.5, 103.0 };
            var matched = new List<double> { 100.0, double.NaN, 100.25 };
            List<PartParityMotionPlan.RenderPrime> p = PartParityMotionPlan.RenderPrimes(same, matched, 100.0, 1.0);
            Assert.Equal(new[] { 100.0, 100.0, 100.25, 100.5, 101.5, 102.5, 103.0 }, p.Select(x => x.Ut).ToArray());
            Assert.False(p[0].Matched);
            Assert.True(p[1].Matched);
            Assert.Equal(0, p[1].Frame);
            Assert.Equal(2, p[2].Frame);
            Assert.True(p[2].Matched);
            Assert.Equal(-1, p[4].Frame);
            Assert.Equal(-1, p[5].Frame);
            Assert.Equal(2, p[6].Frame);
            for (int i = 1; i < p.Count; i++) Assert.True(p[i].Ut - p[i - 1].Ut <= 1.0 + 1e-9);
            Assert.Empty(PartParityMotionPlan.RenderPrimes(null, null, 0.0, 1.0));
        }

        [Fact]
        public void ShiftEvents_MovesEveryPolledEventAndLeavesTheInputAlone()
        {
            var polled = new List<PartEvent> { new PartEvent { ut = 10.0, partPersistentId = 7 }, new PartEvent { ut = 12.5 } };
            List<PartEvent> late = PartParityMotionPlan.ShiftEvents(polled, PartParityMotionRules.LateShiftSeconds);
            Assert.Equal(new[] { 11.0, 13.5 }, late.Select(e => e.ut).ToArray());
            Assert.Equal(7u, late[0].partPersistentId);
            Assert.Equal(10.0, polled[0].ut);
            Assert.Empty(PartParityMotionPlan.ShiftEvents(null, 1.0));
        }

        [Fact]
        public void TheGhostRun_AndTheControlExtension_FollowTheKindAndTheControl()
        {
            Assert.Equal(3.5, PartParityMotionPlan.GhostExpectedRunSeconds(PartParityMotionRules.KindClip, 3.5));
            Assert.Equal(0.0, PartParityMotionPlan.GhostExpectedRunSeconds(PartParityMotionRules.KindClip, double.NaN));
            Assert.Equal(PartParityMotionRules.ServoStopHoldSeconds, PartParityMotionPlan.GhostExpectedRunSeconds(PartParityMotionRules.KindServo, 9.0));
            Assert.Equal(0.0, PartParityMotionPlan.GhostExpectedRunSeconds(PartParityMotionRules.KindCanopy, 9.0));
            Assert.Equal(2.0 * (PartParityMotionRules.SlowClipFactor - 1.0),
                PartParityMotionPlan.ControlExtraSeconds(TestCommandPartParitySweep.ControlMotionGhostSlow, 2.0));
            Assert.Equal(PartParityMotionRules.LateShiftSeconds,
                PartParityMotionPlan.ControlExtraSeconds(TestCommandPartParitySweep.ControlMotionGhostLate, 2.0));
            Assert.Equal(0.0, PartParityMotionPlan.ControlExtraSeconds(TestCommandPartParitySweep.ControlMotionRealVsReal, 2.0));
            Assert.Equal(0.0, PartParityMotionPlan.ControlExtraSeconds(null, 2.0));
        }

        private static List<KeyValuePair<double, double>> Samples(params double[] tv)
        {
            var list = new List<KeyValuePair<double, double>>();
            for (int i = 0; i + 1 < tv.Length; i += 2) list.Add(new KeyValuePair<double, double>(tv[i], tv[i + 1]));
            return list;
        }

        [Fact]
        public void SurfaceDone_WantsAMovedDeflectionStillOverTheWindow()
        {
            double w = PartParityMotionRules.SurfaceStillWindowSeconds, still = PartParityMotionRules.SurfaceStillDeg;
            Assert.True(PartParityMotionPlan.SurfaceDone(Samples(0.0, 0.0, 1.0, 20.0, 1.0 + w, 20.0), 0.0));
            // Still moving inside the window.
            Assert.False(PartParityMotionPlan.SurfaceDone(Samples(0.0, 0.0, 1.0, 10.0, 1.0 + w, 20.0), 0.0));
            // Never left its start.
            Assert.False(PartParityMotionPlan.SurfaceDone(Samples(0.0, 0.0, 1.0 + w, still / 2.0), 0.0));
            // Samples shorter than the window.
            Assert.False(PartParityMotionPlan.SurfaceDone(Samples(0.0, 20.0, w / 2.0, 20.0), 0.0));
            Assert.False(PartParityMotionPlan.SurfaceDone(null, 0.0));
        }

        [Fact]
        public void ChoosePredicted_PrefersTheKindsFormula_ThenThePlayingClip_ThenTheLongest_ThenTheCap()
        {
            Assert.Equal(4.0, PartParityMotionPlan.ChoosePredicted(PartParityMotionRules.KindServo, 2.0, 4.0, 1.0, 9.0, out string src));
            Assert.Equal("servo", src);
            Assert.Equal(1.0, PartParityMotionPlan.ChoosePredicted(PartParityMotionRules.KindSurface, 2.0, 4.0, 1.0, 9.0, out src));
            Assert.Equal("surface", src);
            Assert.Equal(2.0, PartParityMotionPlan.ChoosePredicted(PartParityMotionRules.KindClip, 2.0, 4.0, 1.0, 9.0, out src));
            Assert.Equal("playing-clip", src);
            Assert.Equal(9.0, PartParityMotionPlan.ChoosePredicted(PartParityMotionRules.KindClip, double.NaN, 4.0, 1.0, 9.0, out src));
            Assert.Equal("longest-clip", src);
            Assert.Equal(PartParityMotionRules.MaxPredictedSeconds,
                PartParityMotionPlan.ChoosePredicted(PartParityMotionRules.KindServo, 0.0, double.NaN, double.NaN, -1.0, out src));
            Assert.Equal("cap", src);
            Assert.Equal(PartParityMotionRules.MaxPredictedSeconds,
                PartParityMotionPlan.ChoosePredicted(PartParityMotionRules.KindClip, 500.0, 0.0, 0.0, 0.0, out src));
        }

        [Fact]
        public void TheSlowAndLateControls_WriteTheirNormalRouteBesideThemselves()
        {
            Assert.True(PartParityMotionRules.HasNormalRoute(TestCommandPartParitySweep.ControlMotionGhostSlow));
            Assert.True(PartParityMotionRules.HasNormalRoute(TestCommandPartParitySweep.ControlMotionGhostLate));
            Assert.False(PartParityMotionRules.HasNormalRoute(TestCommandPartParitySweep.ControlMotionRealVsReal));
            Assert.False(PartParityMotionRules.HasNormalRoute(null));
            Assert.Equal(TestCommandPartParitySweep.ControlMotionGhostLate + "--normal",
                PartParityMotionRules.NormalRouteCaseId(TestCommandPartParitySweep.ControlMotionGhostLate));
        }

        [Fact]
        public void TheWindowRule_WantsFiveFramesAndARealFrameAtNinetyPercent()
        {
            Assert.Null(PartParityMotionPlan.WindowRuleProblem(new List<double> { 0.0, 0.2, 0.5, 0.8, 0.95 }));
            Assert.Null(PartParityMotionPlan.WindowRuleProblem(new List<double> { 0.0, double.NaN, 0.5, 0.9, 1.0, 1.0 }));
            Assert.Contains("fewer than 5", PartParityMotionPlan.WindowRuleProblem(new List<double> { 0.0, 0.5, 1.0, 1.0 }));
            Assert.Contains("no real frame reached", PartParityMotionPlan.WindowRuleProblem(new List<double> { 0.0, 0.2, 0.5, 0.8, 0.85 }));
            Assert.Contains("best -", PartParityMotionPlan.WindowRuleProblem(new List<double> { double.NaN, double.NaN, double.NaN, double.NaN, double.NaN }));
            Assert.NotNull(PartParityMotionPlan.WindowRuleProblem(null));
        }

        // ----- the controls -----

        private static PartParityMotionTiming T(double lag, double ratio, double shape, double onset = 0.0, double t02 = 0.5, double t98 = 3.0)
            => new PartParityMotionTiming
            {
                LagS = lag, DurationRatio = ratio, CurveShapeDev = shape, OnsetLagS = onset,
                RealCrossings = new[] { t02, 1.0, 1.75, 2.5, t98 },
            };

        private static List<string> Flags(PartParityMotionControlEvidence e)
            => new List<string> { "seed-events:0", PartParityMotionControls.FormatEvidence(e) };

        [Fact]
        public void TheEvidenceFlag_RoundTripsBothRoutes()
        {
            var e = new PartParityMotionControlEvidence
            {
                Normal = T(0.1, 1.0, 0.02, 0.05), Altered = T(1.1, 1.01, double.NaN, 1.05, 0.25, 10.0),
                FrameS = 0.25, PredictedS = 2.0, EndsWithinAllowance = true, LastFramePasses = false,
            };
            string flag = PartParityMotionControls.FormatEvidence(e);
            Assert.StartsWith(PartParityMotionControls.EvidenceFlagPrefix, flag);
            Assert.DoesNotContain(" ", flag);
            PartParityMotionControlEvidence back = PartParityMotionControls.ParseEvidence(Flags(e), "ok");
            Assert.Equal(0.1, back.Normal.LagS);
            Assert.Equal(1.1, back.Altered.LagS);
            Assert.True(double.IsNaN(back.Altered.CurveShapeDev));
            Assert.Equal(10.0 - 0.25, back.Altered.RealDurationS, 9);
            Assert.Equal(0.25, back.FrameS);
            Assert.Equal(2.0, back.PredictedS);
            Assert.True(back.EndsWithinAllowance);
            Assert.False(back.LastFramePasses);
            Assert.Equal("ok", back.SetupStatus);
            Assert.Null(PartParityMotionControls.ParseEvidence(new List<string> { "x" }, "ok"));
            Assert.Null(PartParityMotionControls.FormatEvidence(null));
        }

        [Fact]
        public void TheJudge_DispatchesToTheM1Judges()
        {
            string rvr = TestCommandPartParitySweep.ControlMotionRealVsReal;
            var same = new PartParityMotionControlEvidence { Altered = T(0.01, 1.0, 0.0), FrameS = 0.25, PredictedS = 2.0, EndsWithinAllowance = true };
            Assert.Equal("ok", PartParityMotionControls.Judge(rvr, Flags(same), "ok", out _));
            same.EndsWithinAllowance = false;
            Assert.Equal("bad", PartParityMotionControls.Judge(rvr, Flags(same), "ok", out string d));
            Assert.Contains("end frames", d);

            string slow = TestCommandPartParitySweep.ControlMotionGhostSlow;
            var s = new PartParityMotionControlEvidence { Normal = T(0.0, 1.0, 0.0), Altered = T(1.25, 2.0, 0.2, 0.05), FrameS = 0.25, LastFramePasses = true };
            Assert.Equal("ok", PartParityMotionControls.Judge(slow, Flags(s), "ok", out _));
            s.LastFramePasses = false;
            Assert.Equal("bad", PartParityMotionControls.Judge(slow, Flags(s), "ok", out _));

            // The late control of a 10 s motion: the 1 s shift sits inside the lag line, so the
            // relative requirement is the whole test (H.3, M4).
            string late = TestCommandPartParitySweep.ControlMotionGhostLate;
            var l = new PartParityMotionControlEvidence { Normal = T(0.1, 1.0, 0.0, 0.0, 0.0, 10.0), Altered = T(1.1, 1.0, 0.0, 0.0, 0.0, 10.0), FrameS = 1.25 };
            Assert.Equal("ok", PartParityMotionControls.Judge(late, Flags(l), "ok", out _));

            Assert.Equal("bad", PartParityMotionControls.Judge(late, new List<string>(), "ok", out d));
            Assert.Equal("no motion evidence", d);
            Assert.Equal("bad", PartParityMotionControls.Judge(late, new List<string>(), TestCommandPartParitySweep.SetupCrashed, out d));
            Assert.Contains("setup", d);
            Assert.Equal("bad", PartParityMotionControls.Judge(late, Flags(l), TestCommandPartParitySweep.SetupCrashed, out _));
            Assert.Equal("bad", PartParityMotionControls.Judge("ctl-empty", Flags(l), "ok", out d));
        }

        private static PartParityMotionFrame Frame(params PartParityViewMeasures[] views)
        {
            var f = new PartParityMotionFrame();
            foreach (var m in views)
                f.Views.Add(new PartParityMotionFrameView { View = "v", Same = m == null ? null : new PartParityViewRecord { Measures = m } });
            return f;
        }

        private static PartParityViewMeasures M(int realPx = 100, int realOnly = 0, int ghostOnly = 0, int colorOver = 0,
            int shapeBlob = 0, int colorBlob = 0)
            => new PartParityViewMeasures
            {
                RealPx = realPx, GhostPx = realPx, RealOnlyPx = realOnly, GhostOnlyPx = ghostOnly, ColorOverPx = colorOver,
                RealOnlyBlobPx = shapeBlob, ColorBlobPx = colorBlob,
            };

        [Fact]
        public void TheEndFrames_AreWithinTheRealVsRealAllowance_AsPartparityReadsThem()
        {
            var ok = new List<PartParityMotionFrame> { Frame(M(), M(0)), Frame(M(), M()), Frame(M(), M(colorOver: PartParitySweepPlan.RealVsRealColorNoisePx)) };
            Assert.True(PartParityMotionControls.EndFramesWithinAllowance(ok));
            // An in-between frame is not read.
            ok[1] = Frame(M(ghostOnly: 50));
            Assert.True(PartParityMotionControls.EndFramesWithinAllowance(ok));
            Assert.False(PartParityMotionControls.EndFramesWithinAllowance(
                new List<PartParityMotionFrame> { Frame(M()), Frame(M(), M(realOnly: 1)) }));
            Assert.False(PartParityMotionControls.EndFramesWithinAllowance(
                new List<PartParityMotionFrame> { Frame(M()), Frame(M(colorOver: PartParitySweepPlan.RealVsRealColorNoisePx + 1)) }));
            Assert.False(PartParityMotionControls.EndFramesWithinAllowance(
                new List<PartParityMotionFrame> { Frame(M()), Frame(M(), null) }));
            Assert.False(PartParityMotionControls.EndFramesWithinAllowance(
                new List<PartParityMotionFrame> { Frame(M(0)), Frame(M()) }));
            Assert.False(PartParityMotionControls.EndFramesWithinAllowance(new List<PartParityMotionFrame> { Frame() }));
            Assert.False(PartParityMotionControls.EndFramesWithinAllowance(new List<PartParityMotionFrame>()));
        }

        [Fact]
        public void TheLastFrame_PassesTheEndStateRules_AsJudgeRouteReadsThem()
        {
            var frames = new List<PartParityMotionFrame> { Frame(M(shapeBlob: 999)), Frame(M(), M()) };
            Assert.True(PartParityMotionControls.LastFramePasses(frames, null, null));
            frames.Add(Frame(M(), M(shapeBlob: PartParitySweepPlan.ShapeBlobFailPx + 1)));
            Assert.False(PartParityMotionControls.LastFramePasses(frames, null, null));
            Assert.True(PartParityMotionControls.LastFramePasses(frames, null, new List<string> { PartParityCaseRules.TagContextDependent }));
            frames[2] = Frame(M(colorBlob: PartParitySweepPlan.ColorBlobFailPx + 1));
            Assert.False(PartParityMotionControls.LastFramePasses(frames, null, null));

            frames[2] = Frame(M());
            var structure = new PartParityStructureDiff();
            structure.MissingOnGhost.Add(new PartParityRendererRef { Key = "mask", Rendered = true, NonVisual = PartParityStructure.NonVisualDepthMask });
            structure.ExtraOnGhost.Add(new PartParityRendererRef { Key = "off", Rendered = false });
            Assert.True(PartParityMotionControls.LastFramePasses(frames, structure, null));
            structure.MissingOnGhost.Add(new PartParityRendererRef { Key = "door", Rendered = true });
            Assert.False(PartParityMotionControls.LastFramePasses(frames, structure, new List<string> { PartParityCaseRules.TagContextDependent }));
            Assert.False(PartParityMotionControls.LastFramePasses(new List<PartParityMotionFrame>(), null, null));
        }

        [Fact]
        public void TheSpawnSet_IsTheWaveItself_UnlessAPairAddsItsCopyB_RightAfterItsCase()
        {
            var a = new WaveCase { CaseId = "a" };
            var b = new WaveCase { CaseId = "b", Motion = new PartParityMotionRun() };
            var wave = new List<WaveCase> { a, b };
            Assert.Same(wave, PartParityMotionDriver.SpawnSet(wave));

            var twin = new WaveCase { CaseId = "b-copy" };
            b.Motion.Twin = twin;
            var c = new WaveCase { CaseId = "c" };
            wave.Add(c);
            List<WaveCase> all = PartParityMotionDriver.SpawnSet(wave);
            Assert.NotSame(wave, all);
            Assert.Equal(new[] { "a", "b", "b-copy", "c" }, all.Select(w => w.CaseId).ToArray());
            Assert.Equal(3, wave.Count);
        }

        [Fact]
        public void AMotionWave_IsOneWithAMotionCase_OrAMotionOnlyControl()
        {
            Assert.False(PartParityMotionDriver.IsMotionWave(null));
            Assert.False(PartParityMotionDriver.IsMotionWave(new List<WaveCase> { new WaveCase { CaseId = "x" } }));
            Assert.False(PartParityMotionDriver.IsMotionWave(new List<WaveCase> { new WaveCase { CaseId = "e", ControlId = TestCommandPartParitySweep.ControlEmpty } }));
            Assert.True(PartParityMotionDriver.IsMotionWave(new List<WaveCase> { new WaveCase { CaseId = "m", Motion = new PartParityMotionRun() } }));
            Assert.True(PartParityMotionDriver.IsMotionWave(new List<WaveCase> { new WaveCase { CaseId = "s", ControlId = TestCommandPartParitySweep.ControlMotionGhostSlow } }));
        }
    }
}
