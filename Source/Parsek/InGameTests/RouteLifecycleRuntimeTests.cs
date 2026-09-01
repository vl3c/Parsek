using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;
using Parsek.TestCommands;

namespace Parsek.InGameTests
{
    /// <summary>
    /// (RVR-3) In-game coverage for the supply-route SEND-ONCE / PAUSE lifecycle:
    /// synthetic routes driven through <see cref="RouteOrchestrator"/>'s real tick
    /// inside a live KSP session, so a driven scenario can gate the Send-Once
    /// contract - including the 2026-08-30 blocked-then-paused fix - on every
    /// flight instead of only headlessly.
    ///
    /// <para><b>What the headless xUnit layer already proves</b>
    /// (<c>RouteSendOnceBlockedPauseTests</c>, <c>RouteLoopDeliveryFireTests</c>,
    /// <c>TestCommandLogisticsVerbSeamTests</c>): the armed-pause honor on a
    /// blocked cycle, the delivered tail, the toast builders and the RouteCommand
    /// decision core, all over hand-built inputs with a FAKE
    /// <see cref="IRouteRuntimeEnvironment"/>. <b>What THIS category adds:</b> the
    /// same seams driven against the PRODUCTION
    /// <see cref="LiveRouteRuntimeEnvironment"/> and the live statics behind it -
    /// the real ERS computation, the real
    /// <see cref="RouteEndpointResolver"/> over <c>FlightGlobals</c>, the real
    /// <see cref="Ledger"/> / <see cref="RouteStore"/> singletons, the real
    /// <see cref="ParsekLog.ScreenMessage"/> path, and the real
    /// <see cref="RouteCandidateFinder"/> over the live committed trees. NO cell
    /// here installs a fake env: every eligibility verdict these cells act on is
    /// one the shipping environment produced.</para>
    ///
    /// <para><b>What a DELIVERED cycle needs live, and why no cell drives one.</b>
    /// <see cref="RouteDispatchEvaluator.CheckEligibility"/> must pass all six
    /// gates before <c>EmitLoopCycle</c> runs: ERS-resolvable sources, a resolved
    /// endpoint for every stop, origin cargo, Career/KSC funds, destination
    /// capacity, and the partner constraint. Two cells here deliberately pass the
    /// first three and stop at the fourth (see the RESOLUTION half below): they
    /// point a stop at a REAL live vessel, because the endpoint gate resolves a
    /// live <c>Vessel</c> by <c>persistentId</c> and nothing synthetic satisfies
    /// it. What they never do is DELIVER: the capacity gate only probes, and the
    /// cycle blocks before <c>EmitDispatchDebit</c>, so no debit / delivery / funds
    /// row touches the real craft (both cells assert zero of each for their route
    /// id). Actually FILLING a real destination would be a world mutation no
    /// batch-safe cell may make, so
    /// <see cref="DeliverableCycle_RequiresRealDestination_LiveEnvRefusesSyntheticEndpoint"/>
    /// PINS the synthetic-endpoint refusal (the live resolver's exact reason
    /// token), and the delivered half stays owned by the headless fire tests plus
    /// the driven RVR-1 / RVR-2 flights, which fly a real transport to a real
    /// base.</para>
    ///
    /// <para><b>TWO blocked populations, and the split is the whole point.</b>
    /// <see cref="RouteOrchestrator.IsPostponementHold"/> divides the evaluator's
    /// failure kinds in two, and the armed pause behaves OPPOSITELY across the
    /// divide:
    /// <list type="bullet">
    ///   <item><b>POSTPONEMENTS</b> (<c>SourcesStale</c>, <c>WaitingForPartner</c>)
    ///     - the crossing self-resolves on a later tick, so
    ///     <see cref="RouteOrchestrator.TryHonorArmedPauseOnBlockedCycle"/> returns
    ///     false and KEEPS the arm: the crossing is still consumed as skipped, but
    ///     the route does not pause, emits no <c>RoutePaused</c> row and posts no
    ///     toast. Cheap to reach live: a fresh-GUID source id is in no ERS snapshot
    ///     in any scene, with no arranging at all.</item>
    ///   <item><b>RESOLUTIONS</b> (every other kind - <c>DestinationFull</c>,
    ///     <c>FundsShort</c>, <c>OriginLacksCargo</c>, <c>EndpointLost</c>) - the
    ///     one-shot's cycle IS over, so the arm is CONSUMED: Paused, reason
    ///     <see cref="RouteOrchestrator.BlockedThenPausedReason"/>, flags cleared,
    ///     hold kept, marker row, toast (Send Once only).</item>
    /// </list>
    /// The first flight (2026-09-01) measured the postponement half only, because
    /// every cell here reached the ERS gate first. The resolution half now has its
    /// own pair of cells, which get PAST the ERS gate by pointing the route's
    /// <c>SourceRefs</c> at a REAL id out of the live ERS snapshot and its stop at
    /// a REAL live vessel pid, then hand it a delivery manifest no craft can hold
    /// (see <see cref="ResolutionBlockKind"/>). Both halves drive the SAME
    /// <c>ProcessLoopRoute</c> branch (<c>!elig.Eligible</c>); what differs is only
    /// which side of <c>IsPostponementHold</c> the recorded hold lands on.</para>
    ///
    /// <para><b>Why the resolution cells are still world-safe.</b> The capacity
    /// gate (<c>LiveRouteRuntimeEnvironment.DestinationHasCapacity</c> ->
    /// <c>RouteDestinationCapacityCheck</c> -> <c>RouteDeliveryPlanner</c> over
    /// <c>LiveDeliveryCapacityProbe</c>) only PROBES free capacity; it writes
    /// nothing. The cycle then blocks BEFORE <c>EmitDispatchDebit</c>, so no debit,
    /// no delivery and no funds row ever touch the real destination vessel. The
    /// pre-tick <see cref="RequireLiveResolutionBlock"/> gate refuses to tick at
    /// all unless the live environment has ALREADY judged the crossing blocked with
    /// the expected kind, so an eligible verdict is a hard failure rather than a
    /// dispatch against live world state.</para>
    ///
    /// <para><b>Batch-safety contract</b> (mirrors
    /// <see cref="RouteRewindTimelineRuntimeTests"/>): every cell is
    /// <c>AllowBatchExecution = true</c> and scene-agnostic
    /// (<see cref="InGameTestAttribute.AnyScene"/>), synchronous (<c>void</c>) so
    /// the background 1 Hz <c>ParsekScenario.Update</c> tick can never interleave
    /// with a half-arranged store, and touches only in-memory statics - all of
    /// them snapshotted up front and restored in <c>finally</c>. No scene loads,
    /// no save writes, no live-vessel or career-pool mutation.</para>
    ///
    /// <para><b>Store isolation is MANDATORY here, not hygiene.</b>
    /// <see cref="RouteOrchestrator.Tick(double, IRouteRuntimeEnvironment)"/>
    /// processes EVERY committed route, and these cells tick at a synthetic
    /// far-future UT with a loop-unit resolver installed that answers for any
    /// route. A player route left in the store would be handed a bogus clock and
    /// could fire crossings against it. Every ticking cell therefore swaps the
    /// store down to ONLY its synthetic route via
    /// <see cref="RouteStore.InstallRoutesAtRewind"/> first and restores the
    /// pre-test lists afterwards.</para>
    ///
    /// <para><b>Synthetic-UT discipline.</b> The ticking cells anchor at
    /// <c>liveUT + 1000000</c> so every route stamp and ledger row lives in a UT
    /// band disjoint from anything the live session wrote (the
    /// RouteRewindTimeline seam-test rule). The live clock is still REQUIRED to be
    /// healthy: <c>EmitRouteLifecycleMarker</c> refuses a UT &lt;= 0, so a session
    /// with no resolvable clock skips instead of silently proving nothing.</para>
    /// </summary>
    public sealed class RouteLifecycleRuntimeTests
    {
        private const string Category = "RouteLifecycle";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>Synthetic-UT offset for the ticking cells (see class doc).</summary>
        private const double SyntheticBandOffset = 1000000.0;

        /// <summary>Span length of the synthetic loop unit, seconds.</summary>
        private const double SpanSeconds = 300.0;

        /// <summary>Dock phase inside the span, seconds from span start.</summary>
        private const double DockPhaseSeconds = 150.0;

        /// <summary>
        /// The POSTPONEMENT verdict the fresh-GUID-source cells drive their blocked
        /// cycle through: the ERS gate is the first thing
        /// <see cref="RouteDispatchEvaluator.CheckEligibility"/> checks, and a fresh
        /// GUID cannot be in any ERS snapshot in any scene. Classified a
        /// postponement by <see cref="RouteOrchestrator.IsPostponementHold"/>, so an
        /// armed pause SURVIVES it.
        /// </summary>
        private const RouteDispatchEvaluator.EligibilityFailureKind PostponementBlockKind =
            RouteDispatchEvaluator.EligibilityFailureKind.SourcesStale;

        /// <summary>The detail token <see cref="RouteDispatchEvaluator.CheckEligibility"/>
        /// pairs with <see cref="PostponementBlockKind"/>.</summary>
        private const string PostponementBlockDetail = "sources-stale";

