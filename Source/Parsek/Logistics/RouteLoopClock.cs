using System;
using System.Globalization;

namespace Parsek.Logistics
{
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
    ///   <item><b>Inter-body</b> (future): the unit carries a synodic / re-aimed
    ///     <see cref="MissionRelaunchSchedule"/> built by the locked Missions layer, and
    ///     delivery inherits it for free through the passthrough. v0 does NOT enable
    ///     inter-body routes; the passthrough is only the seam.</item>
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
            dockCycleIndex = ComputeDockCycleIndex(loopUT, cycleIndex, recordedDockUT);
            if (!IsDockUTInSpan(unit, recordedDockUT))
                return false;
            return dockCycleIndex > lastObservedLoopCycleIndex;
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
            if (unit.RelaunchSchedule != null)
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
