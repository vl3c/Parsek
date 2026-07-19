using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// Pure, Unity-free helpers for the route rewind-visibility extension
    /// (dormant routes; plan
    /// <c>docs/dev/plans/plan-route-rewind-dormant-visibility.md</c>).
    ///
    /// <para><b>The model.</b> RouteStore is preserved in memory across every
    /// in-session load (LoadRoutesFrom runs only on the cold-start branch), so
    /// a Rewind-to-Separation must move-and-replace the route lists itself:
    /// routes created AFTER the rewind cutoff become DORMANT (invisible,
    /// non-firing, preserved definition) and re-materialize when the re-flown
    /// timeline passes their <see cref="Route.CreatedUT"/> again; routes
    /// created BEFORE the cutoff stay committed but get their forward-looking
    /// cycle state reconciled so the loop clock does not silently swallow
    /// re-flown cycles against abandoned-future cursors.</para>
    ///
    /// <para>All methods are pure with respect to statics (they mutate only the
    /// Route instances handed in) so xUnit drives them directly. The live
    /// orchestration lives in <c>ReconciliationBundle.Restore</c> and
    /// <c>RouteStore.MaterializeDueDormantRoutes</c>.</para>
    /// </summary>
    internal static class RouteRewindClassifier
    {
        private const string Tag = "Route";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// Splits the captured route lists at a rewind cutoff. Captured
        /// committed routes with <see cref="Route.CreatedUT"/> strictly after
        /// <paramref name="cutoffUT"/> move to the dormant list; routes created
        /// at/before the cutoff, or with an UNKNOWN creation stamp
        /// (<c>CreatedUT &lt; 0</c>, legacy), stay committed. Captured dormant
        /// entries are carried forward (they are still future) and deduped by
        /// id against the committed result and among themselves (first wins).
        /// Null entries and null lists are tolerated.
        /// </summary>
        internal static void Classify(
            IReadOnlyList<Route> capturedCommitted,
            IReadOnlyList<Route> capturedDormant,
            double cutoffUT,
            out List<Route> committed,
            out List<Route> dormant)
        {
            committed = new List<Route>();
            dormant = new List<Route>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            if (capturedCommitted != null)
            {
                for (int i = 0; i < capturedCommitted.Count; i++)
                {
                    Route r = capturedCommitted[i];
                    if (r == null || string.IsNullOrEmpty(r.Id) || !seen.Add(r.Id))
                        continue;
                    if (r.CreatedUT >= 0.0 && r.CreatedUT > cutoffUT)
                        dormant.Add(r);
                    else
                        committed.Add(r);
                }
            }

            if (capturedDormant != null)
            {
                for (int i = 0; i < capturedDormant.Count; i++)
                {
                    Route r = capturedDormant[i];
                    if (r == null || string.IsNullOrEmpty(r.Id) || !seen.Add(r.Id))
                        continue;
                    dormant.Add(r);
                }
            }
        }

        /// <summary>
        /// Reconciles a KEPT (pre-cutoff-created) committed route's
        /// forward-looking cycle state after a rewind, fixing the stale-cursor
        /// bug (plan section 1): without this, the loop-clock crossing detector
        /// compares against abandoned-future observations and silently swallows
        /// re-flown cycles whose Rec-1-retired ledger rows then never re-emit.
        ///
        /// <para>Resets the loop cursors unconditionally (mirrors the
        /// <c>TryActivate</c> reset discipline: -1 means the first post-rewind
        /// crossing fires; the ELS dedup over the KEPT rows is the double-fire
        /// backstop). Clears in-flight cycle state whose UTs lie beyond the
        /// cutoff (an InTransit cycle started after the cutoff returns to
        /// Active), holds / partial reports stamped after the cutoff, and a
        /// pending recovery credit whose dispatch happened after the cutoff.
        /// <see cref="Route.CompletedCycles"/> / <see cref="Route.SkippedCycles"/>
        /// are deliberately NOT recomputed: kept rows use cycle ids below the
        /// counter, so continuing it can never collide with a kept cycleId
        /// (gap ids are harmless); the inflation is a documented cosmetic
        /// residual.</para>
        /// </summary>
        /// <returns>True when anything changed (for batch counting).</returns>
        internal static bool ResetCycleStateForRewind(Route route, double cutoffUT)
        {
            if (route == null)
                return false;

            bool changed = false;

            if (route.LastObservedLoopCycleIndex != -1)
            {
                route.LastObservedLoopCycleIndex = -1;
                changed = true;
            }
            if (route.WindowAnchorCycleIndex != -1)
            {
                route.WindowAnchorCycleIndex = -1;
                changed = true;
            }
            changed |= RouteOrchestrator.ResetStopFireStateReturningChanged(route);

            // In-flight cycle state referencing the abandoned future.
            if (route.CurrentCycleStartUT.HasValue && route.CurrentCycleStartUT.Value > cutoffUT)
            {
                route.CurrentCycleStartUT = null;
                route.PendingDeliveryUT = null;
                route.PendingStopIndex = -1;
                route.CurrentSegmentIndex = -1;
                if (route.Status == RouteStatus.InTransit)
                    route.TransitionTo(RouteStatus.Active, "rewind-cycle-reconcile");
                changed = true;
            }
            if (route.PendingDeliveryUT.HasValue && route.PendingDeliveryUT.Value > cutoffUT)
            {
                route.PendingDeliveryUT = null;
                route.PendingStopIndex = -1;
                changed = true;
            }
            if (route.NextEligibilityCheckUT.HasValue && route.NextEligibilityCheckUT.Value > cutoffUT)
            {
                route.NextEligibilityCheckUT = null;
                changed = true;
            }

            // Holds / partial reports stamped in the abandoned future.
            if (route.LastHoldUT > cutoffUT)
            {
                route.ClearHold("rewind-cycle-reconcile");
                changed = true;
            }
            if (route.LastPartialDeliveryUT > cutoffUT)
            {
                route.LastPartialDeliverySummary = null;
                route.LastPartialDeliveryUT = -1.0;
                route.LastPartialDeliveryCycleId = null;
                changed = true;
            }

            // A recovery credit owed by an abandoned-future dispatch: its
            // dispatch row was Rec-1-retired, so flushing it would credit a
            // cycle the surviving timeline never charged.
            if (!string.IsNullOrEmpty(route.PendingRecoveryCreditCycleId)
                && route.PendingRecoveryCreditDispatchUT > cutoffUT)
            {
                route.PendingRecoveryCreditCycleId = null;
                route.PendingRecoveryCreditDispatchUT = -1.0;
                changed = true;
            }

            if (changed)
            {
                ParsekLog.Verbose(Tag,
                    $"RewindReconcile: route {RouteIds.Short(route.Id)} cycle state reset " +
                    $"at cutoff={cutoffUT.ToString("R", IC)} status={route.Status}");
            }
            return changed;
        }

        /// <summary>
        /// True when a dormant route's creation point has been reached by the
        /// (re-flown) timeline: <c>currentUT &gt;= CreatedUT</c>. A route with
        /// an unknown stamp (<c>&lt; 0</c>) can never be dormant by
        /// <see cref="Classify"/>, but is treated as due defensively so it can
        /// never be stranded invisible.
        /// </summary>
        internal static bool IsDormantRouteDue(Route route, double currentUT)
        {
            if (route == null)
                return false;
            return route.CreatedUT < 0.0 || currentUT >= route.CreatedUT;
        }

        /// <summary>
        /// The route's source-tree id set: every non-empty
        /// <c>SourceRefs[].TreeId</c> plus <see cref="Route.BackingMissionTreeId"/>.
        /// Null/empty ids are never added, so two tree-less routes can never
        /// "share" a vacuous tree.
        /// </summary>
        internal static HashSet<string> TreeIdSet(Route route)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (route == null)
                return set;
            if (route.SourceRefs != null)
            {
                for (int i = 0; i < route.SourceRefs.Count; i++)
                {
                    var sref = route.SourceRefs[i];
                    if (sref != null && !string.IsNullOrEmpty(sref.TreeId))
                        set.Add(sref.TreeId);
                }
            }
            if (!string.IsNullOrEmpty(route.BackingMissionTreeId))
                set.Add(route.BackingMissionTreeId);
            return set;
        }

        /// <summary>
        /// True when a committed route occupies any of the dormant route's
        /// source trees (set intersection over <see cref="TreeIdSet"/>). Used
        /// as the materialize drop guard: the player re-created a route over
        /// that tree during the re-fly, and live intent wins (also preserves
        /// the one-route-binds-one-tree invariant behind
        /// <c>RouteTreeGuard</c>).
        /// </summary>
        internal static bool IsTreeOccupied(Route dormant, IReadOnlyList<Route> committedRoutes)
        {
            if (dormant == null || committedRoutes == null || committedRoutes.Count == 0)
                return false;
            HashSet<string> mine = TreeIdSet(dormant);
            if (mine.Count == 0)
                return false;
            for (int i = 0; i < committedRoutes.Count; i++)
            {
                Route other = committedRoutes[i];
                if (other == null)
                    continue;
                foreach (string treeId in TreeIdSet(other))
                {
                    if (mine.Contains(treeId))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Resets a dormant route to the fresh-creation runtime state for
        /// materialization, PRESERVING the definition (Id, Name, sources,
        /// stops, manifests, cadence, priority, <see cref="Route.CreatedUT"/>,
        /// origin flags, backing-mission fields) and the
        /// <see cref="Route.LinkedRouteId"/> former-partner hint (the caller
        /// resolves re-link vs sever). Mutates and returns the same instance.
        ///
        /// <para>Rationale: the abandoned future's counters and anchors
        /// describe cycles the surviving timeline never ran (their ledger rows
        /// were Rec-1-retired); a stale cursor against a rebased loop clock
        /// silently swallows or mass-fires cycles. Materializing
        /// <see cref="RouteStatus.Paused"/> matches routes-are-created-Paused
        /// and avoids surprise dispatches into a diverged world; the player's
        /// re-activation is a recorded RouteResumed timeline event.</para>
        /// </summary>
        internal static Route ResetToFreshForMaterialize(Route route)
        {
            if (route == null)
                return null;

            route.TransitionTo(RouteStatus.Paused, "dormant-materialize");
            route.PreMissingStatus = RouteStatus.Active;
            route.CompletedCycles = 0;
            route.SkippedCycles = 0;
            route.LastObservedLoopCycleIndex = -1;
            route.WindowAnchorCycleIndex = -1;
            route.ReaimWindowBasisEngaged = false;
            route.LoopAnchorUT = -1.0;
            route.CurrentCycleStartUT = null;
            route.CurrentSegmentIndex = -1;
            route.PendingDeliveryUT = null;
            route.PendingStopIndex = -1;
            route.NextEligibilityCheckUT = null;
            route.PauseAfterCurrentCycle = false;
            route.SendOnceArmed = false;
            route.LastConsumedPartnerCycle = 0;
            route.PendingRecoveryCreditCycleId = null;
            route.PendingRecoveryCreditDispatchUT = -1.0;
            route.ClearHold("dormant-materialize");
            route.LastPartialDeliverySummary = null;
            route.LastPartialDeliveryUT = -1.0;
            route.LastPartialDeliveryCycleId = null;
            RouteOrchestrator.ResetStopFireStateReturningChanged(route);
            return route;
        }
    }
}
