using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Parsek.Tests
{
    /// <summary>
    /// Builds the committed <c>harness/fixtures/saves/stock-screen-census-repair</c> fixture
    /// BY CONSTRUCTION from a fresh <see cref="StockScreenCensusFixture"/> build: the same
    /// rewound career, plus the state KSC-REPAIR-AFTER-REWIND-DOUBLE-CHARGE is about. The
    /// Tracking Station's <see cref="DestroyedBuilding"/> collapsed BEFORE the save clock
    /// (a past KSC-origin <c>FacilityDestruction</c> row, and <c>intact = False</c> in
    /// <c>ScenarioDestructibles</c>, which is what stock loads the building from) and the
    /// committed timeline repairs it AFTER the clock (a future KSC-origin
    /// <c>FacilityRepair</c> row). That is the state a Parsek rewind to a UT between a
    /// collapse and its repair leaves behind, and the one in which the facility menu's
    /// Repair button is greyed and a repair click refused
    /// (<see cref="StockUiReservationPredicates.IsFacilityRepairBlocked"/>). The lane
    /// <c>KB-2-ksc-repair-block-after-rewind</c> flies it.
    ///
    /// <para>The repair row lies BEFORE the census's Tracking Station upgrade (UT 90000):
    /// stock's upgrade resets the facility's structures, so a committed timeline that
    /// upgraded first would have had nothing left to repair.</para>
    ///
    /// <para>Deterministic; <c>StockScreenRepairFixtureTests</c> rebuilds it and compares
    /// it with the committed tree, so a hand edit on either side reds the suite. A change
    /// to the census builder moves this fixture too (rewrite both).</para>
    /// </summary>
    internal static class StockScreenRepairFixture
    {
        internal const string FixtureName = "stock-screen-census-repair";
        internal const string Title = "stock-screen-census-repair (CAREER)";

        internal const string Facility = "SpaceCenter/TrackingStation";
        /// <summary>The destroyed building. At facility level 0 the Tracking Station has two
        /// live destructibles, <c>building</c> and <c>OuterDish</c> (KB-1's target).</summary>
        internal const string DestroyedBuilding = "SpaceCenter/TrackingStation/Facility/OuterDish";
        /// <summary>The intact sibling: the repair must charge and repair the dish only.</summary>
        internal const string IntactSibling = "SpaceCenter/TrackingStation/Facility/building";

        /// <summary>The collapse: after the census's past strategy activation (UT 390),
        /// before the save clock (UT 409.56).</summary>
        internal const double DestructionUT = 400;
        /// <summary>The committed repair: after the census's hire (70000), before its
        /// Tracking Station upgrade (90000).</summary>
        internal const double RepairUT = 80000;
        /// <summary>The OuterDish's level-0 repair cost, as KB-1's flights measured it
        /// (<c>BuildingRepaired '...OuterDish' cost=4000</c>).</summary>
        internal const float RepairCost = 4000f;

        /// <summary>The highest KSC-scoped <c>seq</c> the census ledger carries.</summary>
        internal const int CensusLastKscSeq = 10;

        private const string Crlf = "\r\n";

        internal static string TargetDir(string repoRoot)
        {
            return Path.Combine(StockScreenCensusFixture.SavesDir(repoRoot), FixtureName);
        }

        /// <summary>Every file the fixture holds, keyed by its '/'-separated relative path.</summary>
        internal static SortedDictionary<string, byte[]> BuildFiles(string repoRoot)
        {
            var files = StockScreenCensusFixture.BuildFiles(repoRoot);

            string sfs = Encoding.UTF8.GetString(files["persistent.sfs"]);
            files["persistent.sfs"] = Encoding.UTF8.GetBytes(BuildSave(sfs));

            string ledger = Encoding.UTF8.GetString(files["Parsek/GameState/ledger.pgld"]);
            files["Parsek/GameState/ledger.pgld"] = Encoding.UTF8.GetBytes(BuildLedger(ledger));
            return files;
        }

        internal static string BuildSave(string censusText)
        {
            if (censusText.IndexOf(Crlf, StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("census persistent.sfs is not CRLF");
            var lines = new List<string>(censusText.Split(new[] { Crlf }, StringSplitOptions.None));

            StockScreenCensusFixture.ReplaceExactlyOnce(lines,
                "\tTitle = " + StockScreenCensusFixture.Title, "\tTitle = " + Title);

            // The collapsed dish, in the shape stock writes for a building that finished
            // collapsing (DestructibleBuilding.Save: intact = False).
            int destructibles = StockScreenCensusFixture.FindScenario(lines, "ScenarioDestructibles");
            int dish = StockScreenCensusFixture.FindChild(lines, destructibles, DestroyedBuilding);
            StockScreenCensusFixture.ReplaceInBlockExactlyOnce(lines, dish,
                "\t\t\tintact = True", "\t\t\tintact = False");

            return string.Join(Crlf, lines);
        }

        /// <summary>The rows appended to the census ledger, in append order.</summary>
        internal static List<GameAction> BuildLedgerRows()
        {
            int seq = CensusLastKscSeq;
            return new List<GameAction>
            {
                // PAST: the collapse the save already carries (stock writes no cost for it).
                new GameAction
                {
                    UT = DestructionUT,
                    Type = GameActionType.FacilityDestruction,
                    ActionId = "act_1f0e2d3c4b5a4968877665544332210b",
                    Sequence = ++seq,
                    FacilityId = DestroyedBuilding,
                },
                // FUTURE: the committed repair of that same collapse.
                new GameAction
                {
                    UT = RepairUT,
                    Type = GameActionType.FacilityRepair,
                    ActionId = "act_1f0e2d3c4b5a4968877665544332210c",
                    Sequence = ++seq,
                    FacilityId = DestroyedBuilding,
                    FacilityCost = RepairCost,
                },
            };
        }

        internal static string BuildLedger(string censusText)
        {
            if (!censusText.EndsWith("}" + Crlf, StringComparison.Ordinal))
                throw new InvalidOperationException("census ledger does not end with a CRLF-terminated node");
            var sb = new StringBuilder(censusText);
            var holder = new ConfigNode("LEDGER");
            foreach (var row in BuildLedgerRows())
                row.SerializeInto(holder);
            foreach (ConfigNode node in holder.nodes)
                foreach (string line in StockScreenCensusFixture.SerializeNode(node, "GAME_ACTION", 0))
                    sb.Append(line).Append(Crlf);
            return sb.ToString();
        }
    }
}
