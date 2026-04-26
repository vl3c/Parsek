using System;
using KSP.UI.Screens;

namespace Parsek
{
    /// <summary>
    /// Wraps stock <see cref="PauseMenu"/> static accessors so custom IMGUI
    /// surfaces (ghost icons, ghost labels, ghost action panel) can hide while
    /// the Esc / pause overlay is up.
    ///
    /// Stock map / vessel labels live on the KSP Canvas and are sorted under
    /// the pause overlay automatically; our IMGUI layer is not, so without
    /// this gate ghost icons render on top of the pause menu.
    ///
    /// <see cref="PauseMenu.fetch"/> can be null pre-Awake or post-Destroy,
    /// which would NRE inside <see cref="PauseMenu.isOpen"/>. We guard with
    /// <see cref="PauseMenu.exists"/> and additionally swallow exceptions so
    /// any future KSP refactor of these accessors degrades to "not paused"
    /// rather than tearing the OnGUI hook with an unhandled exception.
    /// </summary>
    internal static class PauseMenuGate
    {
        /// <summary>
        /// Test-only override for <see cref="IsPauseMenuOpen"/>. Production
        /// leaves this null and reads the live KSP API. Tests assign a
        /// deterministic delegate and clear it via
        /// <see cref="ResetForTesting"/> in their dispose path.
        /// </summary>
        internal static Func<bool> ProbeForTesting;

        internal static bool IsPauseMenuOpen()
        {
            Func<bool> probe = ProbeForTesting;
            if (probe != null)
                return probe();

            try
            {
                return PauseMenu.exists && PauseMenu.isOpen;
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited("PauseMenuGate", "probe-failure",
                    "PauseMenu probe failed: " + ex.GetType().Name + ": " + ex.Message,
                    5.0);
                return false;
            }
        }

        internal static void ResetForTesting()
        {
            ProbeForTesting = null;
        }
    }
}
