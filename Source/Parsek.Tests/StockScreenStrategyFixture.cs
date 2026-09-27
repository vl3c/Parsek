using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Parsek.Tests
{
    /// <summary>
    /// Builds the committed <c>harness/fixtures/saves/stock-screen-census-strategy</c> fixture
    /// BY CONSTRUCTION from a fresh <see cref="StockScreenCensusFixture"/> build: the same
    /// rewound career WITHOUT its active strategy, so the Administration building's one
    /// level-1 slot is free and stock itself would allow activating
    /// <see cref="StockScreenCensusFixture.ActivatedLaterStrategy"/> now. The committed
    /// timeline still activates it later, so the only refusal left on that Accept button is
    /// Parsek's (the future-activation block, section 4 S1 of
    /// docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md). On the census the
    /// strategy is already refused by stock for the full slot, and Parsek's reason stands
    /// aside there (stock-first precedence, ruling F7), so the activation block cannot be
    /// pressed on it. The lane <c>KB-5-strategy-activate-block-after-rewind</c> flies this one.
    ///
    /// <para>What differs from the census, and nothing else: the census's past
    /// <see cref="StockScreenCensusFixture.ActiveStrategy"/> activation (UT 390) and its
    /// committed deactivation (UT 150000) are not in the ledger, stock's
    /// <c>STRATEGIES</c> list is empty (as the base career's is), and the funds pool is the
    /// base's, since no setup cost was ever charged. The rows the census keeps keep their
    /// action ids and sequence numbers, so the gaps where the two dropped rows were stay
    /// visible.</para>
    ///
    /// <para>Deterministic; <c>StockScreenStrategyFixtureTests</c> rebuilds it and compares it
    /// with the committed tree, so a hand edit on either side reds the suite. A change to
    /// the census builder moves this fixture too (rewrite both).</para>
    /// </summary>
    internal static class StockScreenStrategyFixture
    {
        internal const string FixtureName = "stock-screen-census-strategy";
        internal const string Title = "stock-screen-census-strategy (CAREER)";

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

            string baseLedgerPath = Path.Combine(StockScreenCensusFixture.SavesDir(repoRoot),
                StockScreenCensusFixture.BaseName, "Parsek", "GameState", "ledger.pgld");
            if (!File.Exists(baseLedgerPath))
                throw new InvalidOperationException("base ledger not found: " + baseLedgerPath);
            string baseLedger = Encoding.UTF8.GetString(File.ReadAllBytes(baseLedgerPath));
            files["Parsek/GameState/ledger.pgld"] = Encoding.UTF8.GetBytes(BuildLedger(baseLedger));
            return files;
        }

        internal static string BuildSave(string censusText)
        {
            if (censusText.IndexOf(Crlf, StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("census persistent.sfs is not CRLF");
            var lines = new List<string>(censusText.Split(new[] { Crlf }, StringSplitOptions.None));

            StockScreenCensusFixture.ReplaceExactlyOnce(lines,
                "\tTitle = " + StockScreenCensusFixture.Title, "\tTitle = " + Title);

            // No setup cost was ever charged: the base career's funds pool.
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            int funding = StockScreenCensusFixture.FindScenario(lines, "Funding");
            StockScreenCensusFixture.ReplaceInBlockExactlyOnce(lines, funding,
                "\t\tfunds = " + (StockScreenCensusFixture.BaseFunds
                    - StockScreenCensusFixture.ActiveStrategySetupCost).ToString("R", ic),
                "\t\tfunds = " + StockScreenCensusFixture.BaseFunds.ToString("R", ic));

            // Stock's STRATEGIES list back to the base's empty one.
            int strategySystem = StockScreenCensusFixture.FindScenario(lines, "StrategySystem");
            int strategies = StockScreenCensusFixture.FindChild(lines, strategySystem, "STRATEGIES");
            int strategy = StockScreenCensusFixture.FindChild(lines, strategies, "STRATEGY");
            int strategyEnd = StockScreenCensusFixture.BlockEnd(lines, strategy);
            if (lines[strategy + 2] != "\t\t\t\tname = " + StockScreenCensusFixture.ActiveStrategy)
                throw new InvalidOperationException("census STRATEGY is not " + StockScreenCensusFixture.ActiveStrategy);
            lines.RemoveRange(strategy, strategyEnd - strategy + 1);
            if (StockScreenCensusFixture.BlockEnd(lines, strategies) != strategies + 2)
                throw new InvalidOperationException("STRATEGIES is not empty after the removal");

            return string.Join(Crlf, lines);
        }

        /// <summary>The census's rows minus the two that name its active strategy.</summary>
        internal static List<GameAction> BuildLedgerRows()
        {
            return StockScreenCensusFixture.BuildLedgerRows()
                .Where(r => r.StrategyId != StockScreenCensusFixture.ActiveStrategy)
                .ToList();
        }

        internal static string BuildLedger(string baseText)
        {
            if (!baseText.EndsWith("}" + Crlf, StringComparison.Ordinal))
                throw new InvalidOperationException("base ledger does not end with a CRLF-terminated node");
            var sb = new StringBuilder(baseText);
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
