using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Shared per-window-instance renderer for the bottom "hovered control help text"
    /// strip used by the Settings, Recordings, Logistics, Real Spawn Control and both
    /// test-runner windows.
    ///
    /// <para><b>The shape contract.</b> The strip is PERMANENTLY visible and always
    /// exactly two text lines tall - empty when nothing is hovered, clipped when the
    /// hovered control's help text needs more than two wrapped lines. Every draw emits
    /// exactly one <c>GUILayout.Space</c> plus one <c>GUILayout.Label</c>, with the same
    /// spacing, the same style and the same explicit height option every time. Control
    /// count, control shape and reserved size are therefore constant across both IMGUI
    /// passes of a frame AND across frames.</para>
    ///
    /// <para><b>Why constant size is the mechanism.</b> IMGUI sizes a control during the
    /// Layout event and REUSES that cached rect during Repaint
    /// (GUILayoutUtility.DoGetRect), while <see cref="GUI.tooltip"/> is populated by the
    /// hovered control DURING Repaint. A strip whose size depends on the tooltip text
    /// therefore sized itself for one string and painted another on every hover-start
    /// frame (a one-frame dark sliver of box background), and a strip that varied its
    /// CONTROL COUNT by whether a tooltip was present overran its layout group outright
    /// ("Getting control N's position in a group with only N controls when doing
    /// repaint", thrown every Repaint while hovering and aborting the rest of that
    /// window's draw). Pinning the height removes both at the source: the reserved rect
    /// does not depend on the text at all, so the text is read LIVE each pass and the
    /// two passes cannot disagree about anything that matters. It also removes the
    /// player-visible cost of a variable strip - the window no longer changes height,
    /// and its bottom row no longer shuffles, when the pointer crosses a control.</para>
    ///
    /// <para>One instance per window (styles are built from <see cref="GUI.skin"/>, so
    /// a window whose skin is scene-scoped must call <see cref="ResetStyles"/> on a
    /// scene change).</para>
    /// </summary>
    internal sealed class TooltipEchoBox
    {
        /// <summary>House spacing above the strip; matches every window's SpacingSmall.</summary>
        internal const float DefaultSpacing = 3f;

        /// <summary>
        /// Two explicit lines measured at a width nothing can wrap at, so the fixed
        /// height is exactly two lines of the box style plus its own padding.
        /// </summary>
        private const string TwoLineProbeText = "Ay\nAy";

        /// <summary>Width used for the two-line measurement; far wider than any Parsek window.</summary>
        private const float NoWrapMeasureWidth = 10000f;

        private readonly float spacing;

        private GUIStyle wrappedTooltipStyle;
        private float fixedStripHeight;

        internal TooltipEchoBox() : this(DefaultSpacing)
        {
        }

        internal TooltipEchoBox(float spacing)
        {
            this.spacing = spacing;
        }

        /// <summary>The wrapped box style, once built. Null before the first draw.</summary>
        internal GUIStyle WrappedStyle
        {
            get { return wrappedTooltipStyle; }
        }

        /// <summary>
        /// The constant two-line height every draw reserves. Zero before the first draw
        /// (it is measured from <see cref="GUI.skin"/> the first time the strip renders).
        /// </summary>
        internal float FixedStripHeight
        {
            get { return fixedStripHeight; }
        }

        /// <summary>
        /// Drops the cached style and its measured height so the next draw rebuilds both
        /// from the current <see cref="GUI.skin"/>. Windows whose skin is scene-scoped
        /// call this on a scene change (see
        /// <c>TestRunnerShortcut.ResetSceneScopedWindowState</c>).
        /// </summary>
        internal void ResetStyles()
        {
            wrappedTooltipStyle = null;
            fixedStripHeight = 0f;
        }

        /// <summary>
        /// What the strip renders: a window-supplied manual override wins over
        /// <see cref="GUI.tooltip"/>, because a window that computes its own hover string
        /// (RecordingsTableUI's clamped loop-period cell, Real Spawn Control's bottom-row
        /// Warp button) has already resolved a more specific answer than the generic
        /// GUIContent tooltip. Pure and Unity-free.
        /// </summary>
        internal static string ResolveCapturedText(string manualOverride, string guiTooltip)
        {
            if (!string.IsNullOrEmpty(manualOverride))
                return manualOverride;
            return guiTooltip ?? string.Empty;
        }

        /// <summary>
        /// Emits the strip. Call once per draw pass, at the point in the window where the
        /// strip belongs - by house convention directly above the Close button row, which
        /// is always the window's last content row.
        ///
        /// <para>The text is read LIVE: during Layout it is empty or one frame stale, and
        /// that is irrelevant, because the rect Layout reserves is the same fixed height
        /// either way. Only controls drawn BEFORE this call can feed
        /// <see cref="GUI.tooltip"/> in time; a tooltipped control that sits below the
        /// strip must hand its text in through <paramref name="manualOverride"/>.</para>
        /// </summary>
        /// <param name="manualOverride">
        /// Optional window-computed hover text that outranks <see cref="GUI.tooltip"/>.
        /// </param>
        internal void Draw(string manualOverride = null)
        {
            EnsureStyles();

            string text = ResolveCapturedText(manualOverride, GUI.tooltip);

            GUILayout.Space(spacing);
            GUILayout.Label(
                text,
                wrappedTooltipStyle,
                GUILayout.Height(fixedStripHeight),
                GUILayout.ExpandWidth(true));
        }

        private void EnsureStyles()
        {
            if (wrappedTooltipStyle == null)
            {
                wrappedTooltipStyle = new GUIStyle(GUI.skin.box)
                {
                    wordWrap = true,
                    alignment = TextAnchor.UpperLeft,
                    // Explicit rather than inherited: the strip's whole contract is that
                    // over-long text CLIPS instead of growing the box.
                    clipping = TextClipping.Clip
                };
            }

            if (fixedStripHeight <= 0f)
            {
                fixedStripHeight = wrappedTooltipStyle.CalcHeight(
                    new GUIContent(TwoLineProbeText), NoWrapMeasureWidth);
            }
        }
    }
}
