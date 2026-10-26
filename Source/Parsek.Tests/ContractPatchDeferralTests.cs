using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// CONTRACT-RESTORED-AT-SCENE-LOAD-PAYS-TWICE: the deferred scene-load contract patch's
    /// bookkeeping (one run per defer, latest request wins, no re-entrant run from Parsek's
    /// own onContractsLoaded fire, dropped by a save load or a run-now patch), the clock it
    /// re-reads, and a source gate that the live <c>PatchContracts</c> routes through it.
    /// </summary>
    [Collection("Sequential")]
    public class ContractPatchDeferralTests : IDisposable
    {
        public ContractPatchDeferralTests()
        {
            KspStatePatcher.ResetDeferredContractPatch();
        }

        public void Dispose()
        {
            KspStatePatcher.ResetDeferredContractPatch();
        }

        [Fact]
        public void TwoDefers_RunOnce_WithTheLatestModuleAndClock()
        {
            var state = new KspStatePatcher.DeferredContractPatch();
            var first = new ContractsModule();
            var second = new ContractsModule();
            Assert.False(state.Defer(first, 10.0));
            Assert.True(state.Defer(second, 20.0));

            var runs = new List<(ContractsModule module, double clock)>();
            Assert.True(state.TryRun(c => c, (m, c) => runs.Add((m, c))));
            Assert.False(state.TryRun(c => c, (m, c) => runs.Add((m, c))));

            Assert.Single(runs);
            Assert.Same(second, runs[0].module);
            Assert.Equal(20.0, runs[0].clock);
            Assert.False(state.HasPending);
        }

        [Fact]
        public void HandlerWithNothingPending_DoesNothing()
        {
            var state = new KspStatePatcher.DeferredContractPatch();
            int runs = 0;
            Assert.False(state.TryRun(c => c, (m, c) => runs++));
            Assert.Equal(0, runs);
        }

        [Fact]
        public void ResetForSaveLoad_DropsAPendingPatch()
        {
            KspStatePatcher.DeferredContractPatchState.Defer(new ContractsModule(), 5.0);
            Assert.True(KspStatePatcher.DeferredContractPatchState.HasPending);

            KspStatePatcher.ResetForSaveLoad();

            Assert.False(KspStatePatcher.DeferredContractPatchState.HasPending);
            int runs = 0;
            Assert.False(KspStatePatcher.DeferredContractPatchState.TryRun(c => c, (m, c) => runs++));
            Assert.Equal(0, runs);
        }

        [Fact]
        public void HandlerFiredFromInsideTheCore_DoesNotRunAgain()
        {
            // The core fires onContractsLoaded itself (to refresh stock's UI); that lands in
            // the same handler while the first run is still on the stack.
            var state = new KspStatePatcher.DeferredContractPatch();
            state.Defer(new ContractsModule(), 1.0);
            int runs = 0;
            bool innerRan = true;
            state.TryRun(c => c, (m, c) =>
            {
                runs++;
                innerRan = state.TryRun(x => x, (m2, c2) => runs++);
            });
            Assert.Equal(1, runs);
            Assert.False(innerRan);
        }

        [Fact]
        public void ADeferWhileRunning_WaitsForTheNextHandlerCall()
        {
            var state = new KspStatePatcher.DeferredContractPatch();
            state.Defer(new ContractsModule(), 1.0);
            int runs = 0;
            state.TryRun(c => c, (m, c) =>
            {
                runs++;
                state.Defer(new ContractsModule(), 2.0);
                Assert.False(state.TryRun(x => x, (m2, c2) => runs++));
            });
            Assert.Equal(1, runs);
            Assert.True(state.HasPending);
            Assert.True(state.TryRun(c => c, (m, c) => runs++));
            Assert.Equal(2, runs);
        }

        [Fact]
        public void Cancel_ReportsWhetherOneWasWaiting()
        {
            var state = new KspStatePatcher.DeferredContractPatch();
            Assert.False(state.Cancel());
            state.Defer(new ContractsModule(), 3.0);
            Assert.True(state.Cancel());
            Assert.False(state.HasPending);
        }

        [Fact]
        public void TheResolvedClockIsWhatTheCoreReceives()
        {
            var state = new KspStatePatcher.DeferredContractPatch();
            state.Defer(new ContractsModule(), double.NaN);
            double seen = -1;
            state.TryRun(c => KspStatePatcher.ResolveDeferredContractClockUT(c, false, () => 345.5),
                (m, c) => seen = c);
            Assert.Equal(345.5, seen);
        }

        [Theory]
        [InlineData(120.0, false, 345.5, 120.0)]  // a real captured clock stands
        [InlineData(double.NaN, false, 345.5, 345.5)] // captured inside OnLoad: re-read after it
        [InlineData(double.NaN, true, 345.5, double.NaN)] // still loading: no clock yet
        public void DeferredClock_ReReadsANaNCapturedDuringOnLoad(
            double captured, bool onLoad, double live, double expected)
        {
            double got = KspStatePatcher.ResolveDeferredContractClockUT(captured, onLoad, () => live);
            Assert.Equal(expected, got);
        }

        [Fact]
        public void DeferredClock_NoLiveReaderStaysNaN()
        {
            Assert.True(double.IsNaN(KspStatePatcher.ResolveDeferredContractClockUT(double.NaN, false, null)));
        }

        // ---- source gate: the live entry point routes through the deferral ----

        [Fact]
        public void PatchContracts_DefersBeforeStockLoaded_AndOnlyThenRunsTheCore()
        {
            string body = MethodBody(StripComments(ReadPatcherSource()),
                "internal static void PatchContracts(ContractsModule contracts");
            int decide = body.IndexOf("DecideContractPatchTiming(", StringComparison.Ordinal);
            int loaded = body.IndexOf("ContractSystem.loaded", StringComparison.Ordinal);
            int deferBranch = body.IndexOf("ContractPatchTiming.DeferUntilStockLoaded", StringComparison.Ordinal);
            int deferCall = body.IndexOf("DeferContractPatchUntilStockLoaded(", StringComparison.Ordinal);
            int core = body.IndexOf("PatchContractsCore(", StringComparison.Ordinal);
            Assert.True(decide >= 0, "PatchContracts must ask DecideContractPatchTiming");
            Assert.True(loaded > decide, "the timing decision must read ContractSystem.loaded");
            Assert.True(deferBranch > loaded && deferCall > deferBranch,
                "PatchContracts must route DeferUntilStockLoaded to DeferContractPatchUntilStockLoaded");
            Assert.True(core > deferCall, "the core may run only after the deferral branch returned");
            int returnAfterDefer = body.IndexOf("return;", deferCall, StringComparison.Ordinal);
            Assert.True(returnAfterDefer > deferCall && returnAfterDefer < core,
                "the deferral branch must return before the core");
        }

        [Fact]
        public void TheStockLoadHandler_RunsTheCoreThroughTheHolder()
        {
            string body = MethodBody(StripComments(ReadPatcherSource()),
                "private static void OnStockContractsLoaded()");
            Assert.Contains("DeferredContractPatchState.TryRun(", body);
            Assert.Contains("ResolveDeferredContractClockUT(", body);
            Assert.Contains("PatchContractsCore(", body);
        }

        private static string ReadPatcherSource()
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(root, "Source", "Parsek", "GameActions", "KspStatePatcher.cs");
            Assert.True(File.Exists(path), "Source file not found at " + path);
            return File.ReadAllText(path);
        }

        private static string StripComments(string source)
        {
            var sb = new StringBuilder(source.Length);
            foreach (string line in source.Split('\n'))
            {
                int idx = line.IndexOf("//", StringComparison.Ordinal);
                sb.Append(idx >= 0 ? line.Substring(0, idx) : line);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static string MethodBody(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "method not found: " + signature);
            int open = source.IndexOf('{', at);
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }
            throw new InvalidOperationException("unbalanced braces after " + signature);
        }
    }
}
