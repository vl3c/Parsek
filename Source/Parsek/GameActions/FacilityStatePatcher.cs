using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Patches KSP facility level and destruction state from the facilities module.
    /// Kept behind KspStatePatcher wrappers so existing patch order and call signatures stay stable.
    /// </summary>
    internal static class FacilityStatePatcher
    {
        private const string Tag = "KspStatePatcher";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private static void VerboseStablePatchState(string identity, string stateKey, string message)
        {
            ParsekLog.VerboseOnChange(Tag, identity, stateKey, message);
        }

        /// <summary>
        /// Patches KSP's facility levels to match the module's derived state.
        /// Reads from ScenarioUpgradeableFacilities.protoUpgradeables and sets
        /// levels via UpgradeableFacility.SetLevel, following the same pattern
        /// as the old ActionReplay.ReplayFacilityUpgrade (now removed).
        /// No-op if protoUpgradeables is null.
        /// </summary>
        internal static void PatchFacilities(FacilitiesModule facilities)
        {
            if (facilities == null)
            {
                ParsekLog.Warn(Tag, "PatchFacilities: null FacilitiesModule — skipping");
                return;
            }

            if (ScenarioUpgradeableFacilities.protoUpgradeables == null)
            {
                VerboseStablePatchState("patch-skip|facilities|proto-upgradeables", "proto-null",
                    "PatchFacilities: protoUpgradeables is null — skipping");
                return;
            }

            var allFacilities = facilities.GetAllFacilities();
            int patchedCount = 0;
            int skippedCount = 0;
            int notFoundCount = 0;

            foreach (var kvp in allFacilities)
            {
                string facilityId = kvp.Key;
                var state = kvp.Value;

                ScenarioUpgradeableFacilities.ProtoUpgradeable proto;
                if (!ScenarioUpgradeableFacilities.protoUpgradeables.TryGetValue(
                        facilityId, out proto)
                    || proto.facilityRefs == null || proto.facilityRefs.Count == 0)
                {
                    notFoundCount++;
                    continue;
                }

                var facility = proto.facilityRefs[0];
                if (facility == null)
                {
                    notFoundCount++;
                    continue;
                }

                int currentLevel = facility.FacilityLevel;
                int targetLevel = state.Level;

                if (currentLevel == targetLevel)
                {
                    skippedCount++;
                    continue;
                }

                facility.SetLevel(targetLevel);
                patchedCount++;

                ParsekLog.Verbose(Tag,
                    $"PatchFacilities: '{facilityId}' level {currentLevel.ToString(IC)} -> " +
                    $"{targetLevel.ToString(IC)} (destroyed={state.Destroyed.ToString(IC)})");
            }

            // Bug #596: gate INFO on changed-state-or-not-found-diagnostic only.
            // skippedCount increments when a facility is already at its target
            // level (no-op pass), so a steady-state non-empty facility list
            // would still emit the INFO summary on every recalc if `skipped`
            // counted toward the gate. Use `patched + notFound > 0` so INFO
            // fires only when real game state changed (`patched`) or when a
            // facility lookup miss is worth surfacing (`notFound`). Skipped-
            // only summaries (steady state, nothing to do) and the all-zero
            // empty-totals case both route through `VerboseRateLimited` so the
            // diagnostic stays available when investigating without flooding
            // INFO.
            int materialWork = patchedCount + notFoundCount;
            if (materialWork > 0)
            {
                ParsekLog.Info(Tag,
                    $"PatchFacilities: levels patched={patchedCount.ToString(IC)}, " +
                    $"skipped={skippedCount.ToString(IC)}, " +
                    $"notFound={notFoundCount.ToString(IC)}, " +
                    $"total={allFacilities.Count}");
            }
            else if (skippedCount > 0)
            {
                ParsekLog.VerboseRateLimited(Tag, "patch-facilities-skipped-only",
                    $"PatchFacilities: skipped-only steady state " +
                    $"(patched=0, skipped={skippedCount.ToString(IC)}, " +
                    $"notFound=0, total={allFacilities.Count})");
            }
            else
            {
                ParsekLog.VerboseRateLimited(Tag, "patch-facilities-empty",
                    $"PatchFacilities: nothing to patch (total={allFacilities.Count})");
            }

            // Patch destruction state via DestructibleBuilding components.
            // Collect all destructibles once (expensive FindObjectsOfType call),
            // then match against facility states that have destruction data.
            PatchDestructionState(allFacilities);
        }

        /// <summary>
        /// Patches DestructibleBuilding components to match the module's destroyed/intact state.
        /// Collects all DestructibleBuilding objects once, then iterates facilities to find matches.
        /// Uses exact ID matching between FacilityId and DestructibleBuilding.id.
        /// No-op if no DestructibleBuilding objects are found (e.g. not in KSC scene).
        /// </summary>
        internal static void PatchDestructionState(
            IReadOnlyDictionary<string, FacilitiesModule.FacilityState> allFacilities)
        {
            if (KspStatePatcher.SuppressUnityCallsForTesting)
            {
                VerboseStablePatchState("patch-skip|destruction|test-suppression", "suppressed",
                    "PatchDestructionState: SuppressUnityCallsForTesting — skipping");
                return;
            }

            var destructibles = UnityEngine.Object.FindObjectsOfType<DestructibleBuilding>();
            if (destructibles == null || destructibles.Length == 0)
            {
                VerboseStablePatchState("patch-skip|destruction|destructibles", "none-found",
                    "PatchDestructionState: no DestructibleBuilding objects found (not in KSC scene?) — skipping");
                return;
            }

            // Build a lookup from building id to DestructibleBuilding for efficient matching
            var buildingById = new Dictionary<string, DestructibleBuilding>(
                destructibles.Length);
            foreach (var db in destructibles)
            {
                if (db != null && !string.IsNullOrEmpty(db.id))
                    buildingById[db.id] = db;
            }

            int demolishedCount = 0;
            int repairedCount = 0;
            int matchedCount = 0;
            int noMatchCount = 0;

            foreach (var kvp in allFacilities)
            {
                string facilityId = kvp.Key;
                var state = kvp.Value;

                DestructibleBuilding db;
                if (buildingById.TryGetValue(facilityId, out db))
                {
                    matchedCount++;

                    if (state.Destroyed && !db.IsDestroyed)
                    {
                        db.Demolish();
                        demolishedCount++;
                        ParsekLog.Verbose(Tag,
                            $"PatchDestructionState: demolished '{db.id}'");
                    }
                    else if (!state.Destroyed && db.IsDestroyed)
                    {
                        db.Repair();
                        repairedCount++;
                        ParsekLog.Verbose(Tag,
                            $"PatchDestructionState: repaired '{db.id}'");
                    }
                }
                else
                {
                    noMatchCount++;
                }
            }

            ParsekLog.Info(Tag,
                $"PatchDestructionState: demolished={demolishedCount.ToString(IC)}, " +
                $"repaired={repairedCount.ToString(IC)}, " +
                $"matched={matchedCount.ToString(IC)}, " +
                $"noMatch={noMatchCount.ToString(IC)}, " +
                $"buildings={buildingById.Count}, " +
                $"facilities={allFacilities.Count}");
        }
    }
}
