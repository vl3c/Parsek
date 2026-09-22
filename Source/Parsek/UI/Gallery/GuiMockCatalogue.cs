using System;
using System.Collections.Generic;

namespace Parsek.UI.Gallery
{
    /// <summary>
    /// The compiled catalogue of synthetic GUI states: the registry the automation-only
    /// <c>UiAction op=mock</c> applier resolves a <c>mockState=</c> token against.
    ///
    /// <para><b>It ships in the player's DLL and is inert there.</b> Nothing calls
    /// <see cref="All"/> unless <c>PARSEK_TEST_COMMANDS=1</c> armed the seam addon, the
    /// builders are lazy (a state's <c>Build</c> runs only when that state is applied),
    /// and the type has no static constructor work beyond building the list on first
    /// touch. The cost is some text and a few hundred object literals; the alternative -
    /// a data file - would let a renamed model field drift silently and let the gallery
    /// photograph a stale state while claiming coverage.</para>
    ///
    /// <para><b>The supported-window set is the honest statement of what this build can
    /// mock.</b> <c>op=mock</c> on any other window answers
    /// <c>mock-window-unsupported</c> and NAMES this set, so a lane learns what it has
    /// rather than getting a plausible empty capture.</para>
    /// </summary>
    internal static class GuiMockCatalogue
    {
        private static List<GuiMockState> all;
        private static Dictionary<string, GuiMockState> byId;

        /// <summary>The windows this build has an injection seam and a builder family
        /// for, in catalogue order. Named on every <c>mock-window-unsupported</c>
        /// refusal.</summary>
        internal static readonly string[] SupportedWindows =
        {
            GuiMockSession.KerbalsWindow,
            GuiMockSession.CareerWindow,
            GuiMockSession.StructureWindow,
        };

        /// <summary>Every catalogue state, in declaration order (which is the order a
        /// gallery run walks and therefore the order a mirror rail reads).</summary>
        internal static IReadOnlyList<GuiMockState> All
        {
            get
            {
                EnsureBuilt();
                return all;
            }
        }

        /// <summary>Resolves a state id. Ordinal, fail-closed, case-sensitive - the seam
        /// rule for every closed vocabulary.</summary>
        internal static GuiMockState ById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            EnsureBuilt();
            GuiMockState state;
            return byId.TryGetValue(id, out state) ? state : null;
        }

        /// <summary>The states of one window, in catalogue order.</summary>
        internal static List<GuiMockState> ForWindow(string window)
        {
            EnsureBuilt();
            var rows = new List<GuiMockState>();
            for (int i = 0; i < all.Count; i++)
                if (string.Equals(all[i].Window, window, StringComparison.Ordinal))
                    rows.Add(all[i]);
            return rows;
        }

        /// <summary>Whether this build can mock <paramref name="window"/> at all.</summary>
        internal static bool IsSupportedWindow(string window)
        {
            for (int i = 0; i < GuiMockCatalogue.SupportedWindows.Length; i++)
                if (string.Equals(GuiMockCatalogue.SupportedWindows[i], window,
                                  StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>
        /// The main-window LAUNCHER surface a mockable window is reached through, when it
        /// has one.
        ///
        /// <para>It exists because the complexity mode decides whether a window can be on
        /// screen at all: Basic HIDES the Kerbals and Career State launchers
        /// (<c>UiSurfaceVisibility.IsVisible</c>) and the mode switch FORCE-CLOSES both
        /// (<c>ParsekUI.BuildGatedWindowCloseSet</c>), so applying a mock to one of them
        /// in Basic would photograph a window no player can open - and the coverage audit
        /// already established those index rows are UNREACHABLE rather than uncaptured.
        /// The applier refuses with <c>mock-refused-mode</c> instead of producing the
        /// picture.</para>
        ///
        /// <para>Structure List has NO launcher: it is opened from a Missions or Logistics
        /// row, it is not in the gated close set, and it draws in both modes - so it
        /// answers false here and is mockable in either.</para>
        /// </summary>
        internal static bool TryGetLauncherSurface(string window, out UiSurface surface)
        {
            surface = default(UiSurface);
            if (string.Equals(window, GuiMockSession.KerbalsWindow, StringComparison.Ordinal))
            {
                surface = UiSurface.MainButtonKerbals;
                return true;
            }
            if (string.Equals(window, GuiMockSession.CareerWindow, StringComparison.Ordinal))
            {
                surface = UiSurface.MainButtonCareer;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Whether <paramref name="window"/> can be mocked in <paramref name="mode"/>:
        /// true when it has no launcher surface, or its launcher draws in that mode.
        /// </summary>
        internal static bool IsMockableInMode(string window, UiComplexityMode mode)
        {
            UiSurface surface;
            if (!TryGetLauncherSurface(window, out surface)) return true;
            return UiSurfaceVisibility.IsVisible(surface, mode);
        }

        /// <summary>The supported set, comma-joined, for the
        /// <c>mock-window-unsupported</c> message.</summary>
        internal static string SupportedWindowNames
            => string.Join(",", SupportedWindows);

        /// <summary>The window tokens the catalogue actually carries states for,
        /// comma-joined in catalogue order. Carried by the describe payload.</summary>
        internal static string WindowsWithStates()
        {
            EnsureBuilt();
            var seen = new List<string>();
            for (int i = 0; i < all.Count; i++)
            {
                string w = all[i].Window;
                if (!seen.Contains(w)) seen.Add(w);
            }
            return seen.Count == 0 ? "-" : string.Join(",", seen.ToArray());
        }

        private static void EnsureBuilt()
        {
            if (all != null) return;
            var rows = new List<GuiMockState>();
            GuiMockKerbalsStates.Append(rows);
            GuiMockCareerStates.Append(rows);
            GuiMockStructureStates.Append(rows);
            var index = new Dictionary<string, GuiMockState>(StringComparer.Ordinal);
            for (int i = 0; i < rows.Count; i++)
            {
                // A duplicate id would silently shadow a state and, worse, produce two
                // captures under one label in a gallery run - which nothing in the
                // harness validates. The unit suite asserts uniqueness too; this keeps
                // the live index deterministic if it ever slipped past.
                if (!index.ContainsKey(rows[i].Id)) index[rows[i].Id] = rows[i];
            }
            byId = index;
            all = rows;
        }
    }
}
