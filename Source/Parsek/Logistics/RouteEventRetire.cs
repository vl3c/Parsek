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
        /// True when a loaded save was written inside a go-back rewind's lead-time window: its
        /// own clock (<paramref name="saveUT"/>, the load's cutoff before the floor raise) lies
        /// behind the route state floor it carries (<paramref name="floorUT"/>). The rows in
        /// (save clock, floor] are then either the go-back's settled rows, which the save holds,
        /// or rows written after the save, which it does not (todo
        /// ROUTE-WINDOW-SAVE-LOAD-KEEPS-WINDOW-ACTIONS).
        /// </summary>
        internal static bool IsInSaveWindow(double saveUT, double floorUT)
        {
            return RouteLoadReconcile.IsUsableCutoffUT(saveUT)
                && RouteLoadReconcile.IsUsableCutoffUT(floorUT)
                && floorUT > saveUT;
        }

        /// <summary>
        /// The identity of one route event across a save round trip: type, UT (round-trip
        /// text), sequence, route, cycle and stop. Every field is written by
        /// <see cref="RouteEvent.SerializeInto"/> and read back exactly, so an event the save
        /// holds has the same key in memory and in the save.
        /// </summary>
        internal static string WindowRowKey(RouteEvent e)
        {
            if (e == null)
                return string.Empty;
            return ((int)e.Type).ToString(IC)
                + "|" + e.UT.ToString("R", IC)
                + "|" + e.Sequence.ToString(IC)
                + "|" + (e.RouteId ?? string.Empty)
                + "|" + (e.RouteCycleId ?? string.Empty)
                + "|" + e.RouteStopIndex.ToString(IC);
        }

        /// <summary>
        /// The live events a load of a save written inside a go-back's window must retire below
        /// the floor: every event of <paramref name="live"/> with UT in
        /// (<paramref name="saveUT"/>, <paramref name="floorUT"/>] that
        /// <paramref name="savedEvents"/> (the loaded save's own <c>ROUTE_EVENT_LOG</c>) does not
        /// hold, matched one to one by <see cref="WindowRowKey"/>. Those were written after the
        /// save (a pause or resume, a held run, the run of a route created or resumed after it).
        /// Empty when the save is not inside a window (<see cref="IsInSaveWindow"/>). Events at
        /// or below the save clock and above the floor are never selected: the ordinary retire
        /// owns the ones above the floor. Pure; returns the selected instances in log order.
        /// </summary>
        internal static List<RouteEvent> SelectUnheldWindowRows(
            IReadOnlyList<RouteEvent> live, double saveUT, double floorUT,
            IReadOnlyList<RouteEvent> savedEvents)
        {
            var selected = new List<RouteEvent>();
            if (live == null || !IsInSaveWindow(saveUT, floorUT))
                return selected;

            var held = new Dictionary<string, int>(System.StringComparer.Ordinal);
            if (savedEvents != null)
            {
                for (int i = 0; i < savedEvents.Count; i++)
                {
                    RouteEvent s = savedEvents[i];
                    if (s == null || !(s.UT > saveUT) || s.UT > floorUT)
                        continue;
                    string key = WindowRowKey(s);
                    held.TryGetValue(key, out int n);
                    held[key] = n + 1;
                }
            }

            for (int i = 0; i < live.Count; i++)
            {
                RouteEvent e = live[i];
                if (e == null || string.IsNullOrEmpty(e.RouteId) || !(e.UT > saveUT) || e.UT > floorUT)
                    continue;
                string key = WindowRowKey(e);
                if (held.TryGetValue(key, out int n) && n > 0)
                {
                    held[key] = n - 1;
                    continue;
                }
                selected.Add(e);
            }
            return selected;
        }

        private static readonly System.Globalization.CultureInfo IC = System.Globalization.CultureInfo.InvariantCulture;

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
