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
            ParsekLog.Verbose(Tag, $"ResetForTesting prevCount={prevCount}");
        }

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
            return RouteIds.Short(id);
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
            var ersById = BuildErsIndex(out int ersIndexed, out int ersTotal);

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
                RouteSourceInspection inspection = InspectRouteSources(route, ersById);

                RouteStatus next = DecideRevalidatedStatus(route, prev, inspection, reasonOrNone, out string cause);

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
        /// Materialises ERS into a RecordingId -> Recording index for O(1) source-ref
        /// lookup. Returns the index and reports the indexed/total counts the caller
        /// logs with. Extracted verbatim from RevalidateSources (no logic change).
        /// </summary>
        private static Dictionary<string, Recording> BuildErsIndex(out int ersIndexed, out int ersTotal)
        {
            var ers = EffectiveState.ComputeERS();
            var ersById = new Dictionary<string, Recording>(StringComparer.Ordinal);
            ersTotal = ers != null ? ers.Count : 0;
            ersIndexed = 0;
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

            return ersById;
        }

        /// <summary>
        /// Inspects every source-ref of a route against the ERS index, stopping on the
        /// first problem so the caller's log line names the specific cause. Extracted
        /// verbatim from RevalidateSources (no logic change).
        /// </summary>
        private static RouteSourceInspection InspectRouteSources(
            Route route, Dictionary<string, Recording> ersById)
        {
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

            return new RouteSourceInspection
            {
                AnyMissing = anyMissing,
                AnyDrift = anyDrift,
                FirstMissingId = firstMissingId,
                FirstDriftId = firstDriftId,
                FirstDriftField = firstDriftField,
            };
        }

        /// <summary>
        /// Decides the next RouteStatus from a source inspection result, capturing /
        /// restoring the pre-missing baseline and emitting the audit cause string.
        /// Extracted verbatim from RevalidateSources (no logic change).
        /// </summary>
        private static RouteStatus DecideRevalidatedStatus(
            Route route,
            RouteStatus prev,
            RouteSourceInspection inspection,
            string reasonOrNone,
            out string cause)
        {
            RouteStatus next = prev;
            cause = null;

            if (inspection.AnyMissing)
            {
                next = RouteStatus.MissingSourceRecording;
                cause = $"MissingSourceRecording/source-not-in-ers id={ShortId(inspection.FirstMissingId)}";

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
            else if (inspection.AnyDrift)
            {
                next = RouteStatus.SourceChanged;
                cause = $"SourceChanged/{inspection.FirstDriftField}-drift id={ShortId(inspection.FirstDriftId)}";
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

            return next;
        }

        /// <summary>
        /// Per-route source-inspection result handed from <see cref="InspectRouteSources"/>
        /// to <see cref="DecideRevalidatedStatus"/>.
        /// </summary>
        private struct RouteSourceInspection
        {
            public bool AnyMissing;
            public bool AnyDrift;
            public string FirstMissingId;
            public string FirstDriftId;
            public string FirstDriftField;
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

