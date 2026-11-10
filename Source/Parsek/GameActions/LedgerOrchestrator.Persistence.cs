using System;
using System.Collections.Generic;
using System.Globalization;
using KscActionExpectation = Parsek.KscActionExpectationClassifier.KscActionExpectation;

namespace Parsek
{
    internal static partial class LedgerOrchestrator
    {
        /// <summary>
        /// Called on KSP load. Reconcile ledger against current save state,
        /// then recalculate and patch.
        /// </summary>
        /// <param name="validRecordingIds">Set of recording IDs present in the loaded save.</param>
        /// <param name="maxUT">Current UT — future spendings and contract lifecycle rows after this are pruned.</param>
        /// <param name="useCurrentUtCutoffForFutureActions">
        /// When true, keep the live KSP state at <paramref name="maxUT"/> if the
        /// surviving ledger still contains later actions. This is needed for loading
        /// older saves/quicksaves behind already-committed recording rewards.
        /// </param>
        /// <param name="loadDroppedRecordingIds">
        /// Committed recordings the loaded save names but that did not load (O3): their rows are
        /// kept, untagged, before the reconcile (<see cref="KeepRowsOfRecordingsThatFailedToLoad"/>).
        /// </param>
        internal static void OnKspLoad(
            HashSet<string> validRecordingIds,
            double maxUT,
            bool useCurrentUtCutoffForFutureActions = false,
            ICollection<string> loadDroppedRecordingIds = null)
        {
            Initialize();
            InvalidateCommittedFutureSnapshot("ksp-load");

            LedgerRecoveryFundsPairing.ClearConsumedRecoveryEventKeys();
            RecoveryPayoutContextStore.Clear("KSP load");
            // Log-before-clear so any stale entries from the previous session show up
            // in KSP.log before we drop them. Matches the FlushStalePendingRecoveryFunds
            // contract used at scene-switch / rewind-end boundaries.
            FlushStalePendingRecoveryFunds("KSP load");

            // Revert double-rollout repair (#710): saves written before the
            // OnVesselRolloutSpending dedup gate landed contain pairs of
            // VesselRollout actions for the same logical rollout (same PID/site/
            // vessel/cost, UT within ~1 s) when the player reverted mid-flight
            // and re-launched. Collapse each cluster down to its earliest member
            // so the ledger total matches what the user actually paid. Idempotent
            // on healthy saves; logs a single INFO when at least one row was
            // collapsed. See production repro at
            // logs/2026-05-01_2208_investigate/parsek/GameState/ledger.pgld.
            //
            // Run before Reconcile: adopted+unadopted duplicate repair may move
            // the adopted survivor to the relaunch's rolled-back UT. If Reconcile's
            // maxUT pruning ran first, an adopted row still carrying the original
            // future UT could be pruned before the repair pass had a chance to
            // normalize it.
            Ledger.RepairDuplicateRolloutActions();

            // BUG-F: during a cold OnLoad the universe clock is not yet initialized, so
            // maxUT (= Planetarium.GetUniversalTime() at the load call site) reads as 0.
            // Reconcile's maxUT pruning removes every row with UT > maxUT unless
            // preserveFutureTimelineActions is set, so at maxUT=0 a non-cutoff scene
            // (EDITOR cold-load, where useCurrentUtCutoffForFutureActions is
            // false) would PERMANENTLY delete the entire committed career. Preserve all rows
            // whenever the clock is not ready; the recalc below then full-replays them and the
            // deferred-seed pass re-evaluates once the clock is valid. When the clock IS ready
            // this falls back to the original scene-driven behavior.
            bool currentUtReady = IsCurrentUtReadyForCutoff(maxUT);
            bool preserveFutureRows = useCurrentUtCutoffForFutureActions || !currentUtReady;
            // Rows up to the save's held-through UT are in the loaded world (a save written in
            // a Rewind-to-Launch lead window): never pruned as future. Only a ready clock is
            // raised; a clock that is not ready keeps every row anyway.
            if (currentUtReady)
                maxUT = ResolveOnKspLoadReconcileUT(maxUT);

            // O3: a recording the save names but that did not load keeps its rows. Before the
            // reconcile, which prunes rows tagged to a recording missing from the loaded set.
            KeepRowsOfRecordingsThatFailedToLoad(loadDroppedRecordingIds, validRecordingIds);

            // Reconcile after timestamp-normalizing rollout repair. Current-UT load
            // mode removes orphaned rows but preserves future committed rows so the
            // cutoff walk can filter them until the game clock catches up.
            Ledger.Reconcile(
                validRecordingIds,
                maxUT,
                preserveFutureTimelineActions: preserveFutureRows);

            // Compatibility repair for early #444 saves written before recovery
            // FundsEarning.DedupKey was serialized. Rebuild the missing key from the
            // loaded FundsChanged(VesselRecovery) event so replayed recoveries cannot
            // double-credit after the per-session consumed set is cleared on load.
            RepairMissingRecoveryDedupKeys();

            // Re-derive every committed recording's KerbalAssignment rows from its crew
            // data, keeping ActionIds where the row identity matches.
            MigrateKerbalAssignments();

            // NO load-time backfill for the paired kerbal-death reputation penalty.
            // KerbalAssignment rows are safe to derive at load because they carry no
            // pool arithmetic; the penalty does, and the inside-the-seed answer is only
            // available at PRODUCTION time - it is what the commit knew about where the
            // seed came from, and a load can no longer reconstruct it (the seed is taken
            // lazily at the first commit, i.e. usually AFTER the crash already lowered
            // the live pool). Adding a row here would subtract the same penalty a second
            // time, and an authoritative merge recalc would write live-10 to the career.
            // Flights filed before this producer shipped therefore keep exactly the rows
            // they had; only newly committed recordings get one.

            // BUG-F: during a cold OnLoad the universe clock is not yet initialized, so
            // maxUT (= Planetarium.GetUniversalTime() at the load call site) reads as 0.
            // A current-UT cutoff at UT=0 treats the entire established career as "future"
            // and filters it out, wiping funds to the starting seed and science to 0. Only
            // take the cutoff path when the UT is a real, initialized clock value; otherwise
            // fall back to the safe full replay (cutoffUT=null) that replays every committed
            // action. The deferred-seed coroutine re-applies the cutoff once the clock is
            // ready, so a genuine future-timeline (post-rewind) cold load is still honored.
            // currentUtReady was computed above (it also gates row preservation in Reconcile).

            bool useCurrentUtCutoff = DecideOnKspLoadCurrentUtCutoff(
                maxUT, useCurrentUtCutoffForFutureActions, currentUtReady);
            if (useCurrentUtCutoff)
            {
                RecalculateAndPatchForCurrentTimelineUT(maxUT, "ksp-load");
            }
            else
            {
                RecalculateAndPatch();
            }

            KerbalLoadRepairDiagnostics.EmitAndReset();
        }

