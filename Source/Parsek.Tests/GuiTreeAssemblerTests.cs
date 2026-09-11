using System;
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
        public void ASiblingGroupClosesTheOpenOneAtTheSameClipDepth()
        {
            // The OTHER half of the clip rule, and the one a single comparison loses.
            // A clip container's recorded depth is its CHILDREN's depth, so two SIBLING
            // groups carry the SAME number. With GUI.EndGroup inlined away, only
            // "a container Begin at equal depth closes the open container" keeps them
            // siblings instead of nesting group B inside group A forever.
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.Window, 0, 0, 400, 400, 1),
                Begin(GuiNodeKind.Group, 5, 5, 100, 100, 2),
                Leaf(GuiNodeKind.Label, 8, 8, 50, 18, 2, "in-a"),
                // no End(Group)
                Begin(GuiNodeKind.Group, 5, 200, 100, 100, 2),
                Leaf(GuiNodeKind.Label, 8, 208, 50, 18, 2, "in-b"),
                End(GuiNodeKind.Group),
                End(GuiNodeKind.Window),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            GuiTreeNode window = r.Roots[0];
            Assert.Equal(2, window.Children.Count);
            Assert.Equal("in-a", window.Children[0].Children[0].Text);
            Assert.Equal("in-b", window.Children[1].Children[0].Text);
            Assert.Equal(1, r.AutoClosedByClip);
            Assert.Equal(0, r.UnclosedAtEnd);
        }

        [Fact]
        public void ANestedGroupStillNestsRatherThanClosingItsParent()
        {
            // The mirror of the cell above: the equal-depth rule must not evict a
            // GENUINELY nested container, whose depth is strictly greater.
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.Window, 0, 0, 400, 400, 1),
                Begin(GuiNodeKind.Group, 5, 5, 300, 300, 2),
                Begin(GuiNodeKind.ScrollView, 10, 10, 200, 200, 3),
                Leaf(GuiNodeKind.Label, 12, 12, 50, 18, 3, "deep"),
                End(GuiNodeKind.ScrollView),
                End(GuiNodeKind.Group),
                End(GuiNodeKind.Window),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            GuiTreeNode group = r.Roots[0].Children[0];
            Assert.Equal(GuiNodeKind.ScrollView, group.Children[0].Kind);
            Assert.Equal("deep", group.Children[0].Children[0].Text);
            Assert.Equal(0, r.AutoClosedByClip);
        }

        [Fact]
        public void ALeafAtTheSameDepthStaysInsideItsContainer()
        {
            // The other side of the asymmetry: a LEAF at equal depth is INSIDE, and must
            // never be treated as a sibling that closes the container.
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.ScrollView, 0, 0, 200, 200, 2),
                Leaf(GuiNodeKind.Label, 2, 2, 50, 18, 2, "row-1"),
                Leaf(GuiNodeKind.Label, 2, 22, 50, 18, 2, "row-2"),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            Assert.Single(r.Roots);
            Assert.Equal(2, r.Roots[0].Children.Count);
            Assert.Equal(0, r.AutoClosedByClip);
        }

        [Fact]
        public void ALayoutGroupBeginAtTheSameDepthAlsoStaysInside()
        {
            // A LayoutGroup pushes no clip, so its Begin reports the depth it was drawn
            // AT - the same rule as a leaf, not the same rule as a group.
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.Group, 0, 0, 200, 200, 2),
                Begin(GuiNodeKind.LayoutGroup, 2, 2, 190, 190, 2),
                Leaf(GuiNodeKind.Label, 4, 4, 50, 18, 2, "row"),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            Assert.Single(r.Roots);
            Assert.Equal(GuiNodeKind.LayoutGroup, r.Roots[0].Children[0].Kind);
            Assert.Equal("row", r.Roots[0].Children[0].Children[0].Text);
            Assert.Equal(0, r.AutoClosedByClip);
        }

        [Fact]
        public void ASecondWindowIsASiblingEvenWithItsPredecessorsEndMissing()
        {
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.Window, 0, 0, 100, 100, 1, "A"),
                Leaf(GuiNodeKind.Label, 2, 2, 20, 10, 1, "a"),
                // no End(Window)
                Begin(GuiNodeKind.Window, 200, 0, 100, 100, 1, "B"),
                Leaf(GuiNodeKind.Label, 202, 2, 20, 10, 1, "b"),
                End(GuiNodeKind.Window),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            Assert.Equal(2, r.Roots.Count);
            Assert.Equal(2, r.WindowCount);
            Assert.Equal("a", r.Roots[0].Children[0].Text);
            Assert.Equal("b", r.Roots[1].Children[0].Text);
            Assert.Equal(1, r.AutoClosedByClip);
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

        // ------------------------------------------------------------------------
        // The cells below kill mutations that survived the first review: each one
        // fails against a specific plausible wrong implementation, named in its own
        // comment, and passes against the real one.
        // ------------------------------------------------------------------------

        [Fact]
        public void AnEndClosesTheNearestOpenGroupNotTheOutermost()
        {
            // Mutation killed: CloseMatching searching FORWARD (0..n) instead of
            // backward. With two nested layout groups the outermost would match, both
            // would close, and the second leaf would land on the root - which every
            // other cell in this file tolerates, because none of them nests two of a
            // kind and then closes only one.
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.LayoutGroup, 10, 10, 200, 200, 1, "A"),
                Begin(GuiNodeKind.LayoutGroup, 20, 20, 100, 100, 1, "B"),
                Leaf(GuiNodeKind.Label, 25, 25, 50, 18, 1, "in-B"),
                End(GuiNodeKind.LayoutGroup),
                Leaf(GuiNodeKind.Label, 30, 130, 50, 18, 1, "in-A"),
                End(GuiNodeKind.LayoutGroup),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            Assert.Single(r.Roots);
            GuiTreeNode a = r.Roots[0];
            Assert.Equal("A", a.Text);
            Assert.Equal(2, a.Children.Count);
            Assert.Equal("B", a.Children[0].Text);
            Assert.Equal("in-B", a.Children[0].Children[0].Text);
            Assert.Equal("in-A", a.Children[1].Text);
            Assert.Equal(0, r.AutoClosedByEnd);
            Assert.Equal(0, r.StrayEnds);
        }

        [Fact]
        public void ALayoutGroupEndMatchesItsOwnOrientationNotJustItsKind()
        {
            // The recorder pops the orientation off its own Begin stack, because
            // GUILayoutUtility.EndLayoutGroup takes no arguments. Mutation killed:
            // ignoring Horizontal in CloseMatching. Here a HORIZONTAL group was left
            // open (its own End inlined or lost) inside a VERTICAL one; the vertical
            // End must skip past it rather than close it and leak the vertical group.
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.Window, 0, 0, 400, 400, 1),
                BeginLayoutGroup(false, 10, 10, 300, 300, 1, "vertical"),
                BeginLayoutGroup(true, 12, 12, 280, 24, 1, "horizontal-stranded"),
                Leaf(GuiNodeKind.Label, 14, 14, 60, 18, 1, "row"),
                // no End for the horizontal group
                EndLayoutGroup(false),
                End(GuiNodeKind.Window),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            GuiTreeNode window = r.Roots[0];
            Assert.Single(window.Children);
            Assert.Equal("vertical", window.Children[0].Text);
            Assert.Equal("horizontal-stranded", window.Children[0].Children[0].Text);
            // The stranded horizontal group is closed on the way out, and counted.
            Assert.Equal(1, r.AutoClosedByEnd);
            Assert.Equal(0, r.StrayEnds);
            Assert.Equal(0, r.UnclosedAtEnd);
        }

        [Fact]
        public void AnOrientationLessLayoutGroupEndStillMatchesEitherKind()
        {
            // The recorder emits an End with no orientation when its Begin stack was
            // empty (the per-frame cap tripped between the two). That End must still
            // close something, or the group leaks.
            var events = new List<GuiTreeEvent>
            {
                BeginLayoutGroup(true, 0, 0, 100, 100, 0, "horizontal"),
                EndLayoutGroup(null),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            Assert.Equal(0, r.StrayEnds);
            Assert.Equal(0, r.UnclosedAtEnd);
        }

        [Fact]
        public void MissingEndScrollViewIsRecoveredFromTheClipDepth()
        {
            // The mirror of MissingEndGroupIsRecoveredFromTheClipDepth. A scroll view
            // pushes a clip exactly like a group, so the same asymmetric rule must
            // close it - a rule that special-cased Group would leave every scroll view
            // open and swallow the rest of the window.
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.Window, 0, 0, 400, 400, 1),
                Begin(GuiNodeKind.ScrollView, 10, 10, 200, 70, 2),
                Leaf(GuiNodeKind.Label, 12, 12, 100, 18, 2, "row"),
                // no End(ScrollView)
                Leaf(GuiNodeKind.Label, 12, 300, 100, 18, 1, "after-the-scroll-view"),
                End(GuiNodeKind.Window),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            GuiTreeNode window = r.Roots[0];
            Assert.Equal(2, window.Children.Count);
            Assert.Equal(GuiNodeKind.ScrollView, window.Children[0].Kind);
            Assert.Equal("row", window.Children[0].Children[0].Text);
            Assert.Equal("after-the-scroll-view", window.Children[1].Text);
            Assert.Equal(1, r.AutoClosedByClip);
        }

        [Fact]
        public void TheRectRuleClosesOnTheXAxisToo()
        {
            // Mutation killed: a containment test that only compares Y. Inside a
            // HORIZONTAL layout group the next sibling moves RIGHT, not down, so a
            // y-only rule would keep every horizontal group open to the end of the
            // window.
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.Window, 0, 0, 400, 400, 1),
                BeginLayoutGroup(true, 10, 10, 100, 24, 1, "horizontal"),
                Leaf(GuiNodeKind.Label, 12, 12, 40, 18, 1, "inside"),
                // no End; the next control is to the RIGHT of the group, same y band
                Leaf(GuiNodeKind.Label, 300, 12, 40, 18, 1, "far-right"),
                End(GuiNodeKind.Window),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            GuiTreeNode window = r.Roots[0];
            Assert.Equal(2, window.Children.Count);
            Assert.Equal("inside", window.Children[0].Children[0].Text);
            Assert.Equal("far-right", window.Children[1].Text);
            Assert.Equal(1, r.AutoClosedByRect);
        }

        [Fact]
        public void TheClipRuleClosesTwoContainersOnOneEvent()
        {
            // Mutation killed: an `if` instead of a `while` in CloseByClipDepth. Two
            // nested containers whose Ends were both inlined must both close on the
            // first event that proves it; one iteration would leave the outer one open
            // and reparent everything after it.
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.Window, 0, 0, 400, 400, 1),
                Begin(GuiNodeKind.Group, 10, 10, 200, 200, 2),
                Begin(GuiNodeKind.Group, 20, 20, 100, 100, 3),
                Leaf(GuiNodeKind.Label, 22, 22, 50, 18, 3, "deep"),
                // both Ends inlined; the next leaf is back at the window's own depth
                Leaf(GuiNodeKind.Label, 12, 300, 50, 18, 1, "back-out"),
                End(GuiNodeKind.Window),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            GuiTreeNode window = r.Roots[0];
            Assert.Equal(2, window.Children.Count);
            Assert.Equal("back-out", window.Children[1].Text);
            Assert.Equal(2, r.AutoClosedByClip);
        }

        [Fact]
        public void TheRectRuleClosesTwoLayoutGroupsOnOneEvent()
        {
            // The rect-rule mirror of the cell above, and the same mutation.
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.Window, 0, 0, 400, 400, 1),
                BeginLayoutGroup(false, 10, 10, 200, 100, 1, "outer"),
                BeginLayoutGroup(true, 12, 12, 100, 24, 1, "inner"),
                Leaf(GuiNodeKind.Label, 14, 14, 40, 18, 1, "inside"),
                // neither End arrives; the next control is outside BOTH
                Leaf(GuiNodeKind.Label, 14, 350, 40, 18, 1, "far-below"),
                End(GuiNodeKind.Window),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            GuiTreeNode window = r.Roots[0];
            Assert.Equal(2, window.Children.Count);
            Assert.Equal("far-below", window.Children[1].Text);
            Assert.Equal(2, r.AutoClosedByRect);
        }

        [Fact]
        public void ADegenerateLayoutGroupIsLookedPastToTheNearestUsableAncestor()
        {
            // Mutation killed: the old "top rect degenerate -> return" stopped the rect
            // rule dead whenever GUILayout left a zero-size carrier on top, which is
            // ordinary. The rule now looks past it to the enclosing group with a real
            // rect, and closes BOTH when the incoming rect is outside that one.
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.Window, 0, 0, 400, 400, 1),
                BeginLayoutGroup(false, 10, 10, 200, 100, 1, "real"),
                BeginLayoutGroup(false, 0, 0, 0, 0, 1, "zero-size-carrier"),
                Leaf(GuiNodeKind.Label, 14, 14, 40, 18, 1, "inside"),
                Leaf(GuiNodeKind.Label, 14, 350, 40, 18, 1, "far-below"),
                End(GuiNodeKind.Window),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            GuiTreeNode window = r.Roots[0];
            Assert.Equal(2, window.Children.Count);
            Assert.Equal("far-below", window.Children[1].Text);
            Assert.Equal(2, r.AutoClosedByRect);
            Assert.Equal(0, r.RectRuleInert);
        }

        [Fact]
        public void WithNoUsableAncestorRectTheRuleIsCountedInertRatherThanGuessing()
        {
            // ... and when there is NO usable rect anywhere in the open layout-group
            // run, the rule cannot say anything. It must not guess, and the reader has
            // to be told that the nesting there rests on the End pairing alone.
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.Window, 0, 0, 400, 400, 1),
                BeginLayoutGroup(false, 0, 0, 0, 0, 1, "carrier"),
                Leaf(GuiNodeKind.Label, 14, 350, 40, 18, 1, "anywhere"),
                End(GuiNodeKind.Window),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            Assert.Equal(0, r.AutoClosedByRect);
            Assert.Equal(1, r.RectRuleInert);
            // The leaf stayed in the carrier: nothing was guessed.
            Assert.Equal("anywhere", r.Roots[0].Children[0].Children[0].Text);
        }

        [Fact]
        public void KnownAndUnknownClipDepthsMixWithoutPoisoningEachOther()
        {
            // The containers resolved a real depth; the leaves did not (a transient
            // failure of GUIClip.Internal_GetCount now degrades PER EVENT rather than
            // parking the probe for the rest of the capture). Mutation killed: treating
            // -1 as a small number, which would evict every unknown-depth leaf from the
            // container it belongs to.
            var events = new List<GuiTreeEvent>
            {
                Begin(GuiNodeKind.Window, 0, 0, 400, 400, 1),
                Begin(GuiNodeKind.Group, 10, 10, 200, 200, 2),
                Leaf(GuiNodeKind.Label, 12, 12, 50, 18, -1, "unknown-depth"),
                Leaf(GuiNodeKind.Label, 12, 40, 50, 18, -1, "also-unknown"),
                End(GuiNodeKind.Group),
                End(GuiNodeKind.Window),
            };

            GuiTreeResult r = GuiTreeAssembler.Assemble(events);

            GuiTreeNode group = r.Roots[0].Children[0];
            Assert.Equal(2, group.Children.Count);
            Assert.Equal(0, r.AutoClosedByClip);
            Assert.Equal(0, r.StrayEnds);
        }

        [Fact]
        public void TheRectSlackIsAnUpperBoundNotJustALowerOne()
        {
            // RectRuleKeepsALayoutGroupOpenForSubPixelDisagreement pins that a small
            // overhang is tolerated. Nothing pinned that a LARGER one is not - a slack
            // mutated to 400 px would pass every other cell in this file. Both sides
            // are expressed against the constant itself, so a deliberate change moves
            // them together and an accidental one reds here.
            float slack = GuiTreeAssembler.LayoutGroupContainmentSlackPx;
            var tolerated = new List<GuiTreeEvent>
            {
                BeginLayoutGroup(false, 100, 100, 100, 100, 0, "group"),
                Leaf(GuiNodeKind.Label, 100 - (slack - 1f), 100, 40, 18, 0, "just-inside"),
            };
            var closing = new List<GuiTreeEvent>
            {
                BeginLayoutGroup(false, 100, 100, 100, 100, 0, "group"),
                Leaf(GuiNodeKind.Label, 100 - (slack + 1f), 100, 40, 18, 0, "just-outside"),
            };

            Assert.Equal(0, GuiTreeAssembler.Assemble(tolerated).AutoClosedByRect);
            Assert.Equal(1, GuiTreeAssembler.Assemble(closing).AutoClosedByRect);
        }

        [Theory]
        [InlineData(0.6f, 0.6f, false)]
        [InlineData(0.5f, 10f, true)]
        [InlineData(10f, 0.5f, true)]
        [InlineData(0.51f, 0.51f, false)]
        [InlineData(0f, 0f, true)]
        public void DegeneracyIsDecidedAtExactlyHalfAPixel(float w, float h, bool degenerate)
        {
            // The 0.5 threshold is a CONTRACT shared with the Python viewer, whose
            // rect_of() drops a box on the same edge (test_gui_tree_view.py pins the
            // other side of it). A rect exactly 0.5 px wide is degenerate; 0.51 is not.
            Assert.Equal(degenerate, new GuiRect(0f, 0f, w, h).IsDegenerate);
        }

        [Fact]
        public void TheClipContainerPredicateHasExactlyOneDefinition()
        {
            // GuiTreeNode.IsClipContainer and the assembler's IsClipKind used to be two
            // copies of the same list, and the asymmetric clip rule compares one against
            // the other - so a kind added to one copy and not the other makes the rule
            // silently disagree with itself. One definition now; this pins it.
            foreach (GuiNodeKind kind in Enum.GetValues(typeof(GuiNodeKind)))
            {
                var node = new GuiTreeNode { Kind = kind };
                Assert.Equal(GuiTreeAssembler.IsClipKind(kind), node.IsClipContainer);
            }
            Assert.True(GuiTreeAssembler.IsClipKind(GuiNodeKind.Window));
            Assert.True(GuiTreeAssembler.IsClipKind(GuiNodeKind.Group));
            Assert.True(GuiTreeAssembler.IsClipKind(GuiNodeKind.ScrollView));
            Assert.False(GuiTreeAssembler.IsClipKind(GuiNodeKind.LayoutGroup));
        }

        private static GuiTreeEvent BeginLayoutGroup(bool horizontal, float x, float y,
            float w, float h, int clipDepth, string text = null)
        {
            GuiTreeEvent e = Begin(GuiNodeKind.LayoutGroup, x, y, w, h, clipDepth, text);
            e.Horizontal = horizontal;
            return e;
        }

        private static GuiTreeEvent EndLayoutGroup(bool? horizontal)
        {
            return new GuiTreeEvent
            {
                Op = GuiTreeOp.End,
                Kind = GuiNodeKind.LayoutGroup,
                Horizontal = horizontal,
            };
        }
    }
}