        /// <summary>
        /// The RESOLUTION verdict the two live-source cells drive: gate 8 of
        /// <see cref="RouteDispatchEvaluator.CheckEligibility"/>, reached only once
        /// the ERS (gate 4), endpoint (gate 5) and origin-cargo (gate 6) gates all
        /// PASS. NOT a postponement, so an armed pause is CONSUMED by it - which is
        /// the contract the 2026-08-30 blocked-then-paused fix shipped and the half
        /// of the state machine no postponement cell can reach.
        /// </summary>
        private const RouteDispatchEvaluator.EligibilityFailureKind ResolutionBlockKind =
            RouteDispatchEvaluator.EligibilityFailureKind.DestinationFull;

        /// <summary>
        /// The single resource the resolution cells' delivery manifest carries. The
        /// gate's <c>fullToken</c> is the bare name of the first short resource line
        /// (<c>RouteDestinationCapacityCheck.FirstShortToken</c>), and a one-entry
        /// manifest makes that token deterministic.
        /// </summary>
        private const string ResolutionBlockResource = "LiquidFuel";

        /// <summary>
        /// The amount that makes the block a property of arithmetic rather than of
        /// the host: 1e9 units of LiquidFuel is ~5e9 kg, orders of magnitude beyond
        /// any craft KSP can hold, so <c>ProbeResourceFreeCapacity</c> cannot cover
        /// it on ANY destination - including one with empty tanks. The planner
        /// clamps available to free capacity and reports the plan PARTIAL, which is
        /// exactly the all-or-nothing hold.
        /// </summary>
        private const double ResolutionBlockAmount = 1e9;

        /// <summary>
        /// Destination pid every synthetic stop carries: near <c>uint.MaxValue</c>,
        /// which KSP does not hand out to a live vessel, so the live resolver's miss
        /// is a property of the fixture rather than luck. The one cell that depends
        /// on the miss SKIPS (never asserts, never proceeds) if a save somehow holds
        /// a vessel on it.
        /// </summary>
        private const uint SyntheticDestinationPid = 4294967290u;

        // ==================================================================
        // (a) What a deliverable cycle needs live (the investigation cell)
        // ==================================================================

        [InGameTest(Category = Category,
            Description = "Live-env ground truth for the blocked cells: the production LiveRouteRuntimeEnvironment refuses a synthetic destination pid with pid-miss-no-surface-fallback and blocks a synthetic route at the ERS gate, so no scene-agnostic cell can drive a DELIVERED cycle without mutating a real vessel")]
        public void DeliverableCycle_RequiresRealDestination_LiveEnvRefusesSyntheticEndpoint()
        {
            double liveUT = RequireLiveContext();

            // No store mutation at all: CheckEligibility reads RouteStore only for
            // the round-trip partner gate, which an unlinked route never reaches.
            string routeId = NewId("deliverable-probe");
            Route route = BuildLoopRoute(routeId, "Parsek RouteLifecycle deliverable probe",
                RouteStatus.Active, liveUT + SyntheticBandOffset);
            var env = new LiveRouteRuntimeEnvironment();

            // (i) The endpoint half. A synthetic pid resolves to no live vessel and
            // the endpoint is not surface-anchored, so the production resolver
            // returns its exact no-fallback token. THIS is why a deliverable cycle
            // needs a real destination: nothing a synthetic cell can author will
            // ever satisfy it.
            RouteStop stop = route.Stops[0];
            string endpointReason;
            if (env.TryResolveEndpoint(stop.Endpoint, out endpointReason))
            {
                // Cannot self-set-up: this save happens to hold a live vessel on the
                // near-uint.MaxValue pid the fixture picked precisely because KSP
                // does not hand it out. Skip rather than assert against a world we
                // did not arrange - and never proceed, because the cell must not
                // touch a real vessel.
                InGameAssert.Skip(
                    $"The synthetic destination pid {SyntheticDestinationPid.ToString(IC)} resolves to a " +
                    "LIVE vessel in this save; the fixture cannot prove the refusal without " +
                    "binding a real delivery target");
            }
            InGameAssert.AreEqual("pid-miss-no-surface-fallback", endpointReason,
                $"Unexpected live endpoint refusal token '{endpointReason ?? "<null>"}'");

            // (ii) The gate that actually fires first for a synthetic route: ERS.
            // Fresh-GUID source ids cannot be in any ERS snapshot, in any scene.
            RouteDispatchEvaluator.EligibilityResult elig =
                RouteDispatchEvaluator.CheckEligibility(route, liveUT + SyntheticBandOffset, env);
            InGameAssert.IsFalse(elig.Eligible,
                "A synthetic route was judged ELIGIBLE by the live environment - a cycle " +
                "would have dispatched and delivered against live world state");
            InGameAssert.AreEqual(PostponementBlockKind, elig.Kind,
                $"Live eligibility blocked with kind={elig.Kind} reason={elig.Reason ?? "<none>"}; " +
                "the fresh-GUID-source cells in this category are written against " +
                $"{PostponementBlockKind} and would be measuring a different gate");
            InGameAssert.AreEqual(PostponementBlockDetail, elig.Reason,
                $"Unexpected live block detail '{elig.Reason ?? "<null>"}'");

            ParsekLog.Info("TestRunner",
                $"RouteLifecycle deliverable-probe: PASS routeId={routeId} " +
                $"endpointReason={endpointReason} blockKind={elig.Kind} " +
                $"blockDetail={elig.Reason ?? "<none>"} liveUT={liveUT.ToString("R", IC)} " +
                "- a DELIVERED cycle additionally needs a live destination vessel with " +
                "capacity, origin cargo and (Career+KSC) funds, i.e. real world mutation: " +
                "owned by the headless fire tests and the driven RVR flights");
        }

        // ==================================================================
        // (b) THE resolution gate: send-once armed + a RESOLUTION block -> Paused
        // ==================================================================

        [InGameTest(Category = Category,
            Description = "Live gate for the 2026-08-30 blocked-then-paused fix on a RESOLUTION kind: a send-once-armed route whose sources are REAL live-ERS ids and whose stop is a REAL live vessel blocks at the destination-capacity gate (DestinationFull), and that consumes the arm - Paused with reason blocked-then-paused, both one-shot flags cleared, the hold KEPT, SkippedCycles bumped, a RoutePaused ledger row at the crossing UT, nothing dispatched or delivered, and the player toast posted")]
        public void SendOnce_ResolutionBlock_PausesWithReason_KeepsHold()
        {
            double liveUT = RequireLiveContext();
            RequireRenderCompositionUnarmed();
            RouteSourceRef sourceRef = RequireLiveErsSourceRef();
            Vessel destination = RequireLiveDestinationVessel();

            double baseUT = liveUT + SyntheticBandOffset;
            double crossingUT = baseUT + DockPhaseSeconds;
            string routeId = NewId("sendonce-resolution");
            Route route = BuildResolutionBlockRoute(routeId, "Parsek RouteLifecycle Send Once",
                RouteStatus.Active, baseUT, sourceRef, destination.persistentId);

            var lines = new List<string>();
            var toasts = new List<string>();
            using (var arena = new LifecycleArena(lines, toasts, route, baseUT))
            {
                // Pre-flight: only tick once the LIVE environment has already said
                // this crossing blocks, and blocks with the RESOLUTION kind this
                // cell asserts. A cell that ticked first could dispatch a real cycle
                // against the real destination vessel before finding out.
                RequireLiveResolutionBlock(route, crossingUT, arena.Env, destination);

                InGameAssert.IsTrue(RouteOrchestrator.TrySendOneCycleNow(route, baseUT + 1.0),
                    "TrySendOneCycleNow returned false for an Active synthetic route");
                InGameAssert.IsTrue(route.PauseAfterCurrentCycle, "PauseAfterCurrentCycle not armed");
                InGameAssert.IsTrue(route.SendOnceArmed, "SendOnceArmed not armed");
                int beforeTick = Ledger.Actions.Count;

                RouteOrchestrator.Tick(crossingUT, arena.Env);

                // The cycle was CONSUMED as blocked...
                InGameAssert.AreEqual(1, route.SkippedCycles,
                    $"SkippedCycles not bumped by the blocked cycle (got {route.SkippedCycles.ToString(IC)})");
                InGameAssert.AreEqual(0, route.CompletedCycles,
                    "A blocked cycle must not count as completed");
                InGameAssert.AreEqual(0L, route.LastObservedLoopCycleIndex,
                    "The blocked cycle did not snap the crossing index forward - it would re-fire every tick");

                // ...nothing was emitted beyond the pause marker. This is also the
                // world-safety assertion: the stop points at a REAL vessel, so a
                // debit or delivery row here would mean the cell moved live cargo.
                InGameAssert.AreEqual(0, CountRouteRows(routeId, GameActionType.RouteDispatched),
                    "A blocked cycle emitted a RouteDispatched row");
                InGameAssert.AreEqual(0, CountRouteRows(routeId, GameActionType.RouteCargoDelivered),
                    "A blocked cycle emitted a RouteCargoDelivered row against a REAL vessel");
                InGameAssert.AreEqual(0, CountRouteRows(routeId, GameActionType.RouteCargoDebited),
                    "A blocked cycle emitted a RouteCargoDebited row");

                // ...and the armed pause was honored (THE defect: the route used to
                // stay Active with the flag still armed - an endless ghost loop plus
                // a live arm that would deliver at some arbitrary later crossing).
                InGameAssert.AreEqual(RouteStatus.Paused, route.Status,
                    $"A RESOLUTION block ({ResolutionBlockKind}) did not consume the armed pause " +
                    $"(status={route.Status}); only IsPostponementHold kinds keep the arm");
                InGameAssert.IsFalse(route.PauseAfterCurrentCycle,
                    "PauseAfterCurrentCycle survived the blocked cycle");
                InGameAssert.IsFalse(route.SendOnceArmed,
                    "SendOnceArmed survived the blocked cycle");

                // The hold is deliberately KEPT so the Logistics window still names
                // WHY the run did not happen next to the now-Paused row.
                InGameAssert.AreEqual(ResolutionBlockKind, route.LastHoldKind,
                    $"Hold kind not retained across the armed pause (got {route.LastHoldKind})");
                InGameAssert.AreEqual(ResolutionBlockResource, route.LastHoldDetail,
                    $"Hold detail not the first-short resource token (got '{route.LastHoldDetail ?? "<null>"}')");
                InGameAssert.ApproxEqual(crossingUT, route.LastHoldUT, 0.001,
                    "Hold UT not stamped at the crossing");

                // The durable timeline marker a later rewind retires.
                GameAction pauseRow = LatestRouteRow(routeId, GameActionType.RoutePaused, beforeTick);
                InGameAssert.IsNotNull(pauseRow,
                    "No RoutePaused ledger row emitted by the blocked-cycle armed pause");
                InGameAssert.AreEqual(RouteOrchestrator.BlockedThenPausedReason,
                    pauseRow.RouteEndpointReason,
                    $"RoutePaused row reason mismatch (got '{pauseRow.RouteEndpointReason ?? "<null>"}')");
                InGameAssert.ApproxEqual(crossingUT, pauseRow.UT, 0.001,
                    "RoutePaused row not stamped at the crossing UT");

                // Grep-stable production log lines.
                InGameAssert.IsTrue(
                    AnyLine(lines, "ArmedPause:", RouteOrchestrator.BlockedThenPausedReason,
                        "armedBy=send-once"),
                    "No 'ArmedPause: ... blocked-then-paused ... armedBy=send-once' log line observed");
                InGameAssert.IsTrue(
                    AnyLine(lines, "LifecycleMarker:", "type=RoutePaused",
                        "reason=" + RouteOrchestrator.BlockedThenPausedReason, "ut="),
                    "No genuine 'LifecycleMarker: ... reason=blocked-then-paused ut=' emission observed");
                InGameAssert.IsFalse(AnyLine(lines, "BLOCKED by postponement"),
                    "The resolution cell took the POSTPONEMENT branch - its block is on the " +
                    "wrong side of IsPostponementHold and it is measuring the other contract");

                // The player-visible half: a send-once run that resolved without
                // delivering says so on screen and names the hold.
                InGameAssert.AreEqual(1, toasts.Count,
                    $"Expected exactly one send-once screen message, got {toasts.Count.ToString(IC)}");
                InGameAssert.Contains(toasts[0], "Send Once",
                    "The blocked send-once toast does not name the route");
                InGameAssert.Contains(toasts[0], "Paused",
                    "The blocked send-once toast does not say the route paused");

                ParsekLog.Info("TestRunner",
                    $"RouteLifecycle sendonce-resolution: PASS routeId={routeId} " +
                    $"holdKind={route.LastHoldKind} holdDetail={route.LastHoldDetail ?? "<none>"} " +
                    $"destPid={destination.persistentId.ToString(IC)} " +
                    $"sourceId={sourceRef.RecordingId} " +
                    $"crossingUT={crossingUT.ToString("R", IC)} armedBy=send-once");
            }
        }

