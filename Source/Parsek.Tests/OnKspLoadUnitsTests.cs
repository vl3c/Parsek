using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct cases for the two units split out of <c>LedgerOrchestrator.OnKspLoad</c>:
    /// <see cref="LedgerOrchestrator.ResolveOnKspLoadReconcileUT"/> and
    /// <see cref="LedgerOrchestrator.DecideOnKspLoadCurrentUtCutoff"/>. They run under de-DE
    /// to prove both lines format their UTs invariantly.
    /// </summary>
    [Collection("Sequential")]
    public class OnKspLoadUnitsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly CultureInfo savedCulture;

        public OnKspLoadUnitsTests()
        {
            savedCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            Ledger.ResetForTesting();
            LedgerHeldWindow.ResetForTesting();
        }

        public void Dispose()
        {
            Ledger.ResetForTesting();
            LedgerHeldWindow.ResetForTesting();
            Thread.CurrentThread.CurrentCulture = savedCulture;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void AddRowAt(double ut)
        {
            Ledger.AddAction(new GameAction
            {
                UT = ut,
                Type = GameActionType.FundsSpending,
                FundsSpent = 10f,
                FundsSpendingSource = FundsSpendingSource.Other,
            });
        }

        [Fact]
        public void ReconcileUT_NoHeldWindow_KeepsTheClockSilently()
        {
            Assert.Equal(500.5, LedgerOrchestrator.ResolveOnKspLoadReconcileUT(500.5));
            Assert.DoesNotContain(logLines, l => l.Contains("reconcile clock"));
        }

        [Fact]
        public void ReconcileUT_HeldAboveTheClock_RaisesAndLogs()
        {
            LedgerHeldWindow.SetForTesting(612.25);

            Assert.Equal(612.25, LedgerOrchestrator.ResolveOnKspLoadReconcileUT(500.5));
            Assert.Contains(logLines, l => l.Contains("[INFO][LedgerOrchestrator]")
                && l.EndsWith("OnKspLoad: reconcile clock 500.5 raised to the ledger held-through UT 612.25",
                    StringComparison.Ordinal));
        }

        [Fact]
        public void ReconcileUT_HeldBelowTheClock_KeepsTheClockSilently()
        {
            LedgerHeldWindow.SetForTesting(400.0);

            Assert.Equal(500.5, LedgerOrchestrator.ResolveOnKspLoadReconcileUT(500.5));
            Assert.DoesNotContain(logLines, l => l.Contains("reconcile clock"));
        }

        [Fact]
        public void Decide_FeatureOnReadyClockFutureRows_CutsAtTheCurrentUT()
        {
            AddRowAt(100.0);
            AddRowAt(800.0);

            Assert.True(LedgerOrchestrator.DecideOnKspLoadCurrentUtCutoff(500.5, true, true));
            Assert.Contains(logLines, l => l.EndsWith(
                "OnKspLoad recalc decision: useCurrentUtCutoff=True maxUT=500.5 featureSupportsCutoff=True " +
                "currentUtReady=True hasFutureActions=True", StringComparison.Ordinal));
        }

        [Fact]
        public void Decide_AnyInputMissing_ReplaysEverything()
        {
            AddRowAt(800.0);
            Assert.False(LedgerOrchestrator.DecideOnKspLoadCurrentUtCutoff(500.5, false, true));
            Assert.False(LedgerOrchestrator.DecideOnKspLoadCurrentUtCutoff(0.0, true, false));

            Ledger.ResetForTesting();
            AddRowAt(100.0);
            Assert.False(LedgerOrchestrator.DecideOnKspLoadCurrentUtCutoff(500.5, true, true));
            Assert.Contains(logLines, l => l.EndsWith(
                "useCurrentUtCutoff=False maxUT=500.5 featureSupportsCutoff=True currentUtReady=True " +
                "hasFutureActions=False", StringComparison.Ordinal));
        }
    }
}
