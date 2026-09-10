using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Parsek.Patches
{
    /// <summary>
    /// Harmony interceptions of the UnityEngine IMGUI funnels that feed
    /// <see cref="GuiTreeRecorder"/>.
    ///
    /// <para><b>NOT part of ParsekHarmony's permanent patch set.</b> No class in this
    /// file carries a <c>[HarmonyPatch]</c> attribute, so the assembly sweep in
    /// <c>ParsekHarmony.Awake</c> cannot discover them. They are applied by
    /// <see cref="GuiTreeRecorderPatches.Apply"/> from
    /// <c>GuiTreeRecorder.ArmForNextRepaint</c> and removed again by
    /// <see cref="GuiTreeRecorderPatches.Remove"/> when the capture flushes (or on
    /// disarm / fault). The reason is cost: a Harmony detour on <c>GUI.DoLabel</c> is
    /// paid by EVERY IMGUI consumer in the process - KSP's own debug UI, MechJeb, KER,
    /// ClickThroughBlocker - on every control of every event pass, forever. A patch
    /// body's <c>if (!ArmedFlag) return;</c> is one static bool read, but the detour
    /// around it is not free, and the recorder is armed for single frames minutes apart
    /// at most. Disarmed, the cost is now ZERO patches rather than a cheap patch.</para>
    ///
    /// <para><b>Contract every body in this file obeys.</b> First statement is
    /// <c>if (!GuiTreeRecorder.ArmedFlag) return;</c> - the patches can outlive a capture
    /// by one frame (the unpatch is deferred out of the GUI pass), so the flag still
    /// gates. No body throws: all the work happens behind <c>GuiTreeRecorder</c>'s
    /// try/catch entry points, because an exception escaping here would abort Unity's GUI
    /// pass mid-window.</para>
    ///
    /// <para><b>Why these methods.</b> Each is the deepest funnel that still knows the
    /// control kind. Target signatures live in one place,
    /// <see cref="GuiTreeFunnels.Target"/>, which each <c>TargetMethod()</c> returns, so
    /// the patched signature and the JSON's funnel report cannot disagree. Rationale,
    /// call chains, IL sizes and the Mono-inlining analysis:
    /// <c>docs/dev/design-gui-tree-dump.md</c>.</para>
    /// </summary>
    internal static class GuiTreeRecorderPatches
    {
        /// <summary>
        /// One row per funnel: the funnel and the class holding its Prefix / Postfix.
        /// This table is the ONLY discovery mechanism now that the attributes are gone,
        /// so a patch class dropped from the file reds
        /// <c>GuiTreeFunnelTests.PatchParameterNamesAndTypesMatchTheirTargets</c> instead
        /// of costing one silent control kind in a dump.
        /// </summary>
        internal static readonly Tuple<GuiFunnel, Type>[] All =
        {
            Tuple.Create(GuiFunnel.DoWindow, typeof(GuiTreeDoWindowPatch)),
            Tuple.Create(GuiFunnel.CallWindowDelegate, typeof(GuiTreeCallWindowDelegatePatch)),
            Tuple.Create(GuiFunnel.BeginGroup, typeof(GuiTreeBeginGroupPatch)),
            Tuple.Create(GuiFunnel.EndGroup, typeof(GuiTreeEndGroupPatch)),
            Tuple.Create(GuiFunnel.BeginScrollView, typeof(GuiTreeBeginScrollViewPatch)),
            Tuple.Create(GuiFunnel.EndScrollView, typeof(GuiTreeEndScrollViewPatch)),
            Tuple.Create(GuiFunnel.BeginLayoutGroup, typeof(GuiTreeBeginLayoutGroupPatch)),
            Tuple.Create(GuiFunnel.EndLayoutGroup, typeof(GuiTreeEndLayoutGroupPatch)),
            Tuple.Create(GuiFunnel.DoLabel, typeof(GuiTreeDoLabelPatch)),
            Tuple.Create(GuiFunnel.Box, typeof(GuiTreeBoxPatch)),
            Tuple.Create(GuiFunnel.DoControl, typeof(GuiTreeDoControlPatch)),
            Tuple.Create(GuiFunnel.DoButton, typeof(GuiTreeDoButtonPatch)),
            Tuple.Create(GuiFunnel.DoToggle, typeof(GuiTreeDoTogglePatch)),
            Tuple.Create(GuiFunnel.DoRepeatButton, typeof(GuiTreeDoRepeatButtonPatch)),
            Tuple.Create(GuiFunnel.DoTextField, typeof(GuiTreeDoTextFieldPatch)),
            Tuple.Create(GuiFunnel.DoButtonGrid, typeof(GuiTreeDoButtonGridPatch)),
            Tuple.Create(GuiFunnel.Slider, typeof(GuiTreeSliderPatch)),
        };

        private static Harmony harmony;

        /// <summary>True while the interceptions are installed.</summary>
        internal static bool Applied { get; private set; }

        /// <summary>
        /// Installs every funnel interception. Idempotent: a second arm before the first
        /// capture flushed is a no-op rather than a second detour. Each funnel is applied
        /// independently, so one drifted signature costs one funnel and is reported in
        /// the dump's <c>funnels</c> block as <c>patched: false</c>.
        /// </summary>
        internal static void Apply()
        {
            if (Applied)
                return;
            if (harmony == null)
                harmony = new Harmony(GuiTreeFunnels.HarmonyId);

            // Applied is set BEFORE the loop: a mid-loop throw must still leave Remove()
            // able to unpatch whatever did get installed.
            Applied = true;
            int ok = 0;
            int failed = 0;
            for (int i = 0; i < All.Length; i++)
            {
                GuiFunnel funnel = All[i].Item1;
                try
                {
                    MethodInfo target = GuiTreeFunnels.Target(funnel);
                    if (target == null)
                    {
                        failed++;
                        ParsekLog.Warn("GuiTree", "funnel target missing funnel="
                            + GuiTreeFunnels.Name(funnel)
                            + "; that control kind will be absent from the dump");
                        continue;
                    }
                    harmony.Patch(target,
                        Hook(All[i].Item2, "Prefix"), Hook(All[i].Item2, "Postfix"));
                    ok++;
                }
                catch (Exception ex)
                {
                    failed++;
                    ParsekLog.Warn("GuiTree", "funnel patch failed funnel="
                        + GuiTreeFunnels.Name(funnel) + ": " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            ParsekLog.Info("GuiTree", "patches applied id=" + GuiTreeFunnels.HarmonyId
                + " ok=" + ok + " failed=" + failed + " of=" + All.Length);
        }

        /// <summary>
        /// Removes every interception this class installed. Idempotent. MUST NOT be called
        /// from inside an IMGUI pass - rewriting a method the current call stack is
        /// executing is worse than leaving the patch on for one more frame - which is why
        /// <c>GuiTreeRecorder</c> defers to its LateUpdate pump whenever
        /// <c>GUIUtility.guiDepth &gt; 0</c>. (NOT <c>Event.current != null</c>: that is
        /// non-null forever once the process has drawn one frame, so it deferred every
        /// unpatch and refused every arm.)
        /// </summary>
        internal static void Remove()
        {
            if (!Applied)
                return;
            Applied = false;
            try
            {
                if (harmony != null)
                    harmony.UnpatchAll(GuiTreeFunnels.HarmonyId);
                ParsekLog.Info("GuiTree", "patches removed id=" + GuiTreeFunnels.HarmonyId);
            }
            catch (Exception ex)
            {
                ParsekLog.Error("GuiTree", "unpatch failed id=" + GuiTreeFunnels.HarmonyId
                    + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static HarmonyMethod Hook(Type patchClass, string name)
        {
            MethodInfo hook = patchClass.GetMethod(name,
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            return hook == null ? null : new HarmonyMethod(hook);
        }
    }

    // ------------------------------------------------------------------------ windows

    /// <summary>
    /// <c>GUI.DoWindow</c> - the single funnel for all six <c>GUI.Window</c> overloads
    /// AND <c>GUILayout.Window</c> (which wraps its callback and calls
    /// <c>GUI.Window</c>), which is also where <c>ClickThruBlocker.GUILayoutWindow</c>
    /// lands. Carries the declared rect and the title; the window NODE is opened by
    /// <see cref="GuiTreeCallWindowDelegatePatch"/>.
    /// </summary>
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
    /// run a window's body. It brackets the window body exactly, it is large, and it is
    /// <c>[RequiredByNativeCode]</c> invoked FROM native code, so it cannot be inlined at
    /// all. That makes it the load-bearing window funnel.
    /// </summary>
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
    /// <c>GUILayout.BeginArea</c>. <c>GUI.BeginScrollView</c> does NOT come through here:
    /// it pushes its own clip, which is why the scroll view has a funnel of its own.
    /// </summary>
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
    /// <c>GUI.EndGroup</c>. 14 bytes of IL, inside Mono's inline limit, so this End is
    /// EXPECTED to be bypassed and the clip-depth rule is the real close mechanism for a
    /// group. The patch stays because when it does fire it closes the group exactly, and
    /// because <c>hits: 0</c> against <c>patched: true</c> is the measurement that says
    /// the inlining happened.
    /// </summary>
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
    /// <c>GUILayoutUtility.BeginLayoutGroup(GUIStyle, GUILayoutOption[], Type)</c> - 180
    /// bytes of IL, far past Mono's inline limit, and the ONE funnel every layout group
    /// passes through. It replaced patches on <c>GUILayout.BeginHorizontal</c> /
    /// <c>BeginVertical</c> / <c>EndHorizontal</c> / <c>EndVertical</c>, whose End sides
    /// are 8 bytes each and are therefore almost certainly inlined - Mono's inliner reads
    /// IL from metadata, not the detour, so Harmony does not protect a small callee.
    ///
    /// <para><b>Its callers, module-wide</b> (grepped over the whole decompiled
    /// <c>UnityEngine.IMGUIModule</c>, not just <c>GUILayout</c>): exactly three -
    /// <c>GUILayout.BeginHorizontal(GUIContent, GUIStyle, GUILayoutOption[])</c> and
    /// <c>BeginVertical(...)</c>, both with <c>typeof(GUILayoutGroup)</c>, and
    /// <c>GUILayout.BeginScrollView(...)</c> with <c>typeof(GUIScrollGroup)</c>.
    /// <c>GUILayout.BeginArea</c> goes through <c>BeginLayoutArea</c> instead and
    /// <c>GUILayoutUtility.BeginWindow</c> assigns <c>topLevel</c> directly, so neither
    /// an area's nor a window's root layout group can be mistaken for a user group. The
    /// scroll group IS filtered out (by <c>layoutType</c>), because the scroll view
    /// already has its own node from <c>GUI.BeginScrollView</c>.</para>
    /// </summary>
    internal static class GuiTreeBeginLayoutGroupPatch
    {
        static MethodBase TargetMethod()
        {
            return GuiTreeFunnels.Target(GuiFunnel.BeginLayoutGroup);
        }

        // POSTFIX for two reasons: __result IS the GUILayoutGroup - its rect and its
        // isVertical both final during Repaint, because the group object is the one the
        // Layout pass created and sized - and the group has to be on the layout stack
        // before anything can be read off it. __result is declared as `object` because
        // UnityEngine.GUILayoutGroup is internal; Harmony accepts any type the return
        // type is assignable to (MethodPatcher's __result branch).
        static void Postfix(GUIStyle style, Type layoutType, object __result)
        {
            if (!GuiTreeRecorder.ArmedFlag)
                return;
            GuiTreeRecorder.RecordBeginLayoutGroup(style, layoutType, __result);
        }
    }

    /// <summary>
    /// <c>GUILayoutUtility.EndLayoutGroup()</c> - 123 bytes, also past the inline limit,
    /// and the single End for all three <c>BeginLayoutGroup</c> callers. It carries no
    /// arguments, so the recorder pairs it against its own Begin stack: that is what
    /// tells a scroll view's layout carrier (recorded as nothing) from a real group, and
    /// what gives the End event its ORIENTATION so a stranded horizontal group cannot
    /// swallow the End of the vertical group enclosing it.
    /// </summary>
    internal static class GuiTreeEndLayoutGroupPatch
    {
        static MethodBase TargetMethod()
        {
            return GuiTreeFunnels.Target(GuiFunnel.EndLayoutGroup);
        }

        static void Prefix()
        {
            if (!GuiTreeRecorder.ArmedFlag)
                return;
            GuiTreeRecorder.RecordEndLayoutGroup();
        }
    }

    // ----------------------------------------------------------------------- leaves

    /// <summary>
    /// <c>GUI.DoLabel</c> - every <c>GUI.Label</c> overload and every
    /// <c>GUILayout.Label</c> reaches it. It is also one of the two managed
    /// tooltip-publish sites in the module, which is why the tooltip on a Label is
    /// always the real one.
    /// </summary>
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
    /// <c>BeginVertical</c>, which draws its group background through it
    /// (<c>GUI.Box(group.rect, content, style)</c>) AFTER <c>BeginLayoutGroup</c> has
    /// returned - so with the layout group now recorded from that funnel's postfix, the
    /// box lands as the group's FIRST CHILD carrying the group's own rect. Real, not a
    /// duplicate.
    /// </summary>
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
    /// <c>GUI.DoButton</c> - 33 bytes of IL, so Mono may inline it. This patch exists only
    /// to name the KIND for the <c>DoControl</c> body underneath: if Mono inlined THIS
    /// method the kind degrades to the style-name classification; if Mono inlined
    /// <c>DoControl</c> instead, the postfix emits the staged leaf, so nothing is lost
    /// either way.
    /// </summary>
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

    /// <summary><c>GUI.DoToggle</c> - the Toggle side of the same staging pair (34 bytes).</summary>
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

    /// <summary>
    /// <c>GUI.DoRepeatButton</c> - every RepeatButton overload, including the two arrow
    /// buttons a scrollbar draws through <c>GUI.Scroller</c>.
    /// </summary>
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
