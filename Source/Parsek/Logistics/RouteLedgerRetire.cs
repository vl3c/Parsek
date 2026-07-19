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
    /// pre-rewind ledger action list while the live world AND the route counters
    /// revert via the <c>.sfs</c>. The UT-blind dispatch dedup then reproduces the
    /// counter-keyed cycleId and SUPPRESSES the re-flown cycle that would re-apply
    /// the live cargo, so the player is charged but the goods are never
    /// re-delivered ("funds spent, no goods").</para>
    ///
    /// <para><b>The fix.</b> At a successful rewind restore, DROP every free-standing
    /// route ledger row whose <c>UT</c> is strictly after the rewind cutoff (the
    /// post-load live UT = the loaded quicksave UT). The surviving ledger then matches the reverted world,
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
    /// neither the static ledger action list nor the committed-recordings store, so it
    /// is not a grep-gate concern and needs no allowlist entry (the caller —
    /// <c>ReconciliationBundle</c>, already allowlisted — passes the list in).</para>
    /// </summary>
    internal static class RouteLedgerRetire
    {
        /// <summary>
        /// True for the eight free-standing route <see cref="GameActionType"/>
        /// members (values 23-30). Implemented as an explicit switch over the named
        /// members (NOT a numeric range) so that adding an unrelated action Type
        /// after the route block can never silently widen the retire set.
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
                case GameActionType.RouteResumed:           // 30
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
        /// cutoff is <paramref name="cutoffUT"/> (the post-load live UT = the loaded quicksave UT).
        ///
        /// <para><b>Strict <c>&gt;</c> (not <c>&gt;=</c>), justified by emit ordering,
        /// not probability.</b> A route's ledger row and its physical write are emitted
        /// in the SAME synchronous <c>EmitLoopCycle</c> tick from the same
        /// <c>currentUT</c> (the row is written only after the live writer runs), so a
        /// row stamped exactly at the cutoff means its physical effect already landed
        /// and IS captured in the reverted world's tanks — keep the row, matching the
        /// world-revert boundary. <b>The cutoff MUST be the UT the world actually
        /// reverted to (the loaded quicksave UT).</b> The rewind caller
        /// (<c>RewindInvoker.ConsumePostLoad</c>) passes the POST-LOAD live UT for
        /// exactly this reason — NOT <c>RewindPoint.UT</c>, which is captured one frame
        /// before the deferred quicksave save and is ~one frame earlier than the UT the
        /// <c>.sfs</c> embeds (edge-case review finding #4: an earlier cutoff would drop
        /// a row in the <c>(rp.UT, quicksaveUT]</c> window whose effect is in the
        /// reverted world, making the re-fly double-apply). A tie is also near-impossible
        /// on the ~1 Hz dispatch grid, but the emit-ordering argument is the reason. Do
        /// NOT "fix" this to <c>&gt;=</c>.</para>
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

        // -------------------------------------------------------------------
        // Rec-3 (the non-rewind discard leak) — pure selection helpers.
        //
        // Shared surface used by the Rec-3 discard-time OBSERVABILITY reporter
        // (RouteDiscardObservability) and, later, the DEFERRED reverse-on-discard
        // pass. Both need to know which free-standing route rows physically fired
        // inside a discarded segment's UT window. Kept here (next to the Rec-1
        // retire) because they share IsFreeStandingRouteAction / IsRouteActionType.
        // Plan Phase 4; report risk #6.
        // -------------------------------------------------------------------

        /// <summary>
        /// True when a free-standing route action carries an ACTUAL physical
        /// world mutation — a non-empty resource manifest
        /// (<see cref="GameAction.RouteResourceManifest"/>) or inventory manifest
        /// (<see cref="GameAction.RouteInventoryManifest"/>). These are the rows the
        /// three no-reverse writers (<c>LiveDeliveryWriters</c>,
        /// <c>LiveOriginDebitWriters</c>, <c>LiveInventoryPickupWriter</c>) produce.
        ///
        /// <para>Funds-only and marker rows return false: <c>RouteDispatched</c>,
        /// <c>RoutePaused</c>, <c>RouteResumed</c>, <c>RouteEndpointLost</c>, <c>RouteRecoveryCredited</c>,
        /// and the KSC-funds-only <c>RouteCargoDebited</c> variant (which sets
        /// <see cref="GameAction.RouteKscFundsCost"/> instead of a manifest) mutate no
        /// physical world state, so they have nothing to leak on a non-rewind discard.</para>
        /// </summary>
        internal static bool IsPhysicalRouteMutation(GameAction a)
        {
            return IsFreeStandingRouteAction(a)
                   && (((a.RouteResourceManifest != null) && (a.RouteResourceManifest.Count > 0))
                       || ((a.RouteInventoryManifest != null) && (a.RouteInventoryManifest.Count > 0)));
        }

        /// <summary>
        /// True when a free-standing route action's <c>UT</c> falls inside the
        /// INCLUSIVE window <c>[<paramref name="minUT"/>, <paramref name="maxUT"/>]</c>.
        /// Inclusive on both ends: a route row stamped exactly at a discarded
        /// recording's start/end UT physically fired during that segment.
        /// </summary>
        internal static bool IsFreeStandingRouteActionInWindow(
            GameAction a, double minUT, double maxUT)
        {
            return IsFreeStandingRouteAction(a) && a.UT >= minUT && a.UT <= maxUT;
        }

        /// <summary>
        /// Returns a NEW list of the free-standing route actions from
        /// <paramref name="source"/> whose <c>UT</c> lands inside the inclusive window
        /// <c>[<paramref name="minUT"/>, <paramref name="maxUT"/>]</c>, preserving order.
        ///
        /// <para>Pure list-&gt;list (does not touch the static <c>Ledger</c>), so it is
        /// unit-testable in isolation and ERS/ELS-clean (the allowlisted caller passes
        /// the ledger action list in). A degenerate window where
        /// <paramref name="minUT"/> &gt; <paramref name="maxUT"/> selects nothing.</para>
        /// </summary>
        internal static List<GameAction> SelectFreeStandingRouteActionsInWindow(
            IReadOnlyList<GameAction> source, double minUT, double maxUT)
        {
            var selected = new List<GameAction>();
            if (source == null || minUT > maxUT)
                return selected;

            for (int i = 0; i < source.Count; i++)
            {
                GameAction a = source[i];
                if (IsFreeStandingRouteActionInWindow(a, minUT, maxUT))
                    selected.Add(a);
            }
            return selected;
        }
    }
}
