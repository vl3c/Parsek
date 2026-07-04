using System;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// (M-MIS-11 item 3) Semantic type of a span-clock <c>cycleIndex</c>. The raw
    /// <c>long</c> out of <see cref="GhostPlaybackLogic.TryComputeSpanLoopUT"/>
    /// means two different things depending on the unit, distinguished only by
    /// <c>unit.RelaunchSchedule != null</c>; this enum names the distinction at
    /// the RouteLoopClock/emitter layer so consumers cannot misread one as the
    /// other. Logistics M5's "CadenceMultiplier as a modulo on the
    /// scheduled-launch index" builds on this seam.
    /// </summary>
    internal enum CycleIndexKind
    {
        /// <summary>
        /// Flat-cadence index: <c>cycleIndex = floor((currentUT - PhaseAnchorUT)
        /// / clampedCadence)</c> (the uniform span-clock branch). Cycle START UTs
        /// obey the uniform arithmetic <c>PhaseAnchorUT + k * cadence</c>. NOTE:
        /// a RE-AIM unit (<see cref="GhostPlaybackLogic.LoopUnit.ReaimSchedule"/>
        /// valid, <see cref="GhostPlaybackLogic.LoopUnit.RelaunchSchedule"/> null
        /// BY CONSTRUCTION - the builder's re-aim branch sets
        /// <c>relaunchSchedule = null</c>) classifies Flat: its runtime index
        /// comes out of the same uniform branch over the synodic cadence, and
        /// that flat index doubles as the synodic window index
        /// (<c>ReaimPlaybackResolver</c>: <c>windowIndex = cycleIndex</c>), so
        /// the uniform per-CYCLE arithmetic stays valid. The within-cycle PHASE
        /// mapping of a re-aim unit is still non-identity (loiter cuts / holds),
        /// which is why <see cref="RouteLoopClock.TryComputeSecondsToNextDockCrossing"/>
        /// gates on <see cref="GhostPlaybackLogic.LoopUnit.LoiterCuts"/>
        /// separately - Flat kind alone does not license the naive dock-UT
        /// formula.
        /// </summary>
        Flat = 0,

        /// <summary>
        /// Scheduled-launch index: <c>cycleIndex = sIdx</c> resolved by
        /// <c>MissionRelaunchSchedule.TryResolveActiveLaunch</c> (the zero-drift
        /// non-uniform schedule branch). Launch UTs are NON-uniform; the flat
        /// arithmetic <c>PhaseAnchorUT + k * cadence</c> is INVALID for this
        /// kind - resolve launch UTs through the schedule only.
        /// </summary>
        Scheduled = 1,
    }

    /// <summary>
    /// (M5 D1) Route-side WINDOW-BASIS classification of a resolved loop unit -
    /// the RENDER-WINDOW semantics question ("which mechanism spaces the rendered
    /// launch windows, and who consumed the route's CadenceMultiplier N?"), which
    /// is a DIFFERENT question from <see cref="CycleIndexKind"/>'s index
    /// arithmetic (a re-aim unit deliberately classifies
    /// <see cref="CycleIndexKind.Flat"/> - truthful for the index math - while
    /// its windows are synodic). Derived per tick from the resolved unit by
    /// <see cref="RouteLoopClock.DeriveWindowBasis"/>; NEVER persisted (the
    /// persisted <c>Route.ReaimWindowBasisEngaged</c> marker is flip-detector
    /// memory only, not a basis cache).
    /// </summary>
    internal enum RouteWindowBasis
    {
        /// <summary>No schedule mechanism: the uniform flat cadence
        /// (<c>DispatchInterval = N x TransitDuration</c>) spaces the windows, so
        /// N is FULLY consumed by the cadence itself. The v0 same-body path and
        /// the honest fallback for a declined cross-parent build (M5 D7). The
        /// route-side residual modulo is OFF.</summary>
        FlatInterval = 0,

        /// <summary>Same-parent zero-drift <c>MissionRelaunchSchedule</c>
        /// (identical population to <see cref="CycleIndexKind.Scheduled"/>). The
        /// Missions-side schedule generator already throttles launches to
        /// <c>minSpacing = cadence = DispatchInterval</c>, so N is FULLY consumed
        /// Missions-side and the scheduled-launch index <c>sIdx</c> indexes the
        /// THROTTLED list: the residual modulo is 1 (every rendered launch
        /// delivers; a route-side modulo on <c>sIdx</c> would double-apply N).</summary>
        ZeroDriftSchedule = 1,

        /// <summary>Cross-parent re-aim: a supported <c>ReaimMissionPlan</c> +
        /// valid synodic <c>ReaimWindowSchedule</c> (the builder discarded the
        /// route's <c>DispatchInterval</c> on this path and renders EVERY synodic
        /// window), so N survives as the route-side residual modulo on the
        /// window index: deliver every Nth rendered window (M5 D2).</summary>
        ReaimWindows = 2,
    }

    /// <summary>
    /// (M5 D6) Basis-flip transition decision for one tick, produced by the pure
    /// <see cref="RouteLoopClock.EvaluateWindowBasisTransition"/> from the
    /// persisted flip-detector marker + the tick's derived basis. The
    /// orchestrator-side applier owns the actual field re-baselines.
    /// </summary>
    internal enum WindowBasisTransitionKind
    {
        /// <summary>Steady state - marker and basis agree; nothing to do.</summary>
        None = 0,

        /// <summary>Marker false, basis <see cref="RouteWindowBasis.ReaimWindows"/>:
        /// the build (re-)engaged re-aim. Re-baseline the cycle cursors DOWN into
        /// window space (<c>dockCycleIndex - 1</c>: exactly the current window owed)
        /// and reset the anchor, else a stale flat-space cursor exceeds every
        /// future synodic index and the route silently never delivers again
        /// (review C6, the permanent-skip blocker).</summary>
        Engage = 1,

        /// <summary>Marker true, basis NOT <see cref="RouteWindowBasis.ReaimWindows"/>:
        /// the build declined to faithful. Re-baseline the cycle cursors to the
        /// CURRENT index in the new space with NO fire this tick (fail-closed),
        /// clear the marker, reset the anchor - a stale window-space cursor
        /// compared against flat indices reads as a huge owed jump and would fire
        /// a delivery the player never scheduled.</summary>
        Decline = 2,
    }

    /// <summary>
    /// Loop-clock crossing detector for Supply Routes (design §0.4 / §0.5; plan
    /// Phase 4). Wraps the LOCKED Missions seam
    /// <see cref="GhostPlaybackLogic.TryComputeSpanLoopUT"/> so the route's
    /// dispatch PHASE is driven by the same span clock that renders the ghost,
    /// keeping the rendered loop and the delivery fire in lock-step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The backing-mission <see cref="GhostPlaybackLogic.LoopUnit"/> is built with the
    /// SAME <c>bodyInfo</c> the render seams use (DEL-1:
    /// <c>FlightGlobalsBodyInfo.Instance</c>, not <c>null</c>), so the delivery clock
    /// phase-locks exactly as the rendered ghost. The clock also passes the unit's OWN
    /// relaunch schedule (<see cref="GhostPlaybackLogic.LoopUnit.RelaunchSchedule"/>)
    /// and loiter cuts (<see cref="GhostPlaybackLogic.LoopUnit.LoiterCuts"/>) straight
    /// through into <see cref="GhostPlaybackLogic.TryComputeSpanLoopUT"/> (Phase 6
    /// hardening). Behaviour by scenario:
    /// <list type="bullet">
    ///   <item><b>Same-body suborbital / atmospheric</b> (rover to base): the Missions
    ///     layer emits no rotation constraint, so there is no phase-lock,
    ///     <c>RelaunchSchedule</c> / <c>LoiterCuts</c> are <c>null</c>, the cadence is
    ///     the raw <c>DispatchInterval</c>, and the span is UNCOMPRESSED (the recorded
    ///     dock UT maps directly into <c>[spanStart, spanEnd]</c>).</item>
    ///   <item><b>Same-body orbital</b> (tanker to LKO station): the run launches from a
    ///     rotating body and reaches its orbit, so the Missions layer emits a launch-pad
    ///     rotation constraint; the unit's <c>PhaseAnchorUT</c> snaps to the next
    ///     launch-pad window and <c>CadenceSeconds</c> quantizes up to a multiple of the
    ///     rotation period -- the SAME values the ghost renders on, so delivery fires on
    ///     the relaunch UTs the player sees (the DEL-1 fix; before it the
    ///     <c>bodyInfo:null</c> delivery clock over-fired at the raw interval).</item>
    ///   <item><b>Inter-body</b> (M5, BUILT): the unit carries either the
    ///     same-parent zero-drift <see cref="MissionRelaunchSchedule"/> or the
    ///     cross-parent re-aim synodic schedule built by the locked Missions
    ///     layer, and delivery fires on the SAME rendered windows through this
    ///     passthrough, with <c>Route.CadenceMultiplier</c> applied as the
    ///     RESIDUAL window modulo (<see cref="ResolveResidualCadence"/> /
    ///     <see cref="DeriveWindowBasis"/>).</item>
    /// </list>
    /// All consumed fields are READ-ONLY (no Missions/engine file is edited). Phase 5's
    /// <c>RouteBuilder</c> clamps <c>DispatchInterval &gt;= backingMissionSpan</c> and
    /// the DEL-2 dock-phase gate fires once per cycle, so ONE dock crossing equals ONE
    /// dispatch cycle regardless of whether the cadence was phase-locked.
    /// </para>
    /// <para>
    /// <b>cadence == span vs cadence &gt; span.</b> When the dispatch interval
    /// equals the span the clock never idles: every UT inside a cycle reports a
    /// loopUT strictly inside the span (or the rolled-back final frame at the
    /// back-to-back boundary), and <paramref name="isInInterCycleTail"/> is always
    /// false. When the dispatch interval EXCEEDS the span (the common v0 case,
    /// since the clamp is <c>&gt;=</c>), the clock parks at <c>spanEnd</c> for the
    /// tail of each cycle: the vessel has already arrived/undocked and nothing is
    /// in transit, so <paramref name="isInInterCycleTail"/> is true there and the
    /// crossing detector ignores those parked samples (no re-fire while parked).
    /// </para>
    /// <para>
    /// Reads only the route store surface + the route-owned
    /// <see cref="GhostPlaybackLogic.LoopUnit"/> accessors (consumed READ-ONLY),
    /// so it sits outside the ERS/ELS grep gate (mirrors
    /// <see cref="RouteGhostDriverSelector"/> / <see cref="RouteTreeGuard"/>).
    /// </para>
    /// </remarks>
    internal static class RouteLoopClock
    {
        private const string Tag = "Route";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// (M-MIS-11 item 2) The owed-dock-crossing emitter result: the cycle
        /// whose recorded-dock phase has most recently been reached/passed
        /// (<see cref="DockCycleIndex"/>, the fire-once snap-forward target) plus
        /// the semantic kind of that index (<see cref="Kind"/>, item 3). Emitted
        /// by <see cref="TryGetOwedDockCrossing"/>; consumed by the orchestrator's
        /// single-stop path (fire once for <see cref="DockCycleIndex"/>, snap the
        /// route marker to it), its multi-stop path (per-stop owed cycles feed the
        /// lowest-owed-cycle cMin selection), and by <see cref="IsDockCrossing"/>
        /// (the pre-M-MIS-11 predicate shape, kept as a thin wrapper so existing
        /// consumers and tests are byte-identical).
        /// </summary>
        internal readonly struct OwedDockCrossing
        {
            internal OwedDockCrossing(long dockCycleIndex, CycleIndexKind kind)
            {
                DockCycleIndex = dockCycleIndex;
                Kind = kind;
            }

            /// <summary>The 0-based cycle whose dock instant has most recently
            /// been reached/passed (<see cref="ComputeDockCycleIndex"/>). On a
            /// warp tick that jumps several cycles this is the HIGHEST cycle
            /// whose dock passed - the caller fires ONCE and snaps its
            /// last-observed marker to this, never replaying skipped cycles.
            /// Populated even when no crossing is owed (the caller's diagnostics
            /// log it on the no-crossing path).</summary>
            internal long DockCycleIndex { get; }

            /// <summary>Semantic kind of <see cref="DockCycleIndex"/> (item 3):
            /// flat-cadence counter vs zero-drift scheduled-launch index. See
            /// <see cref="CycleIndexKind"/> for the re-aim classification.</summary>
            internal CycleIndexKind Kind { get; }
        }

        /// <summary>
        /// (M-MIS-11 item 3) Derives the semantic kind of the unit's span-clock
        /// cycle index. Scheduled iff the unit carries a zero-drift
        /// <see cref="GhostPlaybackLogic.LoopUnit.RelaunchSchedule"/> (the branch
        /// of <see cref="GhostPlaybackLogic.TryComputeSpanLoopUT"/> that returns
        /// <c>sIdx</c> from <c>TryResolveActiveLaunch</c>); Flat otherwise -
        /// INCLUDING re-aim units, whose <c>ReaimSchedule</c> shapes the anchor /
        /// synodic cadence at BUILD time while the runtime index still resolves
        /// through the uniform flat branch (see the <see cref="CycleIndexKind.Flat"/>
        /// doc for why that is the truthful classification). Pure.
        /// </summary>
        internal static CycleIndexKind DeriveCycleIndexKind(GhostPlaybackLogic.LoopUnit unit)
        {
            return unit.RelaunchSchedule != null ? CycleIndexKind.Scheduled : CycleIndexKind.Flat;
        }

        /// <summary>
        /// Resolves the route's loop-clock state at <paramref name="currentUT"/>
        /// from its backing-mission <paramref name="unit"/>. Pass-through to
        /// <see cref="GhostPlaybackLogic.TryComputeSpanLoopUT"/> threading the
        /// unit's OWN <see cref="GhostPlaybackLogic.LoopUnit.RelaunchSchedule"/> +
        /// <see cref="GhostPlaybackLogic.LoopUnit.LoiterCuts"/> (Phase 6 hardening;
        /// both null for a v0 same-body route -> the uncompressed-span behavior is
        /// byte-identical, and a future inter-body route's synodic schedule fires
        /// delivery on the same re-aimed launches the ghost renders on). Returns
        /// false on the same early-return conditions as the inner clock
        /// (degenerate span, <paramref name="currentUT"/> before the phase anchor,
        /// or — with a non-null schedule — before the first scheduled launch) with
        /// <paramref name="cycleIndex"/> = 0 and
        /// <paramref name="isInInterCycleTail"/> = false.
        /// </summary>
        /// <param name="unit">The route's backing-mission loop unit (read-only).</param>
        /// <param name="currentUT">Game UT.</param>
        /// <param name="loopUT">Recorded UT inside <c>[spanStart, spanEnd]</c> the
        /// loop is currently playing.</param>
        /// <param name="cycleIndex">0-based span-clock cycle index. With the
        /// Phase 5 <c>DispatchInterval &gt;= span</c> clamp this equals the
        /// dispatch cycle index (one crossing == one cycle).</param>
        /// <param name="isInInterCycleTail">True when the clock is parked at
        /// <c>spanEnd</c> waiting for the next cycle (cadence &gt; span tail);
        /// false at every play sample and both early-return paths.</param>
        internal static bool TryGetRouteLoopState(
            GhostPlaybackLogic.LoopUnit unit,
            double currentUT,
            out double loopUT,
            out long cycleIndex,
            out bool isInInterCycleTail)
        {
            // Phase 6 hardening: thread the unit's OWN relaunch schedule + loiter
            // cuts (NOT hardcoded null). For a v0 same-body route the backing
            // Mission is faithful (bodyInfo=null), so both are null and this stays
            // the uncompressed-span path. For a future inter-body route they carry
            // the Missions-layer synodic / re-aim schedule, so delivery fires on the
            // same re-aimed launches the render uses. Consumed read-only.
            return GhostPlaybackLogic.TryComputeSpanLoopUT(
                currentUT,
                unit.PhaseAnchorUT,
                unit.SpanStartUT,
                unit.SpanEndUT,
                unit.CadenceSeconds,
                out loopUT,
                out cycleIndex,
                out isInInterCycleTail,
                schedule: unit.RelaunchSchedule,
                loiterCuts: unit.LoiterCuts);
        }

        /// <summary>
        /// True when <paramref name="recordedDockUT"/> lies within the unit's
        /// uncompressed span <c>[SpanStartUT, SpanEndUT]</c>. v0 maps the recorded
        /// dock UT directly into the span (no compression), so a dock UT outside
        /// the span means the delivery binding does not fall inside the rendered
        /// <c>[launch .. undock]</c> window and no crossing can ever fire.
        /// </summary>
        internal static bool IsDockUTInSpan(GhostPlaybackLogic.LoopUnit unit, double recordedDockUT)
        {
            return recordedDockUT >= unit.SpanStartUT && recordedDockUT <= unit.SpanEndUT;
        }

        /// <summary>
        /// The 0-based cycle whose recorded-dock PHASE has most recently been
        /// reached/passed at the current sample (DEL-2). Within a cycle the
        /// span clock's <paramref name="loopUT"/> climbs monotonically from
        /// <c>spanStart</c> toward <c>spanEnd</c>; the dock instant of that cycle
        /// is crossed once <paramref name="loopUT"/> reaches
        /// <paramref name="recordedDockUT"/>. So:
        /// <list type="bullet">
        ///   <item>if <paramref name="loopUT"/> &gt;= <paramref name="recordedDockUT"/>
        ///     the CURRENT cycle's dock has been reached -&gt; its
        ///     <paramref name="cycleIndex"/> (this also covers the parked
        ///     cadence &gt; span tail, where <c>loopUT == spanEnd &gt;= dock</c>,
        ///     and the cadence == span back-to-back boundary frame at
        ///     <c>spanEnd</c>);</item>
        ///   <item>otherwise the clock is still BEFORE this cycle's dock (early in
        ///     the cycle, ghost just launching) -&gt; the most recently DOCKED cycle
        ///     is the PRIOR one, <paramref name="cycleIndex"/> - 1.</item>
        /// </list>
        /// The span-membership of the dock is enforced separately (see
        /// <see cref="IsDockUTInSpan"/> / <see cref="IsDockCrossing"/>); a dock UT
        /// outside the span never produces a fire. Pure: no logging.
        /// </summary>
        internal static long ComputeDockCycleIndex(double loopUT, long cycleIndex, double recordedDockUT)
        {
            return loopUT >= recordedDockUT ? cycleIndex : cycleIndex - 1;
        }

        /// <summary>
        /// Pure dock-phase crossing predicate (plan Phase 4 task 2, retuned for
        /// DEL-2). A crossing is confirmed when BOTH:
        /// <list type="bullet">
        ///   <item><see cref="IsDockUTInSpan"/> for
        ///     <paramref name="recordedDockUT"/> (the delivery binding falls inside
        ///     the rendered span; a dock outside the span never fires);</item>
        ///   <item>the dock-PHASE cycle index
        ///     (<see cref="ComputeDockCycleIndex"/>) is strictly greater than
        ///     <paramref name="lastObservedLoopCycleIndex"/> -- i.e. a cycle whose
        ///     dock instant has now been reached/passed has NOT yet been
        ///     delivered.</item>
        /// </list>
        /// Unlike the pre-DEL-2 predicate this fires at the DOCK phase, not at
        /// cycle start: a fresh <paramref name="cycleIndex"/> alone does not fire
        /// while <paramref name="loopUT"/> is still before
        /// <paramref name="recordedDockUT"/> (ghost just launching). The
        /// cadence &gt; span parked tail no longer needs a separate gate: in the
        /// tail <c>loopUT == spanEnd &gt;= dock</c> so the tail's own cycle is the
        /// dock cycle, and the <paramref name="lastObservedLoopCycleIndex"/> snap
        /// is the sole re-fire guard. A warp tick that jumps several cycles in one
        /// frame still yields exactly one crossing for the highest cycle whose dock
        /// instant has passed (the caller snaps forward to
        /// <paramref name="dockCycleIndex"/> and fires once). Pure: no logging (the
        /// caller owns the per-tick rate-limited summary).
        /// </summary>
        /// <param name="dockCycleIndex">The cycle whose dock has most recently been
        /// reached/passed (<see cref="ComputeDockCycleIndex"/>). The caller snaps
        /// <c>LastObservedLoopCycleIndex</c> to THIS on a fire, NOT to the raw
        /// span-clock <paramref name="cycleIndex"/>, so a tick that lands early in a
        /// new cycle (before its dock) does not prematurely consume that cycle.</param>
        internal static bool IsDockCrossing(
            GhostPlaybackLogic.LoopUnit unit,
            double loopUT,
            long cycleIndex,
            double recordedDockUT,
            long lastObservedLoopCycleIndex,
            out long dockCycleIndex)
        {
            // Thin wrapper over the M-MIS-11 item 2 emitter so the pre-existing
            // predicate shape (and its tests) stay byte-identical.
            bool owedNow = TryGetOwedDockCrossing(
                unit, loopUT, cycleIndex, recordedDockUT, lastObservedLoopCycleIndex,
                out OwedDockCrossing owed);
            dockCycleIndex = owed.DockCycleIndex;
            return owedNow;
        }

        /// <summary>
        /// (M-MIS-11 item 2) THE fire-once-snap-forward dock-crossing emitter.
        /// Given the loop state (<paramref name="loopUT"/> /
        /// <paramref name="cycleIndex"/> from <see cref="TryGetRouteLoopState"/>),
        /// the recorded dock UT, and the consumer's last-observed/fired cycle
        /// index, yields the owed crossing: <c>true</c> plus the newest owed dock
        /// cycle (<see cref="OwedDockCrossing.DockCycleIndex"/>, the snap-forward
        /// target) when a cycle whose dock instant has been reached/passed has
        /// NOT yet been consumed; <c>false</c> otherwise (with
        /// <see cref="OwedDockCrossing.DockCycleIndex"/> still populated for
        /// diagnostics). Same predicate as the pre-extraction
        /// <see cref="IsDockCrossing"/>:
        /// <list type="bullet">
        ///   <item><see cref="IsDockUTInSpan"/> - a dock outside the span never
        ///     fires;</item>
        ///   <item><see cref="ComputeDockCycleIndex"/> strictly greater than
        ///     <paramref name="lastObservedCycleIndex"/> - the sole re-fire
        ///     guard (covers the cadence &gt; span parked tail, where
        ///     <c>loopUT == spanEnd &gt;= dock</c>).</item>
        /// </list>
        /// A warp tick that jumps N&gt;1 cycles yields ONE owed crossing for the
        /// highest passed dock cycle; the single-stop consumer fires once and
        /// snaps forward to it, while the multi-stop consumer feeds each stop's
        /// owed cycle into its lowest-owed-cycle (cMin) catch-up selection. The
        /// result also carries the cycle index's semantic
        /// <see cref="CycleIndexKind"/> (item 3). Pure: no logging (callers own
        /// their per-tick rate-limited summaries).
        /// </summary>
        internal static bool TryGetOwedDockCrossing(
            GhostPlaybackLogic.LoopUnit unit,
            double loopUT,
            long cycleIndex,
            double recordedDockUT,
            long lastObservedCycleIndex,
            out OwedDockCrossing owed)
        {
            long dockCycleIndex = ComputeDockCycleIndex(loopUT, cycleIndex, recordedDockUT);
            owed = new OwedDockCrossing(dockCycleIndex, DeriveCycleIndexKind(unit));
            if (!IsDockUTInSpan(unit, recordedDockUT))
                return false;
            return dockCycleIndex > lastObservedCycleIndex;
        }

        /// <summary>
        /// Pure read-only countdown to the NEXT dock crossing (H1). Computes the
        /// absolute UT at which the loop clock will next reach the recorded dock
        /// PHASE and returns <c>nextDockUT - <paramref name="nowUT"/></c> in
        /// <paramref name="seconds"/>. Read-only: touches NO clock or route state,
        /// the loop clock is never advanced.
        ///
        /// <para><b>Same clock as <see cref="TryGetRouteLoopState"/> /
        /// <see cref="IsDockCrossing"/>.</b> For the uniform v0 path (schedule and
        /// loiter cuts both null) the inner clock plays
        /// <c>loopUT = SpanStartUT + (elapsed - k*cadence)</c> in cycle <c>k</c>
        /// (<c>elapsed = nowUT - PhaseAnchorUT</c>), with the cadence clamped to
        /// <see cref="LoopTiming.MinCycleDuration"/> EXACTLY as
        /// <see cref="GhostPlaybackLogic.TryComputeSpanLoopUT"/> clamps it. The dock
        /// PHASE of cycle <c>k</c> is reached when <c>loopUT &gt;= recordedDockUT</c>,
        /// i.e. at the absolute instant
        /// <c>dockUT_k = PhaseAnchorUT + k*cadence + (recordedDockUT - SpanStartUT)</c>.
        /// This routine finds the smallest cycle index <c>k</c> whose
        /// <c>dockUT_k</c> is strictly greater than <paramref name="nowUT"/> AND not
        /// already consumed (<c>k &gt; <paramref name="lastObservedLoopCycleIndex"/></c>,
        /// matching <see cref="IsDockCrossing"/>'s re-fire guard), then returns
        /// <c>dockUT_k - nowUT</c>.</para>
        ///
        /// <para><b>Span gate.</b> Returns <c>false</c> (<paramref name="seconds"/> = 0)
        /// when <paramref name="recordedDockUT"/> is not in the unit's span
        /// (<see cref="IsDockUTInSpan"/>): a dock outside the span never fires, so
        /// there is no crossing to count down to.</para>
        ///
        /// <para><b>Non-v0 paths.</b> A unit carrying a relaunch schedule
        /// (<see cref="GhostPlaybackLogic.LoopUnit.RelaunchSchedule"/>) or loiter cuts
        /// (<see cref="GhostPlaybackLogic.LoopUnit.LoiterCuts"/>) is a future
        /// inter-body / re-aim loop whose dock UTs are non-uniform; v0 same-body
        /// routes carry null for both (see the class remarks). Returns <c>false</c>
        /// for those rather than approximating with the uniform formula.</para>
        ///
        /// <para>Pure: no logging (the orchestrator-side accessor owns the
        /// rate-limited per-route summary, mirroring
        /// <see cref="IsDockCrossing"/> / <see cref="ComputeDockCycleIndex"/>).</para>
        /// </summary>
        /// <param name="unit">The route's backing-mission loop unit (read-only).</param>
        /// <param name="nowUT">Game UT to count down from.</param>
        /// <param name="recordedDockUT">The recorded dock UT the delivery binds to.</param>
        /// <param name="lastObservedLoopCycleIndex">Highest dock cycle already
        /// delivered; the next crossing is strictly after this cycle.</param>
        /// <param name="seconds">On success, the wall-clock-UT seconds until the next
        /// dock crossing (always &gt; 0); 0 on a false return.</param>
        /// <returns>True when a finite next-dock-crossing countdown exists.</returns>
        internal static bool TryComputeSecondsToNextDockCrossing(
            GhostPlaybackLogic.LoopUnit unit,
            double nowUT,
            double recordedDockUT,
            long lastObservedLoopCycleIndex,
            out double seconds)
        {
            seconds = 0.0;

            // Non-v0 paths (inter-body relaunch schedule / re-aim loiter cuts) have
            // non-uniform dock UTs; do not approximate with the uniform formula. v0
            // same-body routes carry null for both, so this never trips for them.
            // (M-MIS-11 item 3) The schedule refusal is expressed through the typed
            // kind: a Scheduled index has non-uniform launch UTs by definition.
            // Kind == Flat alone is NOT sufficient (a re-aim unit is Flat but its
            // loiter cuts make the within-cycle dock phase non-identity), hence the
            // separate LoiterCuts gate below.
            if (DeriveCycleIndexKind(unit) == CycleIndexKind.Scheduled)
                return false;
            if (unit.LoiterCuts != null && unit.LoiterCuts.Count > 0)
                return false;

            // A dock outside the span never produces a crossing (same gate the
            // crossing detector applies).
            if (!IsDockUTInSpan(unit, recordedDockUT))
                return false;

            double span = unit.SpanEndUT - unit.SpanStartUT;
            if (span <= 0.0)
                return false;

            // SAME cadence clamp as TryComputeSpanLoopUT (edge 14): the span clock
            // clamps the cadence to MinCycleDuration internally, so the dock-UT
            // spacing must use the identical clamped value or the countdown drifts.
            double cadence = Math.Max(unit.CadenceSeconds, LoopTiming.MinCycleDuration);
            if (cadence <= 0.0)
                return false;

            // dockUT_k = PhaseAnchorUT + k*cadence + (recordedDockUT - SpanStartUT).
            // The phase offset is the in-span distance from span start to the dock.
            double dockPhaseOffset = recordedDockUT - unit.SpanStartUT;
            double cycleZeroDockUT = unit.PhaseAnchorUT + dockPhaseOffset;

            // Smallest cycle index k whose dock instant is strictly AFTER nowUT.
            double rawCyclesElapsed = (nowUT - cycleZeroDockUT) / cadence;
            long k = (long)Math.Floor(rawCyclesElapsed) + 1;

            // Never count down to a cycle whose dock has already been delivered
            // (matches IsDockCrossing's dockCycleIndex > lastObservedLoopCycleIndex
            // guard). lastObservedLoopCycleIndex defaults to -1 (nothing delivered),
            // so the floor is cycle 0; clamp the floor itself non-negative to keep k
            // >= 0 even if a pathological lastObserved is below -1.
            long minCycle = lastObservedLoopCycleIndex + 1;
            if (minCycle < 0)
                minCycle = 0;
            if (k < minCycle)
                k = minCycle;

            double nextDockUT = cycleZeroDockUT + k * cadence;

            // Guard the strict-after invariant against floating-point edge cases at
            // an exactly-on-the-dock boundary: if rounding left us at or before
            // nowUT, advance one cycle so the countdown is always strictly positive.
            if (nextDockUT <= nowUT)
                nextDockUT += cadence;

            seconds = nextDockUT - nowUT;
            return true;
        }

        // ==================================================================
        // M5 - inter-body window basis + residual modulo (pure predicates)
        // ==================================================================

        /// <summary>
        /// (M5 D1) Derives the route-side window basis from the resolved unit.
        /// Fail-closed pairs: a re-aim unit classifies
        /// <see cref="RouteWindowBasis.ReaimWindows"/> ONLY on the full
        /// BUILD-level verdict (plan present AND supported, schedule present AND
        /// valid - the same conjunction as <c>LoopUnit.IsReaim</c>); any partial
        /// shape (invalid schedule, unsupported plan, either half missing) falls
        /// back to <see cref="RouteWindowBasis.FlatInterval"/>, today's honest
        /// faithful path (D7). Resolver PER-WINDOW declines never null the unit's
        /// schedule/plan, so they cannot flip the basis (D6, build-level-only
        /// pin). Pure: no logging.
        /// </summary>
        internal static RouteWindowBasis DeriveWindowBasis(GhostPlaybackLogic.LoopUnit unit)
        {
            if (unit.RelaunchSchedule != null)
                return RouteWindowBasis.ZeroDriftSchedule;
            if (unit.ReaimPlan.HasValue && unit.ReaimPlan.Value.Supported
                && unit.ReaimSchedule.HasValue && unit.ReaimSchedule.Value.Valid)
                return RouteWindowBasis.ReaimWindows;
            return RouteWindowBasis.FlatInterval;
        }

        /// <summary>
        /// (M5 D2) The ONE route-side cadence rule: the residual part of
        /// <paramref name="cadenceMultiplier"/> NOT already consumed by the
        /// Missions-side build. 0 = modulo OFF (<see cref="RouteWindowBasis.FlatInterval"/>:
        /// N is fully consumed by the flat cadence <c>DispatchInterval = N x span</c>);
        /// 1 = every rendered window delivers
        /// (<see cref="RouteWindowBasis.ZeroDriftSchedule"/>: N is fully consumed
        /// Missions-side by the schedule's minSpacing throttle - a route-side
        /// modulo on <c>sIdx</c> would double-apply N); N =
        /// <see cref="RouteWindowBasis.ReaimWindows"/> (the build discarded
        /// <c>DispatchInterval</c>; the ghost renders EVERY synodic window and
        /// delivery fires on every Nth of those same rendered UTs). The
        /// firing-path modulo gate is live only when the result is &gt; 1, which
        /// is only possible under ReaimWindows - the behavior-identical-off
        /// guarantee for flat and zero-drift routes. Pure: no logging.
        /// </summary>
        internal static int ResolveResidualCadence(RouteWindowBasis basis, int cadenceMultiplier)
        {
            switch (basis)
            {
                case RouteWindowBasis.ReaimWindows:
                    return Route.ClampCadenceMultiplier(cadenceMultiplier);
                case RouteWindowBasis.ZeroDriftSchedule:
                    return 1;
                default:
                    return 0; // FlatInterval: modulo OFF
            }
        }

        /// <summary>
        /// (M5 D2/D3) True when window <paramref name="windowIndex"/> is
        /// deliverable under the residual modulo:
        /// <c>(windowIndex - anchor) % nResidual == 0</c> (Euclidean modulo, so
        /// negative indices behave). A residual of 0 (off) or 1 makes every
        /// window deliverable. An UNSET anchor (&lt; 0) also returns true: the
        /// first owed crossing after creation / activation / rebase ALWAYS
        /// delivers - the caller adopts <c>anchor = dockCycleIndex</c> on that
        /// crossing (D3 anchor adoption). Pure: no logging.
        /// </summary>
        internal static bool IsDeliverableWindow(long windowIndex, long anchor, int nResidual)
        {
            if (nResidual <= 1)
                return true;
            if (anchor < 0)
                return true; // anchor adoption: first crossing always delivers
            long m = (windowIndex - anchor) % nResidual;
            if (m < 0)
                m += nResidual;
            return m == 0;
        }

        /// <summary>
        /// Sentinel returned by <see cref="ComputeHighestDeliverableWindow"/>
        /// when no deliverable window lies in the owed range.
        /// </summary>
        internal const long NoDeliverableWindow = long.MinValue;

        /// <summary>
        /// (M5 D5) The warp rule under the residual modulo: the HIGHEST
        /// deliverable window index in <c>(lastObserved, dockCycleIndex]</c>, or
        /// <see cref="NoDeliverableWindow"/> when none is. The caller fires ONCE
        /// for the returned window and snaps its marker to
        /// <paramref name="dockCycleIndex"/> either way (never replaying skipped
        /// cycles), so a warp tick that jumps K&gt;1 windows keeps the existing
        /// fire-once-snap-forward semantics and the modulo can never pick a
        /// non-deliverable window. An UNSET anchor (&lt; 0) makes the newest
        /// window (<paramref name="dockCycleIndex"/>) deliverable (D3 anchor
        /// adoption at the caller). Pure: no logging.
        /// </summary>
        internal static long ComputeHighestDeliverableWindow(
            long lastObserved, long dockCycleIndex, long anchor, int nResidual)
        {
            if (dockCycleIndex <= lastObserved)
                return NoDeliverableWindow; // nothing owed at all
            if (nResidual <= 1 || anchor < 0)
                return dockCycleIndex; // modulo off / every window / adoption
            // Highest w <= dockCycleIndex with w ≡ anchor (mod nResidual).
            long m = (dockCycleIndex - anchor) % nResidual;
            if (m < 0)
                m += nResidual;
            long w = dockCycleIndex - m;
            return w > lastObserved ? w : NoDeliverableWindow;
        }

        /// <summary>
        /// (M5 D6) Pure basis-flip transition decision: compares the persisted
        /// flip-detector marker (<c>Route.ReaimWindowBasisEngaged</c>) against
        /// the tick's derived <paramref name="basis"/>.
        /// <see cref="WindowBasisTransitionKind.Engage"/> when the marker is
        /// false and the basis is <see cref="RouteWindowBasis.ReaimWindows"/>;
        /// <see cref="WindowBasisTransitionKind.Decline"/> when the marker is
        /// true and the basis is anything else;
        /// <see cref="WindowBasisTransitionKind.None"/> otherwise. Flat &lt;-&gt;
        /// zero-drift flips are deliberately invisible here (they exist pre-M5
        /// and share one index arithmetic through the span clock; the marker
        /// does not track them). Idempotent by construction: a second Engage /
        /// Decline evaluation in the settled state returns None. Pure: no
        /// logging (the orchestrator applier owns the transition Info lines).
        /// </summary>
        internal static WindowBasisTransitionKind EvaluateWindowBasisTransition(
            bool reaimWindowBasisEngaged, RouteWindowBasis basis)
        {
            if (!reaimWindowBasisEngaged && basis == RouteWindowBasis.ReaimWindows)
                return WindowBasisTransitionKind.Engage;
            if (reaimWindowBasisEngaged && basis != RouteWindowBasis.ReaimWindows)
                return WindowBasisTransitionKind.Decline;
            return WindowBasisTransitionKind.None;
        }

        /// <summary>
        /// Formats a one-line diagnostic of the loop-clock state for the
        /// orchestrator's per-tick log. Centralized here so the
        /// <c>ToString("R", InvariantCulture)</c> formatting stays consistent.
        /// </summary>
        internal static string DescribeState(
            GhostPlaybackLogic.LoopUnit unit,
            double currentUT,
            double loopUT,
            long cycleIndex,
            bool isInInterCycleTail,
            double recordedDockUT,
            long lastObservedLoopCycleIndex)
        {
            return $"ut={currentUT.ToString("R", IC)} " +
                   $"spanStart={unit.SpanStartUT.ToString("R", IC)} " +
                   $"spanEnd={unit.SpanEndUT.ToString("R", IC)} " +
                   $"cadence={unit.CadenceSeconds.ToString("R", IC)} " +
                   $"anchor={unit.PhaseAnchorUT.ToString("R", IC)} " +
                   $"loopUT={loopUT.ToString("R", IC)} " +
                   $"cycleIdx={cycleIndex.ToString(IC)} " +
                   $"tail={(isInInterCycleTail ? "1" : "0")} " +
                   $"dockUT={recordedDockUT.ToString("R", IC)} " +
                   $"lastObserved={lastObservedLoopCycleIndex.ToString(IC)}";
        }
    }
}