        // ==================================================================
        // (b2) The POSTPONEMENT half: the arm SURVIVES a self-resolving block
        // ==================================================================

        [InGameTest(Category = Category,
            Description = "Live pin of the PR #1583 postponement carve-out: a send-once-armed route whose crossing blocks on SourcesStale (a postponement, not a resolution) KEEPS both one-shot flags and stays Active - the crossing is still consumed as skipped and the hold is recorded, but there is no pause transition, no RoutePaused row, no ledger row at all and no toast, and the production 'BLOCKED by postponement ... arm kept' line is emitted")]
        public void SendOnce_PostponementBlock_KeepsArm_RouteStaysActive_NoToast()
        {
            double liveUT = RequireLiveContext();
            RequireRenderCompositionUnarmed();

            double baseUT = liveUT + SyntheticBandOffset;
            double crossingUT = baseUT + DockPhaseSeconds;
            string routeId = NewId("sendonce-postponed");
            Route route = BuildLoopRoute(routeId, "Parsek RouteLifecycle Send Once",
                RouteStatus.Active, baseUT);

            var lines = new List<string>();
            var toasts = new List<string>();
            using (var arena = new LifecycleArena(lines, toasts, route, baseUT))
            {
                // Pre-flight: only tick once the LIVE environment has already said
                // this crossing blocks, and blocks with the kind this cell asserts.
                // A cell that ticked first could - in some future scene - dispatch a
                // real cycle before finding out.
                RequireLivePostponementBlock(route, crossingUT, arena.Env);

                InGameAssert.IsTrue(RouteOrchestrator.TrySendOneCycleNow(route, baseUT + 1.0),
                    "TrySendOneCycleNow returned false for an Active synthetic route");
                InGameAssert.IsTrue(route.PauseAfterCurrentCycle, "PauseAfterCurrentCycle not armed");
                InGameAssert.IsTrue(route.SendOnceArmed, "SendOnceArmed not armed");
                int beforeTick = Ledger.Actions.Count;

                RouteOrchestrator.Tick(crossingUT, arena.Env);

                // The crossing IS consumed as skipped - a postponement does not undo
                // the cycle, it only refuses to RESOLVE the one-shot.
                InGameAssert.AreEqual(1, route.SkippedCycles,
                    $"SkippedCycles not bumped by the postponed cycle (got {route.SkippedCycles.ToString(IC)})");
                InGameAssert.AreEqual(0, route.CompletedCycles,
                    "A blocked cycle must not count as completed");
                InGameAssert.AreEqual(0L, route.LastObservedLoopCycleIndex,
                    "The blocked cycle did not snap the crossing index forward - it would re-fire every tick");

                // The hold is recorded, naming the postponement.
                InGameAssert.AreEqual(PostponementBlockKind, route.LastHoldKind,
                    $"Hold kind not recorded (got {route.LastHoldKind})");
                InGameAssert.AreEqual(PostponementBlockDetail, route.LastHoldDetail,
                    $"Hold detail not recorded (got '{route.LastHoldDetail ?? "<null>"}')");
                InGameAssert.ApproxEqual(crossingUT, route.LastHoldUT, 0.001,
                    "Hold UT not stamped at the crossing");
                InGameAssert.IsTrue(RouteOrchestrator.IsPostponementHold(route.LastHoldKind),
                    $"{route.LastHoldKind} is no longer classified a postponement - this cell " +
                    "is written against the arm-kept branch and would measure the other one");

                // THE contract: the arm survives, and so does the status.
                InGameAssert.AreEqual(RouteStatus.Active, route.Status,
                    $"A postponement block paused the route (status={route.Status}); a Send Once " +
                    "cancelled by a self-resolving ERS gap is a cancellation the player cannot see coming");
                InGameAssert.IsTrue(route.PauseAfterCurrentCycle,
                    "PauseAfterCurrentCycle was consumed by a POSTPONEMENT block");
                InGameAssert.IsTrue(route.SendOnceArmed,
                    "SendOnceArmed was consumed by a POSTPONEMENT block");

                // Nothing durable happened: no pause marker, and in fact no row at
                // all (the blocked branch emits nothing, and the postponement branch
                // returns before the marker emission).
                InGameAssert.IsNull(LatestRouteRow(routeId, GameActionType.RoutePaused, beforeTick),
                    "A postponement block emitted a RoutePaused row");
                InGameAssert.AreEqual(beforeTick, Ledger.Actions.Count,
                    "A postponement block emitted ledger rows (it must emit NOTHING)");
                InGameAssert.AreEqual(0, toasts.Count,
                    $"A postponement block posted {toasts.Count.ToString(IC)} screen message(s); " +
                    "the one-shot has not resolved, so there is nothing to report yet");

                // Grep-stable production log lines: the postponement audit line is
                // present and the resolution tail is absent.
                InGameAssert.IsTrue(
                    AnyLine(lines, "ArmedPause:", "BLOCKED by postponement",
                        "kind=" + PostponementBlockKind, "arm kept"),
                    "No 'ArmedPause: ... BLOCKED by postponement kind=SourcesStale - arm kept' line " +
                    "observed (Verbose; the arena force-arms verbose logging for exactly this)");
                InGameAssert.IsFalse(
                    AnyLine(lines, "ArmedPause:", RouteOrchestrator.BlockedThenPausedReason),
                    "A postponement block ran the blocked-then-paused resolution tail");

                ParsekLog.Info("TestRunner",
                    $"RouteLifecycle sendonce-postponed: PASS routeId={routeId} " +
                    $"crossingUT={crossingUT.ToString("R", IC)} hold={route.LastHoldKind} " +
                    "armKept=true status=Active");
            }
        }

        // ==================================================================
        // (c) The arm itself is observable
        // ==================================================================

