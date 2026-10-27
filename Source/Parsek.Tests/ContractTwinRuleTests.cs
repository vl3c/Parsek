using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Design 3.15 step 8 (owner ruling 2026-10-26): a contract the committed future accepts
    /// keeps its twin (the same goal stock generates again after a rewind, under a new guid)
    /// out of the offer pool, AutoAccept included. The identity is stock's own uniqueness id
    /// (<c>Contract.ContractID</c>), captured on the accept row, or (owner ruling 2026-10-27)
    /// the goal key of a once-per-career goal (<see cref="ContractTwinGoalKeyTests"/>).
    /// </summary>
    [Collection("Sequential")]
    public class ContractTwinRuleTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ContractTwinRuleTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            GameStateStore.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            CommittedFutureIndexCache.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            CommittedFutureIndexCache.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static GameAction Accept(string guid, double ut, long stockId, string title = "Orbit the Mun", string rec = null)
            => new GameAction
            {
                UT = ut, Type = GameActionType.ContractAccept, ContractId = guid, ContractTitle = title,
                ContractStockId = stockId, RecordingId = rec,
            };

        private static CommittedFutureIndex Index(params GameAction[] rows)
            => CommittedFutureIndex.Build(rows, id => id == "committed-rec", id => id == "committed-rec" ? "Mun Lander" : null, null);

        // ------------------------------------------------------------------
        // The pure rule
        // ------------------------------------------------------------------

        // catches: a twin offered after a rewind, or a non-twin (other goal, the committed
        // contract itself, a past accept, an unknown id) refused.
        [Fact]
        public void FindTwinHolder_OnlyAnotherGuidWithTheStockIdOfACommittedFutureAccept()
        {
            CommittedFutureIndex index = Index(
                Accept("g-committed", 1000, 4242),
                Accept("g-past", 100, 777));

            CommittedFutureEntry holder = ContractTwinRule.FindTwinHolder(index, 4242, "g-twin", 500);
            Assert.NotNull(holder);
            Assert.Equal("g-committed", holder.Key);
            Assert.Equal(4242L, holder.StockContractId);

            Assert.Null(ContractTwinRule.FindTwinHolder(index, 4242, "g-committed", 500)); // itself
            Assert.Null(ContractTwinRule.FindTwinHolder(index, 4242, "G-COMMITTED", 500)); // guid case
            Assert.Null(ContractTwinRule.FindTwinHolder(index, 4242, "g-twin", 1000));    // accept reached
            Assert.Null(ContractTwinRule.FindTwinHolder(index, 777, "g-other", 500));     // accept in the past
            Assert.Null(ContractTwinRule.FindTwinHolder(index, 9999, "g-other", 500));    // another goal
            Assert.Null(ContractTwinRule.FindTwinHolder(index, 0, "g-twin", 500));        // id unknown
            Assert.Null(ContractTwinRule.FindTwinHolder(null, 4242, "g-twin", 500));
        }

        // catches: an accept row with no captured id (an older save) judging every id-0 offer.
        [Fact]
        public void FindTwinHolder_AnAcceptWithNoCapturedId_IsNeverAHolder()
        {
            CommittedFutureIndex index = Index(Accept("g-old", 1000, 0));
            Assert.Null(ContractTwinRule.FindTwinHolder(index, 0, "g-twin", 500));
            Assert.Null(ContractTwinRule.FindTwinHolder(index, 5, "g-twin", 500));
        }

        // catches: an accept in an uncommitted (live or discarded) recording holding a twin out.
        [Fact]
        public void FindTwinHolder_IgnoresAnAcceptNoCommittedFlightHolds()
        {
            CommittedFutureIndex index = Index(
                Accept("g-live", 1000, 4242, rec: "live-tree"),
                Accept("g-flight", 2000, 5151, rec: "committed-rec"));
            Assert.Null(ContractTwinRule.FindTwinHolder(index, 4242, "g-twin", 500));
            CommittedFutureEntry holder = ContractTwinRule.FindTwinHolder(index, 5151, "g-twin", 500);
            Assert.NotNull(holder);
            Assert.Contains("in Mun Lander", ContractTwinRule.DescribeHolder(holder));
        }

        // catches: repeatable goals (a part test, tourism) zeroed for good: once the committed
        // accept is past, stock may offer the same goal again, exactly as stock itself would
        // after the committed contract ends.
        [Fact]
        public void FindTwinHolder_ARepeatableGoalIsOfferedAgainOnceTheCommittedAcceptIsPast()
        {
            CommittedFutureIndex index = Index(Accept("g-parttest", 1000, 31337, "Test RT-10 at the pad"));
            Assert.NotNull(ContractTwinRule.FindTwinHolder(index, 31337, "g-new", 999));
            Assert.Null(ContractTwinRule.FindTwinHolder(index, 31337, "g-new", 1000.5));
        }

        [Fact]
        public void SelectOfferedTwins_ReturnsOnlyOfferedTwins_InInputOrder()
        {
            CommittedFutureIndex index = Index(Accept("g-committed", 1000, 4242), Accept("g-b", 1000, 88));
            var list = new List<ContractTwinRule.OfferedContract>
            {
                new ContractTwinRule.OfferedContract { Index = 0, Guid = "a", StockContractId = 4242, Offered = false },
                new ContractTwinRule.OfferedContract { Index = 1, Guid = "b", StockContractId = 4242, Offered = true },
                new ContractTwinRule.OfferedContract { Index = 2, Guid = "c", StockContractId = 1, Offered = true },
                new ContractTwinRule.OfferedContract { Index = 3, Guid = "g-b", StockContractId = 88, Offered = true },
                new ContractTwinRule.OfferedContract { Index = 4, Guid = "e", StockContractId = 88, Offered = true },
            };
            var twins = ContractTwinRule.SelectOfferedTwins(list, index, 10);
            Assert.Equal(new[] { 1, 4 }, twins.Select(t => t.Key.Index).ToArray());
            Assert.Equal("g-committed", twins[0].Value.Key);
            Assert.Equal("g-b", twins[1].Value.Key);
            Assert.Empty(ContractTwinRule.SelectOfferedTwins(null, index, 10));
        }

        // catches: the live doors reading another index than the shared committed-future one,
        // or a twin refused without a line naming both contracts.
        [Fact]
        public void IsTwinNow_ReadsTheSharedIndex_AndLogsBothContracts()
        {
            Ledger.AddAction(Accept("g-committed", 1000, 4242));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 500;

            Assert.True(ContractTwinRule.IsTwinNow(4242, "g-twin", "Orbit the Mun", "Generation"));
            Assert.Contains(logLines, l => l.Contains("[INFO][ContractTwin]") && l.Contains("Generation:")
                && l.Contains("guid=g-twin") && l.Contains("stockId=4242") && l.Contains("guid=g-committed")
                && l.Contains("accepted by the committed timeline at ut=1000") && l.Contains("kept out of the offer pool"));

            logLines.Clear();
            Assert.False(ContractTwinRule.IsTwinNow(4242, "g-committed", "Orbit the Mun", "Generation"));
            Assert.False(ContractTwinRule.IsTwinNow(1, "g-x", "Other", "Auto-accept"));
            Assert.False(ContractTwinRule.IsTwinNow(0, "g-x", "Other", "Auto-accept"));
            Assert.DoesNotContain(logLines, l => l.Contains("[ContractTwin]"));

            CommittedFutureIndexCache.NowUtProviderForTesting = () => 1500;
            Assert.False(ContractTwinRule.IsTwinNow(4242, "g-twin", "Orbit the Mun", "Generation"));
        }

        // catches: the generation door logging once per refused seed (one offer pass can refuse
        // the same twin many times), or the other doors losing their per-event line.
        [Fact]
        public void IsTwinNow_GenerationDoor_LogsOncePerHolder()
        {
            Ledger.AddAction(Accept("g-committed", 1000, 4242));
            Ledger.AddAction(Accept("g-other", 1200, 77));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 500;

            for (int i = 0; i < 5; i++)
                Assert.True(ContractTwinRule.IsTwinNow(4242, "g-seed" + i, "Orbit the Mun", "Generation",
                    rateLimitPerHolder: true));
            Assert.True(ContractTwinRule.IsTwinNow(77, "g-seed-x", "Test a part", "Generation",
                rateLimitPerHolder: true));
            Assert.Single(logLines, l => l.Contains("Generation:") && l.Contains("guid=g-committed"));
            Assert.Single(logLines, l => l.Contains("Generation:") && l.Contains("guid=g-other"));

            Assert.True(ContractTwinRule.IsTwinNow(4242, "g-a", "Orbit the Mun", "Auto-accept"));
            Assert.True(ContractTwinRule.IsTwinNow(4242, "g-b", "Orbit the Mun", "Auto-accept"));
            Assert.Equal(2, logLines.Count(l => l.Contains("Auto-accept:")));
        }

        // Pins the rule's scope (design 3.15 item 8, owner ruling 2026-10-27): stock's own
        // ContractID for the goal-level types, plus the goal key for the guid-seeded types whose
        // goal stock offers once per career; the todo is closed. No stock id is built here: the
        // hash is stock's, and faking one would prove nothing about which types repeat it.
        [Fact]
        public void Scope_NamesBothIdentities_AndTheTodoIsClosed()
        {
            var e = LedgerEventIdentityRegistry.Entries.Single(x => x.Name == "ContractAccept");
            Assert.Contains("goal-level", e.EnforcedBy);
            Assert.Contains("ContractGoalKey", e.EnforcedBy);
            Assert.Contains("CONTRACT-TWIN-GUID-SEEDED-TYPES", e.EnforcedBy);

            string todo = File.ReadAllText(Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "docs", "dev", "todo-and-known-bugs.md")));
            Assert.Contains("\n## ~~CONTRACT-TWIN-GUID-SEEDED-TYPES:", todo.Replace("\r\n", "\n"));
        }

        // ------------------------------------------------------------------
        // Capture: the stock id rides the accept event and row
        // ------------------------------------------------------------------

        [Fact]
        public void StockId_DetailSuffix_IsEmptyForZero_AndInvariant()
        {
            Assert.Equal("", GameStateRecorder.FormatContractStockIdDetail(0));
            Assert.Equal(";cid=-1234567890123", GameStateRecorder.FormatContractStockIdDetail(-1234567890123L));
        }

        // catches: the converter dropping the id, or an older detail with no cid reading as a
        // known id.
        [Fact]
        public void Converter_ReadsTheStockId_AndAnOlderDetailHasNone()
        {
            var evt = new GameStateEvent
            {
                ut = 100, eventType = GameStateEventType.ContractAccepted, key = "g1",
                detail = "title=Orbit the Mun;deadlineAbsUT=NaN;type=ExploreBody;funds=100;failFunds=10;failRep=1"
                    + GameStateRecorder.FormatContractStockIdDetail(-987654321L),
            };
            GameAction row = GameStateEventConverter.ConvertEvent(evt, null);
            Assert.NotNull(row);
            Assert.Equal(-987654321L, row.ContractStockId);
            Assert.Contains(logLines, l => l.Contains("ConvertContractAccepted") && l.Contains("stockContractId=-987654321"));

            evt.detail = "title=Orbit the Mun;deadlineAbsUT=NaN;type=ExploreBody;funds=100;failFunds=10;failRep=1";
            Assert.Equal(0L, GameStateEventConverter.ConvertEvent(evt, null).ContractStockId);
        }

        [Fact]
        public void StockId_RoundTrips_AndIsAbsentWhenZero()
        {
            var root = new ConfigNode("ROOT");
            Accept("g1", 100, 4242).SerializeInto(root);
            ConfigNode node = root.GetNode("GAME_ACTION");
            Assert.Equal("4242", node.GetValue(GameAction.ContractStockIdKey));
            Assert.Equal(4242L, GameAction.DeserializeFrom(node).ContractStockId);

            var plain = new ConfigNode("ROOT");
            Accept("g2", 100, 0).SerializeInto(plain);
            Assert.Null(plain.GetNode("GAME_ACTION").GetValue(GameAction.ContractStockIdKey));
            Assert.Equal(0L, GameAction.DeserializeFrom(plain.GetNode("GAME_ACTION")).ContractStockId);
        }

        [Fact]
        public void Index_CarriesTheStockIdOnAcceptEntriesOnly()
        {
            CommittedFutureIndex index = Index(
                Accept("g1", 1000, 4242),
                new GameAction { UT = 1000, Type = GameActionType.ContractComplete, ContractId = "g1", ContractStockId = 4242 });
            Assert.Equal(4242L, index.FirstFuture(CommittedFutureKind.ContractAccept, "g1", 0).StockContractId);
            Assert.Equal(0L, index.FirstFuture(CommittedFutureKind.ContractComplete, "g1", 0).StockContractId);
            Assert.Equal(4242L, index.FirstFuture(CommittedFutureKind.ContractAccept, "g1", 0)
                .WithAgentTitle("Agent").StockContractId);
        }

        // ------------------------------------------------------------------
        // Wiring (the live doors run only inside KSP)
        // ------------------------------------------------------------------

        /// <summary>A source file with comments stripped (a commented-out call never passes) and
        /// whitespace runs collapsed (a call wrapped over two lines still matches).</summary>
        private static string Code(string relative)
        {
            return System.Text.RegularExpressions.Regex.Replace(
                SourceScanText.StripCSharpComments(ReadSource(relative)), @"\s+", " ");
        }

        private static string ReadSource(string relative)
        {
            string path = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "Source", "Parsek", relative));
            Assert.True(File.Exists(path), "source file not found for scan: " + path);
            return File.ReadAllText(path);
        }

        // catches: one of the three doors (generation, auto-accept, the patch's withdrawal) or the
        // capture losing its call.
        [Fact]
        public void Wiring_TheThreeDoorsAndTheCaptureCallTheRule()
        {
            string gen = Code(Path.Combine("Patches", "ContractTwinGenerationPatch.cs"));
            Assert.Contains("typeof(int), typeof(Contract.ContractPrestige), typeof(Type)", gen);
            Assert.Contains("ContractTwinRule.IsTwinNow(stockId, __result.ContractGuid.ToString(), __result.Title, \"Generation\", "
                + "rateLimitPerHolder: true, goalKey: goalKey)", gen);
            Assert.Contains("string goalKey = ContractGoalKeyReader.ReadIfJudged(__result, CommittedFutureIndexCache.Current, "
                + "CommittedFutureIndexCache.CurrentUT(), \"Generation\");", gen);
            Assert.Contains("__result.GenerateFailed();", gen);
            Assert.Contains("__result = null;", gen);
            Assert.NotNull(Parsek.Patches.ContractTwinGenerationPatch.ResolveTargetMethodForTesting());

            string accept = Code(Path.Combine("Patches", "ContractAcceptPatch.cs"));
            Assert.Contains("if (ShouldWithdrawAutoAcceptTwin(__instance, keyString, title))", accept);
            Assert.Contains("ContractTwinRule.IsTwinNow(stockId, keyString, title, \"Auto-accept\", goalKey: goalKey)", accept);
            Assert.Contains("string goalKey = ContractGoalKeyReader.ReadIfJudged(contract, CommittedFutureIndexCache.Current, "
                + "CommittedFutureIndexCache.CurrentUT(), \"Auto-accept\");", accept);
            Assert.Contains("if (!contract.AutoAccept || contract.ContractState != Contracts.Contract.State.Offered)", accept);

            string patcher = Code(Path.Combine("GameActions", "KspStatePatcher.cs"));
            int core = patcher.IndexOf("private static void PatchContractsCore(", StringComparison.Ordinal);
            int call = patcher.IndexOf("WithdrawOfferedContractTwins(currentContracts, parameterClockUT);", core, StringComparison.Ordinal);
            Assert.True(core >= 0 && call > core, "PatchContractsCore no longer withdraws twin offers at its real clock");
            // A withdrawal has no undo: no real clock, no withdrawal (never the UT-0 fallback).
            int withdraw = patcher.IndexOf("internal static int WithdrawOfferedContractTwins(List<Contract> current, double clockUT)",
                StringComparison.Ordinal);
            Assert.True(withdraw >= 0, "WithdrawOfferedContractTwins signature not found");
            int guard = patcher.IndexOf("if (!IsRealClock(clockUT))", withdraw, StringComparison.Ordinal);
            int select = patcher.IndexOf("ContractTwinRule.SelectOfferedTwins(", withdraw, StringComparison.Ordinal);
            Assert.True(guard > withdraw && guard < select, "the withdrawal no longer refuses without a real clock");
            Assert.Contains("double nowUT = clockUT;", patcher);
            Assert.DoesNotContain("CommittedFutureIndexCache.CurrentUT()", patcher.Substring(withdraw, select - withdraw));
            Assert.Contains("CommittedFutureIndex twinIndex = CommittedFutureIndexCache.Current;", patcher);
            Assert.Contains("ContractTwinRule.SelectOfferedTwins(offered, twinIndex, nowUT)", patcher);
            Assert.Contains("ContractGoalKeyReader.ReadIfJudged(c, twinIndex, nowUT, \"PatchContracts\")",
                patcher.Substring(withdraw, select - withdraw));
            Assert.Contains("live.Withdraw();", patcher);

            string handlers = Code("GameStateRecorder.Handlers.cs");
            Assert.Contains("FormatContractStockIdDetail(ReadContractStockId(contract))", handlers);
            Assert.Contains("FormatContractGoalKeyDetail(ReadContractGoalKey(contract, contractNode))", handlers);
        }

        [Fact]
        public void Registry_TheAcceptEntryNamesTheTwinRule()
        {
            var e = LedgerEventIdentityRegistry.Entries.Single(x => x.Name == "ContractAccept");
            Assert.Contains("ContractTwinRule", e.EnforcedBy);
        }
    }
}
