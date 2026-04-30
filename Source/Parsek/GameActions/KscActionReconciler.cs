using System;
using System.Collections.Generic;
using KscExpectationLeg = Parsek.KscActionExpectationClassifier.KscExpectationLeg;
using KscExpectationLegMode = Parsek.KscActionExpectationClassifier.KscExpectationLegMode;
using KscReconcileClass = Parsek.KscActionExpectationClassifier.KscReconcileClass;

namespace Parsek
{
    internal static class KscActionReconciler
    {
        // Preserve the original LedgerOrchestrator subsystem tag so log lines stay byte-stable.
        private const string Tag = "LedgerOrchestrator";

        private struct KscExpectedLegMatch
        {
            public GameAction Action;
            public KscExpectationLeg Leg;
        }

        /// <summary>
        /// Per-action reconciliation for KSC-side ledger writes. Called from
        /// <see cref="LedgerOrchestrator.OnKscSpending"/>. Compares the action's
        /// expected paired resource event — matched by <see cref="GameStateEventType"/>
        /// + KSP <c>TransactionReasons</c> key within <see cref="KscReconcileEpsilonSeconds"/>
        /// — against the delta the action's raw field represents, and logs WARN on
        /// missing or mismatched event. Log-only: the action has already been written
        /// to the ledger by the caller.
        /// <para>
        /// Scope: <b>untransformed action types only</b> (part purchase, tech unlock,
        /// facility upgrade/repair, kerbal hire, contract advance). Transformed types
        /// (contract rewards, milestones, reputation earnings) skip with a rate-limited
        /// VERBOSE line — their raw fields are subject to strategy diversion and the
        /// reputation curve, so a post-walk hook (Phase D territory) is required for
        /// those. See <see cref="KscActionExpectationClassifier.ClassifyAction"/> for
        /// the full type map.
        /// </para>
        /// <para>
        /// Coalescing safety: <see cref="GameStateStore.AddEvent"/> merges
        /// same-type/same-tag resource events within a 0.1 s window into a single slot.
        /// Two back-to-back KSC actions of the same kind (e.g. two part purchases) share
        /// one coalesced event with the summed delta. This reconciler handles that by
        /// summing both sides across a UT window: expected from ledger actions with the
        /// same expectation classification within <see cref="KscReconcileEpsilonSeconds"/>
        /// of <paramref name="ut"/>, observed from events with the matching key in the
        /// same window. The current action is already in <paramref name="ledgerActions"/>
        /// (the caller adds it before calling).
        /// </para>
        /// <para>
        /// Internal static + parameterized on <paramref name="events"/> and
        /// <paramref name="ledgerActions"/> for testability — production calls pass
        /// the live game-state event list and ledger action list.
        /// </para>
        /// </summary>
        internal static void ReconcileKscAction(
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> ledgerActions,
            GameAction action,
            double ut)
        {
            if (action == null) return;

            var expectation = KscActionExpectationClassifier.ClassifyAction(action);

            switch (expectation.Class)
            {
                case KscReconcileClass.NoResourceImpact:
                    return;

                case KscReconcileClass.Transformed:
                    // Rate-limited VERBOSE so a long session with many contract completions
                    // doesn't drown the log, but one line always lands on the first call
                    // per (action type, skip reason) pair so the skip is observable during
                    // debugging. Multiple skip causes share this branch — Phase E1.5
                    // strategy spending, and #448's RnDPartPurchase suppression under
                    // BypassEntryPurchaseAfterResearch=true. Both can fire for the SAME
                    // action.Type (FundsSpending), so a key keyed only on action.Type
                    // would let whichever cause hits first suppress the other for the
                    // entire rate-limit window. Include a hash of the SkipReason so each
                    // distinct cause gets its own slot.
                    int skipKeyHash = expectation.SkipReason != null
                        ? expectation.SkipReason.GetHashCode()
                        : 0;
                    ParsekLog.VerboseRateLimited(Tag,
                        $"ksc-reconcile-skip:{action.Type}:{skipKeyHash}",
                        $"KSC reconciliation: {action.Type} skipped " +
                        $"({expectation.SkipReason}) ut={ut:F1}");
                    return;

                case KscReconcileClass.Untransformed:
                    break;
            }

            if (expectation.LegCount == 0)
                return;

            ReconcileKscExpectationLeg(events, ledgerActions, action, ut, expectation.FundsLeg);
            ReconcileKscExpectationLeg(events, ledgerActions, action, ut, expectation.ScienceLeg);
            ReconcileKscExpectationLeg(events, ledgerActions, action, ut, expectation.ReputationLeg);
        }