        [InGameTest(Category = Category,
            Description = "TrySendOneCycleNow on a Paused route un-pauses it to Active with the send-once-arm transition reason, arms BOTH one-shot flags, pulls NextDispatchUT to now-or-past, clears the retry backoff, and emits NO ledger row at arm time")]
        public void SendOnce_ArmIsObservable_UnPausesAndPullsDispatchForward()
        {
            double liveUT = RequireLiveContext();
            double baseUT = liveUT + SyntheticBandOffset;

            // Arming touches the route object only (no store read, no tick), so this
            // cell installs nothing and has nothing to restore: the ledger-count
            // assertion below is the proof that it wrote no rows.
            string routeId = NewId("sendonce-arm");
            Route route = BuildLoopRoute(routeId, "Parsek RouteLifecycle arm",
                RouteStatus.Paused, baseUT);
            route.NextDispatchUT = baseUT + 99999.0;
            route.NextEligibilityCheckUT = baseUT + 5000.0;

            var lines = new List<string>();
            Action<string> prevObserver = ParsekLog.TestObserverForTesting;
            int beforeArm = Ledger.Actions.Count;
            try
            {
                ParsekLog.TestObserverForTesting = l => { lines.Add(l); prevObserver?.Invoke(l); };

                InGameAssert.IsTrue(RouteOrchestrator.TrySendOneCycleNow(route, baseUT),
                    "TrySendOneCycleNow returned false for a Paused route");

                InGameAssert.AreEqual(RouteStatus.Active, route.Status,
                    $"Send Once did not un-pause the route (status={route.Status})");
                InGameAssert.IsTrue(route.SendOnceArmed, "SendOnceArmed not set");
                InGameAssert.IsTrue(route.PauseAfterCurrentCycle, "PauseAfterCurrentCycle not set");
                InGameAssert.IsTrue(route.NextDispatchUT <= baseUT + 0.001,
                    $"Send Once did not pull NextDispatchUT to now-or-past " +
                    $"(next={route.NextDispatchUT.ToString("R", IC)} now={baseUT.ToString("R", IC)})");
                InGameAssert.IsFalse(route.NextEligibilityCheckUT.HasValue,
                    "Send Once did not clear the wait-retry backoff");
                InGameAssert.AreEqual(beforeArm, Ledger.Actions.Count,
                    "Send Once must not emit a ledger row at arm time (the provenance rides " +
                    "the dispatched row, and a never-fired arm must leave no trace)");

                // The transition reason is the arm's only durable provenance until a
                // cycle resolves; it is what a log-reading lane greps for.
                InGameAssert.IsTrue(
                    AnyLine(lines, "Route " + RouteIds.Short(routeId), "Paused", "Active",
                        "reason=send-once-arm"),
                    "No 'Route <id> Paused→Active reason=send-once-arm' transition line observed");

                ParsekLog.Info("TestRunner",
                    $"RouteLifecycle sendonce-arm: PASS routeId={routeId} " +
                    $"nextDispatchUT={route.NextDispatchUT.ToString("R", IC)}");
            }
            finally
            {
                ParsekLog.TestObserverForTesting = prevObserver;
                // Nothing else to undo: the route was never added to any store, and
                // the ledger-count assertion above pins that no row was written.
            }
        }

        // ==================================================================
        // (d) The OTHER arming provenance: Pause while InTransit
        // ==================================================================

        [InGameTest(Category = Category,
            Description = "Pause-while-InTransit provenance driven live against a RESOLUTION kind: TryPause on an InTransit route arms PauseAfterCurrentCycle WITHOUT SendOnceArmed, and a DestinationFull crossing (real ERS sources, real destination vessel) COMPLETES that pause (Paused, reason blocked-then-paused, hold kept, armedBy=pause-after-cycle) with NO send-once toast - nobody is watching for that run")]
        public void PauseInTransit_ResolutionBlock_CompletesThePause_NoSendOnceToast()
        {
            double liveUT = RequireLiveContext();
            RequireRenderCompositionUnarmed();
            RouteSourceRef sourceRef = RequireLiveErsSourceRef();
            Vessel destination = RequireLiveDestinationVessel();

            double baseUT = liveUT + SyntheticBandOffset;
            double crossingUT = baseUT + DockPhaseSeconds;
            string routeId = NewId("pause-intransit-resolution");
            Route route = BuildResolutionBlockRoute(routeId, "Parsek RouteLifecycle in-transit pause",
                RouteStatus.InTransit, baseUT, sourceRef, destination.persistentId);

            var lines = new List<string>();
            var toasts = new List<string>();
            using (var arena = new LifecycleArena(lines, toasts, route, baseUT))
            {
                RequireLiveResolutionBlock(route, crossingUT, arena.Env, destination);

                // Arm through the REAL InTransit branch (env-injected overload, so
                // the recovery-credit flush runs against the same live env the tick
                // uses) rather than by poking the field.
                InGameAssert.IsTrue(RouteOrchestrator.TryPause(route, baseUT + 1.0, arena.Env),
                    "TryPause returned false for an InTransit route");
                InGameAssert.IsTrue(route.PauseAfterCurrentCycle,
                    "TryPause did not arm PauseAfterCurrentCycle on the InTransit branch");
                InGameAssert.IsFalse(route.SendOnceArmed,
                    "TryPause must not set the Send Once provenance");
                InGameAssert.AreEqual(RouteStatus.InTransit, route.Status,
                    "TryPause must leave an InTransit route in transit until the cycle resolves");
                int beforeTick = Ledger.Actions.Count;

                RouteOrchestrator.Tick(crossingUT, arena.Env);

                InGameAssert.AreEqual(RouteStatus.Paused, route.Status,
                    $"The {ResolutionBlockKind} cycle did not complete the armed pause " +
                    $"(status={route.Status})");
                InGameAssert.IsFalse(route.PauseAfterCurrentCycle,
                    "PauseAfterCurrentCycle survived the blocked cycle");
                InGameAssert.AreEqual(1, route.SkippedCycles,
                    $"SkippedCycles not bumped (got {route.SkippedCycles.ToString(IC)})");
                InGameAssert.AreEqual(ResolutionBlockKind, route.LastHoldKind,
                    $"Hold kind not retained across the armed pause (got {route.LastHoldKind})");

                // World safety: the stop is a REAL vessel, so nothing may be written.
                InGameAssert.AreEqual(0, CountRouteRows(routeId, GameActionType.RouteCargoDelivered),
                    "A blocked cycle emitted a RouteCargoDelivered row against a REAL vessel");
                InGameAssert.AreEqual(0, CountRouteRows(routeId, GameActionType.RouteCargoDebited),
                    "A blocked cycle emitted a RouteCargoDebited row");

                GameAction pauseRow = LatestRouteRow(routeId, GameActionType.RoutePaused, beforeTick);
                InGameAssert.IsNotNull(pauseRow, "No RoutePaused row for the completed in-transit pause");
                InGameAssert.AreEqual(RouteOrchestrator.BlockedThenPausedReason,
                    pauseRow.RouteEndpointReason,
                    $"RoutePaused row reason mismatch (got '{pauseRow.RouteEndpointReason ?? "<null>"}')");

                InGameAssert.IsTrue(
                    AnyLine(lines, "ArmedPause:", "armedBy=pause-after-cycle"),
                    "No 'ArmedPause: ... armedBy=pause-after-cycle' log line observed");

                // The provenance discriminator: no toast for a pause-after-cycle arm.
                InGameAssert.AreEqual(0, toasts.Count,
                    $"A pause-after-cycle arm posted {toasts.Count.ToString(IC)} screen message(s); " +
                    "only the Send Once provenance is a run the player is waiting on");

                ParsekLog.Info("TestRunner",
                    $"RouteLifecycle pause-intransit-resolution: PASS routeId={routeId} " +
                    $"holdKind={route.LastHoldKind} holdDetail={route.LastHoldDetail ?? "<none>"} " +
                    $"destPid={destination.persistentId.ToString(IC)} " +
                    $"crossingUT={crossingUT.ToString("R", IC)} armedBy=pause-after-cycle");
            }
        }

        // ==================================================================
        // (d2) The postponement half of the pause-in-transit provenance
        // ==================================================================

