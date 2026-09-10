using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// The IMGUI funnel each Harmony patch targets. One enum member per patched method,
    /// so a funnel's target signature, its wire name and its hit counter cannot drift
    /// apart: <see cref="GuiTreeFunnels.Target"/> is what the patch classes'
    /// <c>TargetMethod()</c> returns AND what the arm-time report resolves.
    /// </summary>
    internal enum GuiFunnel
    {
        DoWindow = 0,
        CallWindowDelegate,
        BeginGroup,
        EndGroup,
        BeginScrollView,
        EndScrollView,
        BeginLayoutGroup,
        EndLayoutGroup,
        DoLabel,
        Box,
        DoControl,
        DoButton,
        DoToggle,
        DoRepeatButton,
        DoTextField,
        DoButtonGrid,
        Slider,
    }

    /// <summary>
    /// Resolves the UnityEngine IMGUI methods the GUI-tree recorder intercepts, and names
    /// them for the JSON's <c>funnels</c> block.
    ///
    /// <para><b>Signatures verified against the shipped
    /// <c>UnityEngine.IMGUIModule.dll</c> (KSP 1.12.5 / Unity 2019.4)</b> by decompiling
    /// <c>UnityEngine.GUI</c>, <c>GUILayout</c> and <c>GUILayoutUtility</c>. Each is the
    /// deepest funnel THAT STILL KNOWS THE CONTROL KIND - not the deepest managed method,
    /// which is <c>GUIStyle.Draw</c> and knows only a rect and a style. Below these the
    /// module is <c>[MethodImpl(InternalCall)]</c> (<c>GUIStyle.Internal_Draw2</c>,
    /// <c>GUIClip.Internal_Push</c>, <c>GUI.Internal_DoWindow</c>) and cannot be
    /// Harmony-patched at all. See <c>docs/dev/design-gui-tree-dump.md</c> for the call
    /// chains, the IL sizes and the inlining analysis.</para>
    /// </summary>
    internal static class GuiTreeFunnels
    {
        internal const int Count = (int)GuiFunnel.Slider + 1;

        /// <summary>
        /// Harmony id of the recorder's OWN instance. These patches are not part of
        /// <c>com.parsek.mod</c>'s permanent set: they are applied at arm time and removed
        /// again after the capture, so every IMGUI consumer in the process (KSP's debug
        /// UI, MechJeb, KER, ClickThroughBlocker) pays nothing at all while the recorder
        /// is disarmed. Owner-scoping the <c>patched</c> probe on this id is what keeps
        /// another mod's patch on the same method from reading as ours.
        /// </summary>
        internal const string HarmonyId = "com.parsek.guitree";

        /// <summary>Per-funnel hit counter for the captured frame. Reset at arm time.</summary>
        internal static readonly int[] Hits = new int[Count];

        /// <summary>
        /// Whether each funnel's target actually carried our patch, MEASURED AT ARM -
        /// the only time it can be measured, because the patches are removed again when
        /// the capture flushes and a flush-time reading would report <c>false</c> for
        /// every funnel. This is what the JSON's <c>funnels</c> block reports.
        /// </summary>
        internal static readonly bool[] PatchedAtArm = new bool[Count];

        internal static string Name(GuiFunnel funnel)
        {
            switch (funnel)
            {
                case GuiFunnel.DoWindow: return "GUI.DoWindow";
                case GuiFunnel.CallWindowDelegate: return "GUI.CallWindowDelegate";
                case GuiFunnel.BeginGroup: return "GUI.BeginGroup";
                case GuiFunnel.EndGroup: return "GUI.EndGroup";
                case GuiFunnel.BeginScrollView: return "GUI.BeginScrollView";
                case GuiFunnel.EndScrollView: return "GUI.EndScrollView";
                case GuiFunnel.BeginLayoutGroup: return "GUILayoutUtility.BeginLayoutGroup";
                case GuiFunnel.EndLayoutGroup: return "GUILayoutUtility.EndLayoutGroup";
                case GuiFunnel.DoLabel: return "GUI.DoLabel";
                case GuiFunnel.Box: return "GUI.Box";
                case GuiFunnel.DoControl: return "GUI.DoControl";
                case GuiFunnel.DoButton: return "GUI.DoButton";
                case GuiFunnel.DoToggle: return "GUI.DoToggle";
                case GuiFunnel.DoRepeatButton: return "GUI.DoRepeatButton";
                case GuiFunnel.DoTextField: return "GUI.DoTextField";
                case GuiFunnel.DoButtonGrid: return "GUI.DoButtonGrid";
                case GuiFunnel.Slider: return "GUI.Slider";
                default: return "unknown";
            }
        }

        /// <summary>
        /// The exact method a funnel patches, or null when the signature is not present
        /// in this Unity build. Null is reported rather than thrown: the applier patches
        /// each funnel independently, so a null target degrades to "that one funnel is
        /// missing" instead of losing the whole capture.
        /// </summary>
        internal static MethodInfo Target(GuiFunnel funnel)
        {
            switch (funnel)
            {
                case GuiFunnel.DoWindow:
                    return AccessTools.Method(typeof(GUI), "DoWindow", new[]
                    {
                        typeof(int), typeof(Rect), typeof(GUI.WindowFunction),
                        typeof(GUIContent), typeof(GUIStyle), typeof(GUISkin), typeof(bool),
                    });
                case GuiFunnel.CallWindowDelegate:
                    return AccessTools.Method(typeof(GUI), "CallWindowDelegate", new[]
                    {
                        typeof(GUI.WindowFunction), typeof(int), typeof(int), typeof(GUISkin),
                        typeof(int), typeof(float), typeof(float), typeof(GUIStyle),
                    });
                case GuiFunnel.BeginGroup:
                    return AccessTools.Method(typeof(GUI), "BeginGroup", new[]
                    {
                        typeof(Rect), typeof(GUIContent), typeof(GUIStyle), typeof(Vector2),
                    });
                case GuiFunnel.EndGroup:
                    return AccessTools.Method(typeof(GUI), "EndGroup", Type.EmptyTypes);
                case GuiFunnel.BeginScrollView:
                    return AccessTools.Method(typeof(GUI), "BeginScrollView", new[]
                    {
                        typeof(Rect), typeof(Vector2), typeof(Rect), typeof(bool), typeof(bool),
                        typeof(GUIStyle), typeof(GUIStyle), typeof(GUIStyle),
                    });
                case GuiFunnel.EndScrollView:
                    return AccessTools.Method(typeof(GUI), "EndScrollView", new[] { typeof(bool) });
                case GuiFunnel.BeginLayoutGroup:
                    return AccessTools.Method(typeof(GUILayoutUtility), "BeginLayoutGroup", new[]
                    {
                        typeof(GUIStyle), typeof(GUILayoutOption[]), typeof(Type),
                    });
                case GuiFunnel.EndLayoutGroup:
                    return AccessTools.Method(typeof(GUILayoutUtility), "EndLayoutGroup",
                        Type.EmptyTypes);
                case GuiFunnel.DoLabel:
                    return AccessTools.Method(typeof(GUI), "DoLabel", new[]
                    {
                        typeof(Rect), typeof(GUIContent), typeof(GUIStyle),
                    });
                case GuiFunnel.Box:
                    return AccessTools.Method(typeof(GUI), "Box", new[]
                    {
                        typeof(Rect), typeof(GUIContent), typeof(GUIStyle),
                    });
                case GuiFunnel.DoControl:
                    return AccessTools.Method(typeof(GUI), "DoControl", new[]
                    {
                        typeof(Rect), typeof(int), typeof(bool), typeof(bool),
                        typeof(GUIContent), typeof(GUIStyle),
                    });
                case GuiFunnel.DoButton:
                    return AccessTools.Method(typeof(GUI), "DoButton", new[]
                    {
                        typeof(Rect), typeof(int), typeof(GUIContent), typeof(GUIStyle),
                    });
                case GuiFunnel.DoToggle:
                    return AccessTools.Method(typeof(GUI), "DoToggle", new[]
                    {
                        typeof(Rect), typeof(int), typeof(bool), typeof(GUIContent), typeof(GUIStyle),
                    });
                case GuiFunnel.DoRepeatButton:
                    return AccessTools.Method(typeof(GUI), "DoRepeatButton", new[]
                    {
                        typeof(Rect), typeof(GUIContent), typeof(GUIStyle), typeof(FocusType),
                    });
                case GuiFunnel.DoTextField:
                    return AccessTools.Method(typeof(GUI), "DoTextField", new[]
                    {
                        typeof(Rect), typeof(int), typeof(GUIContent), typeof(bool), typeof(int),
                        typeof(GUIStyle), typeof(string), typeof(char),
                    });
                case GuiFunnel.DoButtonGrid:
                    return AccessTools.Method(typeof(GUI), "DoButtonGrid", new[]
                    {
                        typeof(Rect), typeof(int), typeof(GUIContent[]), typeof(string[]),
                        typeof(int), typeof(GUIStyle), typeof(GUIStyle), typeof(GUIStyle),
                        typeof(GUIStyle), typeof(GUI.ToolbarButtonSize), typeof(bool[]),
                    });
                case GuiFunnel.Slider:
                    return AccessTools.Method(typeof(GUI), "Slider", new[]
                    {
                        typeof(Rect), typeof(float), typeof(float), typeof(float), typeof(float),
                        typeof(GUIStyle), typeof(GUIStyle), typeof(bool), typeof(int), typeof(GUIStyle),
                    });
                default:
                    return null;
            }
        }

        /// <summary>
        /// Whether <see cref="HarmonyId"/> currently owns a patch on this funnel's target.
        /// Read at ARM time into <see cref="PatchedAtArm"/>; see that field for why a
        /// flush-time reading would be worthless.
        /// </summary>
        internal static bool IsPatched(GuiFunnel funnel)
        {
            try
            {
                MethodInfo target = Target(funnel);
                if (target == null)
                    return false;
                HarmonyLib.Patches info = Harmony.GetPatchInfo(target);
                if (info == null || info.Owners == null)
                    return false;
                return info.Owners.Contains(HarmonyId);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
