using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>What <see cref="RouteLoadReconcile.ReconcileAtInSessionLoad(LoadKind, double, double, IReadOnlyList{Route})"/> did.</summary>
    internal enum RouteLoadReconcileOutcome
    {
        /// <summary>Route rows after the cutoff retired, the route store reconciled at it.</summary>
        Reconciled,
        /// <summary>Not this hook's load: the cold path reads routes from the save,
        /// <c>HandleRewindOnLoad</c> reconciles a plain rewind, the Re-Fly bundle a Re-Fly start.</summary>
        SkippedOwnedElsewhere,
        /// <summary>No finite positive cutoff UT to key the reconcile to.</summary>
        SkippedNoUsableCutoff,
        /// <summary>An in-session load with no route state after the loaded save: a forward or
        /// same-instant scene change.</summary>
        SkippedNothingAfterCutoff,
    }

    /// <summary>
    /// The in-session load route reconcile (owner ruling 2026-10-07, todo
    /// ROUTE-STATE-NOT-RECONCILED-ON-F9-REVERT-DISCARD). <see cref="RouteStore"/> is loaded from
    /// the save on a cold load only, and the ledger is never reloaded in session, so a load that
    /// goes back in time keeps the abandoned future's route rows and loop cursors: the re-flown
    /// crossings are swallowed by the cursor and the UT-blind dispatch dedup while their funds
    /// rows survive. An F9 quickload, a stock revert and the Esc-menu Discard Re-fly load
    /// therefore run the SAME reconcile the go-back rewind exit runs
    /// (<see cref="Ledger.RetireFutureRouteActionsAtRewind"/> +
    /// <see cref="RouteRewindClassifier.ReconcileStoreAtRewind"/>) keyed to the loaded save's UT.
    /// Every other in-session load runs it only when route state lies after the loaded save
    /// (an F9 at the Space Center or Tracking Station), so a forward scene change is untouched.
    ///
    /// <para>The shared reconcile's cursor reset (-1) makes the next tick fire the crossing whose
    /// dock instant most recently passed, under a fresh cycle id the dispatch dedup cannot match;
    /// that crossing was dispatched before the cutoff and its cargo is in the loaded world, so it
    /// would be delivered and charged twice. So each kept route then takes its loop position back
    /// from the loaded save's own route copy (loop anchor, route and per-stop cursors, window
    /// anchor, partner alternation cursor), and the recovery credit the save still owes when the
    /// reconcile cleared a later one (<see cref="RestoreLoopPositionFromSave"/>). An in-session
    /// load reads that copy from its OnLoad node; the two rewind exits run the same restore
    /// through <see cref="RestoreLoopPositionAtRewindExit"/>.</para>
    ///
    /// <para>OnLoad-safe: removes ledger rows and mutates Route instances only, never
    /// <c>Ledger.AddAction</c>, so it runs inside <c>ParsekScenario.OnLoad</c> before the
    /// recalculation, as the go-back exit does.</para>
    /// </summary>
    internal static class RouteLoadReconcile
    {
        internal const string LogTag = "LoadPolicy";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>A finite positive UT (a UT-0 save has no route history to reconcile).</summary>
        internal static bool IsUsableCutoffUT(double ut)
        {
            return !double.IsNaN(ut) && !double.IsInfinity(ut) && ut > 0.0;
        }

        /// <summary>
        /// The UT the loaded world sits at. The loaded save's own clock
        /// (<c>flightState.universalTime</c>, <paramref name="loadedSaveUT"/>), except a stock
        /// revert takes the earlier of it and the revert prune's launch boundary
        /// (<paramref name="revertPruneCutoffUT"/>, <c>ParsekScenario.ResolveRevertPruneCutoff</c>):
        /// a revert to the editor can hand OnLoad a game still carrying the revert-moment clock.
        /// NaN when neither is usable.
        /// </summary>
        internal static double ResolveCutoffUT(LoadKind kind, double loadedSaveUT, double revertPruneCutoffUT)
        {
            bool saveUsable = IsUsableCutoffUT(loadedSaveUT);
            if (kind == LoadKind.StockRevert && IsUsableCutoffUT(revertPruneCutoffUT))
                return saveUsable ? Math.Min(loadedSaveUT, revertPruneCutoffUT) : revertPruneCutoffUT;
            return saveUsable ? loadedSaveUT : double.NaN;
        }

        /// <summary>
        /// Whether this load runs the reconcile. The kinds the classifier already knows went back
        /// in time (QuickloadFlight, StockRevert, DiscardReFly) always run: a cursor that advanced
        /// after the save leaves no UT stamp to find, and the revert prune has already removed the
        /// rows. InSessionOther runs only on evidence (<paramref name="routeStateAfterCutoff"/>).
        /// Cold, PlainRewind and ReFlyStart are owned by the cold load, <c>HandleRewindOnLoad</c>
        /// and the Re-Fly bundle.
        /// </summary>
        internal static RouteLoadReconcileOutcome Decide(LoadKind kind, double cutoffUT, bool routeStateAfterCutoff)
        {
            switch (kind)
            {
                case LoadKind.Cold:
                case LoadKind.PlainRewind:
                case LoadKind.ReFlyStart:
                    return RouteLoadReconcileOutcome.SkippedOwnedElsewhere;
                case LoadKind.QuickloadFlight:
                case LoadKind.StockRevert:
                case LoadKind.DiscardReFly:
                    return IsUsableCutoffUT(cutoffUT)
                        ? RouteLoadReconcileOutcome.Reconciled
                        : RouteLoadReconcileOutcome.SkippedNoUsableCutoff;
                case LoadKind.InSessionOther:
                    if (!IsUsableCutoffUT(cutoffUT))
                        return RouteLoadReconcileOutcome.SkippedNoUsableCutoff;
                    return routeStateAfterCutoff
                        ? RouteLoadReconcileOutcome.Reconciled
                        : RouteLoadReconcileOutcome.SkippedNothingAfterCutoff;
            }
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown LoadKind");
        }

        /// <summary>
        /// True when anything that HAPPENED on a route lies strictly after
        /// <paramref name="cutoffUT"/>: a free-standing route ledger row
        /// (<paramref name="ledgerHasFutureRouteRows"/>), a route created after it, or a route's
        /// cycle start, hold, partial delivery or owed-credit dispatch stamped after it. Scheduled
        /// UTs (next dispatch, next eligibility check, pending delivery) are the future by design
        /// and do not count. <paramref name="evidence"/> names the first hit, or "none".
        /// </summary>
        internal static bool HasRouteStateAfterUT(
            double cutoffUT, IReadOnlyList<Route> committedRoutes, bool ledgerHasFutureRouteRows, out string evidence)
        {
            if (ledgerHasFutureRouteRows)
            {
                evidence = "ledger-rows";
                return true;
            }
            if (committedRoutes != null)
            {
                for (int i = 0; i < committedRoutes.Count; i++)
                {
                    Route r = committedRoutes[i];
                    if (r == null)
                        continue;
                    string field = null;
                    if (r.CreatedUT > cutoffUT)
                        field = "created";
                    else if (r.CurrentCycleStartUT.HasValue && r.CurrentCycleStartUT.Value > cutoffUT)
                        field = "cycle-start";
                    else if (r.LastHoldUT > cutoffUT)
                        field = "hold";
                    else if (r.LastPartialDeliveryUT > cutoffUT)
                        field = "partial-delivery";
                    else if (!string.IsNullOrEmpty(r.PendingRecoveryCreditCycleId)
                        && r.PendingRecoveryCreditDispatchUT > cutoffUT)
                        field = "credit-dispatch";
                    if (field != null)
                    {
                        evidence = "route-" + field + ":" + RouteIds.Short(r.Id);
                        return true;
                    }
                }
            }
            evidence = "none";
            return false;
        }

        /// <summary>
        /// True when the loop index space of <paramref name="live"/> is the one
        /// <paramref name="saved"/>'s cursors were counted in: same backing mission, cadence,
        /// dispatch interval, transit span, recorded dock UT, window basis and per-stop dock UTs.
        /// A cadence change rebases the cursors (<c>RouteCadence</c>), so a saved index taken
        /// before it would stall or jump the live clock.
        /// </summary>
        internal static bool ClockDefinitionMatches(Route live, Route saved)
        {
            if (live == null || saved == null)
                return false;
            if (!string.Equals(live.BackingMissionTreeId, saved.BackingMissionTreeId, StringComparison.Ordinal))
                return false;
            if (live.CadenceMultiplier != saved.CadenceMultiplier
                || live.DispatchInterval != saved.DispatchInterval
                || live.TransitDuration != saved.TransitDuration
                || live.RecordedDockUT != saved.RecordedDockUT
                || live.ReaimWindowBasisEngaged != saved.ReaimWindowBasisEngaged)
            {
                return false;
            }
            int liveStops = live.Stops?.Count ?? 0;
            int savedStops = saved.Stops?.Count ?? 0;
            if (liveStops != savedStops)
                return false;
            for (int i = 0; i < liveStops; i++)
            {
                RouteStop ls = live.Stops[i];
                RouteStop ss = saved.Stops[i];
                if (ls == null || ss == null)
                {
                    if (ls != ss)
                        return false;
                    continue;
                }
                if (ls.RecordedDockUT != ss.RecordedDockUT)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// After the shared reconcile, gives each kept route back the loop position the loaded
        /// save carries for it: <see cref="Route.LoopAnchorUT"/> (the index space) always, then
        /// <see cref="Route.LastObservedLoopCycleIndex"/>, <see cref="Route.WindowAnchorCycleIndex"/>
        /// and every stop's <see cref="RouteStop.LastFiredCycleIndex"/> when
        /// <see cref="ClockDefinitionMatches"/>; <see cref="Route.LastConsumedPartnerCycle"/> when
        /// the route is linked to the same partner. The other inputs of the loop span (excluded
        /// interval keys, origin undock UT, source refs, creation members) are set at creation and
        /// never change, so the live copy already equals the save's. Also restores the recovery
        /// credit the save still owes (its dispatch at or before the cutoff) when the live route
        /// owes none: the reconcile clears a credit owed by a dispatch after the cutoff and cannot
        /// bring back the one a later flush paid.
        /// Restores nothing when the save is newer than the cutoff (its position is not the
        /// position at the cutoff) or unknown. A route missing from the save keeps the reset.
        /// Pure over the Route instances handed in (<paramref name="logTag"/> only tags its log
        /// lines with the calling exit). Returns the loop positions restored.
        /// </summary>
        internal static int RestoreLoopPositionFromSave(
            IReadOnlyList<Route> keptRoutes, IReadOnlyList<Route> savedRoutes,
            double cutoffUT, double loadedSaveUT,
            out int creditsRestored, out int definitionChanged, out int missingFromSave,
            string logTag = LogTag)
        {
            creditsRestored = 0;
            definitionChanged = 0;
            missingFromSave = 0;
            if (keptRoutes == null || keptRoutes.Count == 0 || savedRoutes == null)
                return 0;
            if (!IsUsableCutoffUT(loadedSaveUT) || !IsUsableCutoffUT(cutoffUT) || loadedSaveUT > cutoffUT)
                return 0;

            var savedById = new Dictionary<string, Route>(StringComparer.Ordinal);
            for (int i = 0; i < savedRoutes.Count; i++)
            {
                Route s = savedRoutes[i];
                if (s != null && !string.IsNullOrEmpty(s.Id) && !savedById.ContainsKey(s.Id))
                    savedById.Add(s.Id, s);
            }

            int restored = 0;
            int anchorsRestored = 0;
            int partnerCursorsRestored = 0;
            for (int i = 0; i < keptRoutes.Count; i++)
            {
                Route kept = keptRoutes[i];
                if (kept == null || string.IsNullOrEmpty(kept.Id))
                    continue;
                if (!savedById.TryGetValue(kept.Id, out Route saved))
                {
                    missingFromSave++;
                    continue;
                }

                // The loop anchor is the cursors' index space: BuildMission copies it into the
                // backing mission and the loop builder takes the phase anchor as
                // max(LoopAnchorUT, spanEnd). TryActivate after the save moved it to the
                // activation UT, which would hold the clock (or offset every saved index) on the
                // abandoned future's anchor. It goes back even when the cursors stay reset.
                if (kept.LoopAnchorUT != saved.LoopAnchorUT)
                {
                    ParsekLog.Verbose(logTag,
                        $"Route reconcile: route {RouteIds.Short(kept.Id)} loop anchor " +
                        $"{kept.LoopAnchorUT.ToString("R", IC)} -> {saved.LoopAnchorUT.ToString("R", IC)} (saved)");
                    kept.LoopAnchorUT = saved.LoopAnchorUT;
                    anchorsRestored++;
                }

                // A linked pair alternates on LastConsumedPartnerCycle against the partner's
                // CompletedCycles, which the shared reconcile rebuilds from the kept rows; left at
                // the abandoned future's value it holds both routes. Only for the same partner.
                if (string.Equals(kept.LinkedRouteId, saved.LinkedRouteId, StringComparison.Ordinal)
                    && kept.LastConsumedPartnerCycle != saved.LastConsumedPartnerCycle)
                {
                    kept.LastConsumedPartnerCycle = saved.LastConsumedPartnerCycle;
                    partnerCursorsRestored++;
                }

                if (string.IsNullOrEmpty(kept.PendingRecoveryCreditCycleId)
                    && !string.IsNullOrEmpty(saved.PendingRecoveryCreditCycleId)
                    && saved.PendingRecoveryCreditDispatchUT >= 0.0
                    && saved.PendingRecoveryCreditDispatchUT <= cutoffUT)
                {
                    kept.PendingRecoveryCreditCycleId = saved.PendingRecoveryCreditCycleId;
                    kept.PendingRecoveryCreditDispatchUT = saved.PendingRecoveryCreditDispatchUT;
                    creditsRestored++;
                }

                if (!ClockDefinitionMatches(kept, saved))
                {
                    definitionChanged++;
                    ParsekLog.Verbose(logTag,
                        $"Route reconcile: route {RouteIds.Short(kept.Id)} clock definition changed after the save; " +
                        "loop cursors stay reset (next crossing fires)");
                    continue;
                }

                kept.LastObservedLoopCycleIndex = saved.LastObservedLoopCycleIndex;
                kept.WindowAnchorCycleIndex = saved.WindowAnchorCycleIndex;
                int stops = kept.Stops?.Count ?? 0;
                for (int s = 0; s < stops; s++)
                {
                    if (kept.Stops[s] != null && saved.Stops[s] != null)
                        kept.Stops[s].LastFiredCycleIndex = saved.Stops[s].LastFiredCycleIndex;
                }
                restored++;
            }
            ParsekLog.Verbose(logTag,
                $"Route reconcile: restored from the save cursors={restored.ToString(IC)} " +
                $"anchors={anchorsRestored.ToString(IC)} partnerCursors={partnerCursorsRestored.ToString(IC)} " +
                $"credits={creditsRestored.ToString(IC)} clockChanged={definitionChanged.ToString(IC)} " +
                $"missingFromSave={missingFromSave.ToString(IC)}");
            return restored;
        }

        /// <summary>
        /// Why <see cref="RestoreLoopPositionFromSave"/> would restore nothing for these inputs,
        /// or null when it runs: no-kept-routes, no-save-copy, no-usable-cutoff,
        /// no-loaded-save-clock, save-newer-than-cutoff.
        /// </summary>
        internal static string DescribeRestoreSkip(
            int keptRouteCount, IReadOnlyList<Route> savedRoutes, double cutoffUT, double loadedSaveUT)
        {
            if (keptRouteCount <= 0)
                return "no-kept-routes";
            if (savedRoutes == null)
                return "no-save-copy";
            if (!IsUsableCutoffUT(cutoffUT))
                return "no-usable-cutoff";
            if (!IsUsableCutoffUT(loadedSaveUT))
                return "no-loaded-save-clock";
            if (loadedSaveUT > cutoffUT)
                return "save-newer-than-cutoff";
            return null;
        }

        /// <summary>
        /// The rewind exits' half of the loop-position restore (todo
        /// ROUTE-REWIND-CURSOR-RESET-REFIRES-LATEST-CROSSING). The go-back rewind
        /// (<c>ParsekScenario.HandleRewindOnLoad</c>) and the Re-Fly start
        /// (<c>ReconciliationBundle.Restore(cutoff)</c>) call it right after the shared
        /// <see cref="RouteRewindClassifier.ReconcileStoreAtRewind"/> has installed the kept
        /// routes: it runs <see cref="RestoreLoopPositionFromSave"/> over
        /// <see cref="RouteStore.CommittedRoutes"/> with the loaded save's own route copy, so the
        /// reset does not re-fire the crossing that most recently passed before the cutoff.
        /// <paramref name="savedRoutes"/> null (no copy in hand) leaves the reset standing. Sets
        /// Route fields only, never a ledger row (both exits run inside OnLoad), and the owed
        /// credit it puts back is paid by the next crossing, not here. One log line per call.
        /// </summary>
        internal static int RestoreLoopPositionAtRewindExit(
            IReadOnlyList<Route> savedRoutes, double cutoffUT, double loadedSaveUT,
            string logTag, string logPrefix)
        {
            int keptCount = RouteStore.CommittedRoutes.Count;
            string cutoffText = cutoffUT.ToString("R", IC);
            string saveText = loadedSaveUT.ToString("R", IC);
            string skip = DescribeRestoreSkip(keptCount, savedRoutes, cutoffUT, loadedSaveUT);
            if (skip != null)
            {
                string line = logPrefix + ": loop position not restored from the loaded save: " +
                    $"reason={skip} cutoff={cutoffText} loadedSaveUT={saveText} " +
                    $"keptRoutes={keptCount.ToString(IC)}" +
                    (keptCount > 0 ? "; kept loop cursors stay reset (the next crossing fires)" : string.Empty);
                if (keptCount > 0)
                    ParsekLog.Info(logTag, line);
                else
                    ParsekLog.Verbose(logTag, line);
                return 0;
            }

            int restored = RestoreLoopPositionFromSave(
                RouteStore.CommittedRoutes, savedRoutes, cutoffUT, loadedSaveUT,
                out int creditsRestored, out int definitionChanged, out int missingFromSave,
                logTag);
            ParsekLog.Info(logTag,
                logPrefix + ": loop position restored from the loaded save at cutoff=" + cutoffText +
                $" loadedSaveUT={saveText} keptRoutes={keptCount.ToString(IC)} " +
                $"savedRoutes={savedRoutes.Count.ToString(IC)} loopPositionsRestored={restored.ToString(IC)} " +
                $"creditsRestored={creditsRestored.ToString(IC)} clockChanged={definitionChanged.ToString(IC)} " +
                $"missingFromSave={missingFromSave.ToString(IC)}");
            return restored;
        }

        /// <summary>
        /// The go-back rewind's capture of the loaded save's route copy, called from
        /// <c>RecordingStore.ExecuteRewindSaveLoad</c> right after <c>GamePersistence.LoadGame</c>
        /// parsed the rewind save and before the Space Center load. That OnLoad node is
        /// persistent.sfs as last written (<c>SpaceCenterMain.Start</c> reloads it and
        /// <c>Game.Load</c> hands its scenario protos to OnLoad), so the rewind save's own ROUTES
        /// exist only in <paramref name="scenarios"/>, the parsed game's scenario protos built
        /// from the file. Parses them into detached Route instances and parks them with the
        /// parsed save's clock (<paramref name="loadedClockUT"/>, after the lead-time windback) in
        /// <see cref="RewindContext"/> for <c>HandleRewindOnLoad</c>. A read that throws parks no
        /// copy (the reset then stands) instead of failing the rewind it runs inside.
        /// </summary>
        internal static void CaptureRewindSaveRoutes(
            IList<ProtoScenarioModule> scenarios, double loadedClockUT, string label)
        {
            List<Route> saved;
            try
            {
                saved = RouteStore.ReadSavedCommittedRoutesFromScenarios(scenarios);
            }
            catch (Exception ex)
            {
                saved = null;
                ParsekLog.Warn("Rewind",
                    $"{label}: reading the rewind save's route copy threw {ex.GetType().Name}: {ex.Message}; " +
                    "route loop cursors will stay reset");
            }
            RewindContext.SetRewindSaveRoutes(saved, loadedClockUT);
            ParsekLog.Info("Rewind",
                $"{label}: rewind save's own route copy read for the loop-position restore: " +
                $"routes={(saved == null ? "unknown (no ParsekScenario in the save)" : saved.Count.ToString(IC))} " +
                $"clockUT={loadedClockUT.ToString("R", IC)}");
        }

        /// <summary>
        /// Runs the reconcile for one load. <paramref name="cutoffUT"/> comes from
        /// <see cref="ResolveCutoffUT"/>; <paramref name="loadedSaveUT"/> is the loaded save's own
        /// clock (it gates the loop-position restore); <paramref name="savedRoutes"/> is the loaded
        /// save's committed route copy (<see cref="RouteStore.ReadSavedCommittedRoutes"/>).
        /// Logs one line per call.
        /// </summary>
        internal static RouteLoadReconcileOutcome ReconcileAtInSessionLoad(
            LoadKind kind, double cutoffUT, double loadedSaveUT, IReadOnlyList<Route> savedRoutes)
        {
            string cutoffText = cutoffUT.ToString("R", IC);
            string saveText = loadedSaveUT.ToString("R", IC);

            bool evidenceFound = false;
            string evidence = "not-read";
            if (kind == LoadKind.InSessionOther && IsUsableCutoffUT(cutoffUT))
            {
                evidenceFound = HasRouteStateAfterUT(
                    cutoffUT, RouteStore.CommittedRoutes,
                    Ledger.HasFreeStandingRouteActionsAfterUT(cutoffUT), out evidence);
            }

            RouteLoadReconcileOutcome outcome = Decide(kind, cutoffUT, evidenceFound);
            if (outcome != RouteLoadReconcileOutcome.Reconciled)
            {
                ParsekLog.Verbose(LogTag,
                    $"Route reconcile skipped at in-session load: kind={kind} outcome={outcome} " +
                    $"cutoff={cutoffText} loadedSaveUT={saveText} evidence={evidence} " +
                    $"committedRoutes={RouteStore.CommittedRoutes.Count}");
                return outcome;
            }

            int retiredRouteRows = Ledger.RetireFutureRouteActionsAtRewind(
                cutoffUT, out List<GameAction> keptLedgerActions);
            int dormantBefore = RouteStore.DormantRoutes.Count;
            RouteRewindClassifier.ReconcileStoreAtRewind(
                new List<Route>(RouteStore.CommittedRoutes),
                new List<Route>(RouteStore.DormantRoutes),
                cutoffUT,
                keptLedgerActions,
                logTag: LogTag,
                logPrefix: "OnLoad in-session " + kind);

            int restored = RestoreLoopPositionFromSave(
                RouteStore.CommittedRoutes, savedRoutes, cutoffUT, loadedSaveUT,
                out int creditsRestored, out int definitionChanged, out int missingFromSave);

            ParsekLog.Info(LogTag,
                $"Route reconcile at in-session load: kind={kind} cutoff={cutoffText} " +
                $"loadedSaveUT={saveText} evidence={evidence} retiredRouteRows={retiredRouteRows} " +
                $"committedRoutes={RouteStore.CommittedRoutes.Count} " +
                $"newlyDormant={RouteStore.DormantRoutes.Count - dormantBefore} " +
                $"savedRoutes={savedRoutes?.Count ?? 0} loopPositionsRestored={restored} " +
                $"creditsRestored={creditsRestored} clockChanged={definitionChanged} " +
                $"missingFromSave={missingFromSave}");
            return outcome;
        }

        /// <summary>
        /// The OnLoad entry: reads the loaded save's committed routes from
        /// <paramref name="loadedNode"/> only when the load reconciles, then runs
        /// <see cref="ReconcileAtInSessionLoad(LoadKind, double, double, IReadOnlyList{Route})"/>.
        /// </summary>
        internal static RouteLoadReconcileOutcome ReconcileAtInSessionLoad(
            LoadKind kind, double cutoffUT, double loadedSaveUT, ConfigNode loadedNode)
        {
            List<Route> saved = Decide(kind, cutoffUT, routeStateAfterCutoff: true)
                == RouteLoadReconcileOutcome.Reconciled
                ? RouteStore.ReadSavedCommittedRoutes(loadedNode)
                : null;
            return ReconcileAtInSessionLoad(kind, cutoffUT, loadedSaveUT, saved);
        }
    }
}
