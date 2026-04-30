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
                LedgerTombstones = new List<LedgerTombstone>(),
                ActiveReFlySessionMarker = null,
                ActiveMergeJournal = null,
                CrewReplacements = new Dictionary<string, string>(),
                GroupParents = new Dictionary<string, string>(),
                HiddenGroups = new HashSet<string>(),
                Milestones = new List<Milestone>(),
            };

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

            // Ledger actions.
            Ledger.Clear();
            if (bundle.Actions != null && bundle.Actions.Count > 0)
                Ledger.AddActions(bundle.Actions);

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
                $"tombstones={(bundle.LedgerTombstones?.Count ?? 0)} " +
                $"marker={(bundle.ActiveReFlySessionMarker != null)} " +
                $"journal={(bundle.ActiveMergeJournal != null)} " +
                $"crew={(bundle.CrewReplacements?.Count ?? 0)} groups={(bundle.GroupParents?.Count ?? 0)} " +
                $"hidden={(bundle.HiddenGroups?.Count ?? 0)} milestones={(bundle.Milestones?.Count ?? 0)}");
        }
    }
}
