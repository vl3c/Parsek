using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Which entry branch the consume site chose (or refused to enter) for
    /// an armed <see cref="StockActionIntentMarker"/>. The three <c>Started*</c>
    /// values represent the three branches in plan §"Behavior by Entry Path";
    /// the <c>Refused_*</c> values represent the staleness / target-mismatch
    /// guards that clear the marker without starting a segment;
    /// <see cref="NoIntent"/> is the common no-op path for `[` keyboard
    /// cycling, EVA boarding, docking, etc.
    /// </summary>
    internal enum SwitchSegmentEntryRoute
    {
        /// <summary>No marker armed at consume time.</summary>
        NoIntent = 0,

        /// <summary>Marker armed in a different AppDomain — cleared with
        /// reason <c>stale-cross-run</c>.</summary>
        Refused_StaleCrossRun = 1,

        /// <summary>Marker exceeded its TTL or UT regressed — cleared with
        /// reason <c>stale-intent-ttl</c> / <c>stale-intent-ut-regressed</c>.</summary>
        Refused_StaleIntent = 2,

        /// <summary>Marker targeted a different vessel than the one that just
        /// became active — cleared with reason <c>stale-target-mismatch</c>.</summary>
        Refused_TargetMismatch = 3,

        /// <summary>Same target vessel as an already-active session with the
        /// same focused PID — cleared with reason <c>duplicate-intent-same-target</c>.
        /// No new segment is started; no-op for rapid double-clicks.</summary>
        Refused_DuplicateSameTarget = 5,

        /// <summary>Consume requested from inside the missed-switch recovery
        /// path — cleared with reason <c>stale-cross-run</c> per plan §"Missed-switch recovery"
        /// to keep the existing fallback from becoming a second immediate-start source.</summary>
        Refused_MissedSwitchRecovery = 6,

        /// <summary>Consume site received a null <c>newVessel</c> argument
        /// (early scene-transition race). Cleared with reason
        /// <c>consume-null-vessel</c>. Distinct from <see cref="Refused_TargetMismatch"/>
        /// because the failure is "no vessel at all", not "wrong vessel".</summary>
        Refused_NullVessel = 7,

        /// <summary>Focused vessel is still <see cref="Vessel.Situations.PRELAUNCH"/>
        /// (sitting on the pad/runway). The switch-fly immediate-start is
        /// declined so the vessel follows the same auto-record-on-launch
        /// rules as any other on-pad vessel; the recorder starts on the
        /// PRELAUNCH->FLYING transition / first staging instead. Cleared
        /// with reason <c>prelaunch-defer-to-launch</c>.</summary>
        Refused_PrelaunchTarget = 8,

        /// <summary>Started a continuation segment under a committed-tree
        /// clone (plan §"Fly / Switch-To a committed spawned vessel").</summary>
        StartedCommittedSpawnedClone = 10,

        /// <summary>Started a continuation segment under a BG-tracked recording
        /// of the live activeTree (plan §"Goal 4").</summary>
        StartedBgMemberContinuation = 11,

        /// <summary>Started a standalone segment under a fresh tree for a
        /// vessel unrelated to any Parsek tree (plan §"Goal 5").</summary>
        StartedStandalone = 12,

        /// <summary>Goal-4 BG-lookup failed for any reason; consume fell
        /// through to start a standalone segment instead. Logged as
        /// <c>bg-lookup-failed-start-standalone</c>.</summary>
        FellThroughBgLookupFailed = 13,
    }

    /// <summary>
    /// Result returned from <c>ParsekFlight.TryConsumeStockActionIntent</c>.
    /// </summary>
    internal struct StockActionIntentConsumeResult
    {
        public SwitchSegmentEntryRoute Route;
        public string NewRecordingId;
        public string NewBranchPointId;
        public Guid SessionId;
        public string DiagnosticReason;

        public bool StartedSegment
        {
            get
            {
                switch (Route)
                {
                    case SwitchSegmentEntryRoute.StartedCommittedSpawnedClone:
                    case SwitchSegmentEntryRoute.StartedBgMemberContinuation:
                    case SwitchSegmentEntryRoute.StartedStandalone:
                    case SwitchSegmentEntryRoute.FellThroughBgLookupFailed:
                        return true;
                    default:
                        return false;
                }
            }
        }
    }

    /// <summary>
    /// Pure decision predicate for the consume site. Resolves the marker's
    /// staleness, the target-mismatch guard, and the duplicate-same-target /
    /// missed-switch-recovery short-circuits before the routing branches run.
    /// The branch selection itself (committed clone vs BG member vs standalone)
    /// lives in the live-side wrapper because it needs the activeTree plus
    /// committed-store lookups.
    ///
    /// <para>Exposed as a pure static method so unit tests can pin every
    /// pre-routing branch without driving the live KSP clock or Planetarium.</para>
    /// </summary>
    internal static class StockActionIntentConsumeDecision
    {
        /// <summary>
        /// Classification returned by <see cref="Evaluate"/>: every branch
        /// other than <see cref="Authorized"/> implies the caller should
        /// clear the marker with the matching reason and refuse to start
        /// a segment.
        /// </summary>
        internal enum Outcome
        {
            /// <summary>Marker is fresh, target matches, no session conflict
            /// — caller proceeds to branch routing.</summary>
            Authorized = 0,

            /// <summary>No marker armed — return <see cref="SwitchSegmentEntryRoute.NoIntent"/>
            /// without logging or clearing (no-op).</summary>
            NoIntent = 1,

            StaleCrossRun = 2,
            StaleIntentTtlExpired = 3,
            StaleIntentUtRegressed = 4,
            TargetMismatch = 5,
            DuplicateSameTarget = 7,
            MissedSwitchRecovery = 8,

            /// <summary>Focused vessel is still PRELAUNCH (on the pad). Defer
            /// the switch-fly start to the normal auto-record-on-launch
            /// trigger; clear the marker with <c>prelaunch-defer-to-launch</c>.</summary>
            PrelaunchDeferToLaunch = 9,
        }

        /// <summary>
        /// Maps the consume outcome to the clear-reason string handed to
        /// <c>ParsekScenario.ClearStockActionIntent</c>. Pure so tests can
        /// pin every branch's wire reason without instantiating the scenario.
        /// </summary>
        internal static string ClearReasonFor(Outcome outcome)
        {
            switch (outcome)
            {
                case Outcome.StaleCrossRun: return "stale-cross-run";
                case Outcome.StaleIntentTtlExpired: return "stale-intent-ttl";
                case Outcome.StaleIntentUtRegressed: return "stale-intent-ut-regressed";
                case Outcome.TargetMismatch: return "stale-target-mismatch";
                case Outcome.DuplicateSameTarget: return "duplicate-intent-same-target";
                case Outcome.MissedSwitchRecovery: return "stale-cross-run";
                case Outcome.PrelaunchDeferToLaunch: return "prelaunch-defer-to-launch";
                default: return null;
            }
        }

        /// <summary>
        /// Maps the consume outcome to the <see cref="SwitchSegmentEntryRoute"/>
        /// the caller returns for refusal branches. Authorized must be
        /// dispatched to the live-side routing logic instead; passing
        /// <see cref="Outcome.Authorized"/> here is a programmer error and
        /// returns <see cref="SwitchSegmentEntryRoute.NoIntent"/>.
        /// </summary>
        internal static SwitchSegmentEntryRoute RouteForRefusal(Outcome outcome)
        {
            switch (outcome)
            {
                case Outcome.NoIntent: return SwitchSegmentEntryRoute.NoIntent;
                case Outcome.StaleCrossRun: return SwitchSegmentEntryRoute.Refused_StaleCrossRun;
                case Outcome.StaleIntentTtlExpired:
                case Outcome.StaleIntentUtRegressed:
                    return SwitchSegmentEntryRoute.Refused_StaleIntent;
                case Outcome.TargetMismatch: return SwitchSegmentEntryRoute.Refused_TargetMismatch;
                case Outcome.DuplicateSameTarget: return SwitchSegmentEntryRoute.Refused_DuplicateSameTarget;
                case Outcome.MissedSwitchRecovery: return SwitchSegmentEntryRoute.Refused_MissedSwitchRecovery;
                case Outcome.PrelaunchDeferToLaunch: return SwitchSegmentEntryRoute.Refused_PrelaunchTarget;
                case Outcome.Authorized:
                default:
                    return SwitchSegmentEntryRoute.NoIntent;
            }
        }

        /// <summary>
        /// Pure decision predicate. Returns the classification the consume
        /// site should act on.
        /// </summary>
        /// <param name="marker">Pending intent marker. Null returns <see cref="Outcome.NoIntent"/>.</param>
        /// <param name="newVesselPersistentId">PID of the just-activated focused vessel.</param>
        /// <param name="currentProcessSessionId"><see cref="ParsekProcess.ProcessSessionId"/>
        /// at consume time.</param>
        /// <param name="currentRealtime"><see cref="UnityEngine.Time.realtimeSinceStartup"/>
        /// at consume time.</param>
        /// <param name="currentUT">Planetarium UT at consume time.</param>
        /// <param name="missedSwitchRecoveryInProgress">True when the consume is
        /// running inside the missed-switch recovery replay path; per plan
        /// §"Missed-switch recovery" the marker must be cleared with
        /// <c>stale-cross-run</c> rather than consumed.</param>
        /// <param name="activeSessionFocusedPid">Focused PID of the currently
        /// armed <see cref="SwitchSegmentSession"/> (0 = no active session).</param>
        /// <param name="targetIsPrelaunch">True when the just-activated focused
        /// vessel is still <see cref="Vessel.Situations.PRELAUNCH"/> (sitting on
        /// the pad/runway). Such a vessel must follow the normal
        /// auto-record-on-launch rules rather than starting a switch-fly
        /// segment immediately, so a fresh / matching marker is declined with
        /// <see cref="Outcome.PrelaunchDeferToLaunch"/>.</param>
        internal static Outcome Evaluate(
            StockActionIntentMarker marker,
            uint newVesselPersistentId,
            Guid currentProcessSessionId,
            float currentRealtime,
            double currentUT,
            bool missedSwitchRecoveryInProgress,
            uint activeSessionFocusedPid,
            bool targetIsPrelaunch)
        {
            if (marker == null)
                return Outcome.NoIntent;

            // MED 8 (PR #876 review): evaluate staleness BEFORE the missed-
            // switch recovery short-circuit. A stale-cross-run marker
            // arriving inside the recovery replay path is still a
            // stale-cross-run condition first; reporting MissedSwitchRecovery
            // loses the diagnostic signal that the marker was already stale.
            // The recovery refusal only fires when the marker would
            // otherwise have been Fresh.
            StockActionIntentStaleness staleness = StockActionIntentMarker.EvaluateStaleness(
                marker, currentProcessSessionId, currentRealtime, currentUT);
            switch (staleness)
            {
                case StockActionIntentStaleness.StaleCrossRun:
                    return Outcome.StaleCrossRun;
                case StockActionIntentStaleness.StaleIntentTtlExpired:
                    return Outcome.StaleIntentTtlExpired;
                case StockActionIntentStaleness.StaleIntentUtRegressed:
                    return Outcome.StaleIntentUtRegressed;
                case StockActionIntentStaleness.Fresh:
                default:
                    break;
            }

            if (missedSwitchRecoveryInProgress)
                return Outcome.MissedSwitchRecovery;

            // Target-mismatch: a marker armed for vessel A while the active
            // vessel is vessel B is a diagnostic-relevant mis-target (player
            // clicked Switch-To on B while [/] cycling lands on A, or a mod
            // conflict races SetActiveVessel).
            if (marker.TargetVesselPersistentId != newVesselPersistentId)
                return Outcome.TargetMismatch;

            // Duplicate-same-target short-circuit: a session is already armed
            // for the same vessel as this marker targets, and the new active
            // vessel matches both. This is the rapid-double-click case (plan
            // §"Two rapid switches semantics → Same target vessel").
            if (activeSessionFocusedPid != 0u
                && activeSessionFocusedPid == marker.TargetVesselPersistentId
                && newVesselPersistentId == marker.TargetVesselPersistentId)
            {
                return Outcome.DuplicateSameTarget;
            }

            // PRELAUNCH defer: a vessel still sitting on the pad/runway has not
            // launched yet, so the stock Fly / Switch-To click must not start a
            // recording immediately. Decline here and let the normal
            // auto-record-on-launch trigger (PRELAUNCH->FLYING / first staging)
            // own the start, matching how any other on-pad vessel behaves. This
            // is placed after the staleness / target-mismatch / duplicate guards
            // so those diagnostic-relevant refusals keep precedence over the
            // routine defer.
            if (targetIsPrelaunch)
                return Outcome.PrelaunchDeferToLaunch;

            return Outcome.Authorized;
        }

        /// <summary>
        /// Renders an Info-level diagnostic line for the consume site to log
        /// alongside the clear. Pure so tests can pin the exact log format.
        /// </summary>
        internal static string FormatRefusalDiagnostic(
            Outcome outcome,
            StockActionIntentMarker marker,
            uint newVesselPersistentId)
        {
            string reason = ClearReasonFor(outcome) ?? "<unknown>";
            var ic = CultureInfo.InvariantCulture;
            if (marker == null)
            {
                return string.Format(ic,
                    "refused-no-marker: reason={0} newVesselPid={1}",
                    reason, newVesselPersistentId);
            }
            return string.Format(ic,
                "refused: reason={0} intentId={1} action={2} markerTargetPid={3} newVesselPid={4}",
                reason,
                marker.IntentId.ToString("D", ic),
                marker.Action,
                marker.TargetVesselPersistentId,
                newVesselPersistentId);
        }
    }
}
