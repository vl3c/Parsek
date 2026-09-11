using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Rectangle carried by the GUI-tree event stream. Deliberately NOT
    /// <c>UnityEngine.Rect</c>: everything in this file has to be reachable from the
    /// headless xUnit host, and the pure layer must stay free of Unity types so the
    /// assembler and the JSON writer can be driven from unit tests with no engine.
    /// The recorder converts at the seam (<see cref="GuiTreeRecorder"/>).
    /// </summary>
    internal struct GuiRect
    {
        internal float X;
        internal float Y;
        internal float W;
        internal float H;

        internal GuiRect(float x, float y, float w, float h)
        {
            X = x;
            Y = y;
            W = w;
            H = h;
        }

        /// <summary>True when the rect has no area worth drawing a box around.</summary>
        internal bool IsDegenerate
        {
            get { return !(W > 0.5f) || !(H > 0.5f); }
        }

        /// <summary>
        /// Containment of a point with a slack margin. Used by the LayoutGroup recovery
        /// rule, which has to tolerate the sub-pixel rounding GUILayout applies to group
        /// rects versus the child rects it hands out.
        /// </summary>
        internal bool ContainsWithSlack(float px, float py, float slack)
        {
            return px >= X - slack && py >= Y - slack
                && px <= X + W + slack && py <= Y + H + slack;
        }
    }

    /// <summary>What the event does to the tree.</summary>
    internal enum GuiTreeOp
    {
        Begin,
        Leaf,
        End,
    }

    /// <summary>
    /// Control / container kind. <see cref="Control"/> is the honest fallback for a
    /// <c>GUI.DoControl</c> hit whose caller-identity hint never arrived (see the
    /// inlining discussion in <c>docs/dev/design-gui-tree-dump.md</c>).
    /// </summary>
    internal enum GuiNodeKind
    {
        Window,
        Group,
        ScrollView,
        LayoutGroup,
        Label,
        Box,
        Button,
        RepeatButton,
        Toggle,
        TextField,
        ButtonGrid,
        Slider,
        Control,
    }

    /// <summary>
    /// One record in the flat stream the Harmony patches push. The stream is the ONLY
    /// thing that crosses from the Unity side into the pure layer.
    /// </summary>
    internal sealed class GuiTreeEvent
    {
        internal GuiTreeOp Op;
        internal GuiNodeKind Kind;

        /// <summary>Screen-space rect (post <c>GUIUtility.GUIToScreenRect</c>).</summary>
        internal GuiRect Rect;

        /// <summary>Rect exactly as the funnel received it, before any conversion.</summary>
        internal GuiRect LocalRect;

        /// <summary>
        /// The clip-stack depth measured at record time. For a clip-pushing Begin
        /// (Window / Group / ScrollView) this is the depth its DIRECT CHILDREN report,
        /// because the recorder takes those from a Harmony POSTFIX - i.e. after Unity
        /// pushed the clip - rather than normalising the number afterwards.
        /// A negative value means "unknown" (the depth probe was unavailable) and the
        /// clip-based recovery rule is then skipped for that event.
        /// </summary>
        internal int ClipDepth = -1;

        internal string Text;
        internal string Tooltip;
        internal string StyleName;
        internal bool Enabled = true;

        internal bool? ToggleValue;
        internal string TextValue;
        internal int? ControlId;
        internal int? WindowId;

        /// <summary>Layout-group orientation: true horizontal, false vertical.</summary>
        internal bool? Horizontal;

        /// <summary>
        /// Window only: <c>GUIUtility.GUIToScreenPoint(Vector2.zero)</c> measured inside
        /// the window callback. An INDEPENDENT reading of where the window's content
        /// origin actually landed, kept so a screen-space conversion defect shows up as a
        /// disagreement with <see cref="Rect"/> rather than as a silently wrong overlay.
        /// </summary>
        internal float? ContentOriginX;
        internal float? ContentOriginY;

        /// <summary>Window only: the width/height Unity handed the window callback.</summary>
        internal float? ArgWidth;
        internal float? ArgHeight;
    }

    /// <summary>One assembled node. Children are in draw order.</summary>
    internal sealed class GuiTreeNode
    {
        internal GuiNodeKind Kind;
        internal GuiRect Rect;
        internal GuiRect LocalRect;
        internal int ClipDepth = -1;
        internal string Text;
        internal string Tooltip;
        internal string StyleName;
        internal bool Enabled = true;
        internal bool? ToggleValue;
        internal string TextValue;
        internal int? ControlId;
        internal int? WindowId;
        internal bool? Horizontal;
        internal float? ContentOriginX;
        internal float? ContentOriginY;
        internal float? ArgWidth;
        internal float? ArgHeight;
        internal readonly List<GuiTreeNode> Children = new List<GuiTreeNode>();

        /// <summary>
        /// True for a node that pushed a Unity clip rect, so the clip-depth recovery rule
        /// applies to it. LayoutGroup pushes none. ONE definition, shared with the
        /// event-side check: <see cref="GuiTreeAssembler.IsClipKind"/>. Two copies of this
        /// predicate is how the asymmetric clip rule silently stops agreeing with itself.
        /// </summary>
        internal bool IsClipContainer
        {
            get { return GuiTreeAssembler.IsClipKind(Kind); }
        }
    }

    /// <summary>Assembly outcome plus the repair counters the JSON reports.</summary>
    internal sealed class GuiTreeResult
    {
        internal readonly List<GuiTreeNode> Roots = new List<GuiTreeNode>();
        internal int NodeCount;
        internal int WindowCount;
        internal int EventCount;

        /// <summary>End events that matched no open container. Should be zero.</summary>
        internal int StrayEnds;

        /// <summary>Containers closed because the clip depth said they had to be.</summary>
        internal int AutoClosedByClip;

        /// <summary>
        /// Containers closed because an End for an OUTER container arrived first
        /// (an unbalanced Begin somewhere inside).
        /// </summary>
        internal int AutoClosedByEnd;

        /// <summary>LayoutGroups closed because the next rect fell outside them.</summary>
        internal int AutoClosedByRect;

        /// <summary>
        /// Events at which the rect rule could not be applied at all, because every open
        /// LayoutGroup down to the enclosing clip container had a degenerate rect. Not an
        /// error - a zero-size carrier group is ordinary GUILayout - but it means the
        /// layout nesting at that point rests on the End pairing alone, with no
        /// independent check behind it. The viewer reports a non-zero count as "layout
        /// nesting unverified".
        /// </summary>
        internal int RectRuleInert;

        /// <summary>Containers still open when the stream ended.</summary>
        internal int UnclosedAtEnd;
    }

    /// <summary>
    /// Pure event-stream -> nested-tree assembler. No Unity, no I/O, no statics.
    ///
    /// <para><b>Why the recovery rules exist.</b> The Begin/End pairing is produced by
    /// Harmony patches on individual UnityEngine methods, and the End side of several
    /// pairs is a two-instruction method that Mono is free to inline into its caller,
    /// which would silently bypass the patch. Rather than trust the pairing, the
    /// assembler treats a missing End as normal and repairs it from two independent
    /// signals: the clip-stack depth carried on every event (authoritative for
    /// Window / Group / ScrollView) and rect containment (best effort for
    /// LayoutGroup, which pushes no clip). Every repair is counted so a reader of the
    /// JSON can tell a clean capture from a patched-but-inlined one.</para>
    /// </summary>
    internal static class GuiTreeAssembler
    {
        /// <summary>
        /// Slack for the LayoutGroup rect rule, in pixels. GUILayout rounds group rects
        /// and child rects independently; a couple of pixels of disagreement is normal
        /// and must not close a group that is genuinely still open.
        /// </summary>
        internal const float LayoutGroupContainmentSlackPx = 4f;

        internal static GuiTreeResult Assemble(IList<GuiTreeEvent> events)
        {
            var result = new GuiTreeResult();
            if (events == null)
                return result;

            var open = new List<GuiTreeNode>();

            for (int i = 0; i < events.Count; i++)
            {
                GuiTreeEvent e = events[i];
                if (e == null)
                    continue;
                result.EventCount++;

                if (e.Op == GuiTreeOp.End)
                {
                    CloseMatching(open, e, result);
                    continue;
                }

                CloseByClipDepth(open, e, result);
                CloseByRectContainment(open, e, result);

                GuiTreeNode node = ToNode(e);
                Append(open, result, node);
                result.NodeCount++;
                if (node.Kind == GuiNodeKind.Window)
                    result.WindowCount++;

                if (e.Op == GuiTreeOp.Begin)
                    open.Add(node);
            }

            result.UnclosedAtEnd += open.Count;
            return result;
        }

        private static void Append(List<GuiTreeNode> open, GuiTreeResult result, GuiTreeNode node)
        {
            if (open.Count == 0)
                result.Roots.Add(node);
            else
                open[open.Count - 1].Children.Add(node);
        }

        /// <summary>
        /// Pops down to and including the NEAREST open node of the End's kind. Anything
        /// closed on the way there was left open by its own producer and is counted in
        /// <see cref="GuiTreeResult.AutoClosedByEnd"/>. An End with no match closes
        /// nothing at all - never guess, or one stray End collapses the tree.
        ///
        /// <para>A LayoutGroup End additionally carries its own ORIENTATION (the recorder
        /// pops it off its Begin stack, because <c>GUILayoutUtility.EndLayoutGroup</c>
        /// takes no arguments). Matching on it means a stranded horizontal group cannot
        /// swallow the End of the vertical group that encloses it - it is skipped, the
        /// vertical one matches, and the stranded group is counted as
        /// <c>autoClosedByEnd</c> where it belongs. An End with NO orientation (the
        /// recorder's stack was empty) matches either, which is the older behaviour.</para>
        /// </summary>
        private static void CloseMatching(List<GuiTreeNode> open, GuiTreeEvent e, GuiTreeResult result)
        {
            int match = -1;
            for (int i = open.Count - 1; i >= 0; i--)
            {
                if (open[i].Kind != e.Kind)
                    continue;
                if (e.Kind == GuiNodeKind.LayoutGroup
                    && e.Horizontal.HasValue && open[i].Horizontal.HasValue
                    && open[i].Horizontal.Value != e.Horizontal.Value)
                {
                    continue;
                }
                match = i;
                break;
            }

            if (match < 0)
            {
                result.StrayEnds++;
                return;
            }

            result.AutoClosedByEnd += open.Count - 1 - match;
            open.RemoveRange(match, open.Count - match);
        }

        /// <summary>
        /// True for the kinds that push a Unity clip rect, and whose Begin event
        /// therefore carries the depth their CHILDREN report rather than the depth they
        /// were opened at.
        /// </summary>
        internal static bool IsClipKind(GuiNodeKind kind)
        {
            return kind == GuiNodeKind.Window
                || kind == GuiNodeKind.Group
                || kind == GuiNodeKind.ScrollView;
        }

        private static void CloseByClipDepth(List<GuiTreeNode> open, GuiTreeEvent e, GuiTreeResult result)
        {
            if (e.ClipDepth < 0)
                return;

            // THE ASYMMETRY, and it is the whole reason this rule works without a
            // reliable End. Both an open clip container's ClipDepth and a clip-container
            // Begin's ClipDepth are CHILD depths, so two SIBLING containers carry the
            // SAME number and a nested one carries a strictly greater one - a sibling
            // must therefore close the open one at EQUAL depth. A leaf, or a LayoutGroup
            // Begin, carries the depth it was drawn AT, which equals its parent
            // container's child depth - so for those, equal means "still inside" and only
            // a strictly smaller depth closes. Collapsing the two into one comparison
            // either loses every sibling group whose End was inlined, or evicts every
            // leaf from the container it belongs to.
            bool siblingClosesAtEqualDepth = e.Op == GuiTreeOp.Begin && IsClipKind(e.Kind);

            while (open.Count > 0)
            {
                GuiTreeNode top = open[open.Count - 1];
                if (!top.IsClipContainer)
                {
                    // A LayoutGroup sitting above a clip container: it can only be closed
                    // once we know the container under it must close, so look past it.
                    int deepestClip = -1;
                    for (int i = open.Count - 1; i >= 0; i--)
                    {
                        if (open[i].IsClipContainer)
                        {
                            deepestClip = i;
                            break;
                        }
                    }
                    if (deepestClip < 0)
                        return;
                    if (!ClipDepthForcesClose(open[deepestClip], e, siblingClosesAtEqualDepth))
                        return;
                    result.AutoClosedByClip += open.Count - deepestClip;
                    open.RemoveRange(deepestClip, open.Count - deepestClip);
                    continue;
                }

                if (!ClipDepthForcesClose(top, e, siblingClosesAtEqualDepth))
                    return;
                result.AutoClosedByClip++;
                open.RemoveAt(open.Count - 1);
            }
        }

        private static bool ClipDepthForcesClose(GuiTreeNode container, GuiTreeEvent e,
            bool siblingClosesAtEqualDepth)
        {
            if (container.ClipDepth < 0)
                return false;
            return siblingClosesAtEqualDepth
                ? e.ClipDepth <= container.ClipDepth
                : e.ClipDepth < container.ClipDepth;
        }

        /// <summary>
        /// The LayoutGroup recovery rule: a group closes when the next event's rect falls
        /// outside it by more than <see cref="LayoutGroupContainmentSlackPx"/>.
        ///
        /// <para>A DEGENERATE top group used to stop the rule dead, which loses the whole
        /// check whenever GUILayout puts a zero-size carrier on top (a spacer, a group
        /// that ended up empty). It now LOOKS PAST it to the nearest enclosing LayoutGroup
        /// with a usable rect, mirroring the clip rule's lookback, and closes the whole run
        /// down to that group when the incoming rect is outside IT. The search stops at the
        /// enclosing clip container, whose own containment is the clip rule's business.
        /// When no usable rect exists at all, the event is counted in
        /// <see cref="GuiTreeResult.RectRuleInert"/> - the nesting there rests on the End
        /// pairing alone and the reader is told so.</para>
        /// </summary>
        private static void CloseByRectContainment(List<GuiTreeNode> open, GuiTreeEvent e, GuiTreeResult result)
        {
            if (e.Rect.IsDegenerate)
                return;

            while (open.Count > 0)
            {
                GuiTreeNode top = open[open.Count - 1];
                if (top.Kind != GuiNodeKind.LayoutGroup)
                    return;

                if (top.Rect.IsDegenerate)
                {
                    int usable = -1;
                    for (int i = open.Count - 1; i >= 0; i--)
                    {
                        if (open[i].Kind != GuiNodeKind.LayoutGroup)
                            break;
                        if (!open[i].Rect.IsDegenerate)
                        {
                            usable = i;
                            break;
                        }
                    }
                    if (usable < 0)
                    {
                        result.RectRuleInert++;
                        return;
                    }
                    if (open[usable].Rect.ContainsWithSlack(
                            e.Rect.X, e.Rect.Y, LayoutGroupContainmentSlackPx))
                    {
                        return;
                    }
                    result.AutoClosedByRect += open.Count - usable;
                    open.RemoveRange(usable, open.Count - usable);
                    continue;
                }

                if (top.Rect.ContainsWithSlack(e.Rect.X, e.Rect.Y, LayoutGroupContainmentSlackPx))
                    return;
                result.AutoClosedByRect++;
                open.RemoveAt(open.Count - 1);
            }
        }

        private static GuiTreeNode ToNode(GuiTreeEvent e)
        {
            return new GuiTreeNode
            {
                Kind = e.Kind,
                Rect = e.Rect,
                LocalRect = e.LocalRect,
                ClipDepth = e.ClipDepth,
                Text = e.Text,
                Tooltip = e.Tooltip,
                StyleName = e.StyleName,
                Enabled = e.Enabled,
                ToggleValue = e.ToggleValue,
                TextValue = e.TextValue,
                ControlId = e.ControlId,
                WindowId = e.WindowId,
                Horizontal = e.Horizontal,
                ContentOriginX = e.ContentOriginX,
                ContentOriginY = e.ContentOriginY,
                ArgWidth = e.ArgWidth,
                ArgHeight = e.ArgHeight,
            };
        }

        /// <summary>
        /// Lower-case wire name for a kind. Pinned: the offline viewer and any agent
        /// reading the JSON key off these strings, so they are a contract, not a
        /// <c>ToString()</c>.
        /// </summary>
        internal static string KindName(GuiNodeKind kind)
        {
            switch (kind)
            {
                case GuiNodeKind.Window: return "window";
                case GuiNodeKind.Group: return "group";
                case GuiNodeKind.ScrollView: return "scrollview";
                case GuiNodeKind.LayoutGroup: return "layoutgroup";
                case GuiNodeKind.Label: return "label";
                case GuiNodeKind.Box: return "box";
                case GuiNodeKind.Button: return "button";
                case GuiNodeKind.RepeatButton: return "repeatbutton";
                case GuiNodeKind.Toggle: return "toggle";
                case GuiNodeKind.TextField: return "textfield";
                case GuiNodeKind.ButtonGrid: return "buttongrid";
                case GuiNodeKind.Slider: return "slider";
                case GuiNodeKind.Control: return "control";
                default: return "unknown";
            }
        }

        /// <summary>
        /// Best-effort kind for a <c>GUI.DoControl</c> hit whose caller hint is missing,
        /// derived from the style name Unity was drawing with. Stock skin names are the
        /// only reliable signal; a Parsek custom style falls through to
        /// <see cref="GuiNodeKind.Control"/> rather than guessing.
        /// </summary>
        internal static GuiNodeKind ClassifyFromStyleName(string styleName, bool on)
        {
            if (!string.IsNullOrEmpty(styleName))
            {
                if (styleName.IndexOf("toggle", StringComparison.OrdinalIgnoreCase) >= 0)
                    return GuiNodeKind.Toggle;
                if (styleName.IndexOf("button", StringComparison.OrdinalIgnoreCase) >= 0)
                    return on ? GuiNodeKind.Toggle : GuiNodeKind.Button;
            }
            // `on == true` cannot come out of a button: GUI.Button hard-codes on:false.
            return on ? GuiNodeKind.Toggle : GuiNodeKind.Control;
        }
    }
}