        [InGameTest(Category = Category,
            Description = "The pause-after-cycle sibling of the send-once postponement pin: TryPause arms an InTransit route, its crossing blocks on SourcesStale (a postponement), and the pause is NOT completed - the route stays InTransit with the arm intact, the crossing is consumed as skipped, and no RoutePaused row, no ledger row and no toast are produced")]
        public void PauseInTransit_PostponementBlock_KeepsArm_StaysInTransit()
        {
            double liveUT = RequireLiveContext();
            RequireRenderCompositionUnarmed();

            double baseUT = liveUT + SyntheticBandOffset;
            double crossingUT = baseUT + DockPhaseSeconds;
            string routeId = NewId("pause-intransit-postponed");
            Route route = BuildLoopRoute(routeId, "Parsek RouteLifecycle in-transit pause",
                RouteStatus.InTransit, baseUT);

            var lines = new List<string>();
            var toasts = new List<string>();
            using (var arena = new LifecycleArena(lines, toasts, route, baseUT))
            {
                RequireLivePostponementBlock(route, crossingUT, arena.Env);

                InGameAssert.IsTrue(RouteOrchestrator.TryPause(route, baseUT + 1.0, arena.Env),
                    "TryPause returned false for an InTransit route");
                InGameAssert.IsTrue(route.PauseAfterCurrentCycle,
                    "TryPause did not arm PauseAfterCurrentCycle on the InTransit branch");
                InGameAssert.IsFalse(route.SendOnceArmed,
                    "TryPause must not set the Send Once provenance");
                int beforeTick = Ledger.Actions.Count;

                RouteOrchestrator.Tick(crossingUT, arena.Env);

                InGameAssert.AreEqual(RouteStatus.InTransit, route.Status,
                    $"A postponement block completed the armed pause (status={route.Status}); " +
                    "the cycle has not resolved, so the pause must wait for one that does");
                InGameAssert.IsTrue(route.PauseAfterCurrentCycle,
                    "PauseAfterCurrentCycle was consumed by a POSTPONEMENT block");
                InGameAssert.AreEqual(1, route.SkippedCycles,
                    $"SkippedCycles not bumped (got {route.SkippedCycles.ToString(IC)})");
                InGameAssert.AreEqual(PostponementBlockKind, route.LastHoldKind,
                    $"Hold kind not recorded (got {route.LastHoldKind})");
                InGameAssert.AreEqual(PostponementBlockDetail, route.LastHoldDetail,
                    $"Hold detail not recorded (got '{route.LastHoldDetail ?? "<null>"}')");

                InGameAssert.IsNull(LatestRouteRow(routeId, GameActionType.RoutePaused, beforeTick),
                    "A postponement block emitted a RoutePaused row");
                InGameAssert.AreEqual(beforeTick, Ledger.Actions.Count,
                    "A postponement block emitted ledger rows (it must emit NOTHING)");
                InGameAssert.AreEqual(0, toasts.Count,
                    "A pause-after-cycle arm must never toast, postponed or not");

                InGameAssert.IsTrue(
                    AnyLine(lines, "ArmedPause:", "BLOCKED by postponement",
                        "kind=" + PostponementBlockKind, "arm kept", "route stays InTransit"),
                    "No 'ArmedPause: ... BLOCKED by postponement ... arm kept, route stays InTransit' " +
                    "line observed (Verbose; the arena force-arms verbose logging for exactly this)");
                InGameAssert.IsFalse(
                    AnyLine(lines, "ArmedPause:", RouteOrchestrator.BlockedThenPausedReason),
                    "A postponement block ran the blocked-then-paused resolution tail");

                ParsekLog.Info("TestRunner",
                    $"RouteLifecycle pause-intransit-postponed: PASS routeId={routeId} " +
                    $"crossingUT={crossingUT.ToString("R", IC)} hold={route.LastHoldKind} " +
                    "armKept=true status=InTransit");
            }
        }

        // ==================================================================
        // (e) Negative control: the ordinary loop keeps looping through a block
        // ==================================================================

        [InGameTest(Category = Category,
            Description = "NEGATIVE CONTROL for the fix: an UNARMED route whose crossing blocks stays Active and keeps looping - the hold is recorded and SkippedCycles bumps, but no pause transition, no RoutePaused row and no toast. Without this the fix could silently pause every blocked loop route")]
        public void UnarmedRoute_BlockedCycle_StaysActive_NoPauseMarker()
        {
            double liveUT = RequireLiveContext();
            RequireRenderCompositionUnarmed();

            double baseUT = liveUT + SyntheticBandOffset;
            double crossingUT = baseUT + DockPhaseSeconds;
            string routeId = NewId("unarmed-blocked");
            Route route = BuildLoopRoute(routeId, "Parsek RouteLifecycle unarmed",
                RouteStatus.Active, baseUT);

            var lines = new List<string>();
            var toasts = new List<string>();
            using (var arena = new LifecycleArena(lines, toasts, route, baseUT))
            {
                RequireLivePostponementBlock(route, crossingUT, arena.Env);
                int beforeTick = Ledger.Actions.Count;

                RouteOrchestrator.Tick(crossingUT, arena.Env);

                InGameAssert.AreEqual(RouteStatus.Active, route.Status,
                    $"An UNARMED route was paused by a blocked cycle (status={route.Status}) - " +
                    "the armed-pause fix must not touch the ordinary loop");
                InGameAssert.IsFalse(route.PauseAfterCurrentCycle,
                    "A blocked cycle armed PauseAfterCurrentCycle on an unarmed route");
                InGameAssert.AreEqual(1, route.SkippedCycles,
                    $"SkippedCycles not bumped (got {route.SkippedCycles.ToString(IC)})");
                InGameAssert.AreEqual(0L, route.LastObservedLoopCycleIndex,
                    "Crossing index not snapped forward on the unarmed blocked cycle");
                InGameAssert.AreEqual(PostponementBlockKind, route.LastHoldKind,
                    $"Hold not recorded on the unarmed blocked cycle (got {route.LastHoldKind})");

                InGameAssert.IsNull(LatestRouteRow(routeId, GameActionType.RoutePaused, beforeTick),
                    "An unarmed blocked cycle emitted a RoutePaused row");
                InGameAssert.AreEqual(beforeTick, Ledger.Actions.Count,
                    "An unarmed blocked cycle emitted ledger rows (it must emit NOTHING)");
                InGameAssert.AreEqual(0, toasts.Count,
                    "An unarmed blocked cycle posted a screen message");
                InGameAssert.IsFalse(AnyLine(lines, "ArmedPause:"),
                    "An unarmed blocked cycle ran the armed-pause tail");

                ParsekLog.Info("TestRunner",
                    $"RouteLifecycle unarmed-blocked: PASS routeId={routeId} " +
                    $"skipped={route.SkippedCycles.ToString(IC)}");
            }
        }

        // ==================================================================
        // (f) The RouteCommand seam's create gate, against the LIVE stores
        // ==================================================================

        [InGameTest(Category = Category,
            Description = "Live sibling of the headless RouteCommand seam tests: the action=create candidacy gate walked against the REAL committed store and RouteCandidateFinder - an unknown tree id classifies UnknownTree, a planted UNSEALED tree classifies TreeNotSealed, and sealing its recordings advances the walk past that gate (no route is created; a synthetic tree is never analysis-eligible)")]
        public void RouteCommandSeam_CreateGateWalk_LiveCommittedTree()
        {
            double liveUT = RequireLiveContext();

            string treeId = NewId("routecmd-tree");
            var committedRecs = new List<Recording>();
            bool treeAdded = false;
            var lines = new List<string>();
            Action<string> prevObserver = ParsekLog.TestObserverForTesting;

            try
            {
                ParsekLog.TestObserverForTesting = l => { lines.Add(l); prevObserver?.Invoke(l); };

                // (i) Unknown tree: the first gate in DeriveCandidates' order.
                InGameAssert.AreEqual(
                    RouteCreateRefusal.UnknownTree,
                    ClassifyLiveCreateRefusal(NewId("routecmd-absent")),
                    "An id no committed tree carries must classify UnknownTree");

                // (ii) Plant an UNSEALED two-leg tree in the live committed store.
                RecordingTree tree = BuildTwoLegTree(treeId);
                foreach (Recording rec in tree.Recordings.Values)
                    rec.MergeState = MergeState.CommittedProvisional;
                RecordingStore.AddCommittedTreeForTesting(tree);
                treeAdded = true;
                foreach (Recording rec in tree.Recordings.Values)
                {
                    RecordingStore.AddCommittedInternal(rec);
                    committedRecs.Add(rec);
                }

                InGameAssert.IsFalse(RouteCandidateFinder.IsTreeFullySealed(tree),
                    "A CommittedProvisional tree read as fully sealed");
                InGameAssert.AreEqual(
                    RouteCreateRefusal.TreeNotSealed, ClassifyLiveCreateRefusal(treeId),
                    "An unsealed committed tree must classify TreeNotSealed (run SealSlot first)");

                // (iii) Seal it: the gate the SealSlot verb closes. The walk must now
                // get PAST TreeNotSealed. It does NOT reach None - a synthetic tree
                // carries no route proof, so the analysis refuses it - and that is
                // the point: the classification advances to the NEXT gate rather
                // than sticking, which is what proves the walk order is live.
                foreach (Recording rec in tree.Recordings.Values)
                    rec.MergeState = MergeState.Immutable;
                InGameAssert.IsTrue(RouteCandidateFinder.IsTreeFullySealed(tree),
                    "A tree whose recordings are all Immutable did not read as sealed");

                RouteCreateRefusal sealedRefusal = ClassifyLiveCreateRefusal(treeId);
                InGameAssert.AreNotEqual(RouteCreateRefusal.TreeNotSealed, sealedRefusal,
                    "Sealing the tree did not advance the create gate walk past TreeNotSealed");
                InGameAssert.AreNotEqual(RouteCreateRefusal.UnknownTree, sealedRefusal,
                    "The planted tree stopped resolving in the live committed store");
                InGameAssert.AreEqual(RouteCreateRefusal.CandidateIneligible, sealedRefusal,
                    $"A synthetic sealed tree classified {sealedRefusal}; the live route analysis " +
                    "was expected to refuse it (no route proof, no dock window)");

                // The wire token a driven lane classifies off (compound tail on the
                // ineligible branch, per the refly-gate shape).
                string msg = TestCommandRouteCommand.RefusalMsg(
                    sealedRefusal, RouteAnalysisStatus.MissingRouteProof);
                InGameAssert.IsNotNull(msg, "RefusalMsg returned null for a refusal");
                InGameAssert.Contains(msg, TestCommandRouteCommand.CandidateIneligibleReason,
                    $"Refusal msg '{msg}' does not lead with the candidate-ineligible token");

                ParsekLog.Info("TestRunner",
                    $"RouteLifecycle routecommand-gate: PASS treeId={treeId} " +
                    $"sealedRefusal={sealedRefusal} liveUT={liveUT.ToString("R", IC)}");
            }
            finally
            {
                ParsekLog.TestObserverForTesting = prevObserver;
                for (int i = 0; i < committedRecs.Count; i++)
                    RecordingStore.RemoveCommittedInternal(committedRecs[i]);
                if (treeAdded) RemoveCommittedTree(treeId);
                MissionStore.PruneOrphans(RecordingStore.CommittedTrees);
            }
        }

