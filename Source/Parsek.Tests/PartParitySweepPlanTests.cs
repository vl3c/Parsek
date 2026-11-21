using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure decisions of the sweep runner (<see cref="PartParitySweepPlan"/>): wave slots,
    /// the settle rule, the control judge (each control's required outcome met and missed),
    /// the missing-mesh target pick and the manifest summary; plus the capture's camera math
    /// (<see cref="PartParityCaptureMath"/>).
    /// </summary>
    public class PartParitySweepPlanTests
    {
        // ----- waves -----

        [Fact]
        public void PlanWave_AlternatesSidesInnerFirst()
        {
            List<double> o = PartParitySweepPlan.PlanWave(Enumerable.Repeat(2.0, 10).ToList());
            Assert.Equal(new[] { 60.0, -60.0, 100.0, -100.0, 140.0, -140.0, 180.0, -180.0 }, o.ToArray());
        }

        [Theory]
        [InlineData("", 0)]
        [InlineData("S", 1)]          // a case owed a solo run flies alone
        [InlineData("SS", 1)]
        [InlineData("SNNN", 1)]
        [InlineData("N", 1)]
        [InlineData("NNNS", 3)]       // a solo case never joins a normal wave
        [InlineData("NSNN", 1)]
        [InlineData("NNNNNNNNNN", 8)] // capped at MaxWaveCases
        public void WaveCandidateCount(string flags, int expected)
        {
            var solo = flags.Select(c => c == 'S').ToList();
            Assert.Equal(expected, PartParitySweepPlan.WaveCandidateCount(solo));
        }

        [Fact]
        public void WaveCandidateCount_NullAndCap()
        {
            Assert.Equal(0, PartParitySweepPlan.WaveCandidateCount(null));
            Assert.Equal(2, PartParitySweepPlan.WaveCandidateCount(new List<bool> { false, false, false }, 2));
            Assert.Equal(0, PartParitySweepPlan.WaveCandidateCount(new List<bool> { true }, 0));
        }

        [Fact]
        public void FormatCleanup_IsInvariantKeyValues()
        {
            Assert.Equal("observerRestored=true casesDestroyed=3 captureReleased=false faults=1",
                PartParitySweepPlan.FormatCleanup(true, 3, false, 1));
            Assert.Equal("observerRestored=false casesDestroyed=0 captureReleased=true faults=0",
                PartParitySweepPlan.FormatCleanup(false, 0, true, 0));
        }

        [Fact]
        public void PlanWave_LargePartTakesTwoSlotsAtTheirMidpoint()
        {
            List<double> o = PartParitySweepPlan.PlanWave(new List<double> { 20.0, 2.0, 20.0, 2.0, 2.0 });
            Assert.Equal(new[] { 80.0, -60.0, 160.0, -100.0, -140.0 }, o.ToArray());
        }

        [Fact]
        public void PlanWave_StopsWhenNoSideHasRoom_ButAlwaysTakesOne()
        {
            List<double> o = PartParitySweepPlan.PlanWave(new List<double> { 20.0, 20.0, 20.0, 20.0, 20.0 });
            Assert.Equal(4, o.Count);
            foreach (double d in o) Assert.True(System.Math.Abs(d) <= 180.0);
            Assert.Single(PartParitySweepPlan.PlanWave(new List<double> { 500.0 }));
            Assert.Empty(PartParitySweepPlan.PlanWave(new List<double>()));
        }

        [Fact]
        public void PlanWave_EverySlotSitsInsideTheUnpackRange()
        {
            foreach (double s in PartParitySweepPlan.SlotPositionsM) Assert.InRange(s, 1.0, 199.0);
        }

        // ----- settle -----

        [Theory]
        [InlineData(0.2, false, "Wait")]
        [InlineData(0.5, false, "Settled")]
        [InlineData(2.0, true, "Wait")]
        [InlineData(5.0, true, "StillAnimating")]
        [InlineData(5.0, false, "Settled")]
        public void DecideSettle(double since, bool animating, string expected)
        {
            Assert.Equal(expected, PartParitySweepPlan.DecideSettle(since, animating).ToString());
        }

        [Fact]
        public void UnloadEveryTenthWave()
        {
            Assert.False(PartParitySweepPlan.ShouldUnloadAfterWave(0));
            Assert.False(PartParitySweepPlan.ShouldUnloadAfterWave(9));
            Assert.True(PartParitySweepPlan.ShouldUnloadAfterWave(10));
            Assert.True(PartParitySweepPlan.ShouldUnloadAfterWave(20));
        }

        // ----- controls -----

        private static PartParityViewRecord View(string name, int realPx = 1000, int ghostPx = 1000,
            int realOnly = 0, int ghostOnly = 0, int realBlob = 0, int ghostBlob = 0, int colorOver = 0,
            int colorBlob = 0, double iou = 1.0)
        {
            return new PartParityViewRecord
            {
                View = name,
                Measures = new PartParityViewMeasures
                {
                    RealPx = realPx, GhostPx = ghostPx, OrPx = System.Math.Max(realPx, ghostPx),
                    RealOnlyPx = realOnly, GhostOnlyPx = ghostOnly,
                    RealOnlyBlobPx = realBlob, GhostOnlyBlobPx = ghostBlob,
                    ColorOverPx = colorOver, ColorBlobPx = colorBlob, Iou = iou,
                },
            };
        }

        private static List<PartParityViewRecord> Four(System.Func<string, PartParityViewRecord> make)
            => TestCommandPartParitySweep.RequiredViews.Select(make).ToList();

        private static string Judge(string id, List<PartParityViewRecord> views, string status = "ok")
            => PartParitySweepPlan.JudgeControl(id, views, status, out string _);

        [Fact]
        public void Control_Empty()
        {
            Assert.Equal("ok", Judge(TestCommandPartParitySweep.ControlEmpty, Four(v => View(v, 0, 0))));
            var leak = Four(v => View(v, 0, 0));
            leak[2] = View("top", 0, 3);
            Assert.Equal("bad", Judge(TestCommandPartParitySweep.ControlEmpty, leak));
        }

        [Fact]
        public void Control_GhostAbsent()
        {
            Assert.Equal("ok", Judge(TestCommandPartParitySweep.ControlGhostAbsent, Four(v => View(v, 1000, 0))));
            Assert.Equal("bad", Judge(TestCommandPartParitySweep.ControlGhostAbsent, Four(v => View(v, 1000, 5))));
            Assert.Equal("bad", Judge(TestCommandPartParitySweep.ControlGhostAbsent, Four(v => View(v, 0, 0))));
        }

        [Fact]
        public void Control_RealVsReal()
        {
            Assert.Equal("ok", Judge(TestCommandPartParitySweep.ControlRealVsReal, Four(v => View(v))));
            Assert.Equal("bad", Judge(TestCommandPartParitySweep.ControlRealVsReal, Four(v => View(v, colorOver: 1))));
            Assert.Equal("bad", Judge(TestCommandPartParitySweep.ControlRealVsReal, Four(v => View(v, realOnly: 1))));
        }

        [Fact]
        public void Control_Scale90_NeedsEveryViewToFailOnShape()
        {
            Assert.Equal("ok", Judge(TestCommandPartParitySweep.ControlGhostScale90, Four(v => View(v, realBlob: 400))));
            var one = Four(v => View(v, realBlob: 400));
            one[3] = View("bottom", realBlob: 10);
            Assert.Equal("bad", Judge(TestCommandPartParitySweep.ControlGhostScale90, one));
        }

        [Fact]
        public void Control_Rot90_NeedsBothSideViews()
        {
            var views = Four(v => View(v, realBlob: v.StartsWith("side") ? 300 : 0));
            Assert.Equal("ok", Judge(TestCommandPartParitySweep.ControlGhostRot90, views));
            views[1] = View("side-b", realBlob: 2);
            Assert.Equal("bad", Judge(TestCommandPartParitySweep.ControlGhostRot90, views));
            Assert.Equal("bad", Judge(TestCommandPartParitySweep.ControlGhostRot90, new List<PartParityViewRecord> { View("side-a", realBlob: 300) }));
        }

        [Fact]
        public void Control_Flip_ShapeOrColourInEveryView()
        {
            var views = Four(v => v == "top" ? View(v, colorBlob: 500) : View(v, ghostBlob: 500));
            Assert.Equal("ok", Judge(TestCommandPartParitySweep.ControlGhostFlip, views));
            views[2] = View("top");
            Assert.Equal("bad", Judge(TestCommandPartParitySweep.ControlGhostFlip, views));
        }

        [Fact]
        public void Control_MissingMesh_LocalFailWithHighIou()
        {
            var views = Four(v => View(v, iou: 0.995));
            views[0] = View("side-a", realBlob: 200, iou: 0.993);
            Assert.Equal("ok", Judge(TestCommandPartParitySweep.ControlGhostMissingMesh, views));
            views[0] = View("side-a", realBlob: 200, iou: 0.95);
            Assert.Equal("bad", Judge(TestCommandPartParitySweep.ControlGhostMissingMesh, views));
            Assert.Equal("bad", Judge(TestCommandPartParitySweep.ControlGhostMissingMesh, Four(v => View(v))));
        }

        [Fact]
        public void Control_WrongSkin_ColourFailWithHighIou()
        {
            var views = Four(v => View(v, colorBlob: 5000, iou: 0.999));
            Assert.Equal("ok", Judge(TestCommandPartParitySweep.ControlWrongSkin, views));
            Assert.Equal("bad", Judge(TestCommandPartParitySweep.ControlWrongSkin, Four(v => View(v, iou: 0.999))));
            Assert.Equal("bad", Judge(TestCommandPartParitySweep.ControlWrongSkin, Four(v => View(v, colorBlob: 5000, iou: 0.9))));
        }

        [Fact]
        public void Control_SetupFaultOrNoViewsIsBad()
        {
            Assert.Equal("bad", Judge(TestCommandPartParitySweep.ControlEmpty, Four(v => View(v, 0, 0)), "spawn-timeout"));
            Assert.Equal("ok", Judge(TestCommandPartParitySweep.ControlEmpty, Four(v => View(v, 0, 0)), "still-animating"));
            Assert.Equal("bad", Judge(TestCommandPartParitySweep.ControlEmpty, new List<PartParityViewRecord>()));
            Assert.Equal("bad", Judge("ctl-unknown", Four(v => View(v))));
        }

        [Fact]
        public void EveryControl_HasASubjectAndARequiredOutcome()
        {
            foreach (string id in TestCommandPartParitySweep.ControlIds)
            {
                Assert.False(string.IsNullOrEmpty(PartParitySweepPlan.ControlSubject(id)), id);
                Assert.False(string.IsNullOrEmpty(PartParitySweepPlan.ControlRequired(id)), id);
                Assert.True(PartParitySweepPlan.IsControl(id));
                Assert.True(TestCommandCaptureScreenshot.IsValidLabel(id), id); // a control id is its own case id
            }
            Assert.False(PartParitySweepPlan.IsControl("fueltank--default--00000000"));
        }

        [Fact]
        public void MissingMeshTarget_SmallestAboveTheFloor()
        {
            var diag = new List<double> { 2.0, 0.01, double.NaN, 0.3, 0.5 };
            Assert.Equal(3, PartParitySweepPlan.ChooseMissingMeshTarget(diag, 3.0)); // floor 0.12
            Assert.Equal(-1, PartParitySweepPlan.ChooseMissingMeshTarget(new List<double> { 0.01 }, 3.0));
        }

        // ----- summaries -----

        [Fact]
        public void Summarize_TakesTheWorstViewAndCountsRenderedOnly()
        {
            var s = new PartParityCaseSidecar
            {
                CaseId = "x--default--00000000", Part = "x", Setup = new PartParitySetup { Status = "ok" },
                Structure = new PartParityStructureDiff(),
            };
            s.Views.Add(View("side-a", realBlob: 10, ghostBlob: 40, colorBlob: 3, iou: 0.97));
            s.Views.Add(View("top", realBlob: 70, colorBlob: 9, iou: 0.99));
            s.Structure.MissingOnGhost.Add(new PartParityRendererRef { Key = "a", Rendered = true });
            s.Structure.MissingOnGhost.Add(new PartParityRendererRef { Key = "b", Rendered = false });
            PartParityCaseSummary sum = PartParitySweepPlan.Summarize(7, s);
            Assert.Equal(7, sum.Index);
            Assert.Equal(70, sum.MaxShapeBlobPx);
            Assert.Equal(9, sum.MaxColorBlobPx);
            Assert.Equal(0.97, sum.MinIou);
            Assert.Equal(1, sum.MissingRendered);
            Assert.Equal(0, sum.ExtraRendered);
            Assert.Equal("x--default--00000000.part.json", sum.Sidecar);
        }

        [Fact]
        public void Summarize_NoViewsLeavesMeasuresNull()
        {
            var s = new PartParityCaseSidecar { CaseId = "c", Setup = new PartParitySetup { Status = "spawn-timeout" } };
            PartParityCaseSummary sum = PartParitySweepPlan.Summarize(0, s);
            Assert.Null(sum.MaxShapeBlobPx);
            Assert.Null(sum.MinIou);
            Assert.Null(sum.MissingRendered);
        }

        [Fact]
        public void NextIncomplete()
        {
            Assert.Equal(1, PartParitySweepPlan.NextIncomplete(new List<bool> { true, false, true }));
            Assert.Equal(2, PartParitySweepPlan.NextIncomplete(new List<bool> { true, true }));
            Assert.Equal(0, PartParitySweepPlan.NextIncomplete(new List<bool>()));
        }

        // ----- the step line (the lane's log contract) -----

        [Fact]
        public void StepLine_CarriesTheLiteralForRunAndReset()
        {
            var run = new PartParitySweepArgs { Label = "pp1", Op = PartParitySweepOp.Run };
            var ok = new PartParitySweepStepResult
            {
                Ok = true,
                Payload = TestCommandPartParitySweep.BuildPayload("pp1", false, 3, 10, 3, 0, 1, "ok", 12.34),
            };
            Assert.Equal("PartParity step label=pp1 op=run ok=true label=pp1 done=false next=3 cases=10 complete=3 " +
                "crashed=0 setupFaults=1 controls=ok wall=12.3", PartParitySweepRunner.StepSummaryLine(run, ok));

            var reset = new PartParitySweepArgs { Label = "pp1", Op = PartParitySweepOp.Reset };
            var refused = new PartParitySweepStepResult { Ok = false, Reason = "host-not-orbiting", Detail = "situation=LANDED" };
            string line = PartParitySweepRunner.StepSummaryLine(reset, refused);
            Assert.StartsWith(PartParitySweepRunner.StepLineLiteral + " label=pp1 op=reset ok=false", line);
            Assert.Contains("reason=host-not-orbiting detail=situation=LANDED", line);
            Assert.Equal("PartParity step", PartParitySweepRunner.StepLineLiteral);
        }

        // ----- capture math -----

        [Fact]
        public void OrthoSize_IsTheLargestHalfExtentPlusTenPercent()
        {
            Assert.Equal(2.2, PartParityCaptureMath.OrthoSize(new[] { 1.0, 2.0, 0.5 }), 9);
            Assert.Equal(0.5, PartParityCaptureMath.OrthoSize(new[] { 0.0, 0.0, 0.0 }));
        }

        [Fact]
        public void ClipPlanes_WrapTheDepthRangeWithAMargin()
        {
            PartParityCaptureMath.ClipPlanes(2.0, 6.0, 2.0, out double near, out double far);
            Assert.Equal(1.85, near, 9);
            Assert.Equal(6.15, far, 9);
            PartParityCaptureMath.ClipPlanes(0.0, 1.0, 1.0, out near, out _);
            Assert.Equal(0.01, near);
        }

        [Fact]
        public void ProjectedArea_ScalesWithPixelsPerMetre()
        {
            // a 2 x 1 m face under ortho 1.1 in a 512 px tile: (512 / 2.2) px per metre.
            double ppm = 512.0 / 2.2;
            Assert.Equal(2.0 * 1.0 * ppm * ppm, PartParityCaptureMath.ProjectedAreaPx(1.0, 0.5, 1.1, 512), 6);
            Assert.Equal(0.0, PartParityCaptureMath.ProjectedAreaPx(1.0, 1.0, 0.0, 512));
        }

        [Fact]
        public void HeroDistance_FitsTheSphereInTheFieldOfView()
        {
            double d = PartParityCaptureMath.HeroDistance(1.0);
            Assert.True(1.0 * PartParityCaptureMath.OrthoMargin / d <= System.Math.Sin(15.0 * System.Math.PI / 180.0) + 1e-9);
        }
    }
}
