using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>What <see cref="GuiTreeGeometry.Inspect"/> measured.</summary>
    internal sealed class GuiTreeGeometryReport
    {
        internal bool WindowFound;

        /// <summary>
        /// The window's DECLARED rect, as <c>GUI.DoWindow</c> received it. The SECOND
        /// reading, kept for comparison - it is one frame stale while a window is being
        /// dragged, and it is unconverted, which is exactly why it is not the box.
        /// </summary>
        internal GuiRect WindowRect;

        /// <summary>
        /// The containment box actually used: <c>contentOrigin</c> + <c>argSize</c>, both
        /// measured INSIDE the window callback of the frame being captured
        /// (<see cref="ContentBoxMeasured"/> true), else the declared rect as a fallback.
        /// </summary>
        internal GuiRect ContainmentBox;

        /// <summary>True when <see cref="ContainmentBox"/> came from the measured pair.</summary>
        internal bool ContentBoxMeasured;

        /// <summary>Leaves under the window whose text carries the caller's marker.</summary>
        internal int ProbeControlsFound;

        /// <summary>Marked leaves whose screen rect fell outside the containment box.</summary>
        internal int ControlsOutsideWindow;

        /// <summary>Marked leaves that recorded no drawable area.</summary>
        internal int ControlsWithDegenerateRect;

        /// <summary>Text + rect of the first offender, for the failure message.</summary>
        internal string FirstOffender = "none";

        /// <summary>Deepest nesting under the window, counting the window itself as 1.</summary>
        internal int MaxDepth;

        /// <summary>
        /// Node count per kind in the window's SUBTREE, the window itself excluded. This
        /// is the SCOPED instrument: per-funnel hit counters are process-wide (every
        /// window on screen during the armed frame feeds them, and during an in-game batch
        /// the Test Runner window is one of them), so an EXACT expectation can only be
        /// stated over one window's own subtree.
        /// </summary>
        internal readonly int[] KindCounts =
            new int[Enum.GetValues(typeof(GuiNodeKind)).Length];

        internal int CountOf(GuiNodeKind kind)
        {
            int i = (int)kind;
            return i >= 0 && i < KindCounts.Length ? KindCounts[i] : 0;
        }

        /// <summary>Every non-zero kind count, for a failure message that can be acted on.</summary>
        internal string DescribeKindCounts()
        {
            var parts = new List<string>();
            for (int i = 0; i < KindCounts.Length; i++)
            {
                if (KindCounts[i] > 0)
                {
                    parts.Add(GuiTreeAssembler.KindName((GuiNodeKind)i) + "="
                        + KindCounts[i].ToString(CultureInfo.InvariantCulture));
                }
            }
            return parts.Count == 0 ? "<empty>" : string.Join(" ", parts.ToArray());
        }

        internal string DescribeWindowRect()
        {
            return WindowFound ? Describe(WindowRect) : "<not found>";
        }

        internal string DescribeContainmentBox()
        {
            if (!WindowFound)
                return "<not found>";
            return Describe(ContainmentBox)
                + (ContentBoxMeasured ? " (measured contentOrigin+argSize)" : " (declared rect)");
        }

        private static string Describe(GuiRect r)
        {
            return "[" + Fmt(r.X) + "," + Fmt(r.Y) + "," + Fmt(r.W) + "," + Fmt(r.H) + "]";
        }

        private static string Fmt(float v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>What <see cref="GuiTreeGeometry.MeasureScrollOffset"/> measured.</summary>
    internal sealed class GuiTreeScrollOffsetReport
    {
        internal bool ScrollViewFound;
        internal bool RowFound;
        internal GuiRect ScrollViewRect;
        internal GuiRect RowRect;

        /// <summary>
        /// How far ABOVE the scroll view's own top the row was drawn, in screen pixels.
        /// A scroll view scrolled down by N pixels draws its first row N pixels above its
        /// viewport, clipped away - which is the only observable proof that the scroll
        /// offset a scroll view pushes onto the clip stack reached the recorder's screen
        /// conversion at all.
        /// </summary>
        internal float OffsetAbovePx;

        internal string Describe()
        {
            if (!ScrollViewFound)
                return "<no scroll view under the window>";
            if (!RowFound)
            {
                return "<no marked row under the scroll view> scrollView=["
                    + F(ScrollViewRect.X) + "," + F(ScrollViewRect.Y) + ","
                    + F(ScrollViewRect.W) + "," + F(ScrollViewRect.H) + "]";
            }
            return "row.y=" + F(RowRect.Y) + " scrollView.y=" + F(ScrollViewRect.Y)
                + " offsetAbove=" + F(OffsetAbovePx);
        }

        private static string F(float v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Pure geometry checks over an assembled GUI tree. Exists so the live in-game cell
    /// asserts against a checked derivation rather than string-matching a JSON blob, and
    /// so the derivation itself is unit-testable headlessly.
    ///
    /// <para>The load-bearing question it answers is the screen-space one: a marked
    /// control whose rect falls OUTSIDE its own window is proof that
    /// <c>GUIUtility.GUIToScreenRect</c> does not convert the way the recorder assumes
    /// inside a <c>GUI.Window</c> callback - the one thing about this design that cannot
    /// be settled by reading the decompiled source, because the conversion bottoms out in
    /// a native ICall.</para>
    /// </summary>
    internal static class GuiTreeGeometry
    {
        /// <summary>
        /// Pixels of tolerance on the containment test. A control may legitimately
        /// overhang by a border's worth (styles draw outside their rect), and GUILayout
        /// rounds a group's rect independently of the child rects it hands out.
        /// </summary>
        internal const float ContainmentSlackPx = 8f;

        internal static GuiTreeGeometryReport Inspect(GuiTreeResult tree, string windowTitle,
            string controlTextMarker)
        {
            return Inspect(tree, null, windowTitle, controlTextMarker);
        }

        /// <summary>
        /// <see cref="Inspect(GuiTreeResult, string, string)"/> keyed on the WINDOW ID
        /// first, with the title as the secondary key.
        ///
        /// <para><b>Why the id has to be the primary key.</b> A window node's
        /// <c>Text</c> is written by exactly one funnel - <c>GUI.DoWindow</c>, 26 bytes of
        /// IL and squarely inside Mono's inline limit. The NODE comes from
        /// <c>GUI.CallWindowDelegate</c>, which is <c>[RequiredByNativeCode]</c> and cannot
        /// be inlined at all, and the node it builds when no declaration arrived carries
        /// the window ID but NO title. A title-only lookup therefore turns an inlined
        /// 26-byte declaration funnel into "the window is not in the dump at all", which
        /// reads as the interception having failed when it did not. The id is on both
        /// paths.</para>
        /// </summary>
        internal static GuiTreeGeometryReport Inspect(GuiTreeResult tree, int? windowId,
            string windowTitle, string controlTextMarker)
        {
            var report = new GuiTreeGeometryReport();
            if (tree == null)
                return report;

            GuiTreeNode window = FindWindow(tree.Roots, windowId, windowTitle);
            if (window == null)
                return report;

            report.WindowFound = true;
            report.WindowRect = window.Rect;
            // The containment box is the pair MEASURED INSIDE the callback, not the
            // declared rect: the declaration is the rect the caller passed to
            // GUILayout.Window BEFORE this frame moved it (one frame stale for the whole
            // duration of a drag) and it is never screen-converted. contentOrigin is a
            // GUIToScreenPoint(Vector2.zero) taken inside the same callback the children
            // were drawn in, and argSize is the size Unity handed that callback, so the
            // two together are the frame the children actually live in.
            if (window.ContentOriginX.HasValue && window.ContentOriginY.HasValue
                && window.ArgWidth.HasValue && window.ArgHeight.HasValue)
            {
                report.ContentBoxMeasured = true;
                report.ContainmentBox = new GuiRect(
                    window.ContentOriginX.Value, window.ContentOriginY.Value,
                    window.ArgWidth.Value, window.ArgHeight.Value);
            }
            else
            {
                report.ContainmentBox = window.Rect;
            }

            Walk(window, 1, report.ContainmentBox, controlTextMarker, report);
            return report;
        }

        /// <summary>
        /// The scroll-offset reading: the marked row's screen y against the scroll view's
        /// own screen y, both taken from the assembled tree.
        /// </summary>
        internal static GuiTreeScrollOffsetReport MeasureScrollOffset(GuiTreeResult tree,
            string windowTitle, string rowText)
        {
            return MeasureScrollOffset(tree, null, windowTitle, rowText);
        }

        /// <summary>
        /// The scroll-offset reading, keyed on the window ID first for the same reason
        /// <see cref="Inspect(GuiTreeResult, int?, string, string)"/> is.
        /// </summary>
        internal static GuiTreeScrollOffsetReport MeasureScrollOffset(GuiTreeResult tree,
            int? windowId, string windowTitle, string rowText)
        {
            var report = new GuiTreeScrollOffsetReport();
            if (tree == null)
                return report;
            GuiTreeNode window = FindWindow(tree.Roots, windowId, windowTitle);
            if (window == null)
                return report;
            GuiTreeNode scrollView = FindKind(window, GuiNodeKind.ScrollView);
            if (scrollView == null)
                return report;

            report.ScrollViewFound = true;
            report.ScrollViewRect = scrollView.Rect;
            GuiTreeNode row = FindByText(scrollView, rowText);
            if (row == null)
                return report;
            report.RowFound = true;
            report.RowRect = row.Rect;
            report.OffsetAbovePx = scrollView.Rect.Y - row.Rect.Y;
            return report;
        }

        /// <summary>
        /// The window, by id when one is given and matches, else by title. TWO passes on
        /// purpose: an id pass that ignores the title (the fallback node from
        /// <c>CallWindowDelegate</c> has no title at all), then the original title pass.
        /// A window found by id but bearing a different title is still the right window -
        /// the id is the launch-independent handle, the title is a label.
        /// </summary>
        private static GuiTreeNode FindWindow(List<GuiTreeNode> nodes, int? windowId, string title)
        {
            if (windowId.HasValue)
            {
                GuiTreeNode byId = FindWindowById(nodes, windowId.Value);
                if (byId != null)
                    return byId;
            }
            return FindWindowByTitle(nodes, title);
        }

        private static GuiTreeNode FindWindowById(List<GuiTreeNode> nodes, int windowId)
        {
            if (nodes == null)
                return null;
            for (int i = 0; i < nodes.Count; i++)
            {
                GuiTreeNode n = nodes[i];
                if (n == null)
                    continue;
                if (n.Kind == GuiNodeKind.Window && n.WindowId.HasValue
                    && n.WindowId.Value == windowId)
                {
                    return n;
                }
                GuiTreeNode nested = FindWindowById(n.Children, windowId);
                if (nested != null)
                    return nested;
            }
            return null;
        }

        private static GuiTreeNode FindWindowByTitle(List<GuiTreeNode> nodes, string title)
        {
            if (nodes == null)
                return null;
            for (int i = 0; i < nodes.Count; i++)
            {
                GuiTreeNode n = nodes[i];
                if (n == null)
                    continue;
                if (n.Kind == GuiNodeKind.Window
                    && (title == null || string.Equals(n.Text, title, StringComparison.Ordinal)))
                {
                    return n;
                }
                // Recursive on purpose: a window is not necessarily a root. An OnGUI
                // container that opens a group or a layout group before drawing its
                // window puts the window BELOW the roots, and the nested-window case
                // (which Parsek does not draw today, but Unity permits) puts it deeper.
                GuiTreeNode nested = FindWindowByTitle(n.Children, title);
                if (nested != null)
                    return nested;
            }
            return null;
        }

        private static GuiTreeNode FindKind(GuiTreeNode node, GuiNodeKind kind)
        {
            for (int i = 0; i < node.Children.Count; i++)
            {
                GuiTreeNode child = node.Children[i];
                if (child == null)
                    continue;
                if (child.Kind == kind)
                    return child;
                GuiTreeNode nested = FindKind(child, kind);
                if (nested != null)
                    return nested;
            }
            return null;
        }

        private static GuiTreeNode FindByText(GuiTreeNode node, string text)
        {
            for (int i = 0; i < node.Children.Count; i++)
            {
                GuiTreeNode child = node.Children[i];
                if (child == null)
                    continue;
                if (string.Equals(child.Text, text, StringComparison.Ordinal))
                    return child;
                GuiTreeNode nested = FindByText(child, text);
                if (nested != null)
                    return nested;
            }
            return null;
        }

        private static void Walk(GuiTreeNode node, int depth, GuiRect box,
            string marker, GuiTreeGeometryReport report)
        {
            if (depth > report.MaxDepth)
                report.MaxDepth = depth;

            if (depth > 1)
            {
                int kind = (int)node.Kind;
                if (kind >= 0 && kind < report.KindCounts.Length)
                    report.KindCounts[kind]++;

                if (IsMarked(node, marker))
                {
                    report.ProbeControlsFound++;
                    if (node.Rect.IsDegenerate)
                    {
                        report.ControlsWithDegenerateRect++;
                        NoteOffender(report, node, "degenerate");
                    }
                    else if (!box.ContainsWithSlack(node.Rect.X, node.Rect.Y, ContainmentSlackPx)
                        || !box.ContainsWithSlack(
                            node.Rect.X + node.Rect.W, node.Rect.Y + node.Rect.H, ContainmentSlackPx))
                    {
                        report.ControlsOutsideWindow++;
                        NoteOffender(report, node, "outside");
                    }
                }
            }

            for (int i = 0; i < node.Children.Count; i++)
            {
                GuiTreeNode child = node.Children[i];
                if (child != null)
                    Walk(child, depth + 1, box, marker, report);
            }
        }

        private static bool IsMarked(GuiTreeNode node, string marker)
        {
            if (string.IsNullOrEmpty(marker))
                return false;
            if (node.Text != null && node.Text.IndexOf(marker, StringComparison.Ordinal) >= 0)
                return true;
            return node.TextValue != null
                && node.TextValue.IndexOf(marker, StringComparison.Ordinal) >= 0;
        }

        private static void NoteOffender(GuiTreeGeometryReport report, GuiTreeNode node, string why)
        {
            if (report.FirstOffender != "none")
                return;
            report.FirstOffender = why + " "
                + GuiTreeAssembler.KindName(node.Kind) + " \"" + (node.Text ?? node.TextValue ?? "")
                + "\" rect=[" + Fmt(node.Rect.X) + "," + Fmt(node.Rect.Y) + ","
                + Fmt(node.Rect.W) + "," + Fmt(node.Rect.H) + "]"
                + " localRect=[" + Fmt(node.LocalRect.X) + "," + Fmt(node.LocalRect.Y) + ","
                + Fmt(node.LocalRect.W) + "," + Fmt(node.LocalRect.H) + "]";
        }

        private static string Fmt(float v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
