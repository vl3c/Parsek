using System;
using System.Collections.Generic;

namespace Parsek.Logistics
{
    /// <summary>
    /// Static in-memory store for committed supply routes (design §4.7).
    /// Survives scene changes within a KSP session. Save/load is handled by
    /// <see cref="ParsekScenario"/> driving <see cref="SaveRoutesTo"/> /
    /// <see cref="LoadRoutesFrom"/>.
    ///
    /// Phase 3 owns CRUD + codec drivers; Phase 5 adds
    /// <see cref="RevalidateSources(string)"/> which routes through
    /// <see cref="EffectiveState.ComputeERS"/>. This file must not read the
    /// raw committed-recording list or raw ledger actions directly; route
    /// everything through EffectiveState (CI gated by
    /// <c>scripts/grep-audit-ers-els.ps1</c>).
    /// </summary>
    internal static class RouteStore
    {
        private const string Tag = "Route";
        private const string RoutesParentNodeName = "ROUTES";
        private const string RouteChildNodeName = "ROUTE";

        private static readonly List<Route> committedRoutes = new List<Route>();

        /// <summary>Read-only view of currently committed routes.</summary>
        internal static IReadOnlyList<Route> CommittedRoutes => committedRoutes;

        // -----------------------------------------------------------------
        // M4b Phase B2 (plan D11 / OQ7): RAM-only cargo escrow.
        // -----------------------------------------------------------------

        /// <summary>
        /// M4b Phase B2 (plan D11 / OQ7): the RAM-ONLY cargo escrow map.
        /// Shape: <c>routeId -&gt; vesselPid -&gt; resourceName -&gt; reserved amount</c>.
        /// A per-resource sub-map per <c>(routeId, pid)</c> so a depot can reserve
        /// LiquidFuel and Oxidizer against the same source vessel independently.
        ///
        /// <para><b>NOT serialized.</b> Untouched by <see cref="SaveRoutesTo"/> /
        /// <see cref="LoadRoutesFrom"/>; a loaded route carries no escrow. The
        /// reservation is a within-tick / dispatch-to-window-phase guard recomputed
        /// from pending route state on the next <see cref="RouteOrchestrator.Tick(double)"/>,
        /// so it need not survive a scene change (cleared at the four lifecycle
        /// sites below).</para>
        ///
        /// <para><b>Pure RAM - reads NO ERS/ELS.</b> The escrow holds nothing from
        /// the committed-recording set or the ledger action list; it is a plain
        /// reservation counter (routeId/pid/resource -&gt; double), so it passes the
        /// ERS/ELS grep gate (<c>scripts/grep-audit-ers-els.ps1</c>) with no
        /// allowlist entry.</para>
        ///
        /// <para><b>DROP-not-revert.</b> Escrow has no ledger row; the physical
        /// debit's revert is the rewind quicksave + ELS replay keys (RouteModule
        /// observe-only). The clear sites only RELEASE reservations.</para>
        /// </summary>
        private static readonly Dictionary<string, Dictionary<uint, Dictionary<string, double>>>
            cargoEscrow = new Dictionary<string, Dictionary<uint, Dictionary<string, double>>>(StringComparer.Ordinal);

        /// <summary>
        /// Add a route. Idempotent on <see cref="Route.Id"/>: a second call
        /// with the same Id logs a Warn and does NOT replace the existing
        /// entry. Callers wanting replace semantics must remove-then-add.
        /// </summary>
        internal static void AddRoute(Route route)
        {
            if (route == null)
            {
                ParsekLog.Warn(Tag, "AddRoute: null route — ignored");
                return;
            }
            if (string.IsNullOrEmpty(route.Id))
            {
                ParsekLog.Warn(Tag, "AddRoute: route with empty Id — ignored");
                return;
            }

            for (int i = 0; i < committedRoutes.Count; i++)
            {
                if (string.Equals(committedRoutes[i].Id, route.Id, System.StringComparison.Ordinal))
                {
                    ParsekLog.Warn(Tag,
                        $"AddRoute: duplicate id={ShortId(route.Id)} (full={route.Id}); " +
                        "keeping the original entry. Callers wanting replace semantics " +
                        "must RemoveRoute first, then AddRoute.");
                    return;
                }
            }

            committedRoutes.Add(route);
            int stopCount = route.Stops != null ? route.Stops.Count : 0;
            ParsekLog.Info(Tag,
                $"Route {ShortId(route.Id)} added: status={route.Status} stops={stopCount}");
        }

