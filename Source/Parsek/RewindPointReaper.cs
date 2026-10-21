using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Parsek
{
    /// <summary>
    /// Phase 11 of Rewind-to-Staging (design §5.1 / §6.6 step 9 / §6.8 /
    /// §10.1): reaps orphaned <see cref="RewindPoint"/>s.
    ///
    /// <para>
    /// A <see cref="RewindPoint"/> becomes reap-eligible when every child
    /// slot's effective chain+supersede tip recording is closed
    /// (<see cref="MergeState.Immutable"/> = sealed / concluded). A slot whose
    /// tip is <see cref="MergeState.NotCommitted"/> or
    /// <see cref="MergeState.CommittedProvisional"/> is still open and keeps
    /// the RP alive. Open/closed is read solely from the tip MergeState (the
    /// single source of truth after collapse-seal-into-mergestate). At that
    /// point no slot can be re-flown any more, so the on-disk quicksave and the
    /// scenario entry are dead weight. Session-provisional RPs
    /// (<see cref="RewindPoint.SessionProvisional"/> still true) are
    /// always retained; Phase 10's <see cref="MergeJournalOrchestrator.TagRpsForReap"/>
    /// flips that flag at merge time so the first post-merge reap pass can
    /// claim them.
    /// </para>
    ///
    /// <para>
    /// Reap action per RP:
    /// <list type="bullet">
    ///   <item><description>Delete the quicksave file at
    ///   <c>saves/&lt;save&gt;/Parsek/RewindPoints/&lt;rpId&gt;.sfs</c>
    ///   (best-effort; a file-delete failure logs a Warn and continues).</description></item>
    ///   <item><description>Remove the RP from
    ///   <see cref="ParsekScenario.RewindPoints"/>.</description></item>
    ///   <item><description>Clear <see cref="BranchPoint.RewindPointId"/> on
    ///   every BranchPoint whose back-reference matches the reaped RP.</description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Callers: <see cref="MergeJournalOrchestrator.RunMerge"/> invokes the
    /// reaper right after the RpReap checkpoint is written so a merge that
    /// makes a slot Immutable immediately frees its RP; <see cref="ParsekScenario.OnLoad"/>
    /// also runs a housekeeping pass so saves that crashed between Phase 10
    /// tagging and Phase 11 reap eventually converge.
    /// </para>
    /// </summary>
    internal static class RewindPointReaper
    {
        private const string Tag = "Rewind";

        /// <summary>
        /// Test seam: when non-null, replaces
        /// <see cref="ReleaseQuicksaveFile"/> with an injected predicate that
        /// observes which points were released. Returning false counts as a
        /// release failure in the summary (the scenario entry is still removed).
        /// Production code leaves this null; the release never touches the file.
        /// </summary>
        internal static Func<string, bool> ReleaseQuicksaveForTesting;
        internal static Func<string, string> ResolveQuicksaveAbsolutePathForTesting;

        /// <summary>Clears all test seams. Called from test class Dispose.</summary>
        internal static void ResetTestOverrides()
        {
            ReleaseQuicksaveForTesting = null;
            ResolveQuicksaveAbsolutePathForTesting = null;
        }

        /// <summary>
        /// Scans <see cref="ParsekScenario.RewindPoints"/> for reap-eligible
        /// entries (per the rule in the class summary), deletes each one's
        /// quicksave file, scenario entry, and <see cref="BranchPoint"/>
        /// back-reference. Returns the number of RPs reaped (0 when there
        /// is nothing to do or no scenario is live).
        /// </summary>
        internal static int ReapOrphanedRPs()
        {
            var scenario = ParsekScenario.Instance;
            if (ReferenceEquals(null, scenario))
            {
                ParsekLog.Verbose(Tag, "ReapOrphanedRPs: no ParsekScenario instance — skipping");
                return 0;
            }

            var rps = scenario.RewindPoints;
            if (rps == null || rps.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    $"ReapOrphanedRPs: reaped=0 remaining=0");
                return 0;
            }

            IReadOnlyList<RecordingSupersedeRelation> supersedes =
                scenario.RecordingSupersedes
                ?? (IReadOnlyList<RecordingSupersedeRelation>)new List<RecordingSupersedeRelation>();
            string markerRewindPointId = scenario.ActiveReFlySessionMarker?.RewindPointId;

            // Snapshot of eligible RPs + matching indices so we don't mutate
            // while iterating.
            var toReap = new List<RewindPoint>();
            var toReapIndices = new List<int>();
            int keptInert = 0;
            for (int i = 0; i < rps.Count; i++)
            {
                var rp = rps[i];
                if (rp == null) continue;
                if (!string.IsNullOrEmpty(markerRewindPointId)
                    && string.Equals(rp.RewindPointId, markerRewindPointId, StringComparison.Ordinal))
                {
                    ParsekLog.Verbose(Tag,
                        $"ReapOrphanedRPs: keeping marker rp={rp.RewindPointId ?? "<no-id>"} " +
                        $"while re-fly session {scenario.ActiveReFlySessionMarker?.SessionId ?? "<no-id>"} is active");
                    continue;
                }
                if (!IsReapEligible(rp, supersedes))
                {
                    if (NamesInertRecording(rp, supersedes))
                        keptInert++;
                    continue;
                }
                toReap.Add(rp);
                toReapIndices.Add(i);
            }

            if (keptInert > 0)
            {
                ParsekLog.Info(Tag,
                    $"ReapOrphanedRPs: kept {keptInert.ToString(CultureInfo.InvariantCulture)} rewind point(s) " +
                    "naming inert recordings (committed trees from a different recording schema generation)");
            }

            if (toReap.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    $"ReapOrphanedRPs: reaped=0 remaining={rps.Count.ToString(CultureInfo.InvariantCulture)}");
                return 0;
            }

            int fileReleaseOk = 0;
            int fileReleaseFail = 0;
            int contractSnapshotsPurged = 0;
            int bpBackrefCleared = 0;

            // Walk in reverse-index order so RemoveAt doesn't shift still-to-
            // come indices.
            for (int k = toReap.Count - 1; k >= 0; k--)
            {
                var rp = toReap[k];
                int idx = toReapIndices[k];

                // 1. Release the quicksave file to the collector.
                if (ReleaseQuicksaveFile(rp))
                    fileReleaseOk++;
                else
                    fileReleaseFail++;

                // 2. Remove scenario entry.
                rps.RemoveAt(idx);
                RecordingsTableUI.ClearRewindSlotCanInvokeLogState(rp?.RewindPointId);

                // 3. Clear BranchPoint back-reference (if any).
                if (ClearBranchPointBackref(rp))
                    bpBackrefCleared++;

                // 4. Drop the RP's contract re-snapshots. They exist only to reinstate a
                // tombstoned contract at THIS rewind point's state; once the RP is gone
                // nothing can select them, and leaving them would grow snapshot storage
                // without bound across a long career.
                contractSnapshotsPurged +=
                    GameStateStore.PurgeContractSnapshotsForRewindPoint(rp?.RewindPointId);

                ParsekLog.Info(Tag,
                    $"Reaped rp={rp.RewindPointId ?? "<no-id>"} " +
                    $"bp={rp.BranchPointId ?? "<no-bp>"} slots={rp.ChildSlots?.Count ?? 0}");
            }

            ParsekLog.Info(Tag,
                $"ReapOrphanedRPs: reaped={toReap.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"remaining={rps.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"fileReleaseOk={fileReleaseOk.ToString(CultureInfo.InvariantCulture)} " +
                $"fileReleaseFail={fileReleaseFail.ToString(CultureInfo.InvariantCulture)} " +
                $"bpBackrefCleared={bpBackrefCleared.ToString(CultureInfo.InvariantCulture)} " +
                $"contractSnapshotsPurged={contractSnapshotsPurged.ToString(CultureInfo.InvariantCulture)}");

            return toReap.Count;
        }

        /// <summary>
        /// A <see cref="RewindPoint"/> is reap-eligible when <b>all</b> of:
        /// <list type="bullet">
        ///   <item><description><see cref="RewindPoint.SessionProvisional"/> is false (the owning session has merged).</description></item>
        ///   <item><description>Every <see cref="ChildSlot"/> is CLOSED: its effective chain+supersede tip recording resolves to <see cref="MergeState.Immutable"/>. A slot is OPEN (blocks reap) when its tip is <see cref="MergeState.NotCommitted"/> (recorder still running) or <see cref="MergeState.CommittedProvisional"/> (re-flyable Unfinished Flight or stashed stable leaf).</description></item>
        /// </list>
        /// Open/closed is read solely from the tip MergeState — the single
        /// source of truth after the <c>slot.Sealed</c> bit was collapsed into
        /// MergeState (collapse-seal-into-mergestate plan). A slot whose
        /// OriginChildRecordingId is null/blank, or whose effective tip cannot
        /// be resolved to a committed recording (orphan), is treated as closed
        /// (there is no recording that could still be re-flown), EXCEPT a tip that
        /// belongs to a committed tree kept inert (<see cref="InertRecordingTrees"/>,
        /// GC-3): that slot reads OPEN, so the point and its quicksave stay for the
        /// Parsek build that can load the tree. An RP with no slots at all is eligible.
        /// </summary>
        internal static bool IsReapEligible(
            RewindPoint rp, IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            if (rp == null) return false;
            if (rp.SessionProvisional) return false;

            var slots = rp.ChildSlots;
            if (slots == null) return true;

            for (int s = 0; s < slots.Count; s++)
            {
                var slot = slots[s];
                if (slot == null) continue;

                // Null / empty origin -> closed (no live slot to re-fly).
                // Same policy as EffectiveRecordingId.
                string effectiveId = slot.EffectiveRecordingId(supersedes);
                if (string.IsNullOrEmpty(effectiveId))
                    continue;

                var rec = FindRecordingById(effectiveId);
                if (rec == null)
                {
                    // A recording of an inert tree is not an orphan: it is kept
                    // unchanged for the Parsek build that wrote it -> open.
                    if (InertRecordingTrees.ContainsRecordingId(effectiveId))
                        return false;
                    // Orphan recording id (tree was discarded mid-session,
                    // or the save dropped the recording). No re-fly target
                    // survives -> closed.
                    continue;
                }

                // Open iff the effective tip is NotCommitted (recorder still
                // running) or CommittedProvisional (re-flyable). Immutable =
                // sealed / concluded = closed.
                if (rec.MergeState == MergeState.NotCommitted
                    || rec.MergeState == MergeState.CommittedProvisional)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// True when a slot of <paramref name="rp"/> resolves to a recording of a committed tree
        /// kept inert (the reason <see cref="IsReapEligible"/> kept it). Diagnostics only.
        /// </summary>
        internal static bool NamesInertRecording(
            RewindPoint rp, IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            if (rp?.ChildSlots == null || InertRecordingTrees.Count == 0)
                return false;
            for (int s = 0; s < rp.ChildSlots.Count; s++)
            {
                var slot = rp.ChildSlots[s];
                if (slot == null) continue;
                string effectiveId = slot.EffectiveRecordingId(supersedes);
                if (InertRecordingTrees.ContainsRecordingId(effectiveId)
                    && FindRecordingById(effectiveId) == null)
                    return true;
            }
            return false;
        }

        private static Recording FindRecordingById(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            // Tree-aware raw lookup. A slot's effective tip can live in a
            // committed tree without being mirrored into the flat committed
            // list (transiently true during a re-fly merge's tree-replace
            // tail). A flat-list-only scan would miss such a tip, return null,
            // and the caller would treat the slot as an orphan (closed) -> the
            // reaper would reap the RP and destroy an OPEN sibling slot (data
            // loss). EffectiveState.FindCommittedRecordingByIdRaw checks the
            // flat list first, then the committed trees. The reaper MUST see
            // NotCommitted / CommittedProvisional states (ERS would filter
            // NotCommitted out), which the raw-by-id helper preserves.
            return EffectiveState.FindCommittedRecordingByIdRaw(recordingId);
        }

        /// <summary>
        /// Releases a reaped point's quicksave file. The reaper only unlists the point; the file
        /// stays until <see cref="ParsekFileCollector"/> finds that no save file and no memory
        /// list names the point any more (design-load-restores-the-save.md 4.5), because an
        /// older save may still list it and offer its rewind.
        /// </summary>
        internal static bool ReleaseQuicksaveFile(RewindPoint rp)
        {
            var hook = ReleaseQuicksaveForTesting;
            if (hook != null)
            {
                try
                {
                    return hook(rp?.RewindPointId ?? "");
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(Tag,
                        $"RewindPoint quicksave release hook threw for rp={rp?.RewindPointId ?? "<no-id>"}: " +
                        $"{ex.GetType().Name}: {ex.Message}");
                    return false;
                }
            }

            ParsekLog.Verbose(Tag,
                $"Rewind quicksave released for collection rp={rp?.RewindPointId ?? "<no-id>"} " +
                "(deleted once no save lists the point)");
            return true;
        }

        internal static string ResolveQuicksaveAbsolutePath(string rewindPointId)
        {
            var hook = ResolveQuicksaveAbsolutePathForTesting;
            if (hook != null)
                return hook(rewindPointId);

            string relPath = RecordingPaths.BuildRewindPointRelativePath(rewindPointId);
            if (string.IsNullOrEmpty(relPath)) return null;

            string root = null;
            string saveFolder = null;
            try { root = KSPUtil.ApplicationRootPath; } catch { root = null; }
            try { saveFolder = HighLogic.SaveFolder; } catch { saveFolder = null; }
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(saveFolder))
                return null;

            return Path.GetFullPath(Path.Combine(root, "saves", saveFolder, relPath));
        }

        internal static bool ClearBranchPointBackref(RewindPoint rp)
        {
            if (rp == null || string.IsNullOrEmpty(rp.RewindPointId))
                return false;
            string rpId = rp.RewindPointId;
            bool cleared = false;

            // Walk every committed tree's BranchPoints list looking for a
            // back-reference. RPs always point at a single BP by design, but
            // we clear every match defensively.
            var trees = RecordingStore.CommittedTrees;
            if (trees != null)
            {
                for (int t = 0; t < trees.Count; t++)
                {
                    var tree = trees[t];
                    if (tree == null || tree.BranchPoints == null) continue;
                    for (int b = 0; b < tree.BranchPoints.Count; b++)
                    {
                        var bp = tree.BranchPoints[b];
                        if (bp == null) continue;
                        if (!string.Equals(bp.RewindPointId, rpId, StringComparison.Ordinal))
                            continue;
                        bp.RewindPointId = null;
                        cleared = true;
                        ParsekLog.Verbose(Tag,
                            $"Cleared BranchPoint.RewindPointId rp={rpId} bp={bp.Id ?? "<no-id>"}");
                    }
                }
            }

            // Also check the pending tree (if any) — an RP can be reaped
            // after its owning tree drops back to pending via OnLoad.
            var pending = RecordingStore.PendingTree;
            if (pending != null && pending.BranchPoints != null)
            {
                for (int b = 0; b < pending.BranchPoints.Count; b++)
                {
                    var bp = pending.BranchPoints[b];
                    if (bp == null) continue;
                    if (!string.Equals(bp.RewindPointId, rpId, StringComparison.Ordinal))
                        continue;
                    bp.RewindPointId = null;
                    cleared = true;
                    ParsekLog.Verbose(Tag,
                        $"Cleared BranchPoint.RewindPointId (pending tree) rp={rpId} bp={bp.Id ?? "<no-id>"}");
                }
            }

            return cleared;
        }
    }
}
