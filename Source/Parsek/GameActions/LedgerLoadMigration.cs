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
            double minUT = 0;
            double maxUT = double.MaxValue;
            var actions = GameStateEventConverter.ConvertEvents(events, null, minUT, maxUT);

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
