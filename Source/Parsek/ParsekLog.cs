using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Shared logging utilities for the Parsek mod.
    /// </summary>
    public static class ParsekLog
    {
        // When true, suppresses Debug.Log calls (for unit testing outside Unity)
        internal static bool SuppressLogging;

        public static void Log(string message)
        {
            if (!SuppressLogging)
                Debug.Log($"[Parsek] {message}");
        }

        public static void ScreenMessage(string message, float duration)
        {
            ScreenMessages.PostScreenMessage(
                $"[Parsek] {message}",
                duration,
                ScreenMessageStyle.UPPER_CENTER);
        }
    }
}
