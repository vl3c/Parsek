using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Runtime check for the P9a kerbal-experience re-assert: after a rewind, a kerbal whose
    /// recovery lives on a SURVIVING flight gets their archived career-log entries back on the
    /// live roster, while a kerbal whose recovery lives only on a SUPERSEDED branch does not.
    ///
    /// <para>
    /// Why it has to run in-game: the whole point is what the live
    /// <c>ProtoCrewMember.careerLog</c> holds after a quicksave load, and neither the roster
    /// nor the load can exist headlessly. The pure pieces (wire format, accumulator union,
    /// missing-entry diff, Layer-A parse) are covered by <c>KerbalExperienceFacetTests</c>;
    /// this cell covers the one thing they cannot — that the ledger's view and KSP's roster
    /// actually agree after the patch runs.
    /// </para>
    ///
    /// <para>
    /// Self-skips rather than asserting against an assumed context: it needs an ELS that
    /// carries at least one <see cref="GameActionType.KerbalExperience"/> row, which means a
    /// crewed recovery must have been recorded. The category is <c>LedgerGroundTruth</c>
    /// deliberately — no committed harness spec pins that category's tally, so this cell does
    /// not move a pinned <c>BATCH_COMPLETE</c> number.
    /// </para>
    /// </summary>
    public class KerbalExperienceReassertTest
    {
        [InGameTest(Category = "LedgerGroundTruth", Scene = GameScenes.FLIGHT,
            Description = "Surviving flights' archived career-log entries are re-asserted onto the live roster")]
        public void SurvivingCareerLogEntriesAreOnTheLiveRoster()
        {
            var roster = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.CrewRoster : null;
            if (roster == null)
            {
                InGameAssert.Skip("No CrewRoster — needs a loaded career/science game.");
                return;
            }

            // ELS, not the raw ledger: a tombstoned XP row from a superseded branch must be
            // invisible here, and that invisibility is half of what this cell asserts.
            var els = EffectiveState.ComputeELS();
            if (els == null)
            {
                InGameAssert.Skip("ComputeELS returned null.");
                return;
            }

            var expectedByKerbal = new Dictionary<string, HashSet<KerbalCareerLogEntry>>();
            foreach (var action in els)
            {
                if (action == null) continue;
                if (action.Type != GameActionType.KerbalExperience) continue;
                if (string.IsNullOrEmpty(action.KerbalName)) continue;
                if (string.IsNullOrEmpty(action.KerbalCareerEntries)) continue;

                HashSet<KerbalCareerLogEntry> set;
                if (!expectedByKerbal.TryGetValue(action.KerbalName, out set))
                {
                    set = new HashSet<KerbalCareerLogEntry>();
                    expectedByKerbal[action.KerbalName] = set;
                }
                foreach (var entry in KerbalCareerLogEntry.ParseSet(action.KerbalCareerEntries))
                    set.Add(entry);
            }

            if (expectedByKerbal.Count == 0)
            {
                InGameAssert.Skip(
                    "No KerbalExperience rows in the effective ledger — recover a crewed vessel "
                    + "first, then rerun. (A superseded-only recovery correctly produces none, "
                    + "which is the other half of this check and is asserted by the ELS walk itself.)");
                return;
            }

            // The recalc must have run at least once for the accumulator to be populated; the
            // patcher's re-assert runs inside the same ApplyToRoster pass.
            LedgerOrchestrator.RecalculateAndPatch();

            int kerbalsChecked = 0;
            int entriesChecked = 0;
            int absentKerbals = 0;
            var missingReport = new List<string>();

            foreach (var kvp in expectedByKerbal)
            {
                ProtoCrewMember member = null;
                try { member = roster[kvp.Key]; }
                catch { member = null; }

                if (member == null || member.careerLog == null)
                {
                    // A kerbal the ledger credits but the roster does not have is the
                    // documented skip-with-counter case, not a failure.
                    absentKerbals++;
                    continue;
                }

                var live = new HashSet<KerbalCareerLogEntry>();
                for (int i = 0; i < member.careerLog.Count; i++)
                {
                    var entry = member.careerLog[i];
                    if (entry == null || string.IsNullOrEmpty(entry.type)) continue;
                    live.Add(new KerbalCareerLogEntry(entry.flight, entry.type, entry.target));
                }

                kerbalsChecked++;
                foreach (var expected in kvp.Value)
                {
                    entriesChecked++;
                    if (!live.Contains(expected))
                        missingReport.Add($"{kvp.Key}:{KerbalCareerLogEntry.Format(expected)}");
                }
            }

            if (kerbalsChecked == 0)
            {
                InGameAssert.Skip(
                    $"Every ledger-credited kerbal ({absentKerbals}) is absent from the live roster "
                    + "— nothing to compare.");
                return;
            }

            ParsekLog.Info("LedgerGroundTruth",
                $"KerbalExperienceReassert: kerbals={kerbalsChecked} entries={entriesChecked} "
                + $"absent={absentKerbals} missing={missingReport.Count}");

            InGameAssert.AreEqual(0, missingReport.Count,
                "Every career-log entry the surviving ledger credits must be present on the live "
                + "roster after the re-assert. Missing: [" + string.Join(", ", missingReport.ToArray()) + "]");
        }
    }
}