        // ==================================================================
        // Arena (arrange + guaranteed restore for the ticking cells)
        // ==================================================================

        /// <summary>
        /// Owns EVERY global a ticking cell touches and restores all of them on
        /// dispose, so an assertion that throws mid-cell can never leave a
        /// synthetic route, a stale ledger row, an installed loop-unit resolver, a
        /// forced verbose-logging override or a hijacked screen-message sink behind
        /// in the player's session. Mirrors the RouteRewindTimeline seam tests'
        /// <c>finally</c> block, hoisted into a struct because six of this
        /// category's globals travel together.
        /// </summary>
        private struct LifecycleArena : IDisposable
        {
            private readonly List<GameAction> preLedger;
            private readonly List<Route> preCommitted;
            private readonly List<Route> preDormant;
            private readonly Action<string> prevObserver;
            private readonly Action<string, float> prevScreenSink;
            private readonly bool? prevVerboseOverride;

            /// <summary>The PRODUCTION environment every cell drives its tick with.</summary>
            internal LiveRouteRuntimeEnvironment Env { get; }

            internal LifecycleArena(
                List<string> lines, List<string> toasts, Route route, double baseUT)
            {
                preLedger = SnapshotLedger();
                SnapshotRouteLists(out preCommitted, out preDormant);
                prevObserver = ParsekLog.TestObserverForTesting;
                prevScreenSink = ParsekLog.ScreenMessageSinkForTesting;
                Env = new LiveRouteRuntimeEnvironment();

                // The two lines these cells assert on - `ArmedPause: ... BLOCKED by
                // postponement ... arm kept` and the PartnerGate audit - are
                // ParsekLog.Verbose, which is gated on the LIVE
                // ParsekSettings.verboseLogging. A session with verbose off would
                // make an arm-kept cell fail on a MISSING LINE while the product
                // behaved correctly, so force the override on for the tick and
                // restore whatever was there. [ThreadStatic], and these cells are
                // synchronous, so it cannot leak into the 1 Hz background tick.
                prevVerboseOverride = ParsekLog.VerboseOverrideForTesting;
                ParsekLog.VerboseOverrideForTesting = true;

                Action<string> observer = prevObserver;
                ParsekLog.TestObserverForTesting = l => { lines.Add(l); observer?.Invoke(l); };
                // Capture the toast INSTEAD of posting it: a synthetic route's screen
                // message would be a confusing artifact in the player's session.
                ParsekLog.ScreenMessageSinkForTesting = (msg, dur) => toasts.Add(msg ?? string.Empty);

                // Store isolation (see the class doc): the tick processes EVERY
                // committed route, and the resolver installed below answers for any
                // of them.
                RouteStore.InstallRoutesAtRewind(new List<Route> { route }, new List<Route>());
                RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => BuildUnit(baseUT);
            }

            public void Dispose()
            {
                RouteOrchestrator.LoopUnitResolverForTesting = null;
                ParsekLog.VerboseOverrideForTesting = prevVerboseOverride;
                ParsekLog.ScreenMessageSinkForTesting = prevScreenSink;
                ParsekLog.TestObserverForTesting = prevObserver;
                RouteStore.InstallRoutesAtRewind(preCommitted, preDormant);
                RestoreLedger(preLedger);
            }
        }

        // ==================================================================
        // Preconditions
        // ==================================================================

        /// <summary>
        /// Common precondition gate (mirrors the RouteRewindTimeline helper):
        /// skips attributably when the live context these cells need is absent,
        /// and returns the live UT. The lifecycle markers these cells assert on
        /// are refused by <c>EmitRouteLifecycleMarker</c> at UT &lt;= 0, so a
        /// session without a healthy clock must skip rather than fail.
        /// </summary>
        private static double RequireLiveContext()
        {
            if (HighLogic.CurrentGame == null)
                InGameAssert.Skip("HighLogic.CurrentGame is null; need a loaded game");
            double liveUT;
            try
            {
                liveUT = Planetarium.GetUniversalTime();
            }
            catch (Exception ex)
            {
                InGameAssert.Skip($"Planetarium.GetUniversalTime threw {ex.GetType().Name}; no live clock");
                return 0.0; // unreachable
            }
            if (liveUT <= 0.0)
            {
                InGameAssert.Skip(
                    $"Live UT {liveUT.ToString("R", IC)} <= 0; the lifecycle markers these cells " +
                    "assert on would be skipped by the UT guard");
            }
            return liveUT;
        }

        /// <summary>
        /// Skip guard for the crossing-driving cells. A confirmed dock crossing
        /// unconditionally appends a <c>route-dock-crossing</c> CLOCK-EVENT to the
        /// M-A7 render-composition manifest whenever that capture is armed, and a
        /// synthetic route's crossing is not part of the composition the lane
        /// exporting that manifest is measuring. Skipping (attributably) is the
        /// honest answer: a lane that wants BOTH runs them as separate flights.
        /// </summary>
        private static void RequireRenderCompositionUnarmed()
        {
            if (Parsek.MapRender.RenderCompositionRecorder.IsEnabled)
            {
                InGameAssert.Skip(
                    "M-A7 render-composition capture is ARMED; driving a synthetic dock " +
                    "crossing would inject route-dock-crossing clock events into the manifest " +
                    "this run exports. Run this category on a flight that does not export a " +
                    "render manifest");
            }
        }

        /// <summary>
        /// Pre-tick safety + stability gate for the POSTPONEMENT cells: refuses to
        /// drive the crossing unless the LIVE environment has already judged this
        /// cycle blocked with the kind the cell asserts. Fails closed in both
        /// directions - an ELIGIBLE verdict is a hard failure (a real cycle would
        /// have dispatched against live world state), a DIFFERENT block kind is a
        /// skip (the cell would be measuring a gate it was not written for).
        /// </summary>
        private static void RequireLivePostponementBlock(
            Route route, double currentUT, IRouteRuntimeEnvironment env)
        {
            RouteDispatchEvaluator.EligibilityResult elig =
                RouteDispatchEvaluator.CheckEligibility(route, currentUT, env);
            if (elig.Eligible)
            {
                InGameAssert.Fail(
                    "The live environment judged a SYNTHETIC route eligible to dispatch - " +
                    "refusing to tick, because the cycle would emit debits and deliveries " +
                    "against live world state");
            }
            if (elig.Kind != PostponementBlockKind)
            {
                InGameAssert.Skip(
                    $"Live eligibility blocked with kind={elig.Kind} reason={elig.Reason ?? "<none>"} " +
                    $"instead of the expected {PostponementBlockKind}; this cell is written against " +
                    "the ERS gate and would otherwise measure a different one");
            }
        }

        /// <summary>
        /// The RESOLUTION-side twin of <see cref="RequireLivePostponementBlock"/>,
        /// and the reason the two live-source cells are safe to run at all. Same
        /// fail-closed shape: an ELIGIBLE verdict is a hard FAILURE (the tick would
        /// debit and deliver against a REAL vessel), any other kind is an
        /// attributable SKIP naming what the live gate actually answered.
        ///
        /// <para>The skip is the honest outcome for a save whose destination
        /// somehow HAS capacity for <see cref="ResolutionBlockAmount"/>, or whose
        /// earlier gates refuse (the ERS id went stale between the pick and the
        /// tick, the vessel unloaded out of the pid map, a pickup source appeared).
        /// Each of those is a different gate and the cell would be measuring it
        /// silently.</para>
        /// </summary>
        private static void RequireLiveResolutionBlock(
            Route route, double currentUT, IRouteRuntimeEnvironment env, Vessel destination)
        {
            RouteDispatchEvaluator.EligibilityResult elig =
                RouteDispatchEvaluator.CheckEligibility(route, currentUT, env);
            if (elig.Eligible)
            {
                InGameAssert.Fail(
                    "The live environment judged the synthetic route ELIGIBLE against real " +
                    $"destination '{destination?.vesselName ?? "<none>"}' " +
                    $"pid={(destination != null ? destination.persistentId.ToString(IC) : "<none>")} - " +
                    "refusing to tick, because the cycle would debit and deliver against live " +
                    $"world state. {ResolutionBlockAmount.ToString("R", IC)} units of " +
                    $"{ResolutionBlockResource} were expected to exceed every destination's capacity");
            }
            if (elig.Kind != ResolutionBlockKind)
            {
                InGameAssert.Skip(
                    $"Live eligibility blocked with kind={elig.Kind} reason={elig.Reason ?? "<none>"} " +
                    $"instead of the expected {ResolutionBlockKind}; an earlier gate " +
                    "(ERS sources / endpoint / origin cargo / funds) refused first, so this cell " +
                    "would be measuring a gate it was not written for");
            }
        }