        private static void ReconcileKscExpectationLeg(
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> ledgerActions,
            GameAction action,
            double ut,
            KscExpectationLeg leg)
        {
            if (!leg.IsPresent)
                return;

            double tol;
            switch (leg.EventType)
            {
                case GameStateEventType.FundsChanged: tol = 1.0; break;
                case GameStateEventType.ReputationChanged: tol = 0.1; break;
                case GameStateEventType.ScienceChanged: tol = 0.1; break;
                default:
                    ParsekLog.Warn(Tag,
                        $"KSC reconciliation: unexpected EventType {leg.EventType} " +
                        $"for action {action.Type} — classifier bug");
                    return;
            }

            if (Math.Abs(leg.ExpectedDelta) <= tol)
                return;

            double summedObserved = 0;
            int observedCount = 0;
            double earliestObservedUt = double.PositiveInfinity;
            double earliestObservedBefore = 0;
            if (events != null)
            {
                for (int i = 0; i < events.Count; i++)
                {
                    var e = events[i];
                    if (e.eventType != leg.EventType) continue;
                    if (Math.Abs(e.ut - ut) > KscReconcileEpsilonSeconds) continue;
                    string eventKey = e.key ?? "";
                    if (!string.Equals(eventKey, leg.ExpectedReasonKey, StringComparison.Ordinal))
                        continue;

                    if (observedCount == 0 || e.ut < earliestObservedUt)
                    {
                        earliestObservedUt = e.ut;
                        earliestObservedBefore = e.valueBefore;
                    }

                    summedObserved += (e.valueAfter - e.valueBefore);
                    observedCount++;
                }
            }

            string channelTag = ResourceChannelTag(leg.EventType);
            if (observedCount == 0)
            {
                ParsekLog.Warn(Tag,
                    $"KSC reconciliation ({channelTag}): action {action.Type} expected delta={leg.ExpectedDelta:F1} " +
                    $"(reason='{leg.ExpectedReasonKey}') but no matching {leg.EventType} event within " +
                    $"{KscReconcileEpsilonSeconds:F1}s of ut={ut:F1} — missing earning channel or stale event?");
                return;
            }

            var matchingLegs = CollectMatchingLegs(ledgerActions, action, ut, leg);
            int expectedCount = matchingLegs.Count;
            double summedExpected = ComputeExpectedDeltaForLeg(
                leg,
                matchingLegs,
                earliestObservedBefore,
                tol);

            if (Math.Abs(summedExpected - summedObserved) > tol)
            {
                ParsekLog.Warn(Tag,
                    $"KSC reconciliation ({channelTag}): action {action.Type} expected delta={leg.ExpectedDelta:F1}, " +
                    $"aggregate expected={summedExpected:F1} across {expectedCount} ledger action(s) vs " +
                    $"observed={summedObserved:F1} across {observedCount} event(s) keyed '{leg.ExpectedReasonKey}' " +
                    $"— delta mismatch at ut={ut:F1}");
            }
        }

        private static List<KscExpectedLegMatch> CollectMatchingLegs(
            IReadOnlyList<GameAction> ledgerActions,
            GameAction action,
            double ut,
            KscExpectationLeg targetLeg)
        {
            var matches = new List<KscExpectedLegMatch>();
            if (ledgerActions != null)
            {
                for (int i = 0; i < ledgerActions.Count; i++)
                {
                    var other = ledgerActions[i];
                    if (other == null) continue;
                    if (Math.Abs(other.UT - ut) > KscReconcileEpsilonSeconds) continue;
                    var otherExpectation = KscActionExpectationClassifier.ClassifyAction(other);
                    if (otherExpectation.Class != KscReconcileClass.Untransformed) continue;
                    AddMatchingLeg(matches, other, otherExpectation.FundsLeg, targetLeg);
                    AddMatchingLeg(matches, other, otherExpectation.ScienceLeg, targetLeg);
                    AddMatchingLeg(matches, other, otherExpectation.ReputationLeg, targetLeg);
                }
            }

            if (matches.Count == 0)
            {
                AddMatchingLeg(matches, action, targetLeg, targetLeg);
            }

            return matches;
        }

