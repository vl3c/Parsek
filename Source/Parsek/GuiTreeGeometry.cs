using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>What <see cref="GuiTreeGeometry.Inspect"/> measured.</summary>
    internal sealed class GuiTreeGeometryReport
    {
        internal bool WindowFound;
        internal GuiRect WindowRect;

        /// <summary>Leaves under the window whose text carries the caller's marker.</summary>
        internal int ProbeControlsFound;

        /// <summary>Marked leaves whose screen rect origin fell outside the window.</summary>
        internal int ControlsOutsideWindow;

        /// <summary>Marked leaves that recorded no drawable area.</summary>
        internal int ControlsWithDegenerateRect;

        /// <summary>Text + rect of the first offender, for the failure message.</summary>
        internal string FirstOffender = "none";

        /// <summary>Deepest nesting under the window, counting the window itself as 1.</summary>
        internal int MaxDepth;

        internal string DescribeWindowRect()
        {
            if (!WindowFound)
                return "<not found>";
            return "[" + F(WindowRect.X) + "," + F(WindowRect.Y) + ","
                + F(WindowRect.W) + "," + F(WindowRect.H) + "]";
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
        /// overhang its window by a border's worth (styles draw outside their rect), and
        /// the window rect itself is the frame rather than the content area.
        /// </summary>
        internal const float ContainmentSlackPx = 8f;

        internal static GuiTreeGeometryReport Inspect(GuiTreeResult tree, string windowTitle,
            string controlTextMarker)
        {
            var report = new GuiTreeGeometryReport();
            if (tree == null)
                return report;

            GuiTreeNode window = FindWindow(tree.Roots, windowTitle);
            if (window == null)
                return report;

            report.WindowFound = true;
            report.WindowRect = window.Rect;
            Walk(window, 1, window.Rect, controlTextMarker, report);
            return report;
        }

        private static GuiTreeNode FindWindow(List<GuiTreeNode> nodes, string title)
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
                GuiTreeNode nested = FindWindow(n.Children, title);
                if (nested != null)
                    return nested;
            }
            return null;
        }

        private static void Walk(GuiTreeNode node, int depth, GuiRect windowRect,
            string marker, GuiTreeGeometryReport report)
        {
            if (depth > report.MaxDepth)
                report.MaxDepth = depth;

            if (depth > 1 && IsMarked(node, marker))
            {
                report.ProbeControlsFound++;
                if (node.Rect.IsDegenerate)
                {
                    report.ControlsWithDegenerateRect++;
                    NoteOffender(report, node, "degenerate");
                }
                else if (!windowRect.ContainsWithSlack(node.Rect.X, node.Rect.Y, ContainmentSlackPx)
                    || !windowRect.ContainsWithSlack(
                        node.Rect.X + node.Rect.W, node.Rect.Y + node.Rect.H, ContainmentSlackPx))
                {
                    report.ControlsOutsideWindow++;
                    NoteOffender(report, node, "outside");
                }
            }

            for (int i = 0; i < node.Children.Count; i++)
            {
                GuiTreeNode child = node.Children[i];
                if (child != null)
                    Walk(child, depth + 1, windowRect, marker, report);
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