        /// <summary>
        /// A source ref pointing at a REAL recording in the LIVE ERS snapshot, or an
        /// attributable skip. This is the ONLY thing standing between a synthetic
        /// route and the ERS gate: <c>LiveRouteRuntimeEnvironment.RouteHasValidSourcesInErs</c>
        /// requires every <see cref="RouteSourceRef.RecordingId"/> to be a KEY of the
        /// per-tick ERS-by-id dictionary and reads NOTHING else off the ref - not the
        /// proof hash, not the epoch, not the tree id, not the UT span. The remaining
        /// fields are filled anyway, mirroring <c>RouteBuilder</c> field for field, so
        /// the fixture is production-shaped rather than minimally sufficient.
        ///
        /// <para>Read through <see cref="RouteOrchestrator.SafeComputeErs"/> - the
        /// SAME <see cref="EffectiveState.ComputeERS"/> surface the live env builds
        /// its dictionary from - so the pick cannot disagree with the gate. (In-game
        /// tests are ERS-audit-allowlisted, but routing through the production helper
        /// is what makes the two snapshots the same computation.)</para>
        /// </summary>
        private static RouteSourceRef RequireLiveErsSourceRef()
        {
            IReadOnlyList<Recording> ers = RouteOrchestrator.SafeComputeErs();
            if (ers == null || ers.Count == 0)
            {
                InGameAssert.Skip(
                    "The live ERS snapshot is empty, so no synthetic route can pass the ERS " +
                    "gate and reach a RESOLUTION eligibility kind; this cell needs a save " +
                    "carrying at least one committed recording");
                return null; // unreachable
            }

            for (int i = 0; i < ers.Count; i++)
            {
                Recording rec = ers[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) continue;
                return new RouteSourceRef
                {
                    RecordingId = rec.RecordingId,
                    TreeId = rec.TreeId,
                    TreeOrder = rec.TreeOrder,
                    RecordingFormatVersion = rec.RecordingFormatVersion,
                    RecordingSchemaGeneration = rec.RecordingSchemaGeneration,
                    SidecarEpoch = rec.SidecarEpoch,
                    StartUT = rec.StartUT,
                    EndUT = rec.EndUT,
                    RouteProofHash = RouteProofHasher.ComputeRouteProofHashFromRecording(rec),
                };
            }

            InGameAssert.Skip(
                $"The live ERS snapshot holds {ers.Count.ToString(IC)} entr(y/ies) but none " +
                "carries a usable RecordingId, so the ERS gate cannot be satisfied");
            return null; // unreachable
        }

        /// <summary>
        /// A REAL live vessel to point the synthetic stop at, or an attributable
        /// skip. Requirements, in the order they matter:
        /// <list type="bullet">
        ///   <item>NOT a ghost map vessel - <c>RouteEndpointResolver</c> excludes
        ///     those by <see cref="GhostMapPresence.IsGhostMapVessel"/>, so one
        ///     would resolve to nothing and the cell would measure EndpointLost.</item>
        ///   <item>NOT the ACTIVE vessel. Nothing here writes, but the active craft
        ///     is the one the batch's own baseline restore is anchored on, and a
        ///     capacity PROBE walking its live part list is needless proximity to
        ///     the thing a test must not disturb.</item>
        ///   <item>A real craft, not a <c>SpaceObject</c> (asteroid) / <c>Flag</c> /
        ///     <c>Unknown</c>: those carry no resource capacity worth probing and,
        ///     for asteroids, a pid population this category deliberately avoids.</item>
        ///   <item>LANDED / SPLASHED / PRELAUNCH preferred - the stable, host-shaped
        ///     population (the RVR fixture boots a landed rover beside a landed
        ///     base) - falling back to any other qualifying vessel so the cell is
        ///     scene-agnostic rather than fixture-bound.</item>
        /// </list>
        /// </summary>
        private static Vessel RequireLiveDestinationVessel()
        {
            IReadOnlyList<Vessel> vessels = null;
            try
            {
                vessels = FlightGlobals.Vessels;
            }
            catch (Exception ex)
            {
                InGameAssert.Skip($"FlightGlobals.Vessels threw {ex.GetType().Name}; no live vessel list");
            }
            if (vessels == null || vessels.Count == 0)
            {
                InGameAssert.Skip(
                    "No live vessels in this save, so the synthetic stop has no REAL endpoint " +
                    "to resolve and the cell cannot reach the destination-capacity gate");
                return null; // unreachable
            }

            Vessel active = null;
            try { active = FlightGlobals.ActiveVessel; } catch { }

            Vessel fallback = null;
            int examined = 0;
            for (int i = 0; i < vessels.Count; i++)
            {
                Vessel v = vessels[i];
                if (v == null) continue;
                if (v.persistentId == 0u) continue;
                if (active != null && ReferenceEquals(v, active)) continue;
                if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) continue;
                if (v.vesselType == VesselType.SpaceObject
                    || v.vesselType == VesselType.Flag
                    || v.vesselType == VesselType.Unknown)
                {
                    continue;
                }
                examined++;

                // The resolver is the authority, not this walk: only accept a
                // candidate the PRODUCTION endpoint resolver actually returns for
                // the pid the route will carry.
                var endpoint = new RouteEndpoint { VesselPersistentId = v.persistentId };
                if (!RouteEndpointResolver.TryResolveEndpoint(endpoint, out Vessel resolved, out _)
                    || resolved == null)
                {
                    continue;
                }

                if (v.situation == Vessel.Situations.LANDED
                    || v.situation == Vessel.Situations.SPLASHED
                    || v.situation == Vessel.Situations.PRELAUNCH)
                {
                    return resolved;
                }
                if (fallback == null) fallback = resolved;
            }

            if (fallback != null) return fallback;

            InGameAssert.Skip(
                $"No usable destination vessel among {vessels.Count.ToString(IC)} live vessel(s) " +
                $"({examined.ToString(IC)} survived the ghost / active / vessel-type filters, none " +
                "resolved through RouteEndpointResolver); this cell needs one REAL non-active, " +
                "non-ghost craft to point the synthetic stop at");
            return null; // unreachable
        }

        // ==================================================================
        // Fixture builders
        // ==================================================================

        private static string NewId(string label)
        {
            return "ingame-rlc-" + label + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        /// <summary>
        /// The synthetic backing-mission loop unit: span
        /// <c>[baseUT, baseUT + SpanSeconds]</c> with cadence == span (so the clock
        /// never idles in an inter-cycle tail) anchored at <c>baseUT</c>. A tick at
        /// <c>baseUT + DockPhaseSeconds</c> therefore lands cycle 0 exactly at the
        /// route's recorded dock phase: one confirmed crossing, deterministically.
        /// </summary>
        private static GhostPlaybackLogic.LoopUnit BuildUnit(double baseUT)
        {
            return new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0,
                memberIndices: new[] { 0 },
                spanStartUT: baseUT,
                spanEndUT: baseUT + SpanSeconds,
                cadenceSeconds: SpanSeconds,
                phaseAnchorUT: baseUT);
        }

