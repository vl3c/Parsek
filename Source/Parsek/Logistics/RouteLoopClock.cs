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
    /// The clock passes the backing-mission <see cref="GhostPlaybackLogic.LoopUnit"/>'s
    /// OWN relaunch schedule (<see cref="GhostPlaybackLogic.LoopUnit.RelaunchSchedule"/>)
    /// and loiter cuts (<see cref="GhostPlaybackLogic.LoopUnit.LoiterCuts"/>) straight
    /// through into <see cref="GhostPlaybackLogic.TryComputeSpanLoopUT"/> (Phase 6
    /// hardening). For a v0 SAME-BODY route both fields are <c>null</c> (the backing
    /// Mission is built faithful, <c>bodyInfo=null</c>, no re-aim), so this is a
    /// NO-OP: the span is UNCOMPRESSED, the recorded dock UT maps directly into
    /// <c>[spanStart, spanEnd]</c>, and the loopUT it reports is a genuine recorded
    /// UT. The passthrough is the inter-body SEAM: when an inter-body route is later
    /// enabled, the backing Mission's loop unit carries a synodic / re-aimed
    /// <see cref="MissionRelaunchSchedule"/> (built by the locked Missions layer), and
    /// delivery then fires on the SAME re-aimed launch UTs the ghost renders on,
    /// inheriting the synodic schedule for free. v0 does NOT enable inter-body
    /// routes; it only stops hardcoding <c>null</c> so the seam is in place.
    /// Both fields are consumed READ-ONLY (no Missions/engine file is edited).
    /// Phase 5's <c>RouteBuilder</c> clamps
    /// <c>DispatchInterval &gt;= backingMissionSpan</c> so the unit's
    /// <see cref="GhostPlaybackLogic.LoopUnit.CadenceSeconds"/> equals the dispatch
    /// interval, which makes ONE span-clock crossing equal ONE dispatch cycle.
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
        /// Pure crossing-detection predicate (plan Phase 4 task 2). A crossing is
        /// confirmed when ALL of:
        /// <list type="bullet">
        ///   <item><paramref name="cycleIndex"/> &gt;
        ///     <paramref name="lastObservedLoopCycleIndex"/> (a new span-clock
        ///     cycle has advanced past the last one the route delivered);</item>
        ///   <item><c>!</c><paramref name="isInInterCycleTail"/> (the clock is
        ///     actively playing, not parked at <c>spanEnd</c> in the
        ///     cadence &gt; span idle tail);</item>
        ///   <item><see cref="IsDockUTInSpan"/> for
        ///     <paramref name="recordedDockUT"/> (the delivery binding falls inside
        ///     the rendered span).</item>
        /// </list>
        /// Pure: no logging (the caller owns the per-tick rate-limited summary).
        /// </summary>
        internal static bool IsCrossing(
            GhostPlaybackLogic.LoopUnit unit,
            long cycleIndex,
            bool isInInterCycleTail,
            double recordedDockUT,
            long lastObservedLoopCycleIndex)
        {
            if (cycleIndex <= lastObservedLoopCycleIndex)
                return false;
            if (isInInterCycleTail)
                return false;
            return IsDockUTInSpan(unit, recordedDockUT);
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
