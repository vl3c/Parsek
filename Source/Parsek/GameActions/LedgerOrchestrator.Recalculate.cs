using System;
using System.Collections.Generic;
using System.Globalization;
using KscActionExpectation = Parsek.KscActionExpectationClassifier.KscActionExpectation;

namespace Parsek
{
    internal static partial class LedgerOrchestrator
    {
        /// <summary>
        /// Full recalculation from ledger to engine to KSP state patch.
        /// Called on: commit, rewind, warp exit, KSP load.
        /// </summary>
        /// <param name="utCutoff">
        /// Optional UT cutoff forwarded to <see cref="RecalculationEngine.Recalculate"/>.
        /// When non-null, actions with <c>UT &gt; utCutoff</c> are excluded from the walk
        /// (seed actions always survive). Callers that are not rewind-driven pass no
        /// argument and get <c>null</c> — the default walk-everything behavior. The
        /// rewind paths (<c>HandleRewindOnLoad</c> + <c>ApplyRewindResourceAdjustment</c>)
        /// pass the adjusted rewind UT explicitly; <c>RecalculateAndPatch</c> never
        /// consults <see cref="RewindContext"/> itself, so callers must supply the
        /// cutoff at the call site before <see cref="RewindContext.EndRewind"/> clears
        /// the global.
        /// </param>
        /// <param name="deadlineClockUT">
        /// Optional instant contract deadlines are judged against when it differs from the
        /// walk cutoff (the Re-Fly invoke walk: cutoff <c>double.MaxValue</c>, clock = the
        /// live UT). Null keeps the cutoff as the deadline clock.
        /// </param>
        /// <param name="reFlyDiscard">
        /// True only for the recalculation that follows a Re-Fly discard which already removed
        /// the attempt's rows and ended the session (rewind design section 2.12 Rule 3): the
        /// stock pools still carry what the attempt earned or spent, and the patch must take
        /// them back to the ledger's values, so it is an authoritative reduction although the
        /// marker is already gone.
        /// </param>
        internal static void RecalculateAndPatch(
            double? utCutoff = null,
            bool suppressSuspiciousDrawdownWarnings = false,
            double? deadlineClockUT = null,
            bool reFlyDiscard = false)
        {
            RecalculateAndPatchCore(
                utCutoff,
                bypassPatchDeferral: utCutoff.HasValue,
                authoritativeRepeatableRecordState: utCutoff.HasValue,
                techPatchCutoff: utCutoff,
                suppressSuspiciousDrawdownWarnings: suppressSuspiciousDrawdownWarnings,
                deadlineClockUT: deadlineClockUT,
                reFlyDiscard: reFlyDiscard);
        }

        /// <summary>
        /// Current-timeline recalculation that filters the walk to the supplied live UT
        /// without opting into rewind-only patch side effects. This preserves
        /// pending/live-tree patch deferral and same-branch repeatable-record
        /// preservation while keeping resources/contracts/facilities at the live
        /// game clock instead of the full future timeline.
        /// </summary>
        internal static void RecalculateAndPatchForCurrentTimelineUT(
            double utCutoff,
            string reason = null,
            bool reFlyDiscard = false)
        {
            if (!string.IsNullOrEmpty(reason))
            {
                ParsekLog.Info(Tag,
                    $"Current-UT ledger recalculation: reason={reason} cutoffUT={utCutoff.ToString("R", CultureInfo.InvariantCulture)}");
            }

            RecalculateAndPatchCore(
                utCutoff,
                bypassPatchDeferral: false,
                authoritativeRepeatableRecordState: false,
                techPatchCutoff: utCutoff,
                suppressSuspiciousDrawdownWarnings: true,
                reFlyDiscard: reFlyDiscard);
        }

        /// <summary>
        /// Back-compat wrapper for existing post-rewind scene-load call sites/tests.
        /// </summary>
        internal static void RecalculateAndPatchForPostRewindFlightLoad(double utCutoff)
        {
            RecalculateAndPatchForCurrentTimelineUT(utCutoff, "post-rewind-load");
        }

        /// <summary>
        /// Post-time-jump recalculation that filters the committed ledger to the
        /// newly selected UT without opting into rewind-only patch side effects.
        /// This preserves pending/live-tree patch deferral and same-branch
        /// repeatable-record preservation while keeping resources/contracts/facilities
        /// at the jump target instead of at the full future timeline.
        /// </summary>
        internal static void RecalculateAndPatchForTimeJump(double utCutoff)
        {
            // A forward jump (Warp to, the Missions / Recordings fast-forward, the Tracking
            // Station FF) moves the clock past committed-future rows the same way the clock
            // cursor does, so the reached rows get the same treatment; a backward jump
            // reaches none of them.
            ExpireHeldThroughFloorAtTrustedClock(utCutoff, "time-jump");
            RunAsClockAdvance(() => RecalculateAndPatchForCurrentTimelineUT(utCutoff, "time-jump"));
        }

        /// <param name="reFlyDiscard">See <see cref="RecalculateAndPatch(double?, bool, double?, bool)"/>:
        /// set only by a Re-Fly discard path, after it removed the attempt's rows.</param>
        internal static void RecalculateAndPatchForCurrentTimelineIfFutureActions(
            double currentUT,
            string reason,
            bool reFlyDiscard = false)
        {
            string safeReason = string.IsNullOrEmpty(reason) ? "current-timeline" : reason;
            // BUG-F guard: never cut off at a non-positive (not-yet-initialized) clock value.
            // A UT of 0 during an early load/seed pass would otherwise filter out an entire
            // established career as "future". In-session callers always pass a valid UT, so
            // this only changes behavior on the not-ready path (falls back to full replay).
            bool currentUtReady = IsCurrentUtReadyForCutoff(currentUT);
            bool hasFutureActions = currentUtReady && HasActionsAfterUT(currentUT);
            if (hasFutureActions)
            {
                RecalculateAndPatchForCurrentTimelineUT(currentUT, safeReason, reFlyDiscard);
                return;
            }

            ParsekLog.Verbose(Tag,
                "Current-timeline recalc: full replay (cutoffUT=null) — reason=" + safeReason +
                " currentUT=" + currentUT.ToString("R", CultureInfo.InvariantCulture) +
                " currentUtReady=" + currentUtReady);
            RecalculateAndPatch(reFlyDiscard: reFlyDiscard);
        }

        /// <summary>
        /// Pure: true when a commit walk may judge the committed future against the live
        /// clock. False while <c>ParsekScenario.OnLoad</c> runs (the live clock is still the
        /// pre-load one there, QL-1; the load's own in-session or deferred-seed recalculation
        /// walks at the loaded clock right after) and while a rewind's UT adjustment is
        /// pending (the clock is not yet the rewind target's).
        /// </summary>
        internal static bool ShouldCommitWalkUseLiveClock(
            bool onLoadInProgress, bool rewindUtAdjustmentPending)
        {
            return !onLoadInProgress && !rewindUtAdjustmentPending;
        }

        /// <summary>
        /// The ledger walk of a commit (TREE-COMMIT-WALKS-PAST-THE-CLOCK). With committed rows
        /// ahead of the live clock (after a rewind) the walk is cut at the clock, so KSP gets
        /// the career state at now and the committed future stays committed; with none it is
        /// the same full walk as before. The derived fields the post-walk reconciliation reads
        /// are filled either way: a cutoff walk fills the rows it walks, and its projection walk
        /// fills the rows past the cutoff.
        /// </summary>
        private static void RecalculateAndPatchAfterCommit(string reason)
        {
            bool onLoad = ParsekScenario.IsOnLoadInProgress;
            bool rewindAdjust = RecordingStore.RewindUTAdjustmentPending;
            if (!ShouldCommitWalkUseLiveClock(onLoad, rewindAdjust))
            {
                ParsekLog.Verbose(Tag,
                    "Commit ledger walk: full replay (cutoffUT=null), live clock not trusted - reason=" +
                    reason + " onLoadInProgress=" + onLoad +
                    " rewindUTAdjustmentPending=" + rewindAdjust);
                RecalculateAndPatch();
                return;
            }

            RecalculateAndPatchForCurrentTimelineIfFutureActions(
                ParsekScenario.GetCurrentTimelineUTForLedgerRecalc(), reason);
        }

