using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// The route-owned event log (todo LOGISTICS-WITHOUT-MONEY phase 2): every
    /// <see cref="RouteEvent"/> the supply-route scheduler writes, in write order. Supply
    /// routes are a real-time system that only delivers resources, so their events never
    /// enter the game-actions ledger (owner ruling 2026-10-21); the ledger holds committed
    /// history only.
    ///
    /// <para><b>Persistence.</b> Written by <see cref="RouteStore.SaveRoutesTo"/> as the
    /// sparse <c>ROUTE_EVENT_LOG</c> child of the PARSEK scenario node, beside the routes,
    /// and read back by <see cref="RouteStore.LoadRoutesFrom"/> on a cold load, so a save
    /// restores exactly the log it was written with.</para>
    ///
    /// <para><b>In-session loads.</b> Like the route store, the log stays in memory across
    /// an in-session load; every load back in time retires the events after its cutoff
    /// (<see cref="RetireFutureRouteActionsAtRewind"/>: the go-back rewind, the in-session
    /// load reconcile) or restores the Re-Fly bundle's capture retired at its cutoff
    /// (<see cref="ReplaceAll"/>), and the live clock decides those cycles again.</para>
    ///
    /// <para>Readers take <see cref="Events"/> directly: no event is ever tombstoned or
    /// superseded, so there is no effective view to compute.</para>
    /// </summary>
    internal static class RouteEventLog
    {
        internal const string Tag = "RouteEventLog";
        internal const string NodeName = "ROUTE_EVENT_LOG";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private static readonly List<RouteEvent> events = new List<RouteEvent>();

        /// <summary>Every event, in write order.</summary>
        internal static IReadOnlyList<RouteEvent> Events => events;

        /// <summary>Bumped on every change; caches keyed on the log read it.</summary>
        internal static int StateVersion { get; private set; }

        private static void Bump()
        {
            unchecked { StateVersion++; }
        }

        /// <summary>Appends one event. A null event or one with no route id is refused with a Warn.</summary>
        internal static void Add(RouteEvent e)
        {
            if (e == null || string.IsNullOrEmpty(e.RouteId))
            {
                ParsekLog.Warn(Tag,
                    "Add: refused an event with no route id " +
                    $"(type={(e != null ? e.Type.ToString() : "(null)")})");
                return;
            }
            events.Add(e);
            Bump();
            ParsekLog.Verbose(Tag,
                $"Added event: type={e.Type} route={RouteIds.Short(e.RouteId)} " +
                $"cycle={e.RouteCycleId ?? "(none)"} ut={e.UT.ToString("R", IC)} total={events.Count.ToString(IC)}");
        }

        /// <summary>Appends several events in order (one cycle's dispatch and debit).</summary>
        internal static void AddRange(IEnumerable<RouteEvent> batch)
        {
            if (batch == null)
                return;
            foreach (RouteEvent e in batch)
                Add(e);
        }

        /// <summary>
        /// Replaces the whole log with <paramref name="kept"/> (the Re-Fly bundle's restore).
        /// One Verbose line with the counts.
        /// </summary>
        internal static void ReplaceAll(IReadOnlyList<RouteEvent> kept, string reason)
        {
            int before = events.Count;
            events.Clear();
            if (kept != null)
            {
                for (int i = 0; i < kept.Count; i++)
                {
                    if (kept[i] != null && !string.IsNullOrEmpty(kept[i].RouteId))
                        events.Add(kept[i]);
                }
            }
            Bump();
            ParsekLog.Verbose(Tag,
                $"ReplaceAll ({reason ?? "?"}): events {before.ToString(IC)} -> {events.Count.ToString(IC)}");
        }

        /// <summary>
        /// Go-back rewind and in-session load seam: retires every event with UT strictly
        /// after <paramref name="cutoffUT"/> in place (<see cref="RouteEventRetire.ShouldRetireAtRewind"/>),
        /// hands back an independent copy of the kept events for the caller's route-store
        /// reconcile, and gives the retired events to the chain-tip cargo capture
        /// (<see cref="ChainTipRouteCargo.CaptureRetiredRouteCargo"/>) while the route store
        /// still holds the pre-rewind routes (every caller reconciles it after this). The
        /// capture runs with no events too: the cutoff still lowers every tip snapshot's
        /// watermark. The method keeps the name its log line has always carried.
        /// </summary>
        /// <returns>The number of events retired.</returns>
        internal static int RetireFutureRouteActionsAtRewind(
            double cutoffUT, out List<RouteEvent> keptEvents)
        {
            keptEvents = RouteEventRetire.RetireFutureEvents(
                events, cutoffUT, out int retired, out List<RouteEvent> retiredEvents);
            if (retired > 0)
            {
                events.Clear();
                events.AddRange(keptEvents);
                Bump();
                ParsekLog.Info(Tag,
                    $"RetireFutureRouteActionsAtRewind: removed {retired} free-standing route " +
                    $"action(s) after UT {cutoffUT.ToString("R", IC)}, " +
                    $"total={events.Count}");
            }
            else
            {
                ParsekLog.Verbose(Tag,
                    "RetireFutureRouteActionsAtRewind: no free-standing route actions after UT " +
                    $"{cutoffUT.ToString("R", IC)} (total={events.Count})");
            }
            keptEvents = new List<RouteEvent>(events);
            ChainTipRouteCargo.CaptureRetiredRouteCargo(
                retiredEvents, cutoffUT,
                RouteStore.CommittedRoutes, RouteStore.DormantRoutes,
                "route event retire");
            return retired;
        }

        /// <summary>
        /// The window half of a load of a save written inside a go-back rewind's lead-time
        /// window (todo ROUTE-WINDOW-SAVE-LOAD-KEEPS-WINDOW-ACTIONS): removes the events in
        /// (<paramref name="saveUT"/>, <paramref name="floorUT"/>] that the loaded save's own log
        /// (<paramref name="savedEvents"/>) does not hold
        /// (<see cref="RouteEventRetire.SelectUnheldWindowRows"/>) and hands them back. The
        /// ordinary retire above the floor runs after it.
        ///
        /// <para>The removed events are NOT handed to the chain-tip cargo capture. They were
        /// written after the save, inside its 15 s window, so the only snapshots that can hold
        /// their cargo are ones committed inside that same window, while every older snapshot
        /// captured past their UT (a later recording of the route's endpoint) would match them by
        /// UT and lose cargo it never had. Leaving them out keeps every snapshot as recorded,
        /// which is what the code did before (it kept these events).</para>
        /// One Info line when anything is removed, Verbose otherwise. Returns the count.
        /// </summary>
        internal static int RetireUnheldWindowRows(
            double saveUT, double floorUT, IReadOnlyList<RouteEvent> savedEvents,
            out List<RouteEvent> retiredRows)
        {
            retiredRows = RouteEventRetire.SelectUnheldWindowRows(events, saveUT, floorUT, savedEvents);
            if (retiredRows.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    "RetireUnheldWindowRows: every route event in (" + saveUT.ToString("R", IC) + ", " +
                    floorUT.ToString("R", IC) + "] is held by the loaded save " +
                    $"(saved={(savedEvents?.Count ?? 0).ToString(IC)} total={events.Count.ToString(IC)})");
                return 0;
            }
            var drop = new HashSet<RouteEvent>(retiredRows);
            events.RemoveAll(e => e != null && drop.Contains(e));
            Bump();
            var routes = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < retiredRows.Count; i++)
                routes.Add(retiredRows[i].RouteId);
            ParsekLog.Info(Tag,
                $"RetireUnheldWindowRows: removed {retiredRows.Count.ToString(IC)} route event(s) in (" +
                saveUT.ToString("R", IC) + ", " + floorUT.ToString("R", IC) + "] the loaded save does not hold " +
                $"(written after it) for {routes.Count.ToString(IC)} route(s); total={events.Count.ToString(IC)}");
            return retiredRows.Count;
        }

        /// <summary>
        /// The events a saved PARSEK scenario node's <c>ROUTE_EVENT_LOG</c> child carries, decoded
        /// WITHOUT touching the log (none: empty; unreadable events dropped, as a cold load drops
        /// them). The in-session load reads the loaded save's own log from it.
        /// </summary>
        internal static List<RouteEvent> ReadSavedEvents(ConfigNode parsekNode)
        {
            return ReadEventsFrom(parsekNode?.GetNode(NodeName), out _, out _);
        }

        private static List<RouteEvent> ReadEventsFrom(ConfigNode node, out int dropped, out string firstReject)
        {
            var read = new List<RouteEvent>();
            dropped = 0;
            firstReject = null;
            if (node == null)
                return read;
            ConfigNode[] eventNodes = node.GetNodes(RouteEvent.NodeName);
            for (int i = 0; i < eventNodes.Length; i++)
            {
                RouteEvent e = RouteEvent.DeserializeFrom(eventNodes[i], out string reject);
                if (e == null)
                {
                    dropped++;
                    if (firstReject == null)
                        firstReject = reject;
                    continue;
                }
                read.Add(e);
            }
            return read;
        }

        /// <summary>
        /// True when the log holds an event the rewind retire would remove at
        /// <paramref name="cutoffUT"/> (same predicate and strict boundary). Read-only: the
        /// in-session load reconcile reads it as evidence that a load went back past route activity.
        /// </summary>
        internal static bool HasEventsAfterUT(double cutoffUT)
        {
            for (int i = 0; i < events.Count; i++)
            {
                if (RouteEventRetire.ShouldRetireAtRewind(events[i], cutoffUT))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Removes the events <paramref name="shouldRemove"/> selects (in-game test cleanup of
        /// a synthetic route's events). Returns the number removed; one Verbose line.
        /// </summary>
        internal static int RemoveWhere(Func<RouteEvent, bool> shouldRemove, string reason)
        {
            if (shouldRemove == null)
                return 0;
            int removed = 0;
            for (int i = events.Count - 1; i >= 0; i--)
            {
                if (events[i] != null && shouldRemove(events[i]))
                {
                    events.RemoveAt(i);
                    removed++;
                }
            }
            if (removed > 0)
                Bump();
            ParsekLog.Verbose(Tag,
                $"RemoveWhere ({reason ?? "?"}): removed={removed.ToString(IC)} total={events.Count.ToString(IC)}");
            return removed;
        }

        /// <summary>
        /// Writes the log as the sparse <c>ROUTE_EVENT_LOG</c> child of the PARSEK scenario
        /// node (nothing while empty, so a save with no route activity stays byte-identical).
        /// Strips a stale child first.
        /// </summary>
        internal static void SaveInto(ConfigNode parsekNode)
        {
            if (parsekNode == null)
                return;
            parsekNode.RemoveNodes(NodeName);
            if (events.Count == 0)
            {
                ParsekLog.Verbose(Tag, "Save: no route events to write");
                return;
            }
            ConfigNode node = parsekNode.AddNode(NodeName);
            for (int i = 0; i < events.Count; i++)
                events[i]?.SerializeInto(node);
            ParsekLog.Verbose(Tag,
                $"Save: wrote {events.Count.ToString(IC)} route event(s) for {CountRoutes(events).ToString(IC)} route(s)");
        }

        /// <summary>
        /// Replaces the log with the PARSEK scenario node's <c>ROUTE_EVENT_LOG</c> child (none:
        /// empty), on a cold load. An unreadable event is dropped and counted. Logs one Info
        /// summary and the walk summary (<see cref="LogWalkSummary"/>). Returns the number loaded.
        /// </summary>
        internal static int LoadFrom(ConfigNode parsekNode)
        {
            events.Clear();
            Bump();
            ConfigNode node = parsekNode?.GetNode(NodeName);
            events.AddRange(ReadEventsFrom(node, out int dropped, out string firstReject));
            if (dropped > 0)
            {
                ParsekLog.Warn(Tag,
                    $"Load: dropped {dropped.ToString(IC)} unreadable route event(s) " +
                    $"(first reason={firstReject ?? "?"}); loaded={events.Count.ToString(IC)}");
            }
            ParsekLog.Info(Tag,
                $"Load: route event log loaded events={events.Count.ToString(IC)} " +
                $"routes={CountRoutes(events).ToString(IC)} dropped={dropped.ToString(IC)} " +
                $"node={(node != null ? "present" : "absent")}");
            LogWalkSummary();
            return events.Count;
        }

        /// <summary>
        /// One Verbose summary over the log: routes, how many end paused or on a lost
        /// endpoint, dispatched cycles, delivered stops, physical debits and pickups. Keeps
        /// the <c>PostWalk: routes=</c> wording the ledger's route module printed, which
        /// harness lanes pin as the reading of an empty route log on the way in. Runs on every
        /// load (cold and in-session) and after a go-back rewind.
        /// </summary>
        internal static void LogWalkSummary()
        {
            ComputeWalkSummary(events, out int routes, out int paused, out int endpointLost,
                out int dispatched, out int delivered, out int physicalDebits, out int physicalPickups);
            ParsekLog.Verbose("Route",
                $"PostWalk: routes={routes.ToString(IC)}, " +
                $"paused={paused.ToString(IC)}, endpointLost={endpointLost.ToString(IC)}, " +
                $"totalDispatched={dispatched.ToString(IC)}, " +
                $"totalDelivered={delivered.ToString(IC)}, " +
                $"physicalDebits={physicalDebits.ToString(IC)}, " +
                $"physicalPickups={physicalPickups.ToString(IC)}");
        }

        /// <summary>
        /// Pure: the walk summary's counters over <paramref name="log"/> in order. A route
        /// counts as paused when its last pause or resume event is a pause, and as endpoint
        /// lost once it has a lost-endpoint event. A debit or pickup is physical when it names
        /// the vessel it took cargo from.
        /// </summary>
        internal static void ComputeWalkSummary(
            IReadOnlyList<RouteEvent> log,
            out int routes, out int paused, out int endpointLost,
            out int dispatched, out int delivered, out int physicalDebits, out int physicalPickups)
        {
            routes = paused = endpointLost = dispatched = delivered = physicalDebits = physicalPickups = 0;
            if (log == null)
                return;
            var pausedByRoute = new Dictionary<string, bool>(StringComparer.Ordinal);
            var lostRoutes = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < log.Count; i++)
            {
                RouteEvent e = log[i];
                if (e == null || string.IsNullOrEmpty(e.RouteId))
                    continue;
                if (!pausedByRoute.ContainsKey(e.RouteId))
                    pausedByRoute[e.RouteId] = false;
                switch (e.Type)
                {
                    case RouteEventType.RouteDispatched: dispatched++; break;
                    case RouteEventType.RouteCargoDelivered: delivered++; break;
                    case RouteEventType.RouteCargoDebited:
                        if (e.RouteOriginVesselPid != 0u) physicalDebits++;
                        break;
                    case RouteEventType.RouteCargoPickedUp:
                        if (e.RouteOriginVesselPid != 0u) physicalPickups++;
                        break;
                    case RouteEventType.RoutePaused: pausedByRoute[e.RouteId] = true; break;
                    case RouteEventType.RouteResumed: pausedByRoute[e.RouteId] = false; break;
                    case RouteEventType.RouteEndpointLost: lostRoutes.Add(e.RouteId); break;
                }
            }
            routes = pausedByRoute.Count;
            foreach (var kv in pausedByRoute)
            {
                if (kv.Value)
                    paused++;
            }
            endpointLost = lostRoutes.Count;
        }

        private static int CountRoutes(IReadOnlyList<RouteEvent> log)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < log.Count; i++)
            {
                if (log[i] != null && !string.IsNullOrEmpty(log[i].RouteId))
                    ids.Add(log[i].RouteId);
            }
            return ids.Count;
        }

        /// <summary>Test seam: empties the log.</summary>
        internal static void ResetForTesting()
        {
            events.Clear();
            Bump();
        }
    }
}