        /// <summary>
        /// A single-stop loop route in the synthetic UT band.
        /// <c>BackingMissionTreeId</c> is what makes <see cref="Route.IsLoopRoute"/>
        /// true (the loop-clock path); the source ref is a fresh GUID id that no ERS
        /// snapshot can contain, which is the deterministic block these cells drive.
        /// The stop endpoint carries a synthetic pid with no surface anchor, so the
        /// live resolver refuses it too (cell 1).
        /// </summary>
        private static Route BuildLoopRoute(string id, string name, RouteStatus status, double baseUT)
        {
            return new Route
            {
                Id = id,
                Name = name,
                Status = status,
                IsKscOrigin = false,
                BackingMissionTreeId = id + "-tree",
                RecordedDockUT = baseUT + DockPhaseSeconds,
                DockMemberRecordingId = id + "-dock",
                LoopAnchorUT = baseUT,
                LastObservedLoopCycleIndex = -1,
                NextDispatchUT = baseUT,
                DispatchInterval = SpanSeconds,
                TransitDuration = SpanSeconds,
                CadenceMultiplier = 1,
                CostManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = SyntheticDestinationPid },
                        DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                    },
                },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef
                    {
                        RecordingId = id + "-dock",
                        TreeId = id + "-tree",
                        RouteProofHash = "ingame-rlc",
                    },
                },
            };
        }

        /// <summary>
        /// The RESOLUTION-block fixture: the same single-stop loop route, rebuilt so
        /// every eligibility gate BEFORE destination capacity passes against the
        /// live environment, and that one fails.
        ///
        /// <para>Gate by gate, in <c>RouteDispatchEvaluator.CheckEligibility</c>'s
        /// own order:</para>
        /// <list type="number">
        ///   <item>(4) ERS - <paramref name="sourceRef"/> names a recording that IS
        ///     in the live ERS snapshot.</item>
        ///   <item>(5) endpoint - the stop carries <paramref name="destinationPid"/>,
        ///     a REAL live vessel pid the production resolver returns. The ORIGIN
        ///     endpoint is skipped entirely because the route is
        ///     <c>IsHarvestOrigin</c>: a harvest origin has no origin vessel by
        ///     design (plan D7), which is the only way a synthetic route can pass
        ///     gate 5 without a second real craft to point <c>Route.Origin</c> at.</item>
        ///   <item>(6) origin cargo - the harvest-origin branch returns true (no
        ///     physical source to gate), and the single stop carries NO
        ///     <c>PickupManifest</c>, so the per-pickup-source gate groups zero
        ///     sources and passes.</item>
        ///   <item>(7) funds - <c>IsKscOrigin</c> is false, so the Career funds gate
        ///     does not run at all. This is deliberate: it keeps the verdict
        ///     identical on a career and a sandbox host, where a KSC origin would
        ///     hand a career save a FundsShort hold instead.</item>
        ///   <item>(8) destination capacity - <see cref="ResolutionBlockAmount"/>
        ///     units of <see cref="ResolutionBlockResource"/> fit in no craft, so
        ///     the plan is partial and the gate holds all-or-nothing. THIS is the
        ///     verdict the cells measure.</item>
        ///   <item>(9) partner - unreached; <c>LinkedRouteId</c> is null anyway.</item>
        /// </list>
        /// <c>CostManifest</c> is deliberately EMPTY (a harvest origin's is, by
        /// construction) so nothing about the origin half can influence the verdict.
        /// </summary>
        private static Route BuildResolutionBlockRoute(
            string id, string name, RouteStatus status, double baseUT,
            RouteSourceRef sourceRef, uint destinationPid)
        {
            return new Route
            {
                Id = id,
                Name = name,
                Status = status,
                IsKscOrigin = false,
                IsHarvestOrigin = true,
                BackingMissionTreeId = id + "-tree",
                RecordedDockUT = baseUT + DockPhaseSeconds,
                DockMemberRecordingId = sourceRef.RecordingId,
                LoopAnchorUT = baseUT,
                LastObservedLoopCycleIndex = -1,
                NextDispatchUT = baseUT,
                DispatchInterval = SpanSeconds,
                TransitDuration = SpanSeconds,
                CadenceMultiplier = 1,
                CostManifest = new Dictionary<string, double>(),
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = destinationPid },
                        DeliveryManifest = new Dictionary<string, double>
                        {
                            { ResolutionBlockResource, ResolutionBlockAmount },
                        },
                    },
                },
                SourceRefs = new List<RouteSourceRef> { sourceRef },
            };
        }

        /// <summary>Two-leg committed chain for the RouteCommand gate-walk cell.
        /// Ids carry the tree id so parallel-running saves cannot collide.</summary>
        private static RecordingTree BuildTwoLegTree(string treeId)
        {
            var tree = new RecordingTree { Id = treeId, RootRecordingId = treeId + "-leg0" };
            tree.Recordings[treeId + "-leg0"] = Leg(treeId + "-leg0", 0, 1000, 2000);
            tree.Recordings[treeId + "-leg1"] = Leg(treeId + "-leg1", 1, 2000, 3000);
            foreach (Recording rec in tree.Recordings.Values)
                rec.TreeId = treeId;
            return tree;
        }

        private static Recording Leg(string id, int chainIndex, double start, double end)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "Lifecycle",
                ChainId = "C0",
                ChainIndex = chainIndex,
                ChainBranch = 0,
                IsDebris = false,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
            };
        }

        // ==================================================================
        // Live probes / assertions
        // ==================================================================

        /// <summary>
        /// Walks the SAME create-candidacy gates the <c>RouteCommand</c> applier
        /// walks (<c>RouteCommandCreate</c>), against the live committed store, and
        /// returns the classification. Kept in the same order as
        /// <c>RouteCandidateFinder.DeriveCandidates</c>: the analysis runs only
        /// once the two cheap gates pass, exactly as production does.
        /// </summary>
        private static RouteCreateRefusal ClassifyLiveCreateRefusal(string treeId)
        {
            RecordingTree tree = FindCommittedTree(treeId);
            bool treeFound = tree != null;
            bool dismissed = treeFound && RouteStore.IsCandidateDismissed(treeId);
            bool treeSealed = treeFound && RouteCandidateFinder.IsTreeFullySealed(tree);

            bool eligible = false;
            bool alreadyPromoted = false;
            if (treeFound && !dismissed && treeSealed)
            {
                RouteAnalysisResult analysis =
                    RouteAnalysisEngine.AnalyzeTree(tree, RouteAnalysisLogMode.Quiet);
                eligible = analysis != null && analysis.IsEligible;
                // A synthetic tree never gets past the analysis, so the promotion
                // gate is unreachable here; computed anyway so the walk this cell
                // exercises is the applier's walk and not a truncated copy of it.
                if (eligible)
                    alreadyPromoted = IsSourceAlreadyPromoted(analysis);
            }

            return TestCommandRouteCommand.ClassifyCreateRefusal(
                treeFound, dismissed, treeSealed, eligible, alreadyPromoted);
        }

        private static bool IsSourceAlreadyPromoted(RouteAnalysisResult analysis)
        {
            string sourceId = analysis?.SourceRecording?.RecordingId;
            if (string.IsNullOrEmpty(sourceId))
                return false;
            IReadOnlyList<Route> routes = RouteStore.CommittedRoutes;
            for (int i = 0; i < routes.Count; i++)
            {
                Route r = routes[i];
                if (r?.RecordingIds == null) continue;
                for (int j = 0; j < r.RecordingIds.Count; j++)
                {
                    if (string.Equals(r.RecordingIds[j], sourceId, StringComparison.Ordinal))
                        return true;
                }
            }
            return false;
        }

        private static RecordingTree FindCommittedTree(string treeId)
        {
            var trees = RecordingStore.CommittedTrees;
            if (trees == null) return null;
            for (int i = 0; i < trees.Count; i++)
            {
                RecordingTree t = trees[i];
                if (t != null && string.Equals(t.Id, treeId, StringComparison.Ordinal))
                    return t;
            }
            return null;
        }

        // ==================================================================
        // Log / ledger helpers
        // ==================================================================

        private static bool AnyLine(List<string> lines, params string[] fragments)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                string l = lines[i];
                bool all = true;
                for (int f = 0; f < fragments.Length; f++)
                {
                    if (l.IndexOf(fragments[f], StringComparison.Ordinal) < 0)
                    {
                        all = false;
                        break;
                    }
                }
                if (all) return true;
            }
            return false;
        }

        /// <summary>Latest row of <paramref name="type"/> for
        /// <paramref name="routeId"/> at or after <paramref name="fromIndex"/>;
        /// null when none.</summary>
        private static GameAction LatestRouteRow(string routeId, GameActionType type, int fromIndex)
        {
            var actions = Ledger.Actions;
            if (actions == null) return null;
            for (int i = actions.Count - 1; i >= fromIndex && i >= 0; i--)
            {
                GameAction a = actions[i];
                if (a == null) continue;
                if (a.Type != type) continue;
                if (string.Equals(a.RouteId, routeId, StringComparison.Ordinal))
                    return a;
            }
            return null;
        }

        private static int CountRouteRows(string routeId, GameActionType type)
        {
            int count = 0;
            var actions = Ledger.Actions;
            if (actions == null) return 0;
            for (int i = 0; i < actions.Count; i++)
            {
                GameAction a = actions[i];
                if (a == null) continue;
                if (a.Type == type && string.Equals(a.RouteId, routeId, StringComparison.Ordinal))
                    count++;
            }
            return count;
        }

        private static List<GameAction> SnapshotLedger()
        {
            var snapshot = new List<GameAction>();
            var actions = Ledger.Actions;
            if (actions != null)
            {
                for (int i = 0; i < actions.Count; i++)
                    snapshot.Add(actions[i]);
            }
            return snapshot;
        }

        /// <summary>
        /// Restores the live ledger to the pre-test snapshot (Clear + AddActions,
        /// the verified surfaces). One full retry runs before giving up so a
        /// first-attempt failure cannot leave a partially restored ledger silently;
        /// a second failure Warn-logs both counts so the residue is attributable in
        /// the batch log. (Copied from the RouteRewindTimeline helper: the two
        /// categories must not share mutable test state.)
        /// </summary>
        private static void RestoreLedger(List<GameAction> snapshot)
        {
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    Ledger.Clear();
                    if (snapshot != null && snapshot.Count > 0)
                        Ledger.AddActions(snapshot);
                    return;
                }
                catch (Exception ex)
                {
                    int liveCount = 0;
                    try { liveCount = Ledger.Actions != null ? Ledger.Actions.Count : 0; } catch { }
                    ParsekLog.Warn("TestRunner",
                        $"RouteLifecycle cleanup: ledger restore attempt {attempt.ToString(IC)}/2 failed " +
                        $"({ex.GetType().Name}: {ex.Message}); snapshot={(snapshot?.Count ?? 0).ToString(IC)} " +
                        $"live={liveCount.ToString(IC)}" +
                        (attempt == 2 ? " - ledger may be partially restored" : "; retrying"));
                }
            }
        }

        private static void SnapshotRouteLists(out List<Route> committed, out List<Route> dormant)
        {
            committed = new List<Route>();
            dormant = new List<Route>();
            IReadOnlyList<Route> c = RouteStore.CommittedRoutes;
            for (int i = 0; i < c.Count; i++)
                if (c[i] != null) committed.Add(c[i]);
            IReadOnlyList<Route> d = RouteStore.DormantRoutes;
            for (int i = 0; i < d.Count; i++)
                if (d[i] != null) dormant.Add(d[i]);
        }

        private static void RemoveCommittedTree(string treeId)
        {
            var trees = RecordingStore.CommittedTrees;
            if (trees == null) return;
            var survivors = new List<RecordingTree>(trees.Count);
            for (int i = 0; i < trees.Count; i++)
            {
                RecordingTree t = trees[i];
                if (t != null && string.Equals(t.Id, treeId, StringComparison.Ordinal))
                    continue;
                survivors.Add(t);
            }
            RecordingStore.ClearCommittedTreesInternal();
            for (int i = 0; i < survivors.Count; i++)
                RecordingStore.AddCommittedTreeForTesting(survivors[i]);
        }
    }
}
