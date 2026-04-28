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
    /// slot's effective recording has reached <see cref="MergeState.Immutable"/>
    /// — at that point no slot can be re-flown any more, so the on-disk
    /// quicksave and the scenario entry are dead weight. Session-provisional
    /// RPs (<see cref="RewindPoint.SessionProvisional"/> still true) are
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
        /// <see cref="TryDeleteQuicksaveFile"/> with an injected predicate.
        /// Returns true on "file deleted / file absent" (reap continues),
        /// false on "I/O failure" (reap continues + logs a Warn — the
        /// scenario entry is still removed so the state is bounded).
        /// Production code leaves this null and the reaper performs a
        /// real <see cref="File.Delete"/>.
        /// </summary>
        internal static Func<string, bool> DeleteQuicksaveForTesting;

        /// <summary>Clears all test seams. Called from test class Dispose.</summary>
        internal static void ResetTestOverrides()
        {
            DeleteQuicksaveForTesting = null;
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
            int sealedSlotsContributingTotal = 0;

            // Snapshot of eligible RPs + matching indices so we don't mutate
            // while iterating.
            var toReap = new List<RewindPoint>();
            var toReapIndices = new List<int>();
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
                int sealedSlotsContributing;
                if (!IsReapEligible(rp, supersedes, out sealedSlotsContributing))
                    continue;
                sealedSlotsContributingTotal += sealedSlotsContributing;
                toReap.Add(rp);
                toReapIndices.Add(i);
            }

            if (toReap.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    $"ReapOrphanedRPs: reaped=0 remaining={rps.Count.ToString(CultureInfo.InvariantCulture)} " +
                    $"sealedSlotsContributing=0");
                return 0;
            }

            int fileDeleteOk = 0;
            int fileDeleteFail = 0;
            int bpBackrefCleared = 0;

            // Walk in reverse-index order so RemoveAt doesn't shift still-to-
            // come indices.
            for (int k = toReap.Count - 1; k >= 0; k--)
            {
                var rp = toReap[k];
                int idx = toReapIndices[k];

                // 1. Delete quicksave file (best-effort).
                if (TryDeleteQuicksaveFile(rp))
                    fileDeleteOk++;
                else
                    fileDeleteFail++;

                // 2. Remove scenario entry.
                rps.RemoveAt(idx);
                RecordingsTableUI.ClearRewindSlotCanInvokeLogState(rp?.RewindPointId);

                // 3. Clear BranchPoint back-reference (if any).
                if (ClearBranchPointBackref(rp))
                    bpBackrefCleared++;

                ParsekLog.Info(Tag,
                    $"Reaped rp={rp.RewindPointId ?? "<no-id>"} " +
                    $"bp={rp.BranchPointId ?? "<no-bp>"} slots={rp.ChildSlots?.Count ?? 0}");
            }

            ParsekLog.Info(Tag,
                $"ReapOrphanedRPs: reaped={toReap.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"remaining={rps.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"fileDeleteOk={fileDeleteOk.ToString(CultureInfo.InvariantCulture)} " +
                $"fileDeleteFail={fileDeleteFail.ToString(CultureInfo.InvariantCulture)} " +
                $"bpBackrefCleared={bpBackrefCleared.ToString(CultureInfo.InvariantCulture)} " +
                $"sealedSlotsContributing={sealedSlotsContributingTotal.ToString(CultureInfo.InvariantCulture)}");

            return toReap.Count;
        }

        /// <summary>
        /// A <see cref="RewindPoint"/> is reap-eligible when <b>all</b> of:
        /// <list type="bullet">
        ///   <item><description><see cref="RewindPoint.SessionProvisional"/> is false (the owning session has merged).</description></item>
        ///   <item><description>Every <see cref="ChildSlot"/>'s effective recording resolves to a live <see cref="Recording"/> with <see cref="MergeState.Immutable"/>.</description></item>
        /// </list>
        /// A slot whose OriginChildRecordingId is null/blank is treated as
        /// terminal-Immutable (there is no recording that could still be
        /// re-flown). An RP with no slots at all is eligible — the feature
        /// doesn't create empty RPs, but defensive: an empty slot list has
        /// no open rewind arrows.
        /// </summary>
        internal static bool IsReapEligible(
            RewindPoint rp, IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            int sealedSlotsContributing;
            return IsReapEligible(rp, supersedes, out sealedSlotsContributing);
        }

        private static bool IsReapEligible(
            RewindPoint rp,
            IReadOnlyList<RecordingSupersedeRelation> supersedes,
            out int sealedSlotsContributing)
        {
            sealedSlotsContributing = 0;
            if (rp == null) return false;
            if (rp.SessionProvisional) return false;

            var slots = rp.ChildSlots;
            if (slots == null) return true;

            for (int s = 0; s < slots.Count; s++)
            {
                var slot = slots[s];
                if (slot == null) continue;

                // Null / empty origin -> treat as eligible (no live slot to
                // re-fly). Same policy as EffectiveRecordingId.
                string effectiveId = slot.EffectiveRecordingId(supersedes);
                if (string.IsNullOrEmpty(effectiveId))
                    continue;

                var rec = FindRecordingById(effectiveId);
                if (rec == null)
                {
                    // Orphan recording id (tree was discarded mid-session,
                    // or the save dropped the recording). No re-fly target
                    // survives — treat as eligible.
                    continue;
                }

                if (rec.MergeState == MergeState.NotCommitted)
                    return false;
                if (rec.MergeState == MergeState.Immutable)
                    continue;
                if (slot.Sealed)
                {
                    sealedSlotsContributing++;
                    continue;
                }

                return false;
            }

            return true;
        }

        private static Recording FindRecordingById(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            // Allowlisted raw read: the reaper MUST see NotCommitted /
            // CommittedProvisional states (ERS would filter NotCommitted out).
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) return null;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec == null) continue;
                if (string.Equals(rec.RecordingId, recordingId, StringComparison.Ordinal))
                    return rec;
            }
            return null;
        }

        internal static bool TryDeleteQuicksaveFile(RewindPoint rp)
        {
            var hook = DeleteQuicksaveForTesting;
            if (hook != null)
            {
                try
                {
                    return hook(rp?.RewindPointId ?? "");
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(Tag,
                        $"RewindPoint quicksave delete hook threw for rp={rp?.RewindPointId ?? "<no-id>"}: " +
                        $"{ex.GetType().Name}: {ex.Message}");
                    return false;
                }
            }

            if (rp == null || string.IsNullOrEmpty(rp.RewindPointId))
            {
                ParsekLog.Verbose(Tag,
                    "TryDeleteQuicksaveFile: no rewindPointId — nothing to delete");
                return true;
            }

            string absolute = ResolveQuicksaveAbsolutePath(rp.RewindPointId);
            if (string.IsNullOrEmpty(absolute))
            {
                ParsekLog.Verbose(Tag,
                    $"TryDeleteQuicksaveFile: could not resolve save root for rp={rp.RewindPointId} — skipping delete");
                return true;
            }

            try
            {
                if (File.Exists(absolute))
                {
                    File.Delete(absolute);
                    ParsekLog.Verbose(Tag,
                        $"Deleted rewind quicksave rp={rp.RewindPointId} path={absolute}");
                }
                else
                {
                    ParsekLog.Verbose(Tag,
                        $"Rewind quicksave already absent rp={rp.RewindPointId} path={absolute}");
                }
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"Failed to delete rewind quicksave rp={rp.RewindPointId} path={absolute}: " +
                    $"{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static string ResolveQuicksaveAbsolutePath(string rewindPointId)
        {
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
