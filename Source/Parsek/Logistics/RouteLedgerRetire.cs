using System.Collections.Generic;

namespace Parsek.Logistics
{
    /// <summary>
    /// Pure, Unity-free predicate + list filter for the Rec-1 fix
    /// (logistics &lt;-&gt; time-rewind determinism; plan
    /// <c>docs/dev/plans/fix-logistics-rewind-determinism.md</c>; report
    /// <c>docs/dev/research/logistics-time-rewind-compat-report.md</c>).
    ///
    /// <para><b>The bug.</b> Route dispatch/delivery/credit ledger rows are
    /// FREE-STANDING (they carry a <see cref="GameAction.RouteId"/> but never a
    /// <see cref="GameAction.RecordingId"/>), so the supersede/tombstone walk —
    /// which gates on <c>RecordingId</c> — can never retire them. On a
    /// Rewind-to-Separation the <c>ReconciliationBundle</c> PRESERVES the full
    /// pre-rewind <c>Ledger.Actions</c> while the live world AND the route counters
    /// revert via the <c>.sfs</c>. The UT-blind dispatch dedup then reproduces the
    /// counter-keyed cycleId and SUPPRESSES the re-flown cycle that would re-apply
    /// the live cargo, so the player is charged but the goods are never
    /// re-delivered ("funds spent, no goods").</para>
    ///
    /// <para><b>The fix.</b> At a successful rewind restore, DROP every free-standing
    /// route ledger row whose <c>UT</c> is strictly after the rewind cutoff (the
    /// loaded RewindPoint UT). The surviving ledger then matches the reverted world,
    /// and the live re-fly re-emits each cycle deterministically — re-charging funds
    /// (once) AND re-delivering physical cargo (once), with the dedup correctly
    /// seeing an empty slate for those cycles.</para>
    ///
    /// <para><b>Parity, not a new behavior.</b> The STOCK-revert path already drops
    /// these rows via <c>Ledger.PruneOrphanActionsAfterUT</c> (untagged orphan rows
    /// after a cutoff). Rewind-to-Separation is the ONLY revert that preserves them,
    /// because it routes through <c>ReconciliationBundle</c> (which deliberately
    /// snapshots the full ledger) instead of that prune. This helper restores parity:
    /// route rows revert on a rewind exactly as they already do on a stock revert,
    /// scoped to route action Types instead of "untagged".</para>
    ///
    /// <para><b>Determinism invariant.</b> The predicate gates on route action Type
    /// AND a non-empty <c>RouteId</c>, so it can ONLY ever remove route rows. Every
    /// non-route action (FundsEarning, ContractComplete, MilestoneAchievement,
    /// FundsInitial, ...) is invisible to the retire and flows through
    /// Capture/Restore/ComputeELS exactly as today — non-route ledger reconstruction
    /// stays byte-identical.</para>
    ///
    /// <para><b>ERS/ELS grep-gate:</b> takes the action / list BY PARAMETER and reads
    /// NEITHER <c>Ledger.Actions</c> NOR <c>.CommittedRecordings</c>, so it is not a
    /// grep-gate concern and needs no allowlist entry (the caller —
    /// <c>ReconciliationBundle</c>, already allowlisted — passes the list in).</para>
    /// </summary>
    internal static class RouteLedgerRetire
    {
        /// <summary>
        /// True for the seven free-standing route <see cref="GameActionType"/>
        /// members (values 23-29). Implemented as an explicit switch over the named
        /// members (NOT a numeric range) so that adding an unrelated action Type at
        /// 30+ can never silently widen the retire set.
        /// </summary>
        internal static bool IsRouteActionType(GameActionType t)
        {
            switch (t)
            {
                case GameActionType.RouteDispatched:        // 23
                case GameActionType.RouteCargoDebited:      // 24
                case GameActionType.RouteCargoDelivered:    // 25
                case GameActionType.RoutePaused:            // 26
                case GameActionType.RouteEndpointLost:      // 27
                case GameActionType.RouteRecoveryCredited:  // 28
                case GameActionType.RouteCargoPickedUp:     // 29
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// A free-standing route action: a route Type carrying a non-empty
        /// <see cref="GameAction.RouteId"/>. The Type is the authority; the
        /// <c>RouteId</c> non-empty check is a belt-and-suspenders guard against any
        /// future non-route action that ever reused a route Type value, and documents
        /// intent.
        /// </summary>
        internal static bool IsFreeStandingRouteAction(GameAction a)
        {
            return a != null
                   && IsRouteActionType(a.Type)
                   && !string.IsNullOrEmpty(a.RouteId);
        }

        /// <summary>
        /// True when a free-standing route action should be RETIRED at a rewind whose
        /// cutoff is <paramref name="cutoffUT"/> (the loaded RewindPoint UT).
        ///
        /// <para><b>Strict <c>&gt;</c> (not <c>&gt;=</c>), justified by emit ordering,
        /// not probability.</b> A route's ledger row and its physical write are emitted
        /// in the SAME synchronous <c>EmitLoopCycle</c> tick from the same
        /// <c>currentUT</c> (the row is written only after the live writer runs), so a
        /// row stamped exactly at the cutoff means its physical effect already landed
        /// and IS captured in the RewindPoint quicksave's tanks — keep the row,
        /// matching the world-revert boundary. A tie is also near-impossible on the
        /// ~1 Hz dispatch grid, but the emit-ordering argument is the reason. Do NOT
        /// "fix" this to <c>&gt;=</c>.</para>
        /// </summary>
        internal static bool ShouldRetireRouteActionAtRewind(GameAction a, double cutoffUT)
        {
            return IsFreeStandingRouteAction(a) && a.UT > cutoffUT;
        }

        /// <summary>
        /// Returns a NEW list containing every action from <paramref name="source"/>
        /// EXCEPT free-standing route actions with <c>UT &gt; <paramref name="cutoffUT"/></c>,
        /// preserving order. <paramref name="retired"/> receives the drop count.
        ///
        /// <para>Pure list-&gt;list (does not touch the static <c>Ledger</c>), so it is
        /// unit-testable in isolation and reusable by both the rewind path and any
        /// future caller. A <paramref name="cutoffUT"/> of
        /// <c>double.PositiveInfinity</c> retires nothing (the rollback / route-blind
        /// contract).</para>
        /// </summary>
        internal static List<GameAction> RetireFutureRouteActions(
            IReadOnlyList<GameAction> source, double cutoffUT, out int retired)
        {
            retired = 0;
            if (source == null)
                return new List<GameAction>();

            var kept = new List<GameAction>(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                GameAction a = source[i];
                if (ShouldRetireRouteActionAtRewind(a, cutoffUT))
                {
                    retired++;
                    continue;
                }
                kept.Add(a);
            }
            return kept;
        }
    }
}
