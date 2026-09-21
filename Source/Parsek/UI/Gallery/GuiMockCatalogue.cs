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
            for (int i = 0; i < SupportedWindows.Length; i++)
                if (string.Equals(SupportedWindows[i], window, StringComparison.Ordinal))
                    return true;
            return false;
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
