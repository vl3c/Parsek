using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Parsek.Tests.SaveRoundTrip
{
    /// <summary>
    /// Golden pin of the GAME_ACTION codec's exact output. Every row type is written twice,
    /// once with every field filled (capture sequence, conversion cause and sources set) and
    /// once sparse (every optional field unset), plus the capture-sequence edge values; the
    /// exact key / value sequence of the whole set is pinned by hash, a few header shapes are
    /// pinned literally, and a load of the pinned node must write back byte-identically. The
    /// load-side tallies a GAME_ACTION read feeds (legacy action ids, ContractAccept deadline
    /// resolutions) are pinned through the one-line summaries the ledger load prints. A move
    /// of codec code between types must keep every expectation here unchanged.
    /// </summary>
    [Collection("Sequential")]
    public class GameActionCodecGoldenTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GameActionCodecGoldenTests()
        {
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private const string PinnedSha256 = "1bf250ded5df0ae5d48f2d95f3f809fc046353a704e1c8932b9d182e9edede3f";
        private const int PinnedRowCount = 69;

        private static IEnumerable<GameActionType> RowTypes() =>
            Enum.GetValues(typeof(GameActionType)).Cast<GameActionType>()
                .Where(t => !LedgerLoadMigration.IsReservedRouteActionType(t));

        private static List<GameAction> GoldenRows()
        {
            var rows = new List<GameAction>();
            foreach (GameActionType t in RowTypes())
            {
                var rt = new FieldRoundTrip(new FieldSpec[0], seed: (int)t * 1000 + 17);
                GameAction full = rt.Build<GameAction>();
                full.Type = t;
                rows.Add(full);

                rows.Add(new GameAction
                {
                    UT = 100.25 + (int)t,
                    Type = t,
                    ActionId = "act_golden_sparse_" + ((int)t).ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
            }
            rows.Add(new GameAction
            {
                UT = 7.5, Type = GameActionType.FundsSpending, ActionId = "act_golden_unsequenced",
                CaptureSequence = CaptureSequence.Unsequenced, ConversionCause = "golden-cause",
                FundsSpent = 12.5f, FundsSpendingSource = FundsSpendingSource.Other,
            });
            rows.Add(new GameAction
            {
                UT = 8.5, Type = GameActionType.ReputationEarning, ActionId = "act_golden_negative_cseq",
                CaptureSequence = -7, NominalRep = 3f, RepSource = ReputationSource.Other,
            });
            rows.Add(new GameAction
            {
                UT = 9.5, Type = GameActionType.FundsEarning, ActionId = "act_golden_positive_cseq",
                CaptureSequence = 42, Sequence = 3, RecordingId = "rec-golden",
                FundsAwarded = 99f, FundsSource = FundsEarningSource.Recovery,
            });
            return rows;
        }

        /// <summary>Order-exact text of a node: one <c>name=value</c> line per value, children nested.</summary>
        private static void Canonical(ConfigNode node, StringBuilder sb, string indent)
        {
            sb.Append(indent).Append(node.name).Append(" {\n");
            for (int i = 0; i < node.values.Count; i++)
                sb.Append(indent).Append("  ").Append(node.values[i].name).Append('=').Append(node.values[i].value).Append('\n');
            for (int i = 0; i < node.nodes.Count; i++)
                Canonical(node.nodes[i], sb, indent + "  ");
            sb.Append(indent).Append("}\n");
        }

        private static string Canonical(ConfigNode node)
        {
            var sb = new StringBuilder();
            Canonical(node, sb, "");
            return sb.ToString();
        }

        private static string Sha256Hex(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                    sb.Append(hash[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private static ConfigNode WriteGolden()
        {
            var parent = new ConfigNode("LEDGER");
            foreach (GameAction row in GoldenRows())
                row.SerializeInto(parent);
            return parent;
        }

        private static string KeysOf(ConfigNode node) =>
            string.Join(",", Enumerable.Range(0, node.values.Count).Select(i => node.values[i].name));

        [Fact]
        public void Write_IsTheExactPinnedKeyValueSequence()
        {
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                ConfigNode parent = WriteGolden();
                string text = Canonical(parent);
                Assert.Equal(PinnedRowCount, parent.nodes.Count);
                Assert.True(Sha256Hex(text) == PinnedSha256,
                    "GAME_ACTION golden output changed (sha256 " + Sha256Hex(text) + "):\n" + text);
            });
        }

        [Fact]
        public void Write_HeaderShapes_ArePinnedLiterally()
        {
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                ConfigNode parent = WriteGolden();
                int n = parent.nodes.Count;
                ConfigNode unsequenced = parent.nodes[n - 3];
                ConfigNode negative = parent.nodes[n - 2];
                ConfigNode positive = parent.nodes[n - 1];

                Assert.Equal("ut,type,actionId,cseq,conversionCause,fundsSpent,fundsSpendingSource", KeysOf(unsequenced));
                Assert.Equal("-1", unsequenced.GetValue("cseq"));
                Assert.Equal("golden-cause", unsequenced.GetValue("conversionCause"));
                Assert.Equal("ut,type,actionId,cseq,nominalRep,repSource", KeysOf(negative));
                Assert.Equal("-7", negative.GetValue("cseq"));
                Assert.Equal("ut,type,actionId,recordingId,seq,cseq,fundsAwarded,fundsSource", KeysOf(positive));
                Assert.Equal("42", positive.GetValue("cseq"));

                ConfigNode sparseFunds = parent.nodes.Cast<ConfigNode>()
                    .Single(c => c.GetValue("actionId") == "act_golden_sparse_" + ((int)GameActionType.FundsEarning).ToString(System.Globalization.CultureInfo.InvariantCulture));
                Assert.Null(sparseFunds.GetValue("cseq"));
                Assert.Null(sparseFunds.GetValue("conversionCause"));
            });
        }

        [Fact]
        public void Load_OfTheGoldenNode_WritesBackByteIdentically()
        {
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                ConfigNode parent = WriteGolden();
                var reread = new ConfigNode("LEDGER");
                var loaded = new List<GameAction>();
                foreach (ConfigNode child in parent.GetNodes("GAME_ACTION"))
                {
                    GameAction a = GameAction.DeserializeFrom(child);
                    loaded.Add(a);
                    a.SerializeInto(reread);
                }

                // The one value a load normalizes: any negative capture number reads back as
                // Unsequenced, so the -7 row re-saves as -1. Everything else is byte-identical.
                string expected = Canonical(parent).Replace("\n    cseq=-7\n", "\n    cseq=-1\n");
                Assert.Equal(expected, Canonical(reread));
                int n = loaded.Count;
                Assert.Equal(CaptureSequence.Unsequenced, loaded[n - 3].CaptureSequence);
                Assert.Equal("golden-cause", loaded[n - 3].ConversionCause);
                Assert.Equal(CaptureSequence.Unsequenced, loaded[n - 2].CaptureSequence);
                Assert.Equal(42L, loaded[n - 1].CaptureSequence);
                Assert.Equal(FundsEarningSource.Recovery, loaded[n - 1].FundsSource);
                Assert.Equal(ReputationSource.Other, loaded[n - 2].RepSource);
            });
        }

        [Fact]
        public void Load_FeedsTheLegacyIdAndDeadlineTallies()
        {
            Ledger.ResetLegacyActionIdMigrationForTesting();
            Ledger.ResetContractDeadlineMigrationTally();

            var legacy = new ConfigNode("GAME_ACTION");
            legacy.AddValue("ut", "55.5");
            legacy.AddValue("type", ((int)GameActionType.FundsEarning).ToString(System.Globalization.CultureInfo.InvariantCulture));
            legacy.AddValue("recordingId", "rec-legacy");
            legacy.AddValue("seq", "4");
            legacy.AddValue("cseq", "abc");
            GameAction legacyRow = GameAction.DeserializeFrom(legacy);
            Assert.Equal("act_legacy_a1a3abe44b49f15c", legacyRow.ActionId);
            Assert.Equal(0L, legacyRow.CaptureSequence);

            string accept = ((int)GameActionType.ContractAccept).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var absolute = new ConfigNode("GAME_ACTION");
            absolute.AddValue("ut", "10"); absolute.AddValue("type", accept); absolute.AddValue("actionId", "act_a");
            absolute.AddValue("deadlineAbsUT", "500");
            var migrated = new ConfigNode("GAME_ACTION");
            migrated.AddValue("ut", "10"); migrated.AddValue("type", accept); migrated.AddValue("actionId", "act_b");
            migrated.AddValue("deadlineUT", "200");
            var unparseable = new ConfigNode("GAME_ACTION");
            unparseable.AddValue("ut", "10"); unparseable.AddValue("type", accept); unparseable.AddValue("actionId", "act_c");
            unparseable.AddValue("deadlineAbsUT", "nope");
            var absent = new ConfigNode("GAME_ACTION");
            absent.AddValue("ut", "10"); absent.AddValue("type", accept); absent.AddValue("actionId", "act_d");
            foreach (ConfigNode node in new[] { absolute, migrated, migrated, unparseable, absent, absent, absent })
                GameAction.DeserializeFrom(node);

            logLines.Clear();
            Ledger.EmitLegacyActionIdMigrationLogOnce();
            Ledger.EmitContractDeadlineMigrationSummary("golden");

            Assert.Contains(logLines, l => l.Contains("[Ledger]")
                && l.Contains("Assigned deterministic ActionIds to 1 legacy actions"));
            Assert.Contains(logLines, l => l.Contains("[Ledger]") && l.Contains(
                "ContractAccept deadline resolution for 'golden': absolute=1, migratedFromDuration=2, noDeadline=3, unparseable=1"));
        }
    }
}
