using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KSP.UI;

namespace Parsek.Patches
{
    /// <summary>
    /// Removes reserved kerbals from auto-assigned crew slots in the VAB/SPH
    /// crew assignment dialog, replacing them with their stand-ins.
    ///
    /// KSP's KerbalRoster.DefaultCrewForVessel auto-assigns Available kerbals
    /// into command pod seats BEFORE the crew dialog is displayed. Reserved
    /// kerbals remain at Available status (by design — changing rosterStatus
    /// caused tug-of-war bugs), so they get auto-assigned into the manifest.
    ///
    /// The existing CrewDialogFilterPatch removes reserved kerbals from the
    /// "available crew" list, but by that point they're already assigned to
    /// seats and appear in the vessel crew panel.
    ///
    /// This patch intercepts RefreshCrewLists (called before UI list creation)
    /// and walks the VesselCrewManifest to replace any reserved crew with their
    /// stand-ins. If no stand-in exists for a reserved kerbal, the seat is left
    /// empty.
    /// </summary>
    [HarmonyPatch]
    internal static class CrewAutoAssignPatch
    {
        private const string Tag = "CrewAutoAssign";

        /// <summary>
        /// Result of evaluating a single crew slot for auto-assign filtering.
        /// </summary>
        internal enum SlotAction
        {
            /// <summary>Crew member is not reserved — leave in seat.</summary>
            Keep,
            /// <summary>Crew member is reserved and has a stand-in — swap.</summary>
            Swap,
            /// <summary>Crew member is reserved but no stand-in available — clear seat.</summary>
            Clear
        }

        /// <summary>
        /// Pure decision method: given a crew name, determines what action the
        /// auto-assign patch should take. Testable without Unity or KSP UI types.
        /// </summary>
        /// <param name="crewName">Name of the crew member in the seat.</param>
        /// <param name="kerbals">KerbalsModule for reservation checks.</param>
        /// <param name="replacements">Reserved-name to stand-in-name mapping.</param>
        /// <param name="standInName">Output: stand-in name if action is Swap, null otherwise.</param>
        /// <returns>The action to take for this crew slot.</returns>
        internal static SlotAction DecideSlotAction(
            string crewName,
            KerbalsModule kerbals,
            IReadOnlyDictionary<string, string> replacements,
            out string standInName)
        {
            standInName = null;

            if (string.IsNullOrEmpty(crewName))
                return SlotAction.Keep;

            if (kerbals == null)
                return SlotAction.Keep;

            if (!kerbals.ShouldFilterFromCrewDialog(crewName))
                return SlotAction.Keep;

            // Crew is reserved — check for stand-in
            if (replacements != null && replacements.TryGetValue(crewName, out standInName)
                && !string.IsNullOrEmpty(standInName))
            {
                return SlotAction.Swap;
            }

            standInName = null;
            return SlotAction.Clear;
        }

        static MethodBase TargetMethod()
        {
            var method = AccessTools.Method(
                typeof(BaseCrewAssignmentDialog),
                nameof(BaseCrewAssignmentDialog.RefreshCrewLists),
                new[]
                {
                    typeof(VesselCrewManifest),
                    typeof(bool),
                    typeof(bool),
                    typeof(Func<PartCrewManifest, bool>)
                });

            if (method == null)
                ParsekLog.Warn(Tag,
                    "BaseCrewAssignmentDialog.RefreshCrewLists not found " +
                    "— crew auto-assign filtering will not apply. " +
                    "Harmony will skip this patch (caught by ParsekHarmony try/catch).");

            return method;
        }

        /// <summary>
        /// Prefix that walks the incoming VesselCrewManifest and replaces any
        /// reserved kerbals with their stand-ins before the UI lists are built.
        /// </summary>
        static void Prefix(VesselCrewManifest crewManifest)
        {
            if (crewManifest == null) return;

            var kerbals = LedgerOrchestrator.Kerbals;
            if (kerbals == null) return;

            var replacements = CrewReservationManager.CrewReplacements;
            if (replacements.Count == 0) return;

            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null) return;

            int swapCount = 0;
            int clearCount = 0;

            foreach (PartCrewManifest pcm in crewManifest.PartManifests)
            {
                for (int i = 0; i < pcm.partCrew.Length; i++)
                {
                    string crewName = pcm.partCrew[i];
                    var action = DecideSlotAction(crewName, kerbals, replacements,
                        out string standInName);

                    switch (action)
                    {
                        case SlotAction.Swap:
                        {
                            ProtoCrewMember standIn = roster[standInName];
                            if (standIn != null && !crewManifest.Contains(standIn))
                            {
                                pcm.RemoveCrewFromSeat(i);
                                pcm.AddCrewToSeat(standIn, i);
                                swapCount++;

                                ParsekLog.Verbose(Tag,
                                    $"Swapped reserved '{crewName}' -> stand-in '{standInName}' " +
                                    $"in part '{pcm.PartInfo?.title ?? "unknown"}' seat {i}");
                            }
                            else
                            {
                                // Stand-in already assigned elsewhere or not in roster
                                pcm.RemoveCrewFromSeat(i);
                                clearCount++;

                                ParsekLog.Verbose(Tag,
                                    $"Cleared reserved '{crewName}' from part " +
                                    $"'{pcm.PartInfo?.title ?? "unknown"}' seat {i} " +
                                    $"(stand-in '{standInName}' unavailable)");
                            }
                            break;
                        }
                        case SlotAction.Clear:
                        {
                            pcm.RemoveCrewFromSeat(i);
                            clearCount++;

                            ParsekLog.Verbose(Tag,
                                $"Cleared reserved '{crewName}' from part " +
                                $"'{pcm.PartInfo?.title ?? "unknown"}' seat {i} " +
                                "(no stand-in registered)");
                            break;
                        }
                    }
                }
            }

            if (swapCount > 0 || clearCount > 0)
            {
                ParsekLog.Info(Tag,
                    $"Crew manifest adjusted: {swapCount} reserved crew replaced with stand-ins, " +
                    $"{clearCount} seats cleared");
            }
        }
    }
}
