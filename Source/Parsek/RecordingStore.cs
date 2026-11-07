using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// State of the pending tree slot in <see cref="RecordingStore"/>.
    /// Determines how <see cref="ParsekScenario.OnLoad"/> dispatches the pending tree
    /// after revert detection runs.
    /// </summary>
    public enum PendingTreeState
    {
        /// <summary>
        /// Tree has been through <c>FinalizeTreeRecordings</c>: terminal state set,
        /// terminal orbit / position captured, snapshots preserved for the merge dialog.
        /// Default behavior — this is what <see cref="PendingTreeSlot.StashPendingTree"/>
        /// produced before the quickload-resume redesign.
        /// </summary>
        Finalized = 0,

        /// <summary>
        /// Tree is still "in-flight": recorder field references were torn down because
        /// the scene is unloading, but no terminal state was set. OnLoad decides based
        /// on revert detection whether to finalize-then-commit (true revert) or
        /// restore-and-resume (quickload / vessel switch).
        /// See <c>docs/dev/plans/quickload-resume-recording.md</c>.
        /// </summary>
        Limbo = 1,

        /// <summary>
        /// Tree was pre-transitioned for a vessel switch that triggers a FLIGHT→FLIGHT
        /// scene reload (e.g. clicking an unloaded vessel in the tracking station).
        /// At stash time the recorder was flushed, the previous active recording's
        /// vessel PID was inserted into <c>BackgroundMap</c>, and
        /// <c>ActiveRecordingId</c> was nulled — exactly the state the in-session
        /// <c>OnVesselSwitchComplete</c> path produces. The OnLoad dispatch routes
        /// this state through the dedicated vessel-switch restore coroutine, which
        /// reinstalls the tree and either promotes the new active vessel from
        /// <c>BackgroundMap</c> (round-trip) or leaves the recorder null (outsider).
        /// See bug #266 in <c>docs/dev/todo-and-known-bugs.md</c>.
        /// </summary>
        LimboVesselSwitch = 2,
    }

    /// <summary>
    /// Static holder for recording data that survives scene changes.
    /// Static fields persist across scene loads within a KSP session.
    /// Save/load persistence is handled separately by ParsekScenario.
    /// </summary>
    public static partial class RecordingStore
    {
        // The schema constants themselves live on RecordingSchema so the Recording data
        // type can stamp itself without referencing this store; these two forward them
        // under the names every existing caller and the docs use.
        public const int CurrentRecordingFormatVersion = RecordingSchema.CurrentRecordingFormatVersion;
        public const int CurrentRecordingSchemaGeneration = RecordingSchema.CurrentRecordingSchemaGeneration;

        // Recording.GhostVisible consults a rule its owner installs; the rule reads only the
        // committed trees, so installing it when this store initialises guarantees it is in
        // place before any committed recording can be read.
        private static readonly bool ghostHiddenRuleInstalled =
            LaunchSiteOnlyGhost.EnsureGhostHiddenRuleInstalled();

        internal static bool GhostHiddenRuleInstalledForTesting => ghostHiddenRuleInstalled;

        /// <summary>
        /// Amount of pre-launch setup time rewind-to-launch restores before the launch UT.
        /// </summary>
        internal const double RewindToLaunchLeadTimeSeconds = 15.0;

        internal static bool IsRecordingSchemaCompatible(
            int recordingFormatVersion,
            int recordingSchemaGeneration,
            out string reason)
        {
            return IsRecordingSchemaCompatible(
                recordingFormatVersion, recordingSchemaGeneration, out reason, out _);
        }

        /// <summary>
        /// The recording schema gate (<see cref="SaveFormat"/> rows recording-schema-generation and
        /// recording-format). Current stamps pass; an Upgradable stamp (at or after the 1.0
        /// baseline, below current) passes only when its upgrade steps are registered, and then
        /// <paramref name="needsUpgrade"/> is true and the reader runs them; everything else is
        /// rejected with <c>generation-missing</c> / <c>generation-older</c> (pre-baseline, or no
        /// upgrade path) / <c>generation-newer</c> / <c>format-version-mismatch</c>.
        /// </summary>
        internal static bool IsRecordingSchemaCompatible(
            int recordingFormatVersion,
            int recordingSchemaGeneration,
            out string reason,
            out bool needsUpgrade)
        {
            reason = SaveFormat.GateReason(
                SaveFormat.RecordingSchemaGeneration, recordingSchemaGeneration, zeroIsMissing: true,
                missingReason: "generation-missing", olderReason: "generation-older",
                newerReason: "generation-newer", needsUpgrade: out bool generationUpgrade);
            if (reason != null)
            {
                needsUpgrade = false;
                return false;
            }

            reason = SaveFormat.GateReason(
                SaveFormat.RecordingFormat, recordingFormatVersion, zeroIsMissing: false,
                missingReason: "format-version-mismatch", olderReason: "format-version-mismatch",
                newerReason: "format-version-mismatch", needsUpgrade: out bool formatUpgrade);
            needsUpgrade = reason == null && (generationUpgrade || formatUpgrade);
            return reason == null;
        }

        // When true, suppresses logging calls (for unit testing outside Unity)
        internal static bool SuppressLogging;

        // Rewind state is now encapsulated in RewindContext.
        // These delegate properties preserve API compatibility for callers
        // that read IsRewinding/RewindUT/etc. through RecordingStore.
        internal static bool IsRewinding => RewindContext.IsRewinding;
        internal static double RewindUT => RewindContext.RewindUT;
        internal static double RewindAdjustedUT => RewindContext.RewindAdjustedUT;
        internal static BudgetSummary RewindReserved => RewindContext.RewindReserved;
        internal static double RewindBaselineFunds => RewindContext.RewindBaselineFunds;
        internal static double RewindBaselineScience => RewindContext.RewindBaselineScience;
        internal static float RewindBaselineRep => RewindContext.RewindBaselineRep;

        // True while the deferred UT adjustment coroutine hasn't run yet.
        // KSC spawn must not use Planetarium.GetUniversalTime() while this is set —
        // it still reflects the pre-rewind future UT until the coroutine fires.
        internal static bool RewindUTAdjustmentPending;

        /// <summary>
        /// The UT the pending rewind adjustment will set (captured with
        /// <see cref="RewindUTAdjustmentPending"/>, because <c>RewindContext.EndRewind</c>
        /// clears <c>RewindAdjustedUT</c> before the deferred coroutine runs). Read by the
        /// kerbal-reservation walk clock only while the flag is set.
        /// </summary>
        internal static double RewindUTAdjustmentTargetUT = double.NaN;

        private const string LegacyPrefix = "[Parsek] ";

        internal static void Log(string message)
        {
            if (SuppressLogging) return;

            string clean = message ?? "(empty)";
            if (clean.StartsWith(LegacyPrefix, StringComparison.Ordinal))
                clean = clean.Substring(LegacyPrefix.Length);

            if (clean.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase) ||
                clean.StartsWith("WARN:", StringComparison.OrdinalIgnoreCase))
            {
                int idx = clean.IndexOf(':');
                string trimmed = idx >= 0 ? clean.Substring(idx + 1).TrimStart() : clean;
                ParsekLog.Warn("RecordingStore", trimmed);
                return;
            }

            ParsekLog.Info("RecordingStore", clean);
        }


        // Merged to timeline — these auto-playback during flight.
        //
        // POLICY: the player can never delete a committed recording, singly or wholesale.
        // Recordings can only be committed (at merge dialog) or discarded (before commit);
        // a deletion would break the timeline and the ledger (orphaned vessels, broken
        // chains, dangling anchors, inconsistent ghost playback). The per-row Archive
        // (Hidden) checkbox is the only way to stop seeing one. Internal removals (optimizer
        // merge, Re-Fly rollback, load-time sweeps, bundle restore) are not player deletions.
        private static List<Recording> committedRecordings = new List<Recording>();

        // Committed recording trees (parallel storage — tree recordings also appear in committedRecordings)
        private static List<RecordingTree> committedTrees = new List<RecordingTree>();

        public static IReadOnlyList<Recording> CommittedRecordings => committedRecordings;
        public static List<RecordingTree> CommittedTrees => committedTrees;
        // Forwarders to PendingTreeSlot (RecordingStore services plan, PR10): 35 / 25 / 8
        // production files read these three, and LoadReconcileWiringGateTests pins the
        // ParsekScenario text `RecordingStore.PendingTree`.
        public static bool HasPendingTree => PendingTreeSlot.HasPendingTree;
        public static RecordingTree PendingTree => PendingTreeSlot.PendingTree;
        public static PendingTreeState PendingTreeStateValue => PendingTreeSlot.PendingTreeStateValue;
        internal static bool HasCommittedTreeRestoreAttempt =>
            CommittedTreeRestoreAttempt.HasCommittedTreeRestoreAttempt;

        // Forwarders to CommittedTreeRestoreAttempt (RecordingStore services plan, PR9). The
        // service purges game-state events, so it reaches GameStateStore; a caller that the
        // RecordingStore cut does not remove (GameStateStore, MergeDialog, RewindInvoker,
        // SceneExitInterceptor, RouteRunPrompt, the map-focus patch) would close a cycle
        // through it around that cut, so those callers and ParsekScenario stay on these.
        internal static void ArmCommittedTreeRestoreAttempt(RecordingTree tree, string reason)
        {
            CommittedTreeRestoreAttempt.ArmCommittedTreeRestoreAttempt(tree, reason);
        }

        internal static bool IsCommittedTreeRestoreAttemptTree(string treeId)
        {
            return CommittedTreeRestoreAttempt.IsCommittedTreeRestoreAttemptTree(treeId);
        }

        internal static bool IsCommittedTreeRestoreAttemptRecordingId(string recordingId)
        {
            return CommittedTreeRestoreAttempt.IsCommittedTreeRestoreAttemptRecordingId(recordingId);
        }

        internal static bool ShouldSuppressCommittedTreeRestoreAttemptEventPersistence(
            GameStateEvent evt)
        {
            return CommittedTreeRestoreAttempt.ShouldSuppressCommittedTreeRestoreAttemptEventPersistence(evt);
        }

        internal static void ClearCommittedTreeRestoreAttempt(string reason)
        {
            CommittedTreeRestoreAttempt.ClearCommittedTreeRestoreAttempt(reason);
        }

        internal static void DiscardCommittedTreeRestoreAttempt(
            string reason, bool preserveIrreversibleTails)
        {
            CommittedTreeRestoreAttempt.DiscardCommittedTreeRestoreAttempt(
                reason, preserveIrreversibleTails);
        }

        internal static bool ClearCommittedTreeRestoreAttemptForDroppedTree(
            string droppedTreeId, string reason, bool preserveIrreversibleTails)
        {
            return CommittedTreeRestoreAttempt.ClearCommittedTreeRestoreAttemptForDroppedTree(
                droppedTreeId, reason, preserveIrreversibleTails);
        }

        /// <summary>
        /// Slot identifier returned by <see cref="TryResolveTreeById"/> so
        /// callers know whether the resolved tree came from the pending, active
        /// (live activeTree on ParsekFlight), or committed slot. The slots are
        /// walked in priority order: a live in-FLIGHT clone-restore wrapper
        /// sits in the active slot and must beat the original committed copy.
        /// </summary>
        internal enum TreeSlotSource
        {
            None = 0,
            Pending = 1,
            Active = 2,
            Committed = 3,
        }

        /// <summary>
        /// M3 (PR #876 round-5 review): shared tree lookup used by both
        /// <see cref="SceneExitInterceptor.TryResolveSessionTreeForDialog"/>
        /// and <see cref="MergeDialog.ShowPreSwitchDecisionDialog"/>. The two
        /// callers used to walk the same slots with diverging logic — the
        /// pre-switch dialog stopped after pending+active and ignored
        /// CommittedTrees, while SceneExit walked all three. This helper is
        /// the canonical resolver.
        ///
        /// <para>Priority order: Pending (sealed-but-not-yet-committed) →
        /// Active (live activeTree on <see cref="ParsekFlight"/>, including
        /// in-FLIGHT clone-restore wrappers) → Committed (terminal storage).
        /// The active slot wins over committed when a clone-restore is mid-
        /// flight, so the segment-bearing clone is dialog-ed instead of the
        /// original committed tree. Invariant: no live
        /// <see cref="SwitchSegmentSession"/> should ever share its TreeId
        /// with a non-clone committed tree.</para>
        /// </summary>
        internal static bool TryResolveTreeById(
            string treeId,
            out RecordingTree tree,
            out TreeSlotSource sourceSlot)
        {
            tree = null;
            sourceSlot = TreeSlotSource.None;
            if (string.IsNullOrEmpty(treeId))
                return false;

            if (PendingTreeSlot.PendingTree != null
                && string.Equals(PendingTreeSlot.PendingTree.Id, treeId, System.StringComparison.Ordinal))
            {
                tree = PendingTreeSlot.PendingTree;
                sourceSlot = TreeSlotSource.Pending;
                return true;
            }

            var flight = ParsekFlight.Instance;
            if (flight != null
                && flight.ActiveTreeForSerialization != null
                && string.Equals(
                    flight.ActiveTreeForSerialization.Id,
                    treeId,
                    System.StringComparison.Ordinal))
            {
                tree = flight.ActiveTreeForSerialization;
                sourceSlot = TreeSlotSource.Active;
                return true;
            }

            if (committedTrees != null)
            {
                for (int i = 0; i < committedTrees.Count; i++)
                {
                    var t = committedTrees[i];
                    if (t != null
                        && string.Equals(t.Id, treeId, System.StringComparison.Ordinal))
                    {
                        tree = t;
                        sourceSlot = TreeSlotSource.Committed;
                        return true;
                    }
                }
            }

            return false;
        }

        // Phase 2 (Rewind-to-Staging): state-version counter consumed by
        // <see cref="EffectiveState"/> to invalidate the ERS cache. Every code
        // path that mutates <see cref="committedRecordings"/> MUST call
        // <see cref="BumpStateVersion"/>; the mutating internal helpers below
        // already do so, so callers that route through them get the bump for
        // free.
        internal static int StateVersion;

        /// <summary>
        /// Bumps <see cref="StateVersion"/>. Called whenever
        /// <see cref="committedRecordings"/> is mutated so the
        /// <see cref="EffectiveState"/> ERS cache knows to rebuild.
        /// </summary>
        internal static void BumpStateVersion()
        {
            unchecked { StateVersion++; }
        }

        /// <summary>
        /// Creates a Recording from raw flight data, trimming leading stationary points
        /// and retiming events. Returns null if too short (fewer than 2 points after trim).
        /// This is the factory replacement for StashPending — it builds the Recording
        /// without storing it in any pending slot.
        /// </summary>
        public static Recording CreateRecordingFromFlightData(
            List<TrajectoryPoint> points, string vesselName,
            List<OrbitSegment> orbitSegments = null,
            string recordingId = null,
            int? recordingFormatVersion = null,
            List<PartEvent> partEvents = null,
            List<FlagEvent> flagEvents = null,
            List<SegmentEvent> segmentEvents = null,
            List<TrackSection> trackSections = null)
        {
            if (points == null || points.Count < 2)
            {
                Log($"[Parsek] Recording too short for '{vesselName}' ({points?.Count ?? 0} points, need >= 2) — discarded");
                return null;
            }

            // Trim leading stationary points (vessel sitting on pad/runway before launch).
            // This prevents the ghost from overlapping the real vessel at the start position.
            // Also trims any orbit segments and part events that fall before the new start.
            int firstMoving = TrajectoryMath.FindFirstMovingPoint(points);
            if (firstMoving > 0)
            {
                double trimUT = points[firstMoving].ut;
                Log($"[Parsek] Trimmed {firstMoving} leading stationary points for '{vesselName}' " +
                    $"(alt delta < 1m, speed < 5 m/s, new startUT={trimUT:F1})");
                points = points.GetRange(firstMoving, points.Count - firstMoving);
                if (points.Count < 2)
                {
                    Log($"[Parsek] Recording too short after trimming for '{vesselName}' ({points.Count} points) — discarded");
                    return null;
                }
                // Remove orbit segments that end before the new start
                if (orbitSegments != null)
                    orbitSegments.RemoveAll(s => s.endUT <= trimUT);
                // Retime part events from the trimmed window to the new start
                if (partEvents != null)
                {
                    for (int i = 0; i < partEvents.Count; i++)
                    {
                        if (partEvents[i].ut < trimUT)
                        {
                            var e = partEvents[i];
                            e.ut = trimUT;
                            partEvents[i] = e;
                        }
                    }
                }
                // Retime flag events the same way
                if (flagEvents != null)
                {
                    for (int i = 0; i < flagEvents.Count; i++)
                    {
                        if (flagEvents[i].ut < trimUT)
                        {
                            var e = flagEvents[i];
                            e.ut = trimUT;
                            flagEvents[i] = e;
                        }
                    }
                }
            }

            var rec = new Recording
            {
                RecordingId = string.IsNullOrEmpty(recordingId) ? Guid.NewGuid().ToString("N") : recordingId,
                RecordingFormatVersion = recordingFormatVersion ?? CurrentRecordingFormatVersion,
                Points = new List<TrajectoryPoint>(points),
                OrbitSegments = orbitSegments != null
                    ? new List<OrbitSegment>(orbitSegments)
                    : new List<OrbitSegment>(),
                PartEvents = partEvents != null
                    ? new List<PartEvent>(partEvents)
                    : new List<PartEvent>(),
                FlagEvents = flagEvents != null
                    ? new List<FlagEvent>(flagEvents)
                    : new List<FlagEvent>(),
                SegmentEvents = segmentEvents != null
                    ? new List<SegmentEvent>(segmentEvents)
                    : new List<SegmentEvent>(),
                TrackSections = trackSections != null
                    ? Recording.DeepCopyTrackSections(trackSections)
                    : new List<TrackSection>(),
                VesselName = vesselName
            };

            Log($"[Parsek] Created recording: {points.Count} points, " +
                $"{rec.OrbitSegments.Count} orbit segments from {vesselName}");
            ParsekLog.Verbose("RecordingStore", $"CreateRecordingFromFlightData: {rec.DebugName}");
            return rec;
        }

        /// <summary>
        /// Adds a recording to the internal committed list without flushing or side effects.
        /// For production code paths (e.g., undock continuation) that need direct list access
        /// after CommittedRecordings became IReadOnlyList.
        /// </summary>
        internal static void AddCommittedInternal(Recording rec)
        {
            committedRecordings.Add(rec);
            BumpStateVersion();
        }

        /// <summary>
        /// Inserts <paramref name="toInsert"/> into the committed list at the
        /// slot immediately after the first recording whose
        /// <see cref="Recording.RecordingId"/> matches
        /// <paramref name="afterRecordingId"/>. If no match is found,
        /// <paramref name="toInsert"/> is appended at the end. Used by
        /// <see cref="RecordingTreeSplitter"/> to insert TIP right after HEAD
        /// (the optimizer split inserts its second half the same way, through
        /// <see cref="InsertCommittedAt"/>). Bumps <see cref="StateVersion"/>.
        /// </summary>
        internal static void InsertCommittedAfter(string afterRecordingId, Recording toInsert)
        {
            if (toInsert == null) return;
            int insertAt = committedRecordings.Count;
            if (!string.IsNullOrEmpty(afterRecordingId))
            {
                for (int i = 0; i < committedRecordings.Count; i++)
                {
                    if (string.Equals(
                            committedRecordings[i].RecordingId,
                            afterRecordingId,
                            StringComparison.Ordinal))
                    {
                        insertAt = i + 1;
                        break;
                    }
                }
            }
            InsertCommittedAt(insertAt, toInsert);
        }

        /// <summary>
        /// Removes the first committed recording whose
        /// <see cref="Recording.RecordingId"/> equals
        /// <paramref name="recordingId"/>. Returns true when a row was
        /// removed. Used by <see cref="RecordingTreeSplitter.RollBackInMemory"/>
        /// during snapshot rollback when only the id is known (the TIP
        /// reference may have been mutated in earlier ledger steps).
        /// Bumps <see cref="StateVersion"/> on success.
        /// </summary>
        internal static bool RemoveCommittedById(string recordingId)
        {
            int index = IndexOfRecordingId(committedRecordings, recordingId);
            if (index < 0) return false;
            RemoveCommittedAtWithNotifications(index);
            return true;
        }

        /// <summary>
        /// Index of the recording whose <see cref="Recording.RecordingId"/> equals
        /// <paramref name="recordingId"/> in <paramref name="recordings"/>, or -1.
        /// </summary>
        internal static int IndexOfRecordingId(IReadOnlyList<Recording> recordings, string recordingId)
        {
            if (recordings == null || string.IsNullOrEmpty(recordingId)) return -1;
            for (int i = 0; i < recordings.Count; i++)
            {
                if (recordings[i] != null
                    && string.Equals(recordings[i].RecordingId, recordingId, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        // The removal wrapper every by-object / by-id / by-chain remover in the store routes
        // through, so the Removing / Removed notifications and the StateVersion bump
        // cannot be forgotten by the next helper. Files and chain degradation stay with
        // the callers that want them (TreeDiscardService.RemoveRecordingAt, which calls
        // RemoveCommittedAt itself).
        private static void RemoveCommittedAtWithNotifications(int index)
        {
            RemoveCommittedAt(index, null);
        }

        /// <summary>
        /// The committed-list removal primitive: raises
        /// <see cref="CommittedRecordingRemoving"/> with the list unshifted, removes the
        /// recording at <paramref name="index"/>, bumps <see cref="StateVersion"/>, then raises
        /// <see cref="CommittedRecordingRemoved"/> naming <paramref name="absorbedInto"/> (the
        /// optimizer merge target that now carries the removed trajectory, or null for a plain
        /// delete). Every removal from the committed list goes through here.
        /// </summary>
        internal static void RemoveCommittedAt(int index, Recording absorbedInto)
        {
            var rec = committedRecordings[index];
            NotifyCommittedRecordingRemoving(index, rec);
            committedRecordings.RemoveAt(index);
            BumpStateVersion();
            NotifyCommittedRecordingRemoved(index, rec, absorbedInto);
        }

        /// <summary>
        /// The committed-list insertion primitive: inserts <paramref name="toInsert"/> at
        /// <paramref name="index"/>, bumps <see cref="StateVersion"/>, then raises
        /// <see cref="CommittedRecordingInserted"/>. Every mid-list insertion goes through here.
        /// </summary>
        internal static void InsertCommittedAt(int index, Recording toInsert)
        {
            committedRecordings.Insert(index, toInsert);
            BumpStateVersion();
            NotifyCommittedRecordingInserted(index);
        }

        /// <summary>
        /// The committed-list in-place replacement primitive: puts <paramref name="replacement"/>
        /// at <paramref name="index"/>. Index-stable (no shift, so no Removing / Removed /
        /// Inserted notification); it does not bump <see cref="StateVersion"/>: the caller
        /// (<see cref="TreeCommitService"/>'s tree commit) bumps once after its batch.
        /// </summary>
        internal static void ReplaceCommittedAt(int index, Recording replacement)
        {
            committedRecordings[index] = replacement;
        }

        /// <summary>
        /// Appends <paramref name="rec"/> to the committed list without bumping
        /// <see cref="StateVersion"/>: an append shifts no index, and the tree commit that
        /// calls it bumps once after its batch (<see cref="AddCommittedInternal"/> is the
        /// single-append form that bumps).
        /// </summary>
        internal static void AppendCommittedUnbumped(Recording rec)
        {
            committedRecordings.Add(rec);
        }

        /// <summary>
        /// Puts <paramref name="tree"/> at <paramref name="index"/> of the committed tree list
        /// (index-stable); the caller bumps <see cref="StateVersion"/>.
        /// </summary>
        internal static void ReplaceCommittedTreeAt(int index, RecordingTree tree)
        {
            committedTrees[index] = tree;
        }

        /// <summary>
        /// Appends <paramref name="tree"/> to the committed tree list without bumping
        /// <see cref="StateVersion"/> (<see cref="AddCommittedTreeInternal"/> is the form that
        /// bumps); the tree commit that calls it has bumped for the tree's recordings.
        /// </summary>
        internal static void AppendCommittedTreeUnbumped(RecordingTree tree)
        {
            committedTrees.Add(tree);
        }

        internal static void AutoGroupTreeRecordings(RecordingTree tree)
        {
            RecordingGroupStore.AutoGroupTreeRecordings(tree, committedRecordings);
        }

        internal static void AdoptOrphanedRecordingsIntoTreeGroup(RecordingTree tree)
        {
            RecordingGroupStore.AdoptOrphanedRecordingsIntoTreeGroup(tree, committedRecordings);
        }

        /// <summary>
        /// Forwarder to <see cref="CommittedListOptimizer.RunOptimizationPass"/>, kept while
        /// its call sites move (RecordingStore services plan, PR8).
        /// </summary>
        internal static void RunOptimizationPass()
        {
            CommittedListOptimizer.RunOptimizationPass();
        }

        /// <summary>
        /// Replaces the first committed recording whose
        /// <see cref="Recording.RecordingId"/> equals
        /// <paramref name="replacement"/>.<see cref="Recording.RecordingId"/>
        /// with the supplied reference. No-op when the id is not found.
        /// Used by <see cref="RecordingTreeSplitter.RollBackInMemory"/> to
        /// swap origin's reference back to a pre-split <see cref="Recording.DeepClone"/>
        /// after <see cref="RecordingOptimizer.SplitAtUT"/> trimmed the
        /// in-place mutable. Bumps <see cref="StateVersion"/> on success.
        /// </summary>
        internal static bool ReplaceCommittedReference(Recording replacement)
        {
            if (replacement == null || string.IsNullOrEmpty(replacement.RecordingId)) return false;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                if (string.Equals(
                        committedRecordings[i].RecordingId,
                        replacement.RecordingId,
                        StringComparison.Ordinal))
                {
                    committedRecordings[i] = replacement;
                    BumpStateVersion();
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Phase 6 of Rewind-to-Staging (design §6.3 step 4 phase 1): adds a
        /// provisional re-fly recording to the committed list in the same
        /// synchronous block as the <see cref="ReFlySessionMarker"/> write.
        /// Bumps <see cref="StateVersion"/> so the ERS cache invalidates
        /// immediately; no disk flush (the provisional is durable via the
        /// scenario save, not via sidecar files, until it is merged).
        /// </summary>
        internal static void AddProvisional(Recording rec)
        {
            if (rec == null)
            {
                ParsekLog.Warn("RecordingStore", "AddProvisional called with null recording");
                return;
            }
            committedRecordings.Add(rec);
            BumpStateVersion();
            ParsekLog.Verbose("RecordingStore",
                $"AddProvisional: rec={rec.RecordingId} state={rec.MergeState} " +
                $"supersedeTarget={rec.SupersedeTargetId ?? "<none>"} " +
                $"total={committedRecordings.Count}");
        }

        /// <summary>
        /// Removes a recording from the internal committed list.
        /// For production code that needs mutation after CommittedRecordings became IReadOnlyList.
        /// </summary>
        internal static bool RemoveCommittedInternal(Recording rec)
        {
            if (rec == null) return false;
            int index = committedRecordings.IndexOf(rec);
            if (index < 0) return false;
            RemoveCommittedAtWithNotifications(index);
            return true;
        }

        /// <summary>
        /// Removes a committed tree by id, including its recordings from the flat
        /// committed list, so a live restore can take ownership without duplicate-id
        /// collisions or "still committed" semantics.
        /// </summary>
        internal static bool RemoveCommittedTreeById(string treeId, string logContext = null)
        {
            if (string.IsNullOrEmpty(treeId))
                return false;

            bool removed = false;
            for (int i = committedTrees.Count - 1; i >= 0; i--)
            {
                if (committedTrees[i].Id != treeId)
                    continue;

                RecordingTree stale = committedTrees[i];
                foreach (Recording rec in stale.Recordings.Values)
                    RemoveCommittedInternal(rec);

                committedTrees.RemoveAt(i);
                removed = true;
                ParsekLog.Info("RecordingStore",
                    $"{logContext ?? "RemoveCommittedTreeById"}: removed committed tree " +
                    $"'{stale.TreeName}' (id={treeId}, {stale.Recordings.Count} recording(s))");
            }

            return removed;
        }

        /// <summary>
        /// Clears all recordings from the internal committed list, removing from the top
        /// down through the notifying primitive so every index-keyed consumer tears its
        /// slot down and shifts in step. The bundle restore that runs when a rewind's
        /// quicksave load fails (FLIGHT, ghosts alive, no scene change) reaches this; a
        /// silent wipe there left every ghost slot keyed against a rebuilt list.
        /// </summary>
        internal static void ClearCommittedInternal()
        {
            int count = committedRecordings.Count;
            for (int i = count - 1; i >= 0; i--)
                RemoveCommittedAtWithNotifications(i);
            BumpStateVersion();
            if (count > 0)
                ParsekLog.Verbose("RecordingStore", $"ClearCommittedInternal: removed {count} recording(s) with notifications");
        }

        /// <summary>
        /// Phase 6 of Rewind-to-Staging (design §6.4 reconciliation table):
        /// adds a tree to <see cref="committedTrees"/> without re-running
        /// <see cref="FinalizeTreeCommit"/>'s full commit pipeline (no session
        /// merge, no group assignment, no milestone creation). The bundle
        /// restore path uses this to re-install pre-load trees verbatim after
        /// a scene reload wiped the parallel list; the trees' recordings are
        /// re-installed via <see cref="AddCommittedInternal"/> in the same
        /// restore pass.
        /// </summary>
        internal static void AddCommittedTreeInternal(RecordingTree tree)
        {
            if (tree == null) return;
            committedTrees.Add(tree);
            BumpStateVersion();
        }

        /// <summary>
        /// Phase 6 of Rewind-to-Staging: companion to <see cref="AddCommittedTreeInternal"/>.
        /// Clears <see cref="committedTrees"/> without touching the parallel
        /// <see cref="committedRecordings"/> list — the bundle-restore caller
        /// manages both lists in lockstep.
        /// </summary>
        internal static void ClearCommittedTreesInternal()
        {
            committedTrees.Clear();
            BumpStateVersion();
        }


        /// <summary>
        /// Forwarder to <see cref="TreeCommitService.CommitPendingTree"/> (RecordingStore services
        /// plan, PR12 L). The commit reaches GameStateStore, MilestoneStore, SupersedeCommit and
        /// LedgerOrchestrator; MergeDialog and MergeJournalOrchestrator, which the RecordingStore
        /// cut does not remove, would close cycles through the service around that cut, so they
        /// stay on this and on <see cref="UnionActiveReFlyTreeIntoCommittedForMerge"/>.
        /// </summary>
        public static void CommitPendingTree()
        {
            TreeCommitService.CommitPendingTree();
        }

        /// <summary>Forwarder to <see cref="TreeCommitService.UnionActiveReFlyTreeIntoCommittedForMerge"/>
        /// for MergeJournalOrchestrator (see <see cref="CommitPendingTree"/>).</summary>
        internal static bool UnionActiveReFlyTreeIntoCommittedForMerge(
            RecordingTree existing, RecordingTree incoming, ReFlySessionMarker marker,
            out int addedRecs, out int addedBps)
        {
            return TreeCommitService.UnionActiveReFlyTreeIntoCommittedForMerge(
                existing, incoming, marker, out addedRecs, out addedBps);
        }

        /// <summary>
        /// Forwarder to <see cref="TreeDiscardService.RefreshSaveAndQuicksaveAfterDiscard"/>
        /// (RecordingStore services plan, PR12 K) for MergeDialog. The discard service purges
        /// game-state and route events and re-homes the irreversible ledger tail, so it reaches
        /// GameStateStore, RouteEventLog and LedgerOrchestrator; MergeDialog, which the
        /// RecordingStore cut does not remove, would close a cycle through it around that cut.
        /// </summary>
        internal static void RefreshSaveAndQuicksaveAfterDiscard(
            string reason,
            int discardedRecordingCount)
        {
            TreeDiscardService.RefreshSaveAndQuicksaveAfterDiscard(reason, discardedRecordingCount);
        }

        // Forwarders to SwitchSegmentAttempts (RecordingStore services plan, PR11). The service
        // purges game-state events and route events and preserves the irreversible ledger tail,
        // so it reaches GameStateStore, RouteEventLog and LedgerOrchestrator; a caller the
        // RecordingStore cut does not remove (MergeDialog, the map-focus patch, the restore
        // attempt) would close a cycle through it around that cut, so those callers stay on these.
        internal static SwitchSegmentDiscardDisposition TryDiscardActiveSwitchSegmentAttempt(
            out string reason)
        {
            return SwitchSegmentAttempts.TryDiscardActiveSwitchSegmentAttempt(out reason);
        }

        internal static bool IsMarkerOwnedSwitchSegmentRecordingId(string recordingId)
        {
            return SwitchSegmentAttempts.IsMarkerOwnedSwitchSegmentRecordingId(recordingId);
        }

        /// <summary>
        /// Disposition path returned by <see cref="SwitchSegmentAttempts.TryDiscardActiveSwitchSegmentAttempt"/>.
        /// Drives the caller's follow-up cleanup (clone-drop vs nothing) per
        /// plan §"Final Disposition After Scoped Discard". Bug 2 (post-#876
        /// playtest 2026-05-17) widened the scoped sweep to the topological
        /// subtree, so the historic second-dialog "remaining pending changes"
        /// branch is no longer used — the scoped Discard now removes the
        /// entire segment subtree on its own.
        /// </summary>
        internal enum SwitchSegmentDiscardDisposition
        {
            /// <summary>No active session was armed; nothing was discarded.</summary>
            NoActiveSession = 0,

            /// <summary>The segment was added inside a committed-tree restore
            /// clone. After pruning the segment subtree, the entire
            /// active/pending clone wrapper is dropped and the committed-tree
            /// restore attempt cleared.</summary>
            CommittedRestoreClone = 1,

            /// <summary>The segment was added to a non-committed pending
            /// tree. After pruning the segment subtree, the pruned pending
            /// tree remains in the pending slot.</summary>
            PendingTreePrune = 2,
        }


        /// <summary>
        /// True when <paramref name="recordingId"/> belongs to the current live timeline:
        /// a committed recording that survives ERS supersede/session-suppression filtering,
        /// the pending tree, or the active in-flight tree being recorded/resumed.
        /// This replaces the old epoch gate for "is this branch still live?" decisions.
        /// </summary>
        internal static bool IsCurrentTimelineRecordingId(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return false;
            if (PendingTreeSlot.IsPendingRecordingId(recordingId) || ParsekFlight.IsActiveTreeRecordingId(recordingId))
                return true;

            var ers = EffectiveState.ComputeERS();
            for (int i = 0; i < ers.Count; i++)
            {
                if (string.Equals(ers[i].RecordingId, recordingId, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }


        /// <summary>
        /// Test seam for #292: lets unit tests intercept GamePersistence.SaveGame calls
        /// without invoking the real KSP API. Defaults to the real call. Tests reset to
        /// null in Dispose to restore default behavior.
        /// </summary>
        internal static System.Func<string, string, SaveMode, string> SaveGameForTesting;

        /// <summary>
        /// No longer called (design-load-restores-the-save.md 4.6: a merge keeps the
        /// player's F5 point); kept until the Phase 3 deletion.
        /// Bug #292: refresh quicksave.sfs after a user-initiated tree merge so subsequent
        /// F9 quickloads see the merge result. Without this, F9 loads a stale quicksave
        /// from before the merge and silently drops the new recording IDs the merge added.
        ///
        /// MUST be called only from user-initiated merge entry points (currently:
        /// MergeDialog.cs "Merge to Timeline" button handler), NEVER from inside
        /// RunOptimizationPass or any OnLoad path — KSP's SaveGame triggers OnSave on
        /// every ScenarioModule, which would re-enter Parsek's OnSave during OnLoad and
        /// risk corrupting scenario state.
        ///
        /// Failure modes are non-fatal: if SaveGame returns null or throws, log a warning
        /// and continue. The merge still completes; the player loses only the quicksave
        /// refresh benefit until they manually F5 again.
        /// </summary>
        internal static void RefreshQuicksaveAfterMerge(string reason, int recordingCount)
        {
            var saveFn = SaveGameForTesting ?? GamePersistence.SaveGame;

            // Defense-in-depth: skip during loading scenes (no game state to save).
            // Tests bypass these guards by setting SaveGameForTesting.
            if (SaveGameForTesting == null)
            {
                if (HighLogic.LoadedScene == GameScenes.LOADING)
                {
                    ParsekLog.Verbose("Quicksave",
                        $"Refresh skipped (LOADING scene): reason={reason}");
                    return;
                }
                if (HighLogic.CurrentGame == null)
                {
                    ParsekLog.Verbose("Quicksave",
                        $"Refresh skipped (CurrentGame is null): reason={reason}");
                    return;
                }
            }

            try
            {
                string result = saveFn("quicksave", HighLogic.SaveFolder, SaveMode.OVERWRITE);
                if (string.IsNullOrEmpty(result))
                {
                    ParsekLog.Warn("Quicksave",
                        $"GamePersistence.SaveGame returned null after {reason} — quicksave NOT refreshed");
                    return;
                }
                ParsekLog.Info("Quicksave",
                    $"Refreshed quicksave.sfs after {reason} (tree has {recordingCount} recording IDs)");
            }
            catch (System.Exception ex)
            {
                ParsekLog.Warn("Quicksave",
                    $"Exception refreshing quicksave after {reason}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Test seam: lets unit tests simulate the live-Unity-runtime context that
        // <see cref="ResetForTesting"/>'s data-loss guard checks. Production code never
        // sets this; xUnit tests in <c>RecordingStoreResetGuardTests</c> use it to verify
        // the guard hard-fails when invoked alongside committed data inside an in-game
        // test runner. <see cref="Application.isPlaying"/> is false outside Unity play
        // mode (xUnit, command-line dotnet test) so the guard is otherwise harmless to
        // existing xUnit callers that happen to add data before resetting.
        internal static Func<bool> ApplicationIsPlayingForTesting;

        // <see cref="UnityEngine.Application.isPlaying"/> is an extern (ECall) — fine
        // at runtime inside KSP, but throws SecurityException when xUnit runs the
        // assembly outside the Unity player. The JIT verifies the call before the
        // surrounding try/catch frame is established when the access lives in the
        // same method as the catch, so the exception escapes. Isolating the read in
        // its own non-inlined method delays JIT verification until first call, where
        // the catch around the call site can swallow the exception. Treating "throws"
        // as "not in play mode" preserves existing xUnit <c>ResetForTesting()</c>
        // callers (RecordingStoreTests, etc.) without forcing every one of them to
        // set <see cref="ApplicationIsPlayingForTesting"/>.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool ReadUnityApplicationIsPlayingCore()
        {
            return Application.isPlaying;
        }

        private static bool TryReadUnityApplicationIsPlaying()
        {
            try { return ReadUnityApplicationIsPlayingCore(); }
            catch (System.Security.SecurityException) { return false; }
            catch (System.TypeInitializationException) { return false; }
            catch (MissingMethodException) { return false; }
        }

        /// <summary>
        /// Resets state without Unity logging. For unit tests only.
        /// <para>
        /// Hard-fails when invoked while Unity is in play mode AND the store still
        /// holds real committed data — the in-game test runner (Ctrl+Shift+T) shares
        /// the static <see cref="committedRecordings"/> / <see cref="committedTrees"/>
        /// list with the player's live save, so a stray <c>ResetForTesting()</c>
        /// inside an in-game test would silently delete everything the player had
        /// just recorded (observed in production 2026-05-01: 5 committed recordings
        /// wiped by <c>PersistenceSplitOptimizerTest</c>). xUnit tests run with
        /// <see cref="Application.isPlaying"/> = <c>false</c> and remain unaffected.
        /// In-game tests must wrap mutations in
        /// <see cref="RecordingStoreTestSnapshot.Capture"/> / <see cref="RecordingStoreTestSnapshot.Restore"/>
        /// instead of calling this method.
        /// </para>
        /// </summary>
        internal static void ResetForTesting()
            => ResetForTestingInternal(allowWipingLiveSaveData: false);

        /// <summary>
        /// Clears stale <c>LoopPlayback=true</c> on every recording that is not
        /// "logically loopable" (see <see cref="Recording.IsLoopableRecording"/>):
        /// debris (which rides its parent's loop clock via
        /// <c>GhostPlaybackEngine.TryUpdateLoopSyncedDebris</c>) and pure orbital
        /// coasts (a map line with no watchable flying phase). Neither gets a
        /// Recordings-tab loop toggle, so a stale <c>true</c> here (from a save
        /// made before the toggle was hidden, or from any path that set it on a
        /// non-loopable row) would otherwise loop at the engine with no way to
        /// clear it from the table. Clearing at load keeps "no loop toggle means
        /// it does not loop" consistent. Mission looping is unaffected: a Mission
        /// loops via its own <c>Mission.LoopPlayback</c> plus the shared span
        /// clock and never reads a member recording's <c>LoopPlayback</c> flag
        /// (the loop-unit interception sits above the per-recording loop gate in
        /// <c>GhostPlaybackEngine</c>). Returns the count cleared (zero on
        /// subsequent loads once swept, so this is idempotent).
        /// </summary>
        internal static int SanitizeNonLoopableLoopPlayback()
        {
            int cleared = 0;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var rec = committedRecordings[i];
                if (rec == null) continue;
                // IsLoopableRecording already returns false for debris, so this
                // single predicate subsumes the former debris-only sweep.
                if (rec.LoopPlayback && !Recording.IsLoopableRecording(rec))
                {
                    rec.LoopPlayback = false;
                    cleared++;
                }
            }
            if (cleared > 0)
            {
                ParsekLog.Warn("RecordingStore",
                    $"SanitizeNonLoopableLoopPlayback: cleared LoopPlayback on {cleared} non-loopable recording(s) " +
                    "(debris or pure orbital coast; no Recordings-tab loop toggle, so the stale flag had no way to be cleared)");
            }
            return cleared;
        }

        /// <summary>
        /// Wipe-and-reset variant for the in-game test runner's batch FLIGHT
        /// baseline restore flow. The next operation after this call is a
        /// <c>QuickloadResumeHelpers.TriggerQuickload</c> from the baseline
        /// save slot, which restores the player's clean pre-batch state from
        /// disk via <c>OnLoad</c>. The in-memory wipe is therefore a
        /// transient about-to-be-overwritten step, not a destructive
        /// player-save mutation; the live-save guard exists to prevent the
        /// PersistenceSplitOptimizerTest 2026-05-01 class of bug where a
        /// test wiped state without restoring it. Documented at
        /// <see cref="ParsekScenario.PrepareForIsolatedBatchFlightBaselineRestore"/>
        /// — that's the only caller. Bypassing the guard from anywhere else
        /// re-opens the production bug.
        /// </summary>
        internal static void ResetForBatchFlightBaselineRestoreBypassingGuard()
            => ResetForTestingInternal(allowWipingLiveSaveData: true);

        private static void ResetForTestingInternal(bool allowWipingLiveSaveData)
        {
            if (!allowWipingLiveSaveData)
            {
                bool isPlaying;
                if (ApplicationIsPlayingForTesting != null)
                {
                    isPlaying = ApplicationIsPlayingForTesting();
                }
                else
                {
                    isPlaying = TryReadUnityApplicationIsPlaying();
                }
                if (isPlaying)
                {
                    int recCount = committedRecordings.Count;
                    int treeCount = committedTrees.Count;
                    bool hasPending = PendingTreeSlot.PendingTree != null;
                    bool hasSavedPendingDuringActiveRestore =
                        PendingTreeSlot.SavedPendingTreeDuringActiveRestore != null;
                    int inertTreeCount = InertRecordingTrees.Count;
                    if (recCount > 0
                        || treeCount > 0
                        || inertTreeCount > 0
                        || hasPending
                        || hasSavedPendingDuringActiveRestore)
                    {
                        string msg =
                            $"ResetForTesting blocked: refusing to wipe live save data " +
                            $"(committedRecordings={recCount}, committedTrees={treeCount}, " +
                            $"inertTrees={inertTreeCount}, " +
                            $"hasPendingTree={hasPending}, " +
                            $"hasSavedPendingDuringActiveRestore={hasSavedPendingDuringActiveRestore}). " +
                            $"The in-game test runner shares " +
                            $"static state with the player's save — call " +
                            $"RecordingStoreTestSnapshot.Capture()/Restore() around the " +
                            $"test body instead of ResetForTesting(). Production bug source: " +
                            $"PersistenceSplitOptimizerTest 2026-05-01.";
                        ParsekLog.Error("RecordingStore", msg);
                        throw new InvalidOperationException(msg);
                    }
                }
            }

            committedRecordings.Clear();
            committedTrees.Clear();
            InertRecordingTrees.ResetForTesting();
            BumpStateVersion();
            ResetCommittedListNotificationsForTesting();
            RecordingGroupStore.ResetForTesting();
            durableCommittedRecordingIdHint = null;
            PendingTreeSlot.ResetForTesting();
            CommittedTreeRestoreAttempt.ResetForTesting();
            RecordingFileJanitor.ResetForTesting();
            RecordingSidecarStore.ResetForTesting();
            SceneEntryVesselScope.ResetForTesting();
            rewindCarriedRewindPoints = null;
            RewindContext.ResetForTesting();
            RewindUTAdjustmentPending = false;
            RewindUTAdjustmentTargetUT = double.NaN;
            GameStateRecorder.PendingScienceSubjects.Clear();
            PendingCleanupPids = null;
            PendingCleanupNames = null;
            PendingRevertPreExistingPids = null;
            TreeDiscardService.ResetForTesting();
            DiscardReFlyLoadIntent.ResetForTesting();
            InSessionStagedStateHandoff.ResetForTesting();
            VesselNameStore.ResetForTesting();
        }


        /// <summary>
        /// In-place restore of the four core fields a <see cref="RecordingStoreTestSnapshot"/>
        /// captures: committed recordings, committed trees, pending tree slot + state,
        /// saved pending tree preserved during active restore, and the
        /// auto-assigned-standalone-group dict. Bypasses the
        /// <see cref="ResetForTesting"/> guard because the call site knows it's about to
        /// re-install the very data the guard exists to protect. Used only by
        /// <see cref="RecordingStoreTestSnapshot.Restore"/>.
        /// </summary>
        internal static void RestoreFromSnapshotForTesting(
            List<Recording> snapshotRecordings,
            List<RecordingTree> snapshotTrees,
            RecordingTree snapshotPendingTree,
            PendingTreeState snapshotPendingState,
            RecordingTree snapshotSavedPendingTreeDuringActiveRestore,
            bool snapshotSavedPendingTreeDuringActiveRestoreSerializedForSave,
            Dictionary<string, string> snapshotAutoAssignedGroups)
        {
            // The in-game runner restores in FLIGHT with ghosts alive, and index-keyed live
            // state mirrors this list by position, so the restore goes through the
            // committed-list notification seam: keep the longest prefix that already holds
            // the snapshot's own references, remove everything after it top-down with
            // notifications, then append the snapshot's tail (an append shifts no index).
            int keep = CommonCommittedPrefixLength(committedRecordings, snapshotRecordings);
            int removed = committedRecordings.Count - keep;
            for (int i = committedRecordings.Count - 1; i >= keep; i--)
                RemoveCommittedAtWithNotifications(i);
            int appended = 0;
            if (snapshotRecordings != null)
            {
                for (int i = keep; i < snapshotRecordings.Count; i++)
                {
                    committedRecordings.Add(snapshotRecordings[i]);
                    appended++;
                }
            }
            if (removed > 0 || appended > 0)
                ParsekLog.Verbose("RecordingStore",
                    $"RestoreFromSnapshotForTesting: kept={keep.ToString(CultureInfo.InvariantCulture)} " +
                    $"removed={removed.ToString(CultureInfo.InvariantCulture)} (with notifications) " +
                    $"appended={appended.ToString(CultureInfo.InvariantCulture)}");
            committedTrees.Clear();
            if (snapshotTrees != null)
                committedTrees.AddRange(snapshotTrees);
            PendingTreeSlot.RestoreFromSnapshotForTesting(
                snapshotPendingTree,
                snapshotPendingState,
                snapshotSavedPendingTreeDuringActiveRestore,
                snapshotSavedPendingTreeDuringActiveRestoreSerializedForSave);
            RecordingGroupStore.RestoreAutoAssignedStandaloneGroupsForTesting(snapshotAutoAssignedGroups);
            BumpStateVersion();
        }

        /// <summary>
        /// Pure: how many leading slots of <paramref name="live"/> already hold the very
        /// reference <paramref name="snapshot"/> holds at the same index.
        /// </summary>
        internal static int CommonCommittedPrefixLength(
            IReadOnlyList<Recording> live, IReadOnlyList<Recording> snapshot)
        {
            if (live == null || snapshot == null)
                return 0;
            int n = Math.Min(live.Count, snapshot.Count);
            int i = 0;
            while (i < n && ReferenceEquals(live[i], snapshot[i]))
                i++;
            return i;
        }

        /// <summary>
        /// Adds a recording to the committed list with tree ownership enforced.
        /// If the recording has no TreeId, wraps it in a single-node RecordingTree.
        /// For unit tests only.
        /// </summary>
        internal static void AddRecordingWithTreeForTesting(Recording rec, string treeName = null)
        {
            if (rec.TreeId == null)
            {
                var tree = new RecordingTree
                {
                    Id = Guid.NewGuid().ToString("N"),
                    TreeName = treeName ?? rec.VesselName ?? "TestTree",
                    RootRecordingId = rec.RecordingId
                };
                rec.TreeId = tree.Id;
                tree.AddOrReplaceRecording(rec);
                committedTrees.Add(tree);
            }
            committedRecordings.Add(rec);
            BumpStateVersion();
        }

        /// <summary>
        /// Adds a tree directly to committed trees. For unit tests only.
        /// </summary>
        internal static void AddCommittedTreeForTesting(RecordingTree tree)
        {
            committedTrees.Add(tree);
        }

        #region Rewind

        /// <summary>
        /// Resets all playback state on committed recordings.
        /// Called during rewind to prepare all recordings for fresh replay.
        /// <paramref name="keepSpawnStateRecordingIds"/> names recordings whose spawned vessel
        /// the rewind kept as committed history (<see cref="RewindSpawnScope.ResolveRewindSpawnStripScope"/>):
        /// their spawn state (VesselSpawned / SpawnedVesselPersistentId and the spawn
        /// bookkeeping) is preserved so the kept vessel stays linked to its recording, the
        /// same way a revert restores an earlier tree's spawn state from the revert target.
        /// The post-strip reconcile still resets any of them whose vessel did not survive.
        /// </summary>
        internal static (int recordingCount, int treeCount) ResetAllPlaybackState(
            HashSet<string> keepSpawnStateRecordingIds = null)
        {
            TreeCommitService.MarkSupersededTerminalSpawnsForCommittedContinuations("ResetAllPlaybackState");

            var keptLogged = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < committedRecordings.Count; i++)
                ResetRecordingPlaybackFields(committedRecordings[i], keepSpawnStateRecordingIds, keptLogged);

            for (int i = 0; i < committedTrees.Count; i++)
            {
                foreach (var rec in committedTrees[i].Recordings.Values)
                    ResetRecordingPlaybackFields(rec, keepSpawnStateRecordingIds, keptLogged);
            }

            if (!SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"Playback state reset: {committedRecordings.Count} recording(s), {committedTrees.Count} tree(s), " +
                    $"keptHistorySpawnState={keptLogged.Count}");

            return (committedRecordings.Count, committedTrees.Count);
        }

        private static void ResetRecordingPlaybackFields(
            Recording rec,
            HashSet<string> keepSpawnStateRecordingIds = null,
            HashSet<string> keptLogged = null)
        {
            // The terminal verdict is NOT touched: a committed recording's
            // TerminalStateValue is recorded content (real-vessel terminal events stamp
            // only the pending tree), and Destroyed / Recovered are never spawnable, so
            // the spawn fields below are all a re-spawn needs. Same rule as the OnLoad
            // tree-mutable-state reset (ParsekScenario.ResetTreeRecordingMutableStateForLoad).

            bool keepSpawnState = keepSpawnStateRecordingIds != null
                && !string.IsNullOrEmpty(rec.RecordingId)
                && keepSpawnStateRecordingIds.Contains(rec.RecordingId);
            if (keepSpawnState)
            {
                // Committed history the rewind kept: its vessel exists at the rewind target
                // and nothing that replays spawns it again, so its spawn link is kept too.
                if (keptLogged != null && keptLogged.Add(rec.RecordingId) && !SuppressLogging)
                    ParsekLog.Verbose("Rewind",
                        $"Playback reset kept spawn state for committed history \"{rec.VesselName}\" " +
                        $"id={rec.RecordingId} spawnedPid={rec.SpawnedVesselPersistentId} vesselSpawned={rec.VesselSpawned}");
            }
            else
            {
                rec.VesselSpawned = false;
                rec.VesselDestroyed = false;
                rec.SpawnAttempts = 0;
                rec.SpawnDeathCount = 0;
                rec.SpawnedVesselPersistentId = 0;
                rec.CollisionBlockCount = 0;
                rec.SpawnAbandoned = false;
                rec.WalkbackExhausted = false;
                rec.DuplicateBlockerRecovered = false;
                TerminalOrbitSpawnSafety.Clear(rec);
            }
            rec.LastAppliedResourceIndex = -1;
            // SpawnSuppressedByRewind is cleared here so a subsequent rewind starts
            // from a clean slate. ParsekScenario.HandleRewindOnLoad re-marks the
            // active/source recording AFTER ResetAllPlaybackState fires.
            if (rec.SpawnSuppressedByRewind && !SuppressLogging)
                ParsekLog.Verbose("Rewind",
                    $"SpawnSuppressedByRewind reset: \"{rec.VesselName}\" id={rec.RecordingId} " +
                    $"reason={rec.SpawnSuppressedByRewindReason ?? "<none>"}");
            rec.SpawnSuppressedByRewind = false;
            rec.SpawnSuppressedByRewindReason = null;
            rec.SpawnSuppressedByRewindUT = double.NaN;

            rec.SceneExitSituation = -1;
        }

        /// <summary>
        /// Collects SpawnedVesselPersistentId values and vessel names from all committed
        /// recordings (standalone + tree) that currently have a spawned vessel.
        /// Must be called BEFORE ResetAllPlaybackState
        /// zeroes the PIDs.
        /// </summary>
        internal static (HashSet<uint> pids, HashSet<string> names) CollectSpawnedVesselInfo()
        {
            var pids = new HashSet<uint>();
            var names = new HashSet<string>();

            for (int i = 0; i < committedRecordings.Count; i++)
            {
                uint pid = committedRecordings[i].SpawnedVesselPersistentId;
                if (pid != 0)
                {
                    pids.Add(pid);
                    if (!string.IsNullOrEmpty(committedRecordings[i].VesselName))
                        names.Add(committedRecordings[i].VesselName);
                }
            }

            for (int i = 0; i < committedTrees.Count; i++)
            {
                foreach (var rec in committedTrees[i].Recordings.Values)
                {
                    if (rec.SpawnedVesselPersistentId != 0)
                    {
                        pids.Add(rec.SpawnedVesselPersistentId);
                        if (!string.IsNullOrEmpty(rec.VesselName))
                            names.Add(rec.VesselName);
                    }
                }
            }

            if (!SuppressLogging && pids.Count > 0)
                ParsekLog.Info("RecordingStore",
                    $"CollectSpawnedVesselInfo: {pids.Count} PID(s), {names.Count} name(s)");

            return (pids, names);
        }

        // One-shot cleanup data: populated in ParsekScenario.OnLoad revert/rewind path
        // (before spawn state reset), consumed and cleared in ParsekFlight.OnFlightReady.
        internal static HashSet<uint> PendingCleanupPids { get; set; }
        internal static HashSet<string> PendingCleanupNames { get; set; }

        // BUG-H: pids of vessels present in the revert-target launch/prelaunch quicksave
        // (FlightDriver.PreLaunchState / PostInitState). A vessel whose pid is in this set
        // pre-existed the reverted launch and must NEVER be stripped/recovered — it belongs
        // to an unrelated mission, not the reverted flight. Populated in the OnLoad revert
        // path, consumed and cleared in ParsekFlight.OnFlightReady (belt-and-suspenders
        // cleanup). Null when there is no active revert scope.
        internal static HashSet<uint> PendingRevertPreExistingPids { get; set; }

        /// <summary>
        /// Bug #134: after the rewind strip has already removed orphaned spawned
        /// protoVessels from the loaded flight state, the pending one-shot cleanup
        /// data must be dropped so <c>ParsekFlight.OnFlightReady</c> does not re-run
        /// with the overbroad all-recording name set and destroy freshly-spawned past
        /// vessels. The revert path's alreadyHasCleanupData guard then sees null and
        /// collects fresh data if it needs any.
        /// Called from <c>ParsekScenario.HandleRewindOnLoad</c> right after the strip.
        /// </summary>
        internal static void ClearPendingCleanupAfterRewindStrip()
        {
            PendingCleanupPids = null;
            PendingCleanupNames = null;
            PendingRevertPreExistingPids = null;
            ParsekLog.Info("Rewind",
                "OnLoad: cleared PendingCleanupPids/Names after strip - " +
                "prevents OnFlightReady from destroying freshly-spawned past vessels");
        }

        /// <summary>
        /// The armed-cleanup trigger <c>ParsekFlight.OnFlightReady</c> tests before
        /// running its belt-and-suspenders orphaned-vessel recovery. Kept next to
        /// <see cref="ClearPendingCleanupAfterRewindStrip"/> so the clear and the gate
        /// it silences read the same two fields.
        /// </summary>
        internal static bool ShouldRunPendingCleanupOnFlightReady()
        {
            return PendingCleanupPids != null || PendingCleanupNames != null;
        }

        /// <summary>
        /// Flat list of every committed recording (standalone + tree members), for the
        /// launch-identity-aware vessel-strip / cleanup predicate (BUG-H). Mirrors
        /// <see cref="CollectSpawnedVesselInfo"/>'s iteration. This is a raw read of the committed
        /// store because the consumer is a physical pid+Guid identity correlation (does this live
        /// vessel belong to a recording's launch?), not a supersede-aware recording-set walk — the
        /// same trust scope as <see cref="CollectSpawnedVesselInfo"/> / <see cref="CollectAllRecordingVesselNames"/>.
        /// </summary>
        internal static List<Recording> CollectAllCommittedRecordings()
        {
            var list = new List<Recording>(committedRecordings.Count);
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                if (committedRecordings[i] != null)
                    list.Add(committedRecordings[i]);
            }
            for (int i = 0; i < committedTrees.Count; i++)
            {
                foreach (var rec in committedTrees[i].Recordings.Values)
                {
                    if (rec != null)
                        list.Add(rec);
                }
            }
            return list;
        }

        // Delegate property for RewindQuicksaveVesselPids — state now lives in RewindContext.
        internal static HashSet<uint> RewindQuicksaveVesselPids => RewindContext.RewindQuicksaveVesselPids;

        /// <summary>
        /// Collects vessel names from ALL committed recordings (regardless of spawn state).
        /// Used during rewind to strip every vessel matching a recording name from flightState —
        /// on rewind, any such vessel is from the future and incompatible with the rewound state.
        /// </summary>
        internal static HashSet<string> CollectAllRecordingVesselNames()
        {
            var names = new HashSet<string>();
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                if (!string.IsNullOrEmpty(committedRecordings[i].VesselName))
                    names.Add(committedRecordings[i].VesselName);
            }
            for (int i = 0; i < committedTrees.Count; i++)
            {
                foreach (var rec in committedTrees[i].Recordings.Values)
                {
                    if (!string.IsNullOrEmpty(rec.VesselName))
                        names.Add(rec.VesselName);
                }
            }
            if (!SuppressLogging && names.Count > 0)
                ParsekLog.Info("RecordingStore",
                    $"CollectAllRecordingVesselNames: {names.Count} unique name(s)");
            return names;
        }

        /// <summary>
        /// Advances each recording's <c>LastAppliedResourceIndex</c> to the last point
        /// for recordings with non-empty <c>Points</c>. Does NOT touch
        /// <see cref="MilestoneStore.Milestones"/> — that global mutation is specifically
        /// what callers need to avoid when marking a single tree fully applied (see plan
        /// Phase C, `docs/dev/done/plans/fix-ledger-lump-sum-reconciliation.md`). Returns the
        /// number of recordings whose applied index was advanced. Safe to call with a
        /// null tree (returns 0).
        /// </summary>
        internal static int MarkTreeAsApplied(RecordingTree tree)
        {
            if (tree == null) return 0;

            int advanced = 0;
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec == null) continue;
                if (rec.Points != null && rec.Points.Count > 0)
                {
                    rec.LastAppliedResourceIndex = rec.Points.Count - 1;
                    advanced++;
                }
            }

            if (!SuppressLogging)
                ParsekLog.Verbose("RecordingStore",
                    $"MarkTreeAsApplied: tree id='{tree.Id}' name='{tree.TreeName}' " +
                    $"recordingsAdvanced={advanced}");

            return advanced;
        }

        /// <summary>
        /// Counts committed recordings whose StartUT is after the given UT.
        /// Used to display how many future recordings will replay as ghosts after rewind.
        /// </summary>
        internal static int CountFutureRecordings(double ut)
        {
            int count = 0;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                if (committedRecordings[i].StartUT > ut)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Returns the recording that owns the rewind save for the given recording.
        /// For standalone recordings this is the recording itself; for tree branches
        /// it is the tree root (which captured the quicksave at launch).
        /// Returns null if no rewind save is available.
        /// </summary>
        internal static Recording GetRewindRecording(Recording rec)
        {
            return GetRewindRecording(rec, committedTrees);
        }

        /// <summary>
        /// Parameterized overload for testability.
        /// </summary>
        internal static Recording GetRewindRecording(Recording rec, List<RecordingTree> trees)
        {
            if (rec == null) return null;

            // Direct owner: recording has its own rewind save
            if (!string.IsNullOrEmpty(rec.RewindSaveFileName))
                return rec;

            // Tree branch: look up root recording's rewind save
            if (!string.IsNullOrEmpty(rec.TreeId) && trees != null)
            {
                for (int i = 0; i < trees.Count; i++)
                {
                    if (trees[i].Id == rec.TreeId)
                    {
                        Recording rootRec;
                        if (!string.IsNullOrEmpty(trees[i].RootRecordingId) &&
                            trees[i].Recordings.TryGetValue(trees[i].RootRecordingId, out rootRec) &&
                            !string.IsNullOrEmpty(rootRec.RewindSaveFileName))
                        {
                            return rootRec;
                        }
                        break; // Found tree but root has no save
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Convenience wrapper: returns the rewind save filename for a recording,
        /// resolving through the tree root if needed. Returns null if unavailable.
        /// </summary>
        internal static string GetRewindSaveFileName(Recording rec)
        {
            var owner = GetRewindRecording(rec);
            return owner?.RewindSaveFileName;
        }

        /// <summary>
        /// Checks whether the player can rewind to a given recording's launch point.
        /// </summary>
        internal static bool CanRewind(Recording rec, out string reason, bool isRecording)
        {
            string resolvedSave = GetRewindSaveFileName(rec);
            if (!CanRewindPreFileCheck(resolvedSave, out reason, isRecording))
                return false;

            string savePath = RecordingPaths.ResolveSaveScopedPath(
                RecordingPaths.BuildRewindSaveRelativePath(resolvedSave));
            bool saveExists = !string.IsNullOrEmpty(savePath) && File.Exists(savePath);
            return CanRewindWithResolvedSaveState(resolvedSave, saveExists, out reason, isRecording);
        }

        /// <summary>
        /// Testable core of <see cref="CanRewind"/> once tree-root save resolution and file
        /// existence have already been computed by the caller.
        /// </summary>
        internal static bool CanRewindWithResolvedSaveState(
            string resolvedSave, bool saveExists, out string reason, bool isRecording)
        {
            if (!CanRewindPreFileCheck(resolvedSave, out reason, isRecording))
                return false;

            if (!saveExists)
            {
                reason = "Rewind save file missing";
                // Per-frame logging removed (was 3.7% of all log output); reason returned to caller
                return false;
            }

            reason = "";
            // Per-frame logging removed; reason returned to caller
            return true;
        }

        private static bool CanRewindPreFileCheck(string resolvedSave, out string reason, bool isRecording)
        {
            if (IsRewinding)
            {
                reason = "Rewind already in progress";
                // Per-frame logging removed (was 3.7% of all log output); reason returned to caller
                return false;
            }

            if (string.IsNullOrEmpty(resolvedSave))
            {
                reason = "No rewind save available";
                // Per-frame logging removed (was 3.7% of all log output); reason returned to caller
                return false;
            }

            if (isRecording)
            {
                reason = "Stop recording before rewinding";
                // Per-frame logging removed (was 3.7% of all log output); reason returned to caller
                return false;
            }

            if (HasPendingTree)
            {
                reason = "Merge or discard pending tree first";
                // Per-frame logging removed (was 3.7% of all log output); reason returned to caller
                return false;
            }

            reason = "";
            // Per-frame logging removed; reason returned to caller
            return true;
        }

        /// <summary>
        /// Validates whether the player can fast-forward to a future recording's start.
        /// Subset of CanRewind — no save file required (FF advances UT, doesn't load a save).
        /// </summary>
        internal static bool CanFastForward(Recording rec, out string reason, bool isRecording)
        {
            if (!CanFastForwardPreRuntime(rec, out reason, isRecording))
                return false;

            return CanFastForwardAtUT(rec, Planetarium.GetUniversalTime(), out reason, isRecording);
        }

        /// <summary>
        /// Testable core of <see cref="CanFastForward"/> with the current UT supplied by the caller.
        /// </summary>
        internal static bool CanFastForwardAtUT(Recording rec, double now, out string reason, bool isRecording)
        {
            if (!CanFastForwardPreRuntime(rec, out reason, isRecording))
                return false;

            if (now >= rec.StartUT)
            {
                reason = "Recording is not in the future";
                // Per-frame logging removed (was 20% of all log output); reason returned to caller
                return false;
            }

            reason = "";
            // Per-frame logging removed; reason returned to caller
            return true;
        }

        private static bool CanFastForwardPreRuntime(Recording rec, out string reason, bool isRecording)
        {
            if (IsRewinding)
            {
                reason = "Rewind already in progress";
                // Per-frame logging removed (was 20% of all log output); reason returned to caller
                return false;
            }

            if (rec == null || rec.Points.Count == 0)
            {
                reason = "Recording not available";
                // Per-frame logging removed (was 20% of all log output); reason returned to caller
                return false;
            }

            var scenario = ParsekScenario.Instance;
            // Cascade overload: a parent-anchored debris child whose parent
            // was retired must also refuse fast-forward; without this, the
            // child stays interactive in the recordings table even though
            // its trajectory belongs to a retired re-fly fork.
            if (EffectiveState.IsRewindRetired(
                    rec,
                    CommittedRecordings,
                    object.ReferenceEquals(null, scenario) ? null : scenario.RecordingRewindRetirements))
            {
                reason = "Recording was rewound out of timeline";
                return false;
            }

            if (isRecording)
            {
                reason = "Stop recording before fast-forwarding";
                // Per-frame logging removed (was 20% of all log output); reason returned to caller
                return false;
            }

            if (HasPendingTree)
            {
                reason = "Merge or discard pending tree first";
                // Per-frame logging removed (was 20% of all log output); reason returned to caller
                return false;
            }

            reason = "";
            // Per-frame logging removed; reason returned to caller
            return true;
        }

        /// <summary>
        /// Clears the rewind context + the replay-target scope. The production callers are
        /// the load-failure paths inside <see cref="ExecuteRewindSaveLoad"/>, which is the
        /// ONLY place a rewind can unwind itself: the flags otherwise live until
        /// <c>HandleRewindOnLoad</c>'s <c>EndRewind()</c> in the reloaded scene.
        ///
        /// <para>Internal (not private) for ONE seam-abort consumer: the test-command
        /// applier <c>ParsekTestCommandAddon.TryCompleteInvokeRewindToLaunch</c>'s
        /// rewind-timeout branch. A reload that never settles reaches neither of the two
        /// clearing paths, so <c>RewindContext.IsRewinding</c> would stay armed for the
        /// rest of the process - the next <c>ParsekScenario.OnLoad</c> would take the
        /// rewind branch, and every later <see cref="CanRewind"/> would refuse. The verb
        /// that aborts the seam resets what the abort stranded.</para>
        /// </summary>
        internal static void ResetRewindFlags()
        {
            RewindContext.EndRewind();
            SceneEntryVesselScope.ClearRewindReplayTargetScope();
            ClearRewindCarriedRewindPoints("rewind-flags-reset");
            ClearRewindCarriedStagedLists("rewind-flags-reset");
        }

        /// <summary>
        /// Initiates a rewind to the given recording's launch point.
        /// Sets rewind flags, copies the save file to the root saves dir (KSP's LoadGame
        /// doesn't support subdirectory paths), loads it, then deletes the temp copy.
        /// </summary>
        internal static void InitiateRewind(Recording rec)
        {
            // Resolve the rewind save owner — may be the tree root for branch recordings.
            // All save-related fields (filename, resources, UT) come from the owner,
            // since the quicksave was captured at the owner's launch.
            var owner = GetRewindRecording(rec);
            if (owner == null)
            {
                if (!SuppressLogging)
                    ParsekLog.Error("Rewind",
                        $"InitiateRewind: no rewind owner for '{rec.VesselName}' (id={rec.RecordingId})");
                return;
            }

            bool isTreeBranch = owner != rec;
            if (isTreeBranch && !SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"Rewind via tree root: branch '{rec.VesselName}' -> root '{owner.VesselName}' " +
                    $"save={owner.RewindSaveFileName}");

            // Refuse rewind during an active re-fly merge journal. The rewind carries the
            // RecordingSupersedes list the merge journal is in the middle of writing
            // across the load; running both concurrently could leave the ledger
            // half-committed across the LoadGame boundary. Half-fix is worse than
            // no-fix — let MergeJournalOrchestrator.RunFinisher resolve the journal
            // on the next OnLoad first, then the user can rewind.
            var scenarioForJournalCheck = ParsekScenario.Instance;
            if (scenarioForJournalCheck?.ActiveMergeJournal != null
                && scenarioForJournalCheck.ActiveMergeJournal.Phase != MergeJournal.Phases.Complete)
            {
                if (!SuppressLogging)
                    ParsekLog.Error("Rewind",
                        $"Cannot rewind during an active re-fly merge — finish the merge first " +
                        $"(journalPhase={scenarioForJournalCheck.ActiveMergeJournal.Phase})");
                ParsekLog.ScreenMessage("Cannot rewind during an active re-fly merge", 3f);
                return;
            }

            // A plain rewind taken while a Re-Fly session is live ends that session here, before
            // the rewind is armed and before the load (operator ruling 2026-09-24; the reasons
            // it cannot wait for the rewind's OnLoad are on the callee).
            MergeDialog.TryDiscardLiveReFlySessionForRewind(
                ParsekFlight.Instance?.ActiveTreeForSerialization, "Rewind",
                out string droppedLiveTreeId, out bool droppedTreeRestored);

            BeginRewindForOwner(owner);

            // Shared load: pre-process (strip + lead-time windback) keyed by the owner, and
            // log the owner tree's supersede rows the rewind keeps.
            bool loaded = ExecuteRewindSaveLoad(
                owner.RewindSaveFileName,
                preProcessOwner: owner,
                supersedeAuditOwner: owner,
                messageLabel: "Rewind");
            if (!loaded)
                UndoLiveTreeDropAfterFailedRewind(droppedLiveTreeId, droppedTreeRestored, "Rewind");
        }

        /// <summary>
        /// A rewind whose load failed stays in the flight scene. When ending the live Re-Fly
        /// session armed the scene exit to drop the live tree, disarm it, and when the tree was
        /// also put back in the committed store take it out again, so the flight keeps one live
        /// tree (minus the discarded attempt) instead of a tree that is live and committed at
        /// once. The session itself stays ended.
        /// </summary>
        internal static void UndoLiveTreeDropAfterFailedRewind(
            string treeId, bool restoredToCommitted, string label)
        {
            if (string.IsNullOrEmpty(treeId))
                return;
            PendingTreeSlot.TryConsumeNextTreeSceneExitCommitSuppression(GameScenes.FLIGHT, out _);
            bool removed = restoredToCommitted
                && RemoveCommittedTreeById(treeId, $"{label} load failed");
            ParsekLog.Warn("Rewind",
                $"{label} load failed after ending the live Re-Fly session: tree={treeId} " +
                $"returned to the flight scene (removedFromCommitted={removed}); the session stays ended");
        }

        // internal (not private) so RewindLoggingTests can drive the real
        // baseline-argument selection instead of re-issuing BeginRewind inline.
        internal static void BeginRewindForOwner(Recording owner)
        {
            var reserved = new BudgetSummary
            {
                reservedFunds = owner.RewindReservedFunds,
                reservedScience = owner.RewindReservedScience,
                reservedReputation = owner.RewindReservedRep
            };

            // Baseline resources from the owner's pre-launch snapshot.
            // The rewind save was captured at the same moment as PreLaunch values.
            RewindContext.BeginRewind(owner.StartUT, reserved,
                owner.PreLaunchFunds, owner.PreLaunchScience, owner.PreLaunchReputation);
            SceneEntryVesselScope.SetRewindReplayTargetScope(owner);

            if (!SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"Rewind-to-Launch initiated to UT {owner.StartUT} " +
                    $"(save: {owner.RewindSaveFileName}). Plain launch rewind via parsek_rw_* quicksave; " +
                    $"this is NOT a Re-Fly (no RewindPoint / ReFlySessionMarker / MergeJournal)");
        }

        /// <summary>
        /// Shared load mechanics for both rewind entry points (<see cref="InitiateRewind"/>
        /// and <see cref="InitiateRewindToCareerStart"/>). Copies the Parsek/Saves snapshot to
        /// the root saves dir (KSP's LoadGame cannot read subdirectory paths), optionally
        /// pre-processes it, loads it via <see cref="GamePersistence.LoadGame"/>, deletes the
        /// temp copy, captures the adjusted UT, optionally drops supersede relations rewound
        /// out of existence, swaps in the loaded game, and loads the Space Center. On any
        /// failure resets the rewind flags and removes the temp copy. Returns true when the
        /// scene load was initiated.
        ///
        /// <para>Callers own the precondition gates (merge-journal refusal, supersede-count
        /// refusal) and the rewind-context setup (<see cref="BeginRewindForOwner"/> vs the
        /// UT-0 reset) BEFORE calling this; this helper is purely the mechanical save-copy /
        /// load / scene-swap wrapper that the two entry points used to duplicate.</para>
        /// </summary>
        /// <param name="saveFileName">Parsek/Saves snapshot base name (no .sfs extension).</param>
        /// <param name="preProcessOwner">When non-null, runs <see cref="PreProcessRewindSave"/>
        /// with that owner's strip set plus the rewind-to-launch lead-time windback. Null skips
        /// pre-processing (the pristine career-start snapshot, which must not be wound back
        /// below UT 0).</param>
        /// <param name="supersedeAuditOwner">When non-null, logs the supersede rows of that
        /// owner's tree the rewind reaches once the adjusted UT is known; every row is kept.
        /// Null skips the audit (the career-start path refuses up front when any supersede
        /// relation exists).</param>
        /// <param name="messageLabel">Message-text prefix for the adjusted-UT / failure logs
        /// ("Rewind" vs "Warp-to-game-start"); the subsystem tag stays "Rewind" either way.</param>
        private static bool ExecuteRewindSaveLoad(
            string saveFileName,
            Recording preProcessOwner,
            Recording supersedeAuditOwner,
            string messageLabel)
        {
            string tempCopyName = null;
            double rewindSaveOwnUT = double.NaN;
            // The OnLoad this load leads to does NOT read the rewind save's scenario node:
            // SpaceCenterMain.Start reloads persistent.sfs from disk, so the RP list it
            // restores is whatever the last persistent write held. Carry the in-memory
            // list across instead (see ReinstallRewindCarriedRewindPointsAfterLoad).
            CaptureRewindPointsForRewind(ParsekScenario.Instance, messageLabel);
            try
            {
                string savesDir = Path.Combine(
                    KSPUtil.ApplicationRootPath ?? "",
                    "saves",
                    HighLogic.SaveFolder ?? "");

                string sourcePath = Path.Combine(savesDir,
                    RecordingPaths.BuildRewindSaveRelativePath(saveFileName));
                tempCopyName = saveFileName;
                string tempPath = Path.Combine(savesDir, tempCopyName + ".sfs");

                // Copy from Parsek/Saves/ to the root saves dir so LoadGame can find it.
                File.Copy(sourcePath, tempPath, true);

                if (preProcessOwner != null)
                {
                    // Pre-process the save file before KSP parses it:
                    // 1. Remove recorded vessel + any EVA child vessels (other vessels stay intact)
                    // 2. Wind back UT by the rewind-to-launch lead time so the player can
                    //    regain control on the pad before launch.
                    // The owner strip removes the owner's own vessel by name AND launch guid,
                    // so an earlier launch of the same craft still standing in the quicksave
                    // (same name, different guid) is kept as committed history.
                    var ownerStrip = RewindSpawnScope.BuildRewindOwnerStrip(preProcessOwner);
                    // Collect spawned vessel PIDs for PID-based stripping (belt-and-suspenders
                    // alongside name matching, catches renamed vessels or debris)
                    // The PID set is scoped to vessels a replaying recording will re-produce:
                    // committed history the rewind keeps (an earlier tree's spawned vessel)
                    // stays in the save and therefore in the quicksave whitelist.
                    RewindContext.SetHistoricalSpawnKeepRecordingIds(null);
                    rewindSaveOwnUT = PreProcessRewindSave(
                        tempPath, ownerStrip, RewindSpawnScope.ResolveRewindStripSpawnedPids,
                        RewindToLaunchLeadTimeSeconds, RewindSpawnScope.ResolveRewindClaimedVesselPids);
                }

                // Parsek's time travel keeps the current difficulty settings (owner ruling Q1):
                // captured before the load, installed before the game is written or started.
                ConfigNode liveParameters = DifficultySettingsCarry.CaptureLive(messageLabel);
                Game game = GamePersistence.LoadGame(tempCopyName, HighLogic.SaveFolder, true, false);

                // Delete the temp copy (file already parsed into Game object)
                TryDeleteFileQuietly(tempPath);

                if (game == null)
                {
                    ResetRewindFlags();
                    if (!SuppressLogging)
                        ParsekLog.Error("Rewind",
                            $"{messageLabel} failed: LoadGame returned null for save '{saveFileName}'. " +
                            $"Flags reset: IsRewinding={IsRewinding}, RewindUT={RewindUT}, RewindAdjustedUT={RewindAdjustedUT}");
                    return false;
                }

                // Capture the adjusted UT from the (possibly preprocessed) save.
                RewindContext.SetAdjustedUT(game.flightState.universalTime);

                if (!SuppressLogging)
                    ParsekLog.Info("Rewind",
                        $"{messageLabel}: adjustedUT={RewindAdjustedUT:F1}, " +
                        $"rewindUT={RewindUT:F1}, flags=[IsRewinding={IsRewinding}]");

                // A rewind moves the clock and keeps every supersede row of the rewound tree
                // (owner ruling 2026-10-24): log what it keeps. Runs AFTER SetAdjustedUT so the
                // scope reads the post-load UT.
                if (supersedeAuditOwner != null)
                    LogSupersedesKeptAcrossRewind(supersedeAuditOwner, RewindAdjustedUT);

                // Same stale-persistent reason as the RP capture above, for the supersede,
                // rewind-retirement and tombstone lists and the merge journal.
                CaptureRewindStagedListsForRewind(ParsekScenario.Instance, messageLabel);

                // Same reason, for the rewind save's own route copy: OnLoad will read persistent's
                // ROUTES, of unknown age, so the go-back loop-position restore in
                // HandleRewindOnLoad reads this save's from the parsed game's scenario protos,
                // which still hold the file's nodes. That copy is as of the save's own UT, before
                // the lead-time windback the parsed clock already carries, and the go-back route
                // reconcile keys its cutoff to it.
                Logistics.RouteLoadReconcile.CaptureRewindSaveRoutes(game.scenarios,
                    Logistics.RouteLoadReconcile.ResolveRewindSaveOwnUT(
                        rewindSaveOwnUT, game.flightState.universalTime),
                    messageLabel);

                // The ledger held-through UT the rewind save carries (a save written inside an
                // earlier Rewind-to-Launch lead window): its world is as of that UT, so the new
                // floor HandleRewindOnLoad sets rises to it (LedgerHeldWindow.ResolveAtRewind).
                RewindContext.SetRewindSaveLedgerHeldThroughUT(
                    LedgerHeldWindow.ReadSavedFromScenarios(game.scenarios));

                // The replay-scope latch the rewind save carries: a recording that replayed when
                // that save was written replays again from its UT, even after a load since
                // reset memory's latch (archived todo REPLAY-SCOPE-LOST-ON-SAVE-LOAD). Memory's is kept.
                // PreProcessRewindSave already merged it before its strip; this covers the
                // un-preprocessed path and is a no-op otherwise.
                PlaybackScopeTracker.UnionFromScenarios(game.scenarios, messageLabel);

                DifficultySettingsCarry.ApplyTo(game, liveParameters, messageLabel);

                // The Space Center start reloads persistent.sfs, not CurrentGame: put the rewind
                // game there so the rewind lands in the rewind save's own world.
                WriteRewindGameAsPersistent(game, messageLabel);

                HighLogic.CurrentGame = game;
                HighLogic.LoadScene(GameScenes.SPACECENTER);

                if (!SuppressLogging)
                    ParsekLog.Info("Rewind",
                        $"{messageLabel}: loading save '{saveFileName}' into SpaceCenter");
                return true;
            }
            catch (Exception ex)
            {
                ResetRewindFlags();

                DeleteTemporaryRewindSaveCopy(tempCopyName);

                if (!SuppressLogging)
                    ParsekLog.Error("Rewind",
                        $"{messageLabel} failed: {ex.Message}. " +
                        $"Flags reset: IsRewinding={IsRewinding}, RewindUT={RewindUT}, RewindAdjustedUT={RewindAdjustedUT}");
                return false;
            }
        }

        /// <summary>
        /// Resets the game to its career-start snapshot (UT 0): reloads the pristine
        /// snapshot captured at career creation, restoring initial resources/facilities/clock
        /// while KEEPING the in-memory recordings as future ghosts (the OnLoad rewind branch
        /// skips the .sfs recording reload). Used by "Warp to time" for targets at/before the
        /// first launch so Year 1 / Day 1 is a true reset rather than landing at the earliest
        /// launch. Mirrors <see cref="InitiateRewind"/> minus the strip / lead-time windback
        /// (the snapshot is pristine) and minus owner-specific budget / replay scope.
        ///
        /// <para>Refuses when re-fly supersede relations exist: a UT-0 reset would otherwise
        /// leave superseded originals hidden. The caller selects the earliest-launch rewind
        /// path instead in that case (the owner-keyed rewind is the tested route).</para>
        /// </summary>
        internal static bool InitiateRewindToCareerStart(string saveFileName)
        {
            if (string.IsNullOrEmpty(saveFileName))
            {
                if (!SuppressLogging)
                    ParsekLog.Error("Rewind", "InitiateRewindToCareerStart: null/empty save name");
                return false;
            }

            var scenario = ParsekScenario.Instance;
            if (scenario?.ActiveMergeJournal != null
                && scenario.ActiveMergeJournal.Phase != MergeJournal.Phases.Complete)
            {
                if (!SuppressLogging)
                    ParsekLog.Error("Rewind",
                        $"Cannot warp to game start during an active re-fly merge " +
                        $"(journalPhase={scenario.ActiveMergeJournal.Phase})");
                ParsekLog.ScreenMessage("Cannot warp to game start during an active re-fly merge", 3f);
                return false;
            }

            int supersedeCount = scenario?.RecordingSupersedes?.Count ?? 0;
            if (supersedeCount > 0)
            {
                if (!SuppressLogging)
                    ParsekLog.Warn("Rewind",
                        $"InitiateRewindToCareerStart refused: {supersedeCount} supersede relation(s) present " +
                        "(UT-0 reset would hide superseded originals); caller should use earliest-launch rewind");
                return false;
            }

            // Same live Re-Fly session end as InitiateRewind.
            MergeDialog.TryDiscardLiveReFlySessionForRewind(
                ParsekFlight.Instance?.ActiveTreeForSerialization, "Warp-to-game-start",
                out string droppedLiveTreeId, out bool droppedTreeRestored);

            // UT-0 reset: no owner, no reserved budget, no baseline. ApplyRewindResourceAdjustment
            // recalcs the ledger at the adjusted UT (~0), which restores pristine career resources.
            RewindContext.BeginRewind(0.0, default(BudgetSummary), 0, 0, 0f);
            RewindContext.SetQuicksaveVesselPids(null);   // no PreProcessRewindSave -> no whitelist
            SceneEntryVesselScope.ClearRewindReplayTargetScope();               // no specific owner to replay-scope
            if (!SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"Warp-to-game-start initiated to UT 0 (snapshot: {saveFileName}). Keeps in-memory " +
                    "recordings as future ghosts; NOT a Re-Fly.");

            // Pristine snapshot: no PreProcessRewindSave (no future vessels to strip, and it
            // must NOT be wound back below UT 0) and no supersede audit (refused above when any
            // supersede relation exists).
            bool loaded = ExecuteRewindSaveLoad(
                saveFileName,
                preProcessOwner: null,
                supersedeAuditOwner: null,
                messageLabel: "Warp-to-game-start");
            if (!loaded)
                UndoLiveTreeDropAfterFailedRewind(droppedLiveTreeId, droppedTreeRestored, "Warp-to-game-start");
            return loaded;
        }

        /// <summary>
        /// Looks up a committed recording by id. Returns null when the id is null/empty
        /// or no committed recording matches. O(N); used by post-LoadScene rewind
        /// hooks where the owner reference doesn't survive but its id does
        /// (<see cref="SceneEntryVesselScope.RewindReplayTargetRecordingId"/>).
        /// </summary>
        internal static Recording TryFindCommittedRecordingById(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var rec = committedRecordings[i];
                if (rec == null) continue;
                if (string.Equals(rec.RecordingId, recordingId, StringComparison.Ordinal))
                    return rec;
            }
            return null;
        }

        internal static bool IsCommittedRecordingId(string recordingId)
        {
            return TryFindCommittedRecordingById(recordingId) != null;
        }

        /// <summary>
        /// True when the in-memory committed store holds a tree with this id. A pending
        /// tree that shares a committed tree's id is a copy-on-write restore copy of it
        /// (tree ids are fresh Guids, so only a clone can collide), and the committed
        /// original is still the durable holder of its history.
        /// </summary>
        internal static bool HasCommittedTreeWithId(string treeId)
            => FindCommittedTreeById(treeId) != null;

        /// <summary>The committed tree with this id, or null.</summary>
        internal static RecordingTree FindCommittedTreeById(string treeId)
        {
            if (string.IsNullOrEmpty(treeId))
                return null;
            for (int i = 0; i < committedTrees.Count; i++)
            {
                if (string.Equals(committedTrees[i]?.Id, treeId, StringComparison.Ordinal))
                    return committedTrees[i];
            }
            return null;
        }

        // ==================================================================
        // Durable committed-recording-id hint (committed-overlap discard guard)
        // ==================================================================

        /// <summary>
        /// Recording ids the SAVE FILE declares as committed, used only while the
        /// in-memory committed store has not been populated yet.
        /// </summary>
        private static HashSet<string> durableCommittedRecordingIdHint;

        /// <summary>
        /// Publishes the committed recording ids read straight out of the save node,
        /// for the window in <c>ParsekScenario.OnLoad</c> between the stale-pending
        /// discard and <c>LoadRecordingTrees</c>.
        ///
        /// <para><b>Why this exists (data-loss fix, found 2026-08-28).</b>
        /// <see cref="DiscardPendingTree"/> refuses to delete a pending recording's
        /// sidecars when the same id is still committed (#431) - the guard that makes
        /// the copy-on-write committed-tree restore
        /// (<c>ParsekFlight.TryTakeCommittedTreeForSpawnedVesselRestore</c>, which
        /// clones a committed tree into the ACTIVE slot with ids PRESERVED) safe to
        /// throw away. On a COLD load that guard was answered against an EMPTY store:
        /// <c>DiscardStalePendingState()</c> runs BEFORE <c>LoadRecordingTrees</c>, so
        /// every id read as "not committed" and the discard deleted the sidecars of the
        /// COMMITTED tree the pending tree was cloned from. The committed recordings
        /// then loaded as <c>trajectory-missing</c> forever and the save silently
        /// dropped the whole tree. The save node is the authority in that window, so
        /// the guard consults it.</para>
        /// </summary>
        internal static void SetDurableCommittedRecordingIdHint(HashSet<string> recordingIds)
        {
            durableCommittedRecordingIdHint =
                recordingIds != null && recordingIds.Count > 0 ? recordingIds : null;
            ParsekLog.Verbose("RecordingStore",
                $"Durable committed-recording-id hint armed: ids={(durableCommittedRecordingIdHint?.Count ?? 0)}");
        }

        /// <summary>Drops the hint once the in-memory committed store is authoritative again.</summary>
        internal static void ClearDurableCommittedRecordingIdHint()
        {
            if (durableCommittedRecordingIdHint == null)
                return;
            ParsekLog.Verbose("RecordingStore",
                $"Durable committed-recording-id hint cleared (was {durableCommittedRecordingIdHint.Count} id(s))");
            durableCommittedRecordingIdHint = null;
        }

        internal static bool IsDurableCommittedRecordingIdHint(string recordingId)
        {
            return !string.IsNullOrEmpty(recordingId)
                && durableCommittedRecordingIdHint != null
                && durableCommittedRecordingIdHint.Contains(recordingId);
        }

        /// <summary>
        /// Resolves a committed recording id to its display fields for the Logistics
        /// window (H5: names instead of 8-char GUID fragments). Returns false when the
        /// id is null/empty or not in the committed store; on success
        /// <paramref name="recordingName"/> is the recording's display name
        /// ("Untitled" when the vessel name is empty, matching the Recordings table),
        /// <paramref name="treeName"/> is the owning tree/mission name (null for a
        /// standalone recording with no tree), and <paramref name="treeOrder"/> is the
        /// 0-based persisted position within the tree (-1 when unassigned).
        /// <para>
        /// This is the literal-free by-id accessor the Logistics window calls so it
        /// never references <c>committedRecordings</c> directly and the ERS/ELS grep
        /// gate stays green: the raw-list reads stay behind
        /// <see cref="TryFindCommittedRecordingById"/> and
        /// <see cref="TryResolveTreeById"/> in this already-allowlisted file.
        /// <paramref name="treeName"/> is the tree's display name
        /// (<see cref="MissionStore.DisplayNameForTree(RecordingTree)"/>): its original
        /// mission's current name, else <see cref="RecordingTree.TreeName"/>, the raw launch
        /// name. A mission rename renames the groups and <c>Mission.Name</c>, never
        /// <c>TreeName</c>, so the log line below keeps printing the raw TreeName.
        /// </para>
        /// </summary>
        internal static bool TryResolveRecordingDisplayInfo(
            string recordingId,
            out string recordingName,
            out string treeName,
            out int treeOrder)
        {
            return TryResolveRecordingDisplayInfo(recordingId, out recordingName, out treeName,
                out treeOrder, out _, out _);
        }

        /// <summary>
        /// <see cref="TryResolveRecordingDisplayInfo(string, out string, out string, out int)"/>
        /// that also hands back the committed recording and its owning tree (null for a
        /// standalone recording), so the Logistics "Flights used" line can number the
        /// recording's vessel the way the Missions tab does (<see cref="MissionVesselNaming"/>).
        /// </summary>
        internal static bool TryResolveRecordingDisplayInfo(
            string recordingId,
            out string recordingName,
            out string treeName,
            out int treeOrder,
            out Recording recording,
            out RecordingTree owningTree)
        {
            recordingName = null;
            treeName = null;
            treeOrder = -1;
            recording = null;
            owningTree = null;

            Recording rec = TryFindCommittedRecordingById(recordingId);
            if (rec == null)
            {
                ParsekLog.Verbose("RecordingStore",
                    $"Logistics display resolve id={Shorten(recordingId)} resolved=false (not committed)");
                return false;
            }

            recordingName = string.IsNullOrEmpty(rec.VesselName) ? "Untitled" : rec.VesselName;
            treeOrder = rec.TreeOrder;
            string rawTreeName = null;

            if (!string.IsNullOrEmpty(rec.TreeId)
                && TryResolveTreeById(rec.TreeId, out RecordingTree tree, out _)
                && tree != null)
            {
                rawTreeName = tree.TreeName;
                treeName = MissionStore.DisplayNameForTree(tree);
                owningTree = tree;
            }
            recording = rec;

            ParsekLog.Verbose("RecordingStore",
                $"Logistics display resolve id={Shorten(recordingId)} resolved=true "
                + $"name='{recordingName}' tree='{rawTreeName ?? "<none>"}' order={treeOrder.ToString(CultureInfo.InvariantCulture)}");
            return true;
        }

        private static string Shorten(string id)
        {
            if (string.IsNullOrEmpty(id)) return "<none>";
            return id.Length > 8 ? id.Substring(0, 8) : id;
        }

        // ==================================================================
        // Rewind points carried across a plain rewind (operator ruling 2026-09-23)
        // ==================================================================

        /// <summary>
        /// The in-memory <see cref="ParsekScenario.RewindPoints"/> list captured when a plain
        /// rewind (Rewind-to-Launch or Warp-to-game-start) starts its load. Null when no
        /// rewind is in flight.
        ///
        /// <para><b>Why it exists.</b> A rewind point always survives a plain rewind: its
        /// slots stay in Unfinished Flights, and <see cref="RewindInvoker.CanInvoke"/> keeps
        /// the Re-Fly disabled until the clock reaches the RP's UT again. The recordings the
        /// slots point at are kept from memory across the rewind, but the RP list was not:
        /// the rewind's scene load goes to the Space Center, whose
        /// <c>SpaceCenterMain.Start</c> (KSP 1.12.5, decompiled) calls
        /// <c>GamePersistence.LoadGame("persistent")</c> and loads THAT game, so
        /// <c>ParsekScenario.OnLoad</c> rebuilt the list from whatever
        /// <c>persistent.sfs</c> last held on disk. An RP created after the last persistent
        /// write vanished (its quicksave file leaked on disk, since the reaper only walks
        /// the list), an RP the reaper had already removed could come back, and which of the
        /// two happened depended on the scenes the player had passed through.</para>
        /// </summary>
        private static List<RewindPoint> rewindCarriedRewindPoints;

        /// <summary>True while a captured RP list is waiting for the rewind's OnLoad.</summary>
        internal static bool HasRewindCarriedRewindPoints => rewindCarriedRewindPoints != null;

        /// <summary>
        /// Captures a shallow copy of the scenario's RP list for the rewind that is about to
        /// load. A null scenario captures nothing, which leaves the post-load list to the
        /// loaded save exactly as before.
        /// </summary>
        internal static void CaptureRewindPointsForRewind(ParsekScenario scenario, string label)
        {
            if (object.ReferenceEquals(null, scenario) || scenario.RewindPoints == null)
            {
                rewindCarriedRewindPoints = null;
                if (!SuppressLogging)
                    ParsekLog.Info("Rewind",
                        $"{label}: no live scenario RP list to carry across the rewind load");
                return;
            }

            rewindCarriedRewindPoints = new List<RewindPoint>(scenario.RewindPoints.Count);
            for (int i = 0; i < scenario.RewindPoints.Count; i++)
            {
                if (scenario.RewindPoints[i] != null)
                    rewindCarriedRewindPoints.Add(scenario.RewindPoints[i]);
            }
            if (!SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"{label}: carrying {rewindCarriedRewindPoints.Count} rewind point(s) across the rewind load " +
                    $"[{FormatRewindPointIds(rewindCarriedRewindPoints)}]");
        }

        /// <summary>Drops a captured RP list that no rewind OnLoad will consume.</summary>
        internal static void ClearRewindCarriedRewindPoints(string reason)
        {
            if (rewindCarriedRewindPoints == null)
                return;
            if (!SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"Dropped {rewindCarriedRewindPoints.Count} carried rewind point(s) without reinstalling them " +
                    $"(reason={reason})");
            rewindCarriedRewindPoints = null;
        }

        /// <summary>
        /// Pure merge for the rewind OnLoad: the carried in-memory list is authoritative and
        /// replaces the loaded one wholesale, the same way the rewind branch keeps the
        /// in-memory recordings instead of the save's. The two counts only describe how far
        /// the loaded save had drifted: <paramref name="restoredCount"/> carried RPs the save
        /// lacked, <paramref name="staleDroppedCount"/> loaded RPs memory no longer holds.
        /// </summary>
        internal static List<RewindPoint> MergeCarriedRewindPoints(
            IReadOnlyList<RewindPoint> carried,
            IReadOnlyList<RewindPoint> loaded,
            out int restoredCount,
            out int staleDroppedCount)
        {
            var loadedIds = new HashSet<string>(StringComparer.Ordinal);
            if (loaded != null)
            {
                for (int i = 0; i < loaded.Count; i++)
                {
                    if (loaded[i]?.RewindPointId != null)
                        loadedIds.Add(loaded[i].RewindPointId);
                }
            }

            var result = new List<RewindPoint>();
            var carriedIds = new HashSet<string>(StringComparer.Ordinal);
            restoredCount = 0;
            if (carried != null)
            {
                for (int i = 0; i < carried.Count; i++)
                {
                    var rp = carried[i];
                    if (rp == null) continue;
                    result.Add(rp);
                    if (rp.RewindPointId == null) continue;
                    carriedIds.Add(rp.RewindPointId);
                    if (!loadedIds.Contains(rp.RewindPointId))
                        restoredCount++;
                }
            }

            staleDroppedCount = 0;
            foreach (string id in loadedIds)
            {
                if (!carriedIds.Contains(id))
                    staleDroppedCount++;
            }
            return result;
        }

        /// <summary>
        /// Rewind OnLoad half of the RP carry-over: replaces the just-loaded
        /// <see cref="ParsekScenario.RewindPoints"/> with the list captured when the rewind
        /// started, then consumes the capture. Called from <c>ParsekScenario.OnLoad</c>
        /// right after <c>LoadRewindStagingState</c>. A load that is not a rewind
        /// leaves the loaded list alone and drops any stranded capture. Returns the number
        /// of RPs installed, or -1 when nothing was installed.
        /// </summary>
        internal static int ReinstallRewindCarriedRewindPointsAfterLoad(ParsekScenario scenario)
        {
            if (rewindCarriedRewindPoints == null)
                return -1;
            if (!RewindContext.IsRewinding)
            {
                ClearRewindCarriedRewindPoints("load-is-not-a-rewind");
                return -1;
            }
            if (object.ReferenceEquals(null, scenario))
            {
                ClearRewindCarriedRewindPoints("no-scenario-at-rewind-load");
                return -1;
            }

            var carried = rewindCarriedRewindPoints;
            rewindCarriedRewindPoints = null;
            int loadedCount = scenario.RewindPoints?.Count ?? 0;
            scenario.RewindPoints = MergeCarriedRewindPoints(
                carried, scenario.RewindPoints, out int restored, out int staleDropped);
            if (!SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"RewindPoints carried across rewind: installed={scenario.RewindPoints.Count} " +
                    $"loadedFromSave={loadedCount} restored={restored} staleDropped={staleDropped} " +
                    $"[{FormatRewindPointIds(scenario.RewindPoints)}]");
            return scenario.RewindPoints.Count;
        }

        private static string FormatRewindPointIds(IReadOnlyList<RewindPoint> rps)
        {
            if (rps == null || rps.Count == 0)
                return "";
            var sb = new StringBuilder();
            int shown = 0;
            for (int i = 0; i < rps.Count; i++)
            {
                if (rps[i] == null) continue;
                if (shown == 8)
                {
                    sb.Append(", ...");
                    break;
                }
                if (shown > 0) sb.Append(", ");
                sb.Append(rps[i].RewindPointId ?? "<no-id>");
                sb.Append("@ut=");
                sb.Append(rps[i].UT.ToString("R", CultureInfo.InvariantCulture));
                shown++;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Live audit of the supersede rows a plain rewind of <paramref name="owner"/>
        /// reaches. Resolves the owner's tree and the committed recordings, delegates to
        /// <see cref="AuditSupersedesKeptAcrossRewindPure"/> and logs what it found. Changes
        /// nothing: a rewind moves the clock and keeps every supersede row, so a sealed
        /// re-fly stays sealed, an open one stays open, and a row whose fork failed to load
        /// keeps hiding the flight it replaced (design section 2.2; owner ruling 2026-10-24).
        /// Returns the number of rows in scope.
        /// </summary>
        internal static int LogSupersedesKeptAcrossRewind(Recording owner, double rewindAdjustedUT)
        {
            if (owner == null) return 0;
            var scenario = ParsekScenario.Instance;
            if (scenario?.RecordingSupersedes == null
                || scenario.RecordingSupersedes.Count == 0)
                return 0;

            List<Recording> ownerTreeRecordings = null;
            if (!string.IsNullOrEmpty(owner.TreeId))
            {
                for (int t = 0; t < committedTrees.Count; t++)
                {
                    var tree = committedTrees[t];
                    if (tree == null || tree.Recordings == null) continue;
                    if (!string.Equals(tree.Id, owner.TreeId, StringComparison.Ordinal)) continue;
                    ownerTreeRecordings = new List<Recording>(tree.Recordings.Count);
                    foreach (var kvp in tree.Recordings)
                    {
                        if (kvp.Value != null)
                            ownerTreeRecordings.Add(kvp.Value);
                    }
                    break;
                }
            }

            var liveById = new Dictionary<string, Recording>(StringComparer.Ordinal);
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var liveRec = committedRecordings[i];
                if (liveRec != null && !string.IsNullOrEmpty(liveRec.RecordingId))
                    liveById[liveRec.RecordingId] = liveRec;
            }

            RewindSupersedeAudit audit = AuditSupersedesKeptAcrossRewindPure(
                owner, rewindAdjustedUT, ownerTreeRecordings, liveById, scenario.RecordingSupersedes);

            if (SuppressLogging)
                return audit.Rows;

            string ut = rewindAdjustedUT.ToString("F1", CultureInfo.InvariantCulture);
            if (audit.Rows == 0)
            {
                ParsekLog.Verbose("Rewind",
                    $"Rewind keeps supersede rows: none in scope rewindUT={ut} owner='{owner.VesselName}' " +
                    $"treeResolved={(ownerTreeRecordings != null).ToString()}");
                return 0;
            }

            ParsekLog.Info("Rewind",
                $"Rewind keeps supersede rows: rows={audit.Rows.ToString(CultureInfo.InvariantCulture)} " +
                $"forks={audit.Forks.ToString(CultureInfo.InvariantCulture)} " +
                $"sealed={audit.Sealed.ToString(CultureInfo.InvariantCulture)} " +
                $"open={audit.Open.ToString(CultureInfo.InvariantCulture)} " +
                $"dangling={audit.Dangling.ToString(CultureInfo.InvariantCulture)} " +
                $"rewindUT={ut} owner='{owner.VesselName}'");

            // One line per distinct fork; a rewound tree carries a handful at most.
            for (int i = 0; i < audit.ForkIds.Count; i++)
            {
                string forkId = audit.ForkIds[i];
                liveById.TryGetValue(forkId, out Recording forkRec);
                ParsekLog.Info("Rewind",
                    $"Kept re-fly fork across rewind: rec={forkId} " +
                    $"mergeState={(forkRec != null ? forkRec.MergeState.ToString() : "<missing>")} " +
                    $"rewindUT={ut} owner='{owner.VesselName}'");
            }
            return audit.Rows;
        }

        private static void DeleteTemporaryRewindSaveCopy(string tempCopyName)
        {
            // Clean up temp copy on failure
            if (tempCopyName == null)
                return;

            try
            {
                string savesDir = Path.Combine(
                    KSPUtil.ApplicationRootPath ?? "",
                    "saves",
                    HighLogic.SaveFolder ?? "");
                string tempPath = Path.Combine(savesDir, tempCopyName + ".sfs");
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch { }
        }

        private static void TryDeleteFileQuietly(string path)
        {
            try { File.Delete(path); }
            catch { }
        }

        /// <summary>
        /// Modifies the temp rewind save file before KSP parses it:
        /// removes the recorded vessel and winds back UT so the player
        /// has time to reach the launch pad before the ghost appears.
        /// </summary>
        internal static void PreProcessRewindSave(string sfsPath, string vesselName, double leadTime)
        {
            PreProcessRewindSave(sfsPath, new HashSet<string> { vesselName }, leadTime);
        }

        internal static void PreProcessRewindSave(string sfsPath, HashSet<string> vesselNames, double leadTime)
        {
            PreProcessRewindSave(sfsPath, vesselNames, (HashSet<uint>)null, leadTime);
        }

        /// <summary>
        /// Extended overload that strips vessels by both name AND persistent ID.
        /// PID matching catches renamed vessels or debris that name matching misses.
        /// The .sfs ConfigNode reliably has "persistentId" as a text field.
        /// </summary>
        internal static void PreProcessRewindSave(
            string sfsPath, HashSet<string> vesselNames, HashSet<uint> vesselPids, double leadTime)
        {
            PreProcessRewindSave(sfsPath, vesselNames, _ => vesselPids, leadTime);
        }

        /// <summary>
        /// Resolver overload: the PID strip set is resolved from the ADJUSTED (wound-back) UT,
        /// which is only known once the save's own UT is parsed. The plain rewind passes
        /// <see cref="RewindSpawnScope.ResolveRewindStripSpawnedPids"/> so a spawned vessel of committed history
        /// that no replaying recording will re-produce stays in the save (and so in the
        /// quicksave whitelist). The resolver receives NaN when the save's UT is missing or
        /// unparseable.
        /// </summary>
        internal static void PreProcessRewindSave(
            string sfsPath, HashSet<string> vesselNames,
            Func<double, HashSet<uint>> resolveVesselPids, double leadTime)
        {
            PreProcessRewindSave(
                sfsPath, RewindSpawnScope.RewindOwnerStrip.FromNames(vesselNames), resolveVesselPids, leadTime);
        }

        /// <summary>
        /// Owner-strip overload: the name half of the strip is identity-aware
        /// (<see cref="RewindSpawnScope.ClassifyRewindOwnerStripVessel"/>), so a vessel carrying the owner's name
        /// but a conclusively different launch guid (an earlier launch of the same craft) stays
        /// in the save and in the quicksave whitelist.
        /// Returns the save's own UT as written, before the lead-time windback (the moment every
        /// scenario node in the file describes), or NaN when the save has no parseable UT.
        /// </summary>
        internal static double PreProcessRewindSave(
            string sfsPath, RewindSpawnScope.RewindOwnerStrip ownerStrip,
            Func<double, HashSet<uint>> resolveVesselPids, double leadTime,
            Func<double, HashSet<uint>> resolveClaimedVesselPids = null)
        {
            if (ownerStrip == null)
                ownerStrip = new RewindSpawnScope.RewindOwnerStrip();
            ConfigNode root = ConfigNode.Load(sfsPath);
            if (root == null)
                return double.NaN;

            ConfigNode gameNode = root.HasNode("GAME") ? root.GetNode("GAME") : root;

            // The rewind save's replay-scope latch joins memory's BEFORE the strip resolvers run:
            // they decide which recordings replay after the rewind (WillReplayAfterRewind), and a
            // recording replaying when this save was written replays again from its UT (todo
            // REPLAY-SCOPE-LOST-ON-SAVE-LOAD).
            PlaybackScopeTracker.UnionFromGameNode(gameNode, "rewind save pre-process");

            ConfigNode flightState = gameNode.GetNode("FLIGHTSTATE");
            if (flightState == null)
            {
                if (!SuppressLogging)
                    ParsekLog.Warn("Rewind",
                        $"PreProcessRewindSave: no FLIGHTSTATE node in save '{sfsPath}'");
                root.Save(sfsPath);
                return double.NaN;
            }

            // Wind back UT
            string utStr = flightState.GetValue("UT");
            double ut;
            double adjustedUT = double.NaN;
            double saveOwnUT = double.NaN;
            if (!string.IsNullOrEmpty(utStr) &&
                double.TryParse(utStr, NumberStyles.Any, CultureInfo.InvariantCulture, out ut))
            {
                saveOwnUT = ut;
                double newUT = Math.Max(0, ut - leadTime);
                adjustedUT = newUT;
                flightState.SetValue("UT", newUT.ToString("R", CultureInfo.InvariantCulture));
                if (!SuppressLogging)
                    ParsekLog.Info("Rewind",
                        $"UT adjusted: {ut:F1} → {newUT:F1} (lead time {leadTime}s)");
            }
            else if (!SuppressLogging)
            {
                ParsekLog.Warn("Rewind",
                    $"PreProcessRewindSave: missing or invalid UT value '{utStr ?? "(null)"}' in FLIGHTSTATE");
            }

            // Remove vessels matching name OR persistentId
            HashSet<uint> vesselPids = resolveVesselPids != null
                ? resolveVesselPids(adjustedUT)
                : null;
            bool hasPids = vesselPids != null && vesselPids.Count > 0;
            int removedByName = 0;
            int removedByGuid = 0;
            int removedByPid = 0;
            int keptOtherLaunch = 0;
            var vesselNodes = flightState.GetNodes("VESSEL");
            var vesselGuids = new string[vesselNodes.Length];
            for (int g = 0; g < vesselNodes.Length; g++)
                vesselGuids[g] = VesselLaunchIdentity.TryReadVesselGuid(vesselNodes[g]);
            bool ownerAnchored = RewindSpawnScope.RewindOwnerStripIsAnchored(ownerStrip, vesselGuids);
            for (int i = vesselNodes.Length - 1; i >= 0; i--)
            {
                string name = Recording.ResolveLocalizedName(vesselNodes[i].GetValue("name"));
                RewindSpawnScope.RewindOwnerStripDecision ownerDecision = RewindSpawnScope.ClassifyRewindOwnerStripVessel(
                    name, vesselGuids[i], ownerStrip, ownerAnchored);
                if (ownerDecision == RewindSpawnScope.RewindOwnerStripDecision.StripName
                    || ownerDecision == RewindSpawnScope.RewindOwnerStripDecision.StripOwnerGuid)
                {
                    flightState.RemoveNode(vesselNodes[i]);
                    if (ownerDecision == RewindSpawnScope.RewindOwnerStripDecision.StripName)
                        removedByName++;
                    else
                        removedByGuid++;
                    continue;
                }

                if (ownerDecision == RewindSpawnScope.RewindOwnerStripDecision.KeepOtherLaunch)
                {
                    keptOtherLaunch++;
                    if (!SuppressLogging)
                        ParsekLog.Info("Rewind",
                            $"Rewind owner strip: keeping vessel '{name}' (pid={vesselNodes[i].GetValue("persistentId") ?? "(none)"}, " +
                            $"guid={vesselGuids[i]}) in rewind save: owner's name but a different launch " +
                            $"(owner guid={ownerStrip.OwnerGuid})");
                }

                if (hasPids)
                {
                    string pidStr = vesselNodes[i].GetValue("persistentId");
                    uint pid;
                    if (pidStr != null
                        && uint.TryParse(pidStr, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out pid)
                        && vesselPids.Contains(pid))
                    {
                        flightState.RemoveNode(vesselNodes[i]);
                        removedByPid++;
                    }
                }
            }

            if (!SuppressLogging)
            {
                string namesStr = string.Join(", ", ownerStrip.AllNames());
                ParsekLog.Info("Rewind",
                    $"Stripped {removedByName + removedByGuid + removedByPid} vessel(s) from save " +
                    $"({removedByName} by name [{namesStr}], {removedByGuid} by owner guid, " +
                    $"{removedByPid} by PID; kept {keptOtherLaunch} other launch(es) of the owner's craft)");
            }

            // Capture PIDs of surviving vessels in the quicksave.
            // Used by StripFuturePrelaunchVessels to whitelist known-good PRELAUNCH
            // vessels and strip only unknown ones that appeared after the rewind point.
            var survivingPids = new HashSet<uint>();
            var survivingNodes = flightState.GetNodes("VESSEL");
            for (int s = 0; s < survivingNodes.Length; s++)
            {
                uint spid;
                if (uint.TryParse(survivingNodes[s].GetValue("persistentId"), out spid))
                    survivingPids.Add(spid);
            }
            RewindContext.SetQuicksaveVesselPids(survivingPids.Count > 0 ? survivingPids : null);
            if (!SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"Captured {survivingPids.Count} surviving vessel PID(s) from quicksave");

            // The landing scene reloads persistent.sfs, so a vessel a committed future claims
            // lands in its latest (post-claim) form; keep the rewind save's own copy of each
            // claimed vessel for the ghost that stands in for it until the claim.
            if (resolveClaimedVesselPids != null)
            {
                HashSet<uint> claimedPids = resolveClaimedVesselPids(adjustedUT);
                int heldCopies = ClaimedVesselHoldGhost.CaptureRewindSaveVessels(flightState, claimedPids);
                if (!SuppressLogging)
                    ParsekLog.Info("Rewind",
                        $"Captured {heldCopies} rewind-save copy(ies) of {claimedPids?.Count ?? 0} " +
                        "vessel(s) a committed future claims");
            }

            root.Save(sfsPath);
            return saveOwnUT;
        }

        #endregion

        #region Trajectory Serialization

        internal static OrbitSegmentCheckpointBridgeStats EnsureCheckpointSectionsForTopLevelOrbitSegments(
            Recording rec,
            bool markDirty,
            string context,
            bool reconcileEmptySections = true,
            bool invalidateSectionAnnotations = true)
        {
            OrbitSegmentCheckpointBridgeStats stats =
                OrbitSegmentCheckpointBridge.EnsureCheckpointSectionsForTopLevelOrbitSegments(
                    rec, markDirty, reconcileEmptySections, invalidateSectionAnnotations);

            // AnyMutation, not Changed: a pure-re-sort pass leaves Changed false while
            // still renumbering sections and wiping CachedStats, and that is exactly
            // the mutation a markDirty:false caller has to know about.
            if (stats.AnyMutation && !SuppressLogging)
            {
                // reconcile=on/off distinguishes "empty-shell pass gated off (read
                // seam)" from "ran and found nothing" - both print
                // reconciledEmptySections=0 otherwise.
                ParsekLog.Verbose("RecordingStore",
                    $"EnsureCheckpointSectionsForTopLevelOrbitSegments: recording={rec?.RecordingId} " +
                    $"context={context} added={stats.Added} skippedExisting={stats.SkippedExisting} " +
                    $"skippedInvalid={stats.SkippedInvalid} skippedPredicted={stats.SkippedPredicted} " +
                    $"skippedAfterPredicted={stats.SkippedAfterPredicted} " +
                    $"skippedCovered={stats.SkippedCovered} clipped={stats.Clipped} " +
                    $"reconciledEmptySections={stats.ReconciledEmptySections} " +
                    $"resorted={stats.Resorted} markDirty={(markDirty ? "on" : "off")} " +
                    $"reconcile={(reconcileEmptySections ? "on" : "off")}");
            }

            return stats;
        }

        #endregion

        #region Crew End States Serialization

        #endregion

        #region Resource Manifest Serialization

        #endregion

        #region Recording File I/O

        internal static void ReconcileReadableSidecarMirrorsForKnownRecordings()
        {
            RecordingSidecarStore.ReconcileReadableSidecarMirrorsForKnownRecordings(
                committedRecordings, PendingTreeSlot.PendingTree);
        }

        #endregion
    }
}