        /// <summary>
        /// The cheap crossed-an-end check for time passing OUTSIDE the paths that already
        /// recalculate (scene load, commit, rewind, flight warp exit): the KSC and
        /// Tracking Station clocks and the crew-assignment dialog opening. Compares
        /// <paramref name="nowUT"/> with the earliest time-based reservation release the
        /// last walk left pending (<see cref="KerbalsModule.NextReservationReleaseUT"/>)
        /// and, only when the clock has reached it, runs the ordinary current-timeline
        /// recalculation once. Everything per call is two double comparisons; a full
        /// recalculation happens once per crossed release, never per frame.
        /// Returns true when it recalculated.
        /// </summary>
        internal static bool RecalculateIfKerbalReservationReleaseDue(double nowUT, string reason)
        {
            var kerbals = kerbalsModule;
            if (kerbals == null) return false;
            double next = kerbals.NextReservationReleaseUT;
            double lastTriggered = KerbalsModule.ResolveLastTriggeredReleaseUT(
                lastTriggeredReservationReleaseUT,
                kerbals.PostWalkCount,
                postWalkCountAfterReservationReleaseTrigger);
            if (!KerbalsModule.IsReservationReleaseDue(nowUT, next, lastTriggered))
                return false;
            // A load or a rewind's UT adjustment in progress is not a clock to act on; the
            // load / post-rewind recalculation that follows judges the right instant.
            if (ParsekScenario.IsOnLoadInProgress || RecordingStore.RewindUTAdjustmentPending)
                return false;

            lastTriggeredReservationReleaseUT = next;
            string safeReason = string.IsNullOrEmpty(reason) ? "reservation-release" : reason;
            ParsekLog.Info(Tag,
                "Kerbal reservation release due: nextReleaseUT="
                + next.ToString("R", CultureInfo.InvariantCulture)
                + " nowUT=" + nowUT.ToString("R", CultureInfo.InvariantCulture)
                + " reason=" + safeReason + " - recalculating");
            RecalculateAndPatchForCurrentTimelineIfFutureActions(nowUT, safeReason);
            var after = kerbalsModule;
            postWalkCountAfterReservationReleaseTrigger = after != null ? after.PostWalkCount : -1;
            return true;
        }

        /// <summary>
        /// Recalculates after a live KSC/flight event has just been written to the
        /// ledger. If committed actions still exist after the event UT, keep the walk
        /// cutoff at that live UT so future rewards are not patched into KSP early.
        /// </summary>
        internal static void RecalculateAndPatchForLiveTimelineEvent(
            double eventUT,
            string reason)
        {
            string safeReason = string.IsNullOrEmpty(reason) ? "live-event" : reason;
            bool hasFuture = HasActionsAfterUT(eventUT);
            ParsekLog.Verbose(Tag,
                $"Live-event recalc decision: reason={safeReason} eventUT={eventUT.ToString("R", CultureInfo.InvariantCulture)} " +
                $"hasFutureLedgerActions={hasFuture}");

            if (hasFuture)
            {
                RecalculateAndPatchForCurrentTimelineUT(eventUT, safeReason);
                return;
            }

            RecalculateAndPatch();
        }

        /// <summary>
        /// The clock cursor's recalculation (<see cref="CareerLedgerClockCursor"/>, the Space
        /// Center and Tracking Station warp). Same walk as
        /// <see cref="RecalculateAndPatchForLiveTimelineEvent"/>, plus one thing: the
        /// committed-future rows the clock just reached (rows past the previous applied
        /// patch's cutoff, so stock never charged them) are what moves the running pools
        /// away from live, which the drawdown guard would otherwise refuse as a missing
        /// channel. Such a crossing passes the guard when live still holds what the last
        /// patch left (nothing outside the ledger moved it), so the rows apply once; any
        /// other divergence still clamps.
        /// </summary>
        internal static void RecalculateAndPatchForClockAdvance(double currentUT, string reason)
        {
            ExpireHeldThroughFloorAtTrustedClock(currentUT, reason ?? "clock-advance");
            RunAsClockAdvance(() => RecalculateAndPatchForLiveTimelineEvent(currentUT, reason));
        }

        /// <summary>
        /// Drops the ledger held-through UT once the clock that moves forward has reached it
        /// (<see cref="LedgerHeldWindow.ExpireIfClockPassed"/>). Not inside OnLoad or while a
        /// rewind's UT adjustment is pending: the Planetarium reads a stale clock there.
        /// </summary>
        private static void ExpireHeldThroughFloorAtTrustedClock(double clockUT, string site)
        {
            if (!LedgerHeldWindow.IsSet)
                return;
            if (ParsekScenario.IsOnLoadInProgress || RecordingStore.RewindUTAdjustmentPending)
                return;
            LedgerHeldWindow.ExpireIfClockPassed(clockUT, site);
        }

        private static void RunAsClockAdvance(Action recalc)
        {
            bool previous = clockAdvanceRecalcInProgress;
            clockAdvanceRecalcInProgress = true;
            try
            {
                recalc();
            }
            finally
            {
                clockAdvanceRecalcInProgress = previous;
            }
        }

        /// <summary>
        /// Forgets the committed-future snapshot. A load replaces the live pools with the
        /// save's, so rows that were ahead of the clock at a patch before the load prove
        /// nothing about what the loaded save charged.
        /// </summary>
        internal static void InvalidateCommittedFutureSnapshot(string reason)
        {
            if (committedFutureSnapshotValid || committedFutureActionIdsAtLastPatch.Count > 0)
            {
                ParsekLog.Verbose(Tag,
                    $"Committed-future snapshot cleared: reason={reason ?? "unspecified"} " +
                    $"rows={committedFutureActionIdsAtLastPatch.Count.ToString(CultureInfo.InvariantCulture)}");
            }
            committedFutureActionIdsAtLastPatch.Clear();
            committedFutureSnapshotValid = false;
        }

