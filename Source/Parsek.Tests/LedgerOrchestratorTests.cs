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

        // ================================================================
        // CreateVesselCostActions
        // ================================================================

        [Fact]
        public void CreateVesselCostActions_WithBuildCost_ProducesFundsSpendingAction()
        {
            var rec = new Recording
            {
                RecordingId = "rec-build-cost",
                PreLaunchFunds = 50000.0
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 40000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, funds = 39500.0 });
            RecordingStore.ResetForTesting();
            RecordingStore.AddCommittedForTesting(rec);

            var actions = LedgerOrchestrator.CreateVesselCostActions("rec-build-cost", 100.0, 200.0);

            Assert.Single(actions);
            Assert.Equal(GameActionType.FundsSpending, actions[0].Type);
            Assert.Equal(FundsSpendingSource.VesselBuild, actions[0].FundsSpendingSource);
            Assert.Equal(10000f, actions[0].FundsSpent, 1);
            Assert.Equal(100.0, actions[0].UT);
            Assert.Equal("rec-build-cost", actions[0].RecordingId);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("vessel build cost=10000"));

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void CreateVesselCostActions_WithRecovery_ProducesFundsEarningAction()
        {
            var rec = new Recording
            {
                RecordingId = "rec-recovery",
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Recovered
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 40000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, funds = 40000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 300.0, funds = 47000.0 });
            RecordingStore.ResetForTesting();
            RecordingStore.AddCommittedForTesting(rec);

            var actions = LedgerOrchestrator.CreateVesselCostActions("rec-recovery", 100.0, 300.0);

            // Should produce both build cost (50000-40000=10000) and recovery (47000-40000=7000)
            Assert.Equal(2, actions.Count);

            var buildAction = actions[0];
            Assert.Equal(GameActionType.FundsSpending, buildAction.Type);
            Assert.Equal(FundsSpendingSource.VesselBuild, buildAction.FundsSpendingSource);
            Assert.Equal(10000f, buildAction.FundsSpent, 1);

            var recoveryAction = actions[1];
            Assert.Equal(GameActionType.FundsEarning, recoveryAction.Type);
            Assert.Equal(FundsEarningSource.Recovery, recoveryAction.FundsSource);
            Assert.Equal(7000f, recoveryAction.FundsAwarded, 1);
            Assert.Equal(300.0, recoveryAction.UT);
            Assert.Equal("rec-recovery", recoveryAction.RecordingId);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("recovery funds=7000"));

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void CreateVesselCostActions_NoResourceDelta_ProducesNoActions()
        {
            var rec = new Recording
            {
                RecordingId = "rec-no-delta",
                PreLaunchFunds = 40000.0
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 40000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, funds = 40000.0 });
            RecordingStore.ResetForTesting();
            RecordingStore.AddCommittedForTesting(rec);

            var actions = LedgerOrchestrator.CreateVesselCostActions("rec-no-delta", 100.0, 200.0);

            Assert.Empty(actions);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void CreateVesselCostActions_RecordingNotFound_ReturnsEmpty()
        {
            RecordingStore.ResetForTesting();

            var actions = LedgerOrchestrator.CreateVesselCostActions("nonexistent-id", 100.0, 200.0);

            Assert.Empty(actions);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("not found in CommittedRecordings"));
        }

        [Fact]
        public void CreateVesselCostActions_EmptyPoints_ReturnsEmpty()
        {
            var rec = new Recording
            {
                RecordingId = "rec-empty-pts",
                PreLaunchFunds = 50000.0
            };
            RecordingStore.ResetForTesting();
            RecordingStore.AddCommittedForTesting(rec);

            var actions = LedgerOrchestrator.CreateVesselCostActions("rec-empty-pts", 100.0, 200.0);

            Assert.Empty(actions);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("has no points"));

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void CreateVesselCostActions_NotRecovered_NoRecoveryAction()
        {
            var rec = new Recording
            {
                RecordingId = "rec-orbiting",
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Orbiting
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 40000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, funds = 40000.0 });
            RecordingStore.ResetForTesting();
            RecordingStore.AddCommittedForTesting(rec);

            var actions = LedgerOrchestrator.CreateVesselCostActions("rec-orbiting", 100.0, 200.0);

            // Build cost only, no recovery
            Assert.Single(actions);
            Assert.Equal(GameActionType.FundsSpending, actions[0].Type);
            Assert.Equal(FundsSpendingSource.VesselBuild, actions[0].FundsSpendingSource);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void CreateVesselCostActions_RecoveredSinglePoint_NoRecoveryAction()
        {
            // Recovery requires >= 2 points to compute delta
            var rec = new Recording
            {
                RecordingId = "rec-single-pt",
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Recovered
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 47000.0 });
            RecordingStore.ResetForTesting();
            RecordingStore.AddCommittedForTesting(rec);

            var actions = LedgerOrchestrator.CreateVesselCostActions("rec-single-pt", 100.0, 100.0);

            // Build cost only (50000-47000=3000), no recovery (need 2+ points)
            Assert.Single(actions);
            Assert.Equal(GameActionType.FundsSpending, actions[0].Type);
            Assert.Equal(3000f, actions[0].FundsSpent, 1);

            RecordingStore.ResetForTesting();
        }

        // ================================================================
        // CreateKerbalAssignmentActions
        // ================================================================

        [Fact]
        public void CreateKerbalAssignmentActions_RecordingNotFound_ReturnsEmpty()
        {
            RecordingStore.ResetForTesting();

            var actions = LedgerOrchestrator.CreateKerbalAssignmentActions("nonexistent", 100.0, 200.0);

            Assert.Empty(actions);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void CreateKerbalAssignmentActions_NullId_ReturnsEmpty()
        {
            var actions = LedgerOrchestrator.CreateKerbalAssignmentActions(null, 100.0, 200.0);

            Assert.Empty(actions);
        }

        [Fact]
        public void CreateKerbalAssignmentActions_NoCrew_ReturnsEmpty()
        {
            var rec = new Recording
            {
                RecordingId = "rec-no-crew",
                VesselName = "NoCrew Ship"
            };
            // No snapshot -> no crew
            RecordingStore.ResetForTesting();
            RecordingStore.AddCommittedForTesting(rec);

            var actions = LedgerOrchestrator.CreateKerbalAssignmentActions("rec-no-crew", 100.0, 200.0);

            Assert.Empty(actions);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void CreateKerbalAssignmentActions_WithCrew_ProducesActions()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = new ConfigNode("PART");
            part.AddValue("crew", "Jeb Kerman");
            part.AddValue("crew", "Bill Kerman");
            snapshot.AddNode(part);

            var rec = new Recording
            {
                RecordingId = "rec-crew-test",
                VesselName = "Crew Ship",
                GhostVisualSnapshot = snapshot
            };
            rec.CrewEndStates = new Dictionary<string, KerbalEndState>
            {
                { "Jeb Kerman", KerbalEndState.Recovered },
                { "Bill Kerman", KerbalEndState.Aboard }
            };
            RecordingStore.ResetForTesting();
            RecordingStore.AddCommittedForTesting(rec);

            var actions = LedgerOrchestrator.CreateKerbalAssignmentActions("rec-crew-test", 100.0, 500.0);

            Assert.Equal(2, actions.Count);

            Assert.Equal(GameActionType.KerbalAssignment, actions[0].Type);
            Assert.Equal("Jeb Kerman", actions[0].KerbalName);
            Assert.Equal("rec-crew-test", actions[0].RecordingId);
            Assert.Equal(100.0, actions[0].UT);
            Assert.Equal(100f, actions[0].StartUT);
            Assert.Equal(500f, actions[0].EndUT);
            Assert.Equal(KerbalEndState.Recovered, actions[0].KerbalEndStateField);
            Assert.Equal(1, actions[0].Sequence);

            Assert.Equal("Bill Kerman", actions[1].KerbalName);
            Assert.Equal(KerbalEndState.Aboard, actions[1].KerbalEndStateField);
            Assert.Equal(2, actions[1].Sequence);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("2 crew members"));

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void CreateKerbalAssignmentActions_NoCrewEndStates_DefaultsToUnknown()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = new ConfigNode("PART");
            part.AddValue("crew", "Val Kerman");
            snapshot.AddNode(part);

            var rec = new Recording
            {
                RecordingId = "rec-no-endstates",
                VesselName = "Unknown State Ship",
                GhostVisualSnapshot = snapshot
                // CrewEndStates intentionally null
            };
            RecordingStore.ResetForTesting();
            RecordingStore.AddCommittedForTesting(rec);

            var actions = LedgerOrchestrator.CreateKerbalAssignmentActions("rec-no-endstates", 50.0, 150.0);

            Assert.Single(actions);
            Assert.Equal("Val Kerman", actions[0].KerbalName);
            Assert.Equal(KerbalEndState.Unknown, actions[0].KerbalEndStateField);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void CreateKerbalAssignmentActions_FallsBackToVesselSnapshot()
        {
            // No GhostVisualSnapshot, but VesselSnapshot has crew
            var snapshot = new ConfigNode("VESSEL");
            var part = new ConfigNode("PART");
            part.AddValue("crew", "Bob Kerman");
            snapshot.AddNode(part);

            var rec = new Recording
            {
                RecordingId = "rec-fallback",
                VesselName = "Fallback Ship",
                VesselSnapshot = snapshot
                // GhostVisualSnapshot intentionally null
            };
            RecordingStore.ResetForTesting();
            RecordingStore.AddCommittedForTesting(rec);

            var actions = LedgerOrchestrator.CreateKerbalAssignmentActions("rec-fallback", 10.0, 90.0);

            Assert.Single(actions);
            Assert.Equal("Bob Kerman", actions[0].KerbalName);

            RecordingStore.ResetForTesting();
        }

        // ================================================================
        // ExtractCrewFromRecording
        // ================================================================

        [Fact]
        public void ExtractCrewFromRecording_NullRecording_ReturnsEmpty()
        {
            var result = LedgerOrchestrator.ExtractCrewFromRecording(null);

            Assert.Empty(result);
        }

        [Fact]
        public void ExtractCrewFromRecording_NoSnapshot_ReturnsEmpty()
        {
            var rec = new Recording { RecordingId = "rec-empty" };

            var result = LedgerOrchestrator.ExtractCrewFromRecording(rec);

            Assert.Empty(result);
        }

        [Fact]
        public void ExtractCrewFromRecording_WithCrewEndStates_MapsCorrectly()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = new ConfigNode("PART");
            part.AddValue("crew", "Jeb Kerman");
            part.AddValue("crew", "Bill Kerman");
            snapshot.AddNode(part);

            var rec = new Recording
            {
                RecordingId = "rec-extract",
                GhostVisualSnapshot = snapshot
            };
            rec.CrewEndStates = new Dictionary<string, KerbalEndState>
            {
                { "Jeb Kerman", KerbalEndState.Dead },
                { "Bill Kerman", KerbalEndState.Recovered }
            };

            var result = LedgerOrchestrator.ExtractCrewFromRecording(rec);

            Assert.Equal(2, result.Count);
            Assert.Equal("Jeb Kerman", result[0].Name);
            Assert.Equal(KerbalEndState.Dead, result[0].EndState);
            Assert.Equal("Bill Kerman", result[1].Name);
            Assert.Equal(KerbalEndState.Recovered, result[1].EndState);
        }

        // ================================================================
        // FindRecordingById
        // ================================================================

        [Fact]
        public void FindRecordingById_NullId_ReturnsNull()
        {
            Assert.Null(LedgerOrchestrator.FindRecordingById(null));
            Assert.Null(LedgerOrchestrator.FindRecordingById(""));
        }

        [Fact]
        public void FindRecordingById_ExistingId_ReturnsRecording()
        {
            var rec = new Recording { RecordingId = "rec-find-test" };
            RecordingStore.ResetForTesting();
            RecordingStore.AddCommittedForTesting(rec);

            var found = LedgerOrchestrator.FindRecordingById("rec-find-test");

            Assert.NotNull(found);
            Assert.Equal("rec-find-test", found.RecordingId);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void FindRecordingById_NonexistentId_ReturnsNull()
        {
            RecordingStore.ResetForTesting();

            var found = LedgerOrchestrator.FindRecordingById("does-not-exist");

            Assert.Null(found);

            RecordingStore.ResetForTesting();
        }
    }
}
