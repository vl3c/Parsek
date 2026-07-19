using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// Timeline-derived pause state for a kept route at a rewind cutoff,
    /// computed by <see cref="RouteRewindClassifier.DeriveTimelineStatus"/>
    /// from the KEPT (post-Rec-1-retire) <see cref="GameActionType.RoutePaused"/>
    /// / <see cref="GameActionType.RouteResumed"/> ledger rows.
    /// </summary>
    internal enum RouteTimelineStatus
    {
        /// <summary>No kept pause/resume marker for the route: it predates the
        /// route-timeline-events feature or was never paused. Leave the live
        /// status alone.</summary>
        NoMarker = 0,

        /// <summary>The latest kept marker at/before the cutoff is
        /// <see cref="GameActionType.RoutePaused"/>.</summary>
        Paused = 1,

        /// <summary>The latest kept marker at/before the cutoff is
        /// <see cref="GameActionType.RouteResumed"/>.</summary>
        Active = 2,
    }

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
        /// are NOT touched here: the rewind seam reconstructs them from the
        /// kept ledger rows via <see cref="ReconstructCycleCounters"/> right
        /// after this reconcile.</para>
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
            // Legacy (non-loop) dispatch path: an abandoned-future NextDispatchUT
            // would Skip("not-due-yet") every re-flown cycle until the abandoned
            // UT - the same stall the cursor reset fixes for loop routes. Pull it
            // back to the cutoff (due promptly); v0 loop routes never read it.
            if (route.NextDispatchUT > cutoffUT)
            {
                route.NextDispatchUT = cutoffUT;
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
        /// Reason prefix stamped on AUTO-emitted <see cref="GameActionType.RoutePaused"/>
        /// rows (source-validity flips in <c>RouteStore.RevalidateSources</c>;
        /// the emitters live in the dormant-UI slice - the PREFIX string, not
        /// their code, is the stable contract). Auto rows describe source
        /// VALIDITY, not player intent, and are ignored by
        /// <see cref="DeriveTimelineStatus"/>.
        /// </summary>
        private const string AutoPauseReasonPrefix = "AutoPause:";

        /// <summary>Auto-resume counterpart of <see cref="AutoPauseReasonPrefix"/>
        /// (e.g. <c>AutoResume:SourcesRestored</c> / <c>AutoResume:CatchUp</c>).</summary>
        private const string AutoResumeReasonPrefix = "AutoResume:";

        /// <summary>
        /// True for an AUTO lifecycle marker (reason starts with
        /// <see cref="AutoPauseReasonPrefix"/> / <see cref="AutoResumeReasonPrefix"/>,
        /// ordinal). A null/empty reason is treated as PLAYER intent for
        /// backward compatibility with legacy rows.
        /// </summary>
        private static bool IsAutoLifecycleRow(GameAction a)
        {
            string reason = a.RouteEndpointReason;
            if (string.IsNullOrEmpty(reason))
                return false;
            return reason.StartsWith(AutoPauseReasonPrefix, StringComparison.Ordinal)
                || reason.StartsWith(AutoResumeReasonPrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Derives a KEPT route's timeline-correct pause state at a rewind
        /// cutoff from the KEPT ledger rows (route-timeline events): the LATEST
        /// PLAYER-DRIVEN <see cref="GameActionType.RoutePaused"/> /
        /// <see cref="GameActionType.RouteResumed"/> row for
        /// <paramref name="routeId"/> wins.
        ///
        /// <para><b>Auto rows are skipped entirely:</b> rows whose reason
        /// starts with <c>AutoPause:</c> / <c>AutoResume:</c> (emitted by the
        /// source-validity flips in <c>RevalidateSources</c>) describe source
        /// VALIDITY, which re-derives from ERS via RevalidateSources after
        /// every rewind - replaying them as player intent would pause a route
        /// the player never paused (and poison the
        /// <see cref="Route.PreMissingStatus"/> recovery baseline). Only
        /// player-driven rows (<c>player-pause</c>, <c>player-activate</c>,
        /// <c>delivered-then-paused</c>, <c>delivered-partial-then-paused</c>,
        /// <c>delivered-replay-then-paused</c>, or a legacy null/empty reason)
        /// carry intent, so the latest PLAYER row wins regardless of newer
        /// auto rows.</para> <paramref name="keptActions"/> is
        /// the post-Rec-1-retire list, so every route-typed row in it already
        /// satisfies UT &lt;= cutoff — no cutoff parameter is needed. Ordering:
        /// higher UT wins; ties at the same UT resolve by higher
        /// <see cref="GameAction.Sequence"/> (a RouteResumed and a RoutePaused
        /// at identical UT+Sequence are not producible by the emitters; list
        /// order breaks that degenerate tie). Returns
        /// <see cref="RouteTimelineStatus.NoMarker"/> when the route has no
        /// kept marker (predates the feature, or was never paused/resumed).
        ///
        /// <para><b>Resume-needs-a-recorded-pause gate:</b> a derived Active
        /// verdict is only returned when at least one kept PLAYER-DRIVEN
        /// <see cref="GameActionType.RoutePaused"/> row exists for the route
        /// (a resume must resume something recorded; auto pause rows do not
        /// satisfy the gate because they are skipped from the scan). Otherwise the resume
        /// row proves the route WAS paused at some marker-less point (a
        /// pre-feature pause, or one whose emission was skipped on an
        /// unresolved UT), and un-pausing on its evidence alone could undo a
        /// pause the timeline never recorded; the verdict downgrades to
        /// NoMarker (status left alone). Remaining degenerate case
        /// (accepted): a kept RoutePaused from an EARLIER pause cycle followed
        /// by a kept resume followed by a marker-less LATER pause still
        /// derives Active - the marker-less pause is unrecoverable by
        /// construction, and the kept rows genuinely end in a resume.</para>
        /// Pure: null lists / null entries tolerated.
        /// </summary>
        internal static RouteTimelineStatus DeriveTimelineStatus(
            string routeId, IReadOnlyList<GameAction> keptActions)
        {
            if (string.IsNullOrEmpty(routeId) || keptActions == null)
                return RouteTimelineStatus.NoMarker;

            bool found = false;
            bool anyPausedRow = false;
            double bestUT = double.NegativeInfinity;
            int bestSeq = int.MinValue;
            RouteTimelineStatus best = RouteTimelineStatus.NoMarker;

            for (int i = 0; i < keptActions.Count; i++)
            {
                GameAction a = keptActions[i];
                if (a == null || !string.Equals(a.RouteId, routeId, StringComparison.Ordinal))
                    continue;
                if (a.Type != GameActionType.RoutePaused && a.Type != GameActionType.RouteResumed)
                    continue;
                // Auto lifecycle rows (source-validity flips) carry no player
                // intent: skip them BEFORE the pause-row gate and the
                // latest-marker scan, so the latest PLAYER row wins.
                if (IsAutoLifecycleRow(a))
                    continue;
                if (a.Type == GameActionType.RoutePaused)
                    anyPausedRow = true;
                if (found && (a.UT < bestUT || (a.UT == bestUT && a.Sequence < bestSeq)))
                    continue;
                found = true;
                bestUT = a.UT;
                bestSeq = a.Sequence;
                best = a.Type == GameActionType.RoutePaused
                    ? RouteTimelineStatus.Paused
                    : RouteTimelineStatus.Active;
            }

            if (best == RouteTimelineStatus.Active && !anyPausedRow)
                return RouteTimelineStatus.NoMarker;
            return best;
        }

        /// <summary>
        /// Applies a <see cref="DeriveTimelineStatus"/> verdict to a KEPT
        /// route at the rewind seam. Rules:
        /// derived <see cref="RouteTimelineStatus.Paused"/> flips only the
        /// ghost-driving / wait statuses (<see cref="RouteStatus.Active"/>,
        /// <see cref="RouteStatus.WaitingForResources"/>,
        /// <see cref="RouteStatus.WaitingForFunds"/>,
        /// <see cref="RouteStatus.DestinationFull"/>) to Paused; derived
        /// <see cref="RouteTimelineStatus.Active"/> flips only a
        /// <see cref="RouteStatus.Paused"/> route back to Active;
        /// <see cref="RouteTimelineStatus.NoMarker"/> leaves the status alone.
        /// <see cref="RouteStatus.InTransit"/> is owned by
        /// <see cref="ResetCycleStateForRewind"/> (a pre-cutoff in-flight cycle
        /// is real and must complete).
        ///
        /// <para><b>Validity statuses:</b> a route sitting in
        /// <see cref="RouteStatus.MissingSourceRecording"/> /
        /// <see cref="RouteStatus.SourceChanged"/> /
        /// <see cref="RouteStatus.EndpointLost"/> keeps that LIVE status
        /// (validity re-derives from the world elsewhere), but the derived
        /// verdict is written to <see cref="Route.PreMissingStatus"/> instead,
        /// so a later recovery (RevalidateSources restores the pre-missing
        /// baseline) lands on the timeline-correct Paused/Active state rather
        /// than the pre-rewind captured one.</para>
        /// Returns true when the status or the pre-missing baseline changed.
        /// </summary>
        internal static bool ApplyDerivedTimelineStatus(Route route, RouteTimelineStatus derived)
        {
            if (route == null || derived == RouteTimelineStatus.NoMarker)
                return false;

            bool validityStatus =
                route.Status == RouteStatus.MissingSourceRecording
                || route.Status == RouteStatus.SourceChanged
                || route.Status == RouteStatus.EndpointLost;
            if (validityStatus)
            {
                RouteStatus baseline = derived == RouteTimelineStatus.Paused
                    ? RouteStatus.Paused
                    : RouteStatus.Active;
                if (route.PreMissingStatus == baseline)
                    return false;
                ParsekLog.Info(Tag,
                    $"RewindReconcile: route {RouteIds.Short(route.Id)} status={route.Status} kept; " +
                    $"preMissingStatus {route.PreMissingStatus}->{baseline} " +
                    "reason=rewind-status-derivation (recovery restores the timeline-correct state)");
                route.PreMissingStatus = baseline;
                return true;
            }

            if (derived == RouteTimelineStatus.Paused)
            {
                bool ghostDrivingOrWait =
                    route.Status == RouteStatus.Active
                    || route.Status == RouteStatus.WaitingForResources
                    || route.Status == RouteStatus.WaitingForFunds
                    || route.Status == RouteStatus.DestinationFull;
                if (!ghostDrivingOrWait)
                    return false;
                route.TransitionTo(RouteStatus.Paused, "rewind-status-derivation");
                return true;
            }

            // derived == Active: only un-pause an explicitly Paused route.
            if (route.Status != RouteStatus.Paused)
                return false;
            route.TransitionTo(RouteStatus.Active, "rewind-status-derivation");
            return true;
        }

        /// <summary>
        /// Unconditionally clears the armed one-shot flags
        /// (<see cref="Route.PauseAfterCurrentCycle"/>,
        /// <see cref="Route.SendOnceArmed"/>) on a KEPT route at rewind.
        /// Armed one-shots do not survive time travel: the flags carry no
        /// timestamp, so there is no way to tell whether the arm happened
        /// before or after the cutoff — and an arm that survived a rewind
        /// would pause (or stamp Send Once provenance on) a re-flown cycle
        /// the player never armed on the surviving timeline. Dropping a
        /// pre-cutoff arm is the safe direction: the player can trivially
        /// re-arm, while a phantom pause/one-shot cannot be traced.
        /// Returns true when either flag was set.
        /// </summary>
        internal static bool ClearArmedOneShotFlags(Route route)
        {
            if (route == null)
                return false;
            bool changed = route.PauseAfterCurrentCycle || route.SendOnceArmed;
            route.PauseAfterCurrentCycle = false;
            route.SendOnceArmed = false;
            if (changed)
            {
                ParsekLog.Verbose(Tag,
                    $"RewindReconcile: route {RouteIds.Short(route.Id)} cleared armed one-shot " +
                    "flags (PauseAfterCurrentCycle/SendOnceArmed do not survive time travel)");
            }
            return changed;
        }

        /// <summary>
        /// Parses the cycle ordinal from a <c>cycle-N</c> style
        /// <see cref="GameAction.RouteCycleId"/>: the numeric suffix after the
        /// LAST '-'. Returns false for null/empty ids, ids without a dash, or
        /// non-numeric suffixes (unparseable ids are ignored by
        /// <see cref="ReconstructCycleCounters"/>).
        /// </summary>
        internal static bool TryParseCycleOrdinal(string cycleId, out int ordinal)
        {
            ordinal = -1;
            if (string.IsNullOrEmpty(cycleId))
                return false;
            int dash = cycleId.LastIndexOf('-');
            if (dash < 0 || dash == cycleId.Length - 1)
                return false;
            return int.TryParse(
                cycleId.Substring(dash + 1),
                System.Globalization.NumberStyles.None, IC, out ordinal);
        }

        /// <summary>
        /// Best-effort reconstruction of a KEPT route's cycle counters from the
        /// kept ledger rows at the rewind seam (fixes the documented cosmetic
        /// counter-inflation residual). Let <c>maxOrdinal</c> be the highest
        /// ordinal parsed from the route's kept
        /// <see cref="GameActionType.RouteDispatched"/> rows' cycle ids
        /// (unparseable ids ignored). If NO kept dispatch rows exist, both
        /// counters reset to 0. Otherwise the target depends on whether the
        /// route retained a KEPT IN-FLIGHT cycle
        /// (<see cref="Route.CurrentCycleStartUT"/> still set - MUST be
        /// evaluated AFTER <see cref="ResetCycleStateForRewind"/>, which nulls
        /// a post-cutoff cycle start; the rewind seam guarantees that order):
        ///
        /// <para><b>Kept in-flight cycle (the straddle case):</b> the in-flight
        /// cycle is the LATEST kept dispatch (ordinal <c>maxOrdinal</c>) and
        /// its identity must survive: the delivery-time recompute is
        /// <c>cycle-{Completed + Skipped}</c>, so the sum must land ON
        /// <c>maxOrdinal</c> or a straddling multi-stop cycle re-fires its
        /// already-delivered window under a NEW id, misses the kept row in the
        /// per-window ELS dedup, and double-delivers cargo/funds.
        /// <see cref="Route.CompletedCycles"/> = distinct delivered kept cycle
        /// ids EXCLUDING the in-flight cycle's own id (its kept delivered rows
        /// are earlier windows of an incomplete cycle, not a completed one);
        /// <see cref="Route.SkippedCycles"/> = <c>maxOrdinal - Completed</c>
        /// clamped to &gt;= 0. Uniqueness holds: the sum equals the in-flight
        /// cycle's own ordinal, and the NEXT id is only minted after that
        /// cycle completes and bumps the sum past <c>maxOrdinal</c>.</para>
        ///
        /// <para><b>No kept in-flight cycle:</b>
        /// <see cref="Route.CompletedCycles"/> = distinct delivered kept cycle
        /// ids, <see cref="Route.SkippedCycles"/> =
        /// <c>(maxOrdinal + 1) - Completed</c> clamped to &gt;= 0. A cycle
        /// dispatched but not delivered at the cutoff counts as SKIPPED (its
        /// delivery re-runs live under a fresh id) - cosmetically imprecise
        /// but safe: <c>Completed + max(0, maxOrdinal + 1 - Completed) &gt;=
        /// maxOrdinal + 1 &gt; maxOrdinal</c>, so the next live dispatch can
        /// never re-use a kept row's cycle id.</para>
        /// Returns true when either counter changed.
        /// </summary>
        internal static bool ReconstructCycleCounters(
            Route route, IReadOnlyList<GameAction> keptActions)
        {
            if (route == null)
                return false;

            bool anyDispatchRows = false;
            int maxOrdinal = -1;
            string maxOrdinalCycleId = null;
            HashSet<string> deliveredCycleIds = null;

            if (keptActions != null)
            {
                for (int i = 0; i < keptActions.Count; i++)
                {
                    GameAction a = keptActions[i];
                    if (a == null || !string.Equals(a.RouteId, route.Id, StringComparison.Ordinal))
                        continue;
                    if (a.Type == GameActionType.RouteDispatched)
                    {
                        anyDispatchRows = true;
                        if (TryParseCycleOrdinal(a.RouteCycleId, out int ord) && ord > maxOrdinal)
                        {
                            maxOrdinal = ord;
                            maxOrdinalCycleId = a.RouteCycleId;
                        }
                    }
                    else if (a.Type == GameActionType.RouteCargoDelivered
                        && !string.IsNullOrEmpty(a.RouteCycleId))
                    {
                        if (deliveredCycleIds == null)
                            deliveredCycleIds = new HashSet<string>(StringComparer.Ordinal);
                        deliveredCycleIds.Add(a.RouteCycleId);
                    }
                }
            }

            bool keptInFlightCycle = route.CurrentCycleStartUT.HasValue;
            int completed = 0;
            int skipped = 0;
            if (anyDispatchRows)
            {
                completed = deliveredCycleIds != null ? deliveredCycleIds.Count : 0;
                if (keptInFlightCycle)
                {
                    // The kept in-flight cycle is not completed; its kept
                    // delivered rows (earlier windows of a straddling
                    // multi-stop cycle) must not count, and the sum must land
                    // ON its ordinal so the delivery-time cycleId recompute
                    // reproduces the dispatch's id (see XML doc above).
                    if (maxOrdinalCycleId != null && deliveredCycleIds != null
                        && deliveredCycleIds.Contains(maxOrdinalCycleId))
                        completed--;
                    skipped = maxOrdinal - completed;
                }
                else
                {
                    skipped = (maxOrdinal + 1) - completed;
                }
                if (skipped < 0)
                    skipped = 0;
            }

            bool changed = route.CompletedCycles != completed || route.SkippedCycles != skipped;
            if (changed)
            {
                ParsekLog.Verbose(Tag,
                    $"RewindReconcile: route {RouteIds.Short(route.Id)} counters reconstructed " +
                    $"completed {route.CompletedCycles.ToString(IC)}->{completed.ToString(IC)} " +
                    $"skipped {route.SkippedCycles.ToString(IC)}->{skipped.ToString(IC)} " +
                    $"maxKeptDispatchOrdinal={maxOrdinal.ToString(IC)} " +
                    $"keptInFlightCycle={(keptInFlightCycle ? "1" : "0")}");
                route.CompletedCycles = completed;
                route.SkippedCycles = skipped;
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
            // Fresh-creation schedule anchor: the abandoned-future dispatch stamp
            // must not survive (TryActivate only pulls a stale value UP, so a
            // far-future NextDispatchUT would stall the legacy dispatch path
            // until the abandoned UT). CreatedUT is the route's timeline anchor;
            // activation pulls it up to the live UT.
            route.NextDispatchUT = route.CreatedUT >= 0.0 ? route.CreatedUT : 0.0;
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
