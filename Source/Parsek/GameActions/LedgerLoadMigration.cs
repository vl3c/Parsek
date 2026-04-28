using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    internal static class LedgerLoadMigration
    {
        /// <summary>
        /// Set to <c>true</c> by <see cref="MigrateOldSaveEvents"/> when — and only when —
        /// it actually synthesized at least one action from <see cref="GameStateStore.Events"/>.
        /// Reset to <c>false</c> at the top of every <see cref="OnKspLoad"/>.
        ///
        /// <para>Used by <see cref="HasAnyLedgerCoverage"/> to gate the null-RecordingId-in-window
        /// coverage rule. Round-2 external review (PR #347) found that
        /// <see cref="TryRecoverBrokenLedgerOnLoad"/> synthesizes null-tagged
        /// <see cref="GameActionType.ContractAccept"/>/<see cref="GameActionType.ContractComplete"/>/<see cref="GameActionType.ContractFail"/>/<see cref="GameActionType.ContractCancel"/>/<see cref="GameActionType.FundsSpending"/>
        /// actions from the global <see cref="GameStateStore"/> — and on subsequent loads,
        /// those previously-persisted null-tagged actions sit in the ledger at UTs that may
        /// fall inside a legacy tree's window. A multi-day mission can easily overlap with
        /// unrelated KSC activity (player accepts a contract, buys a part, cancels a
        /// contract). The coverage probe saw those actions and incorrectly concluded
        /// "covered" → skipped migration → legacy residual silently dropped.</para>
        ///
        /// <para>The flag distinguishes "null-tagged because the first-load
        /// MigrateOldSaveEvents just synthesized these" from "null-tagged because normal
        /// KSC activity". Only the first case should register as coverage (to prevent
        /// the first-load double-credit that round-3 of Phase A was designed to avoid).</para>
        /// </summary>
        private static bool migrateOldSaveEventsRanThisLoad;

        internal static void ResetMigrateOldSaveEventsForLoad()
        {
            migrateOldSaveEventsRanThisLoad = false;
        }

        internal static void ResetMigrateOldSaveEventsForTesting()
        {
            migrateOldSaveEventsRanThisLoad = false;
        }

        /// <summary>
        /// Test-only setter for <see cref="migrateOldSaveEventsRanThisLoad"/>. Production
        /// code sets this flag via <see cref="MigrateOldSaveEvents"/> and resets it at the
        /// top of <see cref="OnKspLoad"/>; tests bypass that flow by calling this helper.
        /// </summary>
        internal static void SetMigrateOldSaveEventsRanThisLoadForTesting(bool value)
        {
            migrateOldSaveEventsRanThisLoad = value;
        }

        /// <summary>Test-only getter paired with the setter above.</summary>
        internal static bool GetMigrateOldSaveEventsRanThisLoadForTesting()
        {
            return migrateOldSaveEventsRanThisLoad;
        }

        /// <summary>
        /// Migration for old saves: converts existing GameStateStore events from committed
        /// recordings into GameActions and adds them to the ledger. Called once when an old
        /// save loads with committed recordings but no ledger file.
        /// </summary>
        internal static void MigrateOldSaveEvents(HashSet<string> validRecordingIds)
        {
            var events = GameStateStore.Events;
            if (events == null || events.Count == 0)
            {
                ParsekLog.Info(LedgerOrchestrator.Tag, "MigrateOldSaveEvents: no events to migrate");
                return;
            }

            // Convert events using the same converter path as normal commits.
            // Use null recordingId since we can't reliably map old events to specific recordings.
            double minUT = 0;
            double maxUT = double.MaxValue;
            var actions = GameStateEventConverter.ConvertEvents(events, null, minUT, maxUT);

            if (actions.Count > 0)
            {
                Ledger.AddActions(actions);
                // Signal to HasAnyLedgerCoverage that null-tagged in-window actions
                // synthesized on this load should count as coverage (to prevent the
                // first-load double-credit that Phase A round 3 was designed to avoid).
                // Only set on actual synthesis — an empty MigrateOldSaveEvents must NOT
                // make pre-existing null-tagged actions (from prior loads' recovery)
                // falsely count as coverage.
                migrateOldSaveEventsRanThisLoad = true;
                ParsekLog.Info(LedgerOrchestrator.Tag,
                    $"MigrateOldSaveEvents: migrated {actions.Count} actions from {events.Count} events " +
                    $"(committed recordings: {validRecordingIds.Count})");
            }
            else
            {
                ParsekLog.Info(LedgerOrchestrator.Tag,
                    $"MigrateOldSaveEvents: {events.Count} events produced 0 convertible actions");
            }
        }

        // ================================================================
        // Phase A: legacy tree-resource residual migration (zero-coverage scope)
        // ================================================================

        /// <summary>
        /// Tolerance (absolute value) below which a persisted legacy-tree delta is
        /// treated as zero and NOT synthesized. Matches field precision of the
        /// persisted `deltaFunds` / `deltaScience` / `deltaReputation` values with
        /// some slack for floating-point drift across save/load.
        /// </summary>
        private const double LegacyMigrationFundsTolerance = 1.0;
        private const double LegacyMigrationScienceTolerance = 0.1;
        private const double LegacyMigrationReputationTolerance = 0.1;
        private const string LegacyFormatGateTag = "LegacyFormatGate";

        /// <summary>
        /// Phase A of the ledger / lump-sum resource reconciliation fix
        /// (<c>docs/dev/done/plans/fix-ledger-lump-sum-reconciliation.md</c>). Walks the
        /// committed trees and, for each tree whose <see cref="RecordingTree.Load"/>
        /// hydrated a transient legacy residual with any persisted
        /// <c>deltaFunds</c>/<c>deltaScience</c>/<c>deltaReputation</c> outside
        /// tolerance, decides — per the reviewer's zero-coverage scope — either:
        /// <list type="bullet">
        ///   <item><description>
        ///   <b>Zero ledger coverage</b> (no ledger action tagged to any of the tree's
        ///   recordings falls inside the tree's UT window): inject the FULL persisted
        ///   delta as <see cref="FundsEarningSource.LegacyMigration"/> earnings tagged
        ///   with the tree's <see cref="RecordingTree.RootRecordingId"/>. This is the
        ///   stress-test repro path (prior-epoch events were skipped by
        ///   <see cref="TryRecoverBrokenLedgerOnLoad"/>'s epoch gate, so the ledger
        ///   has nothing for those trees).
        ///   </description></item>
        ///   <item><description>
        ///   <b>Any ledger coverage</b> (partial — at least one tagged action in window):
        ///   do NOT inject. Comparing the pre-walk raw field sums to post-walk,
        ///   post-transform, post-curve-and-cap persisted tree deltas is structurally
        ///   wrong (ScienceModule's subject cap, ReputationModule's non-linear curve,
        ///   StrategiesModule's reward transform, and Effective=false duplicate
        ///   suppression all run between the two sides). Log WARN and rely on
        ///   the existing ledger coverage as best-effort; Phase F's format-version
        ///   gate catches anything that slips through to deletion.
        ///   </description></item>
        /// </list>
        ///
        /// <para>Degraded tree (<see cref="RecordingTree.ComputeEndUT"/> returns 0 —
        /// trajectory data missing): mark recordings fully applied, log INFO, no
        /// injection.</para>
        ///
        /// <para>Empty <c>RootRecordingId</c>: mark recordings fully applied, log
        /// WARN. No synthetic — no correct recording id to tag with. Data loss for
        /// the rare empty-root case is preferable to silent residual loss without a
        /// warning.</para>
        ///
        /// <para>Regardless of branch, the tree's recordings are marked fully applied
        /// via <see cref="RecordingStore.MarkTreeAsApplied"/> so budget reservation does
        /// not re-hold already-live resources. Idempotent: after the transient residual
        /// is consumed, repeat runs on the same in-memory tree are no-ops. Reloading the
        /// same legacy save is guarded by existing LegacyMigration synthetic detection.</para>
        /// </summary>
        internal static void MigrateLegacyTreeResources()
        {
            var trees = RecordingStore.CommittedTrees;
            if (trees == null || trees.Count == 0)
            {
                ParsekLog.Verbose(LedgerOrchestrator.Tag,
                    "MigrateLegacyTreeResources: no committed trees, nothing to migrate");
                return;
            }

            int treesConsidered = 0;
            int treesWithoutLegacyResidual = 0;
            int treesAlreadyApplied = 0;
            int treesAlreadyMigrated = 0;
            int treesSkippedNoDelta = 0;
            int treesSkippedEmptyRoot = 0;
            int treesSkippedDegraded = 0;
            int treesSkippedPartialCoverage = 0;
            int treesMigrated = 0;
            int treesWarnedByGate = 0;
            int fundsInjected = 0;
            int scienceInjected = 0;
            int repInjected = 0;

            for (int i = 0; i < trees.Count; i++)
            {
                var tree = trees[i];
                if (tree == null) continue;

                var residual = tree.ConsumeLegacyResidual();
                if (residual == null)
                {
                    treesWithoutLegacyResidual++;
                    continue;
                }

                treesConsidered++;

                bool fundsNonZero = Math.Abs(residual.DeltaFunds) > LegacyMigrationFundsTolerance;
                bool scienceNonZero = Math.Abs(residual.DeltaScience) > LegacyMigrationScienceTolerance;
                bool repNonZero = Math.Abs((double)residual.DeltaReputation) > LegacyMigrationReputationTolerance;
                bool anyNonZeroResidual = fundsNonZero || scienceNonZero || repNonZero;

                if (residual.ResourcesApplied)
                {
                    treesAlreadyApplied++;
                    RecordingStore.MarkTreeAsApplied(tree);
                    continue;
                }

                if (!anyNonZeroResidual)
                {
                    treesSkippedNoDelta++;
                    ParsekLog.Verbose(LedgerOrchestrator.Tag,
                        $"MigrateLegacyTreeResources: tree id='{tree.Id}' name='{tree.TreeName}' " +
                        $"all deltas within tolerance (funds={residual.DeltaFunds.ToString("R", CultureInfo.InvariantCulture)}, " +
                        $"science={residual.DeltaScience.ToString("R", CultureInfo.InvariantCulture)}, " +
                        $"rep={residual.DeltaReputation.ToString("R", CultureInfo.InvariantCulture)}), " +
                        "marking recordings fully applied without synthetic injection");
                    RecordingStore.MarkTreeAsApplied(tree);
                    continue;
                }

                double endUT = tree.ComputeEndUT();
                bool hasFundsSynthetic;
                bool hasScienceSynthetic;
                bool hasRepSynthetic;
                FindLegacyMigrationSyntheticMatches(
                    tree,
                    residual,
                    endUT,
                    out hasFundsSynthetic,
                    out hasScienceSynthetic,
                    out hasRepSynthetic);

                if ((!fundsNonZero || hasFundsSynthetic)
                    && (!scienceNonZero || hasScienceSynthetic)
                    && (!repNonZero || hasRepSynthetic))
                {
                    treesAlreadyMigrated++;
                    ParsekLog.Verbose(LedgerOrchestrator.Tag,
                        $"MigrateLegacyTreeResources: tree id='{tree.Id}' name='{tree.TreeName}' " +
                        "already has a LegacyMigration synthetic in the ledger; skipping duplicate injection");
                    RecordingStore.MarkTreeAsApplied(tree);
                    continue;
                }

                bool warnByFormatGate = false;
                if (endUT == 0)
                {
                    treesSkippedDegraded++;
                    warnByFormatGate = tree.TreeFormatVersion < RecordingTree.CurrentTreeFormatVersion;
                    ParsekLog.Info(LedgerOrchestrator.Tag,
                        $"MigrateLegacyTreeResources: skipping degraded tree id='{tree.Id}' " +
                        $"name='{tree.TreeName}' — ComputeEndUT()==0 (no trajectory data), " +
                        "marking recordings fully applied without synthetic injection " +
                        $"(stale deltaFunds={residual.DeltaFunds.ToString("R", CultureInfo.InvariantCulture)}, " +
                        $"deltaScience={residual.DeltaScience.ToString("R", CultureInfo.InvariantCulture)}, " +
                        $"deltaRep={residual.DeltaReputation.ToString("R", CultureInfo.InvariantCulture)})");
                    RecordingStore.MarkTreeAsApplied(tree);
                }
                else if (string.IsNullOrEmpty(tree.RootRecordingId))
                {
                    treesSkippedEmptyRoot++;
                    warnByFormatGate = tree.TreeFormatVersion < RecordingTree.CurrentTreeFormatVersion;
                    ParsekLog.Warn(LedgerOrchestrator.Tag,
                        $"MigrateLegacyTreeResources: tree id='{tree.Id}' name='{tree.TreeName}' " +
                        $"has empty RootRecordingId — cannot tag synthetic ledger actions; " +
                        "marking recordings fully applied without synthetic injection " +
                        $"(persisted residuals LOST: deltaFunds={residual.DeltaFunds.ToString("R", CultureInfo.InvariantCulture)}, " +
                        $"deltaScience={residual.DeltaScience.ToString("R", CultureInfo.InvariantCulture)}, " +
                        $"deltaRep={residual.DeltaReputation.ToString("R", CultureInfo.InvariantCulture)})");
                    RecordingStore.MarkTreeAsApplied(tree);
                }
                else
                {
                    double startUT = ComputeTreeStartUT(tree);
                    if (HasAnyLedgerCoverage(tree, residual, startUT, endUT))
                    {
                        treesSkippedPartialCoverage++;
                        warnByFormatGate = tree.TreeFormatVersion < RecordingTree.CurrentTreeFormatVersion;
                        ParsekLog.Warn(LedgerOrchestrator.Tag,
                            $"MigrateLegacyTreeResources: tree id='{tree.Id}' name='{tree.TreeName}' " +
                            $"has partial ledger coverage in UT window [{startUT.ToString("R", CultureInfo.InvariantCulture)}, " +
                            $"{endUT.ToString("R", CultureInfo.InvariantCulture)}]; skipping synthetic injection " +
                            $"(persisted deltaFunds={residual.DeltaFunds.ToString("R", CultureInfo.InvariantCulture)}, " +
                            $"deltaScience={residual.DeltaScience.ToString("R", CultureInfo.InvariantCulture)}, " +
                            $"deltaRep={residual.DeltaReputation.ToString("R", CultureInfo.InvariantCulture)})");
                        RecordingStore.MarkTreeAsApplied(tree);
                    }
                    else
                    {
                        bool injectedFunds = false;
                        bool injectedScience = false;
                        bool injectedRep = false;

                        if (fundsNonZero
                            && !hasFundsSynthetic
                            && TryInjectLegacyFundsEarning(tree, residual, endUT))
                        {
                            fundsInjected++;
                            injectedFunds = true;
                        }

                        if (scienceNonZero && !hasScienceSynthetic)
                        {
                            if (residual.DeltaScience > 0)
                                InjectLegacyScienceEarning(tree, residual.DeltaScience, endUT);
                            else
                                InjectLegacyScienceSpending(tree, residual.DeltaScience, endUT);
                            scienceInjected++;
                            injectedScience = true;
                        }

                        if (repNonZero && !hasRepSynthetic)
                        {
                            if (residual.DeltaReputation > 0)
                                InjectLegacyReputationEarning(tree, (double)residual.DeltaReputation, endUT);
                            else
                                InjectLegacyReputationPenalty(tree, (double)residual.DeltaReputation, endUT);
                            repInjected++;
                            injectedRep = true;
                        }

                        ParsekLog.Info(LedgerOrchestrator.Tag,
                            $"MigrateLegacyTreeResources: migrated tree id='{tree.Id}' name='{tree.TreeName}' " +
                            $"rootRec='{tree.RootRecordingId}' " +
                            $"startUT={startUT.ToString("R", CultureInfo.InvariantCulture)} " +
                            $"endUT={endUT.ToString("R", CultureInfo.InvariantCulture)} " +
                            "coverage=zero " +
                            $"deltaFunds={residual.DeltaFunds.ToString("R", CultureInfo.InvariantCulture)} " +
                            $"(injected={injectedFunds}), " +
                            $"deltaScience={residual.DeltaScience.ToString("R", CultureInfo.InvariantCulture)} " +
                            $"(injected={injectedScience}), " +
                            $"deltaRep={residual.DeltaReputation.ToString("R", CultureInfo.InvariantCulture)} " +
                            $"(injected={injectedRep})");

                        RecordingStore.MarkTreeAsApplied(tree);
                        treesMigrated++;
                    }
                }

                if (warnByFormatGate)
                {
                    WarnLegacyFormatGate(tree, residual);
                    treesWarnedByGate++;
                }
            }

            ParsekLog.Info(LedgerOrchestrator.Tag,
                $"MigrateLegacyTreeResources complete: considered={treesConsidered}, " +
                $"withoutResidual={treesWithoutLegacyResidual}, alreadyApplied={treesAlreadyApplied}, " +
                $"alreadyMigrated={treesAlreadyMigrated}, degraded={treesSkippedDegraded}, " +
                $"noDelta={treesSkippedNoDelta}, emptyRoot={treesSkippedEmptyRoot}, " +
                $"partialCoverage={treesSkippedPartialCoverage}, " +
                $"migrated={treesMigrated}, gateWarned={treesWarnedByGate}, " +
                $"fundsInjected={fundsInjected}, scienceInjected={scienceInjected}, " +
                $"repInjected={repInjected}");
        }

        /// <summary>
        /// Funds-emission helper: injects a <see cref="FundsEarningSource.LegacyMigration"/>
        /// FundsEarning with the raw (signed) persisted legacy residual.
        /// Returns true if a non-zero funds synthetic was emitted.
        ///
        /// <para>Sign note: <see cref="FundsModule.ProcessFundsEarning"/> adds the raw
        /// FundsAwarded to both <c>runningBalance</c> and <c>totalEarnings</c> with no clamp,
        /// so a negative FundsEarning correctly reduces both. The reviewer flagged an earlier
        /// concern about "poisoning totalEarnings" as wrong — a tree that net-lost funds
        /// really did reduce the player's income capacity, so a negative totalEarnings
        /// contribution is the right shape. Earning-type actions are pruned by RecordingId
        /// in <see cref="Ledger.Reconcile"/>, so the synthetic is cleaned up with the tree.</para>
        /// </summary>
        private static bool TryInjectLegacyFundsEarning(
            RecordingTree tree,
            RecordingTree.LegacyResourceResidual residual,
            double endUT)
        {
            if (Math.Abs(residual.DeltaFunds) <= LegacyMigrationFundsTolerance)
                return false;

            var action = new GameAction
            {
                UT = endUT,
                Type = GameActionType.FundsEarning,
                RecordingId = tree.RootRecordingId,
                FundsAwarded = (float)residual.DeltaFunds,
                FundsSource = FundsEarningSource.LegacyMigration
            };
            Ledger.AddAction(action);
            return true;
        }

        /// <summary>
        /// Science-emission helper: injects a <see cref="GameActionType.ScienceEarning"/>
        /// with <c>SubjectId="LegacyMigration:{treeId}"</c> (no source enum exists).
        /// Only called for positive residuals — negative science goes through
        /// <see cref="InjectLegacyScienceSpending"/> which emits a ScienceSpending instead.
        /// A generous <see cref="GameAction.SubjectMaxValue"/> is set so the per-subject
        /// hard cap never clips the migrated science on the walk.
        ///
        /// <para>Note on ContractComplete / strategy transforms (per reviewer NIT):
        /// the ContractComplete reward field used by partial-coverage residual math would
        /// need to be <c>TransformedScienceReward</c> once Phase E1.5 lands strategy
        /// capture — <c>ScienceReward</c> and <c>TransformedScienceReward</c> will diverge
        /// then. The zero-coverage scope sidesteps that problem entirely: we do not read
        /// ContractComplete fields at all, we only probe for presence.</para>
        /// </summary>
        private static void InjectLegacyScienceEarning(RecordingTree tree, double scienceDelta, double endUT)
        {
            float residualF = (float)scienceDelta;
            var action = new GameAction
            {
                UT = endUT,
                Type = GameActionType.ScienceEarning,
                RecordingId = tree.RootRecordingId,
                SubjectId = "LegacyMigration:" + tree.Id,
                ScienceAwarded = residualF,
                SubjectMaxValue = residualF + 10f
            };
            Ledger.AddAction(action);
        }

        /// <summary>
        /// Science-spending helper for negative residuals: injects a
        /// <see cref="GameActionType.ScienceSpending"/> with <c>Cost=-scienceDelta</c>
        /// (magnitude, since <see cref="ScienceModule.ProcessSpending"/> subtracts
        /// <c>Cost</c> from the running balance). <see cref="Ledger.Reconcile"/> prunes
        /// spendings by RecordingId since #441, so this synthetic is purged with the tree
        /// on deletion — same contract as the positive-residual earning.
        /// </summary>
        private static void InjectLegacyScienceSpending(RecordingTree tree, double scienceDelta, double endUT)
        {
            float costF = (float)(-scienceDelta);
            var action = new GameAction
            {
                UT = endUT,
                Type = GameActionType.ScienceSpending,
                RecordingId = tree.RootRecordingId,
                NodeId = "LegacyMigration:" + tree.Id,
                Cost = costF
            };
            Ledger.AddAction(action);
        }

        /// <summary>
        /// Reputation-emission helper: injects a <see cref="GameActionType.ReputationEarning"/>
        /// with <see cref="ReputationSource.Other"/>. Only called for positive residuals —
        /// negative rep goes through <see cref="InjectLegacyReputationPenalty"/> which emits
        /// a ReputationPenalty instead.
        /// </summary>
        private static void InjectLegacyReputationEarning(RecordingTree tree, double repDelta, double endUT)
        {
            var action = new GameAction
            {
                UT = endUT,
                Type = GameActionType.ReputationEarning,
                RecordingId = tree.RootRecordingId,
                NominalRep = (float)repDelta,
                RepSource = ReputationSource.Other
            };
            Ledger.AddAction(action);
        }

        /// <summary>
        /// Reputation-penalty helper for negative residuals: injects a
        /// <see cref="GameActionType.ReputationPenalty"/> with <c>NominalPenalty=-repDelta</c>
        /// (magnitude; <see cref="ReputationModule"/> stores penalty as a positive magnitude
        /// and negates it internally before applying the curve).
        /// <see cref="ReputationPenaltySource.Other"/> matches the earning helper's
        /// <see cref="ReputationSource.Other"/>. <see cref="Ledger.Reconcile"/> prunes
        /// spendings by RecordingId since #441.
        /// </summary>
        private static void InjectLegacyReputationPenalty(RecordingTree tree, double repDelta, double endUT)
        {
            var action = new GameAction
            {
                UT = endUT,
                Type = GameActionType.ReputationPenalty,
                RecordingId = tree.RootRecordingId,
                NominalPenalty = (float)(-repDelta),
                RepPenaltySource = ReputationPenaltySource.Other
            };
            Ledger.AddAction(action);
        }

        private static void FindLegacyMigrationSyntheticMatches(
            RecordingTree tree,
            RecordingTree.LegacyResourceResidual residual,
            double endUT,
            out bool hasFundsSynthetic,
            out bool hasScienceSynthetic,
            out bool hasRepSynthetic)
        {
            bool fundsNonZero = Math.Abs(residual.DeltaFunds) > LegacyMigrationFundsTolerance;
            bool scienceNonZero = Math.Abs(residual.DeltaScience) > LegacyMigrationScienceTolerance;
            bool repNonZero = Math.Abs((double)residual.DeltaReputation) > LegacyMigrationReputationTolerance;
            hasFundsSynthetic = !fundsNonZero;
            hasScienceSynthetic = !scienceNonZero;
            hasRepSynthetic = !repNonZero;
            if (hasFundsSynthetic && hasScienceSynthetic && hasRepSynthetic)
                return;

            string scienceKey = "LegacyMigration:" + tree.Id;
            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action == null)
                    continue;

                if (!hasFundsSynthetic
                    && IsMatchingLegacyFundsMigration(action, tree, residual, endUT))
                {
                    hasFundsSynthetic = true;
                }

                if (!hasScienceSynthetic)
                {
                    if (IsMatchingLegacyScienceMigration(action, tree, residual, endUT))
                    {
                        hasScienceSynthetic = true;
                    }
                }

                if (!hasRepSynthetic
                    && IsMatchingLegacyReputationMigration(action, tree, residual, endUT))
                {
                    hasRepSynthetic = true;
                }

                if (hasFundsSynthetic && hasScienceSynthetic && hasRepSynthetic)
                    return;
            }
        }

        private static void WarnLegacyFormatGate(
            RecordingTree tree,
            RecordingTree.LegacyResourceResidual residual)
        {
            string treeId = !string.IsNullOrEmpty(tree.Id)
                ? tree.Id
                : (tree.TreeName ?? "(unknown)");

            ParsekLog.Warn(LegacyFormatGateTag,
                $"Tree '{treeId}' has pre-Phase-F legacy resource fields " +
                $"(funds={residual.DeltaFunds.ToString("R", CultureInfo.InvariantCulture)}, " +
                $"sci={residual.DeltaScience.ToString("R", CultureInfo.InvariantCulture)}, " +
                $"rep={residual.DeltaReputation.ToString("R", CultureInfo.InvariantCulture)}) " +
                "that Phase A migration could not recover. Legacy resource values will " +
                "not be applied; ledger state may differ by this residual. Open the save " +
                "in a 0.8.1 build first if accurate migration matters.");
        }

        /// <summary>
        /// Returns the earliest non-zero StartUT across the tree's recordings, or 0 if
        /// no recording has points. Needed as a conservative lower bound when matching
        /// ledger actions back to a tree's UT window; <see cref="RecordingTree"/> does
        /// not carry a stored StartUT field.
        /// </summary>
        private static double ComputeTreeStartUT(RecordingTree tree)
        {
            double startUT = double.MaxValue;
            bool any = false;
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec == null) continue;
                double recStart = rec.StartUT;
                if (recStart <= 0) continue;
                if (recStart < startUT)
                {
                    startUT = recStart;
                    any = true;
                }
            }
            return any ? startUT : 0.0;
        }

        /// <summary>
        /// Pure classifier: returns true when the given action type actually moves the
        /// funds/science/reputation pools (so its presence inside a tree's UT window counts
        /// as ledger coverage for that tree). Non-resource action types — roster changes
        /// (<see cref="GameActionType.KerbalAssignment"/>, <see cref="GameActionType.KerbalRescue"/>,
        /// <see cref="GameActionType.KerbalStandIn"/>), <see cref="GameActionType.FacilityDestruction"/>
        /// (stateful only, no cost), <see cref="GameActionType.StrategyDeactivate"/> (stateful only,
        /// no cost), and the three <c>*Initial</c> seed types — return false.
        ///
        /// <para>This classification exists specifically so the
        /// <see cref="HasAnyLedgerCoverage"/> probe is not tripped by
        /// <see cref="MigrateKerbalAssignments"/>'s backfilled <c>KerbalAssignment</c> rows,
        /// which run earlier in <see cref="OnKspLoad"/> and would otherwise flag every
        /// crewed legacy tree as partially covered, permanently dropping the residual.</para>
        ///
        /// <para>When adding a new <see cref="GameActionType"/>, this switch must be updated
        /// and the <c>IsResourceImpactingAction_Theory</c> test in
        /// <c>LegacyTreeMigrationTests.cs</c> will fail until an entry is added — which is
        /// the intended forcing function for a code review.</para>
        /// </summary>
        internal static bool IsResourceImpactingAction(GameActionType t)
        {
            switch (t)
            {
                // Earnings/spendings that directly move a pool.
                case GameActionType.FundsEarning:
                case GameActionType.FundsSpending:
                case GameActionType.ScienceEarning:
                case GameActionType.ScienceSpending:
                case GameActionType.ReputationEarning:
                case GameActionType.ReputationPenalty:
                    return true;

                // Composite rewards/penalties consumed by first-tier + second-tier modules.
                case GameActionType.MilestoneAchievement:  // MilestoneFunds/Sci/RepAwarded
                case GameActionType.ContractAccept:        // AdvanceFunds
                case GameActionType.ContractComplete:      // FundsReward/ScienceReward/RepReward
                case GameActionType.ContractFail:          // FundsPenalty/RepPenalty
                case GameActionType.ContractCancel:        // FundsPenalty/RepPenalty
                    return true;

                // Infrastructure/strategy costs consumed by FundsModule / StrategiesModule.
                case GameActionType.FacilityUpgrade:       // FacilityCost
                case GameActionType.FacilityRepair:        // FacilityCost
                case GameActionType.KerbalHire:            // HireCost
                case GameActionType.StrategyActivate:      // SetupCost
                    return true;

                // Non-resource action types: roster changes, stateful facility/strategy flips,
                // and the immutable seed rows. These are intentionally ignored by the
                // coverage probe — see class summary for the false-positive this guards.
                case GameActionType.KerbalAssignment:
                case GameActionType.KerbalRescue:
                case GameActionType.KerbalStandIn:
                case GameActionType.FacilityDestruction:
                case GameActionType.StrategyDeactivate:
                case GameActionType.FundsInitial:
                case GameActionType.ScienceInitial:
                case GameActionType.ReputationInitial:
                    return false;

                default:
                    // Conservative: an unrecognized new action type is treated as non-
                    // resource so it cannot poison the zero-coverage probe. The
                    // IsResourceImpactingAction_Theory test will flag any new enum value
                    // that reaches this branch.
                    return false;
            }
        }

        /// <summary>
        /// Zero-coverage probe: returns true if <see cref="Ledger.Actions"/> contains at
        /// least one <see cref="IsResourceImpactingAction"/>-passing action whose UT falls
        /// inside <paramref name="startUT"/>..<paramref name="endUT"/> AND whose
        /// <see cref="GameAction.RecordingId"/> is either a key in
        /// <see cref="RecordingTree.Recordings"/> OR null (null-tagged actions may be
        /// <see cref="MigrateOldSaveEvents"/> output — see the flag gate below).
        ///
        /// <para>Two round-2 P1s this shape fixes:</para>
        /// <list type="number">
        ///   <item><description><see cref="MigrateKerbalAssignments"/>'s
        ///   <see cref="GameActionType.KerbalAssignment"/> rows (tagged with recording id,
        ///   added before this migration runs) are excluded by the type filter — without
        ///   this, every crewed legacy tree would be falsely flagged as partially covered
        ///   and its residual silently dropped.</description></item>
        ///   <item><description><see cref="MigrateOldSaveEvents"/>'s null-tagged reward
        ///   synthetics (ContractComplete / MilestoneAchievement / ContractAccept from
        ///   GameStateStore on first load of a pre-ledger save) register as coverage ONLY
        ///   when <see cref="migrateOldSaveEventsRanThisLoad"/> is true — otherwise an
        ///   uncrewed legacy tree on first load would get its full residual injected on
        ///   top of the already-migrated rewards, double-crediting.</description></item>
        /// </list>
        ///
        /// <para>Round-2 P1 (PR #347 external review) — null-tag gate:
        /// <see cref="TryRecoverBrokenLedgerOnLoad"/> runs on every load and persists
        /// null-tagged <see cref="GameActionType.ContractAccept"/>/<see cref="GameActionType.ContractComplete"/>/<see cref="GameActionType.ContractFail"/>/<see cref="GameActionType.ContractCancel"/>/<see cref="GameActionType.FundsSpending"/>
        /// actions into the ledger file. On a subsequent load of a long-running mission
        /// save, those previously-persisted null-tagged KSC actions can land at UTs
        /// inside the legacy tree's window (multi-day flight spanning normal KSC
        /// activity: contract accept, part purchase, contract cancel). Without the
        /// flag gate, the probe sees them and falsely concludes the tree is covered —
        /// residual silently lost. With the gate, null-tagged in-window actions only
        /// count when <see cref="MigrateOldSaveEvents"/> actually synthesized actions
        /// on THIS load (the case where the first-load double-credit protection is
        /// needed); in every other case they are ignored, and the tree gets its
        /// residual injected as intended.</para>
        ///
        /// <para>Replaces the v1 residual-math approach: persisted tree deltas were captured
        /// from LIVE KSP state post-commit (post-cap, post-curve, post-strategy-transform,
        /// post-Effective-suppression), so subtracting pre-walk raw field sums was
        /// structurally wrong for any tree with partial coverage.</para>
        ///
        /// <para>Pure; does not mutate state. Early-returns on first match.</para>
        /// </summary>
        private static bool HasAnyLedgerCoverage(
            RecordingTree tree,
            RecordingTree.LegacyResourceResidual residual,
            double startUT,
            double endUT)
        {
            var recordings = tree.Recordings;
            if (recordings == null || recordings.Count == 0)
                return false;

            var ledgerActions = Ledger.Actions;
            for (int i = 0; i < ledgerActions.Count; i++)
            {
                var action = ledgerActions[i];
                if (action == null) continue;
                if (!IsResourceImpactingAction(action.Type)) continue;
                if (action.UT < startUT || action.UT > endUT) continue;

                string recId = action.RecordingId;
                bool nullTagged = string.IsNullOrEmpty(recId);
                bool treeTagged = !nullTagged && recordings.ContainsKey(recId);

                if (treeTagged)
                {
                    if (IsMatchingLegacyFundsMigration(action, tree, residual, endUT)
                        || IsMatchingLegacyScienceMigration(action, tree, residual, endUT)
                        || IsMatchingLegacyReputationMigration(action, tree, residual, endUT))
                    {
                        continue;
                    }

                    // Tree-tagged in-window action always counts.
                    return true;
                }
                if (nullTagged && migrateOldSaveEventsRanThisLoad)
                {
                    // Null-tagged action counts ONLY when MigrateOldSaveEvents just ran
                    // this load — see the flag's XML doc for why TryRecoverBrokenLedgerOnLoad
                    // output (persisted from prior loads) must NOT trigger this branch.
                    return true;
                }
                // Otherwise skip — non-tree-tagged (orphaned) actions and null-tagged
                // KSC activity during normal operation do not count as tree coverage.
            }
            return false;
        }

        private static bool IsMatchingLegacyFundsMigration(
            GameAction action,
            RecordingTree tree,
            RecordingTree.LegacyResourceResidual residual,
            double endUT)
        {
            return action != null
                && Math.Abs(residual.DeltaFunds) > LegacyMigrationFundsTolerance
                && action.Type == GameActionType.FundsEarning
                && action.FundsSource == FundsEarningSource.LegacyMigration
                && action.RecordingId == tree.RootRecordingId
                && Math.Abs(action.UT - endUT) <= 0.001
                && Math.Abs(action.FundsAwarded - (float)residual.DeltaFunds)
                    <= LegacyMigrationFundsTolerance;
        }

        private static bool IsMatchingLegacyScienceMigration(
            GameAction action,
            RecordingTree tree,
            RecordingTree.LegacyResourceResidual residual,
            double endUT)
        {
            if (action == null
                || Math.Abs(residual.DeltaScience) <= LegacyMigrationScienceTolerance
                || action.RecordingId != tree.RootRecordingId
                || Math.Abs(action.UT - endUT) > 0.001)
            {
                return false;
            }

            string scienceKey = "LegacyMigration:" + tree.Id;
            if (residual.DeltaScience > 0)
            {
                return action.Type == GameActionType.ScienceEarning
                    && action.SubjectId == scienceKey
                    && Math.Abs(action.ScienceAwarded - (float)residual.DeltaScience)
                        <= LegacyMigrationScienceTolerance;
            }

            return action.Type == GameActionType.ScienceSpending
                && action.NodeId == scienceKey
                && Math.Abs(action.Cost - (float)(-residual.DeltaScience))
                    <= LegacyMigrationScienceTolerance;
        }

        private static bool IsMatchingLegacyReputationMigration(
            GameAction action,
            RecordingTree tree,
            RecordingTree.LegacyResourceResidual residual,
            double endUT)
        {
            if (action == null
                || Math.Abs((double)residual.DeltaReputation) <= LegacyMigrationReputationTolerance
                || action.RecordingId != tree.RootRecordingId
                || Math.Abs(action.UT - endUT) > 0.001)
            {
                return false;
            }

            if (residual.DeltaReputation > 0)
            {
                return action.Type == GameActionType.ReputationEarning
                    && action.RepSource == ReputationSource.Other
                    && Math.Abs(action.NominalRep - (float)residual.DeltaReputation)
                        <= LegacyMigrationReputationTolerance;
            }

            return action.Type == GameActionType.ReputationPenalty
                && action.RepPenaltySource == ReputationPenaltySource.Other
                && Math.Abs(action.NominalPenalty - (float)(-residual.DeltaReputation))
                    <= LegacyMigrationReputationTolerance;
        }

        /// <summary>
        /// Rewrites persisted legacy part-purchase ledger rows whose stored funds debit
        /// still reflects rollout <c>part.cost</c> instead of the historical R&amp;D
        /// charge proved by saved game-state events. Existing version-1 ledger files
        /// keep the same shape; the compatibility path mutates the in-memory actions
        /// after load and before the first recalculation walk. Ambiguous rows are
        /// preserved as-is rather than guessed from current runtime semantics; the
        /// only no-funds fallback is the known stock-bypass save shape that rewrites
        /// the matched <c>PartPurchased</c> event to zero-cost first.
        /// </summary>
        internal static int RepairLegacyPartPurchaseActionsOnLoad(
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> ledgerActions)
        {
            if (ledgerActions == null || ledgerActions.Count == 0)
                return 0;

            int repaired = 0;
            for (int i = 0; i < ledgerActions.Count; i++)
            {
                var action = ledgerActions[i];
                if (!IsPartPurchaseFundsSpendingAction(action))
                    continue;

                float canonicalCost;
                if (!TryResolveCanonicalPartPurchaseChargeForAction(action, events, out canonicalCost))
                    continue;

                if (Math.Abs(action.FundsSpent - canonicalCost) <= 0.01f)
                    continue;

                action.FundsSpent = canonicalCost;
                repaired++;
            }

            return repaired;
        }

        private static bool IsPartPurchaseFundsSpendingAction(GameAction action)
        {
            return action != null &&
                   action.Type == GameActionType.FundsSpending &&
                   action.FundsSpendingSource == FundsSpendingSource.Other;
        }

        private static bool TryResolveCanonicalPartPurchaseChargeForAction(
            GameAction action,
            IReadOnlyList<GameStateEvent> events,
            out float canonicalCost)
        {
            canonicalCost = 0f;
            if (action == null)
                return false;

            GameStateEvent matchedEvent;
            if (TryFindMatchingPartPurchasedEvent(events, action, out matchedEvent))
            {
                GameStateEvent rewrittenEvent;
                if (GameStateStore.TryRewriteLegacyPartPurchaseEvent(
                    matchedEvent, events, out rewrittenEvent))
                {
                    matchedEvent = rewrittenEvent;
                }

                if (GameStateStore.TryGetStoredPartPurchaseCost(
                    matchedEvent.detail, out canonicalCost))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryFindMatchingPartPurchasedEvent(
            IReadOnlyList<GameStateEvent> events,
            GameAction action,
            out GameStateEvent matchedEvent)
        {
            matchedEvent = default(GameStateEvent);
            if (events == null || action == null)
                return false;

            string partName = action.DedupKey;
            int fallbackCount = 0;
            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                if (evt.eventType != GameStateEventType.PartPurchased)
                    continue;
                if (Math.Abs(evt.ut - action.UT) > LedgerOrchestrator.KscReconcileEpsilonSeconds)
                    continue;

                if (!string.IsNullOrEmpty(partName))
                {
                    if (string.Equals(evt.key, partName, StringComparison.Ordinal))
                    {
                        matchedEvent = evt;
                        return true;
                    }

                    continue;
                }

                matchedEvent = evt;
                fallbackCount++;
                if (fallbackCount > 1)
                    return false;
            }

            return fallbackCount == 1;
        }

        /// <summary>
        /// One-shot save recovery migration for #401 (c1 bricked funds) and #396
        /// (sci1 bricked science). Walks the GameStateStore events and committed
        /// science subjects, synthesizes a matching GameAction for any entry that
        /// does NOT already have one in the ledger, and adds them.
        ///
        /// Must be called AFTER committed recordings (and any cold-start pending
        /// tree) have been restored, because the migration only acts on events
        /// whose recording tags are still visible in the current timeline.
        ///
        /// Idempotent: the LedgerHasMatchingAction guard makes repeat loads a no-op.
        /// </summary>
        internal static int TryRecoverBrokenLedgerOnLoad()
        {
            LedgerOrchestrator.Initialize();
            GameStateStore.RepairLegacyPartPurchaseEventsForCurrentSemantics();

            int recoveredFundsParts = 0;
            int recoveredContracts = 0;
            int recoveredScience = 0;
            int skippedHidden = 0;

            // 1) Funds + contract events
            var events = GameStateStore.Events;
            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                if (!GameStateStore.IsEventVisibleToCurrentTimeline(evt))
                {
                    skippedHidden++;
                    continue;
                }
                if (!IsRecoverableEventType(evt.eventType)) continue;
                if (LedgerHasMatchingAction(evt)) continue;

                var action = GameStateEventConverter.ConvertEvent(evt, null);
                if (action == null) continue;

                Ledger.AddAction(action);
                if (evt.eventType == GameStateEventType.PartPurchased)
                    recoveredFundsParts++;
                else
                    recoveredContracts++;

                ParsekLog.Verbose(LedgerOrchestrator.Tag,
                    $"TryRecoverBrokenLedgerOnLoad: synthesized {action.Type} action for " +
                    $"event {evt.eventType} key='{evt.key}' ut={evt.ut:F1}");
            }

            // 2) Committed science subjects
            // Use the persisted store as the recovery source of truth. Broken pre-#397
            // saves are missing ScienceEarning rows in the ledger; rebuilding the store
            // from the broken ledger here would erase the very totals we need to heal.
            // When some earnings already survive, synthesize only the missing delta so
            // cumulative subject totals do not double-count.
            var subjectIds = GameStateStore.GetCommittedScienceSubjectIds();
            if (subjectIds != null)
            {
                foreach (var subjectId in subjectIds)
                {
                    if (string.IsNullOrEmpty(subjectId)) continue;
                    if (!GameStateStore.TryGetCommittedSubjectScience(subjectId, out float sci)) continue;
                    if (sci <= 0f) continue;

                    float existingScience = GetLedgerScienceEarningTotal(subjectId);
                    float missingScience = sci - existingScience;
                    if (missingScience <= 0.01f) continue;

                    var action = new GameAction
                    {
                        // Science earnings need a UT for ordering; use the earliest
                        // pseudo-UT so the synthesized action slots in at the beginning
                        // of the currently-visible timeline.
                        UT = 0.0,
                        Type = GameActionType.ScienceEarning,
                        RecordingId = null,
                        SubjectId = subjectId,
                        ScienceAwarded = missingScience,
                        SubjectMaxValue = sci + 10f  // generous cap so walk doesn't clip
                    };
                    Ledger.AddAction(action);
                    recoveredScience++;

                    ParsekLog.Verbose(LedgerOrchestrator.Tag,
                        $"TryRecoverBrokenLedgerOnLoad: synthesized ScienceEarning for " +
                        $"subject='{subjectId}' missingSci={missingScience:F1} " +
                        $"(stored={sci:F1}, existing={existingScience:F1})");
                }
            }

            int totalRecovered = recoveredFundsParts + recoveredContracts + recoveredScience;
            if (totalRecovered > 0)
            {
                ParsekLog.Warn(LedgerOrchestrator.Tag,
                    $"TryRecoverBrokenLedgerOnLoad: synthesized {recoveredFundsParts} funds/part, " +
                    $"{recoveredContracts} contract, {recoveredScience} science actions from store " +
                    $"(skippedHidden={skippedHidden}).");
            }
            else
            {
                ParsekLog.Verbose(LedgerOrchestrator.Tag,
                    $"TryRecoverBrokenLedgerOnLoad: no recovery needed " +
                    $"(skippedHidden={skippedHidden})");
            }

            return totalRecovered;
        }

        // Scope limit (deliberate): only contract state changes and part purchases
        // are migrated because those are the event types that #405/#404 actually
        // stripped from the ledger on c1/sci1 saves, so those are the ones a broken
        // save will be missing. MilestoneAchieved is excluded because historical
        // milestones were emitted with funds=0/rep=0 hardcoded — synthesizing them
        // would only add zero-reward actions. CrewHired, TechResearched, and
        // FacilityUpgraded are excluded because their real-time OnKscSpending writes
        // were never broken; c1/sci1 already have those actions. A future broken
        // save that surfaces missing hires/tech/facility actions would need this
        // list extended (see todo-and-known-bugs.md).
        /// <summary>Pure: event types the migration can synthesize actions for.</summary>
        internal static bool IsRecoverableEventType(GameStateEventType t)
        {
            switch (t)
            {
                case GameStateEventType.ContractAccepted:
                case GameStateEventType.ContractCompleted:
                case GameStateEventType.ContractFailed:
                case GameStateEventType.ContractCancelled:
                case GameStateEventType.PartPurchased:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Pure: checks whether the ledger already has an action matching the given
        /// event (same type + UT within epsilon + key). Used by the save recovery
        /// migration to avoid duplicating an action that already exists.
        /// </summary>
        internal static bool LedgerHasMatchingAction(GameStateEvent evt)
        {
            GameActionType? expectedType = MapEventTypeToActionType(evt.eventType);
            if (expectedType == null) return true;   // unmappable => don't recover

            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a.Type != expectedType.Value) continue;
                if (Math.Abs(a.UT - evt.ut) > 0.1) continue;

                // Match on the type-specific key field.
                switch (a.Type)
                {
                    case GameActionType.ContractAccept:
                    case GameActionType.ContractComplete:
                    case GameActionType.ContractFail:
                    case GameActionType.ContractCancel:
                        if (a.ContractId == evt.key) return true;
                        break;
                    case GameActionType.FundsSpending:
                        // Part purchase: evt.key is the part name, stored on DedupKey.
                        if (a.DedupKey == evt.key) return true;
                        break;
                    default:
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Pure: checks whether the ledger already has a ScienceEarning action for
        /// the given subject with at least the given science value credited. Uses
        /// &gt;= comparison so multi-experiment top-ups don't synthesize duplicates.
        /// </summary>
        internal static bool LedgerHasMatchingScienceEarning(string subjectId, float minScience)
        {
            if (string.IsNullOrEmpty(subjectId)) return true;
            return GetLedgerScienceEarningTotal(subjectId) >= minScience - 0.01f;
        }

        /// <summary>
        /// Pure: returns the total ScienceAwarded already present in the ledger for a
        /// given subject. Used by save recovery to synthesize only the missing delta
        /// when a broken save kept some ScienceEarning rows but lost later top-ups.
        /// </summary>
        internal static float GetLedgerScienceEarningTotal(string subjectId)
        {
            if (string.IsNullOrEmpty(subjectId)) return 0f;

            float totalForSubject = 0f;
            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a.Type != GameActionType.ScienceEarning) continue;
                if (a.SubjectId != subjectId) continue;
                totalForSubject += a.ScienceAwarded;
            }

            return totalForSubject;
        }

        /// <summary>Pure: maps GameStateEventType to the corresponding GameActionType.</summary>
        internal static GameActionType? MapEventTypeToActionType(GameStateEventType t)
        {
            switch (t)
            {
                case GameStateEventType.ContractAccepted: return GameActionType.ContractAccept;
                case GameStateEventType.ContractCompleted: return GameActionType.ContractComplete;
                case GameStateEventType.ContractFailed: return GameActionType.ContractFail;
                case GameStateEventType.ContractCancelled: return GameActionType.ContractCancel;
                case GameStateEventType.PartPurchased: return GameActionType.FundsSpending;
                case GameStateEventType.TechResearched: return GameActionType.ScienceSpending;
                case GameStateEventType.CrewHired: return GameActionType.KerbalHire;
                case GameStateEventType.MilestoneAchieved: return GameActionType.MilestoneAchievement;
                case GameStateEventType.FacilityUpgraded: return GameActionType.FacilityUpgrade;
                default: return null;
            }
        }

    }
}
