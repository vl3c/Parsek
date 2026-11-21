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

        // ----- the missing-mesh pick -----

        private static PartParityRendererRecord Rec(string path, double[] min, double[] max, bool rendered = true)
            => new PartParityRendererRecord { Path = path, Rendered = rendered, BoundsMin = min, BoundsMax = max };

        // A 2 x 2 x 2 m body (projected 4 m2 in every axis view) plus small parts.
        private static PartParityRendererRecord Body()
            => Rec("model/body", new[] { -1.0, -1.0, -1.0 }, new[] { 1.0, 1.0, 1.0 });

        private static PartParityRendererRecord Cube(string path, double side, double at = 0.5)
            => Rec(path, new[] { at, at, at }, new[] { at + side, at + side, at + side });

        private static readonly string[] Required = TestCommandPartParitySweep.RequiredViews;

        [Fact]
        public void ProjectedBoxArea_IsTheFaceAreasSeenAtTheirCosines()
        {
            Assert.Equal(6.0, PartParitySweepPlan.ProjectedBoxArea(new[] { 1.0, 2.0, 3.0 }, new[] { 1.0, 0.0, 0.0 }), 9);
            Assert.Equal(3.0, PartParitySweepPlan.ProjectedBoxArea(new[] { 1.0, 2.0, 3.0 }, new[] { 0.0, -1.0, 0.0 }), 9);
            Assert.Equal(2.0, PartParitySweepPlan.ProjectedBoxArea(new[] { 1.0, 2.0, 3.0 }, new[] { 0.0, 0.0, -1.0 }), 9);
            double h = System.Math.Sqrt(0.5);
            Assert.Equal(h * 6.0 + h * 2.0, PartParitySweepPlan.ProjectedBoxArea(new[] { 1.0, 2.0, 3.0 }, new[] { h, 0.0, h }), 9);
            Assert.Equal(0.0, PartParitySweepPlan.ProjectedBoxArea(null, new[] { 1.0, 0.0, 0.0 }));
        }

        [Fact]
        public void ViewDirections_MatchTheCaptureCameraTable()
        {
            foreach (string v in TestCommandPartParitySweep.AllViews)
            {
                Assert.True(PartParitySweepPlan.TryViewDirection(v, out double[] d), v);
                Assert.True(PartParityCapture.TryViewAxes(v, out UnityEngine.Vector3 cam, out UnityEngine.Vector3 _, out bool _), v);
                Assert.Equal(cam.x, d[0], 5);
                Assert.Equal(cam.y, d[1], 5);
                Assert.Equal(cam.z, d[2], 5);
            }
            Assert.False(PartParitySweepPlan.TryViewDirection("nope", out double[] _));
        }

        [Fact]
        public void EstimateFractions_DivideByTheSumOfProjections_NotTheBox()
        {
            // A 2 m body plus a 0.2 m cube: the sum of projections is 4 + 0.04 m2 per view, so
            // the cube is 0.04 / 4.04 of it (about 0.99 %), not 1 % of the box.
            var records = new List<PartParityRendererRecord> { Body(), Cube("c", 0.2) };
            double[] f = PartParitySweepPlan.EstimateFractions(records[1], records, Required);
            Assert.Equal(4, f.Length);
            foreach (double x in f) Assert.Equal(0.04 / 4.04, x, 9);

            // A thin plate (2 x 0.02 x 2 m) with a 0.1 m box on it: from the top the box
            // covers 0.01 / 4.01 of what is drawn; seen edge-on (side-a) the plate is 0.04 m2,
            // so the box is 0.01 / 0.05 = 20 %. Against the 2 m box it would read 0.25 %.
            var plate = Rec("plate", new[] { -1.0, -0.01, -1.0 }, new[] { 1.0, 0.01, 1.0 });
            var thin = new List<PartParityRendererRecord> { plate, Cube("knob", 0.1, 0.0) };
            double[] t = PartParitySweepPlan.EstimateFractions(thin[1], thin, Required);
            Assert.Equal(0.01 / 0.05, t[0], 9);
            Assert.Equal(0.01 / 4.01, t[2], 9);

            // A hidden renderer is not in the sum; no bounds or no direction reads null / NaN.
            records.Add(Cube("hidden", 1.0, 0.0));
            records[2].Rendered = false;
            Assert.Equal(0.04 / 4.04, PartParitySweepPlan.EstimateFractions(records[1], records, Required)[0], 9);
            Assert.Null(PartParitySweepPlan.EstimateFractions(Rec("nobounds", null, null), records, Required));
            Assert.True(double.IsNaN(PartParitySweepPlan.EstimateFractions(records[1], records, new[] { "nope" })[0]));
        }

        [Theory]
        [InlineData(new[] { 0.002, 0.0005, 0.0005, 0.0005 }, true)]  // floor met in one view
        [InlineData(new[] { 0.0009, 0.0009, 0.0009, 0.0009 }, false)] // floor met nowhere
        [InlineData(new[] { 0.002, 0.007, 0.001, 0.001 }, false)]     // ceiling broken in one view
        [InlineData(new[] { 0.006, 0.006, 0.001, 0.001 }, true)]      // both bounds inclusive
        public void FractionsQualify_TheWindow(double[] f, bool expected)
        {
            Assert.Equal(expected, PartParitySweepPlan.FractionsQualify(f));
        }

        [Fact]
        public void FractionsQualify_NaNOrEmptyDisqualifies()
        {
            Assert.False(PartParitySweepPlan.FractionsQualify(null));
            Assert.False(PartParitySweepPlan.FractionsQualify(new double[0]));
            Assert.False(PartParitySweepPlan.FractionsQualify(new[] { 0.002, double.NaN, 0.001, 0.001 }));
        }

        [Theory]
        [InlineData(false, "", true, null)]
        [InlineData(false, "ModuleCommand,ModuleColorChanger,ModuleReactionWheel", true, null)]
        [InlineData(true, "ModuleCommand", false, "Animation component")]
        [InlineData(false, "ModuleCommand,ModuleAnimateGeneric", false, "ModuleAnimateGeneric")]
        [InlineData(false, "RetractableLadder", false, "RetractableLadder")]
        [InlineData(false, "ModuleAnimationGroup", false, "ModuleAnimationGroup")]
        [InlineData(false, "ModuleDeployableSolarPanel", false, "ModuleDeployableSolarPanel")]
        [InlineData(false, "ModuleDeployableAntenna", false, "ModuleDeployableAntenna")]
        [InlineData(false, "ModuleAnimateHeat", false, "ModuleAnimateHeat")]
        [InlineData(false, "FXModuleAnimateThrottle", false, "FXModuleAnimateThrottle")]
        [InlineData(false, "ModuleGimbal", false, "ModuleGimbal")]
        [InlineData(false, "ModuleRoboticServoHinge", false, "ModuleRoboticServoHinge")]
        [InlineData(false, "ModuleWheelBase", false, "ModuleWheelBase")]
        [InlineData(false, "ModuleLight", false, "ModuleLight")]
        public void IsStillSubject(bool hasAnimation, string modules, bool expected, string why)
        {
            var names = modules.Length == 0 ? new List<string>() : modules.Split(',').ToList();
            Assert.Equal(expected, PartParitySweepPlan.IsStillSubject(hasAnimation, names, out string reason));
            Assert.Equal(why, reason);
        }

        private static PartParityMissingMeshCandidate Candidate(string subject, bool animation,
            string module, params PartParityRendererRecord[] records)
        {
            var c = new PartParityMissingMeshCandidate { Subject = subject, HasAnimation = animation };
            if (module != null) c.ModuleNames.Add(module);
            c.Records.AddRange(records);
            return c;
        }

        [Fact]
        public void OrderSubjects_StillOnly_EstimateQualifiedFirst()
        {
            var engine = Candidate("liquidEngine2", true, "ModuleGimbal", Body(), Cube("small", 0.15));
            var ladder = Candidate("telescopicLadder", false, "RetractableLadder", Body(), Cube("rung", 0.15));
            var bellOnly = Candidate("mk1pod.v2", false, null, Body(), Cube("bell", 0.5));
            var hex = Candidate("probeCoreHex.v2", false, null, Body(), Cube("antenna", 0.15));

            List<PartParityMissingMeshSubject> order = PartParitySweepPlan.OrderMissingMeshSubjects(
                new List<PartParityMissingMeshCandidate> { engine, ladder, bellOnly, hex }, Required);
            Assert.Equal(new[] { "probeCoreHex.v2", "mk1pod.v2" }, order.Select(o => o.Subject).ToArray());
            Assert.True(order[0].EstimateQualifies);
            Assert.Equal("antenna", order[0].EstimateRendererPath);
            Assert.False(order[1].EstimateQualifies);
            Assert.Equal("subject=mk1pod.v2;estimate=none", order[1].Describe(Required));
            Assert.StartsWith("subject=probeCoreHex.v2;estimate=antenna;side-a=", order[0].Describe(Required));
        }

        [Fact]
        public void OrderSubjects_AnimatedOnly_IsNoSubject()
        {
            // No animated fallback: an animated-only list orders nothing (the control is then
            // bad / no-subject), however well its renderers fit the window.
            var engine = Candidate("liquidEngine2", true, null, Body(), Cube("small", 0.15));
            var ladder = Candidate("telescopicLadder", false, "RetractableLadder", Body(), Cube("rung", 0.15));
            var panel = Candidate("solarPanels5", false, "ModuleDeployableSolarPanel", Body(), Cube("cell", 0.15));
            Assert.Empty(PartParitySweepPlan.OrderMissingMeshSubjects(
                new List<PartParityMissingMeshCandidate> { engine, ladder, panel }, Required));
            Assert.Empty(PartParitySweepPlan.OrderMissingMeshSubjects(null, Required));
        }

        [Fact]
        public void ContributionPx_IsFullMinusWithout_NeverNegative()
        {
            Assert.Equal(new[] { 120, 0, 0, 5 }, PartParitySweepPlan.ContributionPx(
                new[] { 40000, 30000, 50000, 100 }, new[] { 39880, 30000, 50010, 95 }));
            Assert.Null(PartParitySweepPlan.ContributionPx(new[] { 1, 2 }, new[] { 1 }));
            Assert.Null(PartParitySweepPlan.ContributionPx(null, new[] { 1 }));
        }

        [Fact]
        public void ContributionPick_FirstInTheWindow_ByCountsOnly()
        {
            int[] full = { 40000, 30000, 50000, 50000 };
            var contribution = new List<int[]>
            {
                new[] { 38000, 29000, 49000, 49000 }, // the body: far above 0.6 %
                new[] { 0, 0, 0, 0 },                 // a decal on the hull: removing it changes nothing
                new[] { 99, 90, 80, 70 },             // under 100 px in every view
                new[] { 120, 250, 0, 0 },             // 0.83 % of side-b: over the ceiling
                new[] { 150, 100, 300, 0 },           // in the window (300 = 0.6 % of 50000)
                new[] { 200, 100, 100, 100 },         // also in, later
            };
            Assert.Equal(4, PartParitySweepPlan.ChooseContributionRenderer(contribution, full));
            contribution.RemoveAt(4);
            Assert.Equal(4, PartParitySweepPlan.ChooseContributionRenderer(contribution, full));
            Assert.Equal(-1, PartParitySweepPlan.ChooseContributionRenderer(contribution.GetRange(0, 4), full));

            // The 2026-11-21 mk1pod flag: 331 / 283 px drawn alone, but lying on the hull its
            // contribution is 0 in every view, so it is never picked.
            var podFull = new[] { 147000, 147000, 120000, 120000 };
            var flagSolo = new[] { 0, 331, 283, 0 };
            var flagContribution = PartParitySweepPlan.ContributionPx(podFull, podFull);
            Assert.Equal(0, PartParitySweepPlan.ChooseContributionRenderer(new List<int[]> { flagSolo }, podFull));
            Assert.Equal(-1, PartParitySweepPlan.ChooseContributionRenderer(new List<int[]> { flagContribution }, podFull));

            // A view the ghost does not cover at all allows only a renderer that adds nothing there.
            Assert.Equal(-1, PartParitySweepPlan.ChooseContributionRenderer(
                new List<int[]> { new[] { 150, 0, 0, 1 } }, new[] { 40000, 30000, 50000, 0 }));
            Assert.Equal(0, PartParitySweepPlan.ChooseContributionRenderer(
                new List<int[]> { new[] { 150, 0, 0, 0 } }, new[] { 40000, 30000, 50000, 0 }));
            Assert.Equal(-1, PartParitySweepPlan.ChooseContributionRenderer(null, full));
            Assert.Equal(-1, PartParitySweepPlan.ChooseContributionRenderer(
                new List<int[]> { new[] { 150, 100 } }, full)); // wrong view count
        }

        // ----- the missing-mesh family -----

        [Theory]
        [InlineData("mk1pod.v2", "ctl-ghost-missing-mesh--mk1pod.v2")]
        [InlineData("Mark1Cockpit", "ctl-ghost-missing-mesh--mark1cockpit")]
        [InlineData("mk1-3pod", "ctl-ghost-missing-mesh--mk1-3pod")]
        [InlineData("odd name/x", "ctl-ghost-missing-mesh--odd-name-x")]
        [InlineData("trail.", "ctl-ghost-missing-mesh--trail-")]
        [InlineData("", "ctl-ghost-missing-mesh--x")]
        public void SubCaseId_IsAValidLabelUnderTheControlPrefix(string subject, string expected)
        {
            string id = PartParitySweepPlan.MissingMeshSubCaseId(subject);
            Assert.Equal(expected, id);
            Assert.True(TestCommandCaptureScreenshot.IsValidLabel(id), id);
            Assert.True(PartParitySweepPlan.IsMissingMeshSubCase(id));
            Assert.False(PartParitySweepPlan.IsControl(id));     // not a control of its own
            Assert.False(TestCommandPartParitySweep.ControlIds.Contains(id));
        }

        [Fact]
        public void SubCaseId_LongSubjectIsCut_AndPartsAreNotSubCases()
        {
            string id = PartParitySweepPlan.MissingMeshSubCaseId(new string('a', 200));
            Assert.Equal(TestCommandPartParitySweep.MaxCaseIdLength, id.Length);
            Assert.True(TestCommandCaptureScreenshot.IsValidLabel(id));
            Assert.False(PartParitySweepPlan.IsMissingMeshSubCase(TestCommandPartParitySweep.ControlGhostMissingMesh));
            Assert.False(PartParitySweepPlan.IsMissingMeshSubCase("mk1pod.v2--default--01234567"));
            Assert.False(PartParitySweepPlan.IsMissingMeshSubCase(null));
        }

        [Fact]
        public void Family_TakesTheFirstEightStillSubjects()
        {
            var order = new List<PartParityMissingMeshSubject>();
            for (int i = 0; i < 11; i++) order.Add(new PartParityMissingMeshSubject { Subject = "part" + i });
            order.Insert(2, new PartParityMissingMeshSubject { Subject = "PART0" }); // same id ignoring case
            List<PartParityMissingMeshSubject> family = PartParitySweepPlan.MissingMeshFamily(order);
            Assert.Equal(PartParitySweepPlan.MaxMissingMeshSubCases, family.Count);
            Assert.Equal("ctl-ghost-missing-mesh--part0", family[0].SubCaseId);
            Assert.Equal("part1", family[1].Subject);
            Assert.Equal("part2", family[2].Subject);
            Assert.Empty(PartParitySweepPlan.MissingMeshFamily(null));
        }

        private static PartParityCaseSidecar Sub(string part, bool found, string status = "ok",
            List<PartParityViewRecord> views = null)
        {
            var s = new PartParityCaseSidecar { CaseId = PartParitySweepPlan.MissingMeshSubCaseId(part), Part = part };
            s.Setup = new PartParitySetup { Status = status };
            if (found) s.Flags.Add(PartParitySweepPlan.FlagControlPickPrefix + "subject=" + part + ";renderer=r");
            else if (status == "ok") s.Flags.Add(PartParitySweepPlan.FlagControlNoSubject);
            if (views != null) s.Views.AddRange(views);
            return s;
        }

        private static List<PartParityViewRecord> GoodMissingMeshViews()
        {
            var views = Four(v => View(v, iou: 0.995));
            views[0] = View("side-a", realBlob: 200, iou: 0.993);
            return views;
        }

        private static string Resolve(IList<PartParityCaseSidecar> known, IList<string> ids,
            out PartParityCaseSidecar chosen, out string detail)
        {
            var byId = known.ToDictionary(k => k.CaseId);
            return PartParitySweepPlan.ResolveMissingMeshFamily(ids,
                id => byId.TryGetValue(id, out var s) ? s : null, out chosen, out detail);
        }

        [Fact]
        public void Family_OutcomeIsTheFirstSubCaseThatFoundARenderer()
        {
            var pod = Sub("mk1pod.v2", false);
            var lander = Sub("landerCabinSmall", false, "spawn-timeout"); // a setup fault never measured
            var hex = Sub("probeCoreHex.v2", true, "still-animating", GoodMissingMeshViews());
            var octo = Sub("probeCoreOcto.v2", true, "ok", Four(v => View(v)));   // would judge bad
            var known = new List<PartParityCaseSidecar> { pod, lander, hex, octo };
            var ids = known.Select(k => k.CaseId).ToList();

            Assert.Equal("ok", Resolve(known, ids, out var chosen, out string detail));
            Assert.Same(hex, chosen);
            Assert.Equal("sub=ctl-ghost-missing-mesh--probecorehex.v2", detail);

            // The first finder decides even when its outcome is bad; a later ok one does not rescue it.
            ids = new List<string> { pod.CaseId, octo.CaseId, hex.CaseId };
            Assert.Equal("bad", Resolve(known, ids, out chosen, out detail));
            Assert.Same(octo, chosen);
            Assert.Equal("sub=ctl-ghost-missing-mesh--probecoreocto.v2 no view fails", detail);
        }

        [Fact]
        public void Family_PendingUntilEarlierSubCasesHaveRun()
        {
            var hex = Sub("probeCoreHex.v2", true, "ok", GoodMissingMeshViews());
            var ids = new List<string> { PartParitySweepPlan.MissingMeshSubCaseId("mk1pod.v2"), hex.CaseId };
            Assert.Equal("pending", Resolve(new List<PartParityCaseSidecar> { hex }, ids, out var chosen, out string detail));
            Assert.Null(chosen);
            Assert.Equal("waiting for ctl-ghost-missing-mesh--mk1pod.v2", detail);
            // Once the first finder has run, later sub-cases need not have.
            ids = new List<string> { hex.CaseId, PartParitySweepPlan.MissingMeshSubCaseId("mk1pod.v2") };
            Assert.Equal("ok", Resolve(new List<PartParityCaseSidecar> { hex }, ids, out chosen, out detail));
        }

        [Fact]
        public void Family_NoSubCaseFound_OrEmpty_IsBadNoSubject()
        {
            var known = new List<PartParityCaseSidecar>
            {
                Sub("mk1pod.v2", false), Sub("landerCabinSmall", false), Sub("ladder1", false, "crashed"),
            };
            Assert.Equal("bad", Resolve(known, known.Select(k => k.CaseId).ToList(), out var chosen, out string detail));
            Assert.Null(chosen);
            Assert.Equal("no-subject", detail);

            Assert.Equal("bad", Resolve(known, new List<string>(), out chosen, out detail));
            Assert.Equal("no-subject", detail);
            Assert.Equal("bad", PartParitySweepPlan.ResolveMissingMeshFamily(null, null, out chosen, out detail));
            Assert.Equal("no-subject", detail);
        }

        [Fact]
        public void MeasuredLines_AreInvariant()
        {
            Assert.Equal("full=side-a:40000,side-b:30000,top:50000,bottom:50000 | model/a side-a:150,side-b:100,top:300,bottom:0",
                PartParitySweepPlan.FormatMeasuredCounts(Required, new[] { 40000, 30000, 50000, 50000 },
                    new[] { "model/a" }, new List<int[]> { new[] { 150, 100, 300, 0 } }));
            Assert.Equal("subject=mk1pod.v2;renderer=model/a;side-a=150/40000;side-b=100/30000;top=300/50000;bottom=0/50000",
                PartParitySweepPlan.FormatMeasuredPick("mk1pod.v2", "model/a", Required,
                    new[] { 150, 100, 300, 0 }, new[] { 40000, 30000, 50000, 50000 }, null));
            Assert.Equal("subject=mk1pod.v2;none=no ghost renderer in the size window",
                PartParitySweepPlan.FormatMeasuredPick("mk1pod.v2", null, Required, null, null,
                    "no ghost renderer in the size window"));
        }

        [Fact]
        public void CoveragePx_CountsAlphaAtOrOverHalf()
        {
            var rgba = new byte[] { 0, 0, 0, 127, 9, 9, 9, 128, 1, 1, 1, 255, 5, 5, 5, 0 };
            Assert.Equal(2, PartParityMetrics.CoveragePx(rgba));
            Assert.Equal(0, PartParityMetrics.CoveragePx(null));
        }

        [Fact]
        public void MissingMeshCandidates_AreDistinctDotFormNames()
        {
            var seen = new HashSet<string>();
            foreach (string n in PartParitySweepPlan.MissingMeshCandidates)
            {
                Assert.True(seen.Add(n), n);
                Assert.DoesNotContain("_", n); // runtime names are dot-form
            }
            Assert.True(PartParitySweepPlan.MissingMeshMinViewFraction < PartParitySweepPlan.MissingMeshMaxViewFraction);
            Assert.Equal(100, PartParitySweepPlan.MissingMeshMinContributionPx);
            Assert.Equal(0.006, PartParitySweepPlan.MissingMeshMaxContributionFraction);
        }

        [Fact]
        public void Control_NoSubjectFlag_IsBadWithNoSubjectDetail()
        {
            var views = Four(v => View(v, iou: 0.995));
            views[0] = View("side-a", realBlob: 200, iou: 0.993);
            var flags = new List<string> { PartParitySweepPlan.FlagControlNoSubject };
            Assert.Equal("bad", PartParitySweepPlan.JudgeControl(
                TestCommandPartParitySweep.ControlGhostMissingMesh, views, "spawn-timeout", flags, out string detail));
            Assert.Equal("no-subject", detail);
            // Without the flag the same views judge on their own.
            Assert.Equal("ok", PartParitySweepPlan.JudgeControl(
                TestCommandPartParitySweep.ControlGhostMissingMesh, views, "ok", new List<string> { "control-pick:x" }, out detail));
        }

        [Fact]
        public void Control_StillAnimating_NeverMasksTheViews()
        {
            // still-animating is a tag on a real measurement: the views decide both ways.
            Assert.False(TestCommandPartParitySweep.IsSetupFault(TestCommandPartParitySweep.SetupStillAnimating));
            var good = Four(v => View(v, iou: 0.995));
            good[0] = View("side-a", realBlob: 200, iou: 0.993);
            Assert.Equal("ok", PartParitySweepPlan.JudgeControl(
                TestCommandPartParitySweep.ControlGhostMissingMesh, good, "still-animating", out string _));
            var badIou = Four(v => View(v, iou: 0.995));
            badIou[0] = View("side-a", realBlob: 26484, iou: 0.68);
            Assert.Equal("bad", PartParitySweepPlan.JudgeControl(
                TestCommandPartParitySweep.ControlGhostMissingMesh, badIou, "still-animating", out string detail));
            Assert.Equal("iou below 0.99 in side-a", detail);
            foreach (string id in TestCommandPartParitySweep.ControlIds)
            {
                var anyViews = Four(v => View(v, realBlob: 400, colorBlob: 400, iou: 0.5));
                Assert.Equal(
                    PartParitySweepPlan.JudgeControl(id, anyViews, "ok", out string a),
                    PartParitySweepPlan.JudgeControl(id, anyViews, "still-animating", out string b));
                Assert.Equal(a, b);
            }
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
