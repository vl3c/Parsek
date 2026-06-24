using System;
using System.Collections.Generic;

namespace Parsek
{
    internal static partial class CrewReservationManager
    {
        #region Testing & Serialization

        /// <summary>
        /// Clears replacement dictionary without roster access. For unit tests only.
        /// Also clears the #615 rescue-placed marker set so test fixtures see
        /// a clean rescue signal between cases.
        /// </summary>
        internal static void ResetReplacementsForTesting()
        {
            crewReplacements.Clear();
            rescuePlacedKerbals.Clear();
        }

        /// <summary>
        /// Seeds the crew replacement dictionary directly so a test can
        /// simulate the post-reserve state without driving a real
        /// <see cref="HighLogic.CurrentGame"/>. Used by the P1-review-second-pass
        /// regression that exercises the
        /// Rescue → Unreserve → ApplyToRoster sequence end-to-end.
        /// </summary>
        internal static void SeedReplacementForTesting(string originalName, string replacementName)
        {
            if (string.IsNullOrEmpty(originalName) || string.IsNullOrEmpty(replacementName))
                return;
            crewReplacements[originalName] = replacementName;
        }

        /// <summary>
        /// Test seam mirroring the dictionary-management half of
        /// <see cref="CleanUpReplacement"/> (the only half that touches the
        /// rescue-placed marker contract). The full
        /// <see cref="UnreserveCrewInSnapshot"/> path requires a live KSP
        /// <see cref="HighLogic.CurrentGame"/> + <see cref="KerbalRoster"/>,
        /// which xUnit cannot stand up. This seam asserts the production
        /// invariant (P1 review third pass) that the per-name unreserve does
        /// NOT clear the rescue-placed marker, and the marker stays set so
        /// every subsequent <see cref="KerbalsModule.ApplyToRoster"/> walk
        /// observes it — exercised by
        /// <see cref="RescueCompletionGuardTests"/>.
        /// </summary>
        internal static void CleanUpReplacementForTesting(string originalName)
        {
            if (string.IsNullOrEmpty(originalName)) return;
            // Mirrors the production CleanUpReplacement dictionary path:
            // remove the entry, do NOT touch the rescue-placed marker. The
            // roster-touching cleanup is intentionally omitted because
            // it has no effect on the marker contract.
            crewReplacements.Remove(originalName);
        }

        /// <summary>
        /// Phase 6 of Rewind-to-Staging (design §6.4 reconciliation table):
        /// returns a shallow copy of the replacement dictionary so the bundle
        /// can preserve it across a quicksave load. Keys are the reserved
        /// kerbal names; values are their stand-in replacements.
        /// </summary>
        internal static Dictionary<string, string> SnapshotReplacements()
        {
            return new Dictionary<string, string>(crewReplacements);
        }

        /// <summary>
        /// Phase 6 of Rewind-to-Staging (design §6.4 reconciliation table):
        /// re-applies a previously captured replacement dictionary after the
        /// quicksave load has replaced the live in-memory state. The method
        /// replaces — not merges — the current map so restoring after an
        /// in-memory swap does not duplicate entries.
        /// </summary>
        internal static void RestoreReplacements(IReadOnlyDictionary<string, string> replacements)
        {
            crewReplacements.Clear();
            // #615 P1 review: rewind-quickload reconciliation rewinds time —
            // the in-memory rescue-placed markers from after the quicksave's
            // captured UT no longer apply. The replacement dict is being
            // re-seeded from the bundle; rescue markers should restart empty
            // and re-populate as the post-load spawn pipeline runs.
            rescuePlacedKerbals.Clear();
            if (replacements == null) return;
            foreach (var kv in replacements)
            {
                if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                    crewReplacements[kv.Key] = kv.Value;
            }
        }

        /// <summary>
        /// Load crew replacement mappings from a ConfigNode.
        /// </summary>
        internal static void LoadCrewReplacements(ConfigNode node)
        {
            crewReplacements.Clear();
            // #615 P1 review: rescue-placed markers are session-scoped — the
            // rescue path runs in-flight, the marker drives the next walk's
            // ApplyToRoster decision, and the marker has no persisted home.
            // A cold load starts a new session, so any leftover entries from
            // a prior in-memory state are stale.
            rescuePlacedKerbals.Clear();

            ConfigNode replacementsNode = node.GetNode("CREW_REPLACEMENTS");
            if (replacementsNode == null)
            {
                CrewLog("Loaded 0 crew replacements (no CREW_REPLACEMENTS node)");
                return;
            }

            ConfigNode[] entries = replacementsNode.GetNodes("ENTRY");
            for (int i = 0; i < entries.Length; i++)
            {
                string original = entries[i].GetValue("original");
                string replacement = entries[i].GetValue("replacement");
                if (!string.IsNullOrEmpty(original) && !string.IsNullOrEmpty(replacement))
                {
                    crewReplacements[original] = replacement;
                }
            }

            CrewLog($"Loaded {crewReplacements.Count} crew replacement(s)");
        }

        internal static void SaveCrewReplacements(ConfigNode node)
        {
            if (crewReplacements.Count > 0)
            {
                ConfigNode replacementsNode = node.AddNode("CREW_REPLACEMENTS");
                foreach (var kvp in crewReplacements)
                {
                    ConfigNode entry = replacementsNode.AddNode("ENTRY");
                    entry.AddValue("original", kvp.Key);
                    entry.AddValue("replacement", kvp.Value);
                }
                CrewLog($"Saved {crewReplacements.Count} crew replacement(s)");
            }
        }

        #endregion
    }
}
