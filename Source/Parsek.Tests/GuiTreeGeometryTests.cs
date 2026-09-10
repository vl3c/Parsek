using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit coverage for the pure geometry derivation the live GuiTree cell asserts
    /// against. The cell's load-bearing assertion is "no marked control's screen rect
    /// fell outside its own window", which is the only available evidence that
    /// <c>GUIUtility.GUIToScreenRect</c> converts as assumed inside a window callback.
    /// These cells prove the derivation itself would actually catch that, both ways.
    /// </summary>
    public class GuiTreeGeometryTests
    {
        private const string Title = "Parsek GuiTree Probe";
        private const string Marker = "parsek-probe-";

        private static GuiTreeResult Build(params GuiTreeEvent[] events)
        {
            return GuiTreeAssembler.Assemble(new List<GuiTreeEvent>(events));
        }

        private static GuiTreeEvent Window(float x, float y, float w, float h)
        {
            return new GuiTreeEvent
            {
                Op = GuiTreeOp.Begin,
                Kind = GuiNodeKind.Window,
                Rect = new GuiRect(x, y, w, h),
                LocalRect = new GuiRect(x, y, w, h),
                ClipDepth = 1,
                Text = Title,
            };
        }

        private static GuiTreeEvent Group()
        {
            return new GuiTreeEvent
            {
                Op = GuiTreeOp.Begin,
                Kind = GuiNodeKind.LayoutGroup,
                Rect = new GuiRect(0f, 0f, 0f, 0f),
                ClipDepth = 1,
            };
        }

        private static GuiTreeEvent Control(string text, float x, float y, float w, float h,
            int clipDepth = 1)
        {
            return new GuiTreeEvent
            {
                Op = GuiTreeOp.Leaf,
                Kind = GuiNodeKind.Label,
                Rect = new GuiRect(x, y, w, h),
                LocalRect = new GuiRect(x, y, w, h),
                ClipDepth = clipDepth,
                Text = text,
            };
        }

        [Fact]
        public void NullTreeReportsNoWindowRatherThanThrowing()
        {
            GuiTreeGeometryReport r = GuiTreeGeometry.Inspect(null, Title, Marker);
            Assert.False(r.WindowFound);
            Assert.Equal("<not found>", r.DescribeWindowRect());
        }

        [Fact]
        public void AWrongTitleIsNotTheProbeWindow()
        {
            GuiTreeResult tree = Build(Window(0, 0, 100, 100));
            tree.Roots[0].Text = "Some other mod";
            Assert.False(GuiTreeGeometry.Inspect(tree, Title, Marker).WindowFound);
        }

        [Fact]
        public void ControlsInsideTheWindowAllPass()
        {
            GuiTreeResult tree = Build(
                Window(60, 60, 320, 260),
                Group(),
                Control(Marker + "label", 66, 84, 200, 18),
                Control(Marker + "button", 66, 106, 200, 24),
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.LayoutGroup },
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.Window });

            GuiTreeGeometryReport r = GuiTreeGeometry.Inspect(tree, Title, Marker);

            Assert.True(r.WindowFound);
            Assert.Equal(2, r.ProbeControlsFound);
            Assert.Equal(0, r.ControlsOutsideWindow);
            Assert.Equal(0, r.ControlsWithDegenerateRect);
            Assert.Equal("none", r.FirstOffender);
            Assert.Equal(3, r.MaxDepth);
            Assert.Equal("[60,60,320,260]", r.DescribeWindowRect());
        }

        [Fact]
        public void AControlLeftInWindowLocalSpaceIsCaughtAsOutside()
        {
            // THE failure this derivation exists for: if GUIToScreenRect were a no-op
            // inside a window callback, a control at window-local (6, 24) would be
            // recorded at screen (6, 24) while its window sits at (60, 60).
            GuiTreeResult tree = Build(
                Window(600, 400, 320, 260),
                Control(Marker + "label", 6, 24, 200, 18),
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.Window });

            GuiTreeGeometryReport r = GuiTreeGeometry.Inspect(tree, Title, Marker);

            Assert.Equal(1, r.ProbeControlsFound);
            Assert.Equal(1, r.ControlsOutsideWindow);
            Assert.Contains("outside", r.FirstOffender);
            Assert.Contains("parsek-probe-label", r.FirstOffender);
            Assert.Contains("localRect=[6,24,200,18]", r.FirstOffender);
        }

        [Fact]
        public void AControlOverhangingTheRightEdgeIsCaught()
        {
            GuiTreeResult tree = Build(
                Window(0, 0, 100, 100),
                Control(Marker + "wide", 10, 10, 200, 18),
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.Window });

            Assert.Equal(1, GuiTreeGeometry.Inspect(tree, Title, Marker).ControlsOutsideWindow);
        }

        [Fact]
        public void ABorderWorthOfOverhangIsTolerated()
        {
            // Styles draw slightly outside their rect and the window rect is the frame,
            // not the content area; a few pixels must not read as a conversion defect.
            GuiTreeResult tree = Build(
                Window(0, 0, 100, 100),
                Control(Marker + "edge", -4, -4, 106, 106),
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.Window });

            Assert.Equal(0, GuiTreeGeometry.Inspect(tree, Title, Marker).ControlsOutsideWindow);
        }

        [Fact]
        public void ADegenerateRectIsReportedSeparatelyFromBeingOutside()
        {
            GuiTreeResult tree = Build(
                Window(0, 0, 100, 100),
                Control(Marker + "zero", 10, 10, 0, 0),
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.Window });

            GuiTreeGeometryReport r = GuiTreeGeometry.Inspect(tree, Title, Marker);
            Assert.Equal(1, r.ControlsWithDegenerateRect);
            Assert.Equal(0, r.ControlsOutsideWindow);
            Assert.Contains("degenerate", r.FirstOffender);
        }

        [Fact]
        public void UnmarkedControlsAreIgnoredSoStockChromeCannotFailTheCell()
        {
            GuiTreeResult tree = Build(
                Window(0, 0, 100, 100),
                Control("some-other-mods-label", 5000, 5000, 50, 20),
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.Window });

            GuiTreeGeometryReport r = GuiTreeGeometry.Inspect(tree, Title, Marker);
            Assert.Equal(0, r.ProbeControlsFound);
            Assert.Equal(0, r.ControlsOutsideWindow);
        }

        [Fact]
        public void TheMarkerAlsoMatchesATextFieldsValue()
        {
            var field = new GuiTreeEvent
            {
                Op = GuiTreeOp.Leaf,
                Kind = GuiNodeKind.TextField,
                Rect = new GuiRect(5f, 5f, 80f, 20f),
                ClipDepth = 1,
                TextValue = Marker + "text",
            };
            GuiTreeResult tree = Build(
                Window(0, 0, 100, 100), field,
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.Window });

            Assert.Equal(1, GuiTreeGeometry.Inspect(tree, Title, Marker).ProbeControlsFound);
        }

        [Fact]
        public void TheWindowItselfIsNeverCountedAsOneOfItsOwnControls()
        {
            GuiTreeResult tree = Build(Window(0, 0, 100, 100));
            tree.Roots[0].Text = Marker + Title;

            GuiTreeGeometryReport r = GuiTreeGeometry.Inspect(tree, null, Marker);
            Assert.True(r.WindowFound);
            Assert.Equal(0, r.ProbeControlsFound);
        }

        [Fact]
        public void MaxDepthCountsThroughScrollViewNesting()
        {
            GuiTreeResult tree = Build(
                Window(0, 0, 200, 200),
                Group(),
                new GuiTreeEvent
                {
                    Op = GuiTreeOp.Begin, Kind = GuiNodeKind.ScrollView,
                    Rect = new GuiRect(2f, 2f, 190f, 100f), ClipDepth = 2,
                },
                // clipDepth 2: inside the scroll view's own clip, which is
                // what the recorder would measure there.
                Control(Marker + "row", 4, 4, 100, 18, 2),
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.ScrollView },
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.LayoutGroup },
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.Window });

            GuiTreeGeometryReport r = GuiTreeGeometry.Inspect(tree, Title, Marker);
            Assert.Equal(4, r.MaxDepth);
            Assert.Equal(1, r.ProbeControlsFound);
            Assert.Equal(0, r.ControlsOutsideWindow);
        }

        [Fact]
        public void AnEmptyMarkerMatchesNothingRatherThanEverything()
        {
            GuiTreeResult tree = Build(
                Window(0, 0, 100, 100),
                Control("anything", 10, 10, 20, 20),
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.Window });

            Assert.Equal(0, GuiTreeGeometry.Inspect(tree, Title, null).ProbeControlsFound);
            Assert.Equal(0, GuiTreeGeometry.Inspect(tree, Title, "").ProbeControlsFound);
        }
    }
}