        /// <summary>
        /// Remove a route by Id. Returns true on removal, false on miss or on
        /// an empty/null id (both logged at Warn).
        /// </summary>
        internal static bool RemoveRoute(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                ParsekLog.Warn(Tag, "RemoveRoute: null or empty id — ignored");
                return false;
            }

            for (int i = 0; i < committedRoutes.Count; i++)
            {
                if (string.Equals(committedRoutes[i].Id, id, System.StringComparison.Ordinal))
                {
                    committedRoutes.RemoveAt(i);
                    // M4b Phase B2: a removed / tombstoned route drops its whole
                    // escrow reservation so a competing route's available amount
                    // rises back. DROP-not-revert: this hooks RemoveRoute (not
                    // SupersedeCommit) because a reservation is reserve-and-released
                    // within one EmitLoopCycle and essentially never live across a
                    // tombstone - belt-and-suspenders. No-op when none held.
                    DropRouteEscrow(id);
                    ParsekLog.Info(Tag, $"Route {ShortId(id)} removed");
                    return true;
                }
            }

            ParsekLog.Warn(Tag, $"RemoveRoute: route {ShortId(id)} not found (full={id})");
            return false;
        }

        /// <summary>
        /// Look up a route by Id. Silent on both hit and miss — callers
        /// decide whether to log absence as a warning.
        /// </summary>
        internal static bool TryGetRoute(string id, out Route route)
        {
            if (string.IsNullOrEmpty(id))
            {
                route = null;
                return false;
            }

            for (int i = 0; i < committedRoutes.Count; i++)
            {
                if (string.Equals(committedRoutes[i].Id, id, System.StringComparison.Ordinal))
                {
                    route = committedRoutes[i];
                    return true;
                }
            }

            route = null;
            return false;
        }

        /// <summary>
        /// Clear in-memory state. Test seam — production paths should
        /// remove individual routes through <see cref="RemoveRoute"/>.
        /// </summary>
        internal static void ResetForTesting()
        {
            int prevCount = committedRoutes.Count;
            committedRoutes.Clear();
            // M4b Phase B2: the escrow is shared static state, so a
            // [Collection("Sequential")] test that reserves must not leak its
            // reservation into the next test. Clear it here alongside the routes.
            int prevEscrowRoutes = cargoEscrow.Count;
            cargoEscrow.Clear();
            ParsekLog.Verbose(Tag,
                $"ResetForTesting prevCount={prevCount} prevEscrowRoutes={prevEscrowRoutes}");
        }

        // -----------------------------------------------------------------
        // M4b Phase B2: escrow lifecycle (reserve / release / drop / clear)
        // + the competing-route net read.
        // -----------------------------------------------------------------

