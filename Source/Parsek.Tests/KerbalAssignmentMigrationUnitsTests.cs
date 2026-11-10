using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct cases for the two units split out of
    /// <c>LedgerOrchestrator.MigrateKerbalAssignments</c>:
    /// <see cref="LedgerOrchestrator.IndexKerbalAssignmentRowsByRecording"/> and
    /// <see cref="LedgerOrchestrator.RecordKerbalAssignmentRepairDiagnostics"/>.
    /// </summary>
    [Collection("Sequential")]
    public class KerbalAssignmentMigrationUnitsTests : IDisposable
    {
        public KerbalAssignmentMigrationUnitsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            Ledger.ResetForTesting();
            KerbalLoadRepairDiagnostics.Reset();
        }

        public void Dispose()
        {
            Ledger.ResetForTesting();
            KerbalLoadRepairDiagnostics.Reset();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static GameAction Assignment(string recordingId, string kerbal) =>
            new GameAction
            {
                Type = GameActionType.KerbalAssignment,
                RecordingId = recordingId,
                KerbalName = kerbal,
            };

        [Fact]
        public void Index_GroupsTaggedAssignmentRowsInLedgerOrder()
        {
            var a1 = Assignment("rec-A", "Jebediah Kerman");
            var b1 = Assignment("rec-B", "Bill Kerman");
            var a2 = Assignment("rec-A", "Bob Kerman");
            Ledger.AddAction(a1);
            Ledger.AddAction(b1);
            Ledger.AddAction(new GameAction { Type = GameActionType.FundsEarning, RecordingId = "rec-A" });
            Ledger.AddAction(Assignment(null, "Untagged Kerman"));
            Ledger.AddAction(Assignment("", "Empty Kerman"));
            Ledger.AddAction(a2);

            var index = LedgerOrchestrator.IndexKerbalAssignmentRowsByRecording();

            Assert.Equal(2, index.Count);
            Assert.Equal(new[] { a1, a2 }, index["rec-A"]);
            Assert.Equal(new[] { b1 }, index["rec-B"]);
        }

        [Fact]
        public void Index_EmptyLedger_IsEmpty()
        {
            Assert.Empty(LedgerOrchestrator.IndexKerbalAssignmentRowsByRecording());
        }

        [Fact]
        public void Diagnostics_RecordCountsTouristSkipsRemapsAndRewrites()
        {
            KerbalLoadRepairDiagnostics.Begin();
            var stats = new LedgerOrchestrator.KerbalAssignmentRepairStats { TouristRowsSkipped = 2 };
            stats.RemappedRows.Add(new LedgerOrchestrator.KerbalAssignmentRepairRename
            {
                FromName = "Leia Kerman",
                ToName = "Jebediah Kerman",
            });
            stats.EndStateRewrites.Add(new LedgerOrchestrator.KerbalAssignmentEndStateRewrite
            {
                KerbalName = "Jebediah Kerman",
                FromState = KerbalEndState.Unknown,
                ToState = KerbalEndState.Recovered,
            });
            stats.EndStateRewrites.Add(new LedgerOrchestrator.KerbalAssignmentEndStateRewrite
            {
                KerbalName = "Bill Kerman",
                FromState = KerbalEndState.Recovered,
                ToState = KerbalEndState.Dead,
            });
            var existing = new List<GameAction> { Assignment("rec-A", "Leia Kerman") };
            var desired = new List<GameAction>
            {
                Assignment("rec-A", "Jebediah Kerman"),
                Assignment("rec-A", "Bill Kerman"),
                Assignment("rec-A", "Bob Kerman"),
            };

            LedgerOrchestrator.RecordKerbalAssignmentRepairDiagnostics(
                new Recording { RecordingId = "rec-A" }, existing, desired, stats);

            var summary = KerbalLoadRepairDiagnostics.CurrentForTesting;
            Assert.Equal(1, summary.RepairedRecordings);
            Assert.Equal(1, summary.OldRows);
            Assert.Equal(3, summary.NewRows);
            Assert.Equal(2, summary.TouristRowsSkipped);
            Assert.Equal(1, summary.RemappedRows);
            Assert.Equal(2, summary.EndStateRewrites);
            string samples = summary.FormatSamplesLine();
            Assert.Contains("rec-A", samples);
            Assert.Contains("Leia Kerman->Jebediah Kerman", samples);
            Assert.Contains("Jebediah Kerman Unknown->Recovered", samples);
        }

        [Fact]
        public void Diagnostics_NullExisting_CountsZeroOldRows()
        {
            KerbalLoadRepairDiagnostics.Begin();

            LedgerOrchestrator.RecordKerbalAssignmentRepairDiagnostics(
                new Recording { RecordingId = "rec-A" }, null,
                new List<GameAction> { Assignment("rec-A", "Jebediah Kerman") },
                new LedgerOrchestrator.KerbalAssignmentRepairStats());

            var summary = KerbalLoadRepairDiagnostics.CurrentForTesting;
            Assert.Equal(1, summary.RepairedRecordings);
            Assert.Equal(0, summary.OldRows);
            Assert.Equal(1, summary.NewRows);
            Assert.Equal(0, summary.TouristRowsSkipped + summary.RemappedRows + summary.EndStateRewrites);
        }
    }
}
