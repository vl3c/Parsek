using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

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
        private static readonly HashSet<string> lastPatchedFacilityIds =
            new HashSet<string>(System.StringComparer.Ordinal);
        private static readonly HashSet<string> defaultFacilityIdsOnNextPatch =
            new HashSet<string>(System.StringComparer.Ordinal);
        private static string patchHistorySaveFolder;
        private static string defaultFacilityIdsSaveFolder;

        private static void VerboseStablePatchState(string identity, string stateKey, string message)
        {
            ParsekLog.VerboseOnChange(Tag, identity, stateKey, message);
        }

        // LedgerTrace-only: one facility level change captured at the SetLevel site for
        // the post-loop Tier-B change line + Tier-C read-back reconcile (facilities have
        // no #1098 changed-set).
        private struct LedgerFacilityChange
        {
            public string FacilityId;
            public int OldLevel;
            public int TargetLevel;
            public int ActualLevel;
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
            ResetPatchHistoryForSaveChange(GetCurrentSaveFolderForPatchHistory());

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

            List<string> facilityIdsToDefault = null;
            if (defaultFacilityIdsOnNextPatch.Count > 0)
            {
                facilityIdsToDefault = new List<string>(defaultFacilityIdsOnNextPatch);
                ParsekLog.Verbose(Tag,
                    $"PatchFacilities: forcing default targets for {facilityIdsToDefault.Count.ToString(IC)} tombstoned facility id(s)");
            }

            var allFacilities = BuildFacilityPatchTargets(
                facilities.GetAllFacilities(),
                lastPatchedFacilityIds,
                facilityIdsToDefault);
            int patchedCount = 0;
            int skippedCount = 0;
            int notFoundCount = 0;
            int buildingEntryCount = 0;
            // LedgerTrace: facilities have NO #1098 changed-set, so we accumulate the
            // changed ids locally (id:old->new) for Tier-B change lines + Tier-C
            // read-back below. Only populated when tracing is on. (This is the ONLY new
            // changed-set LedgerTrace adds; every other tier reuses the existing #1098
            // sets.)
            List<LedgerFacilityChange> ledgerTraceChanges =
                LedgerTrace.IsEnabled ? new List<LedgerFacilityChange>() : null;

            foreach (var kvp in allFacilities)
            {
                string facilityId = kvp.Key;
                var state = kvp.Value;

                // A destruction / repair is keyed by one DestructibleBuilding's id, which has
                // no level of its own: PatchDestructionState below owns it. Counting it as a
                // level-lookup miss would print notFound on every recalc.
                if (IsDestructibleBuildingId(facilityId))
                {
                    buildingEntryCount++;
                    // A tombstoned destruction / repair schedules a one-shot default for its
                    // building id too; buildings have no default to restore (see
                    // PatchLiveDestructionState), so the entry is dropped here.
                    if (defaultFacilityIdsOnNextPatch.Remove(facilityId) &&
                        defaultFacilityIdsOnNextPatch.Count == 0)
                    {
                        defaultFacilityIdsSaveFolder = null;
                    }
                    continue;
                }

                int targetLedgerLevel = state.Level;
                int targetLevel = ToKspFacilityLevel(targetLedgerLevel);

                ParsekLog.Verbose(Tag,
                    $"PatchFacilities: resolved '{facilityId}' " +
                    $"ledgerLevel={targetLedgerLevel.ToString(IC)} -> " +
                    $"targetLevel={targetLevel.ToString(IC)}");

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
                lastPatchedFacilityIds.Add(facilityId);
                if (defaultFacilityIdsOnNextPatch.Remove(facilityId) &&
                    defaultFacilityIdsOnNextPatch.Count == 0)
                {
                    defaultFacilityIdsSaveFolder = null;
                }

                int currentLevel = facility.FacilityLevel;

                FacilityLevelDecision decision = ResolveFacilityLevelPatch(currentLevel, targetLedgerLevel);
                if (!decision.ShouldWrite)
                {
                    skippedCount++;
                    continue;
                }

                facility.SetLevel(decision.TargetKspLevel);
                patchedCount++;

                ParsekLog.Verbose(Tag,
                    $"PatchFacilities: '{facilityId}' level {currentLevel.ToString(IC)} -> " +
                    $"{targetLevel.ToString(IC)} (ledgerLevel={targetLedgerLevel.ToString(IC)}, " +
                    $"destroyed={state.Destroyed.ToString(IC)})");

                // LedgerTrace: capture the change + the live read-back level (after
                // SetLevel) for the post-loop Tier-B / Tier-C emit. Reading
                // facility.FacilityLevel here reuses the live ref we just wrote.
                if (ledgerTraceChanges != null)
                    ledgerTraceChanges.Add(new LedgerFacilityChange
                    {
                        FacilityId = facilityId,
                        OldLevel = currentLevel,
                        TargetLevel = targetLevel,
                        ActualLevel = facility.FacilityLevel
                    });
            }

            // LedgerTrace Tier-B (per-facility change line) + Tier-C (level read-back
            // reconcile). Emitted after the loop so the patch path stays tight.
            if (ledgerTraceChanges != null)
            {
                foreach (var change in ledgerTraceChanges)
                {
                    LedgerTrace.EmitOnChange("facility", change.FacilityId,
                        change.OldLevel.ToString(IC) + "->" + change.TargetLevel.ToString(IC));
                    if (LedgerTrace.IsFacilityLevelMismatch(change.TargetLevel, change.ActualLevel))
                        LedgerTrace.EmitAnomaly("facility", change.FacilityId, "ledger-vs-truth",
                            "targetLevel=" + change.TargetLevel.ToString(IC)
                            + " actualLevel=" + change.ActualLevel.ToString(IC));
                }
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

            if (buildingEntryCount > 0)
                ParsekLog.VerboseRateLimited(Tag, "patch-facilities-building-entries",
                    $"PatchFacilities: {buildingEntryCount.ToString(IC)} destructible-building " +
                    "entr(ies) skipped by the level patch (no level)");

            // Building destroyed / intact state is NOT taken from this walk: see
            // PatchLiveDestructionState for why and what it reads instead.
            PatchLiveDestructionState();
        }

        /// <summary>
        /// Patches live KSC buildings from the ledger's building state AT LIVE UT.
        ///
        /// <para>The contract. A walk can run with no UT cutoff (commit, a cold load before
        /// the clock is ready, scene loads, the warp-start visual patch), and its
        /// FacilitiesModule state then includes destruction / repair rows dated after now -
        /// applying that to a live building would knock down a building a reverted flight
        /// only destroys later in the timeline, or repair one whose repair is still in the
        /// future after a rewind. So this reads the effective ledger (ELS) directly and folds,
        /// per building, the LAST FacilityDestruction / FacilityRepair row at or before live
        /// UT (<see cref="ComputeBuildingDestroyedAtUt"/>), independent of the walk's
        /// cutoff.</para>
        ///
        /// <para>It acts only when that row CONTRADICTS the live building
        /// (<see cref="ResolveLiveDestructionPatch"/>). A building with no row at or before
        /// now is never touched: stock state travels with every save, rewind and revert, and
        /// the ledger cannot tell a building stock restored (a revert, a quicksave) from one it
        /// should restore. Nothing is "restored" from the absence of a row, which is also why a
        /// tombstoned destruction schedules no default for its building.</para>
        ///
        /// <para>It does not act at all while a flight is recording or its tree is pending
        /// (<see cref="ResolveDestructionPatchSkipReason"/>): that flight's collapses are
        /// tagged and not in the ledger yet, so the ledger's last row can be stale in either
        /// direction. Nor before the universe clock is ready (UT &lt;= 0 on a cold load).</para>
        /// </summary>
        internal static void PatchLiveDestructionState()
        {
            if (KspStatePatcher.SuppressUnityCallsForTesting)
            {
                VerboseStablePatchState("patch-skip|destruction|test-suppression", "suppressed",
                    "PatchDestructionState: SuppressUnityCallsForTesting - skipping");
                return;
            }

            double liveUt = ReadLiveUniversalTime();
            string skipReason = ResolveDestructionPatchSkipReason(
                GameStateRecorder.HasLiveRecorder(),
                GameStateRecorder.HasActiveUncommittedTree(),
                RecordingStore.HasPendingTree,
                liveUt);
            if (skipReason != null)
            {
                VerboseStablePatchState("patch-skip|destruction|gate", skipReason,
                    $"PatchDestructionState: skipped ({skipReason})");
                return;
            }

            PatchDestructionState(ComputeBuildingDestroyedAtUt(EffectiveState.ComputeELS(), liveUt), liveUt);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double ReadLiveUniversalTime()
        {
            try { return Planetarium.GetUniversalTime(); }
            catch (System.Exception) { return 0.0; }
        }

        /// <summary>
        /// Pure: why the live building patch must not act now, or null when it may. A flight
        /// that is recording, or whose tree is still uncommitted / pending, owns collapses the
        /// ledger has not received; a clock at or below zero is a cold load that has not
        /// read the save's UT yet.
        /// </summary>
        internal static string ResolveDestructionPatchSkipReason(
            bool hasLiveRecorder, bool hasActiveUncommittedTree, bool hasPendingTree, double liveUt)
        {
            if (hasLiveRecorder) return "live recorder active";
            if (hasActiveUncommittedTree) return "active uncommitted flight tree";
            if (hasPendingTree) return "pending tree";
            if (!(liveUt > 0.0)) return "universe clock not ready";
            return null;
        }

        /// <summary>
        /// Pure: per DestructibleBuilding id, whether the ledger says it is destroyed at
        /// <paramref name="liveUt"/> - the kind of its LAST FacilityDestruction /
        /// FacilityRepair row with <c>UT &lt;= liveUt</c>, ordered by UT, then sequence, then
        /// list position. A building with no such row is absent from the result.
        /// </summary>
        internal static Dictionary<string, bool> ComputeBuildingDestroyedAtUt(
            IReadOnlyList<GameAction> effectiveActions, double liveUt)
        {
            var result = new Dictionary<string, bool>(System.StringComparer.Ordinal);
            if (effectiveActions == null)
                return result;

            var rows = new List<KeyValuePair<int, GameAction>>();
            for (int i = 0; i < effectiveActions.Count; i++)
            {
                GameAction a = effectiveActions[i];
                if (a == null || string.IsNullOrEmpty(a.FacilityId)) continue;
                if (a.Type != GameActionType.FacilityDestruction && a.Type != GameActionType.FacilityRepair)
                    continue;
                if (a.UT > liveUt) continue;
                rows.Add(new KeyValuePair<int, GameAction>(i, a));
            }
            rows.Sort((x, y) =>
            {
                int c = x.Value.UT.CompareTo(y.Value.UT);
                if (c != 0) return c;
                c = x.Value.Sequence.CompareTo(y.Value.Sequence);
                return c != 0 ? c : x.Key.CompareTo(y.Key);
            });
            for (int i = 0; i < rows.Count; i++)
                result[rows[i].Value.FacilityId] = rows[i].Value.Type == GameActionType.FacilityDestruction;
            return result;
        }

        /// <summary>
        /// Pure decision for one live building. <paramref name="ledgerDestroyedAtUt"/> is the
        /// ledger's state at live UT, or null when the ledger has no row for the building at
        /// or before now (never acted on). A building between states (stock's collapse or
        /// repair animation running) is left alone. Otherwise demolish an intact building the
        /// ledger says is destroyed, and repair a destroyed one the ledger says was repaired.
        /// </summary>
        internal static DestructionPatchAction ResolveLiveDestructionPatch(
            bool? ledgerDestroyedAtUt, bool isIntact, bool isDestroyed)
        {
            if (!ledgerDestroyedAtUt.HasValue)
                return DestructionPatchAction.None;
            return ResolveDestructionPatch(ledgerDestroyedAtUt.Value, isIntact, isDestroyed);
        }

        /// <summary>
        /// Pure: whether a ledger facility id names one DestructibleBuilding
        /// (<c>SpaceCenter/LaunchPad/Facility/mainBuilding</c>, the key of a FacilityDestruction /
        /// FacilityRepair) rather than an upgradeable facility (<c>SpaceCenter/LaunchPad</c>, the
        /// key of a FacilityUpgrade). Stock's <c>ScenarioDestructibles.GetFacility</c> splits a
        /// building id at its second '/', so two or more separators mean a building.
        /// </summary>
        internal static bool IsDestructibleBuildingId(string facilityId)
        {
            if (string.IsNullOrEmpty(facilityId))
                return false;
            int slashes = 0;
            for (int i = 0; i < facilityId.Length; i++)
            {
                if (facilityId[i] == '/' && ++slashes >= 2)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Converts the ledger/module facility tier (1 = basic, 2 = upgraded,
        /// 3 = fully upgraded) to KSP's zero-based UpgradeableFacility.SetLevel index.
        /// </summary>
        internal static int ToKspFacilityLevel(int ledgerLevel)
        {
            if (ledgerLevel <= 1) return 0;
            if (ledgerLevel >= 3) return 2;
            return ledgerLevel - 1;
        }

        /// <summary>
        /// Pure decision core for one facility inside <see cref="PatchFacilities"/>
        /// (apply-boundary test seam, audit gap 4 / rec #5). Maps the ledger tier to KSP's
        /// zero-based level via <see cref="ToKspFacilityLevel"/> and decides whether a write is
        /// needed by comparing against the live KSP level. Catches the off-by-one where a
        /// ledger tier would be compared directly against a (zero-based) KSP level. Pure: no
        /// KSP singletons touched.
        /// </summary>
        internal readonly struct FacilityLevelDecision
        {
            /// <summary>True when the live KSP level differs from the mapped target level.</summary>
            internal readonly bool ShouldWrite;
            /// <summary>The zero-based KSP level passed to UpgradeableFacility.SetLevel.</summary>
            internal readonly int TargetKspLevel;

            internal FacilityLevelDecision(bool shouldWrite, int targetKspLevel)
            {
                ShouldWrite = shouldWrite;
                TargetKspLevel = targetKspLevel;
            }
        }

        /// <summary>
        /// Resolves a facility's level patch outcome from the live KSP level + the ledger tier.
        /// Mirrors <see cref="PatchFacilities"/>: map the ledger tier to a KSP level, then write
        /// iff it differs from the current KSP level. Pure.
        /// </summary>
        internal static FacilityLevelDecision ResolveFacilityLevelPatch(int currentKspLevel, int ledgerLevel)
        {
            int targetKspLevel = ToKspFacilityLevel(ledgerLevel);
            return new FacilityLevelDecision(currentKspLevel != targetKspLevel, targetKspLevel);
        }

        /// <summary>
        /// Patches DestructibleBuilding components to the ledger's building state at live UT
        /// (<paramref name="ledgerDestroyedAtUt"/>, from <see cref="ComputeBuildingDestroyedAtUt"/>),
        /// acting only where it contradicts the live building. Collects all DestructibleBuilding
        /// objects once and matches by exact id. No-op if none are found (not in a scene with
        /// KSC buildings).
        /// </summary>
        internal static void PatchDestructionState(
            IReadOnlyDictionary<string, bool> ledgerDestroyedAtUt, double liveUt = 0.0)
        {
            if (ledgerDestroyedAtUt == null)
                ledgerDestroyedAtUt = new Dictionary<string, bool>();
            if (KspStatePatcher.SuppressUnityCallsForTesting)
            {
                VerboseStablePatchState("patch-skip|destruction|test-suppression", "suppressed",
                    "PatchDestructionState: SuppressUnityCallsForTesting — skipping");
                return;
            }

            DestructibleBuilding[] destructibles;
            try
            {
                destructibles = UnityEngine.Object.FindObjectsOfType<DestructibleBuilding>();
            }
            catch (System.Exception ex)
            {
                VerboseStablePatchState(
                    "patch-skip|destruction|find-objects",
                    ex.GetType().Name,
                    $"PatchDestructionState: FindObjectsOfType unavailable - skipping ({ex.GetType().Name}: {ex.Message})");
                return;
            }

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
            int settlingCount = 0;
            int unloadedCount = 0;
            int matchedCount = 0;
            int noMatchCount = 0;

            foreach (var kvp in ledgerDestroyedAtUt)
            {
                string facilityId = kvp.Key;

                DestructibleBuilding db;
                if (buildingById.TryGetValue(facilityId, out db))
                {
                    matchedCount++;

                    // A scene load runs Parsek's OnLoad recalc BEFORE ScenarioDestructibles has
                    // loaded the save into the buildings (measured, KB-1 2026-09-23_2018: the
                    // patch ran, then "[ScenarioDestructibles]: Loading... 0 objects
                    // registered"), so a building not yet registered with the current
                    // scenario still shows its default intact state, not the save's.
                    if (!IsBuildingStateLoaded(IsRegisteredWithScenario(db)))
                    {
                        unloadedCount++;
                        continue;
                    }

                    DestructionPatchAction patchAction =
                        ResolveLiveDestructionPatch(kvp.Value, db.IsIntact, db.IsDestroyed);
                    if (patchAction == DestructionPatchAction.Settling)
                    {
                        // Mid-collapse or mid-repair: stock's own animation is already
                        // taking it where it is going, and Demolish() / Repair() would both
                        // return early. Touching it here only logged a patch that did nothing.
                        settlingCount++;
                    }
                    else if (patchAction == DestructionPatchAction.Demolish)
                    {
                        db.Demolish();
                        demolishedCount++;
                        ParsekLog.Info(Tag,
                            $"PatchDestructionState: demolished '{db.id}' (ledger: destroyed at or " +
                            $"before UT {liveUt.ToString("F1", IC)})");
                    }
                    else if (patchAction == DestructionPatchAction.Repair)
                    {
                        db.Repair();
                        repairedCount++;
                        ParsekLog.Info(Tag,
                            $"PatchDestructionState: repaired '{db.id}' (ledger: repaired at or " +
                            $"before UT {liveUt.ToString("F1", IC)})");
                    }
                }
                else
                {
                    noMatchCount++;
                }
            }

            string summary =
                $"PatchDestructionState: demolished={demolishedCount.ToString(IC)}, " +
                $"repaired={repairedCount.ToString(IC)}, " +
                $"settling={settlingCount.ToString(IC)}, " +
                $"unloaded={unloadedCount.ToString(IC)}, " +
                $"matched={matchedCount.ToString(IC)}, " +
                $"noMatch={noMatchCount.ToString(IC)}, " +
                $"buildings={buildingById.Count}, " +
                $"ledgerBuildings={ledgerDestroyedAtUt.Count}, " +
                $"liveUT={liveUt.ToString("F1", IC)}";
            if (demolishedCount + repairedCount > 0)
                ParsekLog.Info(Tag, summary);
            else
                ParsekLog.Verbose(Tag, summary);
        }

        internal enum DestructionPatchAction { None, Demolish, Repair, Settling }

        /// <summary>
        /// Pure: a live building's intact / destroyed flags are the save's only once it is
        /// registered with the current <c>ScenarioDestructibles</c> (its
        /// <c>ProtoDestructible</c> holds this instance): registration is where stock loads
        /// the persisted <c>intact</c> value into the building. Before that the flags are the
        /// prefab default (intact) and must not be read as the building's state.
        /// </summary>
        internal static bool IsBuildingStateLoaded(bool registeredWithScenario)
        {
            return registeredWithScenario;
        }

        private static bool IsRegisteredWithScenario(DestructibleBuilding db)
        {
            try
            {
                if (ScenarioDestructibles.Instance == null || ScenarioDestructibles.protoDestructibles == null)
                    return false;
                ScenarioDestructibles.ProtoDestructible proto;
                return ScenarioDestructibles.protoDestructibles.TryGetValue(db.id, out proto)
                    && proto != null && proto.dBuildingRefs != null
                    && proto.dBuildingRefs.Contains(db);
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Pure decision for one building given a KNOWN target state (see
        /// <see cref="ResolveLiveDestructionPatch"/> for the null-safe live form). A building
        /// that is neither intact nor destroyed is between states (stock's collapse or repair
        /// animation is running) and is left alone; otherwise demolish an intact building the
        /// target says is destroyed, and repair a destroyed one the target says is intact.
        /// </summary>
        internal static DestructionPatchAction ResolveDestructionPatch(
            bool targetDestroyed, bool isIntact, bool isDestroyed)
        {
            if (!isIntact && !isDestroyed)
                return DestructionPatchAction.Settling;
            if (targetDestroyed && isIntact)
                return DestructionPatchAction.Demolish;
            if (!targetDestroyed && isDestroyed)
                return DestructionPatchAction.Repair;
            return DestructionPatchAction.None;
        }

        internal static Dictionary<string, FacilitiesModule.FacilityState> BuildFacilityPatchTargets(
            IReadOnlyDictionary<string, FacilitiesModule.FacilityState> currentFacilities)
        {
            return BuildFacilityPatchTargets(currentFacilities, lastPatchedFacilityIds);
        }

        internal static Dictionary<string, FacilitiesModule.FacilityState> BuildFacilityPatchTargets(
            IReadOnlyDictionary<string, FacilitiesModule.FacilityState> currentFacilities,
            IEnumerable<string> previouslyPatchedFacilityIds)
        {
            return BuildFacilityPatchTargets(
                currentFacilities,
                previouslyPatchedFacilityIds,
                knownFacilityIdsToDefault: null);
        }

        internal static Dictionary<string, FacilitiesModule.FacilityState> BuildFacilityPatchTargets(
            IReadOnlyDictionary<string, FacilitiesModule.FacilityState> currentFacilities,
            IEnumerable<string> previouslyPatchedFacilityIds,
            IEnumerable<string> knownFacilityIdsToDefault)
        {
            var targets = new Dictionary<string, FacilitiesModule.FacilityState>(
                System.StringComparer.Ordinal);
            AddDefaultFacilityTargets(targets, knownFacilityIdsToDefault);
            AddDefaultFacilityTargets(targets, previouslyPatchedFacilityIds);
            if (currentFacilities != null)
            {
                foreach (var kvp in currentFacilities)
                {
                    if (!string.IsNullOrEmpty(kvp.Key))
                        targets[kvp.Key] = kvp.Value;
                }
            }
            return targets;
        }

        private static void AddDefaultFacilityTargets(
            Dictionary<string, FacilitiesModule.FacilityState> targets,
            IEnumerable<string> facilityIds)
        {
            if (targets == null || facilityIds == null)
                return;

            foreach (string facilityId in facilityIds)
            {
                if (string.IsNullOrEmpty(facilityId))
                    continue;
                targets[facilityId] = new FacilitiesModule.FacilityState
                {
                    Level = 1,
                    Destroyed = false
                };
            }
        }

        internal static void ForceDefaultFacilitiesForNextPatch(IEnumerable<string> facilityIds)
        {
            string currentSaveFolder = GetCurrentSaveFolderForPatchHistory();
            if (defaultFacilityIdsOnNextPatch.Count > 0 &&
                !System.String.Equals(
                    defaultFacilityIdsSaveFolder ?? "",
                    currentSaveFolder,
                    System.StringComparison.Ordinal))
            {
                int staleCount = defaultFacilityIdsOnNextPatch.Count;
                defaultFacilityIdsOnNextPatch.Clear();
                defaultFacilityIdsSaveFolder = null;
                ParsekLog.Verbose(Tag,
                    $"PatchFacilities: cleared {staleCount.ToString(IC)} stale pending default target(s) before scheduling for current save");
            }

            int added = 0;
            if (facilityIds != null)
            {
                foreach (string facilityId in facilityIds)
                {
                    if (string.IsNullOrEmpty(facilityId))
                        continue;
                    if (defaultFacilityIdsOnNextPatch.Add(facilityId))
                        added++;
                }
            }

            if (added == 0)
                return;

            defaultFacilityIdsSaveFolder = currentSaveFolder;
            ParsekLog.Verbose(Tag,
                $"PatchFacilities: scheduled default targets for {added.ToString(IC)} tombstoned facility id(s)");
        }

        /// <summary>
        /// Clears static facility patch history when the active save folder changes.
        /// Test fixtures that exercise facility patching should call
        /// <see cref="KspStatePatcher.ResetForTesting"/> or
        /// <see cref="ResetForTesting"/> so first-call initialization cannot inherit
        /// history from an earlier test.
        /// </summary>
        internal static void ResetPatchHistoryForSaveChange(string saveFolder)
        {
            string normalized = saveFolder ?? "";
            if (patchHistorySaveFolder == null)
            {
                patchHistorySaveFolder = normalized;
                return;
            }

            if (System.String.Equals(
                    patchHistorySaveFolder,
                    normalized,
                    System.StringComparison.Ordinal))
                return;

            int previousCount = lastPatchedFacilityIds.Count;
            int pendingDefaultCount = defaultFacilityIdsOnNextPatch.Count;
            bool pendingDefaultsMatchNewSave = pendingDefaultCount > 0
                && System.String.Equals(
                    defaultFacilityIdsSaveFolder ?? "",
                    normalized,
                    System.StringComparison.Ordinal);
            lastPatchedFacilityIds.Clear();
            if (!pendingDefaultsMatchNewSave)
            {
                defaultFacilityIdsOnNextPatch.Clear();
                defaultFacilityIdsSaveFolder = null;
            }
            patchHistorySaveFolder = normalized;

            ParsekLog.Verbose(Tag,
                $"PatchFacilities: cleared facility patch history after save change " +
                $"(previous={previousCount.ToString(IC)}, " +
                $"pendingDefaults={pendingDefaultCount.ToString(IC)}, " +
                $"preservedPendingDefaults={pendingDefaultsMatchNewSave.ToString(IC)})");
        }

        private static string GetCurrentSaveFolderForPatchHistory()
        {
            try
            {
                return HighLogic.SaveFolder ?? "";
            }
            catch
            {
                return "";
            }
        }

        internal static void ResetForTesting()
        {
            lastPatchedFacilityIds.Clear();
            defaultFacilityIdsOnNextPatch.Clear();
            defaultFacilityIdsSaveFolder = null;
            patchHistorySaveFolder = null;
        }
    }
}
