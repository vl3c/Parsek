using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Source gate for the ledger row-writer classification (committed-value rule plan 2.4,
    /// phase 2): every <c>Ledger.AddAction</c> / <c>Ledger.AddActions</c> call under
    /// <c>Source/Parsek</c> names its <see cref="LedgerRowWriter"/> as a literal second argument,
    /// and the (file, enclosing method, writer) set equals the pinned table below. A new writer
    /// fails the suite until someone decides whether stock applied its rows to the live pool
    /// (the drawdown guard's stock pool reference steps only stock-applied rows). The one
    /// re-install of rows already in the ledger (<c>Ledger.RestoreActions</c>, which keeps each
    /// row's own stamp) is pinned the same way.
    ///
    /// <para>Comments are stripped and string literals masked (<see cref="SourceScanText"/>)
    /// before the scan, so a comment or a log string naming a call can neither satisfy nor trip
    /// the gate; the enclosing method is derived from the block structure, never from a regex
    /// over raw text.</para>
    /// </summary>
    public class LedgerRowWriterClassificationTests
    {
        private const string Stock = "StockApplied";
        private const string Parsek = "ParsekAuthored";

        /// <summary>
        /// The pinned classification: "relPath::method" -> the writer of each call there, in
        /// source order. StockApplied: stock applied the row's effect to the live pool itself.
        /// ParsekAuthored: Parsek authored the row with no stock application. In-game tests
        /// write stock-applied rows (the shape every row had before the writer existed).
        /// </summary>
        private static readonly Dictionary<string, string[]> Pinned =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                // A recording commit files the rows converted from the stock game-state events
                // its flight produced: stock paid them during the flight.
                ["GameActions/LedgerOrchestrator.cs::FileRecordingCommitRows"] = new[] { Stock },
                // KSC spending (part purchase, facility, hire): stock already charged it.
                ["GameActions/LedgerOrchestrator.cs::TryAddKscSpendingAction"] = new[] { Stock },
                // A strategy currency exchange stock already made.
                ["GameActions/LedgerOrchestrator.cs::OnStrategyCurrencyConversion"] = new[] { Stock },
                // An ordinary Discard keeps awards stock already paid during the discarded flight
                // (review case N1: not authoritative, stock-applied).
                ["GameActions/LedgerOrchestrator.cs::PreserveIrreversibleLiveGameplayOnDiscard"] = new[] { Stock },
                // KSC science (a recovery / transmission credit) stock already credited.
                ["GameActions/LedgerOrchestrator.cs::TryRecordKscScienceSubject"] = new[] { Stock },
                // Recovery crew experience (no pool), filed with the recovery stock made.
                ["GameActions/LedgerOrchestrator.cs::TryRecordRecoveryKerbalExperience"] = new[] { Stock },
                // A real vessel's crew recovery and the vessel recovery's funds: stock paid them.
                ["GameActions/LedgerOrchestrator.cs::OnRealVesselCrewRecovered"] = new[] { Stock },
                ["GameActions/LedgerOrchestrator.cs::OnRealVesselRecovered"] = new[] { Stock },
                // A retired Re-Fly provisional's awards: "the walk would patch the career below
                // what KSP already paid".
                ["GameActions/LedgerOrchestrator.cs::FileRetiredReFlyProvisionalRows"] = new[] { Stock },
                // The measured stock pool leak: a change stock already made to the pool.
                ["GameActions/KspStatePatcher.cs::FileStockPoolAdjustment"] = new[] { Stock },
                // The rollout charge stock made at launch.
                ["GameActions/LedgerRolloutAdoption.cs::RecordVesselRolloutSpending"] = new[] { Stock },
                // The recovery funds stock paid, paired to its recording.
                ["GameActions/LedgerRecoveryFundsPairing.cs::TryAddVesselRecoveryFundsAction"] = new[] { Stock },
                // Old-save and broken-ledger recovery convert stock events the pool already
                // holds (stock applied them; a ParsekAuthored stamp would count them twice
                // next to a saved reference, see the todo entry's phase 2 note).
                ["GameActions/LedgerLoadMigration.cs::MigrateOldSaveEvents"] = new[] { Stock },
                ["GameActions/LedgerLoadMigration.cs::TryRecoverBrokenLedgerOnLoad"] = new[] { Stock, Stock },
                // In-game tests.
                ["InGameTests/RuntimeTests.cs::CurrencyConverterStrategy_LedgerMatchesNetCredit"] = new[] { Stock },
                ["InGameTests/RuntimeTests.cs::WriteLedgerVisibleFundsRow"] = new[] { Stock },
                ["InGameTests/RuntimeTests.cs::WriteLedgerVisibleReputationRow"] = new[] { Stock },
                ["InGameTests/RuntimeTests.cs::WriteLedgerVisibleScienceTopUp"] = new[] { Stock },
                ["InGameTests/RuntimeTests.cs::AddTopBarProbeActions"] = new[] { Stock, Stock, Stock, Stock },
                ["InGameTests/RuntimeTests.cs::AddCommittedOverlayFixture"] = new[] { Stock },
                ["InGameTests/IncompleteBallisticRuntimeTests.cs::ReFlyFromSpannedRecording_PreservesLaunchRowAndTombstonesPostRewindCrew"] = new[] { Stock },
                ["InGameTests/StockUiFacilityMenuTests.cs::AddCommittedFacilityUpgradeFixture"] = new[] { Stock },
                ["InGameTests/ReFlyRecoveryBundleRuntimeTest.cs::ReFlyMergeRetiresRecoveryBundleAndStopsCareerLogReassert"] = new[] { Stock, Stock, Stock, Stock, Stock },
                ["InGameTests/DiscardEconomyPreservationInGameTest.cs::RunSeededDiscardScenario"] = new[] { Stock },
            };

        private static readonly Dictionary<string, int> PinnedRestores =
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["ReconciliationBundle.cs::Restore"] = 1,
            };

        private static readonly Regex AddCall = new Regex(@"\bLedger\s*\.\s*AddActions?\s*\(");
        private static readonly Regex RestoreCall = new Regex(@"\bLedger\s*\.\s*RestoreActions\s*\(");
        private static readonly Regex WriterArg = new Regex(@"^LedgerRowWriter\s*\.\s*(StockApplied|ParsekAuthored)$");

        private static readonly HashSet<string> ControlKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "for", "foreach", "while", "using", "lock", "switch", "catch", "return", "new",
            "delegate", "fixed", "when", "nameof", "typeof", "sizeof", "default",
        };

        internal sealed class WriterCall
        {
            internal string Key;
            internal string Writer;  // StockApplied / ParsekAuthored / null (unclassified)
            internal string Argument;
        }

        [Fact]
        public void EveryLedgerWriterNamesItsClassification_AndMatchesThePinnedTable()
        {
            string root = ParsekSourceRoot();
            var calls = new List<WriterCall>();
            var restores = new Dictionary<string, int>(StringComparer.Ordinal);
            var problems = new List<string>();
            foreach (string path in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string rel = path.Substring(root.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/');
                if (rel.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("obj/", StringComparison.OrdinalIgnoreCase))
                    continue;
                string prepared = SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(path));
                calls.AddRange(ScanWriterCalls(rel, prepared, problems));
                foreach (Match m in RestoreCall.Matches(prepared))
                {
                    string key = rel + "::" + EnclosingMethodName(prepared, m.Index);
                    restores[key] = restores.TryGetValue(key, out int n) ? n + 1 : 1;
                }
            }

            foreach (var call in calls.Where(c => c.Writer == null))
            {
                problems.Add(call.Key + ": Ledger write without a literal LedgerRowWriter "
                    + "(argument: '" + (call.Argument ?? "<none>") + "')");
            }

            var actual = calls.Where(c => c.Writer != null)
                .GroupBy(c => c.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(c => c.Writer).ToArray(), StringComparer.Ordinal);
            foreach (var kv in actual.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (!Pinned.TryGetValue(kv.Key, out string[] pinned))
                    problems.Add(kv.Key + ": writer(s) [" + string.Join(",", kv.Value) + "] not in the pinned table "
                        + "(decide whether stock applied these rows to the live pool, then pin it)");
                else if (!pinned.SequenceEqual(kv.Value))
                    problems.Add(kv.Key + ": writers [" + string.Join(",", kv.Value) + "], table pins ["
                        + string.Join(",", pinned) + "]");
            }
            foreach (var kv in Pinned)
            {
                if (!actual.ContainsKey(kv.Key))
                    problems.Add(kv.Key + ": pinned but no classified Ledger write found");
            }
            foreach (var kv in restores)
            {
                if (!PinnedRestores.TryGetValue(kv.Key, out int pinned) || pinned != kv.Value)
                    problems.Add(kv.Key + ": " + kv.Value + " Ledger.RestoreActions call(s) not pinned");
            }
            foreach (var kv in PinnedRestores)
            {
                if (!restores.ContainsKey(kv.Key))
                    problems.Add(kv.Key + ": pinned Ledger.RestoreActions call not found");
            }

            Assert.True(problems.Count == 0,
                "ledger row-writer gate: " + string.Join("; ", problems));
            // Anti-vacuity: the scan must see the whole known writer set.
            Assert.Equal(Pinned.Values.Sum(v => v.Length), calls.Count);
            Assert.True(calls.Count >= 30, "the scan found only " + calls.Count + " Ledger writes");
        }

        [Fact]
        public void Scanner_IgnoresCommentsAndLiterals_AndFindsTheEnclosingMethod()
        {
            const string source =
                "namespace Parsek\n{\n    internal static class Decoy\n    {\n"
                + "        // Ledger.AddAction(commented, LedgerRowWriter.ParsekAuthored);\n"
                + "        internal static void Writer(GameAction a)\n        {\n"
                + "            string s = \"Ledger.AddAction(x)\";\n"
                + "            if (a != null)\n            {\n"
                + "                Ledger.AddAction(new GameAction { UT = 1.0 }, LedgerRowWriter.ParsekAuthored);\n"
                + "            }\n"
                + "            foreach (var b in new[] { a }) { Ledger.AddActions(new[] { b }); }\n"
                + "        }\n    }\n}\n";
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(source);
            var problems = new List<string>();
            List<WriterCall> calls = ScanWriterCalls("Decoy.cs", prepared, problems);

            Assert.Empty(problems);
            Assert.Equal(2, calls.Count);
            Assert.Equal("Decoy.cs::Writer", calls[0].Key);
            Assert.Equal(Parsek, calls[0].Writer);
            Assert.Equal("Decoy.cs::Writer", calls[1].Key);
            Assert.Null(calls[1].Writer);
        }

        internal static List<WriterCall> ScanWriterCalls(string rel, string prepared, List<string> problems)
        {
            var calls = new List<WriterCall>();
            foreach (Match m in AddCall.Matches(prepared))
            {
                int open = m.Index + m.Length - 1;
                List<string> args = SplitArguments(prepared, open);
                if (args == null)
                {
                    problems.Add(rel + " at " + m.Index + ": unbalanced Ledger write call");
                    continue;
                }
                string writer = null;
                string argument = args.Count >= 2 ? args[1] : null;
                if (argument != null)
                {
                    Match w = WriterArg.Match(argument);
                    if (w.Success)
                        writer = w.Groups[1].Value;
                }
                calls.Add(new WriterCall
                {
                    Key = rel + "::" + EnclosingMethodName(prepared, m.Index),
                    Writer = writer,
                    Argument = argument,
                });
            }
            return calls;
        }

        /// <summary>The top-level, trimmed arguments of the call whose '(' is at <paramref name="open"/>.</summary>
        private static List<string> SplitArguments(string prepared, int open)
        {
            var args = new List<string>();
            int depth = 0;
            int start = open + 1;
            for (int i = open; i < prepared.Length; i++)
            {
                char c = prepared[i];
                if (c == '(' || c == '{' || c == '[') depth++;
                else if (c == ')' || c == '}' || c == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        string last = prepared.Substring(start, i - start).Trim();
                        if (last.Length > 0 || args.Count > 0)
                            args.Add(Regex.Replace(last, @"\s+", " "));
                        return args;
                    }
                }
                else if (c == ',' && depth == 1)
                {
                    args.Add(Regex.Replace(prepared.Substring(start, i - start).Trim(), @"\s+", " "));
                    start = i + 1;
                }
            }
            return null;
        }

        /// <summary>
        /// The name of the innermost method whose body encloses <paramref name="index"/>: the
        /// innermost open block whose header (the text since the previous ';', '{' or '}') is
        /// a declaration ending in a balanced parameter list, named by an identifier that is
        /// not a control keyword and not preceded by 'new'. Lambdas ('=>') are skipped.
        /// </summary>
        internal static string EnclosingMethodName(string prepared, int index)
        {
            List<int> open = SourceScanText.OpenBlockStack(prepared, index);
            for (int b = open.Count - 1; b >= 0; b--)
            {
                int brace = open[b];
                int headerStart = brace - 1;
                while (headerStart >= 0 && prepared[headerStart] != ';' && prepared[headerStart] != '{'
                    && prepared[headerStart] != '}')
                    headerStart--;
                string header = prepared.Substring(headerStart + 1, brace - headerStart - 1).Trim();
                if (header.Contains("=>") || !header.EndsWith(")", StringComparison.Ordinal))
                    continue;
                int depth = 0;
                int paramsOpen = -1;
                for (int i = header.Length - 1; i >= 0; i--)
                {
                    if (header[i] == ')') depth++;
                    else if (header[i] == '(')
                    {
                        depth--;
                        if (depth == 0) { paramsOpen = i; break; }
                    }
                }
                if (paramsOpen <= 0)
                    continue;
                string before = header.Substring(0, paramsOpen).TrimEnd();
                Match name = Regex.Match(before, @"([A-Za-z_][A-Za-z0-9_]*)\s*(<[^<>()]*>)?$");
                if (!name.Success || ControlKeywords.Contains(name.Groups[1].Value))
                    continue;
                string prefix = before.Substring(0, name.Index).TrimEnd();
                if (prefix.EndsWith("new", StringComparison.Ordinal) || prefix.Length == 0)
                    continue;
                return name.Groups[1].Value;
            }
            return "<no-method>";
        }

        private static string ParsekSourceRoot()
        {
            string repo = SyntheticRecordingTests.ResolveProjectRoot();
            return Path.Combine(repo, "Source", "Parsek");
        }
    }
}
