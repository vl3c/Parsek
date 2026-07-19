using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Phase 6 of Rewind-to-Staging (design §6.4 reconciliation table): captures
    /// the in-memory state that must survive a quicksave load, then re-applies
    /// it after KSP has overwritten the global state with the saved .sfs.
    ///
    /// <para>
    /// [ERS-exempt — Phase 6] <see cref="Capture"/> reads
    /// <see cref="RecordingStore.CommittedRecordings"/> directly; it captures a
    /// snapshot of the global list (not a supersede-aware ERS view) because the
    /// whole purpose of the bundle is to preserve the raw list across a load.
    /// The file is allowlisted in <c>scripts/ers-els-audit-allowlist.txt</c>.
    /// </para>
    /// </summary>
    internal struct ReconciliationBundle
    {
        /// <summary>
        /// Shallow snapshot of <see cref="RecordingStore.CommittedRecordings"/>
        /// at capture time. Preserves the list identity; the <see cref="Recording"/>
        /// objects are the same references (not deep clones) so subsequent
        /// mutations outside the bundle are visible post-restore.
        /// </summary>
        public List<Recording> Recordings;

        /// <summary>
        /// Shallow snapshot of <see cref="RecordingStore.CommittedTrees"/> at
        /// capture time. The parallel tree list must round-trip alongside
        /// <see cref="Recordings"/> because ghost playback, EffectiveState, and
        /// every tree-aware consumer reads it directly — without the snapshot,
        /// a restore-after-load would leave trees empty even though the
        /// recordings list was re-installed.
        /// </summary>
        public List<RecordingTree> Trees;

        /// <summary>
        /// Snapshot of <see cref="Ledger.Actions"/>. Preserves ordering.
        /// </summary>
        public List<GameAction> Actions;

        /// <summary>Snapshot of <see cref="ParsekScenario.RewindPoints"/>.</summary>
        public List<RewindPoint> RewindPoints;

        /// <summary>Snapshot of <see cref="ParsekScenario.RecordingSupersedes"/>.</summary>
        public List<RecordingSupersedeRelation> RecordingSupersedes;

        /// <summary>Snapshot of <see cref="ParsekScenario.RecordingRewindRetirements"/>.</summary>
        public List<RecordingRewindRetirement> RecordingRewindRetirements;

        /// <summary>Snapshot of <see cref="ParsekScenario.LedgerTombstones"/>.</summary>
        public List<LedgerTombstone> LedgerTombstones;

        /// <summary>Snapshot of the active session marker (may be null).</summary>
        public ReFlySessionMarker ActiveReFlySessionMarker;

        /// <summary>Snapshot of the active merge journal (may be null).</summary>
        public MergeJournal ActiveMergeJournal;

        /// <summary>Shallow copy of <c>CrewReservationManager.crewReplacements</c>.</summary>
        public Dictionary<string, string> CrewReplacements;

        /// <summary>Shallow copy of <c>GroupHierarchyStore.groupParents</c>.</summary>
        public Dictionary<string, string> GroupParents;

        /// <summary>Shallow copy of <c>GroupHierarchyStore.hiddenGroups</c>.</summary>
        public HashSet<string> HiddenGroups;

        /// <summary>Snapshot of <c>MilestoneStore.Milestones</c>.</summary>
        public List<Milestone> Milestones;

        /// <summary>
        /// Shallow snapshot of <c>RouteStore.CommittedRoutes</c> (dormant-routes
        /// extension). RouteStore is preserved in memory across every in-session
        /// load (LoadRoutesFrom is cold-start-only), so the rewind seam must
        /// move-and-replace the route lists itself: <c>Restore(cutoff)</c>
        /// classifies these via <c>RouteRewindClassifier.Classify</c> - routes
        /// created after the cutoff go dormant, kept routes get their
        /// forward-looking cycle state reconciled.
        /// </summary>
        public List<Logistics.Route> Routes;

        /// <summary>Shallow snapshot of <c>RouteStore.DormantRoutes</c> (still-future
        /// entries from earlier, deeper rewinds; carried forward by the classify).</summary>
        public List<Logistics.Route> DormantRoutes;

        /// <summary>
        /// Captures a <see cref="ReconciliationBundle"/> from the current in-memory
        /// state. Creates shallow copies so later mutations do not leak into the
        /// bundle.
        /// </summary>
        public static ReconciliationBundle Capture()
        {
            var bundle = new ReconciliationBundle
            {
                Recordings = new List<Recording>(),
                Trees = new List<RecordingTree>(),
                Actions = new List<GameAction>(),
                RewindPoints = new List<RewindPoint>(),
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                RecordingRewindRetirements = new List<RecordingRewindRetirement>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                ActiveReFlySessionMarker = null,
                ActiveMergeJournal = null,
                CrewReplacements = new Dictionary<string, string>(),
                GroupParents = new Dictionary<string, string>(),
                HiddenGroups = new HashSet<string>(),
                Milestones = new List<Milestone>(),
                Routes = new List<Logistics.Route>(),
                DormantRoutes = new List<Logistics.Route>(),
            };

            // Routes (dormant-routes extension): both lists, shallow.
            var committedRoutesSnapshot = Logistics.RouteStore.CommittedRoutes;
            if (committedRoutesSnapshot != null)
            {
                for (int i = 0; i < committedRoutesSnapshot.Count; i++)
                    bundle.Routes.Add(committedRoutesSnapshot[i]);
            }
            var dormantRoutesSnapshot = Logistics.RouteStore.DormantRoutes;
            if (dormantRoutesSnapshot != null)
            {
                for (int i = 0; i < dormantRoutesSnapshot.Count; i++)
                    bundle.DormantRoutes.Add(dormantRoutesSnapshot[i]);
            }

            // Recordings: shallow snapshot (references preserved).
            var committed = RecordingStore.CommittedRecordings;
            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                    bundle.Recordings.Add(committed[i]);
            }

            // Trees: shallow snapshot parallel to Recordings.
            var committedTrees = RecordingStore.CommittedTrees;
            if (committedTrees != null)
            {
                for (int i = 0; i < committedTrees.Count; i++)
                    bundle.Trees.Add(committedTrees[i]);
            }

            // Ledger actions snapshot.
            var actions = Ledger.Actions;
            if (actions != null)
            {
                for (int i = 0; i < actions.Count; i++)
                    bundle.Actions.Add(actions[i]);
            }

            // Scenario lists.
            var scenario = ParsekScenario.Instance;
            if (!object.ReferenceEquals(null, scenario))
            {
                if (scenario.RewindPoints != null)
                    bundle.RewindPoints.AddRange(scenario.RewindPoints);
                if (scenario.RecordingSupersedes != null)
                    bundle.RecordingSupersedes.AddRange(scenario.RecordingSupersedes);
                if (scenario.RecordingRewindRetirements != null)
                    bundle.RecordingRewindRetirements.AddRange(scenario.RecordingRewindRetirements);
                if (scenario.LedgerTombstones != null)
                    bundle.LedgerTombstones.AddRange(scenario.LedgerTombstones);
                bundle.ActiveReFlySessionMarker = scenario.ActiveReFlySessionMarker;
                bundle.ActiveMergeJournal = scenario.ActiveMergeJournal;
            }

            // CrewReservationManager.
            bundle.CrewReplacements = CrewReservationManager.SnapshotReplacements();

            // GroupHierarchyStore: shallow copies of the parents dict + hidden
            // set. HideActive is a simple bool preserved implicitly via the
            // store (we do not alter it during an invocation).
            foreach (var kv in GroupHierarchyStore.GroupParents)
                bundle.GroupParents[kv.Key] = kv.Value;
            foreach (var h in GroupHierarchyStore.HiddenGroups)
                bundle.HiddenGroups.Add(h);

            // MilestoneStore.
            var milestones = MilestoneStore.Milestones;
            if (milestones != null)
                bundle.Milestones.AddRange(milestones);

            ParsekLog.Info("ReconciliationBundle",
                $"Captured: recs={bundle.Recordings.Count} trees={bundle.Trees.Count} " +
                $"actions={bundle.Actions.Count} " +
                $"rps={bundle.RewindPoints.Count} supersedes={bundle.RecordingSupersedes.Count} " +
                $"rewindRetirements={bundle.RecordingRewindRetirements.Count} " +
                $"tombstones={bundle.LedgerTombstones.Count} " +
                $"marker={(bundle.ActiveReFlySessionMarker != null)} " +
                $"journal={(bundle.ActiveMergeJournal != null)} " +
                $"crew={bundle.CrewReplacements.Count} groups={bundle.GroupParents.Count} " +
                $"hidden={bundle.HiddenGroups.Count} milestones={bundle.Milestones.Count}");

            return bundle;
        }

        /// <summary>
        /// Re-applies a captured bundle to the now-post-load global state. Replaces
        /// — not merges — each domain so restore-after-load does not produce
        /// duplicates. Idempotent: calling twice leaves the state identical to
        /// a single call.
        /// </summary>
        public static void Restore(ReconciliationBundle bundle)
            => Restore(bundle, double.PositiveInfinity);

        /// <summary>
        /// Restore overload carrying the Rec-1 route-row retire cutoff
        /// (<paramref name="dropRouteRowsAfterUT"/>; logistics &lt;-&gt; time-rewind
        /// determinism, plan <c>docs/dev/plans/fix-logistics-rewind-determinism.md</c>).
        /// The SUCCESS post-load path (<c>RewindInvoker.ConsumePostLoad</c>) passes the
        /// POST-LOAD live UT (= the loaded quicksave UT, the boundary the world reverted
        /// to) so abandoned-future free-standing route rows are dropped to match the
        /// reverted world; the FAILED-load rollback
        /// (<c>TryRestoreBundle</c>) and every test / other caller use the parameterless
        /// overload above (<c>+inf</c> =&gt; retire nothing), so they stay route-blind
        /// and non-route reconstruction is byte-identical.
        /// </summary>
        public static void Restore(ReconciliationBundle bundle, double dropRouteRowsAfterUT)
        {
            // RecordingStore: clear + re-add via the internal helpers. Trees
            // and recordings are parallel lists that MUST round-trip together
            // — clear both, install both — otherwise the ERS/ELS cache sees a
            // tree-less state and drops tree-scoped events on restore.
            RecordingStore.ClearCommittedInternal();
            RecordingStore.ClearCommittedTreesInternal();
            if (bundle.Recordings != null)
            {
                for (int i = 0; i < bundle.Recordings.Count; i++)
                    RecordingStore.AddCommittedInternal(bundle.Recordings[i]);
            }
            if (bundle.Trees != null)
            {
                for (int i = 0; i < bundle.Trees.Count; i++)
                    RecordingStore.AddCommittedTreeInternal(bundle.Trees[i]);
            }

            // Ledger actions. Rec-1: DROP free-standing route rows whose UT > the
            // rewind cutoff so the preserved ledger matches the reverted world — the
            // live re-fly then re-emits each cycle, re-charging funds AND re-delivering
            // cargo exactly once. The parameterless overload passes +inf, so the
            // failed-load rollback + test callers retire nothing and non-route ledger
            // reconstruction stays byte-identical. (This restores parity with the
            // stock-revert path, which already prunes these rows via
            // Ledger.PruneOrphanActionsAfterUT.)
            Ledger.Clear();
            // Kept (post-retire) rows, reused below by the route seam's
            // status-derivation + counter-reconstruction pass over kept routes.
            List<GameAction> keptActions = null;
            if (bundle.Actions != null && bundle.Actions.Count > 0)
            {
                keptActions = Logistics.RouteLedgerRetire.RetireFutureRouteActions(
                    bundle.Actions, dropRouteRowsAfterUT, out int routeRowsRetired);
                if (keptActions.Count > 0)
                    Ledger.AddActions(keptActions);
                if (routeRowsRetired > 0)
                    ParsekLog.Info("ReconciliationBundle",
                        "Restore: retired " + routeRowsRetired.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                        " free-standing route row(s) with UT > cutoff " +
                        dropRouteRowsAfterUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " (Rec-1)");
            }

            // Routes (dormant-routes extension). RouteStore is preserved in
            // memory across in-session loads, so the rewind seam replaces both
            // route lists here: captured committed routes created AFTER the
            // cutoff move to dormant (invisible, non-firing, re-materialized by
            // RouteStore.MaterializeDueDormantRoutes when the re-flown timeline
            // reaches their CreatedUT); kept routes get their forward-looking
            // cycle state reconciled so the loop clock does not swallow re-flown
            // cycles against abandoned-future cursors. The parameterless
            // rollback overload (+inf) stays route-blind: no classification, no
            // reconcile, lists untouched - matching the Rec-1 contract.
            if (!double.IsPositiveInfinity(dropRouteRowsAfterUT))
            {
                Logistics.RouteRewindClassifier.Classify(
                    bundle.Routes,
                    bundle.DormantRoutes,
                    dropRouteRowsAfterUT,
                    out List<Logistics.Route> keptRoutes,
                    out List<Logistics.Route> dormantRoutes);

                int newlyDormant = dormantRoutes.Count - (bundle.DormantRoutes?.Count ?? 0);
                int reconciled = 0;

                // Dormanting hygiene: drop escrow + sever the committed
                // partner's back-reference (RemoveRoute-style); the dormant
                // entry keeps its LinkedRouteId as a former-partner hint for
                // the materialize-time re-link.
                for (int i = 0; i < dormantRoutes.Count; i++)
                {
                    Logistics.Route d = dormantRoutes[i];
                    Logistics.RouteStore.DropRouteEscrow(d.Id);
                    for (int j = 0; j < keptRoutes.Count; j++)
                    {
                        Logistics.Route kept = keptRoutes[j];
                        if (kept != null && string.Equals(kept.LinkedRouteId, d.Id, StringComparison.Ordinal))
                        {
                            kept.LinkedRouteId = null;
                            kept.LastConsumedPartnerCycle = 0;
                        }
                    }
                }

                // Pre-cutoff kept routes: reconcile abandoned-future cycle
                // state, then derive the timeline-correct pause state from the
                // kept RoutePaused/RouteResumed rows, drop armed one-shot
                // flags (they cannot be timestamped, so they never survive
                // time travel), and reconstruct the cycle counters from the
                // kept dispatch/delivery rows.
                IReadOnlyList<GameAction> keptRows =
                    (IReadOnlyList<GameAction>)keptActions ?? Array.Empty<GameAction>();
                int derivedPaused = 0;
                int derivedActive = 0;
                int oneShotFlagsCleared = 0;
                int countersReconstructed = 0;
                for (int i = 0; i < keptRoutes.Count; i++)
                {
                    Logistics.Route kept = keptRoutes[i];
                    if (Logistics.RouteRewindClassifier.ResetCycleStateForRewind(
                            kept, dropRouteRowsAfterUT))
                        reconciled++;
                    Logistics.RouteTimelineStatus derived =
                        Logistics.RouteRewindClassifier.DeriveTimelineStatus(kept.Id, keptRows);
                    if (Logistics.RouteRewindClassifier.ApplyDerivedTimelineStatus(kept, derived))
                    {
                        if (derived == Logistics.RouteTimelineStatus.Paused)
                            derivedPaused++;
                        else
                            derivedActive++;
                    }
                    if (Logistics.RouteRewindClassifier.ClearArmedOneShotFlags(kept))
                        oneShotFlagsCleared++;
                    if (Logistics.RouteRewindClassifier.ReconstructCycleCounters(kept, keptRows))
                        countersReconstructed++;
                }

                Logistics.RouteStore.InstallRoutesAtRewind(keptRoutes, dormantRoutes);
                if (newlyDormant > 0 || reconciled > 0)
                {
                    ParsekLog.Info("ReconciliationBundle",
                        "Restore: routes at rewind cutoff " +
                        dropRouteRowsAfterUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                        $": kept={keptRoutes.Count} (reconciled={reconciled}) " +
                        $"dormant={dormantRoutes.Count} (newly={newlyDormant})");
                }
                if (keptRoutes.Count > 0)
                {
                    ParsekLog.Info("ReconciliationBundle",
                        "Restore: kept-route status fidelity at cutoff " +
                        dropRouteRowsAfterUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                        $": derivedPaused={derivedPaused} derivedActive={derivedActive} " +
                        $"oneShotFlagsCleared={oneShotFlagsCleared} " +
                        $"countersReconstructed={countersReconstructed}");
                }
            }

            // Scenario lists (may be null if the scenario hasn't loaded yet).
            var scenario = ParsekScenario.Instance;
            if (!object.ReferenceEquals(null, scenario))
            {
                scenario.RewindPoints = bundle.RewindPoints != null
                    ? new List<RewindPoint>(bundle.RewindPoints)
                    : new List<RewindPoint>();

                scenario.RecordingSupersedes = bundle.RecordingSupersedes != null
                    ? new List<RecordingSupersedeRelation>(bundle.RecordingSupersedes)
                    : new List<RecordingSupersedeRelation>();
                scenario.RecordingRewindRetirements = bundle.RecordingRewindRetirements != null
                    ? new List<RecordingRewindRetirement>(bundle.RecordingRewindRetirements)
                    : new List<RecordingRewindRetirement>();
                scenario.BumpSupersedeStateVersion();

                scenario.LedgerTombstones = bundle.LedgerTombstones != null
                    ? new List<LedgerTombstone>(bundle.LedgerTombstones)
                    : new List<LedgerTombstone>();
                scenario.BumpTombstoneStateVersion();

                scenario.ActiveReFlySessionMarker = bundle.ActiveReFlySessionMarker;
                scenario.ActiveMergeJournal = bundle.ActiveMergeJournal;
                // Re-fly Esc-menu button gate: this Restore can change the
                // marker in either direction (success path before
                // AtomicMarkerWrite — Apply re-fires there harmlessly;
                // rollback path on a failed LoadGame — only reachable here).
                ReFlyRevertButtonGate.Apply("ReconciliationBundle:Restore");
            }

            // CrewReservationManager.
            CrewReservationManager.RestoreReplacements(bundle.CrewReplacements);

            // GroupHierarchyStore: replace contents in place.
            GroupHierarchyStore.groupParents.Clear();
            if (bundle.GroupParents != null)
            {
                foreach (var kv in bundle.GroupParents)
                    GroupHierarchyStore.groupParents[kv.Key] = kv.Value;
            }
            GroupHierarchyStore.hiddenGroups.Clear();
            if (bundle.HiddenGroups != null)
            {
                foreach (var h in bundle.HiddenGroups)
                    GroupHierarchyStore.hiddenGroups.Add(h);
            }

            // MilestoneStore: clear + re-add.
            MilestoneStore.ClearAll();
            if (bundle.Milestones != null)
            {
                for (int i = 0; i < bundle.Milestones.Count; i++)
                {
                    MilestoneStore.RestoreMilestone(bundle.Milestones[i]);
                }
            }

            ParsekLog.Info("ReconciliationBundle",
                $"Restored: recs={(bundle.Recordings?.Count ?? 0)} trees={(bundle.Trees?.Count ?? 0)} " +
                $"actions={(bundle.Actions?.Count ?? 0)} " +
                $"rps={(bundle.RewindPoints?.Count ?? 0)} supersedes={(bundle.RecordingSupersedes?.Count ?? 0)} " +
                $"rewindRetirements={(bundle.RecordingRewindRetirements?.Count ?? 0)} " +
                $"tombstones={(bundle.LedgerTombstones?.Count ?? 0)} " +
                $"marker={(bundle.ActiveReFlySessionMarker != null)} " +
                $"journal={(bundle.ActiveMergeJournal != null)} " +
                $"crew={(bundle.CrewReplacements?.Count ?? 0)} groups={(bundle.GroupParents?.Count ?? 0)} " +
                $"hidden={(bundle.HiddenGroups?.Count ?? 0)} milestones={(bundle.Milestones?.Count ?? 0)}");
        }
    }
}
