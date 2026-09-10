using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit coverage for the PURE half of the GUI-tree dump: the event-stream to
    /// nested-tree assembler. Nothing here touches Unity, so every nesting rule and every
    /// recovery rule is provable headlessly - which matters because the other half (the
    /// Harmony interceptions) can only be exercised inside a running KSP.
    /// </summary>
    public class GuiTreeAssemblerTests
    {
        private static GuiTreeEvent Begin(GuiNodeKind kind, float x, float y, float w, float h,
            int clipDepth, string text = null)
        {
            return new GuiTreeEvent
            {
                Op = GuiTreeOp.Begin,
                Kind = kind,
                Rect = new GuiRect(x, y, w, h),
                LocalRect = new GuiRect(x, y, w, h),
                ClipDepth = clipDepth,
                Text = text,
            };
        }

        private static GuiTreeEvent Leaf(GuiNodeKind kind, float x, float y, float w, float h,
            int clipDepth, string text = null)
        {
            return new GuiTreeEvent
            {
                Op = GuiTreeOp.Leaf,
                Kind = kind,
                Rect = new GuiRect(x, y, w, h),
                LocalRect = new GuiRect(x, y, w, h),
                ClipDepth = clipDepth,
                Text = text,
            };
        }

        private static GuiTreeEvent End(GuiNodeKind kind)
        {
            return new GuiTreeEvent { Op = GuiTreeOp.End, Kind = kind };
        }

        [Fact]
        public void EmptyStreamAssemblesToAnEmptyTree()
        {
            GuiTreeResult r = GuiTreeAssembler.Assemble(new List<GuiTreeEvent>());
            Assert.Empty(r.Roots);
            Assert.Equal(0, r.NodeCount);
            Assert.Equal(0, r.EventCount);
        }

        [Fact]
        public void NullStreamIsToleratedRatherThanThrowing()
        {
            GuiTreeResult r = GuiTreeAssembler.Assemble(null);
            Assert.Empty(r.Roots);
        }

        [Fact]
        public void WellFormedWindowNestsItsChildren()
        {
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.Window, 10, 10, 300, 400, 1, "Parsek"),
                Begin(GuiNodeKind.LayoutGroup, 15, 30, 290, 380, 1),
                Leaf(GuiNodeKind.Label, 20, 35, 100, 20, 1, "Recordings"),
                Leaf(GuiNodeKind.Button, 20, 60, 100, 24, 1, "Close"),
                End(GuiNodeKind.LayoutGroup),
                End(GuiNodeKind.Window),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            Assert.Single(r.Roots);
            Assert.Equal(1, r.WindowCount);
            Assert.Equal(4, r.NodeCount);
            Assert.Equal(6, r.EventCount);
            Assert.Equal(0, r.StrayEnds);
            Assert.Equal(0, r.UnclosedAtEnd);
            Assert.Equal(0, r.AutoClosedByClip);
            Assert.Equal(0, r.AutoClosedByEnd);
            Assert.Equal(0, r.AutoClosedByRect);

            GuiTreeNode window = r.Roots[0];
            Assert.Equal(GuiNodeKind.Window, window.Kind);
            Assert.Single(window.Children);
            GuiTreeNode group = window.Children[0];
            Assert.Equal(GuiNodeKind.LayoutGroup, group.Kind);
            Assert.Equal(2, group.Children.Count);
            Assert.Equal("Recordings", group.Children[0].Text);
            Assert.Equal("Close", group.Children[1].Text);
        }

        [Fact]
        public void ScrollViewInsideAGroupNestsThreeDeep()
        {
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.Window, 0, 0, 400, 400, 1),
                Begin(GuiNodeKind.Group, 5, 5, 390, 390, 2),
                Begin(GuiNodeKind.ScrollView, 10, 10, 380, 300, 3),
                Leaf(GuiNodeKind.Label, 12, 12, 100, 18, 3, "row-1"),
                Leaf(GuiNodeKind.Label, 12, 32, 100, 18, 3, "row-2"),
                End(GuiNodeKind.ScrollView),
                Leaf(GuiNodeKind.Button, 10, 330, 80, 24, 2, "OK"),
                End(GuiNodeKind.Group),
                End(GuiNodeKind.Window),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            GuiTreeNode group = r.Roots[0].Children[0];
            Assert.Equal(2, group.Children.Count);
            Assert.Equal(GuiNodeKind.ScrollView, group.Children[0].Kind);
            Assert.Equal(2, group.Children[0].Children.Count);
            Assert.Equal("OK", group.Children[1].Text);
            Assert.Equal(0, r.UnclosedAtEnd);
        }

        // ---------------------------------------------------------- recovery rules

        [Fact]
        public void MissingEndGroupIsRecoveredFromTheClipDepth()
        {
            // The shape a Mono-inlined GUI.EndGroup produces: the End never arrives, but
            // the next event's clip depth proves the group is gone.
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.Window, 0, 0, 400, 400, 1),
                Begin(GuiNodeKind.Group, 5, 5, 200, 200, 2),
                Leaf(GuiNodeKind.Label, 10, 10, 50, 18, 2, "inside"),
                // no End(Group)
                Leaf(GuiNodeKind.Label, 10, 250, 50, 18, 1, "after"),
                End(GuiNodeKind.Window),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            GuiTreeNode window = r.Roots[0];
            Assert.Equal(2, window.Children.Count);
            Assert.Equal(GuiNodeKind.Group, window.Children[0].Kind);
            Assert.Equal("inside", window.Children[0].Children[0].Text);
            Assert.Equal("after", window.Children[1].Text);
            Assert.Equal(1, r.AutoClosedByClip);
            Assert.Equal(0, r.StrayEnds);
        }

        [Fact]
        public void ClipRecoveryAlsoClosesLayoutGroupsStrandedAboveTheGroup()
        {
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.Window, 0, 0, 400, 400, 1),
                Begin(GuiNodeKind.Group, 5, 5, 200, 200, 2),
                Begin(GuiNodeKind.LayoutGroup, 6, 6, 190, 190, 2),
                Leaf(GuiNodeKind.Label, 10, 10, 50, 18, 2, "inside"),
                // neither End arrives
                Leaf(GuiNodeKind.Label, 10, 300, 50, 18, 1, "after"),
                End(GuiNodeKind.Window),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            GuiTreeNode window = r.Roots[0];
            Assert.Equal(2, window.Children.Count);
            Assert.Equal("after", window.Children[1].Text);
            // Group + the LayoutGroup above it.
            Assert.Equal(2, r.AutoClosedByClip);
        }

        [Fact]
        public void MissingEndVerticalIsRecoveredFromRectContainment()
        {
            // A LayoutGroup pushes no clip, so the clip rule cannot see it. The next
            // sibling's rect falling outside the group is the only remaining signal.
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.Window, 0, 0, 400, 400, 1),
                Begin(GuiNodeKind.LayoutGroup, 10, 10, 100, 100, 1),
                Leaf(GuiNodeKind.Label, 12, 12, 80, 18, 1, "inside"),
                // no End(LayoutGroup)
                Leaf(GuiNodeKind.Label, 12, 300, 80, 18, 1, "far-below"),
                End(GuiNodeKind.Window),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            GuiTreeNode window = r.Roots[0];
            Assert.Equal(2, window.Children.Count);
            Assert.Equal("inside", window.Children[0].Children[0].Text);
            Assert.Equal("far-below", window.Children[1].Text);
            Assert.Equal(1, r.AutoClosedByRect);
        }

        [Fact]
        public void RectRuleKeepsALayoutGroupOpenForSubPixelDisagreement()
        {
            // GUILayout rounds group and child rects independently; a couple of pixels of
            // overhang must not be read as "the group ended".
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.LayoutGroup, 10, 10, 100, 100, 0),
                Leaf(GuiNodeKind.Label, 8, 9, 80, 18, 0, "just-outside"),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            Assert.Single(r.Roots);
            Assert.Single(r.Roots[0].Children);
            Assert.Equal(0, r.AutoClosedByRect);
            Assert.Equal(1, r.UnclosedAtEnd);
        }

        [Fact]
        public void DegenerateRectNeverTriggersTheLayoutGroupRectRule()
        {
            // Zero-size carriers (the DisabledHoverEcho label, spacers) sit anywhere and
            // must not be treated as "we left the group".
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.LayoutGroup, 10, 10, 100, 100, 0),
                Leaf(GuiNodeKind.Label, 900, 900, 0, 0, 0, "zero-size-carrier"),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            Assert.Single(r.Roots[0].Children);
            Assert.Equal(0, r.AutoClosedByRect);
        }

        [Fact]
        public void AnEndForAnOuterContainerClosesTheUnbalancedOnesInside()
        {
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.Window, 0, 0, 400, 400, 1),
                Begin(GuiNodeKind.LayoutGroup, 5, 5, 300, 300, 1),
                Begin(GuiNodeKind.LayoutGroup, 6, 6, 200, 200, 1),
                Leaf(GuiNodeKind.Label, 8, 8, 50, 18, 1, "deep"),
                End(GuiNodeKind.Window),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            Assert.Single(r.Roots);
            Assert.Equal(2, r.AutoClosedByEnd);
            Assert.Equal(0, r.StrayEnds);
            Assert.Equal(0, r.UnclosedAtEnd);
        }

        [Fact]
        public void AStrayEndIsCountedAndCollapsesNothing()
        {
            // The dangerous shape: if a stray End popped "the nearest anything", one extra
            // End would reparent the whole rest of the window.
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.Window, 0, 0, 400, 400, 1),
                End(GuiNodeKind.ScrollView),
                Leaf(GuiNodeKind.Label, 8, 8, 50, 18, 1, "still-inside"),
                End(GuiNodeKind.Window),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            Assert.Equal(1, r.StrayEnds);
            Assert.Single(r.Roots);
            Assert.Single(r.Roots[0].Children);
            Assert.Equal("still-inside", r.Roots[0].Children[0].Text);
        }

        [Fact]
        public void UnclosedContainersAtStreamEndAreCounted()
        {
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.Window, 0, 0, 400, 400, 1),
                Begin(GuiNodeKind.Group, 5, 5, 300, 300, 2),
                Leaf(GuiNodeKind.Label, 8, 8, 50, 18, 2, "orphan"),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            Assert.Equal(2, r.UnclosedAtEnd);
            Assert.Single(r.Roots);
        }

        [Fact]
        public void UnknownClipDepthFallsBackToExplicitPairingOnly()
        {
            // ClipDepth -1 is what an unavailable GUIClip probe produces. The clip rule
            // must go quiet rather than close everything.
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.Window, 0, 0, 400, 400, -1),
                Begin(GuiNodeKind.Group, 5, 5, 300, 300, -1),
                Leaf(GuiNodeKind.Label, 8, 8, 50, 18, -1, "a"),
                End(GuiNodeKind.Group),
                Leaf(GuiNodeKind.Label, 8, 40, 50, 18, -1, "b"),
                End(GuiNodeKind.Window),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            Assert.Equal(0, r.AutoClosedByClip);
            Assert.Equal(2, r.Roots[0].Children.Count);
            Assert.Equal(0, r.UnclosedAtEnd);
        }

        [Fact]
        public void TwoSiblingWindowsBecomeTwoRoots()
        {
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.Window, 0, 0, 100, 100, 1, "A"),
                Leaf(GuiNodeKind.Label, 2, 2, 20, 10, 1, "a"),
                End(GuiNodeKind.Window),
                Begin(GuiNodeKind.Window, 200, 0, 100, 100, 1, "B"),
                Leaf(GuiNodeKind.Label, 202, 2, 20, 10, 1, "b"),
                End(GuiNodeKind.Window),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            Assert.Equal(2, r.Roots.Count);
            Assert.Equal(2, r.WindowCount);
            Assert.Equal("A", r.Roots[0].Text);
            Assert.Equal("B", r.Roots[1].Text);
        }

        [Fact]
        public void NullEventsAreSkippedWithoutCorruptingTheTree()
        {
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.Window, 0, 0, 100, 100, 1),
                null,
                Leaf(GuiNodeKind.Label, 2, 2, 20, 10, 1, "a"),
                End(GuiNodeKind.Window),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            Assert.Equal(3, r.EventCount);
            Assert.Single(r.Roots[0].Children);
        }

        [Fact]
        public void LeafFieldsSurviveIntoTheNode()
        {
            var toggle = Leaf(GuiNodeKind.Toggle, 1, 2, 3, 4, 1, "Show ghosts");
            toggle.Tooltip = "Draw committed ghosts in map view";
            toggle.StyleName = "toggle";
            toggle.Enabled = false;
            toggle.ToggleValue = true;
            toggle.ControlId = 4711;

            GuiTreeResult r = GuiTreeAssembler.Assemble(new List<GuiTreeEvent> { toggle });

            GuiTreeNode n = r.Roots[0];
            Assert.Equal("Show ghosts", n.Text);
            Assert.Equal("Draw committed ghosts in map view", n.Tooltip);
            Assert.Equal("toggle", n.StyleName);
            Assert.False(n.Enabled);
            Assert.True(n.ToggleValue.HasValue && n.ToggleValue.Value);
            Assert.Equal(4711, n.ControlId.Value);
        }

        // ------------------------------------------------------------ kind helpers

        // Cases pass the enum as its int value: the theory method has to be public
        // for xUnit and the kind enum is internal to Parsek.
        [Theory]
        [InlineData((int)GuiNodeKind.Window, "window")]
        [InlineData((int)GuiNodeKind.Group, "group")]
        [InlineData((int)GuiNodeKind.ScrollView, "scrollview")]
        [InlineData((int)GuiNodeKind.LayoutGroup, "layoutgroup")]
        [InlineData((int)GuiNodeKind.Label, "label")]
        [InlineData((int)GuiNodeKind.Box, "box")]
        [InlineData((int)GuiNodeKind.Button, "button")]
        [InlineData((int)GuiNodeKind.RepeatButton, "repeatbutton")]
        [InlineData((int)GuiNodeKind.Toggle, "toggle")]
        [InlineData((int)GuiNodeKind.TextField, "textfield")]
        [InlineData((int)GuiNodeKind.ButtonGrid, "buttongrid")]
        [InlineData((int)GuiNodeKind.Slider, "slider")]
        [InlineData((int)GuiNodeKind.Control, "control")]
        public void EveryKindHasAPinnedWireName(int kind, string expected)
        {
            Assert.Equal(expected, GuiTreeAssembler.KindName((GuiNodeKind)kind));
        }

        [Theory]
        [InlineData("button", false, (int)GuiNodeKind.Button)]
        [InlineData("Button", false, (int)GuiNodeKind.Button)]
        [InlineData("toggle", false, (int)GuiNodeKind.Toggle)]
        [InlineData("miniButton", true, (int)GuiNodeKind.Toggle)]
        [InlineData("ParsekRowStyle", false, (int)GuiNodeKind.Control)]
        [InlineData(null, false, (int)GuiNodeKind.Control)]
        [InlineData("", true, (int)GuiNodeKind.Toggle)]
        public void StyleNameClassificationIsTheHonestFallback(string styleName, bool on,
            int expected)
        {
            Assert.Equal((GuiNodeKind)expected,
                GuiTreeAssembler.ClassifyFromStyleName(styleName, on));
        }
    }
}