        private static void AddMatchingLeg(
            List<KscExpectedLegMatch> matches,
            GameAction action,
            KscExpectationLeg candidate,
            KscExpectationLeg target)
        {
            if (!candidate.IsPresent)
                return;
            if (candidate.EventType != target.EventType)
                return;
            if (candidate.Mode != target.Mode)
                return;
            if (!string.Equals(candidate.ExpectedReasonKey ?? "", target.ExpectedReasonKey ?? "", StringComparison.Ordinal))
                return;

            matches.Add(new KscExpectedLegMatch
            {
                Action = action,
                Leg = candidate
            });
        }

        private static double ComputeExpectedDeltaForLeg(
            KscExpectationLeg leg,
            List<KscExpectedLegMatch> matches,
            double startingObservedValue,
            double tol)
        {
            if (matches == null || matches.Count == 0)
                return leg.ExpectedDelta;

            if (leg.Mode != KscExpectationLegMode.ReputationCurve)
            {
                double sum = 0;
                for (int i = 0; i < matches.Count; i++)
                {
                    if (Math.Abs(matches[i].Leg.ExpectedDelta) <= tol)
                        continue;
                    sum += matches[i].Leg.ExpectedDelta;
                }
                return sum;
            }

            matches.Sort((a, b) =>
            {
                int utCompare = a.Action.UT.CompareTo(b.Action.UT);
                if (utCompare != 0)
                    return utCompare;
                return a.Action.Sequence.CompareTo(b.Action.Sequence);
            });

            float currentRep = (float)startingObservedValue;
            double curvedSum = 0;
            for (int i = 0; i < matches.Count; i++)
            {
                float nominal = (float)matches[i].Leg.ExpectedDelta;
                if (Math.Abs(nominal) <= tol)
                    continue;
                var result = ReputationModule.ApplyReputationCurve(nominal, currentRep);
                curvedSum += result.actualDelta;
                currentRep = result.newRep;
            }
            return curvedSum;
        }

        /// <summary>Pure: short channel tag used in the reconciliation log lines.</summary>
        internal static string ResourceChannelTag(GameStateEventType t)
        {
            switch (t)
            {
                case GameStateEventType.FundsChanged: return "funds";
                case GameStateEventType.ReputationChanged: return "rep";
                case GameStateEventType.ScienceChanged: return "sci";
                default: return t.ToString();
            }
        }

        /// <summary>
        /// UT window (seconds) used by <see cref="ReconcileKscAction"/> to pair a KSC
        /// action with its resource-changed event and to aggregate both sides across
        /// coalesced same-key entries. Must match <c>GameStateStore.ResourceCoalesceEpsilon</c>
        /// (private, 0.1 s — see <c>GameStateStore.cs:21</c> and <c>AddEvent</c> at line 42).
        /// <para>
        /// Rationale: within this window, <see cref="GameStateStore.AddEvent"/> has
        /// already merged same-type/same-tag resource deltas into a single slot, so
        /// summing ledger actions and events across the window is safe by construction
        /// — the summed observed delta is one coalesced entry that equals the sum of
        /// the individual raw deltas. Beyond this window same-key events stay separate
        /// in the store, so aggregating them would cross-attribute deltas and opposing
        /// per-action errors could cancel out (review round 3, PR #340).
        /// </para>
        /// <para>
        /// If <c>GameStateStore.ResourceCoalesceEpsilon</c> ever changes, update this
        /// value in lockstep. The two constants encode the same physical invariant.
        /// </para>
        /// </summary>
        internal const double KscReconcileEpsilonSeconds = 0.1;
    }
}