        /// <summary>
        /// <see cref="OnKspLoad"/>'s reconcile clock on a ready clock: the ledger held-through
        /// UT when it is later than <paramref name="maxUT"/> (logged), else
        /// <paramref name="maxUT"/>.
        /// </summary>
        internal static double ResolveOnKspLoadReconcileUT(double maxUT)
        {
            double heldMaxUT = LedgerHeldWindow.ResolveLedgerNowUT(maxUT);
            if (heldMaxUT != maxUT)
            {
                ParsekLog.Info(Tag,
                    "OnKspLoad: reconcile clock " + maxUT.ToString("R", CultureInfo.InvariantCulture) +
                    " raised to the ledger held-through UT " + heldMaxUT.ToString("R", CultureInfo.InvariantCulture));
                maxUT = heldMaxUT;
            }
            return maxUT;
        }

        /// <summary>
        /// <see cref="OnKspLoad"/>'s recalc decision: true when the load recalculates at the
        /// current UT (the feature is on, the clock is ready and rows lie past it), else a
        /// full replay. Logs the decision and its inputs.
        /// </summary>
        internal static bool DecideOnKspLoadCurrentUtCutoff(
            double maxUT, bool useCurrentUtCutoffForFutureActions, bool currentUtReady)
        {
            bool hasFutureActions = HasActionsAfterUT(maxUT);
            bool useCurrentUtCutoff =
                useCurrentUtCutoffForFutureActions && currentUtReady && hasFutureActions;
            ParsekLog.Info(Tag,
                "OnKspLoad recalc decision: useCurrentUtCutoff=" + useCurrentUtCutoff +
                " maxUT=" + maxUT.ToString("R", CultureInfo.InvariantCulture) +
                " featureSupportsCutoff=" + useCurrentUtCutoffForFutureActions +
                " currentUtReady=" + currentUtReady +
                " hasFutureActions=" + hasFutureActions);
            return useCurrentUtCutoff;
        }

