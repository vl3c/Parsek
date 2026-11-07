using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek
{
    /// <summary>
    /// Rewind carry (RecordingStore services plan, service N): what a plain rewind
    /// (Rewind-to-Launch or Warp-to-game-start) carries from memory across its load, because
    /// the rewind's OnLoad reads persistent.sfs as last written while the recordings and the
    /// ledger stay in memory. The rewind-point list (this file), the Re-Fly staged lists and
    /// merge journal (<c>RewindCarry.StagedLists.cs</c>), and the read-only audit of the
    /// supersede rows a rewind keeps (<c>RewindCarry.SupersedeAudit.cs</c>). The two carries
    /// are save-scoped: <see cref="ResetForLoad"/> and <see cref="ResetForTesting"/> drop them.
    /// </summary>
    internal static partial class RewindCarry
    {
        // ==================================================================
        // Rewind points carried across a plain rewind (operator ruling 2026-09-23)
        // ==================================================================

        /// <summary>
        /// Save-scoped reset (<see cref="RecordingStore.ResetSaveScopedStateForLoad"/>): drops
        /// both carries, as the store did before they moved here.
        /// </summary>
        internal static void ResetForLoad()
        {
            rewindCarriedRewindPoints = null;
            rewindCarriedStagedLists = null;
        }

        /// <summary>
        /// Test reset, run by <see cref="RecordingStore.ResetForTesting"/>. Drops both carries;
        /// the store's test reset used to drop only the rewind-point carry, so a test that
        /// captured a staged-list carry and skipped <see cref="ClearRewindCarriedStagedLists"/>
        /// leaked it into the next test.
        /// </summary>
        internal static void ResetForTesting()
        {
            rewindCarriedRewindPoints = null;
            rewindCarriedStagedLists = null;
        }

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
                if (!RecordingStore.SuppressLogging)
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
            if (!RecordingStore.SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"{label}: carrying {rewindCarriedRewindPoints.Count} rewind point(s) across the rewind load " +
                    $"[{FormatRewindPointIds(rewindCarriedRewindPoints)}]");
        }

        /// <summary>Drops a captured RP list that no rewind OnLoad will consume.</summary>
        internal static void ClearRewindCarriedRewindPoints(string reason)
        {
            if (rewindCarriedRewindPoints == null)
                return;
            if (!RecordingStore.SuppressLogging)
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
            if (!RecordingStore.SuppressLogging)
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
                for (int t = 0; t < RecordingStore.CommittedTrees.Count; t++)
                {
                    var tree = RecordingStore.CommittedTrees[t];
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
            for (int i = 0; i < RecordingStore.CommittedRecordings.Count; i++)
            {
                var liveRec = RecordingStore.CommittedRecordings[i];
                if (liveRec != null && !string.IsNullOrEmpty(liveRec.RecordingId))
                    liveById[liveRec.RecordingId] = liveRec;
            }

            RewindSupersedeAudit audit = AuditSupersedesKeptAcrossRewindPure(
                owner, rewindAdjustedUT, ownerTreeRecordings, liveById, scenario.RecordingSupersedes);

            if (RecordingStore.SuppressLogging)
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
    }
}
