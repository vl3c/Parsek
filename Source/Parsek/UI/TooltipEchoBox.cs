using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Which style the tooltip echo strip paints with this frame. Pure enum so the
    /// branch decision can be unit-tested without Unity.
    /// </summary>
    internal enum TooltipEchoStyleKind
    {
        /// <summary>Collapsed strip: an empty label forced to zero height.</summary>
        ZeroHeightLabel,

        /// <summary>Visible strip: the house box style with word wrap on.</summary>
        WrappedBox
    }

    /// <summary>
    /// Which single GUILayoutOption the strip's label is given. Exactly one option is
    /// always passed, so the emitted control shape never varies in arity.
    /// </summary>
    internal enum TooltipEchoWidthOption
    {
        /// <summary>Collapsed strip: GUILayout.Height(0f).</summary>
        ZeroHeight,

        /// <summary>Visible strip: GUILayout.ExpandWidth(true).</summary>
        ExpandWidth
    }

    /// <summary>
    /// The full per-frame decision for one tooltip echo strip. Pure data, produced by
    /// <see cref="TooltipEchoBox.ResolveEchoLayout"/>.
    /// </summary>
    internal struct TooltipEchoLayout
    {
        internal bool Show;
        internal string Text;
        internal float Spacing;
        internal TooltipEchoStyleKind StyleKind;
        internal TooltipEchoWidthOption WidthOption;
    }

    /// <summary>
    /// Shared per-window-instance renderer for the bottom "hovered control help text"
    /// strip used by the Settings, Recordings, Logistics, Real Spawn Control and both
    /// test-runner windows.
    ///
    /// <para><b>Why this exists (the two defects it closes).</b> IMGUI sizes a control
    /// during the Layout event and REUSES that cached rect during Repaint
    /// (GUILayoutUtility.DoGetRect); the style and options handed over at Repaint
    /// affect drawing only, never sizing. <see cref="GUI.tooltip"/>, however, is
    /// populated by the hovered control DURING Repaint. So a strip that reads
    /// GUI.tooltip live diverges between the two passes of the same frame:</para>
    /// <list type="number">
    ///   <item>On the frame a hover starts (or the tooltip string changes), Layout
    ///   sized the label for the OLD text - zero height when the hover just began -
    ///   while Repaint painted the NEW text, leaving a one-frame dark sliver of box
    ///   background at the window bottom on every crossing onto a tooltipped control.
    ///   Hover end was the mirror image: an empty label painted into a tall rect.</item>
    ///   <item>Where the strip also varied its CONTROL COUNT by whether a tooltip was
    ///   present (Real Spawn Control emitted 1 control unhovered and 2 hovered), the
    ///   layout group overran outright: "Getting control N's position in a group with
    ///   only N controls when doing repaint", thrown every Repaint while hovering and
    ///   aborting the rest of that window's draw (resize handle and DragWindow with
    ///   it).</item>
    /// </list>
    ///
    /// <para><b>The contract.</b> Both passes of a frame render from
    /// <see cref="cachedText"/>, which is only ever written at the END of a Repaint
    /// pass. Layout and Repaint therefore always agree on the string, so the reserved
    /// rect and the painted content can never disagree - including the WRAPPED height,
    /// which Layout computes from the exact text Repaint paints at the window's
    /// current width, so a narrowed window reserves the full multi-line height instead
    /// of clipping. The cost is one frame of latency at hover start and hover end,
    /// invisible at frame rate. On top of that the emitted shape is invariant: ALWAYS
    /// exactly one GUILayout.Space plus one GUILayout.Label, varying only their
    /// spacing, content, style and single layout option.</para>
    ///
    /// <para>One instance per window (styles are built from <see cref="GUI.skin"/>, so
    /// a window whose skin is scene-scoped must call <see cref="ResetStyles"/> on a
    /// scene change).</para>
    /// </summary>
    internal sealed class TooltipEchoBox
    {
        /// <summary>House spacing above a visible strip; matches every window's SpacingSmall.</summary>
        internal const float DefaultSpacing = 3f;

        private readonly float spacing;

        // The single source of truth for BOTH passes of a frame. Written only at the
        // end of a Repaint pass; read identically by that frame's Layout and Repaint.
        private string cachedText = string.Empty;
        private bool shownLastDraw;

        private GUIStyle zeroHeightLabelStyle;
        private GUIStyle wrappedTooltipStyle;

        internal TooltipEchoBox() : this(DefaultSpacing)
        {
        }

        internal TooltipEchoBox(float spacing)
        {
            this.spacing = spacing;
        }

        /// <summary>
        /// True when the strip rendered visible help text on the last completed draw.
        /// Read by the Settings window's height re-measure gate: a measurement taken
        /// while the strip is up would bake in a height that vanishes with it.
        /// </summary>
        internal bool ShownLastDraw
        {
            get { return shownLastDraw; }
        }

        /// <summary>The text both passes of the CURRENT frame render from.</summary>
        internal string CachedText
        {
            get { return cachedText; }
        }

        /// <summary>The wrapped box style, once built. Null before the first draw.</summary>
        internal GUIStyle WrappedStyle
        {
            get { return wrappedTooltipStyle; }
        }

        /// <summary>
        /// Drops the cached styles so the next draw rebuilds them from the current
        /// <see cref="GUI.skin"/>. Windows whose skin is scene-scoped call this on a
        /// scene change (see <c>TestRunnerShortcut.ResetSceneScopedWindowState</c>).
        /// </summary>
        internal void ResetStyles()
        {
            zeroHeightLabelStyle = null;
            wrappedTooltipStyle = null;
        }

        /// <summary>
        /// Seeds <see cref="cachedText"/> directly. Test-only seam so a probe can put
        /// the strip into its "showing a long tooltip" state without a real hover.
        /// </summary>
        internal void PrimeCachedTextForTesting(string text)
        {
            cachedText = text ?? string.Empty;
            shownLastDraw = cachedText.Length > 0;
        }

        /// <summary>
        /// Decides whether the strip shows boxed help text this frame. Returns
        /// (false, "") when there is nothing to echo (the strip collapses to zero
        /// height), or (true, text) when there is. Pure and Unity-free.
        /// </summary>
        internal static (bool show, string text) ResolveTooltipEcho(string echoText)
        {
            if (string.IsNullOrEmpty(echoText))
                return (false, string.Empty);
            return (true, echoText);
        }

        /// <summary>
        /// The full per-frame branch: spacing, content, style and layout option. Pure
        /// and Unity-free (the enums stand in for the GUIStyle / GUILayoutOption the
        /// draw picks), so every branch is unit-testable.
        /// </summary>
        internal static TooltipEchoLayout ResolveEchoLayout(string echoText, float spacingWhenShown)
        {
            (bool show, string text) = ResolveTooltipEcho(echoText);
            return new TooltipEchoLayout
            {
                Show = show,
                Text = text,
                Spacing = show ? spacingWhenShown : 0f,
                StyleKind = show ? TooltipEchoStyleKind.WrappedBox : TooltipEchoStyleKind.ZeroHeightLabel,
                WidthOption = show ? TooltipEchoWidthOption.ExpandWidth : TooltipEchoWidthOption.ZeroHeight
            };
        }

        /// <summary>
        /// What the NEXT frame renders, captured at the end of this frame's Repaint: a
        /// window-supplied manual override wins over <see cref="GUI.tooltip"/>, because
        /// a window that computes its own hover string (RecordingsTableUI's clamped
        /// loop-period cell) has already resolved a more specific answer than the
        /// generic GUIContent tooltip. Pure and Unity-free.
        /// </summary>
        internal static string ResolveCapturedText(string manualOverride, string guiTooltip)
        {
            if (!string.IsNullOrEmpty(manualOverride))
                return manualOverride;
            return guiTooltip ?? string.Empty;
        }

        /// <summary>
        /// Emits the strip. Call once per draw pass, at the point in the window where
        /// the strip belongs (before the Close button / resize handle).
        /// </summary>
        /// <param name="manualOverride">
        /// Optional window-computed hover text that outranks <see cref="GUI.tooltip"/>
        /// for the NEXT frame's content. Only consulted during the Repaint capture, so
        /// callers must set it before this call in the same pass.
        /// </param>
        internal void Draw(string manualOverride = null)
        {
            EnsureStyles();

            // Both passes of this frame render from the SAME string, so the rect
            // Layout reserved and the content Repaint paints can never disagree.
            TooltipEchoLayout layout = ResolveEchoLayout(cachedText, spacing);

            GUILayout.Space(layout.Spacing);
            GUILayout.Label(
                layout.Text,
                layout.StyleKind == TooltipEchoStyleKind.WrappedBox
                    ? wrappedTooltipStyle
                    : zeroHeightLabelStyle,
                layout.WidthOption == TooltipEchoWidthOption.ExpandWidth
                    ? GUILayout.ExpandWidth(true)
                    : GUILayout.Height(0f));

            // Capture AFTER emitting: GUI.tooltip is filled in by the hovered control
            // during Repaint, and every control that could be hovered has drawn by now.
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                cachedText = ResolveCapturedText(manualOverride, GUI.tooltip);
                shownLastDraw = cachedText.Length > 0;
            }
        }

        private void EnsureStyles()
        {
            if (zeroHeightLabelStyle == null)
            {
                zeroHeightLabelStyle = new GUIStyle(GUI.skin.label)
                {
                    fixedHeight = 0f,
                    stretchHeight = false,
                    wordWrap = false
                };
                zeroHeightLabelStyle.margin = new RectOffset(0, 0, 0, 0);
                zeroHeightLabelStyle.padding = new RectOffset(0, 0, 0, 0);
            }

            if (wrappedTooltipStyle == null)
            {
                wrappedTooltipStyle = new GUIStyle(GUI.skin.box)
                {
                    wordWrap = true,
                    alignment = TextAnchor.UpperLeft
                };
            }
        }
    }
}