        /// <summary>
        /// Owner ruling O3 (2026-10-21, game-actions design 3.13 / 4.3): a committed recording
        /// the loaded save names but that failed to load, for any reason, loses its ghost and
        /// playback, never its ledger rows. The load dropped it and the next save will not name
        /// it, so its rows are kept as career rows with the tag cleared (the shape a non-rewind
        /// discard leaves, <see cref="Ledger.ClearRecordingTagForRecordings"/>); keeping the tag
        /// would let the next load's reconcile prune them. Ids the loaded set still knows (an
        /// inert tree, GC-3) keep their tagged rows. Returns the number of rows kept this way.
        /// </summary>
        internal static int KeepRowsOfRecordingsThatFailedToLoad(
            ICollection<string> loadDroppedRecordingIds, HashSet<string> validRecordingIds)
        {
            if (loadDroppedRecordingIds == null || loadDroppedRecordingIds.Count == 0)
                return 0;
            var untag = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in loadDroppedRecordingIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (validRecordingIds != null && validRecordingIds.Contains(id)) continue;
                untag.Add(id);
            }
            if (untag.Count == 0)
                return 0;

            int kept = Ledger.ClearRecordingTagForRecordings(untag);
            var examples = new List<string>();
            foreach (string id in untag)
            {
                if (examples.Count >= 3) break;
                examples.Add(id);
            }
            ParsekLog.Info(Tag,
                "OnKspLoad: kept " + kept.ToString(CultureInfo.InvariantCulture) +
                " ledger row(s) of " + untag.Count.ToString(CultureInfo.InvariantCulture) +
                " committed recording(s) that failed to load (O3, tag cleared, rows kept) ids=[" +
                string.Join(", ", examples.ToArray()) + (untag.Count > examples.Count ? ", ..." : "") + "]");
            return kept;
        }

        /// <summary>
        /// Re-derives the KerbalAssignment rows of every committed recording from its
        /// vessel snapshot crew data, so each recording has the rows its crew implies.
        /// </summary>
        private static void MigrateKerbalAssignments()
        {
            var recordings = RecordingStore.CommittedRecordings;
            if (recordings == null || recordings.Count == 0) return;

            var existingByRecording = new Dictionary<string, List<GameAction>>();
            var ledgerActions = Ledger.Actions;
            for (int i = 0; i < ledgerActions.Count; i++)
            {
                var action = ledgerActions[i];
                if (action.Type != GameActionType.KerbalAssignment
                    || string.IsNullOrEmpty(action.RecordingId))
                    continue;

                List<GameAction> existing;
                if (!existingByRecording.TryGetValue(action.RecordingId, out existing))
                {
                    existing = new List<GameAction>();
                    existingByRecording[action.RecordingId] = existing;
                }

                existing.Add(action);
            }

            int repairedRecordings = 0;
            int oldRows = 0;
            int newRows = 0;
            int inheritedRows = 0;
            int freshIdRows = 0;
            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];

                var kerbalActions = CreateKerbalAssignmentActions(
                    rec.RecordingId, rec.StartUT, rec.EndUT);
                List<GameAction> existing;
                existingByRecording.TryGetValue(rec.RecordingId, out existing);
                int touristRolesKept = CarryTouristRoles(existing, kerbalActions);
                if (touristRolesKept > 0)
                    ParsekLog.Verbose(Tag,
                        $"MigrateKerbalAssignments: recording '{rec.RecordingId}' kept the tourist role on " +
                        $"{touristRolesKept.ToString(CultureInfo.InvariantCulture)} row(s) the roster can no longer confirm");
                if (KerbalAssignmentActionsMatch(existing, kerbalActions))
                    continue;

                var repairStats = ClassifyKerbalAssignmentRepair(existing, kerbalActions);

