using System.Collections.Generic;

namespace Parsek.Logistics
{
    /// <summary>
    /// Pure, Unity-free predicates and list filters over route events (the Rec-1 retire and
    /// the Rec-3 discard selection; plan <c>docs/dev/plans/fix-logistics-rewind-determinism.md</c>).
    ///
    /// <para><b>The real-time rule.</b> Every load back in time (a go-back rewind, a Re-Fly
    /// start, an in-session load such as an F9, a stock revert or the Discard Re-fly load)
    /// retires the route events stamped strictly after its cutoff: the loaded world reverted
    /// to the cutoff, so the live clock decides those cycles again and re-delivers the cargo
    /// once, with the dispatch / pickup / delivery dedup seeing an empty slate for them
    /// (design section 10.6).</para>
    ///
    /// <para>Takes the events BY PARAMETER and reads no static store, so it is unit-testable
    /// in isolation.</para>
    /// </summary>
    internal static class RouteEventRetire
    {
        /// <summary>
        /// True when an event should be RETIRED at a load back in time whose cutoff is
        /// <paramref name="cutoffUT"/> (the UT the world reverted to: the loaded save's UT,
        /// raised to the route state floor it carries).
        ///
        /// <para><b>Strict <c>&gt;</c> (not <c>&gt;=</c>), justified by emit ordering.</b> An
        /// event and its physical write are emitted in the SAME synchronous
        /// <c>EmitLoopCycle</c> tick from the same <c>currentUT</c> (the event is written only
        /// after the live writer runs), so an event stamped exactly at the cutoff means its
        /// physical effect already landed and IS captured in the reverted world: keep it. The
        /// cutoff MUST be the UT the world actually reverted to (the Re-Fly caller passes the
        /// post-load live UT, never <c>RewindPoint.UT</c>, which is one frame earlier). Do NOT
        /// "fix" this to <c>&gt;=</c>.</para>
        /// </summary>
        internal static bool ShouldRetireAtRewind(RouteEvent e, double cutoffUT)
        {
            return e != null && !string.IsNullOrEmpty(e.RouteId) && e.UT > cutoffUT;
        }

        /// <summary>
        /// Returns a NEW list holding every event of <paramref name="source"/> except those
        /// <see cref="ShouldRetireAtRewind"/> selects, in order; <paramref name="retired"/>
        /// receives the drop count. A <paramref name="cutoffUT"/> of
        /// <c>double.PositiveInfinity</c> retires nothing (the route-blind rollback contract).
        /// </summary>
        internal static List<RouteEvent> RetireFutureEvents(
            IReadOnlyList<RouteEvent> source, double cutoffUT, out int retired)
        {
            return RetireFutureEvents(source, cutoffUT, out retired, out _);
        }

        /// <summary>
        /// <see cref="RetireFutureEvents(IReadOnlyList{RouteEvent}, double, out int)"/> that
        /// also hands back the retired events, in source order, for the chain-tip cargo
        /// capture (<c>ChainTipRouteCargo.CaptureRetiredRouteCargo</c>).
        /// </summary>
        internal static List<RouteEvent> RetireFutureEvents(
            IReadOnlyList<RouteEvent> source, double cutoffUT, out int retired,
            out List<RouteEvent> retiredEvents)
        {
            retired = 0;
            retiredEvents = new List<RouteEvent>();
            if (source == null)
                return new List<RouteEvent>();

            var kept = new List<RouteEvent>(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                RouteEvent e = source[i];
                if (e == null)
                    continue;
                if (ShouldRetireAtRewind(e, cutoffUT))
                {
                    retired++;
                    retiredEvents.Add(e);
                    continue;
                }
                kept.Add(e);
            }
            return kept;
        }

        /// <summary>
        /// True when a route event carries an ACTUAL physical world mutation: a non-empty
        /// resource or inventory manifest (the events the three no-reverse writers
        /// <c>LiveDeliveryWriters</c>, <c>LiveOriginDebitWriters</c> and
        /// <c>LiveInventoryPickupWriter</c> produce). Dispatched, paused, resumed, held and
        /// endpoint-lost events mutate nothing.
        /// </summary>
        internal static bool IsPhysicalRouteMutation(RouteEvent e)
        {
            return e != null && !string.IsNullOrEmpty(e.RouteId)
                   && ((e.RouteResourceManifest != null && e.RouteResourceManifest.Count > 0)
                       || (e.RouteInventoryManifest != null && e.RouteInventoryManifest.Count > 0));
        }

        /// <summary>
        /// True when a route event's UT falls inside the INCLUSIVE window
        /// <c>[<paramref name="minUT"/>, <paramref name="maxUT"/>]</c>: an event stamped
        /// exactly at a discarded recording's start or end fired during that segment.
        /// </summary>
        internal static bool IsInWindow(RouteEvent e, double minUT, double maxUT)
        {
            return e != null && !string.IsNullOrEmpty(e.RouteId) && e.UT >= minUT && e.UT <= maxUT;
        }

        /// <summary>
        /// Returns a NEW list of the events of <paramref name="source"/> inside the inclusive
        /// window, in order. A degenerate window (<paramref name="minUT"/> &gt;
        /// <paramref name="maxUT"/>) selects nothing.
        /// </summary>
        internal static List<RouteEvent> SelectInWindow(
            IReadOnlyList<RouteEvent> source, double minUT, double maxUT)
        {
            var selected = new List<RouteEvent>();
            if (source == null || minUT > maxUT)
                return selected;

            for (int i = 0; i < source.Count; i++)
            {
                if (IsInWindow(source[i], minUT, maxUT))
                    selected.Add(source[i]);
            }
            return selected;
        }
    }
}