        /// <summary>
        /// Reserve (accumulate) <paramref name="amount"/> of
        /// <paramref name="resourceName"/> against <c>(routeId, pid)</c>. Called at
        /// the all-or-nothing dispatch gate (B3) so a competing higher-priority
        /// route in the same / a later tick (before this route's physical debit)
        /// sees the source's available amount reduced by what this route reserved.
        /// Accumulates: a second reserve on the same key adds to the running total
        /// (e.g. two same-pid windows under <see cref="RouteEndpoint"/> OQ6).
        /// Non-positive amounts and null/empty keys are ignored. Pure RAM.
        /// </summary>
        internal static void ReserveCargo(string routeId, uint pid, string resourceName, double amount)
        {
            if (string.IsNullOrEmpty(routeId) || string.IsNullOrEmpty(resourceName))
            {
                ParsekLog.Warn(Tag,
                    $"ReserveCargo: null/empty routeId or resource (routeId={ShortId(routeId)} " +
                    $"resource={resourceName ?? "<null>"}) - ignored");
                return;
            }
            if (!(amount > 0.0))
                return; // nothing to reserve (also filters NaN)

            if (!cargoEscrow.TryGetValue(routeId, out var byPid))
            {
                byPid = new Dictionary<uint, Dictionary<string, double>>();
                cargoEscrow[routeId] = byPid;
            }
            if (!byPid.TryGetValue(pid, out var byResource))
            {
                byResource = new Dictionary<string, double>(StringComparer.Ordinal);
                byPid[pid] = byResource;
            }
            byResource.TryGetValue(resourceName, out double cur);
            double next = cur + amount;
            byResource[resourceName] = next;

            ParsekLog.Verbose(Tag,
                $"ReserveCargo routeId={ShortId(routeId)} pid={pid.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                $"resource={resourceName} amount={amount.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"newTotal={next.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Release (subtract) <paramref name="amount"/> of
        /// <paramref name="resourceName"/> from <c>(routeId, pid)</c>. Called at
        /// each window's physical debit (B3): the reservation is consumed as the
        /// cargo physically leaves the source. Clamps at zero and removes the
        /// resource / pid / route entry once it reaches zero so the map stays lean
        /// and a later net read sees no residual. Non-positive amounts and missing
        /// keys are no-ops (logged Verbose). Pure RAM.
        /// </summary>
        internal static void ReleaseCargo(string routeId, uint pid, string resourceName, double amount)
        {
            if (string.IsNullOrEmpty(routeId) || string.IsNullOrEmpty(resourceName))
            {
                ParsekLog.Warn(Tag,
                    $"ReleaseCargo: null/empty routeId or resource (routeId={ShortId(routeId)} " +
                    $"resource={resourceName ?? "<null>"}) - ignored");
                return;
            }
            if (!(amount > 0.0))
                return;

            if (!cargoEscrow.TryGetValue(routeId, out var byPid)
                || !byPid.TryGetValue(pid, out var byResource)
                || !byResource.TryGetValue(resourceName, out double cur))
            {
                ParsekLog.Verbose(Tag,
                    $"ReleaseCargo: no reservation to release routeId={ShortId(routeId)} " +
                    $"pid={pid.ToString(System.Globalization.CultureInfo.InvariantCulture)} resource={resourceName} - no-op");
                return;
            }

            double next = cur - amount;
            if (next <= 0.0)
            {
                byResource.Remove(resourceName);
                if (byResource.Count == 0) byPid.Remove(pid);
                if (byPid.Count == 0) cargoEscrow.Remove(routeId);
                next = 0.0;
            }
            else
            {
                byResource[resourceName] = next;
            }

            ParsekLog.Verbose(Tag,
                $"ReleaseCargo routeId={ShortId(routeId)} pid={pid.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                $"resource={resourceName} amount={amount.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"newTotal={next.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Drop a route's WHOLE reservation (all pids, all resources) in one shot.
        /// Called when the route is removed / tombstoned / aborted
        /// (<see cref="RemoveRoute"/>) so a competing route's available amount rises
        /// back. DROP-not-revert (plan D11): this only releases the reservation; the
        /// physical debit (if any already fired) is reverted by the rewind quicksave
        /// + ELS replay, never here. A no-op when the route holds no escrow. Pure RAM.
        /// </summary>
        internal static void DropRouteEscrow(string routeId)
        {
            if (string.IsNullOrEmpty(routeId))
                return;
            if (!cargoEscrow.TryGetValue(routeId, out var byPid))
                return;

            int pids = byPid.Count;
            int resourceEntries = 0;
            foreach (var kv in byPid)
                resourceEntries += kv.Value != null ? kv.Value.Count : 0;
            cargoEscrow.Remove(routeId);

            ParsekLog.Info(Tag,
                $"DropRouteEscrow routeId={ShortId(routeId)} droppedPids={pids} droppedResourceEntries={resourceEntries}");
        }

        /// <summary>
        /// Clear ALL escrow reservations (every route). Wired at the lifecycle
        /// boundaries where the in-cycle reservation is recomputed next tick:
        /// any within-game scene change (<c>onGameSceneSwitchRequested</c>) and game
        /// unload (<see cref="ParsekScenario.OnMainMenuTransition"/>). DROP-not-revert.
        /// Pure RAM.
        /// </summary>
        internal static void ClearAllEscrow(string reason)
        {
            int prevRoutes = cargoEscrow.Count;
            if (prevRoutes == 0)
                return; // common case: nothing reserved, stay quiet (cheap no-op)
            cargoEscrow.Clear();
            ParsekLog.Info(Tag,
                $"ClearAllEscrow reason={reason ?? "<none>"} clearedRoutes={prevRoutes}");
        }

        /// <summary>
        /// M4b Phase B2 NET semantics (plan D11): the amount of
        /// <paramref name="resourceName"/> on <paramref name="pid"/> that route
        /// <paramref name="forRouteId"/> may rely on = live stored MINUS the sum of
        /// reservations held by EVERY OTHER route on that pid+resource. A route does
        /// NOT subtract its OWN reservation (it owns what it reserved). This is the
        /// competing-route protection: route A reserves 100 from depot X at dispatch;
        /// route B gating X in the same / a later tick (before A's physical debit)
        /// sees X's available reduced by A's 100, so B cannot double-claim it.
        ///
        /// <para>Returns the total OTHER-route reservation to subtract (>= 0). The
        /// caller wraps its live stored-amount reader as
        /// <c>name =&gt; max(0, liveReader(name) - OtherRoutesReservedFor(routeId, pid, name))</c>.
        /// Pure RAM - reads only the escrow dict.</para>
        /// </summary>
        internal static double OtherRoutesReservedFor(string forRouteId, uint pid, string resourceName)
        {
            if (string.IsNullOrEmpty(resourceName) || cargoEscrow.Count == 0)
                return 0.0;

            double sum = 0.0;
            foreach (var routeEntry in cargoEscrow)
            {
                // A route never subtracts its OWN reservation - it owns what it
                // reserved (the crux of the net: only competing routes reduce the
                // amount this route sees).
                if (string.Equals(routeEntry.Key, forRouteId, StringComparison.Ordinal))
                    continue;
                var byPid = routeEntry.Value;
                if (byPid == null) continue;
                if (!byPid.TryGetValue(pid, out var byResource) || byResource == null)
                    continue;
                if (byResource.TryGetValue(resourceName, out double reserved) && reserved > 0.0)
                    sum += reserved;
            }
            return sum;
        }

        /// <summary>
        /// Test/diagnostic read: the amount route <paramref name="routeId"/> has
        /// reserved on <c>(pid, resourceName)</c> (0 when absent). Pure RAM.
        /// </summary>
        internal static double GetReservedForTesting(string routeId, uint pid, string resourceName)
        {
            if (string.IsNullOrEmpty(routeId) || string.IsNullOrEmpty(resourceName))
                return 0.0;
            if (cargoEscrow.TryGetValue(routeId, out var byPid)
                && byPid.TryGetValue(pid, out var byResource)
                && byResource.TryGetValue(resourceName, out double v))
                return v;
            return 0.0;
        }

        /// <summary>Test/diagnostic read: number of routes currently holding any escrow.</summary>
        internal static int EscrowRouteCountForTesting => cargoEscrow.Count;

        /// <summary>
        /// Write the current store into <paramref name="parent"/>. Strips any
        /// pre-existing <c>ROUTES</c> children first so stale entries from a
        /// prior save do not leak. When the store is empty, no <c>ROUTES</c>
        /// node is written at all — saves stay lean and
        /// <see cref="LoadRoutesFrom"/> treats a missing node as zero routes.
        /// </summary>
        internal static void SaveRoutesTo(ConfigNode parent)
        {
            if (parent == null)
            {
                ParsekLog.Warn(Tag, "SaveRoutesTo: null parent — skipped");
                return;
            }

            // Always strip pre-existing wrappers before deciding what to
            // write. A previously-saved ROUTES node with stale entries would
            // otherwise survive an empty-store save.
            parent.RemoveNodes(RoutesParentNodeName);

            if (committedRoutes.Count == 0)
            {
                ParsekLog.Verbose(Tag, "SaveRoutesTo: no routes to save");
                return;
            }

            ConfigNode routesNode = parent.AddNode(RoutesParentNodeName);
            for (int i = 0; i < committedRoutes.Count; i++)
            {
                Route route = committedRoutes[i];
                if (route == null) continue;
                ConfigNode routeNode = routesNode.AddNode(RouteChildNodeName);
                route.SerializeInto(routeNode);
            }

            ParsekLog.Info(Tag, $"SaveRoutesTo: wrote {committedRoutes.Count} route(s)");
        }

        /// <summary>
        /// Replace in-memory state with the contents of the <c>ROUTES</c>
        /// child node under <paramref name="parent"/>. Missing
        /// <c>ROUTES</c> node is the common "save with no routes" path —
        /// returns zero without warning. Routes that the Phase-2 codec
        /// rejects (null) are dropped silently here; the codec already
        /// emitted its own Warn explaining the reject reason.
        /// </summary>
        /// <returns>Number of routes successfully loaded.</returns>
        internal static int LoadRoutesFrom(ConfigNode parent)
        {
            // Wholesale replace: clear first, then fill from the save node.
            // Mirrors MilestoneStore.LoadMilestoneFile / RecordingStore load
            // semantics so callers do not have to manage the reset themselves.
            committedRoutes.Clear();

            if (parent == null)
            {
                ParsekLog.Verbose(Tag, "LoadRoutesFrom: null parent — 0 loaded");
                return 0;
            }

            ConfigNode routesNode = parent.GetNode(RoutesParentNodeName);
            if (routesNode == null)
            {
                ParsekLog.Verbose(Tag, "LoadRoutesFrom: no ROUTES node, 0 loaded");
                return 0;
            }

            ConfigNode[] routeNodes = routesNode.GetNodes(RouteChildNodeName);
            int loaded = 0;
            int dropped = 0;
            for (int i = 0; i < routeNodes.Length; i++)
            {
                Route route = Route.DeserializeFrom(routeNodes[i]);
                if (route == null)
                {
                    // Codec already logged the Warn explaining why.
                    dropped++;
                    continue;
                }
                committedRoutes.Add(route);
                loaded++;
            }

            if (dropped > 0)
            {
                ParsekLog.Info(Tag,
                    $"LoadRoutesFrom: loaded {loaded} route(s), {dropped} dropped (see prior Warn lines)");
            }
            else
            {
                ParsekLog.Info(Tag, $"LoadRoutesFrom: loaded {loaded} route(s)");
            }

            return loaded;
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "<no-id>";
            return id.Length > 8 ? id.Substring(0, 8) : id;
        }

        // -----------------------------------------------------------------
        // Phase 5: ERS-driven source-ref validation
        // -----------------------------------------------------------------

        /// <summary>
        /// For each committed route, validate every <see cref="RouteSourceRef"/>
        /// against the current ERS (Effective Recording Set, computed by
        /// <see cref="EffectiveState.ComputeERS"/>). Transition status to:
        /// <list type="bullet">
        ///   <item><c>MissingSourceRecording</c> if any source-ref recording id is not in ERS
        ///     (covers deletion AND supersede / rewind-retirement, since those are filtered out of ERS).</item>
        ///   <item><c>SourceChanged</c> if every source-ref recording is in ERS but at least
        ///     one fingerprint field has drifted.</item>
        ///   <item>Recovery only from <c>MissingSourceRecording</c>: if the route
        ///     was MissingSourceRecording and every source-ref now resolves AND fingerprints match,
        ///     it returns to its captured <see cref="Route.PreMissingStatus"/> (a deliberately
        ///     Paused route comes back Paused, an Active route comes back Active), defaulting to
        ///     <c>Active</c> when no baseline was captured.</item>
        /// </list>
        /// Routes in <see cref="RouteStatus.SourceChanged"/> do NOT auto-recover even when
        /// fingerprints match — design §7.4 requires explicit recreation. Routes with other
        /// non-source-related statuses (Paused, WaitingForResources, etc.) keep that status
        /// unless a source problem is detected (in which case they transition through the same
        /// rules as Active routes — a missing source is more urgent than a pause).
        /// </summary>
        /// <remarks>
        /// Called from <see cref="ParsekScenario.OnLoad"/> and from
        /// <see cref="SupersedeCommit.FlipMergeStateAndClearTransient"/> after a
        /// re-fly supersede commits the new state. New ERS-mutating code paths
        /// must add a RevalidateSources call or document why staleness is
        /// acceptable until next save/load — otherwise routes pointing at the
        /// newly-mutated recordings will retain their pre-mutation status until
        /// the next save/load cycle.
        /// </remarks>
        /// <param name="reason">Free-form audit string included in every transition log line.</param>
        /// <returns>The number of routes whose status changed during this pass.</returns>
        internal static int RevalidateSources(string reason)
        {
            string reasonOrNone = reason ?? "<none>";

            // Single ERS materialisation per pass — O(ERS size). Routes
            // iterate this dict for O(1) source-ref lookup; computing ERS
            // per source-ref would be O(routes * ERS).
            var ers = EffectiveState.ComputeERS();
            var ersById = new Dictionary<string, Recording>(StringComparer.Ordinal);
            int ersTotal = ers != null ? ers.Count : 0;
            int ersIndexed = 0;
            int ersSkippedNoId = 0;
            if (ers != null)
            {
                for (int i = 0; i < ers.Count; i++)
                {
                    var rec = ers[i];
                    if (rec == null) continue;
                    if (string.IsNullOrEmpty(rec.RecordingId))
                    {
                        ersSkippedNoId++;
                        continue;
                    }
                    // ERS contract: ids are unique among visible recordings.
                    // Use [] (overwrite) defensively in case of duplicate ids
                    // — the last entry wins, which matches CommittedRecordings
                    // append-order semantics.
                    ersById[rec.RecordingId] = rec;
                    ersIndexed++;
                }
            }
            if (ersSkippedNoId > 0)
            {
                ParsekLog.Verbose(Tag,
                    $"RevalidateSources: skipped {ersSkippedNoId} ERS entry/entries with null/empty RecordingId");
            }

            int total = committedRoutes.Count;
            int transitioned = 0;

            for (int ri = 0; ri < committedRoutes.Count; ri++)
            {
                Route route = committedRoutes[ri];
                if (route == null) continue;

                if (route.SourceRefs == null || route.SourceRefs.Count == 0)
                {
                    // Defensive: Phase-2 codec rejects routes with no SOURCE
                    // children, but a route already in memory (e.g. injected
                    // by a test) can land here. Log + skip; status untouched.
                    ParsekLog.Verbose(Tag,
                        $"RevalidateSources: route {ShortId(route.Id)} has no SourceRefs, skipping (reason={reasonOrNone})");
                    continue;
                }

                RouteStatus prev = route.Status;

                // Inspect every source-ref against ERS. Stop on the first
                // problem so the log line names the specific cause.
                bool anyMissing = false;
                bool anyDrift = false;
                string firstMissingId = null;
                string firstDriftId = null;
                string firstDriftField = null;
                for (int si = 0; si < route.SourceRefs.Count; si++)
                {
                    var sref = route.SourceRefs[si];
                    if (sref == null || string.IsNullOrEmpty(sref.RecordingId))
                    {
                        // A null/blank source-ref is treated as "missing" —
                        // there is no recording to validate against, so the
                        // route cannot dispatch. Same end state, distinct
                        // cause for the log.
                        anyMissing = true;
                        firstMissingId = sref?.RecordingId ?? "<null>";
                        break;
                    }
                    if (!ersById.TryGetValue(sref.RecordingId, out Recording rec))
                    {
                        anyMissing = true;
                        firstMissingId = sref.RecordingId;
                        break;
                    }
                    var live = BuildLiveSourceRefForComparison(rec);
                    string driftField;
                    if (!FirstDifferingField(sref, live, out driftField))
                    {
                        // Field-by-field comparison flagged a drift; preserve
                        // which field for the audit log line.
                        anyDrift = true;
                        firstDriftId = sref.RecordingId;
                        firstDriftField = driftField;
                        break;
                    }
                }

                RouteStatus next = prev;
                string cause = null;

                if (anyMissing)
                {
                    next = RouteStatus.MissingSourceRecording;
                    cause = $"MissingSourceRecording/source-not-in-ers id={ShortId(firstMissingId)}";

                    // Capture the pre-missing status on the INTO-missing edge only,
                    // so a deliberate Paused (or any other non-source status) can be
                    // restored faithfully on recovery instead of silently un-pausing
                    // to Active. Guard against overwriting a previously-captured value
                    // when the route is already MissingSourceRecording (a repeated
                    // pass must not clobber the remembered status with the missing
                    // status itself). Source-problem statuses are never captured as a
                    // pre-missing baseline (they are not a state worth restoring to).
                    if (prev != RouteStatus.MissingSourceRecording
                        && prev != RouteStatus.SourceChanged)
                    {
                        if (route.PreMissingStatus != prev)
                        {
                            ParsekLog.Verbose(Tag,
                                $"RevalidateSources: route {ShortId(route.Id)} capturing preMissingStatus={prev} " +
                                $"(reason={reasonOrNone})");
                        }
                        route.PreMissingStatus = prev;
                    }
                }
                else if (anyDrift)
                {
                    next = RouteStatus.SourceChanged;
                    cause = $"SourceChanged/{firstDriftField}-drift id={ShortId(firstDriftId)}";
                }
                else
                {
                    // No problem detected. Recovery is only allowed from
                    // MissingSourceRecording — design §7.4 requires explicit
                    // recreation to leave SourceChanged.
                    if (prev == RouteStatus.MissingSourceRecording)
                    {
                        // Restore the remembered pre-missing status so a Paused route
                        // comes back Paused and an Active route comes back Active.
                        // The default sentinel (Active) covers a route that was
                        // already MissingSourceRecording on load with no captured
                        // baseline. The production capture path never records a
                        // source-problem status as the baseline; guard against a
                        // hand-edited / corrupt save seeding SourceChanged or
                        // MissingSourceRecording (a route must never auto-recover INTO
                        // SourceChanged per design §7.4, nor loop back to Missing) by
                        // falling back to Active in those cases.
                        RouteStatus baseline = route.PreMissingStatus;
                        if (baseline == RouteStatus.SourceChanged
                            || baseline == RouteStatus.MissingSourceRecording)
                        {
                            ParsekLog.Warn(Tag,
                                $"RevalidateSources: route {ShortId(route.Id)} has an invalid " +
                                $"preMissingStatus={baseline}; falling back to Active on recovery " +
                                $"(reason={reasonOrNone})");
                            baseline = RouteStatus.Active;
                        }
                        next = baseline;
                        cause = $"{next}/source-restored preMissing={route.PreMissingStatus}";
                    }
                    else
                    {
                        // SourceChanged stays SourceChanged. Everything else
                        // stays put — no spurious self-transitions.
                        next = prev;
                    }
                }

                if (next != prev)
                {
                    // logistics-recovery-credit section 5.4 (ENDPOINT-LOST /
                    // source-missing tail): a loop-route that flips INTO
                    // MissingSourceRecording / SourceChanged stops crossing, so its
                    // last dispatched cycle's deferred recovery credit would be
                    // stranded forever (its "next crossing" never comes). Flush the
                    // owed credit at this transition BEFORE TransitionTo, mirroring
                    // the TryPause / armed-pause / EndpointLost-at-delivery flush
                    // sites. SourceChanged never auto-recovers (design 7.4 requires
                    // recreation) and a deleted MissingSourceRecording route is gone
                    // permanently, so without this the credit (owed funds) leaks.
                    // Defensive: a degenerate env / -1 UT makes EmitPendingRecoveryCredit
                    // no-op safely on the Career gate and clear the stale marker.
                    if (IsSourceProblemStatus(next) && !IsSourceProblemStatus(prev))
                    {
                        FlushPendingRecoveryCreditOnSourceProblem(route, reasonOrNone);
                    }

                    route.TransitionTo(next, $"{reasonOrNone}/{cause}");
                    transitioned++;

                    // Clear the remembered baseline once we have left the missing
                    // state, so a future into-missing edge re-captures fresh and a
                    // healthy route never carries a stale pre-missing status. Reset
                    // to the Active sentinel default (the codec then omits it).
                    if (prev == RouteStatus.MissingSourceRecording
                        && next != RouteStatus.MissingSourceRecording
                        && route.PreMissingStatus != RouteStatus.Active)
                    {
                        ParsekLog.Verbose(Tag,
                            $"RevalidateSources: route {ShortId(route.Id)} clearing preMissingStatus " +
                            $"(was {route.PreMissingStatus}) after recovery to {next}");
                        route.PreMissingStatus = RouteStatus.Active;
                    }
                }
            }

            ParsekLog.Info(Tag,
                $"RevalidateSources reason={reasonOrNone} routes={total} transitioned={transitioned} " +
                $"ersIndexed={ersIndexed} ersTotal={ersTotal}");

            return transitioned;
        }

        /// <summary>
        /// True for the two source-problem stop states a loop-route can flip into
        /// during <see cref="RevalidateSources(string)"/> that halt crossing
        /// (<see cref="RouteStatus.MissingSourceRecording"/> /
        /// <see cref="RouteStatus.SourceChanged"/>). Used to gate the deferred
        /// recovery-credit flush so it fires only on the INTO-source-problem edge,
        /// never on a self-edge or a recovery edge.
        /// </summary>
        internal static bool IsSourceProblemStatus(RouteStatus status)
        {
            return status == RouteStatus.MissingSourceRecording
                || status == RouteStatus.SourceChanged;
        }

        /// <summary>
        /// Flush the route's last dispatched cycle's deferred recovery credit when
        /// the route flips into a source-problem stop state (logistics-recovery-credit
        /// section 5.4). Resolves a live UT + env defensively, exactly like
        /// <see cref="RouteOrchestrator.TryPause(Route)"/>: an early-load or off-Unity
        /// context that cannot obtain live values passes a null env / -1 UT, and
        /// <see cref="RouteOrchestrator.EmitPendingRecoveryCredit"/> then no-ops on
        /// the Career-KSC gate and clears any stale pending marker without emitting.
        /// Idempotent via the credit's keyed backstop, so a re-presented transition
        /// never double-credits.
        /// </summary>
        private static void FlushPendingRecoveryCreditOnSourceProblem(Route route, string reasonOrNone)
        {
            if (route == null) return;

            // Fast path: nothing owed, so do not pay the live UT/env resolution cost.
            if (string.IsNullOrEmpty(route.PendingRecoveryCreditCycleId))
                return;

            double ut = -1.0;
            IRouteRuntimeEnvironment env = null;
            try
            {
                ut = Planetarium.GetUniversalTime();
                env = new LiveRouteRuntimeEnvironment();
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"RevalidateSources: route {ShortId(route.Id)} live UT/env resolution threw " +
                    $"{ex.GetType().Name}: {ex.Message}; flushing recovery credit without a live funds context " +
                    $"(reason={reasonOrNone})");
            }

            ParsekLog.Verbose(Tag,
                $"RevalidateSources: route {ShortId(route.Id)} flushing owed recovery credit before " +
                $"source-problem transition (reason={reasonOrNone})");
            RouteOrchestrator.EmitPendingRecoveryCredit(route, ut, env);
        }

        // Builds a comparison-only RouteSourceRef from a live Recording so
        // RevalidateSources can compare field-by-field. Mirrors the Phase-1
        // capture shape; the only computed field is RouteProofHash.
        private static RouteSourceRef BuildLiveSourceRefForComparison(Recording rec)
        {
            if (rec == null)
            {
                return new RouteSourceRef
                {
                    RouteProofHash = RouteProofHasher.NoRouteProofSentinel
                };
            }
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
                RouteProofHash = RouteProofHasher.ComputeRouteProofHashFromRecording(rec)
            };
        }

        /// <summary>
        /// Compares two source-refs field by field and returns true when every
        /// field matches. On mismatch, <paramref name="differingField"/> names
        /// the first differing field (in declaration order) so the audit log
        /// can pinpoint the drift.
        /// </summary>
        private static bool FirstDifferingField(
            RouteSourceRef a, RouteSourceRef b, out string differingField)
        {
            differingField = null;
            if (a == null && b == null) return true;
            if (a == null || b == null)
            {
                differingField = "ref-null";
                return false;
            }

            if (!string.Equals(a.RecordingId, b.RecordingId, StringComparison.Ordinal))
            { differingField = "recording-id"; return false; }
            if (!string.Equals(a.TreeId, b.TreeId, StringComparison.Ordinal))
            { differingField = "tree-id"; return false; }
            if (a.TreeOrder != b.TreeOrder)
            { differingField = "tree-order"; return false; }
            if (a.RecordingFormatVersion != b.RecordingFormatVersion)
            { differingField = "recording-format-version"; return false; }
            if (a.RecordingSchemaGeneration != b.RecordingSchemaGeneration)
            { differingField = "recording-schema-generation"; return false; }
            if (a.SidecarEpoch != b.SidecarEpoch)
            { differingField = "sidecar-epoch"; return false; }
            if (!a.StartUT.Equals(b.StartUT))
            { differingField = "start-ut"; return false; }
            if (!a.EndUT.Equals(b.EndUT))
            { differingField = "end-ut"; return false; }
            if (!string.Equals(a.RouteProofHash, b.RouteProofHash, StringComparison.Ordinal))
            { differingField = "route-proof-hash"; return false; }
            return true;
        }
    }
}