                int freshIds;
                int inheritedIds = InheritKerbalAssignmentActionIds(
                    existing, kerbalActions, out freshIds, CrewSlotOwnerName);
                inheritedRows += inheritedIds;
                freshIdRows += freshIds;
                ParsekLog.Verbose(Tag,
                    $"MigrateKerbalAssignments: re-derived recording '{rec.RecordingId}' " +
                    $"rows={kerbalActions.Count.ToString(CultureInfo.InvariantCulture)} " +
                    $"inheritedActionIds={inheritedIds.ToString(CultureInfo.InvariantCulture)} " +
                    $"freshActionIds={freshIds.ToString(CultureInfo.InvariantCulture)}");

                Ledger.ReplaceActionsForRecording(
                    GameActionType.KerbalAssignment, rec.RecordingId, kerbalActions);
                repairedRecordings++;
                oldRows += existing != null ? existing.Count : 0;
                newRows += kerbalActions.Count;
                KerbalLoadRepairDiagnostics.RecordMigrationRepair(
                    1,
                    existing != null ? existing.Count : 0,
                    kerbalActions.Count);
                KerbalLoadRepairDiagnostics.RecordTouristRowsSkipped(
                    rec.RecordingId, repairStats.TouristRowsSkipped);

                for (int s = 0; s < repairStats.RemappedRows.Count; s++)
                {
                    var remap = repairStats.RemappedRows[s];
                    KerbalLoadRepairDiagnostics.RecordRemappedRow(
                        rec.RecordingId, remap.FromName, remap.ToName);
                }

