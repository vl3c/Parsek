using System;
using System.Collections.Generic;
using System.Globalization;
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

        // ------------------------------------------------------------------------
        // Cells below cover the containment box, the per-kind counts the live cell
        // asserts against, the scroll-offset reading, and the two things that would
        // otherwise only fail on a different machine (culture, window nesting).
        // ------------------------------------------------------------------------

        [Fact]
        public void TheMeasuredContentBoxWinsOverTheDeclaredRect()
        {
            // The declared rect is what the CALLER passed to GUILayout.Window before
            // this frame moved the window, and it is never screen-converted; the
            // measured pair is a GUIToScreenPoint taken inside the callback the children
            // were drawn in. A control drawn where the window ACTUALLY is must pass, and
            // the stale declaration must not be what decides it.
            GuiTreeEvent window = Window(60, 60, 320, 260);
            window.ContentOriginX = 500f;   // the window really moved to (500, 300)
            window.ContentOriginY = 300f;
            window.ArgWidth = 320f;
            window.ArgHeight = 260f;

            GuiTreeResult tree = Build(
                window,
                Control(Marker + "label", 506, 324, 200, 18),
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.Window });

            GuiTreeGeometryReport r = GuiTreeGeometry.Inspect(tree, Title, Marker);

            Assert.True(r.ContentBoxMeasured);
            Assert.Equal(0, r.ControlsOutsideWindow);
            Assert.Contains("measured contentOrigin+argSize", r.DescribeContainmentBox());
            // ... and the declaration is still reported, as the second reading.
            Assert.Equal("[60,60,320,260]", r.DescribeWindowRect());
        }

        [Fact]
        public void WithNoMeasuredPairTheDeclaredRectIsTheFallbackAndSaysSo()
        {
            GuiTreeResult tree = Build(
                Window(60, 60, 320, 260),
                Control(Marker + "label", 66, 84, 200, 18),
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.Window });

            GuiTreeGeometryReport r = GuiTreeGeometry.Inspect(tree, Title, Marker);

            Assert.False(r.ContentBoxMeasured);
            Assert.Equal(0, r.ControlsOutsideWindow);
            Assert.Contains("declared rect", r.DescribeContainmentBox());
        }

        [Fact]
        public void AWindowBelowTheRootsIsStillFound()
        {
            // FindWindow recurses because a window is not necessarily a root: an OnGUI
            // container that opens a group before drawing its window puts the window one
            // level down. Mutation killed: a flat scan of tree.Roots, which would report
            // "window not found" and skip every geometry check in the live cell.
            var events = new List<GuiTreeEvent>
            {
                new GuiTreeEvent
                {
                    Op = GuiTreeOp.Begin,
                    Kind = GuiNodeKind.Group,
                    Rect = new GuiRect(0f, 0f, 800f, 600f),
                    ClipDepth = 1,
                },
                Window(60, 60, 320, 260),
                Control(Marker + "label", 66, 84, 200, 18),
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.Window },
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.Group },
            };

            GuiTreeResult tree = GuiTreeAssembler.Assemble(events);
            GuiTreeGeometryReport r = GuiTreeGeometry.Inspect(tree, Title, Marker);

            Assert.True(r.WindowFound);
            Assert.Equal(1, r.ProbeControlsFound);
        }

        [Fact]
        public void PerKindCountsAreScopedToTheWindowAndExcludeItself()
        {
            // The live cell's EXACT expectations rest on these counts, precisely because
            // the per-funnel hit counters are process-wide. The window itself must not be
            // counted (it is the scope, not a member), and a second window's contents
            // must not leak in.
            var events = new List<GuiTreeEvent>
            {
                Window(60, 60, 320, 260),
                Group(),
                Control(Marker + "a", 66, 84, 200, 18),
                Control(Marker + "b", 66, 106, 200, 18),
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.LayoutGroup },
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.Window },
                new GuiTreeEvent
                {
                    Op = GuiTreeOp.Begin,
                    Kind = GuiNodeKind.Window,
                    Rect = new GuiRect(0f, 0f, 100f, 100f),
                    ClipDepth = 1,
                    Text = "Some other window",
                },
                Control("not-the-probe", 10, 10, 20, 20),
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.Window },
            };

            GuiTreeResult tree = GuiTreeAssembler.Assemble(events);
            GuiTreeGeometryReport r = GuiTreeGeometry.Inspect(tree, Title, Marker);

            Assert.Equal(2, r.CountOf(GuiNodeKind.Label));
            Assert.Equal(1, r.CountOf(GuiNodeKind.LayoutGroup));
            Assert.Equal(0, r.CountOf(GuiNodeKind.Window));
            Assert.Equal(0, r.CountOf(GuiNodeKind.Button));
            Assert.Equal("layoutgroup=1 label=2", r.DescribeKindCounts());
        }

        [Fact]
        public void TheScrollOffsetReadingMeasuresTheRowAgainstTheViewport()
        {
            // A scroll view scrolled down by 25 px draws its first row 25 px ABOVE its
            // own viewport, clipped away. That difference is the only observable proof
            // that the scroll offset a scroll view pushes onto the clip stack reached
            // GUIUtility.GUIToScreenRect at all.
            var events = new List<GuiTreeEvent>
            {
                Window(60, 60, 320, 260),
                new GuiTreeEvent
                {
                    Op = GuiTreeOp.Begin,
                    Kind = GuiNodeKind.ScrollView,
                    Rect = new GuiRect(66f, 200f, 300f, 70f),
                    ClipDepth = 2,
                },
                Control("row-0", 68, 175, 200, 18, 2),
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.ScrollView },
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.Window },
            };

            GuiTreeResult tree = GuiTreeAssembler.Assemble(events);
            GuiTreeScrollOffsetReport r = GuiTreeGeometry.MeasureScrollOffset(tree, Title, "row-0");

            Assert.True(r.ScrollViewFound);
            Assert.True(r.RowFound);
            Assert.Equal(25f, r.OffsetAbovePx, 3f);
            Assert.Contains("offsetAbove=25", r.Describe());
        }

        [Fact]
        public void AnUnscrolledViewReadsAsNoOffsetRatherThanAsSuccess()
        {
            // The negative control for the cell above: if the clip's scroll offset were
            // dropped, the first row would land AT the viewport's top and the reading
            // would be 0.
            var events = new List<GuiTreeEvent>
            {
                Window(60, 60, 320, 260),
                new GuiTreeEvent
                {
                    Op = GuiTreeOp.Begin,
                    Kind = GuiNodeKind.ScrollView,
                    Rect = new GuiRect(66f, 200f, 300f, 70f),
                    ClipDepth = 2,
                },
                Control("row-0", 68, 200, 200, 18, 2),
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.ScrollView },
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.Window },
            };

            GuiTreeResult tree = GuiTreeAssembler.Assemble(events);
            GuiTreeScrollOffsetReport r = GuiTreeGeometry.MeasureScrollOffset(tree, Title, "row-0");

            Assert.True(r.RowFound);
            Assert.Equal(0f, r.OffsetAbovePx, 3f);
        }

        [Fact]
        public void MissingScrollViewOrRowIsDescribedRatherThanThrown()
        {
            GuiTreeResult empty = Build(
                Window(0, 0, 100, 100),
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.Window });

            GuiTreeScrollOffsetReport none = GuiTreeGeometry.MeasureScrollOffset(empty, Title, "row-0");
            Assert.False(none.ScrollViewFound);
            Assert.Contains("no scroll view", none.Describe());

            Assert.False(GuiTreeGeometry.MeasureScrollOffset(null, Title, "row-0").ScrollViewFound);
        }

        [Fact]
        public void EveryFormattedNumberIsInvariantUnderAGermanCulture()
        {
            // These strings go into an InGameAssert failure message and into
            // parsek-test-results.txt, which a person and a parser both read. Under
            // de-DE a bare ToString would print "60,5" - a comma inside a
            // comma-separated rect. de-DE is pinned here to PROVE the site is invariant,
            // never to make a culture-dependent one pass.
            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");

                GuiTreeEvent window = Window(60.5f, 60.25f, 320.75f, 260.5f);
                window.ContentOriginX = 60.5f;
                window.ContentOriginY = 60.25f;
                window.ArgWidth = 320.75f;
                window.ArgHeight = 260.5f;

                GuiTreeResult tree = Build(
                    window,
                    Control(Marker + "outside", 4000.5f, 12.25f, 10.5f, 10.5f),
                    new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.Window });

                GuiTreeGeometryReport r = GuiTreeGeometry.Inspect(tree, Title, Marker);

                Assert.Equal("[60.5,60.25,320.75,260.5]", r.DescribeWindowRect());
                Assert.StartsWith("[60.5,60.25,320.75,260.5]", r.DescribeContainmentBox());
                Assert.Contains("rect=[4000.5,12.25,10.5,10.5]", r.FirstOffender);
                Assert.DoesNotContain(",25", r.FirstOffender);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }
    }

    /// <summary>
    /// The clip-container rect contract, and the geometry reading that would catch it
    /// being broken.
    ///
    /// <para><b>The defect these cells pin.</b> <c>GUI.BeginGroup</c> ENDS with
    /// <c>GUIClip.Push(position, scrollOffset, ...)</c> and <c>GUI.BeginScrollView</c>
    /// with <c>GUIClip.Push(screenRect, (round(-scroll.x - viewRect.x),
    /// round(-scroll.y - viewRect.y)), ...)</c> - both decompiled from the shipped
    /// <c>UnityEngine.IMGUIModule.dll</c>. Both nodes are recorded from a POSTFIX, where
    /// the container's own clip is topmost, so <c>GUIUtility.GUIToScreenRect</c>'s
    /// <c>UnclipToWindow</c> walk adds the container's origin - and a scroll view's
    /// scroll offset - a SECOND time. The recorder therefore converts the rect in the
    /// PREFIX, before the push, and the postfix takes it off a stack.</para>
    /// </summary>
    public class GuiTreeContainerRectContractTests
    {
        private const string Title = "Parsek GuiTree Probe";

        // The live cell's own numbers, so the arithmetic here is the arithmetic there.
        private const float WindowContentOriginY = 78f;   // window at y=60 plus a title bar
        private const float ScrollViewLocalY = 130f;      // the view's y inside the window
        private const float ScrollPx = 25f;               // GuiTreeProbe.ScrollOffsetPx
        private const float RowTopMarginPx = 2f;          // stock skin, inside the content
        private const float CellSlackPx = 10f;            // GuiTreeDumpImguiTest.ScrollOffsetSlackPx

        [Fact]
        public void ThePrefixConversionIsTheOriginAContainerNodeCarries()
        {
            var pushedByThePrefix = new GuiRect(72f, 208f, 300f, 70f);
            var measuredByThePostfix = new GuiRect(177f, 313f, 300f, 70f);

            GuiRect resolved = GuiTreeRecorder.ResolveContainerScreenRect(
                pushedByThePrefix, measuredByThePostfix);

            Assert.Equal(72f, resolved.X, 3f);
            Assert.Equal(208f, resolved.Y, 3f);
        }

        [Fact]
        public void TheSizeStillComesFromThePostfixWhereTheMatrixScaleIsKnown()
        {
            // GUIToScreenRect converts the ORIGIN only and returns w/h untouched, so the
            // origin is the only half the container's own clip can corrupt. The SIZE is
            // scaled by hand from the GUI.matrix read when the CAPTURE opens - and the
            // prefix deliberately does not open the capture, so a container that happens
            // to be the first funnel event of a capture would otherwise carry a size
            // scaled by the reset defaults instead of by the real matrix.
            var pushedByThePrefix = new GuiRect(72f, 208f, 300f, 70f);
            var scaledAtThePostfix = new GuiRect(177f, 313f, 450f, 105f);

            GuiRect resolved = GuiTreeRecorder.ResolveContainerScreenRect(
                pushedByThePrefix, scaledAtThePostfix);

            Assert.Equal(72f, resolved.X, 3f);
            Assert.Equal(450f, resolved.W, 3f);
            Assert.Equal(105f, resolved.H, 3f);
        }

        [Fact]
        public void WithNoPrefixConversionThePostfixValueIsTheOnlyFallback()
        {
            // Unreachable in the live path - prefix and postfix are hooks on the SAME
            // method, so either both run or neither does - but the resolution must not
            // invent a rect when the stack is empty. The fallback is the double-counted
            // reading, which is still better than a zero rect or a guess.
            var measuredByThePostfix = new GuiRect(177f, 313f, 300f, 70f);

            GuiRect resolved = GuiTreeRecorder.ResolveContainerScreenRect(
                null, measuredByThePostfix);

            Assert.Equal(177f, resolved.X, 3f);
            Assert.Equal(313f, resolved.Y, 3f);
        }

        [Fact]
        public void TheCorrectedScrollViewRectReadsTheScrollOffsetTheLiveCellPins()
        {
            // viewRect.y is 0 for a GUILayout scroll view (GUILayout.BeginScrollView
            // passes new Rect(0, 0, clientWidth, clientHeight)), so the clip's offset is
            // -round(scroll.y) and row 0 lands scroll.y - margin above the view's top.
            float viewTop = WindowContentOriginY + ScrollViewLocalY;
            float rowTop = viewTop - ScrollPx + RowTopMarginPx;

            GuiTreeScrollOffsetReport r = Measure(viewTop, rowTop);

            Assert.True(r.RowFound);
            Assert.Equal(ScrollPx - RowTopMarginPx, r.OffsetAbovePx, 3f);
            Assert.True(Math.Abs(r.OffsetAbovePx - ScrollPx) <= CellSlackPx,
                "the live cell pins this reading at " + ScrollPx + " +/- " + CellSlackPx
                + " and the derivation gives " + r.OffsetAbovePx);
        }

        [Fact]
        public void ADoubleCountedScrollViewRectReadsAsTheViewsOwnLocalYAndBlowsThePin()
        {
            // The mirror of the cell above: the postfix-time conversion adds the view's
            // origin and its scroll offset again, so viewTop reads
            // W + P + (P - scroll) and the offset collapses to P.y - margin - far outside
            // the slack, which is what makes the live reading a detector rather than a
            // formality.
            float correctViewTop = WindowContentOriginY + ScrollViewLocalY;
            float doubleCountedViewTop = correctViewTop + ScrollViewLocalY - ScrollPx;
            float rowTop = correctViewTop - ScrollPx + RowTopMarginPx;

            GuiTreeScrollOffsetReport r = Measure(doubleCountedViewTop, rowTop);

            Assert.True(r.RowFound);
            Assert.Equal(ScrollViewLocalY - RowTopMarginPx, r.OffsetAbovePx, 3f);
            Assert.True(Math.Abs(r.OffsetAbovePx - ScrollPx) > CellSlackPx,
                "a double-counted container rect must fall OUTSIDE the live cell's slack, "
                + "or the cell cannot detect it; reading was " + r.OffsetAbovePx);
        }

        private static GuiTreeScrollOffsetReport Measure(float scrollViewTop, float rowTop)
        {
            var events = new List<GuiTreeEvent>
            {
                new GuiTreeEvent
                {
                    Op = GuiTreeOp.Begin,
                    Kind = GuiNodeKind.Window,
                    Rect = new GuiRect(60f, 60f, 320f, 300f),
                    LocalRect = new GuiRect(60f, 60f, 320f, 300f),
                    ClipDepth = 1,
                    Text = Title,
                    WindowId = 907311,
                },
                new GuiTreeEvent
                {
                    Op = GuiTreeOp.Begin,
                    Kind = GuiNodeKind.ScrollView,
                    Rect = new GuiRect(66f, scrollViewTop, 300f, 70f),
                    LocalRect = new GuiRect(6f, ScrollViewLocalY, 300f, 70f),
                    ClipDepth = 2,
                },
                new GuiTreeEvent
                {
                    Op = GuiTreeOp.Leaf,
                    Kind = GuiNodeKind.Label,
                    Rect = new GuiRect(68f, rowTop, 200f, 18f),
                    LocalRect = new GuiRect(2f, RowTopMarginPx, 200f, 18f),
                    ClipDepth = 2,
                    Text = "parsekscroll-0",
                },
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.ScrollView },
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.Window },
            };

            // The title overload deliberately: this class is about the RECT contract, and
            // the window-id key has its own cells above.
            return GuiTreeGeometry.MeasureScrollOffset(
                GuiTreeAssembler.Assemble(events), Title, "parsekscroll-0");
        }
    }
}
