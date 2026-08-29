using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// "Why is this greyed out?" hover plumbing for DISABLED IMGUI controls.
    ///
    /// <para><b>The problem.</b> Most of Parsek's greyed-out buttons already carry the
    /// right words: the Rewind / Forward buttons pass <c>CanRewind</c>'s and
    /// <c>CanFastForward</c>'s <c>out reason</c> straight into their
    /// <see cref="GUIContent"/> tooltip, the Watch buttons pass
    /// <c>GetWatchButtonTooltip</c>, Re-Fly passes its slot reason. What was missing was
    /// a GUARANTEE that a control drawn under <c>GUI.enabled = false</c> actually
    /// publishes that tooltip to <see cref="GUI.tooltip"/>, which is what the window's
    /// <see cref="TooltipEchoBox"/> strip echoes.</para>
    ///
    /// <para><b>Why a Label carrier and not the button's own tooltip.</b> In the Unity
    /// build KSP 1.12.5 ships, there are exactly TWO managed tooltip-publish sites in the
    /// whole of UnityEngine.IMGUIModule: <c>GUI.DoLabel</c> and <c>GUI.DoButtonGrid</c>.
    /// Neither reads <c>GUI.enabled</c> - <c>DoLabel</c> publishes on
    /// <c>!string.IsNullOrEmpty(content.tooltip) &amp;&amp; position.Contains(mousePosition)
    /// &amp;&amp; GUIClip.visibleRect.Contains(mousePosition)</c> and nothing else. Every
    /// OTHER control (including <c>GUI.Button</c>) reaches the tooltip through the NATIVE
    /// <c>GUIStyle.Internal_Draw2</c>, whose behaviour under <c>GUI.enabled = false</c>
    /// cannot be read from the shipped assembly and is not documented. Routing the reason
    /// through a Label makes the feature rest on the one path that is PROVEN to ignore
    /// <c>GUI.enabled</c>, instead of on an inference about closed-source native code.</para>
    ///
    /// <para><b>Why it cannot disturb layout.</b> <c>GUI.Label(Rect, ...)</c> is the
    /// explicit-rect overload: it calls <c>GUIUtility.CheckOnGUI()</c> and then
    /// <c>DoLabel</c>, and <c>DoLabel</c> never calls <c>GetControlID</c> and never
    /// reserves a layout slot. So the carrier adds ZERO controls and ZERO control IDs to
    /// the enclosing layout group - the invariant <see cref="TooltipEchoBox"/> documents
    /// as load-bearing (a control-count that differs between Layout and Repaint throws
    /// "Getting control N's position in a group with only N controls"). The carrier is
    /// additionally emitted only on <see cref="EventType.Repaint"/>, the one pass
    /// <c>DoLabel</c> does anything at all on.</para>
    ///
    /// <para><b>Cost.</b> The pointer test runs first, so at most ONE carrier is emitted
    /// per frame across every disabled control in the window, and the common case (a
    /// greyed-out button nowhere near the pointer) costs one <c>Rect.Contains</c>. The
    /// <see cref="GUIContent"/> is a single reused instance, so a hovered disabled button
    /// allocates nothing per frame.</para>
    ///
    /// <para>Enabled controls are left completely alone: they publish their own tooltip
    /// through the normal path, exactly as before this type existed.</para>
    /// </summary>
    internal static class DisabledHoverEcho
    {
        /// <summary>
        /// The one reused carrier. Its text stays empty (it must paint nothing) and only
        /// its tooltip is rewritten, so a hovered disabled button allocates nothing.
        /// </summary>
        private static readonly GUIContent Carrier = new GUIContent(string.Empty);

        /// <summary>
        /// Whether a control needs a hover carrier at all: only a DISABLED control that
        /// actually has a reason to give. An enabled control publishes its own tooltip
        /// through the normal path and must not be double-published; a disabled control
        /// with no reason has nothing to say and stays silent rather than echoing an
        /// empty strip. Pure for unit testing.
        /// </summary>
        internal static bool ShouldCarry(bool controlEnabled, string disabledReason)
        {
            return !controlEnabled && !string.IsNullOrEmpty(disabledReason);
        }

        /// <summary>
        /// The same hover test IMGUI itself does, plus the zero-width guard the house
        /// pattern uses for a rect that has not been laid out yet (see
        /// <c>SpawnControlUI.DrawSpawnControlBottomBar</c>). Pure for unit testing.
        /// </summary>
        internal static bool PointerInside(Rect rect, Vector2 pointer)
        {
            return rect.width > 0f && rect.height > 0f && rect.Contains(pointer);
        }

        /// <summary>
        /// Emits the hover carrier for the control that was JUST drawn, using its own
        /// laid-out rect. Call immediately after the control and inside the same layout
        /// group, before any other control is drawn.
        /// </summary>
        /// <param name="controlEnabled">
        /// <see cref="GUI.enabled"/> as it was WHEN THE CONTROL DREW - read it into a
        /// local before the draw call, because call sites routinely restore it right
        /// after.
        /// </param>
        /// <param name="disabledReason">
        /// What to say while it is greyed out. Usually the control's own
        /// <c>GUIContent.tooltip</c>, which at most Parsek sites is already the reason.
        /// </param>
        internal static void CarryLastControl(bool controlEnabled, string disabledReason)
        {
            if (!ShouldCarry(controlEnabled, disabledReason))
                return;
            if (Event.current == null || Event.current.type != EventType.Repaint)
                return;

            Carry(GUILayoutUtility.GetLastRect(), controlEnabled, disabledReason);
        }

        /// <summary>
        /// <see cref="CarryLastControl"/> for a control whose rect the caller already
        /// holds (a manually placed control, or one of a pair drawn inside one cell).
        /// </summary>
        internal static void Carry(Rect rect, bool controlEnabled, string disabledReason)
        {
            if (!ShouldCarry(controlEnabled, disabledReason))
                return;
            if (Event.current == null || Event.current.type != EventType.Repaint)
                return;
            if (!PointerInside(rect, Event.current.mousePosition))
                return;

            Carrier.tooltip = disabledReason;
            GUI.Label(rect, Carrier, GUIStyle.none);
        }
    }
}
