using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure cells for <see cref="LoadReconcilePolicy"/> (the load-kind x state-category table,
    /// its two classifiers and the known-gap ids) and for <see cref="DiscardReFlyLoadIntent"/>.
    /// The wiring into OnLoad and RevertInterceptor is pinned by
    /// <see cref="LoadReconcileWiringGateTests"/>.
    /// </summary>
    [Collection("Sequential")]
    public class LoadReconcilePolicyTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;

        public LoadReconcilePolicyTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            DiscardReFlyLoadIntent.ResetForTesting();
        }

        public void Dispose()
        {
            DiscardReFlyLoadIntent.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
        }

        private static IEnumerable<LoadKind> AllKinds()
            => Enum.GetValues(typeof(LoadKind)).Cast<LoadKind>();

        private static IEnumerable<LoadStateCategory> AllCategories()
            => Enum.GetValues(typeof(LoadStateCategory)).Cast<LoadStateCategory>();

        private static readonly LoadKind[] RefinedInSessionKinds =
        {
            LoadKind.StockRevert, LoadKind.QuickloadFlight, LoadKind.InSessionOther,
        };

        // ---- classifiers ----

        [Theory]
        // initialLoadDone false: always Cold, whatever else is set.
        [InlineData(false, false, false, false, (int)EarlyLoadKind.Cold)]
        [InlineData(false, false, false, true, (int)EarlyLoadKind.Cold)]
        [InlineData(false, false, true, false, (int)EarlyLoadKind.Cold)]
        [InlineData(false, false, true, true, (int)EarlyLoadKind.Cold)]
        [InlineData(false, true, false, false, (int)EarlyLoadKind.Cold)]
        [InlineData(false, true, false, true, (int)EarlyLoadKind.Cold)]
        [InlineData(false, true, true, false, (int)EarlyLoadKind.Cold)]
        [InlineData(false, true, true, true, (int)EarlyLoadKind.Cold)]
        // rewinding wins over a pending Re-Fly invoke and a discard intent.
        [InlineData(true, true, false, false, (int)EarlyLoadKind.PlainRewind)]
        [InlineData(true, true, false, true, (int)EarlyLoadKind.PlainRewind)]
        [InlineData(true, true, true, false, (int)EarlyLoadKind.PlainRewind)]
        [InlineData(true, true, true, true, (int)EarlyLoadKind.PlainRewind)]
        // a pending Re-Fly invoke wins over a discard intent.
        [InlineData(true, false, true, false, (int)EarlyLoadKind.ReFlyStart)]
        [InlineData(true, false, true, true, (int)EarlyLoadKind.ReFlyStart)]
        [InlineData(true, false, false, true, (int)EarlyLoadKind.DiscardReFly)]
        [InlineData(true, false, false, false, (int)EarlyLoadKind.InSession)]
        public void ClassifyEarly_TruthTable(
            bool initialLoadDone, bool rewinding, bool reFlyInvoke, bool discard, int expected)
        {
            Assert.Equal((EarlyLoadKind)expected,
                LoadReconcilePolicy.ClassifyEarly(initialLoadDone, rewinding, reFlyInvoke, discard));
        }

        [Theory]
        [InlineData(false, false, false, (int)LoadKind.InSessionOther)]
        [InlineData(false, false, true, (int)LoadKind.InSessionOther)]
        [InlineData(false, true, false, (int)LoadKind.InSessionOther)]
        [InlineData(false, true, true, (int)LoadKind.QuickloadFlight)]
        [InlineData(true, false, false, (int)LoadKind.StockRevert)]
        [InlineData(true, false, true, (int)LoadKind.StockRevert)]
        [InlineData(true, true, false, (int)LoadKind.StockRevert)]
        [InlineData(true, true, true, (int)LoadKind.StockRevert)]
        public void ClassifyRefined_InSession_TruthTable(
            bool isRevert, bool isFlightToFlight, bool utWentBackwards, int expected)
        {
            Assert.Equal((LoadKind)expected, LoadReconcilePolicy.ClassifyRefined(
                EarlyLoadKind.InSession, isRevert, isFlightToFlight, utWentBackwards));
        }

        [Theory]
        [InlineData((int)EarlyLoadKind.Cold, (int)LoadKind.Cold)]
        [InlineData((int)EarlyLoadKind.PlainRewind, (int)LoadKind.PlainRewind)]
        [InlineData((int)EarlyLoadKind.ReFlyStart, (int)LoadKind.ReFlyStart)]
        [InlineData((int)EarlyLoadKind.DiscardReFly, (int)LoadKind.DiscardReFly)]
        public void ClassifyRefined_NonInSessionKind_IsFinalWhateverTheRevertSignals(
            int earlyValue, int expectedValue)
        {
            var early = (EarlyLoadKind)earlyValue;
            var expected = (LoadKind)expectedValue;
            foreach (bool isRevert in new[] { false, true })
                foreach (bool f2f in new[] { false, true })
                    foreach (bool backwards in new[] { false, true })
                        Assert.Equal(expected,
                            LoadReconcilePolicy.ClassifyRefined(early, isRevert, f2f, backwards));
            Assert.Equal(expected, LoadReconcilePolicy.ToLoadKind(early));
        }

        [Fact]
        public void ToLoadKind_InSessionNeedsTheRefinedClassifier()
        {
            Assert.Throws<ArgumentException>(() => LoadReconcilePolicy.ToLoadKind(EarlyLoadKind.InSession));
        }

        [Fact]
        public void FormatClassificationLine_CarriesEveryInput()
        {
            Assert.Equal(
                "Load classified: early=InSession refined=QuickloadFlight scene=FLIGHT initialLoadDone=true "
                + "rewinding=false reFlyInvoke=false discardReFly=none",
                LoadReconcilePolicy.FormatClassificationLine(
                    EarlyLoadKind.InSession, LoadKind.QuickloadFlight, "FLIGHT", true, false, false, null));
            Assert.Equal(
                "Load classified: early=DiscardReFly refined=DiscardReFly scene=EDITOR initialLoadDone=true "
                + "rewinding=false reFlyInvoke=false discardReFly=Prelaunch",
                LoadReconcilePolicy.FormatClassificationLine(
                    EarlyLoadKind.DiscardReFly, LoadKind.DiscardReFly, "EDITOR", true, false, false, "Prelaunch"));
        }

        // ---- the table ----

        [Fact]
        public void EveryKindHasADecisionForEveryCategory()
        {
            int cells = 0;
            foreach (LoadKind kind in AllKinds())
            {
                foreach (LoadStateCategory category in AllCategories())
                {
                    LoadReconcileDecision decision = LoadReconcilePolicy.Decide(kind, category);
                    Assert.True(Enum.IsDefined(typeof(LoadReconcileAction), decision.Action),
                        $"{kind} x {category}: undefined action {decision.Action}");
                    Assert.False(string.IsNullOrWhiteSpace(decision.Reason),
                        $"{kind} x {category}: every cell names the code that establishes it");
                    if (decision.KnownGapTodoId != null)
                        Assert.False(string.IsNullOrWhiteSpace(decision.KnownGapTodoId), $"{kind} x {category}");
                    cells++;
                }
            }
            Assert.Equal(
                Enum.GetValues(typeof(LoadKind)).Length * Enum.GetValues(typeof(LoadStateCategory)).Length,
                cells);
        }

        [Fact]
        public void EarlyDecisionsAreIdenticalAcrossRefinedInSessionKinds()
        {
            int early = 0;
            foreach (LoadStateCategory category in AllCategories())
            {
                if (!LoadReconcilePolicy.IsConsumedEarly(category))
                    continue;
                early++;
                LoadReconcileDecision first = LoadReconcilePolicy.Decide(RefinedInSessionKinds[0], category);
                foreach (LoadKind kind in RefinedInSessionKinds)
                {
                    Assert.True(first.Equals(LoadReconcilePolicy.Decide(kind, category)),
                        $"{category} is consumed in the OnLoad prologue, before the revert detector, so "
                        + $"{kind} must share {RefinedInSessionKinds[0]}'s decision");
                }
                Assert.Equal(first, LoadReconcilePolicy.DecideEarly(EarlyLoadKind.InSession, category));
            }
            Assert.True(early >= 12, "the nine staging nodes plus crew, kerbal slots and groups are early");
        }

        [Fact]
        public void DecideEarly_NonInSessionKind_IsDecideOfThatKind()
        {
            foreach (EarlyLoadKind early in new[]
                { EarlyLoadKind.Cold, EarlyLoadKind.PlainRewind, EarlyLoadKind.ReFlyStart, EarlyLoadKind.DiscardReFly })
            {
                foreach (LoadStateCategory category in AllCategories())
                {
                    Assert.Equal(
                        LoadReconcilePolicy.Decide(LoadReconcilePolicy.ToLoadKind(early), category),
                        LoadReconcilePolicy.DecideEarly(early, category));
                }
            }
        }

        [Fact]
        public void DecideEarly_ThrowsWhenTheRefinedKindsDisagree()
        {
            // The revert prune: only StockRevert prunes, so nothing before the revert detector
            // can decide it.
            var ex = Assert.Throws<InvalidOperationException>(() =>
                LoadReconcilePolicy.DecideEarly(EarlyLoadKind.InSession, LoadStateCategory.UntaggedLedgerRowsAfterCutoff));
            Assert.Contains("UntaggedLedgerRowsAfterCutoff", ex.Message);
        }

        [Fact]
        public void EveryStagingNodeCategoryIsConsumedEarly()
        {
            foreach (var kv in LoadReconcilePolicy.StagingNodeCategories)
                Assert.True(LoadReconcilePolicy.IsConsumedEarly(kv.Value), kv.Key + " -> " + kv.Value);
            Assert.Equal(9, LoadReconcilePolicy.StagingNodeCategories.Count);
            Assert.Equal(9, LoadReconcilePolicy.StagingNodeCategories.Values.Distinct().Count());
        }

        [Fact]
        public void DecideThrowsForUnknownMember()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                LoadReconcilePolicy.Decide((LoadKind)999, LoadStateCategory.SupersedeRows));
            foreach (LoadStateCategory category in AllCategories())
            {
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    LoadReconcilePolicy.Decide((LoadKind)999, category));
            }
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                LoadReconcilePolicy.Decide(LoadKind.Cold, (LoadStateCategory)999));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                LoadReconcilePolicy.IsConsumedEarly((LoadStateCategory)999));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                LoadReconcilePolicy.ToLoadKind((EarlyLoadKind)999));
        }

        [Fact]
        public void WiredCells_HoldTheDecisionsTheirConsumersRead()
        {
            // OnLoad passes Decide(refined, PendingScience) == Clear into DiscardStashedOnQuickload,
            // whose gate only passes on a QuickloadFlight load.
            Assert.Equal(LoadReconcileAction.Clear,
                LoadReconcilePolicy.Decide(LoadKind.QuickloadFlight, LoadStateCategory.PendingScience).Action);
            // Not wired (isRevert can be true on a ReFlyStart / DiscardReFly load), but the
            // revert prune is the only Prune in the column.
            foreach (LoadKind kind in AllKinds())
            {
                var action = LoadReconcilePolicy.Decide(kind, LoadStateCategory.UntaggedLedgerRowsAfterCutoff).Action;
                Assert.Equal(kind == LoadKind.StockRevert, action == LoadReconcileAction.Prune);
            }
        }

        [Fact]
        public void KnownGaps_AreExactlyTheFiledDefects()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (LoadKind kind in AllKinds())
                foreach (LoadStateCategory category in AllCategories())
                {
                    var decision = LoadReconcilePolicy.Decide(kind, category);
                    if (decision.IsKnownGap)
                    {
                        ids.Add(decision.KnownGapTodoId);
                        // A gap is a cell some load path reaches today.
                        Assert.DoesNotContain("not reachable", decision.Reason);
                    }
                }

            Assert.Equal(
                new[]
                {
                    LoadReconcilePolicy.GapAbandonedFutureEvents,
                    LoadReconcilePolicy.GapDetachedTreeLedgerRows,
                    LoadReconcilePolicy.GapFutureTerminalLeak,
                    LoadReconcilePolicy.GapReFlyLists,
                    LoadReconcilePolicy.GapRouteState,
                }.OrderBy(s => s, StringComparer.Ordinal),
                ids.OrderBy(s => s, StringComparer.Ordinal));

            // No gap on a kind whose state another mechanism owns.
            foreach (LoadStateCategory category in AllCategories())
            {
                foreach (LoadKind owned in new[] { LoadKind.Cold, LoadKind.PlainRewind, LoadKind.ReFlyStart })
                    Assert.False(LoadReconcilePolicy.Decide(owned, category).IsKnownGap, owned + " x " + category);
            }
        }

        [Fact]
        public void EveryKnownGapNamesAnOpenTodoEntry()
        {
            string todoPath = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..",
                "docs", "dev", "todo-and-known-bugs.md"));
            Assert.True(File.Exists(todoPath), "todo file not found at " + todoPath);
            string[] lines = File.ReadAllLines(todoPath);

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (LoadKind kind in AllKinds())
                foreach (LoadStateCategory category in AllCategories())
                {
                    var decision = LoadReconcilePolicy.Decide(kind, category);
                    if (decision.IsKnownGap)
                        ids.Add(decision.KnownGapTodoId);
                }
            Assert.NotEmpty(ids);

            foreach (string id in ids)
            {
                bool open = lines.Any(l => l.StartsWith("## " + id + ":", StringComparison.Ordinal));
                bool struck = lines.Any(l => l.StartsWith("## ~~" + id + ":", StringComparison.Ordinal));
                Assert.True(open,
                    $"KnownGap '{id}' must head an open '## {id}:' entry in docs/dev/todo-and-known-bugs.md"
                    + (struck ? " - it is struck: the fix should have flipped its LoadReconcilePolicy cells" : ""));
            }
        }

        [Fact]
        public void KnownLoadInitiators_DeclareAtLeastOneKindAndACall()
        {
            foreach (var kv in LoadReconcilePolicy.KnownLoadInitiators)
            {
                Assert.True(kv.Value.CallCount > 0, kv.Key);
                Assert.NotNull(kv.Value.Kinds);
                Assert.NotEmpty(kv.Value.Kinds);
                Assert.False(string.IsNullOrWhiteSpace(kv.Value.Note), kv.Key);
                Assert.DoesNotContain("\\", kv.Key);
            }
        }

        // ---- DiscardReFlyLoadIntent ----

        [Fact]
        public void DiscardIntent_ArmThenConsume_OnceOnly()
        {
            DiscardReFlyLoadIntent.Arm(RevertTarget.Launch, "sess-1");
            Assert.True(DiscardReFlyLoadIntent.IsArmed);
            Assert.Contains(logLines, l => l.Contains("[LoadPolicy]")
                && l.Contains("DiscardReFly load intent armed target=Launch sess=sess-1 expectedScene=SPACECENTER"));

            Assert.True(DiscardReFlyLoadIntent.TryConsume(GameScenes.SPACECENTER, "test", out RevertTarget target));
            Assert.Equal(RevertTarget.Launch, target);
            Assert.False(DiscardReFlyLoadIntent.IsArmed);
            Assert.Contains(logLines, l => l.Contains("[LoadPolicy]")
                && l.Contains("DiscardReFly load intent consumed at test: target=Launch sess=sess-1 scene=SPACECENTER"));

            // One-shot: the next load is not the discard's.
            Assert.False(DiscardReFlyLoadIntent.TryConsume(GameScenes.SPACECENTER, "test", out _));
        }

        [Fact]
        public void DiscardIntent_PrelaunchTargetsTheEditor()
        {
            Assert.Equal(GameScenes.EDITOR, DiscardReFlyLoadIntent.ExpectedSceneFor(RevertTarget.Prelaunch));
            Assert.Equal(GameScenes.SPACECENTER, DiscardReFlyLoadIntent.ExpectedSceneFor(RevertTarget.Launch));

            DiscardReFlyLoadIntent.Arm(RevertTarget.Prelaunch, "sess-2");
            Assert.True(DiscardReFlyLoadIntent.TryConsume(GameScenes.EDITOR, "test", out RevertTarget target));
            Assert.Equal(RevertTarget.Prelaunch, target);
        }

        [Fact]
        public void DiscardIntent_LoadInAnotherScene_IsDroppedAsStale()
        {
            DiscardReFlyLoadIntent.Arm(RevertTarget.Launch, "sess-3");

            Assert.False(DiscardReFlyLoadIntent.TryConsume(GameScenes.FLIGHT, "test", out _));
            Assert.False(DiscardReFlyLoadIntent.IsArmed);
            Assert.Contains(logLines, l => l.Contains("[LoadPolicy]") && l.Contains("[WARN]")
                && l.Contains("DiscardReFly load intent dropped as stale at test: target=Launch sess=sess-3 "
                    + "expectedScene=SPACECENTER loadedScene=FLIGHT"));
        }

        [Fact]
        public void DiscardIntent_Clear_DropsItAndLogs()
        {
            DiscardReFlyLoadIntent.Clear("nothing-yet");
            Assert.Contains(logLines, l => l.Contains("[LoadPolicy]")
                && l.Contains("clear requested reason=nothing-yet: nothing armed"));

            DiscardReFlyLoadIntent.Arm(RevertTarget.Launch, "sess-4");
            DiscardReFlyLoadIntent.Clear("DiscardReFly:scene-dispatch-failed");
            Assert.False(DiscardReFlyLoadIntent.IsArmed);
            Assert.Contains(logLines, l => l.Contains("[LoadPolicy]")
                && l.Contains("DiscardReFly load intent cleared reason=DiscardReFly:scene-dispatch-failed target=Launch sess=sess-4"));
            Assert.False(DiscardReFlyLoadIntent.TryConsume(GameScenes.SPACECENTER, "test", out _));
        }

        [Fact]
        public void DiscardIntent_ReArmWhileArmed_Warns()
        {
            DiscardReFlyLoadIntent.Arm(RevertTarget.Launch, "sess-old");
            DiscardReFlyLoadIntent.Arm(RevertTarget.Prelaunch, "sess-new");
            Assert.Contains(logLines, l => l.Contains("[LoadPolicy]") && l.Contains("[WARN]")
                && l.Contains("re-armed before the previous one was consumed: previous target=Launch sess=sess-old"));
            Assert.Equal(RevertTarget.Prelaunch, DiscardReFlyLoadIntent.ArmedTarget);
        }

        [Fact]
        public void DiscardIntent_ResetForTesting_AndTheStoreReset_ClearIt()
        {
            DiscardReFlyLoadIntent.Arm(RevertTarget.Launch, "sess-5");
            DiscardReFlyLoadIntent.ResetForTesting();
            Assert.False(DiscardReFlyLoadIntent.IsArmed);

            DiscardReFlyLoadIntent.Arm(RevertTarget.Launch, "sess-6");
            RecordingStore.ResetForTesting();
            Assert.False(DiscardReFlyLoadIntent.IsArmed);
        }
    }
}
