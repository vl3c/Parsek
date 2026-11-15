using System;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Detects whether an IMGUI text field currently has keyboard focus so raw
    /// `Input.GetKeyDown` shortcut handlers can suppress themselves while the
    /// player is typing. Parsek's UI is IMGUI throughout (GUILayout/GUI through
    /// ClickThroughFix), so the IMGUI `GUIUtility.keyboardControl` check covers
    /// every Parsek-authored text field. A uGUI EventSystem seam was considered
    /// and dropped: `EventSystem.current.currentSelectedGameObject != null` is
    /// satisfied by any stock-KSP uGUI Button click (e.g. the ApplicationLauncher
    /// toolbar that surfaces the Parsek window), which would silently suppress
    /// watch-mode shortcuts in flight after the click.
    /// </summary>
    internal static class InputFocusGuard
    {
        // Test seam. Setting to a non-null value bypasses the Unity static
        // below so xUnit can drive the guard without a live GUIUtility.
        // Reset via ResetTestOverrides() in Dispose.
        internal static Func<int> KeyboardControlProviderForTesting;

        internal static bool IsTextFieldFocused()
        {
            int keyboardControl = KeyboardControlProviderForTesting != null
                ? KeyboardControlProviderForTesting()
                : GetKeyboardControl();
            return keyboardControl != 0;
        }

        internal static void ResetTestOverrides()
        {
            KeyboardControlProviderForTesting = null;
        }

        private static int GetKeyboardControl() => GUIUtility.keyboardControl;
    }
}