                for (int s = 0; s < repairStats.EndStateRewrites.Count; s++)
                {
                    var rewrite = repairStats.EndStateRewrites[s];
                    KerbalLoadRepairDiagnostics.RecordEndStateRewrite(
                        rec.RecordingId, rewrite.KerbalName, rewrite.FromState, rewrite.ToState);
                }
            }

            if (repairedRecordings > 0)
                ParsekLog.Info(Tag,
                    $"MigrateKerbalAssignments: repaired {repairedRecordings} recording(s) " +
                    $"(oldRows={oldRows}, newRows={newRows}, " +
                    $"inheritedActionIds={inheritedRows}, freshActionIds={freshIdRows})");
        }

        /// <summary>
        /// TOMBSTONED-DEATH-RESURRECTS-ON-RELOAD-AFTER-A-RP-SPLIT, cause (a), ruling a1
        /// (2026-09-23): a re-derived KerbalAssignment row that replaces a stored row for
        /// the SAME (RecordingId, KerbalName) inherits that row's
        /// <see cref="GameAction.ActionId"/>. ActionIds are immutable and
        /// <see cref="LedgerTombstone"/>s key on them (rewind design 5.6), so a fresh id
        /// on re-derivation would silently un-retire a tombstoned row and leave the
        /// tombstone orphaned. The row's CONTENT (timing, end state) still follows the
        /// derivation; only its identity is preserved.
        ///
        /// <para>
        /// Pairing is deterministic: both lists are walked in order and the k-th desired
        /// row for a kerbal inherits the k-th stored row for that kerbal (ordinal name
        /// match), each stored row consumed at most once, so two desired rows can never
        /// share an id. When several unconsumed stored rows share the kerbal's name, the
        /// first one with the SAME end state wins, else the first in order: after an
        /// optimizer merge the target holds its own handoff row AND the absorbed
        /// segment's death row for one kerbal, and the re-derived death must keep the
        /// death's id (OPTIMIZER-SPLIT-LEAVES-KERBAL-ROWS-ON-THE-FIRST-SEGMENT review).
        /// A stored row with no same-name partner (a remap, a removed crew
        /// member) is dropped as before; a desired row with no partner keeps its fresh
        /// id. Callers pass rows already grouped by one recording.
        /// </para>
        /// <para>
        /// A crew row names the seated stand-in when it ends Dead and the slot owner
        /// otherwise (<see cref="ResolveCrewRowName"/>), so one seat's row can change
        /// name between derivations (a save written before that rule, or an end state
        /// that moved). When <paramref name="slotOwnerOf"/> is given, a desired row with
        /// no same-name partner pairs, in a second pass, with an unconsumed stored row of
        /// the same recording whose name maps to the same slot owner, so a tombstoned
        /// death keeps its id across the rename.
        /// </para>
        /// Returns the number of inherited ids; <paramref name="freshCount"/> counts the
        /// desired rows that kept a fresh id.
        /// </summary>
        internal static int InheritKerbalAssignmentActionIds(
            List<GameAction> existing, List<GameAction> desired, out int freshCount,
            Func<string, string> slotOwnerOf = null)
        {
            freshCount = 0;
            if (desired == null || desired.Count == 0)
                return 0;

            int existingCount = existing != null ? existing.Count : 0;
            var consumed = new bool[existingCount];
            var paired = new bool[desired.Count];
            int inherited = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                if (pass == 1 && slotOwnerOf == null)
                    break;
                for (int d = 0; d < desired.Count; d++)
                {
                    var want = desired[d];
                    if (want == null || paired[d])
                        continue;
                    string wantOwner = pass == 1 ? SlotOwnerOrSelf(slotOwnerOf, want.KerbalName) : null;

                    int match = -1;
                    int sameFateMatch = -1;
                    for (int e = 0; e < existingCount; e++)
                    {
                        if (consumed[e])
                            continue;
                        var have = existing[e];
                        if (have == null
                            || have.Type != GameActionType.KerbalAssignment
                            || string.IsNullOrEmpty(have.ActionId)
                            || !string.Equals(have.RecordingId, want.RecordingId, StringComparison.Ordinal))
                            continue;
                        bool sameKerbal = pass == 0
                            ? string.Equals(have.KerbalName, want.KerbalName, StringComparison.Ordinal)
                            : !string.IsNullOrEmpty(wantOwner)
                              && string.Equals(SlotOwnerOrSelf(slotOwnerOf, have.KerbalName),
                                  wantOwner, StringComparison.Ordinal);
                        if (!sameKerbal)
                            continue;
                        if (match < 0)
                            match = e;
                        if (have.KerbalEndStateField == want.KerbalEndStateField)
                        {
                            sameFateMatch = e;
                            break;
                        }
                    }
                    if (sameFateMatch >= 0)
                        match = sameFateMatch;
                    if (match < 0)
                        continue;

                    if (pass == 1)
                        ParsekLog.Verbose(Tag,
                            $"InheritKerbalAssignmentActionIds: row '{want.KerbalName}' in recording " +
                            $"'{want.RecordingId}' inherits the id of stored row '{existing[match].KerbalName}' " +
                            $"(same slot owner '{wantOwner}')");
                    consumed[match] = true;
                    paired[d] = true;
                    want.ActionId = existing[match].ActionId;
                    inherited++;
                }
            }

            for (int d = 0; d < desired.Count; d++)
                if (desired[d] != null && !paired[d])
                    freshCount++;
            return inherited;
        }

        private static string SlotOwnerOrSelf(Func<string, string> slotOwnerOf, string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;
            string owner = slotOwnerOf(name);
            return string.IsNullOrEmpty(owner) ? name : owner;
        }

        /// <summary>
        /// The slot owner a crew name stands in for, or the name itself when it is
        /// nobody's stand-in (the same reverse map crew rows use, #337).
        /// </summary>
        internal static string CrewSlotOwnerName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;
            var one = new List<string> { name };
            KerbalsModule.ReverseMapCrewNames(one, CrewReservationManager.CrewReplacements, null);
            return one[0];
        }

        internal static bool IsResourceImpactingAction(GameActionType t)
        {
            return LedgerLoadMigration.IsResourceImpactingAction(t);
        }
        internal struct KerbalAssignmentRepairRename
        {
            public string FromName;
            public string ToName;
        }

        internal struct KerbalAssignmentEndStateRewrite
        {
            public string KerbalName;
            public KerbalEndState FromState;
            public KerbalEndState ToState;
        }

        /// <summary>
        /// A seat recorded as a tourist's stays a tourist's: for every
        /// <paramref name="desired"/> row whose kerbal an <paramref name="existing"/> row of
        /// the same recording names with the tourist role, the desired row takes that role.
        /// Stock deletes a tourist when his contract ends or his vessel is recovered, and a
        /// rewind prunes the baselines that captured him, so a re-derived row would otherwise
        /// fall back to "Pilot" and the hold would grow a stand-in. Stock never turns a
        /// tourist into crew. Returns the rows changed. Pure over its inputs.
        /// </summary>
        internal static int CarryTouristRoles(List<GameAction> existing, List<GameAction> desired)
        {
            if (existing == null || existing.Count == 0 || desired == null || desired.Count == 0)
                return 0;

            var touristNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < existing.Count; i++)
            {
                var a = existing[i];
                if (a != null && !string.IsNullOrEmpty(a.KerbalName)
                    && KerbalsModule.IsTouristRole(a.KerbalRole))
                    touristNames.Add(a.KerbalName);
            }
            if (touristNames.Count == 0) return 0;

            int changed = 0;
            for (int i = 0; i < desired.Count; i++)
            {
                var d = desired[i];
                if (d == null || string.IsNullOrEmpty(d.KerbalName)) continue;
                if (KerbalsModule.IsTouristRole(d.KerbalRole)) continue;
                if (!touristNames.Contains(d.KerbalName)) continue;
                d.KerbalRole = KerbalsModule.TouristRole;
                changed++;
            }
            return changed;
        }

        internal sealed class KerbalAssignmentRepairStats
        {
            public readonly List<KerbalAssignmentRepairRename> RemappedRows
                = new List<KerbalAssignmentRepairRename>();
            public readonly List<KerbalAssignmentEndStateRewrite> EndStateRewrites
                = new List<KerbalAssignmentEndStateRewrite>();
            public int TouristRowsSkipped;
        }

        private static KerbalAssignmentRepairStats ClassifyKerbalAssignmentRepair(
            List<GameAction> existing,
            List<GameAction> desired)
        {
            var stats = new KerbalAssignmentRepairStats();
            if (existing == null || existing.Count == 0)
                return stats;

            var filteredExisting = FilterKerbalRepairExistingRows(existing, stats);

            if (desired == null || desired.Count == 0 || filteredExisting.Count == 0)
                return stats;

            var matchedExisting = new bool[filteredExisting.Count];
            var matchedDesired = new bool[desired.Count];

            // Reorder-only repairs should not log false remap samples. Match exact-name rows
            // first, then classify the remaining unmatched rows as true remaps/rewrites.
            MatchKerbalRepairRowsBySameName(
                filteredExisting, desired, matchedExisting, matchedDesired, stats);
            MatchKerbalRepairRowsAsRemaps(
                filteredExisting, desired, matchedExisting, matchedDesired, stats);

            return stats;
        }

        /// <summary>
        /// The first step of <see cref="ClassifyKerbalAssignmentRepair"/>: the stored rows
        /// it compares against, without null rows and tourist rows (counted in
        /// <paramref name="stats"/>).
        /// </summary>
        internal static List<GameAction> FilterKerbalRepairExistingRows(
            List<GameAction> existing, KerbalAssignmentRepairStats stats)
        {
            var filteredExisting = new List<GameAction>();
            for (int i = 0; i < existing.Count; i++)
            {
                var existingAction = existing[i];
                if (existingAction == null)
                    continue;

                if (string.Equals(existingAction.KerbalRole, "Tourist",
                    StringComparison.OrdinalIgnoreCase))
                {
                    stats.TouristRowsSkipped++;
                    continue;
                }

                filteredExisting.Add(existingAction);
            }

            return filteredExisting;
        }

        /// <summary>
        /// The exact-name pass of <see cref="ClassifyKerbalAssignmentRepair"/>: pair each
        /// desired row with the first unconsumed stored row of the same name and timing, and
        /// record an end-state rewrite where the pair's end states differ.
        /// </summary>
        internal static void MatchKerbalRepairRowsBySameName(
            List<GameAction> filteredExisting,
            List<GameAction> desired,
            bool[] matchedExisting,
            bool[] matchedDesired,
            KerbalAssignmentRepairStats stats)
        {
            for (int i = 0; i < desired.Count; i++)
            {
                var desiredAction = desired[i];
                int existingIndex = FindRepairRowMatch(
                    filteredExisting, matchedExisting, desiredAction, requireSameName: true);
                if (existingIndex < 0)
                    continue;

                matchedExisting[existingIndex] = true;
                matchedDesired[i] = true;

                var existingAction = filteredExisting[existingIndex];
                if (existingAction.KerbalEndStateField != desiredAction.KerbalEndStateField)
                {
                    stats.EndStateRewrites.Add(new KerbalAssignmentEndStateRewrite
                    {
                        KerbalName = desiredAction.KerbalName,
                        FromState = existingAction.KerbalEndStateField,
                        ToState = desiredAction.KerbalEndStateField
                    });
                }
            }
        }

        /// <summary>
        /// The rename pass of <see cref="ClassifyKerbalAssignmentRepair"/>: pair each
        /// desired row the exact-name pass left with the first unconsumed stored row of the
        /// same timing, and record a remap and / or an end-state rewrite.
        /// </summary>
        internal static void MatchKerbalRepairRowsAsRemaps(
            List<GameAction> filteredExisting,
            List<GameAction> desired,
            bool[] matchedExisting,
            bool[] matchedDesired,
            KerbalAssignmentRepairStats stats)
        {
            for (int i = 0; i < desired.Count; i++)
            {
                if (matchedDesired[i])
                    continue;

                var desiredAction = desired[i];
                int existingIndex = FindRepairRowMatch(
                    filteredExisting, matchedExisting, desiredAction, requireSameName: false);
                if (existingIndex < 0)
                    continue;

                matchedExisting[existingIndex] = true;
                matchedDesired[i] = true;

                var existingAction = filteredExisting[existingIndex];
                if (!string.Equals(existingAction.KerbalName, desiredAction.KerbalName,
                    StringComparison.Ordinal))
                {
                    stats.RemappedRows.Add(new KerbalAssignmentRepairRename
                    {
                        FromName = existingAction.KerbalName,
                        ToName = desiredAction.KerbalName
                    });
                }

                if (existingAction.KerbalEndStateField != desiredAction.KerbalEndStateField)
                {
                    stats.EndStateRewrites.Add(new KerbalAssignmentEndStateRewrite
                    {
                        KerbalName = desiredAction.KerbalName,
                        FromState = existingAction.KerbalEndStateField,
                        ToState = desiredAction.KerbalEndStateField
                    });
                }
            }
        }

        private static int FindRepairRowMatch(
            List<GameAction> existing,
            bool[] matchedExisting,
            GameAction desiredAction,
            bool requireSameName)
        {
            if (existing == null || matchedExisting == null || desiredAction == null)
                return -1;

            for (int i = 0; i < existing.Count; i++)
            {
                if (matchedExisting[i])
                    continue;

                var existingAction = existing[i];
                if (existingAction == null)
                    continue;
                if (!string.Equals(existingAction.RecordingId, desiredAction.RecordingId,
                    StringComparison.Ordinal))
                    continue;
                if (!string.Equals(existingAction.KerbalRole, desiredAction.KerbalRole,
                    StringComparison.Ordinal))
                    continue;
                if (Math.Abs(existingAction.UT - desiredAction.UT) > 0.1)
                    continue;
                if (Math.Abs(existingAction.StartUT - desiredAction.StartUT) > 0.1f)
                    continue;
                if (Math.Abs(existingAction.EndUT - desiredAction.EndUT) > 0.1f)
                    continue;
                if (requireSameName
                    && !string.Equals(existingAction.KerbalName, desiredAction.KerbalName,
                        StringComparison.Ordinal))
                    continue;

                return i;
            }

            return -1;
        }

        private static bool KerbalAssignmentActionsMatch(
            List<GameAction> existing, List<GameAction> desired)
        {
            int existingCount = existing != null ? existing.Count : 0;
            int desiredCount = desired != null ? desired.Count : 0;
            if (existingCount != desiredCount)
                return false;

            for (int i = 0; i < existingCount; i++)
            {
                var a = existing[i];
                var b = desired[i];
                if (a == null || b == null)
                    return false;
                if (a.Type != b.Type)
                    return false;
                if (Math.Abs(a.UT - b.UT) > 0.1)
                    return false;
                if (!string.Equals(a.RecordingId, b.RecordingId, StringComparison.Ordinal))
                    return false;
                if (!string.Equals(a.KerbalName, b.KerbalName, StringComparison.Ordinal))
                    return false;
                if (!string.Equals(a.KerbalRole, b.KerbalRole, StringComparison.Ordinal))
                    return false;
                if (Math.Abs(a.StartUT - b.StartUT) > 0.1f)
                    return false;
                if (Math.Abs(a.EndUT - b.EndUT) > 0.1f)
                    return false;
                if (a.KerbalEndStateField != b.KerbalEndStateField)
                    return false;
                if (a.Sequence != b.Sequence)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Populates CrewEndStates on committed recordings that don't have them yet.
        /// Safety net for recordings whose end states weren't populated at commit time.
        /// </summary>
        private static void PopulateUnpopulatedCrewEndStates()
        {
            var recordings = RecordingStore.CommittedRecordings;
            if (recordings == null) return;

            int populated = 0;
            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (NeedsCrewEndStatePopulation(rec))
                {
                    KerbalsModule.PopulateCrewEndStates(rec);
                    if (rec.CrewEndStates != null) populated++;
                }
            }
            if (populated > 0)
                ParsekLog.Verbose(Tag,
                    $"PopulateUnpopulatedCrewEndStates: populated {populated} crew end states");
        }

        /// <summary>
        /// Called on save. Persist ledger to file. <paramref name="prebuiltNode"/> is the
        /// <c>LEDGER</c> node OnSave built once for both the file and the save's
        /// <c>GAME_STATE</c> node; null builds a fresh one.
        /// </summary>
        internal static void OnSave(ConfigNode prebuiltNode = null)
        {
            string path = Ledger.GetLedgerPath();
            if (!string.IsNullOrEmpty(path))
            {
                Ledger.WriteSaveNode(prebuiltNode ?? Ledger.BuildSaveNode(), path);
                ParsekLog.Verbose(Tag, $"OnSave: ledger saved to '{path}'");
            }
            else
            {
                ParsekLog.Warn(Tag, "OnSave: could not resolve ledger path — skipping save");
            }
        }

        /// <summary>
        /// Called on a cold load. Loads the ledger from the save's <c>GAME_STATE/LEDGER</c>
        /// node when <paramref name="saveLedgerNode"/> is given, else from the ledger file,
        /// then runs the legacy repairs against the already loaded events (same order on
        /// both sources) and seeds the KSC sequence counter from the loaded rows.
        /// </summary>
        internal static void OnLoad(ConfigNode saveLedgerNode = null)
        {
            Initialize();

            // Reset per-resource seeding flags so initial balances are re-captured
            // for this save. Without this, switching from a sandbox save to a career
            // save would skip seeding entirely.
            fundsSeedDone = false;
            scienceSeedDone = false;
            repSeedDone = false;

            string source;
            if (saveLedgerNode != null)
            {
                Ledger.LoadFromNode(saveLedgerNode);
                source = "the save's GAME_STATE node";
            }
            else
            {
                string path = Ledger.GetLedgerPath();
                if (string.IsNullOrEmpty(path))
                {
                    ParsekLog.Warn(Tag, "OnLoad: could not resolve ledger path — starting with empty ledger");
                    // The rows, the progress seed, the take-back era and the timeline breaks
                    // belong to the save they came from; none was read, so the previous
                    // save's must not carry over (a cold load is a save switch).
                    Ledger.ClearForFailedLoad();
                    StockShadowTakeBack.LoadFrom(null, "unresolved-ledger-path");
                    return;
                }
                Ledger.LoadFromFile(path);
                source = "'" + path + "'";
            }

            SeedKscSequenceCounterFromLoadedLedger(Ledger.Actions);
            CareerSettingsLock.EnsureAndEnforce(KspStatePatcher.ReadLiveAllowNegativeCurrency(), "ledger-load");
            ParsekLog.Verbose(Tag, $"OnLoad: ledger loaded from {source}");
        }

        /// <summary>
        /// Puts the KSC sequence counter past every loaded row's <c>Sequence</c>, so a KSC row
        /// made after a load sorts after a loaded row at the same UT (the recalculation sort is
        /// UT, earnings first, then sequence). Never moves the counter backwards.
        /// </summary>
        internal static void SeedKscSequenceCounterFromLoadedLedger(IReadOnlyList<GameAction> loaded)
        {
            int max = 0;
            if (loaded != null)
            {
                for (int i = 0; i < loaded.Count; i++)
                {
                    if (loaded[i] != null && loaded[i].Sequence > max)
                        max = loaded[i].Sequence;
                }
            }
            if (max > kscSequenceCounter)
            {
                ParsekLog.Verbose(Tag,
                    $"OnLoad: KSC sequence counter seeded from the loaded ledger: {kscSequenceCounter.ToString(CultureInfo.InvariantCulture)} -> {max.ToString(CultureInfo.InvariantCulture)}");
                kscSequenceCounter = max;
            }
        }

        internal static int KscSequenceCounterForTesting => kscSequenceCounter;
    }
}
