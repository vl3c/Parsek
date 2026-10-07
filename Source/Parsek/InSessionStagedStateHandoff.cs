using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek
{
    /// <summary>Which rule of the in-session rewind-point partition decided one point.</summary>
    internal enum RewindPointOwnerClass
    {
        /// <summary>A session-provisional point stamped with its creating Re-Fly session: kept
        /// from both sides, the in-memory instance first; <c>LoadTimeSweep</c> then spares or
        /// purges it against the loaded marker.</summary>
        SessionScoped,
        /// <summary>The point's branch point lives in a tree that memory holds committed after
        /// the load's active-tree detach: memory's copy wins.</summary>
        CommittedOwner,
        /// <summary>Any other owner (the resumed or reverted flight, an unknown tree): the
        /// loaded save's copy wins and memory-only points drop out of the list; their
        /// quicksave files stay on disk (owner ruling OQ-3).</summary>
        FollowSave,
    }

    /// <summary>Per-class counts of one <see cref="InSessionStagedStateHandoff.MergeRewindPointsByOwner"/> call.</summary>
    internal struct RewindPointPartitionCounts
    {
        internal int SessionScoped;
        internal int CommittedKept;
        internal int CommittedMemoryOnlyKept;
        internal int CommittedSaveOnlyDropped;
        internal int FollowSaveKept;
        internal int FollowSaveMemoryOnlyDropped;
    }

    /// <summary>Counts of one <see cref="InSessionStagedStateHandoff.ApplyResumedTreeRowRule{T}"/> call.</summary>
    internal struct ResumedTreeRowCounts
    {
        /// <summary>Rows in the result.</summary>
        internal int Installed;
        /// <summary>Rows that name no recording of the resumed tree: memory's, as step A installed them.</summary>
        internal int Untouched;
        /// <summary>Rows the resumed Re-Fly attempt wrote (owner ruling OQ-1): memory's copy kept.</summary>
        internal int AttemptKept;
        /// <summary>Rows naming the resumed tree that both sides hold: the save's copy.</summary>
        internal int FollowSave;
        /// <summary>Rows naming the resumed tree that only memory holds: dropped.</summary>
        internal int Dropped;
        /// <summary>Rows naming the resumed tree that only the save holds: put back.</summary>
        internal int RestoredFromSave;
    }

    /// <summary>
    /// The Re-Fly bookkeeping a <see cref="ParsekScenario"/> instance held when it was torn down
    /// (rewind points, supersede rows, rewind retirements, ledger tombstones, the merge journal and
    /// the Re-Fly marker), handed to the next <c>ParsekScenario.OnLoad</c> of the same session.
    ///
    /// <para>An in-session load keeps committed recordings and the ledger in memory, so the lists
    /// that index them must come from memory too, not from the loaded save (todo
    /// QUICKLOAD-REFLY-LISTS-REVERT-WHILE-RECORDINGS-STAY). KSP creates a new scenario instance per
    /// scene, so the in-memory lists are captured from the old instance: in
    /// <c>ParsekScenario.OnDestroy</c> (stock <c>ScenarioRunner.OnGameSceneLoadRequested</c>
    /// destroys every scenario module with <c>DestroyImmediate</c> when a scene load is requested,
    /// before the next scene's <c>Game.Load</c> creates the new instance), or in the new instance's
    /// <c>OnAwake</c> when the old one is still registered.</para>
    ///
    /// <para>Every OnLoad consumes it exactly once (<see cref="Take"/>) in its prologue. A cold load,
    /// a plain rewind (which owns <c>RecordingStore</c>'s rewind carries) and a Re-Fly start (which
    /// owns the reconciliation bundle) drop it; the main-menu transition, a save-folder change and
    /// the inert game-mode early return drop it too. Static state: one scene transition is in flight
    /// at a time.</para>
    /// </summary>
    internal static class InSessionStagedStateHandoff
    {
        internal const string Tag = "Rewind";

        internal const string CaptureReasonOnDestroy = "OnDestroy";
        internal const string CaptureReasonOnAwake = "OnAwake";

        internal const string DropColdLoad = "cold-load";
        internal const string DropRewindCarryOwns = "rewind-carry-owns";
        internal const string DropReFlyBundleOwns = "refly-bundle-owns";
        internal const string DropSaveFolderChanged = "save-folder-changed";
        internal const string DropMainMenu = "main-menu";
        internal const string DropInertGameMode = "inert-game-mode";
        /// <summary>An instance whose OnLoad never reached step A is torn down: the handoff it
        /// never took is older than that load, so it is dropped rather than handed to the next.</summary>
        internal const string DropStagedStateNotLoaded = "staged-state-not-loaded";

        /// <summary>Every reason a handoff can be dropped without being applied.</summary>
        internal static readonly IReadOnlyList<string> DropReasons = new[]
        {
            DropColdLoad, DropRewindCarryOwns, DropReFlyBundleOwns,
            DropSaveFolderChanged, DropMainMenu, DropInertGameMode, DropStagedStateNotLoaded,
        };

        /// <summary>Shallow copies of the scenario's staged lists plus its two singletons.</summary>
        internal sealed class Snapshot
        {
            internal List<RewindPoint> RewindPoints;
            internal List<RecordingSupersedeRelation> Supersedes;
            internal List<RecordingRewindRetirement> Retirements;
            internal List<LedgerTombstone> Tombstones;
            internal MergeJournal Journal;
            internal ReFlySessionMarker Marker;
            internal string SaveFolder;
            internal string CaptureReason;
        }

        private static Snapshot pending;

        /// <summary>Test seam for the current save folder (<c>HighLogic.SaveFolder</c> holds no value headlessly).</summary>
        internal static Func<string> SaveFolderProviderForTesting;

        /// <summary>The save folder the capture stamps and step A compares against.</summary>
        internal static string CurrentSaveFolder()
        {
            var provider = SaveFolderProviderForTesting;
            return provider != null ? provider() : HighLogic.SaveFolder;
        }

        /// <summary>True while a captured handoff waits for the next OnLoad.</summary>
        internal static bool HasPending => pending != null;

        /// <summary>The <c>handoff=</c> token of the load-classification line.</summary>
        internal static string DescribeForClassification()
        {
            return pending == null ? "absent" : "present reason=" + (pending.CaptureReason ?? "<none>");
        }

        internal const string CaptureSkipInertGameMode = "inert-game-mode";
        internal const string CaptureSkipNoCompletedLoad = "no-completed-load";
        internal const string CaptureSkipStagedStateNotLoaded = "staged-state-not-loaded";

        /// <summary>
        /// Whether a scenario instance being torn down (or replaced) captures a handoff: only
        /// after this session completed a load (a cold load follows otherwise, and the main-menu
        /// transition resets the flag before stock destroys the scenario), never for an inert
        /// (mission / scenario game) instance, and only when this instance's own OnLoad got
        /// through the staging load and step A (<paramref name="stagedStateLoaded"/>): an OnLoad
        /// that threw before then leaves empty or half-built lists, which the next load would
        /// otherwise install over its save.
        /// </summary>
        internal static bool ShouldCapture(
            bool initialLoadDone, bool inertGameMode, bool stagedStateLoaded, out string skipReason)
        {
            if (inertGameMode)
            {
                skipReason = CaptureSkipInertGameMode;
                return false;
            }
            if (!initialLoadDone)
            {
                skipReason = CaptureSkipNoCompletedLoad;
                return false;
            }
            if (!stagedStateLoaded)
            {
                skipReason = CaptureSkipStagedStateNotLoaded;
                return false;
            }
            skipReason = null;
            return true;
        }

        /// <summary>
        /// Captures the scenario's staged lists, journal and marker. Replaces a handoff no OnLoad
        /// consumed (with a Warn), since the newer capture is the newer memory. A null scenario
        /// captures nothing.
        /// </summary>
        internal static void Capture(ParsekScenario scenario, string reason, string saveFolder)
        {
            if (object.ReferenceEquals(null, scenario))
            {
                ParsekLog.Verbose(Tag, $"Staged-list handoff capture skipped reason={reason ?? "<none>"}: no scenario");
                return;
            }

            if (pending != null)
            {
                ParsekLog.Warn(Tag,
                    $"Staged-list handoff replaced an unconsumed one (captured reason={pending.CaptureReason ?? "<none>"} " +
                    $"saveFolder={pending.SaveFolder ?? "<none>"}): no OnLoad consumed it");
            }

            pending = new Snapshot
            {
                RewindPoints = CopyNonNull(scenario.RewindPoints),
                Supersedes = CopyNonNull(scenario.RecordingSupersedes),
                Retirements = CopyNonNull(scenario.RecordingRewindRetirements),
                Tombstones = CopyNonNull(scenario.LedgerTombstones),
                Journal = scenario.ActiveMergeJournal,
                Marker = scenario.ActiveReFlySessionMarker,
                SaveFolder = saveFolder,
                CaptureReason = reason,
            };

            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Info(Tag,
                $"Staged-list handoff captured reason={reason ?? "<none>"} " +
                $"rps={pending.RewindPoints.Count.ToString(ic)} " +
                $"supersedes={pending.Supersedes.Count.ToString(ic)} " +
                $"retirements={pending.Retirements.Count.ToString(ic)} " +
                $"tombstones={pending.Tombstones.Count.ToString(ic)} " +
                $"journal={DescribeJournal(pending.Journal)} " +
                $"marker={(pending.Marker != null ? pending.Marker.SessionId ?? "<no-id>" : "none")} " +
                $"saveFolder={saveFolder ?? "<none>"}");
        }

        /// <summary>Reads and clears the pending handoff (null when none).</summary>
        internal static Snapshot Take()
        {
            var taken = pending;
            pending = null;
            return taken;
        }

        /// <summary>Drops a pending handoff without applying it. No-op (silent) when none is pending.</summary>
        internal static void Drop(string reason)
        {
            if (pending == null)
                return;
            LogDropped(pending, reason);
            pending = null;
        }

        internal static void LogDropped(Snapshot handoff, string reason)
        {
            ParsekLog.Info(Tag,
                $"Staged-list handoff dropped reason={reason ?? "<none>"} " +
                $"(captured reason={handoff?.CaptureReason ?? "<none>"} saveFolder={handoff?.SaveFolder ?? "<none>"})");
        }

        internal static void ResetForTesting()
        {
            pending = null;
            SaveFolderProviderForTesting = null;
        }

        /// <summary>
        /// Step A's decision for one load: null when the handoff is applied, else the drop reason.
        /// Reads the policy table: the staged lists decide <c>Memory</c> only on the in-session
        /// kinds; the other owners map to their reasons. A save folder that differs from the one
        /// the handoff was captured in drops it whatever the kind (a different game).
        /// </summary>
        internal static string DecideStepA(EarlyLoadKind early, string capturedSaveFolder, string currentSaveFolder)
        {
            LoadReconcileAction lists = LoadReconcilePolicy.DecideEarly(early, LoadStateCategory.SupersedeRows).Action;
            switch (lists)
            {
                case LoadReconcileAction.Save:
                    return DropColdLoad;
                case LoadReconcileAction.RewindCarry:
                    return DropRewindCarryOwns;
                case LoadReconcileAction.Bundle:
                    return DropReFlyBundleOwns;
                case LoadReconcileAction.Memory:
                    if (!string.Equals(capturedSaveFolder ?? "", currentSaveFolder ?? "", StringComparison.Ordinal))
                        return DropSaveFolderChanged;
                    return null;
            }
            throw new InvalidOperationException(
                "InSessionStagedStateHandoff.DecideStepA: no handoff rule for the staged-list decision "
                + lists + " on " + early);
        }

        // ------------------------------------------------------------------
        // Rewind points: owner partition
        // ------------------------------------------------------------------

        /// <summary>
        /// The branch-point ids and branch-point rewind-point ids of the given committed trees:
        /// the link <c>RecordingStore.PromoteNormalStagingRewindPoints</c> uses between a tree and
        /// its rewind points, plus <see cref="RewindPoint.BranchPointId"/>.
        /// </summary>
        internal static void CollectCommittedTreeRewindLinks(
            IEnumerable<RecordingTree> trees,
            out HashSet<string> branchPointIds,
            out HashSet<string> rewindPointIds)
        {
            branchPointIds = new HashSet<string>(StringComparer.Ordinal);
            rewindPointIds = new HashSet<string>(StringComparer.Ordinal);
            if (trees == null)
                return;
            foreach (var tree in trees)
            {
                if (tree?.BranchPoints == null) continue;
                for (int b = 0; b < tree.BranchPoints.Count; b++)
                {
                    var bp = tree.BranchPoints[b];
                    if (bp == null) continue;
                    if (!string.IsNullOrEmpty(bp.Id))
                        branchPointIds.Add(bp.Id);
                    if (!string.IsNullOrEmpty(bp.RewindPointId))
                        rewindPointIds.Add(bp.RewindPointId);
                }
            }
        }

        /// <summary>
        /// Which rule decides <paramref name="deciding"/> (the in-memory instance when memory holds
        /// the point, else the loaded one).
        /// </summary>
        internal static RewindPointOwnerClass Classify(RewindPoint deciding, Func<RewindPoint, bool> ownedByCommittedTree)
        {
            if (deciding == null || string.IsNullOrEmpty(deciding.RewindPointId))
                return RewindPointOwnerClass.FollowSave;
            if (deciding.SessionProvisional && !string.IsNullOrEmpty(deciding.CreatingSessionId))
                return RewindPointOwnerClass.SessionScoped;
            if (ownedByCommittedTree != null && ownedByCommittedTree(deciding))
                return RewindPointOwnerClass.CommittedOwner;
            return RewindPointOwnerClass.FollowSave;
        }

        /// <summary>
        /// Pure owner partition of the rewind-point list for an in-session load. Points the two
        /// lists share by id keep the loaded order; points only memory holds follow them.
        /// <see cref="RewindPointOwnerClass.SessionScoped"/>: kept from either side (memory's instance
        /// when both hold it). <see cref="RewindPointOwnerClass.CommittedOwner"/>: memory's instance,
        /// a loaded-only point is not resurrected. <see cref="RewindPointOwnerClass.FollowSave"/>: the
        /// loaded instance, a memory-only point drops out (its file stays). A point with no id
        /// follows the save. <paramref name="decisions"/>, when given, receives one
        /// <c>id:class:outcome</c> entry per point.
        /// </summary>
        internal static List<RewindPoint> MergeRewindPointsByOwner(
            IReadOnlyList<RewindPoint> memory,
            IReadOnlyList<RewindPoint> loaded,
            Func<RewindPoint, bool> ownedByCommittedTree,
            out RewindPointPartitionCounts counts,
            List<string> decisions = null)
        {
            counts = new RewindPointPartitionCounts();
            var memoryById = new Dictionary<string, RewindPoint>(StringComparer.Ordinal);
            if (memory != null)
            {
                for (int i = 0; i < memory.Count; i++)
                {
                    var rp = memory[i];
                    if (rp == null || string.IsNullOrEmpty(rp.RewindPointId)) continue;
                    if (!memoryById.ContainsKey(rp.RewindPointId))
                        memoryById.Add(rp.RewindPointId, rp);
                }
            }

            var result = new List<RewindPoint>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (loaded != null)
            {
                for (int i = 0; i < loaded.Count; i++)
                {
                    var rp = loaded[i];
                    if (rp == null) continue;
                    string id = rp.RewindPointId;
                    if (string.IsNullOrEmpty(id))
                    {
                        result.Add(rp);
                        counts.FollowSaveKept++;
                        decisions?.Add("<no-id>:FollowSave:kept-loaded");
                        continue;
                    }
                    if (!seen.Add(id)) continue;

                    memoryById.TryGetValue(id, out RewindPoint fromMemory);
                    RewindPointOwnerClass cls = Classify(fromMemory ?? rp, ownedByCommittedTree);
                    switch (cls)
                    {
                        case RewindPointOwnerClass.SessionScoped:
                            result.Add(fromMemory ?? rp);
                            counts.SessionScoped++;
                            decisions?.Add(id + ":SessionScoped:" + (fromMemory != null ? "kept-memory" : "kept-loaded"));
                            break;
                        case RewindPointOwnerClass.CommittedOwner:
                            if (fromMemory != null)
                            {
                                result.Add(fromMemory);
                                counts.CommittedKept++;
                                decisions?.Add(id + ":CommittedOwner:kept-memory");
                            }
                            else
                            {
                                counts.CommittedSaveOnlyDropped++;
                                decisions?.Add(id + ":CommittedOwner:dropped-save-only");
                            }
                            break;
                        default:
                            result.Add(rp);
                            counts.FollowSaveKept++;
                            decisions?.Add(id + ":FollowSave:kept-loaded");
                            break;
                    }
                }
            }

            if (memory != null)
            {
                for (int i = 0; i < memory.Count; i++)
                {
                    var rp = memory[i];
                    if (rp == null) continue;
                    string id = rp.RewindPointId;
                    if (string.IsNullOrEmpty(id))
                    {
                        counts.FollowSaveMemoryOnlyDropped++;
                        decisions?.Add("<no-id>:FollowSave:dropped-memory-only");
                        continue;
                    }
                    if (!seen.Add(id)) continue;

                    switch (Classify(rp, ownedByCommittedTree))
                    {
                        case RewindPointOwnerClass.SessionScoped:
                            result.Add(rp);
                            counts.SessionScoped++;
                            decisions?.Add(id + ":SessionScoped:kept-memory-only");
                            break;
                        case RewindPointOwnerClass.CommittedOwner:
                            result.Add(rp);
                            counts.CommittedMemoryOnlyKept++;
                            decisions?.Add(id + ":CommittedOwner:kept-memory-only");
                            break;
                        default:
                            counts.FollowSaveMemoryOnlyDropped++;
                            decisions?.Add(id + ":FollowSave:dropped-memory-only");
                            break;
                    }
                }
            }
            return result;
        }

        internal static string FormatPartitionCounts(RewindPointPartitionCounts c)
        {
            var ic = CultureInfo.InvariantCulture;
            return $"sessionScoped={c.SessionScoped.ToString(ic)} " +
                   $"committedOwner kept={c.CommittedKept.ToString(ic)} " +
                   $"memoryOnlyKept={c.CommittedMemoryOnlyKept.ToString(ic)} " +
                   $"saveOnlyDropped={c.CommittedSaveOnlyDropped.ToString(ic)}; " +
                   $"followSave kept={c.FollowSaveKept.ToString(ic)} " +
                   $"memoryOnlyDropped={c.FollowSaveMemoryOnlyDropped.ToString(ic)}";
        }

        internal static string DescribeJournal(MergeJournal journal)
        {
            if (journal == null)
                return "none";
            return (journal.JournalId ?? "<no-id>") + "@" + (journal.Phase ?? "<no-phase>");
        }

        internal static string JoinBounded(IReadOnlyList<string> entries, int max)
        {
            if (entries == null || entries.Count == 0)
                return "";
            var sb = new StringBuilder();
            for (int i = 0; i < entries.Count && i < max; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(entries[i]);
            }
            if (entries.Count > max)
                sb.Append(", ...");
            return sb.ToString();
        }

        // ---------- the resumed-tree row rule (step B) ----------

        /// <summary>
        /// The recording each row kind names as the Re-Fly attempt that wrote it: the NEW side of
        /// a supersede row, the retiring recording of a tombstone, the retired recording of a
        /// rewind retirement. The discard prune (<c>MergeDialog.PruneStagedRowsNamingAttempt</c>)
        /// removes by it and the resumed-tree rule keeps by it, so the rows a resumed session keeps
        /// on the load are exactly the rows its discard prunes.
        /// </summary>
        internal static readonly Func<RecordingSupersedeRelation, string> SupersedeWriterId = r => r.NewRecordingId;
        internal static readonly Func<LedgerTombstone, string> TombstoneWriterId = t => t.RetiringRecordingId;
        internal static readonly Func<RecordingRewindRetirement, string> RetirementWriterId = r => r.RecordingId;

        /// <summary>A supersede row names a recording through either side.</summary>
        internal static bool SupersedeNamesAny(RecordingSupersedeRelation row, HashSet<string> recordingIds)
        {
            if (row == null || recordingIds == null || recordingIds.Count == 0)
                return false;
            return ContainsId(recordingIds, row.OldRecordingId) || ContainsId(recordingIds, row.NewRecordingId);
        }

        /// <summary>A rewind retirement names the recording it retires.</summary>
        internal static bool RetirementNamesAny(RecordingRewindRetirement row, HashSet<string> recordingIds)
        {
            if (row == null || recordingIds == null || recordingIds.Count == 0)
                return false;
            return ContainsId(recordingIds, row.RecordingId);
        }

        /// <summary>
        /// A tombstone names its retiring recording and, when the ledger still holds the action it
        /// retires with a recording tag, that action's recording
        /// (<paramref name="actionRecordingIds"/>: action id to recording id).
        /// </summary>
        internal static bool TombstoneNamesAny(
            LedgerTombstone row, HashSet<string> recordingIds, IReadOnlyDictionary<string, string> actionRecordingIds)
        {
            if (row == null || recordingIds == null || recordingIds.Count == 0)
                return false;
            if (ContainsId(recordingIds, row.RetiringRecordingId))
                return true;
            return actionRecordingIds != null
                && !string.IsNullOrEmpty(row.ActionId)
                && actionRecordingIds.TryGetValue(row.ActionId, out string actionRecordingId)
                && ContainsId(recordingIds, actionRecordingId);
        }

        /// <summary>
        /// Owner ruling OQ-1's side of the resumed-tree rule: the loaded marker resumes the
        /// attempt that wrote a row when it belongs to the resumed tree and the resumed tree holds
        /// its active Re-Fly recording (the save was taken during that session). Returns the
        /// reason token either way.
        /// </summary>
        internal static bool LoadedMarkerResumesAttempt(
            ReFlySessionMarker marker, string resumedTreeId, ICollection<string> resumedTreeRecordingIds,
            out string reason)
        {
            if (marker == null)
            {
                reason = "no-marker";
                return false;
            }
            if (string.IsNullOrEmpty(resumedTreeId)
                || !string.Equals(marker.TreeId, resumedTreeId, StringComparison.Ordinal))
            {
                reason = "marker-tree-differs";
                return false;
            }
            if (string.IsNullOrEmpty(marker.ActiveReFlyRecordingId)
                || resumedTreeRecordingIds == null
                || !resumedTreeRecordingIds.Contains(marker.ActiveReFlyRecordingId))
            {
                reason = "attempt-not-in-resumed-tree";
                return false;
            }
            reason = "marker-resumes-attempt";
            return true;
        }

        /// <summary>
        /// Pure: the resumed-tree row rule for one staged list. <paramref name="current"/> is the
        /// list step A installed (memory's); <paramref name="loaded"/> the loaded save's own copy.
        /// A row that names no recording of the resumed tree stays as step A left it. A row naming
        /// the resumed tree follows the save: the save's copy when the save holds that id, dropped
        /// when only memory does, and a save-only one is put back. Exception (owner ruling OQ-1):
        /// a row the resumed Re-Fly attempt wrote keeps memory's copy, and a save-only one is not
        /// put back. Memory's order first, then the save's for rows put back.
        /// </summary>
        internal static List<T> ApplyResumedTreeRowRule<T>(
            List<T> current,
            List<T> loaded,
            Func<T, string> idOf,
            Func<T, bool> namesResumedTree,
            Func<T, bool> writtenByResumedAttempt,
            out ResumedTreeRowCounts counts,
            List<string> decisions)
            where T : class
        {
            counts = new ResumedTreeRowCounts();
            var loadedById = new Dictionary<string, T>(StringComparer.Ordinal);
            if (loaded != null)
            {
                for (int i = 0; i < loaded.Count; i++)
                {
                    T row = loaded[i];
                    string id = row != null ? idOf(row) : null;
                    if (!string.IsNullOrEmpty(id) && !loadedById.ContainsKey(id))
                        loadedById[id] = row;
                }
            }

            var result = new List<T>(current?.Count ?? 0);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (current != null)
            {
                for (int i = 0; i < current.Count; i++)
                {
                    T row = current[i];
                    if (row == null)
                        continue;
                    string id = idOf(row);
                    if (!string.IsNullOrEmpty(id))
                        seen.Add(id);
                    if (!namesResumedTree(row))
                    {
                        result.Add(row);
                        counts.Untouched++;
                        continue;
                    }
                    if (writtenByResumedAttempt(row))
                    {
                        result.Add(row);
                        counts.AttemptKept++;
                        decisions?.Add((id ?? "<no-id>") + ":attempt-kept");
                        continue;
                    }
                    if (!string.IsNullOrEmpty(id) && loadedById.TryGetValue(id, out T savedRow))
                    {
                        result.Add(savedRow);
                        counts.FollowSave++;
                        decisions?.Add(id + ":save");
                        continue;
                    }
                    counts.Dropped++;
                    decisions?.Add((id ?? "<no-id>") + ":dropped");
                }
            }

            if (loaded != null)
            {
                for (int i = 0; i < loaded.Count; i++)
                {
                    T row = loaded[i];
                    if (row == null)
                        continue;
                    string id = idOf(row);
                    if (string.IsNullOrEmpty(id) || seen.Contains(id))
                        continue;
                    if (!namesResumedTree(row) || writtenByResumedAttempt(row))
                        continue;
                    seen.Add(id);
                    result.Add(row);
                    counts.RestoredFromSave++;
                    decisions?.Add(id + ":restored-from-save");
                }
            }

            counts.Installed = result.Count;
            return result;
        }

        internal static string FormatResumedTreeRowCounts(string label, ResumedTreeRowCounts c)
        {
            var ic = CultureInfo.InvariantCulture;
            return $"{label} installed={c.Installed.ToString(ic)} untouched={c.Untouched.ToString(ic)} " +
                   $"attemptKept={c.AttemptKept.ToString(ic)} followSave={c.FollowSave.ToString(ic)} " +
                   $"dropped={c.Dropped.ToString(ic)} restoredFromSave={c.RestoredFromSave.ToString(ic)}";
        }

        private static bool ContainsId(HashSet<string> ids, string id)
            => !string.IsNullOrEmpty(id) && ids.Contains(id);

        private static List<T> CopyNonNull<T>(List<T> source) where T : class
        {
            var copy = new List<T>(source?.Count ?? 0);
            if (source == null)
                return copy;
            for (int i = 0; i < source.Count; i++)
            {
                if (source[i] != null)
                    copy.Add(source[i]);
            }
            return copy;
        }
    }
}
