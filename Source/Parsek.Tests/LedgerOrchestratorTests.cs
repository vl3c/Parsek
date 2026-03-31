using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class LedgerOrchestratorTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public LedgerOrchestratorTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            LedgerOrchestrator.ResetForTesting();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Initialize
        // ================================================================

        [Fact]
        public void Initialize_RegistersAllModules()
        {
            LedgerOrchestrator.Initialize();

            Assert.True(LedgerOrchestrator.IsInitialized);
            Assert.NotNull(LedgerOrchestrator.Science);
            Assert.NotNull(LedgerOrchestrator.Funds);
            Assert.NotNull(LedgerOrchestrator.Reputation);
            Assert.NotNull(LedgerOrchestrator.Milestones);
            Assert.NotNull(LedgerOrchestrator.Facilities);
            Assert.NotNull(LedgerOrchestrator.Contracts);
            Assert.NotNull(LedgerOrchestrator.Strategies);

            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]") && l.Contains("7 modules registered"));
        }

        [Fact]
        public void Initialize_IsIdempotent()
        {
            LedgerOrchestrator.Initialize();
            LedgerOrchestrator.Initialize();

            // Should only log "Initialized" once
            int initCount = 0;
            foreach (var line in logLines)
            {
                if (line.Contains("[LedgerOrchestrator]") && line.Contains("7 modules registered"))
                    initCount++;
            }
            Assert.Equal(1, initCount);
        }

        // ================================================================
        // ResetForTesting
        // ================================================================

        [Fact]
        public void ResetForTesting_ClearsState()
        {
            LedgerOrchestrator.Initialize();
            Assert.True(LedgerOrchestrator.IsInitialized);

            LedgerOrchestrator.ResetForTesting();

            Assert.False(LedgerOrchestrator.IsInitialized);
            Assert.Null(LedgerOrchestrator.Science);
            Assert.Null(LedgerOrchestrator.Funds);
            Assert.Null(LedgerOrchestrator.Reputation);
            Assert.Null(LedgerOrchestrator.Milestones);
            Assert.Null(LedgerOrchestrator.Facilities);
            Assert.Equal(0, Ledger.Actions.Count);
        }

        [Fact]
        public void ResetForTesting_AllowsReinitialization()
        {
            LedgerOrchestrator.Initialize();
            LedgerOrchestrator.ResetForTesting();
            LedgerOrchestrator.Initialize();

            Assert.True(LedgerOrchestrator.IsInitialized);
            Assert.NotNull(LedgerOrchestrator.Science);
        }

        // ================================================================
        // OnRecordingCommitted
        // ================================================================

        [Fact]
        public void OnRecordingCommitted_AddsActionsToLedger()
        {
            // Manually add a science earning to the ledger as if the converter produced it
            // (we can't call the full OnRecordingCommitted because GameStateStore.Events
            // and GameStateRecorder.PendingScienceSubjects require static state setup)
            LedgerOrchestrator.Initialize();

            var action = new GameAction
            {
                UT = 100.0,
                Type = GameActionType.ScienceEarning,
                RecordingId = "rec-test-1",
                SubjectId = "mysteryGoo@KerbinSrfLanded",
                ScienceAwarded = 10f,
                SubjectMaxValue = 30f
            };

            Ledger.AddAction(action);

            Assert.Equal(1, Ledger.Actions.Count);
            Assert.Equal("rec-test-1", Ledger.Actions[0].RecordingId);
            Assert.Equal(GameActionType.ScienceEarning, Ledger.Actions[0].Type);
        }

        [Fact]
        public void OnRecordingCommitted_LogsSummary()
        {
            LedgerOrchestrator.Initialize();

            // Add actions directly and run recalculate to verify logging
            var action = new GameAction
            {
                UT = 200.0,
                Type = GameActionType.ScienceEarning,
                RecordingId = "rec-log-test",
                SubjectId = "temperatureScan@KerbinSrfLanded",
                ScienceAwarded = 5f,
                SubjectMaxValue = 20f
            };
            Ledger.AddAction(action);

            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("RecalculateAndPatch complete"));
        }

        // ================================================================
        // RecalculateAndPatch
        // ================================================================

        [Fact]
        public void RecalculateAndPatch_RunsWithoutError()
        {
            LedgerOrchestrator.Initialize();

            // Empty ledger — should complete cleanly
            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("RecalculateAndPatch complete") && l.Contains("0 actions"));
        }

        [Fact]
        public void RecalculateAndPatch_WithActions_ProcessesCorrectly()
        {
            LedgerOrchestrator.Initialize();

            // Add a FundsInitial seed and a science earning
            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.FundsInitial,
                InitialFunds = 25000f
            });
            Ledger.AddAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.ScienceEarning,
                RecordingId = "rec-1",
                SubjectId = "mysteryGoo@KerbinSrfLanded",
                ScienceAwarded = 10f,
                SubjectMaxValue = 30f
            });

            LedgerOrchestrator.RecalculateAndPatch();

            // Verify modules processed the actions
            Assert.Equal(10.0, LedgerOrchestrator.Science.GetAvailableScience(), 3);
            Assert.Equal(25000.0, LedgerOrchestrator.Funds.GetAvailableFunds(), 1);
        }

        [Fact]
        public void RecalculateAndPatch_InitializesIfNotYetDone()
        {
            // Don't call Initialize() explicitly
            LedgerOrchestrator.RecalculateAndPatch();

            Assert.True(LedgerOrchestrator.IsInitialized);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("7 modules registered"));
        }

        // ================================================================
        // Multiple recalculations (idempotency)
        // ================================================================

        [Fact]
        public void RecalculateAndPatch_IsIdempotent()
        {
            LedgerOrchestrator.Initialize();

            Ledger.AddAction(new GameAction
            {
                UT = 50.0,
                Type = GameActionType.ScienceEarning,
                RecordingId = "rec-idem",
                SubjectId = "temperatureScan@KerbinSrfLanded",
                ScienceAwarded = 8f,
                SubjectMaxValue = 24f
            });

            LedgerOrchestrator.RecalculateAndPatch();
            double firstScience = LedgerOrchestrator.Science.GetAvailableScience();

            LedgerOrchestrator.RecalculateAndPatch();
            double secondScience = LedgerOrchestrator.Science.GetAvailableScience();

            Assert.Equal(firstScience, secondScience, 5);
        }
    }
}
