using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// #438 gap #7: bridge the per-component KSC reconciliation tests in
    /// <see cref="EarningsReconciliationTests"/> with the full
    /// <see cref="LedgerOrchestrator.OnKscSpending"/> flow. Feeds a paired
    /// <c>FundsChanged(CrewRecruited)</c> event into <see cref="GameStateStore"/>,
    /// calls <c>OnKscSpending</c> with the <c>CrewHired</c> event, and asserts that
    /// (a) a <see cref="GameActionType.KerbalHire"/> row with the correct cost lands
    /// in the ledger, and (b) the KSC reconciliation path stays silent — no
    /// <c>"KSC reconciliation"</c> WARN.
    ///
    /// This test owns the full reset pattern (ctor resets
    /// <see cref="GameStateStore"/>/<see cref="Ledger"/>/<see cref="LedgerOrchestrator"/>,
    /// Dispose symmetric) so it cannot leak state into the pure-function suite in
    /// <see cref="EarningsReconciliationTests"/>.
    /// </summary>
    [Collection("Sequential")]
    public class EarningsReconciliationEndToEndTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public EarningsReconciliationEndToEndTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            GameStateStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void OnKscSpending_CrewHired_WithPairedFundsChangedEvent_ReconcilesSilently()
        {
            // Step 1: seed the paired funds event at the same UT the CrewHired event
            // will carry. Key mirrors KSP's TransactionReasons.CrewRecruited token.
            var fundsEvt = new GameStateEvent
            {
                ut = 400.0,
                eventType = GameStateEventType.FundsChanged,
                key = "CrewRecruited",
                valueBefore = 120000.0,
                valueAfter = 57887.0   // delta -62113, matches HireCost below
            };
            GameStateStore.AddEvent(ref fundsEvt);

            // Step 2: emit the CrewHired event with the expected detail shape —
            // ConvertCrewHired parses `cost=<value>` into HireCost.
            var crewHired = new GameStateEvent
            {
                ut = 400.0,
                eventType = GameStateEventType.CrewHired,
                key = "Jebediah Kerman",
                detail = "trait=Pilot;cost=62113"
            };
            GameStateStore.AddEvent(ref crewHired);

            // Step 3: drive the real KSC spending hook.
            LedgerOrchestrator.OnKscSpending(crewHired);

            // Step 4: the ledger must now carry a KerbalHire row with the right cost.
            var hire = Ledger.Actions.SingleOrDefault(a =>
                a.Type == GameActionType.KerbalHire);
            Assert.NotNull(hire);
            Assert.Equal(62113f, hire.HireCost);
            Assert.Equal("Jebediah Kerman", hire.KerbalName);
            Assert.Equal(400.0, hire.UT);

            // Step 5: the KSC reconciliation path must NOT have emitted a WARN.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("KSC reconciliation"));
        }
    }
}
