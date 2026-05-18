using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Parsek
{
    /// <summary>
    /// Detects whether a UI text field currently has keyboard focus so raw
    /// `Input.GetKeyDown` shortcut handlers can suppress themselves while the
    /// player is typing. The IMGUI seam covers stock and ClickThroughFix windows
    /// (rename fields, settings text fields); the EventSystem seam covers any
    /// uGUI / new-input-system selection.
    /// </summary>
    internal static class InputFocusGuard
    {
        // Test seams. Setting either to a non-null value bypasses the Unity
        // static call below it so xUnit can drive the guard without a live
        // GUIUtility / EventSystem. Reset via ResetTestOverrides() in Dispose.
        internal static Func<int> KeyboardControlProviderForTesting;
        internal static Func<bool> EventSystemFocusedProviderForTesting;

        internal static bool IsTextFieldFocused()
        {
            int keyboardControl = KeyboardControlProviderForTesting != null
                ? KeyboardControlProviderForTesting()
                : GetKeyboardControl();
            if (keyboardControl != 0)
            {
                return true;
            }

            if (EventSystemFocusedProviderForTesting != null)
            {
                return EventSystemFocusedProviderForTesting();
            }
            return IsEventSystemFocused();
        }

        internal static void ResetTestOverrides()
        {
            KeyboardControlProviderForTesting = null;
            EventSystemFocusedProviderForTesting = null;
        }

        // Isolated in their own methods so the JIT only resolves the Unity
        // statics when actually invoked. Tests that set both overrides never
        // enter these branches, so the test runner does not need to load
        // UnityEngine.UI to exercise IsTextFieldFocused().
        private static int GetKeyboardControl() => GUIUtility.keyboardControl;

        private static bool IsEventSystemFocused()
            => EventSystem.current != null
               && EventSystem.current.currentSelectedGameObject != null;
    }
}