        /// <summary>
        /// Records which rows were still ahead of the clock when this patch was applied:
        /// every non-seed row past <paramref name="utCutoff"/>, none on a full walk.
        /// </summary>
        internal static void CaptureCommittedFutureSnapshot(List<GameAction> actions, double? utCutoff)
        {
            committedFutureActionIdsAtLastPatch.Clear();
            committedFutureSnapshotValid = true;
            if (!utCutoff.HasValue || actions == null)
                return;
            double cutoff = utCutoff.Value;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a == null || string.IsNullOrEmpty(a.ActionId)) continue;
                if (RecalculationEngine.IsSeedType(a.Type)) continue;
                if (a.UT > cutoff)
                    committedFutureActionIdsAtLastPatch.Add(a.ActionId);
            }
        }

        /// <summary>
        /// Pure split: the rows of <paramref name="actions"/> that were committed future at
        /// the last applied patch (<paramref name="futureAtLastPatch"/>) and are now within
        /// <paramref name="cutoff"/>, and every other row. Seeds always stay.
        /// </summary>
        internal static List<GameAction> SplitCrossedCommittedFutureRows(
            List<GameAction> actions, ICollection<string> futureAtLastPatch, double cutoff,
            out List<GameAction> crossed)
        {
            crossed = new List<GameAction>();
            var rest = new List<GameAction>(actions != null ? actions.Count : 0);
            if (actions == null)
                return rest;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a == null) continue;
                if (futureAtLastPatch != null
                    && !RecalculationEngine.IsSeedType(a.Type)
                    && a.UT <= cutoff
                    && !string.IsNullOrEmpty(a.ActionId)
                    && futureAtLastPatch.Contains(a.ActionId))
                {
                    crossed.Add(a);
                    continue;
                }
                rest.Add(a);
            }
            return rest;
        }

        /// <summary>
        /// For a clock-advance recalc: the committed-future rows (at the last applied patch)
        /// this advance reaches, or null when it reaches none (the guard then runs
        /// unchanged). The pool patches treat such a crossing as authoritative only when live
        /// still holds what the stock pool reference last wrote
        /// (<see cref="KspStatePatcher.IsLiveUnchangedSinceLastPatch"/>).
        /// </summary>
        private static KspStatePatcher.ClockCrossing ComputeClockCrossing(
            List<GameAction> actions, double? utCutoff)
        {
            if (!committedFutureSnapshotValid || committedFutureActionIdsAtLastPatch.Count == 0)
                return null;
            double cutoff = utCutoff ?? double.MaxValue;
            List<GameAction> crossed;
            SplitCrossedCommittedFutureRows(
                actions, committedFutureActionIdsAtLastPatch, cutoff, out crossed);
            if (crossed.Count == 0)
                return null;

            var crossing = new KspStatePatcher.ClockCrossing { CrossedRows = crossed.Count };
            var labels = new List<string>(Math.Min(crossed.Count, 10));
            // A hire the walk already owns (design 3.15 step 6) is not charged, so it makes
            // nobody: a kerbal dismissed since the purchase that owns him is not hired for free.
            HashSet<GameAction> ownedPurchases = null;
            int ownedHires = 0;
            for (int i = 0; i < crossed.Count; i++)
            {
                if (crossed[i].Type == GameActionType.KerbalHire
                    && !string.IsNullOrEmpty(crossed[i].KerbalName))
                {
                    if (ownedPurchases == null)
                        ownedPurchases = PurchaseStateRule.CollectAlreadyOwned(actions);
                    if (ownedPurchases.Contains(crossed[i]))
                        ownedHires++;
                    else
                        crossing.CrossedHireKerbals.Add(crossed[i].KerbalName);
                }
                if (i < 10)
                    labels.Add(crossed[i].Type + "@" + crossed[i].UT.ToString("R", CultureInfo.InvariantCulture));
            }
            ParsekLog.Info(Tag,
                $"Clock-crossing reached committed-future rows: crossed={crossed.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"[{string.Join(", ", labels.ToArray())}] " +
                $"cutoff={(utCutoff.HasValue ? utCutoff.Value.ToString("R", CultureInfo.InvariantCulture) : "null")} " +
                $"hires={crossing.CrossedHireKerbals.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"ownedHiresSkipped={ownedHires.ToString(CultureInfo.InvariantCulture)}");
            return crossing;
        }

        /// <summary>
        /// Post-supersede tombstone refresh. The broad tombstone scope can remove
        /// career effects from the full ELS after the normal commit-time recalc has
        /// already run, so this path walks the full timeline, bypasses live-tree
        /// deferral, and still enables an authoritative repeatable-record patch.
        /// Tech-tree patching stays disabled unless the current tombstone pass
        /// retired a ScienceSpending row that may need a baseline-seeded unlock
        /// removed.
        /// </summary>
        internal static void RecalculateAndPatchAfterTombstones(
            IReadOnlyCollection<string> currentTombstonedScienceSpendingNodeIds)
        {
            bool patchTechTree = currentTombstonedScienceSpendingNodeIds != null &&
                currentTombstonedScienceSpendingNodeIds.Count > 0;
            FacilityStatePatcher.ForceDefaultFacilitiesForNextPatch(
                BuildTombstonedFacilityIdsForPatch());
            RecalculateAndPatchCore(
                utCutoff: null,
                bypassPatchDeferral: true,
                authoritativeRepeatableRecordState: true,
                techPatchCutoff: patchTechTree ? double.MaxValue : (double?)null,
                excludeTombstonedTechFromBaseline: patchTechTree,
                suppressSuspiciousDrawdownWarnings: false,
                tombstonePath: true);
        }

        private static void RecalculateAndPatchCore(
            double? utCutoff,
            bool bypassPatchDeferral,
            bool authoritativeRepeatableRecordState,
            double? techPatchCutoff,
            bool excludeTombstonedTechFromBaseline = false,
            bool suppressSuspiciousDrawdownWarnings = false,
            bool tombstonePath = false,
            double? deadlineClockUT = null,
            bool reFlyDiscard = false)
        {
            Initialize();

            // The rewind lead window (todo REWIND-LEAD-WINDOW-KSC-ACTIONS-LAND-IN-THE-FUTURE):
            // rows the loaded world already holds count as reached until the clock passes the
            // held-through UT, so a cutoff behind it rises to it. Every walk below (the pools,
            // the KSP state patches, the committed-future snapshot the clock crossings read)
            // then agrees with the loaded world. A full walk (null) is unaffected.
            utCutoff = ApplyHeldThroughFloorToCutoff(utCutoff, "walk");
            techPatchCutoff = ApplyHeldThroughFloorToCutoff(techPatchCutoff, "tech");
            deadlineClockUT = ApplyHeldThroughFloorToCutoff(deadlineClockUT, "deadline");

            PrepareRecalculationWalk();

            var actions = BuildRecalculationActions();
            LogRecalculationInputSummary(actions, utCutoff);

            WalkLedgerAndReconcile(actions, utCutoff, deadlineClockUT);

            // #466: a live or pending tree means KSP's mutable state may already include
            // uncommitted in-flight effects that the committed-only ledger cannot see yet.
            // Walking the committed ledger is still useful, but writing that partial state
            // back into KSP is destructive. Rewind-style cutoff walks remain authoritative.
            // A ledger held from a newer Parsek version walks empty: patching KSP from that walk
            // would reset the career's pools to seed values, so no walk patches this session
            // (cutoff walks included).
            string patchDeferralReason = ResolveRecalcPatchDeferralReason(bypassPatchDeferral);
            if (!string.IsNullOrEmpty(patchDeferralReason))
                ApplyDeferredPatchMilestoneReach(actions, utCutoff, patchDeferralReason);
            else
                PatchKspFromRecalculatedWalk(
                    actions,
                    utCutoff,
                    authoritativeRepeatableRecordState,
                    techPatchCutoff,
                    excludeTombstonedTechFromBaseline,
                    suppressSuspiciousDrawdownWarnings,
                    tombstonePath,
                    deadlineClockUT,
                    reFlyDiscard);

            // #391 / cutoff-cache follow-up: rebuild committedScienceSubjects from
            // the full surviving ledger, not from the current-UT ScienceModule. Cutoff
            // walks intentionally leave the live module at the rewind/jump target, but
            // the persisted cache is a load-recovery source and must retain future
            // committed science rows that still survive in the ledger.
            // The cache is a load-recovery source persisted beside the events; an empty walk of a
            // held ledger must not overwrite it.
            if (!Ledger.IsHeldFromNewerVersion)
                RebuildCommittedScienceFromSurvivingLedger(actions);

            LogRecalculateAndPatchComplete(
                actions, utCutoff, patchDeferralReason, authoritativeRepeatableRecordState);

            OnTimelineDataChanged?.Invoke();
        }

        /// <summary>
        /// The steps of <see cref="RecalculateAndPatchCore"/> before the walk's action list
        /// is built: drop an older walk's pending strategy patch, seed the initial
        /// balances and the pre-ledger progress seed, and fill unpopulated crew end states.
        /// </summary>
        internal static void PrepareRecalculationWalk()
        {
            // A strategy patch still waiting for stock's strategy list belongs to an older
            // walk; this walk re-requests one if (and only if) it patches KSP state.
            StrategyStatePatcher.CancelPendingPatch("new-recalc");

            // Seed initial balances for career mode (per-resource, idempotent).
            // Baselines can represent legitimate zero science/rep values; once such
            // a seed exists it must not be upgraded later from future live state.
            SeedInitialResourceBalances();

            // For stock progress: capture (once per save) what the career had
            // achieved before the ledger began, and hand it to the milestones module for
            // this walk. Until it is captured PatchMilestones skips, so nothing is cleared.
            EnsurePreLedgerProgressSeed();
            milestonesModule.SetPreLedgerProgressSeed(Ledger.ProgressSeed);

            // End-state population safety net: catch recordings with unpopulated end states
            PopulateUnpopulatedCrewEndStates();
        }

        /// <summary>
        /// The engine walk of <see cref="RecalculateAndPatchCore"/> and what must follow it
        /// before any KSP patch decision: the slot limits, the post-walk reconciliation
        /// and, for a cutoff walk, the crew reservations rebuilt from the full ledger.
        /// </summary>
        internal static void WalkLedgerAndReconcile(
            List<GameAction> actions, double? utCutoff, double? deadlineClockUT)
        {
            // A cutoff engine walk sees only the rows up to the cutoff; the kerbals module's
            // authoritative walk is RecomputeAfterCutoffWalk below, so this one records no
            // release / re-reserve transitions.
            if (utCutoff.HasValue && kerbalsModule != null)
                kerbalsModule.MarkNextWalkProvisional();
            RecalculationEngine.Recalculate(actions, utCutoff, deadlineClockUT);

            // FacilitiesModule is dispatched after contracts/strategies, so a
            // facility upgrade's final slot limit is only known after the walk.
            // Refresh now so callers that perform a single recalc after warp exit
            // or a time jump expose the final Mission Control/Admin availability.
            UpdateSlotLimitsFromFacilities();

            // #440 Phase E2: post-walk reconciliation for strategy-transformed
            // and curve-applied reward types. Runs once per walk, log-only,
            // after derived fields (Transformed*Reward/EffectiveRep/
            // EffectiveScience) are populated and before the later KSP patch/defer
            // branch decides whether this walk mutates live game state.
            ReconcilePostWalk(GameStateStore.Events, actions, utCutoff);

            // Cutoff walks intentionally keep resources/career state at the rewind
            // or jump target. Crew reservations are different: committed future
            // recordings still own their crew until recovery/death, so rebuild the
            // crew module from the full effective ledger after the cutoff walk.
            // Roster mutation still happens below through the existing patch gate.
            if (utCutoff.HasValue)
                CrewReservationManager.RecomputeAfterCutoffWalk(utCutoff.Value);
        }

        /// <summary>
        /// Why <see cref="RecalculateAndPatchCore"/> must not patch KSP state from this walk,
        /// or null when it may: a ledger held from a newer version (logged, rate limited),
        /// else, unless the caller bypasses deferral, a live or pending tree.
        /// </summary>
        internal static string ResolveRecalcPatchDeferralReason(bool bypassPatchDeferral)
        {
            string patchDeferralReason = Ledger.IsHeldFromNewerVersion
                ? HeldLedgerPatchDeferralReason
                : bypassPatchDeferral
                    ? null
                    : GetKspPatchDeferralReason();
            if (Ledger.IsHeldFromNewerVersion)
                ParsekLog.InfoRateLimited(Tag, "held-ledger-patch-skipped",
                    "RecalculateAndPatch: KSP state patch skipped (" + HeldLedgerPatchDeferralReason +
                    "); funds, science, reputation, tech, contracts, facilities and strategies keep the save's values");
            return patchDeferralReason;
        }

        /// <summary>
        /// The deferred side of <see cref="RecalculateAndPatchCore"/>: log the reason and
        /// mark, add-only, the world-firsts the committed timeline credits at or before the
        /// clock, so stock does not pay them again while the patch waits.
        /// </summary>
        internal static void ApplyDeferredPatchMilestoneReach(
            List<GameAction> actions, double? utCutoff, string patchDeferralReason)
        {
            ParsekLog.Verbose(Tag,
                $"RecalculateAndPatch: deferred KSP state patch ({patchDeferralReason})");
            // The milestone patch is held back with the rest, but a world-first the
            // committed timeline credits at or before the clock must not stay payable in
            // stock meanwhile (WORLD-FIRST-REEARNED-AFTER-REWIND-PAID-AGAIN). Add-only:
            // it never clears progress the live flight earned.
            CommittedMilestoneReach.Apply(actions,
                CommittedMilestoneReach.ResolveClockUT(utCutoff,
                    ParsekScenario.IsOnLoadInProgress,
                    RecordingStore.RewindUTAdjustmentPending,
                    ParsekScenario.GetCurrentTimelineUTForLedgerRecalc),
                "deferred-patch");
        }

        /// <summary>
        /// The patching side of <see cref="RecalculateAndPatchCore"/>: clear the subject
        /// science mirror, decide the drawdown guard's authoritative reduction, apply the
        /// walk to KSP under this walk's clock crossing, and capture the committed-future
        /// snapshot.
        /// </summary>
        internal static void PatchKspFromRecalculatedWalk(
            List<GameAction> actions,
            double? utCutoff,
            bool authoritativeRepeatableRecordState,
            double? techPatchCutoff,
            bool excludeTombstonedTechFromBaseline,
            bool suppressSuspiciousDrawdownWarnings,
            bool tombstonePath,
            double? deadlineClockUT,
            bool reFlyDiscard)
        {
            // The walk's module is authoritative at this patch's clock; the subject
            // science mirror bridges only a deferred stretch (design 3.15 step 5).
            CommittedMilestoneReach.ClearSubjectScienceMirror("patch-not-deferred");

            // Drawdown-guard plan §3.2: compute the authoritative-reduction signal once
            // here (Core has access to RewindContext + ParsekScenario.Instance) and pass
            // it down to the patch methods, which read the per-resource running balance
            // from the module they already hold. Kept separate from
            // suppressSuspiciousDrawdownWarnings (that flag only silences the legacy 10%
            // WARN; this flag authorizes the clamp bypass).
            bool authoritativeReduction = IsAuthoritativeReduction(
                RewindContext.IsRewinding,
                ParsekScenario.Instance?.ActiveReFlySessionMarker != null,
                ParsekScenario.Instance?.ActiveMergeJournal != null,
                tombstonePath,
                RewindContext.RewindResourceAdjustmentInProgress,
                reFlyDiscard);
            ParsekLog.Verbose(Tag,
                "RecalculateAndPatch: drawdown-guard authoritativeReduction=" +
                authoritativeReduction +
                $" (rewinding={RewindContext.IsRewinding}, " +
                $"reFlyMarker={ParsekScenario.Instance?.ActiveReFlySessionMarker != null}, " +
                $"mergeJournal={ParsekScenario.Instance?.ActiveMergeJournal != null}, " +
                $"tombstone={tombstonePath}, " +
                $"rewindResAdjust={RewindContext.RewindResourceAdjustmentInProgress}, " +
                $"reFlyDiscard={reFlyDiscard})");

            KspStatePatcher.ClockCrossing clockCrossing =
                clockAdvanceRecalcInProgress
                    ? ComputeClockCrossing(actions, utCutoff)
                    : null;
            var previousCrossing = KspStatePatcher.ActiveClockCrossing;
            KspStatePatcher.ActiveClockCrossing = clockCrossing;
            try
            {
                ApplyRecalculatedStateToKsp(
                    actions,
                    utCutoff,
                    authoritativeRepeatableRecordState,
                    techPatchCutoff,
                    excludeTombstonedTechFromBaseline,
                    suppressSuspiciousDrawdownWarnings,
                    authoritativeReduction,
                    deadlineClockUT);
            }
            finally
            {
                KspStatePatcher.ActiveClockCrossing = previousCrossing;
            }
            CaptureCommittedFutureSnapshot(actions, utCutoff);
        }

        /// <summary>
        /// The closing line of <see cref="RecalculateAndPatchCore"/>, printed only when the
        /// walk's shape (row count, cutoff, deferral, repeatable-record mode) changed.
        /// </summary>
        internal static void LogRecalculateAndPatchComplete(
            List<GameAction> actions, double? utCutoff, string patchDeferralReason,
            bool authoritativeRepeatableRecordState)
        {
            string completeStateKey = string.Format(
                CultureInfo.InvariantCulture,
                "actions={0}|cutoff={1}|deferred={2}|repeatable={3}",
                actions.Count,
                utCutoff.HasValue ? utCutoff.Value.ToString("R", CultureInfo.InvariantCulture) : "null",
                string.IsNullOrEmpty(patchDeferralReason) ? "none" : patchDeferralReason,
                authoritativeRepeatableRecordState ? "authoritative" : "preserve-live");
            ParsekLog.VerboseOnChange(Tag,
                "recalculate-and-patch-complete",
                completeStateKey,
                $"RecalculateAndPatch complete: {actions.Count} actions walked");
        }

        /// <summary>
        /// <see cref="LedgerHeldWindow.ResolveLedgerCutoffUT"/> for one of the walk's clocks,
        /// logging a raise. Recalculations are event-driven (a spend, a commit, a crossed row,
        /// a load), never per frame, so one Verbose line per raise is bounded.
        /// </summary>
        private static double? ApplyHeldThroughFloorToCutoff(double? cutoffUT, string which)
        {
            double? floored = LedgerHeldWindow.ResolveLedgerCutoffUT(cutoffUT);
            if (cutoffUT.HasValue && floored.HasValue && floored.Value != cutoffUT.Value)
            {
                ParsekLog.Verbose(Tag,
                    "RecalculateAndPatch: " + which + " cutoff " +
                    cutoffUT.Value.ToString("R", CultureInfo.InvariantCulture) + " raised to the ledger held-through UT " +
                    floored.Value.ToString("R", CultureInfo.InvariantCulture) +
                    " (rows up to it are in the loaded world)");
            }
            return floored;
        }

        private static List<GameAction> BuildRecalculationActions()
        {
            // Phase 9 of Rewind-to-Staging (design §3.2): route the walk input
            // through the Effective Ledger Set so any tombstoned action is
            // excluded. Tombstones are the only filter — the recording-level
            // filter (v0.4) was explicitly dropped in v0.5's narrow-supersede
            // design, and ComputeELS implements exactly that rule. Pre-Phase-9
            // behavior (Ledger.Actions raw) matches the Phase 9 output on a
            // save with no tombstones (trivially), so this is a drop-in for
            // existing behavior plus the new tombstone filter.
            return new List<GameAction>(EffectiveState.ComputeELS());
        }

        private static void LogRecalculationInputSummary(
            List<GameAction> actions,
            double? utCutoff)
        {
            // Count how many actions survive the cutoff filter for the log summary.
            // Matches the filter in RecalculationEngine.Recalculate (seeds always pass).
            int actionsAfterCutoff = actions.Count;
            if (utCutoff.HasValue)
            {
                double cutoff = utCutoff.Value;
                actionsAfterCutoff = 0;
                for (int i = 0; i < actions.Count; i++)
                {
                    var a = actions[i];
                    if (a == null) continue;
                    if (RecalculationEngine.IsSeedType(a.Type) || a.UT <= cutoff)
                        actionsAfterCutoff++;
                }
            }

            string cutoffLabel = utCutoff.HasValue
                ? utCutoff.Value.ToString("R", CultureInfo.InvariantCulture)
                : "null";
            string stateKey = string.Format(
                CultureInfo.InvariantCulture,
                "total={0}|after={1}|cutoff={2}",
                actions.Count,
                actionsAfterCutoff,
                cutoffLabel);
            string message =
                $"RecalculateAndPatch: actionsTotal={actions.Count}, " +
                $"actionsAfterCutoff={actionsAfterCutoff}, cutoffUT={cutoffLabel}";
            if (utCutoff.HasValue)
            {
                ParsekLog.Verbose(Tag, message);
            }
            else
            {
                ParsekLog.VerboseOnChange(Tag,
                    "recalculate-and-patch-input",
                    stateKey,
                    message);
            }
        }

        private static void ApplyRecalculatedStateToKsp(
            List<GameAction> actions,
            double? utCutoff,
            bool authoritativeRepeatableRecordState,
            double? techPatchCutoff,
            bool excludeTombstonedTechFromBaseline,
            bool suppressSuspiciousDrawdownWarnings,
            bool authoritativeReduction,
            double? deadlineClockUT)
        {
            // LedgerTrace Tier-A: open the recalc burst so every trace line emitted by
            // the Patch* sites below shares one grep-sliceable recalcSeq. No-op when the
            // tracer is off. This is the single recalc-scoped lifecycle call; the
            // patch-deferral gate returns BEFORE this method, so a deferred recalc opens
            // no burst and emits nothing.
            LedgerTrace.BeginRecalc();

            // KSP state mutations (PostWalk already called by engine). Repeatable
            // Records* nodes only rebuild strictly from ledger-backed thresholds on
            // rewind-style cutoff walks; normal same-branch recalculations preserve the
            // live in-tier best value because that finer-grained partial progress is not
            // persisted in the ledger.
            kerbalsModule.ApplyToRoster(HighLogic.CurrentGame?.CrewRoster);
            // #559: tech-tree patching is gated on a supplied cutoff, NOT on being a rewind.
            // Live unlocks that happened after the latest captured baseline are not replayed
            // by any ledger action, so a cutoff-less (techPatchCutoff == null) patch would
            // clobber them — hence the gate. But the gate is `techPatchCutoff.HasValue`, and
            // THREE families supply one, only the first of which is time travel:
            //   * RecalculateAndPatch(utCutoff)                  — the rewind paths;
            //   * RecalculateAndPatchAfterTombstones             — post-supersede refresh,
            //     and only when the pass retired a ScienceSpending row;
            //   * RecalculateAndPatchForCurrentTimelineUT        — ORDINARY CAREER PLAY. It
            //     passes techPatchCutoff unconditionally and is reached from ksp-load,
            //     ksc-spending, strategy-conversion, ksc-science, recovery-kerbal-xp,
            //     vessel-rollout, vessel-recovery, the KSC ledger cursor, and every
            //     live-timeline event whose committed timeline extends into the future.
            // Do not restate "rewind-only" here: an earlier reader trusted that wording and
            // mis-scoped a live defect (UNAFFORDABLE-SCIENCE-SPENDING-SILENTLY-RE-LOCKS-A-TECH-NODE)
            // to time travel. The null branch no-ops through PatchTechTree's null-target guard
            // and runs the ADD-ONLY committed-unlock pass instead, capped at the live clock:
            // the KSC ledger cursor takes a cutoff-less walk when the clock passes the LAST
            // committed row, and a ScienceSpending row there must still reach stock
            // (KspStatePatcher.PlanCommittedTechUnlocksForPatch).
            HashSet<string> targetTechIds = null;
            double? techBaselineUt = null;
            HashSet<string> baselineTechExclusions = null;
            Dictionary<string, KspStatePatcher.UnaffordableUnlockDrop> unaffordableTechDrops = null;
            List<string> committedTechUnlockIds = null;
            double? committedTechUnlockLiveUt = null;
            if (techPatchCutoff.HasValue)
            {
                if (excludeTombstonedTechFromBaseline)
                    baselineTechExclusions = BuildTombstonedScienceSpendingNodeIds();

                targetTechIds = KspStatePatcher.BuildTargetTechIdsForPatch(
                    GameStateStore.Baselines,
                    actions,
                    techPatchCutoff,
                    baselineTechExclusions,
                    out unaffordableTechDrops);
                techBaselineUt = KspStatePatcher.GetSelectedTechBaselineUt(
                    GameStateStore.Baselines,
                    techPatchCutoff);
                ParsekLog.Verbose(Tag,
                    "RecalculateAndPatch: tech-tree patch enabled " +
                    $"(walkCutoff={(utCutoff.HasValue ? utCutoff.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "null")}, " +
                    $"techCutoff={techPatchCutoff.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}, " +
                    $"baselineUt={(techBaselineUt.HasValue ? techBaselineUt.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "null")}, " +
                    $"baselineTechExclusions={(baselineTechExclusions == null ? "0" : baselineTechExclusions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))}, " +
                    $"targetCount={(targetTechIds == null ? "null" : targetTechIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))}, " +
                    $"unaffordableTechDrops={(unaffordableTechDrops == null ? "null" : unaffordableTechDrops.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))})");
            }
            else
            {
                double liveUT = CommittedFutureIndexCache.CurrentUT();
                committedTechUnlockIds = KspStatePatcher.PlanCommittedTechUnlocksForPatch(
                    actions,
                    techPatchCutoff,
                    liveUT,
                    RecordingStore.RewindUTAdjustmentPending,
                    out string unlockSkipReason);
                if (committedTechUnlockIds != null)
                    committedTechUnlockLiveUt = liveUT;
                ParsekLog.Verbose(Tag,
                    "RecalculateAndPatch: no cutoff supplied — skipping tech-tree patch to preserve live unlocks; " +
                    (committedTechUnlockIds == null
                        ? $"committed tech unlocks skipped (reason={unlockSkipReason}, " +
                          $"liveUT={liveUT.ToString("R", CultureInfo.InvariantCulture)})"
                        : $"committed tech unlocks add-only at liveUT={liveUT.ToString("R", CultureInfo.InvariantCulture)} " +
                          $"(count={committedTechUnlockIds.Count.ToString(CultureInfo.InvariantCulture)}, " +
                          $"ids=[{KspStatePatcher.ComposeBoundedIdentitySample(committedTechUnlockIds, 10)}])"));
            }

            // Committed hires whose funds row the stock pool reference still counts as not
            // applied: the ones this pass's funds patch applies are made below.
            List<GameAction> unappliedHireRows =
                CollectHireRowsNotAppliedByFunds(StockPoolReference.Funds, actions);

            KspStatePatcher.PatchAll(scienceModule, fundsModule, reputationModule,
                milestonesModule, facilitiesModule, contractsModule,
                targetTechIds,
                authoritativeRepeatableRecordState: authoritativeRepeatableRecordState,
                techUtCutoff: techPatchCutoff,
                techBaselineUt: techBaselineUt,
                suppressSuspiciousDrawdownWarnings: suppressSuspiciousDrawdownWarnings,
                authoritativeReduction: authoritativeReduction,
                unaffordableTechDrops: unaffordableTechDrops,
                strategies: strategiesModule,
                partPurchaseActions: actions,
                walkUtCutoff: utCutoff,
                committedTechUnlockIds: committedTechUnlockIds,
                committedTechUnlockLiveUt: committedTechUnlockLiveUt,
                deadlineClockUT: deadlineClockUT);

            // A hire the clock reached was never made in stock (the load put the kerbal back
            // among the applicants); make it now, uncharged, in the pass whose funds patch
            // takes its cost. A skipped funds patch leaves the hire and its charge both unmade
            // until a later pass applies the row, crossing or not.
            var crossing = KspStatePatcher.ActiveClockCrossing;
            List<string> hiresToMake = ResolveCommittedHiresToMake(
                crossing, unappliedHireRows, StockPoolReference.Funds);
            if (crossing != null && crossing.CrossedHireKerbals.Count > 0 && !crossing.FundsApplied)
            {
                ParsekLog.Warn(Tag,
                    $"Clock-crossing hire(s) not made: the funds patch did not apply the reached rows " +
                    $"({(crossing.NoFundsPool ? "no funds pool" : "skipped; the pass whose funds patch applies them makes them")}); " +
                    $"kerbals=[{string.Join(", ", crossing.CrossedHireKerbals.ToArray())}]");
            }
            if (hiresToMake.Count > 0)
            {
                KerbalsModule.PromoteReachedCommittedHires(
                    HighLogic.CurrentGame?.CrewRoster, hiresToMake);
            }

            // A dismissal the clock reached (design section 9.12.1): a load back in time put
            // the kerbal back on the crew list; make him an applicant again, once. After the
            // hires, so a hire and a later dismissal both reached in one pass end with him
            // dismissed.
            ApplyReachedCommittedDismissals(actions, kerbalsModule);

            // LedgerTrace Tier-A: emit ONE structural snapshot per recalc, here (after
            // PatchAll), never inside a Patch* (that would emit 7x). Built from data
            // already in hand: module getters for the three pools, the facilities
            // collection for a compact name:level list, the local targetTechIds set, the
            // active-contract count, the techPatchCutoff, and authoritativeReduction. We
            // read module getters / locals, never Ledger.Actions or
            // RecordingStore.CommittedRecordings directly (ERS/ELS routing rule).
            if (LedgerTrace.IsEnabled)
            {
                // cutoff = the walk cutoff (utCutoff), the UT this recalc was scoped to
                // (null on a live walk); NOT techPatchCutoff, which is the narrower
                // tech-tree-patch-only cutoff.
                LedgerTrace.EmitStructural(BuildLedgerStructuralSnapshot(
                    targetTechIds, utCutoff, authoritativeReduction));
            }
        }

        /// <summary>
        /// The live half of the dismissal pass: plans against the kerbal walk's clock (raised to
        /// the held-through UT) and puts each named kerbal back among the applicants.
        /// </summary>
        private static void ApplyReachedCommittedDismissals(
            IReadOnlyList<GameAction> actions, KerbalsModule kerbals)
        {
            if (!HasDismissalRow(actions))
                return;
            double clockUT = kerbals != null ? kerbals.WalkClockUT : double.NaN;
            string skipReason;
            List<string> names = PlanReachedCommittedDismissals(
                actions, IsCommittedRowRecording, clockUT, LedgerHeldWindow.HeldThroughUT,
                RecordingStore.RewindUTAdjustmentPending, out skipReason);
            if (names == null)
            {
                ParsekLog.VerboseOnChange(Tag, "dismissal-pass-skip", skipReason,
                    "Committed dismissals not applied this pass: reason=" + skipReason +
                    " walkClockUT=" + clockUT.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (names.Count > 0)
                KerbalsModule.ApplyReachedCommittedDismissals(HighLogic.CurrentGame?.CrewRoster, names);
        }

        private static bool HasDismissalRow(IReadOnlyList<GameAction> actions)
        {
            if (actions == null) return false;
            for (int i = 0; i < actions.Count; i++)
                if (actions[i] != null && actions[i].Type == GameActionType.KerbalDismissal)
                    return true;
            return false;
        }

        /// <summary>A row counts as committed when it has no recording (a Space Center row) or
        /// its recording is in the effective recording set.</summary>
        private static bool IsCommittedRowRecording(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return true;
            var ers = EffectiveState.ComputeERS();
            if (ers == null) return false;
            for (int i = 0; i < ers.Count; i++)
                if (ers[i] != null && string.Equals(ers[i].RecordingId, recordingId, StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>
        /// Pure: the dismissal pass's plan, or null when it must not run (a rewind's UT
        /// adjustment is pending, or the clock is not a usable UT; <paramref name="skipReason"/>
        /// says which). The ledger's now is <paramref name="clockUT"/> raised to
        /// <paramref name="heldThroughUT"/> (<see cref="LedgerHeldWindow.ApplyHeldFloor"/>).
        /// </summary>
        internal static List<string> PlanReachedCommittedDismissals(
            IReadOnlyList<GameAction> actions, Func<string, bool> isCommittedRecording,
            double clockUT, double heldThroughUT, bool rewindUtAdjustmentPending, out string skipReason)
        {
            if (rewindUtAdjustmentPending)
            {
                skipReason = DismissalSkipRewindPending;
                return null;
            }
            if (double.IsNaN(clockUT) || double.IsInfinity(clockUT) || !IsCurrentUtReadyForCutoff(clockUT))
            {
                skipReason = DismissalSkipClockNotReady;
                return null;
            }
            skipReason = null;
            return ResolveCommittedDismissalsToMake(
                actions, isCommittedRecording, LedgerHeldWindow.ApplyHeldFloor(clockUT, heldThroughUT));
        }

        /// <summary>
        /// Pure: the kerbals the dismissal pass puts back among the applicants at
        /// <paramref name="nowUT"/>. Per kerbal name, the latest committed roster row at or
        /// before now (walk order) decides: a <see cref="GameActionType.KerbalDismissal"/>
        /// names him; a row that puts a kerbal of that name on the roster
        /// (<see cref="KerbalsModule.TryGetRosterCreatedKerbalName"/>: a hire, a rescue, a
        /// stand-in) does not. Stock reuses names: a dismissed applicant can expire and the
        /// name go to a later rescued kerbal or stand-in, who must not be demoted. A row tagged to a recording outside the committed set
        /// (<paramref name="isCommittedRecording"/>; null treats every tagged row as not
        /// committed) is not counted. Ordered by kerbal name, without repeats.
        /// </summary>
        internal static List<string> ResolveCommittedDismissalsToMake(
            IReadOnlyList<GameAction> actions, Func<string, bool> isCommittedRecording, double nowUT)
        {
            var latest = new Dictionary<string, GameAction>(StringComparer.Ordinal);
            if (actions != null)
            {
                for (int i = 0; i < actions.Count; i++)
                {
                    GameAction a = actions[i];
                    if (a == null || string.IsNullOrEmpty(a.KerbalName)) continue;
                    string joined;
                    if (a.Type != GameActionType.KerbalDismissal
                        && !KerbalsModule.TryGetRosterCreatedKerbalName(a, out joined))
                        continue;
                    if (a.UT > nowUT) continue;
                    if (!string.IsNullOrEmpty(a.RecordingId)
                        && (isCommittedRecording == null || !isCommittedRecording(a.RecordingId)))
                        continue;
                    GameAction held;
                    if (!latest.TryGetValue(a.KerbalName, out held)
                        || RecalculationEngine.CompareWalkOrder(a, held) > 0)
                        latest[a.KerbalName] = a;
                }
            }
            var names = new List<string>();
            foreach (var kv in latest)
                if (kv.Value.Type == GameActionType.KerbalDismissal)
                    names.Add(kv.Key);
            names.Sort(StringComparer.Ordinal);
            return names;
        }

        /// <summary>
        /// The committed KerbalHire rows of <paramref name="actions"/> whose funds row
        /// <paramref name="funds"/> counts as not applied (a NaN effect: the last funds patch
        /// saw it ahead of its cutoff). Empty when the reference holds none. A hire the walk
        /// already owns (design 3.15 step 6) is left out: it is never charged, so it makes
        /// nobody.
        /// </summary>
        internal static List<GameAction> CollectHireRowsNotAppliedByFunds(
            StockPoolReferenceState funds, IList<GameAction> actions)
        {
            var rows = new List<GameAction>();
            if (funds == null || !funds.Initialized || funds.Counted.Count == 0 || actions == null)
                return rows;
            HashSet<GameAction> ownedPurchases = null;
            for (int i = 0; i < actions.Count; i++)
            {
                GameAction a = actions[i];
                if (a == null || a.Type != GameActionType.KerbalHire
                    || string.IsNullOrEmpty(a.ActionId) || string.IsNullOrEmpty(a.KerbalName))
                    continue;
                if (ownedPurchases == null)
                    ownedPurchases = PurchaseStateRule.CollectAlreadyOwned(actions as IReadOnlyList<GameAction> ?? new List<GameAction>(actions));
                if (ownedPurchases.Contains(a))
                    continue;
                CountedRow counted;
                if (funds.Counted.TryGetValue(a.ActionId, out counted) && double.IsNaN(counted.Effect))
                    rows.Add(a);
            }
            return rows;
        }

        /// <summary>
        /// Pure: the kerbals to hire after one pass. A clock crossing's reached hires when its
        /// funds patch applied them, plus every row
        /// of <paramref name="unappliedBefore"/> that <paramref name="fundsAfter"/> now counts
        /// as applied: a hire whose crossing skipped the funds patch is made by the later pass
        /// that takes its cost. Ordered, without repeats.
        /// </summary>
        internal static List<string> ResolveCommittedHiresToMake(
            KspStatePatcher.ClockCrossing crossing, IList<GameAction> unappliedBefore,
            StockPoolReferenceState fundsAfter)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (crossing != null && crossing.FundsApplied)
            {
                for (int i = 0; i < crossing.CrossedHireKerbals.Count; i++)
                {
                    string name = crossing.CrossedHireKerbals[i];
                    if (!string.IsNullOrEmpty(name) && seen.Add(name))
                        names.Add(name);
                }
            }
            if (unappliedBefore != null && fundsAfter != null)
            {
                int applied = 0;
                for (int i = 0; i < unappliedBefore.Count; i++)
                {
                    GameAction a = unappliedBefore[i];
                    CountedRow counted;
                    if (a == null || !fundsAfter.Counted.TryGetValue(a.ActionId, out counted)
                        || double.IsNaN(counted.Effect))
                        continue;
                    applied++;
                    if (seen.Add(a.KerbalName))
                        names.Add(a.KerbalName);
                }
                if (applied > 0)
                    ParsekLog.Info(Tag,
                        $"Committed hire row(s) applied by this funds patch: count={applied.ToString(CultureInfo.InvariantCulture)} " +
                        $"crossing={(crossing != null)}");
            }
            return names;
        }

        /// <summary>
        /// Builds the LedgerTrace Tier-A structural snapshot from the orchestrator's
        /// recalc modules + the per-recalc locals already in hand. Reads only module
        /// getters and the supplied locals (no direct Ledger.Actions /
        /// RecordingStore.CommittedRecordings read — ERS/ELS routing rule). The facility
        /// list is a compact, sorted name:level string so the structural line stays
        /// grep-stable across recalcs.
        /// </summary>
        private static LedgerTrace.LedgerStructuralSnapshot BuildLedgerStructuralSnapshot(
            HashSet<string> targetTechIds,
            double? cutoff,
            bool authoritativeReduction)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;

            double funds = fundsModule != null ? fundsModule.GetAvailableFunds() : double.NaN;
            double science = scienceModule != null ? scienceModule.GetAvailableScience() : double.NaN;
            double rep = reputationModule != null ? reputationModule.GetRunningRep() : double.NaN;

            string facilities = string.Empty;
            if (facilitiesModule != null)
            {
                var all = facilitiesModule.GetAllFacilities();
                if (all != null && all.Count > 0)
                {
                    var entries = new List<string>(all.Count);
                    foreach (var kvp in all)
                        entries.Add(kvp.Key + ":" + kvp.Value.Level.ToString(ic));
                    entries.Sort(StringComparer.Ordinal);
                    facilities = string.Join(",", entries.ToArray());
                }
            }

            int contractCount = 0;
            if (contractsModule != null)
            {
                var activeIds = contractsModule.GetActiveContractIds();
                contractCount = activeIds != null ? activeIds.Count : 0;
            }

            return new LedgerTrace.LedgerStructuralSnapshot
            {
                Funds = funds,
                Science = science,
                Reputation = rep,
                Facilities = facilities,
                // Count of nodes THIS patch targeted (the local targetTechIds set), NOT
                // the whole career tree. Null on a non-cutoff / live recalc (no tech
                // cutoff scoped) -> 0; do not read 0 here as "no tech unlocked".
                TargetTechNodes = targetTechIds != null ? targetTechIds.Count : 0,
                Contracts = contractCount,
                Cutoff = cutoff,
                AuthoritativeReduction = authoritativeReduction
            };
        }

        internal static HashSet<string> BuildTombstonedFacilityIdsForPatch()
        {
            var tombstonedActionIds = BuildTombstonedActionIds();
            if (tombstonedActionIds == null)
                return null;

            var facilityIds = new HashSet<string>(StringComparer.Ordinal);
            var source = Ledger.Actions;
            for (int i = 0; i < source.Count; i++)
            {
                var action = source[i];
                if (action == null ||
                    string.IsNullOrEmpty(action.ActionId) ||
                    string.IsNullOrEmpty(action.FacilityId))
                {
                    continue;
                }

                switch (action.Type)
                {
                    case GameActionType.FacilityUpgrade:
                    case GameActionType.FacilityDestruction:
                    case GameActionType.FacilityRepair:
                        if (tombstonedActionIds.Contains(action.ActionId))
                            facilityIds.Add(action.FacilityId);
                        break;
                }
            }

            return facilityIds.Count == 0 ? null : facilityIds;
        }

        internal static HashSet<Guid> BuildTombstonedContractGuidsForPatch()
        {
            var tombstonedActionIds = BuildTombstonedActionIds();
            if (tombstonedActionIds == null)
                return null;

            var contractIds = new HashSet<Guid>();
            var source = Ledger.Actions;
            for (int i = 0; i < source.Count; i++)
            {
                var action = source[i];
                if (action == null ||
                    string.IsNullOrEmpty(action.ActionId) ||
                    string.IsNullOrEmpty(action.ContractId) ||
                    !tombstonedActionIds.Contains(action.ActionId))
                {
                    continue;
                }

                switch (action.Type)
                {
                    case GameActionType.ContractAccept:
                    case GameActionType.ContractComplete:
                    case GameActionType.ContractFail:
                    case GameActionType.ContractCancel:
                        Guid contractGuid;
                        if (Guid.TryParse(action.ContractId, out contractGuid))
                            contractIds.Add(contractGuid);
                        break;
                }
            }

            return contractIds.Count == 0 ? null : contractIds;
        }

        /// <summary>
        /// Pure core of <see cref="BuildContractReinstateCutoffsForPatch"/>: folds one
        /// (contract guid, retiring-recording StartUT) pair into the running per-guid
        /// MINIMUM. The minimum is correct because a contract retired by two different
        /// merges must be rebuilt at the EARLIEST point the timeline rewound to — a later
        /// cutoff would reinstate progress a subsequent rewind already discarded.
        /// </summary>
        internal static void FoldContractReinstateCutoff(
            Dictionary<Guid, double> cutoffs, Guid contractGuid, double retiringStartUt)
        {
            if (cutoffs == null || double.IsNaN(retiringStartUt))
                return;

            double existing;
            if (cutoffs.TryGetValue(contractGuid, out existing))
            {
                if (retiringStartUt < existing)
                    cutoffs[contractGuid] = retiringStartUt;
            }
            else
            {
                cutoffs[contractGuid] = retiringStartUt;
            }
        }

        /// <summary>
        /// Per-contract "rebuild it as of THIS UT" cutoffs for
        /// <c>KspStatePatcher.PatchContracts</c>.
        ///
        /// <para>
        /// A contract reaches the rebuild loop because a merge tombstoned its terminal
        /// action while its accept survives. The state it should come back as is the state
        /// it had when the timeline rewound — the StartUT of the recording whose merge
        /// wrote the tombstone, which IS the rewind point. Without this the rebuild fell
        /// back to the zero-progress accept-time snapshot, so a half-finished contract came
        /// back from a merge with all of its parameter progress erased.
        /// </para>
        ///
        /// Returns null when no tombstone maps to a contract, which selects the historical
        /// accept-time behavior everywhere.
        ///
        /// <para>
        /// [ERS-exempt] Reads the RAW ledger and the RAW committed list for the same reason
        /// <see cref="BuildTombstonedContractGuidsForPatch"/> does: tombstones ARE the ELS
        /// filter, so routing this scan through ELS would hide exactly the actions it exists
        /// to find, and the retiring recording is resolved by id (a data lookup, not a
        /// visibility walk).
        /// </para>
        /// </summary>
        internal static Dictionary<Guid, double> BuildContractReinstateCutoffsForPatch()
        {
            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario) ||
                scenario.LedgerTombstones == null ||
                scenario.LedgerTombstones.Count == 0)
            {
                return null;
            }

            // actionId -> contract guid, for contract-shaped actions only.
            var source = Ledger.Actions;
            var contractGuidByActionId = MapContractGuidsByActionId(source);

            if (contractGuidByActionId.Count == 0)
                return null;

            var startUtByRecordingId = new Dictionary<string, double>(StringComparer.Ordinal);
            var cutoffs = new Dictionary<Guid, double>();
            for (int i = 0; i < scenario.LedgerTombstones.Count; i++)
            {
                var tombstone = scenario.LedgerTombstones[i];
                if (tombstone == null ||
                    string.IsNullOrEmpty(tombstone.ActionId) ||
                    string.IsNullOrEmpty(tombstone.RetiringRecordingId))
                {
                    continue;
                }

                Guid contractGuid;
                if (!contractGuidByActionId.TryGetValue(tombstone.ActionId, out contractGuid))
                    continue;

                double startUt = ResolveContractReinstateStartUt(tombstone, startUtByRecordingId);

                FoldContractReinstateCutoff(cutoffs, contractGuid, startUt);
            }

            return cutoffs.Count == 0 ? null : cutoffs;
        }

        /// <summary>
        /// The first step of <see cref="BuildContractReinstateCutoffsForPatch"/>: each
        /// contract-shaped row's ActionId mapped to its parsed contract guid (rows without an
        /// id, a contract id or a parseable guid are left out; a repeated id keeps the last).
        /// </summary>
        internal static Dictionary<string, Guid> MapContractGuidsByActionId(
            IReadOnlyList<GameAction> source)
        {
            var contractGuidByActionId = new Dictionary<string, Guid>(StringComparer.Ordinal);
            for (int i = 0; i < source.Count; i++)
            {
                var action = source[i];
                if (action == null ||
                    string.IsNullOrEmpty(action.ActionId) ||
                    string.IsNullOrEmpty(action.ContractId))
                {
                    continue;
                }

                switch (action.Type)
                {
                    case GameActionType.ContractAccept:
                    case GameActionType.ContractComplete:
                    case GameActionType.ContractFail:
                    case GameActionType.ContractCancel:
                        Guid parsed;
                        if (Guid.TryParse(action.ContractId, out parsed))
                            contractGuidByActionId[action.ActionId] = parsed;
                        break;
                }
            }

            return contractGuidByActionId;
        }

        /// <summary>
        /// The cutoff UT one tombstone gives in <see cref="BuildContractReinstateCutoffsForPatch"/>:
        /// the StartUT of its retiring recording (looked up once per recording id and cached
        /// in <paramref name="startUtByRecordingId"/>), else the tombstone's own UT.
        /// </summary>
        internal static double ResolveContractReinstateStartUt(
            LedgerTombstone tombstone, Dictionary<string, double> startUtByRecordingId)
        {
            double startUt;
            if (!startUtByRecordingId.TryGetValue(tombstone.RetiringRecordingId, out startUt))
            {
                startUt = double.NaN;
                var committed = RecordingStore.CommittedRecordings;
                for (int r = 0; r < committed.Count; r++)
                {
                    var rec = committed[r];
                    if (rec != null &&
                        string.Equals(rec.RecordingId, tombstone.RetiringRecordingId, StringComparison.Ordinal))
                    {
                        startUt = rec.StartUT;
                        break;
                    }
                }
                startUtByRecordingId[tombstone.RetiringRecordingId] = startUt;
            }

            if (double.IsNaN(startUt))
            {
                // The retiring recording is gone (discarded tree, pruned attempt). Fall
                // back to the tombstone's own UT — the merge clock, which is at or after
                // the rewind in every production path, so the selection stays bounded.
                startUt = tombstone.UT;
            }

            return startUt;
        }

        private static HashSet<string> BuildTombstonedScienceSpendingNodeIds()
        {
            var tombstonedActionIds = BuildTombstonedActionIds();
            if (tombstonedActionIds == null)
                return null;

            var excludedTechIds = new HashSet<string>(StringComparer.Ordinal);
            var source = Ledger.Actions;
            for (int i = 0; i < source.Count; i++)
            {
                var action = source[i];
                if (action == null ||
                    action.Type != GameActionType.ScienceSpending ||
                    string.IsNullOrEmpty(action.ActionId) ||
                    string.IsNullOrEmpty(action.NodeId))
                {
                    continue;
                }

                if (tombstonedActionIds.Contains(action.ActionId))
                    excludedTechIds.Add(action.NodeId);
            }

            return excludedTechIds.Count == 0 ? null : excludedTechIds;
        }

        private static HashSet<string> BuildTombstonedActionIds()
        {
            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario) ||
                scenario.LedgerTombstones == null ||
                scenario.LedgerTombstones.Count == 0)
            {
                return null;
            }

            var tombstonedActionIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < scenario.LedgerTombstones.Count; i++)
            {
                var tombstone = scenario.LedgerTombstones[i];
                if (tombstone == null || string.IsNullOrEmpty(tombstone.ActionId))
                    continue;
                tombstonedActionIds.Add(tombstone.ActionId);
            }

            if (tombstonedActionIds.Count == 0)
                return null;
            return tombstonedActionIds;
        }

        internal static bool HasActionsAfterUT(double ut)
        {
            return TryGetNextActionUTAfter(ut, out _);
        }

        /// <summary>
        /// True when <paramref name="ut"/> is a real, initialized game-clock value that can
        /// safely be used as a current-timeline cutoff. During a cold
        /// <c>ScenarioModule.OnLoad</c> (and the deferred-seed coroutine that may run before
        /// the universe clock is set up), <see cref="Planetarium.GetUniversalTime"/> returns
        /// <c>0</c> before time is initialized. Cutting the committed ledger off at UT=0 then
        /// filters out an entire established career as if it were "future" (BUG-F: cold-loading
        /// a career wiped funds to the starting seed and science to 0). A non-positive UT means
        /// the clock is not ready, so callers must fall back to the safe full replay
        /// (<see cref="RecalculateAndPatch"/>, cutoffUT=null) which replays every committed
        /// action. A genuine career clock is always well above 0, so this never blocks a real
        /// post-rewind / future-timeline cutoff (those carry a valid positive UT).
        /// </summary>
        internal static bool IsCurrentUtReadyForCutoff(double ut)
        {
            return ut > 0.0;
        }

        internal static bool TryGetNextActionUTAfter(double ut, out double nextActionUT)
        {
            nextActionUT = double.PositiveInfinity;
            bool found = false;
            for (int i = 0; i < Ledger.Actions.Count; i++)
            {
                var action = Ledger.Actions[i];
                if (action == null)
                    continue;
                if (RecalculationEngine.IsSeedType(action.Type))
                    continue;
                if (action.UT <= ut)
                    continue;

                if (!found || action.UT < nextActionUT)
                {
                    nextActionUT = action.UT;
                    found = true;
                }
            }

            return found;
        }

        private static string GetKspPatchDeferralReason()
        {
            bool hasActiveUncommittedTree = GameStateRecorder.HasActiveUncommittedTree();
            bool hasLiveRecorder = GameStateRecorder.HasLiveRecorder();
            bool hasPendingTree = PendingTreeSlot.HasPendingTree;
            if (!hasActiveUncommittedTree && !hasPendingTree)
                return null;

            string reason = null;
            if (hasLiveRecorder)
                reason = "live recorder active";
            else if (hasActiveUncommittedTree)
                reason = "active uncommitted flight tree";

            if (hasPendingTree)
            {
                string treeName = PendingTreeSlot.PendingTree?.TreeName;
                string pendingReason = string.IsNullOrEmpty(treeName)
                    ? $"pending tree state={PendingTreeSlot.PendingTreeStateValue}"
                    : $"pending tree '{treeName}' state={PendingTreeSlot.PendingTreeStateValue}";
                reason = string.IsNullOrEmpty(reason)
                    ? pendingReason
                    : reason + "; " + pendingReason;
            }

            return reason;
        }

        /// <summary>
        /// Rebuilds the committed science subjects dictionary via
        /// <see cref="GameStateStore.RebuildCommittedScienceSubjects"/>
        /// from the full surviving ledger action set. This is intentionally separate
        /// from the live <see cref="ScienceModule"/> state: cutoff walks keep that
        /// module at the current UT for KSP patching, while this persisted cache is
        /// used as a future load-recovery source of truth.
        /// </summary>
        private static void RebuildCommittedScienceFromSurvivingLedger(
            IReadOnlyList<GameAction> actions)
        {
            int scienceActionCount;
            var pairs = BuildCommittedScienceSubjectCredits(actions, out scienceActionCount);
            GameStateStore.RebuildCommittedScienceSubjects(pairs);

            int actionCount = actions == null ? 0 : actions.Count;
            ParsekLog.Verbose(Tag,
                "RebuildCommittedScienceFromSurvivingLedger: rebuilt committedScienceSubjects " +
                $"from surviving ledger (subjects={pairs.Count}, scienceActions={scienceActionCount}, " +
                $"actions={actionCount})");
        }

        private struct CommittedScienceCreditState
        {
            public double CreditedTotal;
            public double MaxValue;
        }

        internal static List<KeyValuePair<string, float>> BuildCommittedScienceSubjectCredits(
            IReadOnlyList<GameAction> actions,
            out int scienceActionCount)
        {
            scienceActionCount = 0;
            var pairs = new List<KeyValuePair<string, float>>();
            if (actions == null || actions.Count == 0)
                return pairs;

            var scienceActions = new List<GameAction>();
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action == null ||
                    action.Type != GameActionType.ScienceEarning ||
                    string.IsNullOrEmpty(action.SubjectId) ||
                    action.ScienceAwarded <= 0f)
                {
                    continue;
                }

                scienceActions.Add(action);
            }

            scienceActionCount = scienceActions.Count;
            if (scienceActionCount == 0)
                return pairs;

            var sorted = RecalculationEngine.SortActions(scienceActions);
            var credits = new Dictionary<string, CommittedScienceCreditState>(
                StringComparer.Ordinal);

            for (int i = 0; i < sorted.Count; i++)
            {
                var action = sorted[i];
                string subjectId = action.SubjectId;

                CommittedScienceCreditState state;
                if (!credits.TryGetValue(subjectId, out state))
                {
                    state = new CommittedScienceCreditState
                    {
                        CreditedTotal = 0.0,
                        MaxValue = action.SubjectMaxValue
                    };
                }

                // Monotonic max-value assumption: a KSP science subject's cap should
                // stay stable or only increase across surviving ledger rows. Keep the
                // largest observed cap so older/migration rows with smaller caps cannot
                // reduce headroom that was available to later committed science.
                if (action.SubjectMaxValue > state.MaxValue)
                    state.MaxValue = action.SubjectMaxValue;

                double headroom = state.MaxValue - state.CreditedTotal;
                if (headroom < 0.0)
                    headroom = 0.0;

                double effectiveScience = Math.Min((double)action.ScienceAwarded, headroom);
                if (effectiveScience < 0.0)
                    effectiveScience = 0.0;

                state.CreditedTotal += effectiveScience;
                credits[subjectId] = state;
            }

            foreach (var kvp in credits)
            {
                if (kvp.Value.CreditedTotal > 0.0)
                    pairs.Add(new KeyValuePair<string, float>(
                        kvp.Key,
                        (float)kvp.Value.CreditedTotal));
            }

            return pairs;
        }
    }
}
