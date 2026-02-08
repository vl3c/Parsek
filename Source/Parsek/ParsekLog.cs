using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Shared logging utilities for the Parsek mod.
    /// </summary>
    public static class ParsekLog
    {
        public static void Log(string message)
        {
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
