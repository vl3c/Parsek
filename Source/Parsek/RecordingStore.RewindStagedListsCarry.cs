using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    public static partial class RecordingStore
    {
        // ==================================================================
        // Re-Fly staged lists carried across a plain rewind
        // (RP-REWIND-STAGED-LISTS-FROM-STALE-PERSISTENT)
        // ==================================================================

        /// <summary>
        /// The in-memory staged lists captured for a plain rewind (Rewind-to-Launch or
        /// Warp-to-game-start). Same reason as the RP carry
        /// (<see cref="CaptureRewindPointsForRewind"/>): the rewind's OnLoad reads
        /// persistent.sfs as last written (<c>SpaceCenterMain.Start</c> reloads it), while the
        /// recordings and the ledger stay in memory. Loaded from disk, these lists were of
        /// unknown age: a Re-Fly merge in another tree after the last persistent write lost
        /// its supersede rows and tombstones, and a row memory had already removed (tree
        /// discard purge, an earlier rewind's drop) came back. Memory wins wholesale; the
        /// rewound tree's own rows are then dropped by
        /// <see cref="ReapplyRewindSupersedeDropAfterLoad"/>, which runs on the carried list.
        /// </summary>
        internal sealed class RewindCarriedStagedLists
        {
            internal List<RecordingSupersedeRelation> Supersedes;
            internal List<RecordingRewindRetirement> Retirements;
            internal List<LedgerTombstone> Tombstones;
            internal MergeJournal Journal;
        }

        private static RewindCarriedStagedLists rewindCarriedStagedLists;

        /// <summary>True while a captured staged-list set is waiting for the rewind's OnLoad.</summary>
        internal static bool HasRewindCarriedStagedLists => rewindCarriedStagedLists != null;

        /// <summary>
        /// Captures shallow copies of the scenario's supersede, rewind-retirement and
        /// tombstone lists plus the merge-journal reference. A null scenario captures
        /// nothing, which leaves the post-load lists to the loaded save as before.
        /// </summary>
        internal static void CaptureRewindStagedListsForRewind(ParsekScenario scenario, string label)
        {
            if (object.ReferenceEquals(null, scenario))
            {
                rewindCarriedStagedLists = null;
                if (!SuppressLogging)
                    ParsekLog.Info("Rewind",
                        $"{label}: no live scenario staged lists to carry across the rewind load");
                return;
            }

            rewindCarriedStagedLists = new RewindCarriedStagedLists
            {
                Supersedes = CopyNonNull(scenario.RecordingSupersedes),
                Retirements = CopyNonNull(scenario.RecordingRewindRetirements),
                Tombstones = CopyNonNull(scenario.LedgerTombstones),
                Journal = scenario.ActiveMergeJournal,
            };
            if (!SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"{label}: carrying staged lists across the rewind load: " +
                    $"supersedes={rewindCarriedStagedLists.Supersedes.Count.ToString(CultureInfo.InvariantCulture)} " +
                    $"retirements={rewindCarriedStagedLists.Retirements.Count.ToString(CultureInfo.InvariantCulture)} " +
                    $"tombstones={rewindCarriedStagedLists.Tombstones.Count.ToString(CultureInfo.InvariantCulture)} " +
                    $"journal={DescribeJournalForCarry(rewindCarriedStagedLists.Journal)}");
        }

        /// <summary>Drops a captured staged-list set that no rewind OnLoad will consume.</summary>
        internal static void ClearRewindCarriedStagedLists(string reason)
        {
            if (rewindCarriedStagedLists == null)
                return;
            if (!SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"Dropped carried staged lists without reinstalling them (reason={reason})");
            rewindCarriedStagedLists = null;
        }

        /// <summary>
        /// Pure wholesale merge, the staged-list twin of <see cref="MergeCarriedRewindPoints"/>:
        /// the carried list replaces the loaded one (no union, so a row memory removed stays
        /// removed and a row both hold appears once, as the in-memory instance). The counts
        /// only describe the drift: <paramref name="restoredCount"/> carried rows the save
        /// lacked, <paramref name="staleDroppedCount"/> loaded rows memory no longer holds.
        /// Rows without an id are carried but not counted.
        /// </summary>
        internal static List<T> MergeCarriedStagedList<T>(
            IReadOnlyList<T> carried,
            IReadOnlyList<T> loaded,
            Func<T, string> idOf,
            out int restoredCount,
            out int staleDroppedCount)
            where T : class
        {
            var loadedIds = new HashSet<string>(StringComparer.Ordinal);
            if (loaded != null)
            {
                for (int i = 0; i < loaded.Count; i++)
                {
                    string id = loaded[i] != null ? idOf(loaded[i]) : null;
                    if (!string.IsNullOrEmpty(id))
                        loadedIds.Add(id);
                }
            }

            var result = new List<T>();
            var carriedIds = new HashSet<string>(StringComparer.Ordinal);
            restoredCount = 0;
            if (carried != null)
            {
                for (int i = 0; i < carried.Count; i++)
                {
                    T row = carried[i];
                    if (row == null) continue;
                    result.Add(row);
                    string id = idOf(row);
                    if (string.IsNullOrEmpty(id)) continue;
                    if (carriedIds.Add(id) && !loadedIds.Contains(id))
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
        /// Whether the captured merge journal replaces the loaded one. A plain rewind is
        /// refused while an in-memory journal is in flight (<see cref="InitiateRewind"/> and
        /// <see cref="InitiateRewindToCareerStart"/>), so the capture is null or a <c>Complete</c> leftover, and a
        /// journal on disk is one memory already drove or rolled back: installing the
        /// capture keeps the next load's <c>RunFinisher</c> from driving it a second time
        /// (the rewind branch itself runs no finisher). An in-flight capture cannot reach
        /// here; if one does, the loaded journal stands, as before this carry existed.
        /// </summary>
        internal static bool ShouldCarryMergeJournal(MergeJournal carried)
        {
            return carried == null
                || string.Equals(carried.Phase, MergeJournal.Phases.Complete, StringComparison.Ordinal);
        }

        /// <summary>
        /// Rewind OnLoad half of the staged-list carry: replaces the just-loaded supersede,
        /// rewind-retirement and tombstone lists (and, per
        /// <see cref="ShouldCarryMergeJournal"/>, the merge journal) with the capture, then
        /// consumes it. Called from <c>ParsekScenario.OnLoad</c> after the RP carry and
        /// BEFORE <see cref="ReapplyRewindSupersedeDropAfterLoad"/>, so the rewound tree's
        /// rows are dropped from the carried list. A load that is not a rewind leaves the
        /// loaded lists alone and drops any stranded capture. Returns true when installed.
        /// </summary>
        internal static bool ReinstallRewindCarriedStagedListsAfterLoad(ParsekScenario scenario)
        {
            if (rewindCarriedStagedLists == null)
                return false;
            if (!RewindContext.IsRewinding)
            {
                ClearRewindCarriedStagedLists("load-is-not-a-rewind");
                return false;
            }
            if (object.ReferenceEquals(null, scenario))
            {
                ClearRewindCarriedStagedLists("no-scenario-at-rewind-load");
                return false;
            }

            var carried = rewindCarriedStagedLists;
            rewindCarriedStagedLists = null;

            int loadedSupersedes = scenario.RecordingSupersedes?.Count ?? 0;
            int loadedRetirements = scenario.RecordingRewindRetirements?.Count ?? 0;
            int loadedTombstones = scenario.LedgerTombstones?.Count ?? 0;

            scenario.RecordingSupersedes = MergeCarriedStagedList(
                carried.Supersedes, scenario.RecordingSupersedes, r => r.RelationId,
                out int supRestored, out int supStale);
            scenario.RecordingRewindRetirements = MergeCarriedStagedList(
                carried.Retirements, scenario.RecordingRewindRetirements, r => r.RetirementId,
                out int retRestored, out int retStale);
            scenario.LedgerTombstones = MergeCarriedStagedList(
                carried.Tombstones, scenario.LedgerTombstones, t => t.TombstoneId,
                out int tombRestored, out int tombStale);

            MergeJournal loadedJournal = scenario.ActiveMergeJournal;
            bool journalCarried = ShouldCarryMergeJournal(carried.Journal);
            if (journalCarried)
                scenario.ActiveMergeJournal = carried.Journal;

            scenario.BumpSupersedeStateVersion();
            scenario.BumpTombstoneStateVersion();

            if (!SuppressLogging)
            {
                var ic = CultureInfo.InvariantCulture;
                ParsekLog.Info("Rewind",
                    "Staged lists carried across rewind: " +
                    $"supersedes installed={scenario.RecordingSupersedes.Count.ToString(ic)} " +
                    $"loadedFromSave={loadedSupersedes.ToString(ic)} restored={supRestored.ToString(ic)} " +
                    $"staleDropped={supStale.ToString(ic)}; " +
                    $"retirements installed={scenario.RecordingRewindRetirements.Count.ToString(ic)} " +
                    $"loadedFromSave={loadedRetirements.ToString(ic)} restored={retRestored.ToString(ic)} " +
                    $"staleDropped={retStale.ToString(ic)}; " +
                    $"tombstones installed={scenario.LedgerTombstones.Count.ToString(ic)} " +
                    $"loadedFromSave={loadedTombstones.ToString(ic)} restored={tombRestored.ToString(ic)} " +
                    $"staleDropped={tombStale.ToString(ic)}; " +
                    $"journal installed={DescribeJournalForCarry(scenario.ActiveMergeJournal)} " +
                    $"loadedFromSave={DescribeJournalForCarry(loadedJournal)}");
                if (!journalCarried)
                    ParsekLog.Warn("Rewind",
                        $"Staged-list carry kept the loaded merge journal: the captured journal " +
                        $"{DescribeJournalForCarry(carried.Journal)} was in flight at rewind time " +
                        "(a plain rewind refuses that, so this is unexpected)");
                else if (loadedJournal != null && !object.ReferenceEquals(loadedJournal, carried.Journal))
                    ParsekLog.Info("Rewind",
                        $"Dropped stale merge journal {DescribeJournalForCarry(loadedJournal)} read from the " +
                        "save: memory had already finished it, so no later load re-drives it");
            }
            return true;
        }

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

        private static string DescribeJournalForCarry(MergeJournal journal)
        {
            if (journal == null)
                return "none";
            return (journal.JournalId ?? "<no-id>") + "@" + (journal.Phase ?? "<no-phase>");
        }
    }
}
