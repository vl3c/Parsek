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
            //
            // MIGRATION-CONTEXT FILTER (not a converter behavior change): the KSC and
            // commit doors must keep converting ScienceChanged(StrategyInput) into a
            // StrategyScienceDebit, but this retro-fill path must NOT. A null recordingId
            // scope matches EVERY stored event, so an old save's strategy exchange would
            // be retro-created as a debit while the science EARNINGS of that same era are
            // not reconstructible here (they rode PendingScienceSubjects, never a
            // GameStateEvent). Retro-filling one side biases the reconstruction LOW and
            // trips the drawdown-guard clamp + player toast on every old-save load.
            // Pre-fix history stays pre-fix.
            double minUT = 0;
            double maxUT = double.MaxValue;
            var convertible = FilterMigrationConvertibleEvents(events);
            var actions = GameStateEventConverter.ConvertEvents(convertible, null, minUT, maxUT);

            if (actions.Count > 0)
            {
                Ledger.AddActions(actions);
                // Records that this load actually synthesized old-save reward actions.
                // Set only on actual synthesis so an empty MigrateOldSaveEvents leaves
                // the flag unchanged.
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

        /// <summary>
        /// Pure: the event types <see cref="MigrateOldSaveEvents"/> is allowed to convert.
        /// Everything is convertible EXCEPT <see cref="GameStateEventType.ScienceChanged"/>
        /// - see <see cref="IsMigrationConvertibleEvent"/> for why.
        /// </summary>
        internal static List<GameStateEvent> FilterMigrationConvertibleEvents(
            IReadOnlyList<GameStateEvent> events)
        {
            var kept = new List<GameStateEvent>(events == null ? 0 : events.Count);
            if (events == null)
                return kept;

            int excluded = 0;
            for (int i = 0; i < events.Count; i++)
            {
                if (IsMigrationConvertibleEvent(events[i].eventType))
                    kept.Add(events[i]);
                else
                    excluded++;
            }

            if (excluded > 0)
            {
                ParsekLog.Verbose(LedgerOrchestrator.Tag,
                    $"MigrateOldSaveEvents: excluded {excluded.ToString(CultureInfo.InvariantCulture)} " +
                    "ScienceChanged event(s) from the retro-fill conversion (pre-fix history " +
                    "stays pre-fix; a retro-created strategy science debit has no " +
                    "reconstructible earning to balance it)");
            }

            return kept;
        }

        /// <summary>
        /// Pure: true when <see cref="MigrateOldSaveEvents"/> may convert an event of this
        /// type. Only <see cref="GameStateEventType.ScienceChanged"/> is excluded.
        ///
        /// <para>The converter's ScienceChanged carve-out
        /// (<c>ConvertStrategyExchangeScience</c>, STRATEGY-SCIENCE-CONVERSION-LEAK) is
        /// correct at the live KSC door and at commit time, where the surrounding science
        /// EARNINGS are captured by the same era of code. It is wrong on the retro-fill
        /// path: this migration converts with a NULL recording scope, so it matches every
        /// stored event regardless of age, and an old save's science earnings rode
        /// <c>PendingScienceSubjects</c> rather than a <c>GameStateEvent</c> - they cannot
        /// be reconstructed here. Retro-creating only the debit half biases the
        /// reconstruction LOW by the traded amount and trips the drawdown-guard clamp
        /// (plus its one-shot player toast) on the old save's very first load.</para>
        ///
        /// <para>This is a MIGRATION-CONTEXT filter, deliberately NOT a change to
        /// <c>GameStateEventConverter</c>: the KSC and commit doors keep converting.</para>
        /// </summary>
        internal static bool IsMigrationConvertibleEvent(GameStateEventType type)
        {
            return type != GameStateEventType.ScienceChanged;
        }

        /// <summary>
        /// Pure classifier: returns true when the given action type actually moves the
        /// funds/science/reputation pools. Non-resource action types — roster changes
        /// (<see cref="GameActionType.KerbalAssignment"/>, <see cref="GameActionType.KerbalRescue"/>,
        /// <see cref="GameActionType.KerbalStandIn"/>), <see cref="GameActionType.FacilityDestruction"/>
        /// (stateful only, no cost), <see cref="GameActionType.StrategyDeactivate"/> (stateful only,
        /// no cost), and the three <c>*Initial</c> seed types — return false.
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
                // Strategy currency-exchange science leg: ScienceModule debits the pool
                // by Cost, so it moves a resource exactly like a ScienceSpending.
                case GameActionType.StrategyScienceDebit:
                // Strategy currency-converter science yield: credits the pool, so it
                // moves a resource exactly like a ScienceEarning.
                case GameActionType.StrategyScienceCredit:
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

                // Supply-route per-cycle funds (logistics-recovery-credit, Option A):
                // FundsModule now consumes BOTH the gross dispatch debit
                // (RouteCargoDebited, as a spending) and the deferred recovery credit
                // (RouteRecoveryCredited, as an earning), so both move the funds pool
                // and are resource-impacting. The other route types below stay
                // non-impacting (no resource module consumes them).
                case GameActionType.RouteCargoDebited:     // RouteKscFundsCost (spending)
                case GameActionType.RouteRecoveryCredited: // RouteKscFundsCost (earning)
                    return true;

                // Non-resource action types: roster changes, stateful facility/strategy flips,
                // and the immutable seed rows. These are intentionally ignored by the
                // coverage probe — see class summary for the false-positive this guards.
                case GameActionType.KerbalAssignment:
                case GameActionType.KerbalRescue:
                case GameActionType.KerbalStandIn:
                // KerbalRecovered bounds a crew reservation; no pool moves.
                case GameActionType.KerbalRecovered:
                // VesselRecovered is spawn evidence only; no pool moves.
                case GameActionType.VesselRecovered:
                case GameActionType.FacilityDestruction:
                case GameActionType.StrategyDeactivate:
                case GameActionType.FundsInitial:
                case GameActionType.ScienceInitial:
                case GameActionType.ReputationInitial:
                    return false;

                // Route skeleton (design doc section 6): these route types carry no funds /
                // science / reputation movement that any resource module consumes.
                // RouteDispatched is a scheduler-decision marker; RouteCargoDelivered
                // records the delivery (its funds effect is on the paired
                // RouteCargoDebited, not on the delivered row's own FundsModule case);
                // RoutePaused / RouteEndpointLost are scheduler state flips. The
                // funds-moving route rows (RouteCargoDebited, RouteRecoveryCredited)
                // are classified true above.
                //
                // RouteCargoPickedUp (logistics M3, design D6): the per-window pickup
                // debit moves NO funds pool (loaded-en-route cargo debits its physical
                // source, never funds) and no resource module consumes it - the
                // physical removal happened live at emit and is reverted by the rewind
                // quicksave, exactly like RouteCargoDebited's physical half. So it
                // mirrors RouteCargoDelivered: false here.
                case GameActionType.RouteDispatched:
                case GameActionType.RouteCargoDelivered:
                case GameActionType.RouteCargoPickedUp:
                case GameActionType.RoutePaused:
                // RouteResumed (route-timeline events): a scheduler state flip like
                // RoutePaused; moves no funds / science / reputation.
                case GameActionType.RouteResumed:
                case GameActionType.RouteEndpointLost:
                // RouteHeld: records that a run was held; moves no pool.
                case GameActionType.RouteHeld:
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

        // Refusal tokens of TryResolveLegacyFacilityUpgradeCost (one summary line counts them).
        internal const string LegacyUpgradeRefusalExplicitZero = "explicit-zero";
        internal const string LegacyUpgradeRefusalDowngradeInWindow = "downgrade-in-window";
        internal const string LegacyUpgradeRefusalAmbiguousRows = "ambiguous-rows";
        internal const string LegacyUpgradeRefusalNoDebitEvent = "no-debit-event";
        internal const string LegacyUpgradeRefusalAmbiguousEvents = "ambiguous-events";
        internal const string LegacyUpgradeRefusalNonDebit = "non-debit";

        /// <summary>Tally of one <see cref="RepairZeroCostFacilityUpgradeActionsOnLoad"/> pass.</summary>
        internal struct FacilityUpgradeCostRepairResult
        {
            public int ZeroCostRows;
            public int Repaired;
            public int ExplicitZero;
            public int DowngradeInWindow;
            public int AmbiguousRows;
            public int NoDebitEvent;
            public int AmbiguousEvents;
            public int NonDebit;

            public string Format()
            {
                var ic = CultureInfo.InvariantCulture;
                return "zeroCostRows=" + ZeroCostRows.ToString(ic)
                    + " repaired=" + Repaired.ToString(ic)
                    + " noDebitEvent=" + NoDebitEvent.ToString(ic)
                    + " ambiguousRows=" + AmbiguousRows.ToString(ic)
                    + " ambiguousEvents=" + AmbiguousEvents.ToString(ic)
                    + " downgradeInWindow=" + DowngradeInWindow.ToString(ic)
                    + " nonDebit=" + NonDebit.ToString(ic)
                    + " explicitZero=" + ExplicitZero.ToString(ic);
            }
        }

        /// <summary>
        /// Gives a FacilityUpgrade row that was written at cost 0 (every upgrade before the
        /// UpgradeFacility cost capture, see <see cref="FacilityUpgradeCapture"/>) the funds
        /// debit stock made for it, when - and only when - the saved game-state events still
        /// prove it: the one <c>FundsChanged(StructureConstruction)</c> event of the row's own
        /// recording tag at the row's UT. That event is the observed debit the live capture
        /// now records, so a repaired row reads exactly as a freshly captured one. Rows whose
        /// proof is gone (the store prunes events at or before the latest committed flight's
        /// end) or ambiguous keep cost 0 here; <see cref="EstimateLegacyFacilityUpgradeCosts"/>
        /// prices them later from the facility cost table, once the facility objects exist.
        /// Mutates the rows in place, after load and before the first walk.
        /// </summary>
        internal static FacilityUpgradeCostRepairResult RepairZeroCostFacilityUpgradeActionsOnLoad(
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> ledgerActions)
        {
            var result = new FacilityUpgradeCostRepairResult();
            if (ledgerActions == null || ledgerActions.Count == 0)
                return result;

            for (int i = 0; i < ledgerActions.Count; i++)
            {
                var action = ledgerActions[i];
                if (action == null
                    || action.Type != GameActionType.FacilityUpgrade
                    || action.FacilityDowngrade
                    || action.FacilityCost != 0f)
                    continue;

                result.ZeroCostRows++;
                float cost;
                string refusal;
                if (TryResolveLegacyFacilityUpgradeCost(action, events, ledgerActions, out cost, out refusal))
                {
                    action.FacilityCost = cost;
                    result.Repaired++;
                    continue;
                }

                switch (refusal)
                {
                    case LegacyUpgradeRefusalExplicitZero: result.ExplicitZero++; break;
                    case LegacyUpgradeRefusalDowngradeInWindow: result.DowngradeInWindow++; break;
                    case LegacyUpgradeRefusalAmbiguousRows: result.AmbiguousRows++; break;
                    case LegacyUpgradeRefusalAmbiguousEvents: result.AmbiguousEvents++; break;
                    case LegacyUpgradeRefusalNonDebit: result.NonDebit++; break;
                    default: result.NoDebitEvent++; break;
                }
            }

            return result;
        }

        /// <summary>
        /// Pure: the stock debit of one cost-0 FacilityUpgrade row, read off the saved events
        /// within <see cref="LedgerOrchestrator.KscReconcileEpsilonSeconds"/> of the row's UT
        /// and carrying the row's recording tag (the resource store coalesces same-key events
        /// only inside that window and tag, so the pairing is one-to-one or refused). Refused
        /// when the upgrade event itself carries a <c>cost=</c> (a captured 0: funds were not
        /// charged), when a downgrade - the other StructureConstruction debit - shares the
        /// window, when another upgrade row shares it (one coalesced debit would cover both),
        /// when there is not exactly one StructureConstruction funds event, or when that event
        /// is not a debit.
        /// </summary>
        internal static bool TryResolveLegacyFacilityUpgradeCost(
            GameAction row,
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> ledgerActions,
            out float cost,
            out string refusal)
        {
            cost = 0f;
            refusal = LegacyUpgradeRefusalNoDebitEvent;
            if (row == null || events == null)
                return false;

            double window = LedgerOrchestrator.KscReconcileEpsilonSeconds;
            string rowTag = row.RecordingId ?? "";

            int debitEvents = 0;
            double debitDelta = 0.0;
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (Math.Abs(e.ut - row.UT) > window)
                    continue;
                if (!string.Equals(e.recordingId ?? "", rowTag, StringComparison.Ordinal))
                    continue;

                if (e.eventType == GameStateEventType.FacilityUpgraded
                    && string.Equals(e.key, row.FacilityId, StringComparison.Ordinal)
                    && GameStateEventDisplay.ExtractDetailField(e.detail, "cost") != null)
                {
                    refusal = LegacyUpgradeRefusalExplicitZero;
                    return false;
                }
                if (e.eventType == GameStateEventType.FacilityDowngraded)
                {
                    refusal = LegacyUpgradeRefusalDowngradeInWindow;
                    return false;
                }
                if (e.eventType == GameStateEventType.FundsChanged
                    && string.Equals(e.key, "StructureConstruction", StringComparison.Ordinal))
                {
                    debitEvents++;
                    debitDelta = e.valueAfter - e.valueBefore;
                }
            }

            if (ledgerActions != null)
            {
                for (int i = 0; i < ledgerActions.Count; i++)
                {
                    var other = ledgerActions[i];
                    if (other == null || ReferenceEquals(other, row)
                        || other.Type != GameActionType.FacilityUpgrade)
                        continue;
                    if (Math.Abs(other.UT - row.UT) > window)
                        continue;
                    if (!string.Equals(other.RecordingId ?? "", rowTag, StringComparison.Ordinal))
                        continue;
                    refusal = LegacyUpgradeRefusalAmbiguousRows;
                    return false;
                }
            }

            if (debitEvents == 0)
            {
                refusal = LegacyUpgradeRefusalNoDebitEvent;
                return false;
            }
            if (debitEvents > 1)
            {
                refusal = LegacyUpgradeRefusalAmbiguousEvents;
                return false;
            }
            if (!(debitDelta < 0.0))
            {
                refusal = LegacyUpgradeRefusalNonDebit;
                return false;
            }

            cost = (float)(-debitDelta);
            refusal = null;
            return true;
        }

        // ================================================================
        // Cost-table estimate of cost-0 FacilityUpgrade rows (owner ruling 2026-10-07)
        // ================================================================

        // Refusal tokens of TryEstimateLegacyFacilityUpgradeCost.
        internal const string LegacyEstimateRefusalUnknownFacility = "unknown-facility";
        internal const string LegacyEstimateRefusalLevelMismatch = "level-mismatch";
        internal const string LegacyEstimateRefusalZeroCost = "zero-cost";

        // Deferral reasons of EstimateLegacyFacilityUpgradeCosts (the pass retries later).
        internal const string LegacyEstimateDeferNoGame = "no-current-game";
        internal const string LegacyEstimateDeferNoFundsSeed = "no-funds-seed";
        internal const string LegacyEstimateDeferNoMultiplier = "multiplier-unreadable";
        internal const string LegacyEstimateDeferNoFacilityData = "facility-data-unreadable";

        private const int LegacyEstimateSampleCap = 10;

        /// <summary>
        /// When the career's FundsInitial seed was taken, as far as the ledger can tell. The
        /// seed row records no capture moment (its UT is always 0), so the only seed with a
        /// known moment is one equal to the career-start baseline (UT within a second of 0),
        /// which <c>LedgerOrchestrator.DecideInitialFundsSeed</c> always prefers when it is
        /// non-zero: that seed is the pool before any upgrade Parsek recorded.
        /// </summary>
        internal enum FundsSeedCaptureMoment
        {
            NoSeed,
            CareerStart,
            Unknown
        }

        /// <summary>
        /// Pure: classifies the funds seed for the cost-table estimate. A seed read off the
        /// live pool (a mid-career install, or a seed deferred past an upgrade) may already
        /// hold an upgrade's debit, and nothing persisted says when it was read, so it is
        /// <see cref="FundsSeedCaptureMoment.Unknown"/>; a zero career start never seeds from
        /// the baseline and is Unknown too.
        /// </summary>
        internal static FundsSeedCaptureMoment ClassifyFundsSeedCaptureMoment(
            bool hasSeed, float seedFunds, bool hasInitialBaseline, double initialBaselineFunds)
        {
            if (!hasSeed)
                return FundsSeedCaptureMoment.NoSeed;
            if (hasInitialBaseline
                && initialBaselineFunds != 0.0
                && (float)initialBaselineFunds == seedFunds)
                return FundsSeedCaptureMoment.CareerStart;
            return FundsSeedCaptureMoment.Unknown;
        }

        /// <summary>
        /// What the cost-table estimate reads from the running game. The facility level
        /// costs exist only where the UpgradeableFacility objects do (the Space Center, not a
        /// cold load or the Tracking Station), so <see cref="FacilityDataLoaded"/> gates the
        /// pass; <see cref="LevelCosts"/> maps a facility id to its per-level
        /// <c>levelCost</c> values (index = stock level) or null for an id the game does not
        /// know.
        /// </summary>
        internal struct FacilityCostTableProbe
        {
            public bool GameAvailable;
            public bool IsCareer;
            public bool MultiplierReadable;
            public float FundsLossMultiplier;
            public bool FacilityDataLoaded;
            public Func<string, float[]> LevelCosts;
        }

        /// <summary>Tally of one <see cref="EstimateLegacyFacilityUpgradeCosts"/> pass.</summary>
        internal struct LegacyUpgradeEstimateResult
        {
            public bool Complete;
            public string DeferReason;
            public int Candidates;
            public int Estimated;
            public int Repaired;
            public int EvidenceZero;
            public int BeforeSeed;
            public int UnknownFacility;
            public int LevelMismatch;
            public int ZeroCost;
            public int SeedCaptureUnknown;
            public int NotCareer;
            public float FundsLossMultiplier;
            public List<string> EstimatedSample;

            public string Format()
            {
                var ic = CultureInfo.InvariantCulture;
                string text = "candidates=" + Candidates.ToString(ic)
                    + " estimated=" + Estimated.ToString(ic)
                    + " repaired=" + Repaired.ToString(ic)
                    + " evidenceZero=" + EvidenceZero.ToString(ic)
                    + " beforeSeed=" + BeforeSeed.ToString(ic)
                    + " unknownFacility=" + UnknownFacility.ToString(ic)
                    + " levelMismatch=" + LevelMismatch.ToString(ic)
                    + " zeroCost=" + ZeroCost.ToString(ic)
                    + " seedCaptureUnknown=" + SeedCaptureUnknown.ToString(ic)
                    + " notCareer=" + NotCareer.ToString(ic)
                    + " fundsLossMultiplier=" + FundsLossMultiplier.ToString("R", ic);
                if (EstimatedSample != null && EstimatedSample.Count > 0)
                    text += " rows=[" + KspStatePatcher.ComposeBoundedIdentitySample(
                        EstimatedSample, LegacyEstimateSampleCap) + "]";
                return text;
            }
        }

        /// <summary>
        /// Pure: the stock price of one cost-0 FacilityUpgrade row from the facility cost
        /// table, the way <c>UpgradeableFacility.GetUpgradeCost()</c> prices it (decompiled
        /// KSP 1.12.5: <c>upgradeLevels[level + 1].levelCost * Career.FundsLossMultiplier</c>):
        /// reaching the row's tier costs the levelCost at the tier's 0-based index, times
        /// today's multiplier. The ledger tier is derived from stock's 0 / 0.5 / 1 normalized
        /// level, so only a three-level facility maps back; anything else is refused, as is
        /// an unknown facility and a price of 0 (then the cost-0 row is already right).
        /// </summary>
        internal static bool TryEstimateLegacyFacilityUpgradeCost(
            GameAction row, float[] levelCosts, float fundsLossMultiplier,
            out float cost, out string refusal)
        {
            cost = 0f;
            if (levelCosts == null)
            {
                refusal = LegacyEstimateRefusalUnknownFacility;
                return false;
            }
            int levelIndex = row != null ? row.ToLevel - 1 : -1;
            if (levelCosts.Length != 3 || levelIndex < 1 || levelIndex >= levelCosts.Length)
            {
                refusal = LegacyEstimateRefusalLevelMismatch;
                return false;
            }

            double price = (double)levelCosts[levelIndex] * fundsLossMultiplier;
            if (double.IsNaN(price) || double.IsInfinity(price) || !(price > 0.0))
            {
                refusal = LegacyEstimateRefusalZeroCost;
                return false;
            }

            cost = (float)price;
            refusal = null;
            return true;
        }

        /// <summary>
        /// Gives every career FacilityUpgrade row still at cost 0 (written before the upgrade
        /// cost capture, its saved debit since pruned, so <see cref="RepairZeroCostFacilityUpgradeActionsOnLoad"/>
        /// could not prove it) the facility cost table's price of its upgrade. Owner ruling
        /// 2026-10-07: an estimate beats leaving the ledger high by every old upgrade, even
        /// though today's multiplier and the absence of a strategy discount can differ from
        /// what was charged.
        ///
        /// <para>Order of refusals: Science / Sandbox rows are never priced (no funds were
        /// charged there). The pass defers, changing nothing, while there is no game, no funds
        /// seed, no readable multiplier or no loaded facility table; the caller retries on a
        /// later recalc. A seed whose capture moment is unknown skips every row: it may
        /// already hold the debits, and pricing them again would charge twice. Per row, saved
        /// events still win (a unique debit is applied as in the load repair; an explicit
        /// <c>cost=</c> or a non-debit keeps 0), and a row at or before the seed's moment is
        /// skipped.</para>
        ///
        /// <para>Idempotent: a priced row is no longer cost 0. Mutates the rows in place only
        /// on a complete pass.</para>
        /// </summary>
        internal static LegacyUpgradeEstimateResult EstimateLegacyFacilityUpgradeCosts(
            IReadOnlyList<GameAction> ledgerActions,
            IReadOnlyList<GameStateEvent> events,
            FacilityCostTableProbe probe,
            FundsSeedCaptureMoment seedMoment,
            double seedCaptureUT)
        {
            var result = new LegacyUpgradeEstimateResult { FundsLossMultiplier = probe.FundsLossMultiplier };
            var candidates = new List<GameAction>();
            if (ledgerActions != null)
            {
                for (int i = 0; i < ledgerActions.Count; i++)
                {
                    var action = ledgerActions[i];
                    // A downgrade row is never a legacy upgrade: it exists only from the
                    // build that captures its debit, and stock prices it differently.
                    if (action != null
                        && action.Type == GameActionType.FacilityUpgrade
                        && !action.FacilityDowngrade
                        && action.FacilityCost == 0f)
                        candidates.Add(action);
                }
            }

            result.Candidates = candidates.Count;
            if (candidates.Count == 0)
            {
                result.Complete = true;
                return result;
            }
            if (!probe.GameAvailable)
                return Deferred(result, LegacyEstimateDeferNoGame);
            if (!probe.IsCareer)
            {
                result.NotCareer = candidates.Count;
                result.Complete = true;
                return result;
            }
            if (seedMoment == FundsSeedCaptureMoment.NoSeed)
                return Deferred(result, LegacyEstimateDeferNoFundsSeed);
            if (seedMoment != FundsSeedCaptureMoment.CareerStart)
            {
                result.SeedCaptureUnknown = candidates.Count;
                result.Complete = true;
                return result;
            }
            if (!probe.MultiplierReadable)
                return Deferred(result, LegacyEstimateDeferNoMultiplier);
            if (!probe.FacilityDataLoaded || probe.LevelCosts == null)
                return Deferred(result, LegacyEstimateDeferNoFacilityData);

            var ic = CultureInfo.InvariantCulture;
            for (int i = 0; i < candidates.Count; i++)
            {
                var row = candidates[i];
                float cost;
                string refusal;
                if (TryResolveLegacyFacilityUpgradeCost(row, events, ledgerActions, out cost, out refusal))
                {
                    row.FacilityCost = cost;
                    result.Repaired++;
                    continue;
                }
                if (refusal == LegacyUpgradeRefusalExplicitZero || refusal == LegacyUpgradeRefusalNonDebit)
                {
                    result.EvidenceZero++;
                    continue;
                }
                if (!(row.UT > seedCaptureUT))
                {
                    result.BeforeSeed++;
                    continue;
                }

                if (!TryEstimateLegacyFacilityUpgradeCost(
                        row, probe.LevelCosts(row.FacilityId), probe.FundsLossMultiplier,
                        out cost, out refusal))
                {
                    switch (refusal)
                    {
                        case LegacyEstimateRefusalUnknownFacility: result.UnknownFacility++; break;
                        case LegacyEstimateRefusalLevelMismatch: result.LevelMismatch++; break;
                        default: result.ZeroCost++; break;
                    }
                    continue;
                }

                row.FacilityCost = cost;
                result.Estimated++;
                if (result.EstimatedSample == null)
                    result.EstimatedSample = new List<string>();
                result.EstimatedSample.Add(
                    (row.ActionId ?? "(no-id)") + " " + (row.FacilityId ?? "(null)")
                    + "->Lv" + row.ToLevel.ToString(ic)
                    + " estimated=" + cost.ToString("R", ic));
            }

            result.Complete = true;
            return result;
        }

        private static LegacyUpgradeEstimateResult Deferred(LegacyUpgradeEstimateResult result, string reason)
        {
            result.Complete = false;
            result.DeferReason = reason;
            return result;
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
        internal static int TryRecoverBrokenLedgerOnLoad(double? maxUT = null)
        {
            LedgerOrchestrator.Initialize();
            GameStateStore.RepairLegacyPartPurchaseEventsForCurrentSemantics();

            int recoveredFundsParts = 0;
            int recoveredContracts = 0;
            int recoveredScience = 0;
            int skippedHidden = 0;
            int skippedFuture = 0;

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
                if (maxUT.HasValue && evt.ut > maxUT.Value)
                {
                    skippedFuture++;
                    continue;
                }
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
                    $"(skippedHidden={skippedHidden}, skippedFuture={skippedFuture}).");
            }
            else
            {
                ParsekLog.Verbose(LedgerOrchestrator.Tag,
                    $"TryRecoverBrokenLedgerOnLoad: no recovery needed " +
                    $"(skippedHidden={skippedHidden}, skippedFuture={skippedFuture})");
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
                        //
                        // NULL MATCHES NULL HERE, DELIBERATELY LEFT ALONE. A keyless
                        // event would match any DedupKey-less spending row inside the
                        // 0.1 s window - a VesselBuild rollout, or the query family's
                        // untagged StrategyConverter debit - and suppress the synthesis.
                        // That is the SAFE side for a migration whose output is a money
                        // movement: skipping a recovery leaves the ledger short by a
                        // charge the guard then shows, while synthesizing against a
                        // keyless event risks charging twice. Both directions are
                        // theoretical (ConvertPartPurchased always stores a name), and
                        // the conservative one is the one already here.
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
