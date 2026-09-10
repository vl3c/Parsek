using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Parsek.Patches
{
    /// <summary>
    /// Harmony interceptions of the UnityEngine IMGUI funnels that feed
    /// <see cref="GuiTreeRecorder"/>. Applied by <c>ParsekHarmony</c>'s generic
    /// <c>[HarmonyPatch]</c> sweep, which patches each class independently and logs a
    /// failure rather than aborting, so one drifted signature costs one funnel.
    ///
    /// <para><b>Contract every body in this file obeys.</b> First statement is
    /// <c>if (!GuiTreeRecorder.ArmedFlag) return;</c> - a single static bool read, which
    /// is the whole cost while disarmed. No body throws: all the work happens behind
    /// <c>GuiTreeRecorder</c>'s try/catch entry points, because an exception escaping
    /// here would abort Unity's GUI pass mid-window.</para>
    ///
    /// <para><b>Why these methods.</b> Each is the deepest MANAGED method on its control's
    /// path; everything below is an ICall. Target signatures live in one place,
    /// <see cref="GuiTreeFunnels.Target"/>, which each <c>TargetMethod()</c> returns, so
    /// the patched signature and the JSON's funnel report cannot disagree. Rationale,
    /// call chains and the Mono-inlining analysis: <c>docs/dev/design-gui-tree-dump.md</c>.
    /// </para>
    /// </summary>
    internal static class GuiTreeRecorderPatches
    {
        // Marker type only: the real work is in the nested patch classes below, which
        // ParsekHarmony discovers by their own [HarmonyPatch] attributes.
    }

    // ------------------------------------------------------------------------ windows

    /// <summary>
    /// <c>GUI.DoWindow</c> - the single funnel for all six <c>GUI.Window</c> overloads
    /// AND <c>GUILayout.Window</c> (which wraps its callback and calls
    /// <c>GUI.Window</c>), which is also where <c>ClickThruBlocker.GUILayoutWindow</c>
    /// lands. Carries the declared rect and the title; the window NODE is opened by
    /// <see cref="GuiTreeCallWindowDelegatePatch"/>.
    /// </summary>
    [HarmonyPatch]
    internal static class GuiTreeDoWindowPatch
    {
        static MethodBase TargetMethod()
        {
            return GuiTreeFunnels.Target(GuiFunnel.DoWindow);
        }

        static void Prefix(int id, Rect clientRect, GUIContent title, GUIStyle style)
        {
            if (!GuiTreeRecorder.ArmedFlag)
                return;
            GuiTreeRecorder.RecordWindowDeclaration(id, clientRect, title, style);
        }
    }

    /// <summary>
    /// <c>GUI.CallWindowDelegate</c> - the managed method the NATIVE window host calls to
    /// run a window's body. It brackets the window body exactly, and it is large enough
    /// that Mono will not inline it, which makes it the load-bearing window funnel.
    /// </summary>
    [HarmonyPatch]
    internal static class GuiTreeCallWindowDelegatePatch
    {
        static MethodBase TargetMethod()
        {
            return GuiTreeFunnels.Target(GuiFunnel.CallWindowDelegate);
        }

        static void Prefix(int id, float width, float height, GUIStyle style)
        {
            if (!GuiTreeRecorder.ArmedFlag)
                return;
            GuiTreeRecorder.RecordWindowBegin(id, width, height, style);
        }

        static void Postfix()
        {
            if (!GuiTreeRecorder.ArmedFlag)
                return;
            GuiTreeRecorder.RecordEnd(GuiFunnel.CallWindowDelegate, GuiNodeKind.Window);
        }
    }

    // ------------------------------------------------------------- clipped containers

    /// <summary>
    /// <c>GUI.BeginGroup(Rect, GUIContent, GUIStyle, Vector2)</c> - the internal funnel
    /// behind every public <c>GUI.BeginGroup</c> overload and behind
    /// <c>GUILayout.BeginArea</c>.
    /// </summary>
    [HarmonyPatch]
    internal static class GuiTreeBeginGroupPatch
    {
        static MethodBase TargetMethod()
        {
            return GuiTreeFunnels.Target(GuiFunnel.BeginGroup);
        }

        // POSTFIX, so the clip depth recorded on the node is the one its children
        // will report. Nothing is lost by waiting: the group's own background is
        // drawn through GUIStyle.Draw, below the managed surface, so no leaf event
        // can arrive between the prefix and the postfix.
        static void Postfix(Rect position, GUIContent content, GUIStyle style)
        {
            if (!GuiTreeRecorder.ArmedFlag)
                return;
            GuiTreeRecorder.RecordBeginClipContainer(
                GuiFunnel.BeginGroup, GuiNodeKind.Group, position, content, style);
        }
    }

    /// <summary>
    /// <c>GUI.EndGroup</c>. Two statements, so Mono may well inline it into
    /// <c>GUILayout.EndArea</c> and the callers; the assembler therefore does not depend
    /// on this End arriving - the clip depth carried by the next event closes the group
    /// either way.
    /// </summary>
    [HarmonyPatch]
    internal static class GuiTreeEndGroupPatch
    {
        static MethodBase TargetMethod()
        {
            return GuiTreeFunnels.Target(GuiFunnel.EndGroup);
        }

        static void Prefix()
        {
            if (!GuiTreeRecorder.ArmedFlag)
                return;
            GuiTreeRecorder.RecordEnd(GuiFunnel.EndGroup, GuiNodeKind.Group);
        }
    }

    /// <summary>
    /// <c>GUI.BeginScrollView(Rect, Vector2, Rect, bool, bool, GUIStyle x3)</c> - the
    /// internal 8-argument funnel behind every public <c>GUI.BeginScrollView</c> and
    /// <c>GUILayout.BeginScrollView</c>. Patched as a POSTFIX, which is load-bearing:
    /// the method draws its own two scrollbars BEFORE it pushes the clip, so a scroll
    /// view opened on the prefix was immediately closed again by its own scrollbar's
    /// (outer) clip depth and ended up holding none of its rows. As a postfix the
    /// scrollbars are recorded as SIBLINGS just before the scroll view, which is also
    /// the truth about them - they are drawn in the enclosing coordinate space, outside
    /// the scrolled content.
    /// </summary>
    [HarmonyPatch]
    internal static class GuiTreeBeginScrollViewPatch
    {
        static MethodBase TargetMethod()
        {
            return GuiTreeFunnels.Target(GuiFunnel.BeginScrollView);
        }

        static void Postfix(Rect position, GUIStyle background)
        {
            if (!GuiTreeRecorder.ArmedFlag)
                return;
            GuiTreeRecorder.RecordBeginClipContainer(
                GuiFunnel.BeginScrollView, GuiNodeKind.ScrollView, position, null, background);
        }
    }

    /// <summary>
    /// <c>GUI.EndScrollView(bool)</c> - the funnel for both overloads. Patched as a
    /// POSTFIX: the scroll-wheel handling inside it belongs to the scroll view.
    /// </summary>
    [HarmonyPatch]
    internal static class GuiTreeEndScrollViewPatch
    {
        static MethodBase TargetMethod()
        {
            return GuiTreeFunnels.Target(GuiFunnel.EndScrollView);
        }

        static void Postfix()
        {
            if (!GuiTreeRecorder.ArmedFlag)
                return;
            GuiTreeRecorder.RecordEnd(GuiFunnel.EndScrollView, GuiNodeKind.ScrollView);
        }
    }

    // --------------------------------------------------------------- layout groups

    /// <summary>
    /// <c>GUILayout.BeginHorizontal(GUIContent, GUIStyle, GUILayoutOption[])</c> - the
    /// funnel for all five overloads. A layout group pushes no clip, so its rect comes
    /// from the layout cache and its nesting relies on the matching End (with rect
    /// containment as the recovery).
    /// </summary>
    [HarmonyPatch]
    internal static class GuiTreeBeginHorizontalPatch
    {
        static MethodBase TargetMethod()
        {
            return GuiTreeFunnels.Target(GuiFunnel.BeginHorizontal);
        }

        static void Postfix(GUIContent content, GUIStyle style)
        {
            if (!GuiTreeRecorder.ArmedFlag)
                return;
            // POSTFIX: the group has to be on the layout stack before its rect can be read.
            GuiTreeRecorder.RecordBeginLayoutGroup(
                GuiFunnel.BeginHorizontal, true, content, style);
        }
    }

    /// <summary><c>GUILayout.EndHorizontal</c>.</summary>
    [HarmonyPatch]
    internal static class GuiTreeEndHorizontalPatch
    {
        static MethodBase TargetMethod()
        {
            return GuiTreeFunnels.Target(GuiFunnel.EndHorizontal);
        }

        static void Prefix()
        {
            if (!GuiTreeRecorder.ArmedFlag)
                return;
            GuiTreeRecorder.RecordEnd(GuiFunnel.EndHorizontal, GuiNodeKind.LayoutGroup);
        }
    }

    /// <summary>
    /// <c>GUILayout.BeginVertical(GUIContent, GUIStyle, GUILayoutOption[])</c> - the
    /// funnel for all five overloads.
    /// </summary>
    [HarmonyPatch]
    internal static class GuiTreeBeginVerticalPatch
    {
        static MethodBase TargetMethod()
        {
            return GuiTreeFunnels.Target(GuiFunnel.BeginVertical);
        }

        static void Postfix(GUIContent content, GUIStyle style)
        {
            if (!GuiTreeRecorder.ArmedFlag)
                return;
            GuiTreeRecorder.RecordBeginLayoutGroup(
                GuiFunnel.BeginVertical, false, content, style);
        }
    }

    /// <summary><c>GUILayout.EndVertical</c>.</summary>
    [HarmonyPatch]
    internal static class GuiTreeEndVerticalPatch
    {
        static MethodBase TargetMethod()
        {
            return GuiTreeFunnels.Target(GuiFunnel.EndVertical);
        }

        static void Prefix()
        {
            if (!GuiTreeRecorder.ArmedFlag)
                return;
            GuiTreeRecorder.RecordEnd(GuiFunnel.EndVertical, GuiNodeKind.LayoutGroup);
        }
    }

    // ----------------------------------------------------------------------- leaves

    /// <summary>
    /// <c>GUI.DoLabel</c> - every <c>GUI.Label</c> overload and every
    /// <c>GUILayout.Label</c> reaches it. It is also one of the two managed
    /// tooltip-publish sites in the module, which is why the tooltip on a Label is
    /// always the real one.
    /// </summary>
    [HarmonyPatch]
    internal static class GuiTreeDoLabelPatch
    {
        static MethodBase TargetMethod()
        {
            return GuiTreeFunnels.Target(GuiFunnel.DoLabel);
        }

        static void Prefix(Rect position, GUIContent content, GUIStyle style)
        {
            if (!GuiTreeRecorder.ArmedFlag)
                return;
            GuiTreeRecorder.RecordLeaf(GuiFunnel.DoLabel, GuiNodeKind.Label,
                position, content, style, null, null, null);
        }
    }

    /// <summary>
    /// <c>GUI.Box(Rect, GUIContent, GUIStyle)</c> - every Box overload and
    /// <c>GUILayout.Box</c>. Also reached by a STYLED <c>GUILayout.BeginHorizontal</c> /
    /// <c>BeginVertical</c>, which draws its group background through it. That box is
    /// drawn before the group's own postfix records the group, so it appears as the
    /// SIBLING immediately preceding the group rather than as its first child, carrying
    /// the same rect. Real, not a duplicate.
    /// </summary>
    [HarmonyPatch]
    internal static class GuiTreeBoxPatch
    {
        static MethodBase TargetMethod()
        {
            return GuiTreeFunnels.Target(GuiFunnel.Box);
        }

        static void Prefix(Rect position, GUIContent content, GUIStyle style)
        {
            if (!GuiTreeRecorder.ArmedFlag)
                return;
            GuiTreeRecorder.RecordLeaf(GuiFunnel.Box, GuiNodeKind.Box,
                position, content, style, null, null, null);
        }
    }

    /// <summary>
    /// <c>GUI.DoControl</c> - the shared body of Button and Toggle, and a large method
    /// Mono will not inline. It cannot tell the two apart on its own, so the kind comes
    /// from the staging hint the caller's own patch leaves; with no hint it falls back to
    /// the style name.
    /// </summary>
    [HarmonyPatch]
    internal static class GuiTreeDoControlPatch
    {
        static MethodBase TargetMethod()
        {
            return GuiTreeFunnels.Target(GuiFunnel.DoControl);
        }

        static void Prefix(Rect position, int id, bool on, GUIContent content, GUIStyle style)
        {
            if (!GuiTreeRecorder.ArmedFlag)
                return;
            GuiTreeRecorder.RecordControl(position, id, on, content, style);
        }
    }

    /// <summary>
    /// <c>GUI.DoButton</c> - two statements, so this patch exists only to name the KIND
    /// for the <c>DoControl</c> body underneath. If Mono inlined THIS method the kind
    /// degrades to the style-name classification; if Mono inlined <c>DoControl</c>
    /// instead, the postfix emits the staged leaf so nothing is lost either way.
    /// </summary>
    [HarmonyPatch]
    internal static class GuiTreeDoButtonPatch
    {
        static MethodBase TargetMethod()
        {
            return GuiTreeFunnels.Target(GuiFunnel.DoButton);
        }

        static void Prefix(Rect position, int id, GUIContent content, GUIStyle style)
        {
            if (!GuiTreeRecorder.ArmedFlag)
                return;
            GuiTreeRecorder.StageControl(GuiFunnel.DoButton, GuiNodeKind.Button,
                position, id, content, style, null);
        }

        static void Postfix(int id)
        {
            if (!GuiTreeRecorder.ArmedFlag)
                return;
            GuiTreeRecorder.FlushStagedControl(GuiFunnel.DoButton, id);
        }
    }

    /// <summary><c>GUI.DoToggle</c> - the Toggle side of the same staging pair.</summary>
    [HarmonyPatch]
    internal static class GuiTreeDoTogglePatch
    {
        static MethodBase TargetMethod()
        {
            return GuiTreeFunnels.Target(GuiFunnel.DoToggle);
        }

        static void Prefix(Rect position, int id, bool value, GUIContent content, GUIStyle style)
        {
            if (!GuiTreeRecorder.ArmedFlag)
                return;
            GuiTreeRecorder.StageControl(GuiFunnel.DoToggle, GuiNodeKind.Toggle,
                position, id, content, style, value);
        }

        static void Postfix(int id)
        {
            if (!GuiTreeRecorder.ArmedFlag)
                return;
            GuiTreeRecorder.FlushStagedControl(GuiFunnel.DoToggle, id);
        }
    }

    /// <summary><c>GUI.DoRepeatButton</c> - every RepeatButton overload.</summary>
    [HarmonyPatch]
    internal static class GuiTreeDoRepeatButtonPatch
    {
        static MethodBase TargetMethod()
        {
            return GuiTreeFunnels.Target(GuiFunnel.DoRepeatButton);
        }

        static void Prefix(Rect position, GUIContent content, GUIStyle style)
        {
            if (!GuiTreeRecorder.ArmedFlag)
                return;
            GuiTreeRecorder.RecordLeaf(GuiFunnel.DoRepeatButton, GuiNodeKind.RepeatButton,
                position, content, style, null, null, null);
        }
    }

    /// <summary>
    /// <c>GUI.DoTextField</c>, the 8-argument innermost overload - TextField, TextArea and
    /// PasswordField all reach it, from both <c>GUI</c> and <c>GUILayout</c>. The live
    /// text arrives in <c>content.text</c>; <c>secureText</c> is deliberately NOT read,
    /// so a password field records only the masked content Unity is drawing.
    /// </summary>
    [HarmonyPatch]
    internal static class GuiTreeDoTextFieldPatch
    {
        static MethodBase TargetMethod()
        {
            return GuiTreeFunnels.Target(GuiFunnel.DoTextField);
        }

        static void Prefix(Rect position, int id, GUIContent content, GUIStyle style)
        {
            if (!GuiTreeRecorder.ArmedFlag)
                return;
            GuiTreeRecorder.RecordLeaf(GuiFunnel.DoTextField, GuiNodeKind.TextField,
                position, content, style, id, null, content != null ? content.text : null);
        }
    }

    /// <summary>
    /// <c>GUI.DoButtonGrid</c> - the funnel for <c>Toolbar</c> and
    /// <c>SelectionGrid</c> from both <c>GUI</c> and <c>GUILayout</c>. Recorded as ONE
    /// node for the whole grid: the per-cell rects are computed privately inside the
    /// method and the individual cells are drawn through <c>GUIStyle.Draw</c>, below the
    /// managed surface.
    /// </summary>
    [HarmonyPatch]
    internal static class GuiTreeDoButtonGridPatch
    {
        static MethodBase TargetMethod()
        {
            return GuiTreeFunnels.Target(GuiFunnel.DoButtonGrid);
        }

        static void Prefix(Rect position, int selected, GUIContent[] contents, GUIStyle style)
        {
            if (!GuiTreeRecorder.ArmedFlag)
                return;
            string label = null;
            if (contents != null && selected >= 0 && selected < contents.Length
                && contents[selected] != null)
            {
                label = contents[selected].text;
            }
            GuiTreeRecorder.RecordLeaf(GuiFunnel.DoButtonGrid, GuiNodeKind.ButtonGrid,
                position, null, style, selected, null, label);
        }
    }

    /// <summary>
    /// <c>GUI.Slider</c> - the funnel for <c>HorizontalSlider</c>,
    /// <c>VerticalSlider</c> and, through <c>GUI.Scroller</c>, both scrollbars including
    /// the ones a scroll view draws for itself.
    /// </summary>
    [HarmonyPatch]
    internal static class GuiTreeSliderPatch
    {
        static MethodBase TargetMethod()
        {
            return GuiTreeFunnels.Target(GuiFunnel.Slider);
        }

        static void Prefix(Rect position, float value, GUIStyle slider, int id)
        {
            if (!GuiTreeRecorder.ArmedFlag)
                return;
            GuiTreeRecorder.RecordLeaf(GuiFunnel.Slider, GuiNodeKind.Slider,
                position, null, slider, id, null,
                value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
