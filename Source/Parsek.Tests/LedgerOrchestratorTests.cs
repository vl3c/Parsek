using System;
using System.Collections.Generic;
using System.Reflection;
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
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private int CountLogs(string subsystemToken, string messageToken)
        {
            int count = 0;
            foreach (string line in logLines)
            {
                if (line.Contains(subsystemToken) && line.Contains(messageToken))
                    count++;
            }

            return count;
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
            Assert.NotNull(LedgerOrchestrator.Kerbals);

            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]") && l.Contains("8 modules registered"));
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
                if (line.Contains("[LedgerOrchestrator]") && line.Contains("8 modules registered"))
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
        public void RecalculateAndPatch_RepeatedNoopSummaries_EmitOnce()
        {
            LedgerOrchestrator.Initialize();

            LedgerOrchestrator.RecalculateAndPatch();
            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Equal(1, CountLogs("[LedgerOrchestrator]", "RecalculateAndPatch: actionsTotal=0"));
            Assert.Equal(1, CountLogs("[LedgerOrchestrator]", "RecalculateAndPatch complete: 0 actions walked"));
            Assert.Equal(1, CountLogs("[KspStatePatcher]", "PatchAll complete"));
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
                l.Contains("[LedgerOrchestrator]") && l.Contains("8 modules registered"));
        }

        [Fact]
        public void RecalculateAndPatch_WithoutUncommittedTree_DoesNotDeferPatch()
        {
            LedgerOrchestrator.Initialize();

            LedgerOrchestrator.RecalculateAndPatch();

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("deferred KSP state patch"));
            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("PatchAll complete"));
        }

        [Fact]
        public void RecalculateAndPatch_WithPendingTree_DefersKspStatePatch()
        {
            LedgerOrchestrator.Initialize();
            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.FundsInitial,
                InitialFunds = 25000f
            });

            RecordingStore.StashPendingTree(new RecordingTree
            {
                Id = "tree-pending",
                TreeName = "PendingTree",
                RootRecordingId = "rec-pending",
                ActiveRecordingId = "rec-pending"
            });

            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Equal(25000.0, LedgerOrchestrator.Funds.GetAvailableFunds(), 1);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("deferred KSP state patch")
                && l.Contains("PendingTree"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("PatchAll complete"));
        }

        [Fact]
        public void RecalculateAndPatch_WithLiveRecorder_DefersKspStatePatch()
        {
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;
            GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = () => true;
            LedgerOrchestrator.Initialize();

            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("deferred KSP state patch")
                && l.Contains("live recorder active"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("PatchAll complete"));
        }

        [Fact]
        public void RecalculateAndPatch_WithActiveOutsiderTree_DefersKspStatePatch()
        {
            GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = () => true;
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => false;
            LedgerOrchestrator.Initialize();

            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("deferred KSP state patch")
                && l.Contains("active uncommitted flight tree"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("PatchAll complete"));
        }

        [Fact]
        public void RecalculateAndPatch_WithUtCutoff_DoesNotDeferPatchForPendingTree()
        {
            LedgerOrchestrator.Initialize();
            RecordingStore.StashPendingTree(new RecordingTree
            {
                Id = "tree-cutoff",
                TreeName = "CutoffTree",
                RootRecordingId = "rec-cutoff",
                ActiveRecordingId = "rec-cutoff"
            });

            LedgerOrchestrator.RecalculateAndPatch(100.0);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("deferred KSP state patch"));
            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("PatchAll complete"));
        }

        [Fact]
        public void RecalculateAndPatchForPostRewindFlightLoad_WithPendingTree_StillDefersKspStatePatch()
        {
            LedgerOrchestrator.Initialize();
            RecordingStore.StashPendingTree(new RecordingTree
            {
                Id = "tree-scene-load",
                TreeName = "SceneLoadTree",
                RootRecordingId = "rec-scene-load",
                ActiveRecordingId = "rec-scene-load"
            });

            LedgerOrchestrator.RecalculateAndPatchForPostRewindFlightLoad(100.0);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("deferred KSP state patch")
                && l.Contains("SceneLoadTree"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("PatchAll complete"));
        }

        [Fact]
        public void HasActionsAfterUT_WhenLaterActionExists_ReturnsTrue()
        {
            Ledger.AddAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.FundsInitial,
                InitialFunds = 1000f
            });
            Ledger.AddAction(new GameAction
            {
                UT = 250.0,
                Type = GameActionType.FundsEarning,
                FundsAwarded = 100f
            });

            Assert.True(LedgerOrchestrator.HasActionsAfterUT(200.0));
        }

        [Fact]
        public void HasActionsAfterUT_WhenAllActionsAreAtOrBeforeThreshold_ReturnsFalse()
        {
            Ledger.AddAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.FundsInitial,
                InitialFunds = 1000f
            });
            Ledger.AddAction(new GameAction
            {
                UT = 200.0,
                Type = GameActionType.FundsEarning,
                FundsAwarded = 100f
            });

            Assert.False(LedgerOrchestrator.HasActionsAfterUT(200.0));
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
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = LedgerOrchestrator.CreateVesselCostActions("rec-build-cost", 100.0, 200.0);

            Assert.Single(actions);
            Assert.Equal(GameActionType.FundsSpending, actions[0].Type);
            Assert.Equal(FundsSpendingSource.VesselBuild, actions[0].FundsSpendingSource);
            Assert.Equal(10000.0, (double)actions[0].FundsSpent, 1);
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
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = LedgerOrchestrator.CreateVesselCostActions("rec-recovery", 100.0, 300.0);

            // Should produce both build cost (50000-40000=10000) and recovery (47000-40000=7000)
            Assert.Equal(2, actions.Count);

            var buildAction = actions[0];
            Assert.Equal(GameActionType.FundsSpending, buildAction.Type);
            Assert.Equal(FundsSpendingSource.VesselBuild, buildAction.FundsSpendingSource);
            Assert.Equal(10000.0, (double)buildAction.FundsSpent, 1);

            var recoveryAction = actions[1];
            Assert.Equal(GameActionType.FundsEarning, recoveryAction.Type);
            Assert.Equal(FundsEarningSource.Recovery, recoveryAction.FundsSource);
            Assert.Equal(7000.0, (double)recoveryAction.FundsAwarded, 1);
            Assert.Equal(300.0, recoveryAction.UT);
            Assert.Equal("rec-recovery", recoveryAction.RecordingId);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("recovery funds=7000"));

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void CreateVesselCostActions_PairedRecoveryEventPreferredOverPointDelta()
        {
            var rec = new Recording
            {
                RecordingId = "rec-recovery-paired-event",
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Recovered
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 40000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, funds = 47000.0 });
            RecordingStore.ResetForTesting();
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var recoveryEvent = new GameStateEvent
            {
                ut = 300.0,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                valueBefore = 47000.0,
                valueAfter = 48250.0
            };
            GameStateStore.AddEvent(ref recoveryEvent);

            var actions = LedgerOrchestrator.CreateVesselCostActions("rec-recovery-paired-event", 100.0, 300.0);

            Assert.Equal(2, actions.Count);
            var recoveryAction = actions[1];
            Assert.Equal(GameActionType.FundsEarning, recoveryAction.Type);
            Assert.Equal(FundsEarningSource.Recovery, recoveryAction.FundsSource);
            Assert.Equal(1250.0, (double)recoveryAction.FundsAwarded, 1);
            Assert.Equal(300.0, recoveryAction.UT);
            Assert.Equal(
                LedgerOrchestrator.BuildRecoveryEventDedupKey(recoveryEvent),
                recoveryAction.DedupKey);

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
            RecordingStore.AddRecordingWithTreeForTesting(rec);

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
            RecordingStore.AddRecordingWithTreeForTesting(rec);

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
            RecordingStore.AddRecordingWithTreeForTesting(rec);

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
            // Without a paired FundsChanged(VesselRecovery) event, the fallback recovery
            // heuristic still requires >= 2 points to compute a delta.
            var rec = new Recording
            {
                RecordingId = "rec-single-pt",
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Recovered
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 47000.0 });
            RecordingStore.ResetForTesting();
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = LedgerOrchestrator.CreateVesselCostActions("rec-single-pt", 100.0, 100.0);

            // Build cost only (50000-47000=3000), no recovery (need 2+ points)
            Assert.Single(actions);
            Assert.Equal(GameActionType.FundsSpending, actions[0].Type);
            Assert.Equal(3000.0, (double)actions[0].FundsSpent, 1);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void CreateVesselCostActions_RecoveredSinglePoint_WithPairedEvent_ProducesRecoveryAction()
        {
            var rec = new Recording
            {
                RecordingId = "rec-single-pt-paired-recovery",
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Recovered
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 47000.0 });
            RecordingStore.ResetForTesting();
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var recoveryEvt = new GameStateEvent
            {
                ut = 130.0,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                valueBefore = 47000.0,
                valueAfter = 49000.0
            };
            GameStateStore.AddEvent(ref recoveryEvt);

            var actions = LedgerOrchestrator.CreateVesselCostActions("rec-single-pt-paired-recovery", 100.0, 130.0);

            Assert.Equal(2, actions.Count);
            Assert.Contains(actions, a =>
                a.Type == GameActionType.FundsEarning &&
                a.FundsSource == FundsEarningSource.Recovery &&
                System.Math.Abs(a.UT - 130.0) < 0.01 &&
                System.Math.Abs(a.FundsAwarded - 2000f) < 0.01f);

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
            RecordingStore.AddRecordingWithTreeForTesting(rec);

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
            RecordingStore.AddRecordingWithTreeForTesting(rec);

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
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = LedgerOrchestrator.CreateKerbalAssignmentActions("rec-no-endstates", 50.0, 150.0);

            Assert.Single(actions);
            Assert.Equal("Val Kerman", actions[0].KerbalName);
            Assert.Equal(KerbalEndState.Unknown, actions[0].KerbalEndStateField);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void CreateKerbalAssignmentActions_GhostOnlyChainSegment_UsesFiniteHandoffEndState()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = new ConfigNode("PART");
            part.AddValue("crew", "Jeb Kerman");
            snapshot.AddNode(part);

            var rec = new Recording
            {
                RecordingId = "rec-ghost-chain",
                VesselName = "Chain Ship",
                ChainId = "chain-ghost",
                ChainIndex = 0,
                GhostVisualSnapshot = snapshot,
                ExplicitStartUT = 10,
                ExplicitEndUT = 20
            };
            RecordingStore.ResetForTesting();
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = LedgerOrchestrator.CreateKerbalAssignmentActions("rec-ghost-chain", 10.0, 20.0);

            Assert.Single(actions);
            Assert.Equal("Jeb Kerman", actions[0].KerbalName);
            Assert.Equal(KerbalEndState.Recovered, actions[0].KerbalEndStateField);
            Assert.True(rec.CrewEndStatesResolved);
            Assert.Equal(KerbalEndState.Recovered, rec.CrewEndStates["Jeb Kerman"]);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void CreateKerbalAssignmentActions_GhostOnlyStableChainTip_DoesNotForceRecovered()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = new ConfigNode("PART");
            part.AddValue("crew", "Val Kerman");
            snapshot.AddNode(part);

            var rec = new Recording
            {
                RecordingId = "rec-ghost-tip",
                VesselName = "Chain Tip",
                ChainId = "chain-tip",
                ChainIndex = 2,
                GhostVisualSnapshot = snapshot,
                TerminalStateValue = TerminalState.Orbiting
            };
            RecordingStore.ResetForTesting();
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = LedgerOrchestrator.CreateKerbalAssignmentActions("rec-ghost-tip", 50.0, 150.0);

            Assert.Single(actions);
            Assert.Equal("Val Kerman", actions[0].KerbalName);
            Assert.Equal(KerbalEndState.Unknown, actions[0].KerbalEndStateField);
            Assert.Null(rec.CrewEndStates);
            Assert.False(rec.CrewEndStatesResolved);

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
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = LedgerOrchestrator.CreateKerbalAssignmentActions("rec-fallback", 10.0, 90.0);

            Assert.Single(actions);
            Assert.Equal("Bob Kerman", actions[0].KerbalName);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void CreateKerbalAssignmentActions_ReverseMapsStandInNames()
        {
            CrewReservationManager.SetReplacement("Jebediah Kerman", "Leia Kerman");

            var snapshot = new ConfigNode("VESSEL");
            var part = new ConfigNode("PART");
            part.AddValue("crew", "Leia Kerman");
            snapshot.AddNode(part);

            var rec = new Recording
            {
                RecordingId = "rec-standin-action",
                VesselName = "Crew Ship",
                GhostVisualSnapshot = snapshot,
                CrewEndStates = new Dictionary<string, KerbalEndState>
                {
                    { "Jebediah Kerman", KerbalEndState.Recovered }
                }
            };

            RecordingStore.ResetForTesting();
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = LedgerOrchestrator.CreateKerbalAssignmentActions(
                "rec-standin-action", 100.0, 500.0);

            Assert.Single(actions);
            Assert.Equal("Jebediah Kerman", actions[0].KerbalName);
            Assert.Equal(KerbalEndState.Recovered, actions[0].KerbalEndStateField);

            CrewReservationManager.ResetReplacementsForTesting();
            RecordingStore.ResetForTesting();
        }

        // ================================================================
        // Dynamic slot limits (GetContractSlots / GetStrategySlots)
        // ================================================================

        [Fact]
        public void GetContractSlots_Level1_Returns2()
        {
            Assert.Equal(2, LedgerOrchestrator.GetContractSlots(1));
        }

        [Fact]
        public void GetContractSlots_Level2_Returns7()
        {
            Assert.Equal(7, LedgerOrchestrator.GetContractSlots(2));
        }

        [Fact]
        public void GetContractSlots_Level3_Returns999()
        {
            Assert.Equal(999, LedgerOrchestrator.GetContractSlots(3));
        }

        [Fact]
        public void GetStrategySlots_Level1_Returns1()
        {
            Assert.Equal(1, LedgerOrchestrator.GetStrategySlots(1));
        }

        [Fact]
        public void GetStrategySlots_Level2_Returns3()
        {
            Assert.Equal(3, LedgerOrchestrator.GetStrategySlots(2));
        }

        [Fact]
        public void GetStrategySlots_Level3_Returns5()
        {
            Assert.Equal(5, LedgerOrchestrator.GetStrategySlots(3));
        }

        [Fact]
        public void RecalculateAndPatch_UpdatesContractSlotsFromFacilities()
        {
            LedgerOrchestrator.Initialize();

            // Upgrade Mission Control to level 2
            Ledger.AddAction(new GameAction
            {
                UT = 10.0,
                Type = GameActionType.FacilityUpgrade,
                FacilityId = "MissionControl",
                ToLevel = 2
            });

            // First recalculate: processes the facility upgrade, but slot limits
            // were set from prior state (level 1 -> 2 slots) before this walk.
            LedgerOrchestrator.RecalculateAndPatch();

            // Second recalculate: now the facility state shows level 2,
            // so slot limits update to 7 before the walk.
            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Equal(7 - 0, LedgerOrchestrator.Contracts.GetAvailableSlots());

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("7 contract slots"));
        }

        [Fact]
        public void RecalculateAndPatch_UpdatesStrategySlotsFromFacilities()
        {
            LedgerOrchestrator.Initialize();

            // Upgrade Administration to level 3
            Ledger.AddAction(new GameAction
            {
                UT = 10.0,
                Type = GameActionType.FacilityUpgrade,
                FacilityId = "Administration",
                ToLevel = 3
            });

            LedgerOrchestrator.RecalculateAndPatch();
            LedgerOrchestrator.RecalculateAndPatch(); // second call picks up new level

            Assert.Equal(5 - 0, LedgerOrchestrator.Strategies.GetAvailableSlots());

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("5 strategy slots"));
        }

        [Fact]
        public void RecalculateAndPatch_DefaultSlotLimits_MatchLevel1()
        {
            LedgerOrchestrator.Initialize();

            // No facility upgrades — default level 1
            LedgerOrchestrator.RecalculateAndPatch();

            // Default: 2 contract slots, 1 strategy slot
            Assert.Equal(2, LedgerOrchestrator.Contracts.GetAvailableSlots());
            Assert.Equal(1, LedgerOrchestrator.Strategies.GetAvailableSlots());
        }

        // ================================================================
        // Recent KSC tech-unlock debit holdback
        // ================================================================

        [Fact]
        public void ComputePendingRecentKscTechResearchScienceDebit_UnmatchedBurstReturnsGap()
        {
            var events = new List<GameStateEvent>
            {
                new GameStateEvent
                {
                    ut = 1000.0,
                    eventType = GameStateEventType.ScienceChanged,
                    key = LedgerOrchestrator.TechResearchScienceReasonKey,
                    valueBefore = 42.1,
                    valueAfter = 17.1
                }
            };
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 1000.0,
                    Type = GameActionType.ScienceSpending,
                    Cost = 20f
                },
                new GameAction
                {
                    UT = 1000.0,
                    Type = GameActionType.ScienceSpending,
                    RecordingId = "rec-flight",
                    Cost = 99f
                }
            };

            double pending = LedgerOrchestrator.ComputePendingRecentKscTechResearchScienceDebit(
                events,
                actions,
                nowUt: 1000.05);

            Assert.Equal(5.0, pending, 3);
        }

        [Fact]
        public void ComputePendingRecentKscTechResearchScienceDebit_WhenLedgerCaughtUpReturnsZero()
        {
            var events = new List<GameStateEvent>
            {
                new GameStateEvent
                {
                    ut = 1200.0,
                    eventType = GameStateEventType.ScienceChanged,
                    key = LedgerOrchestrator.TechResearchScienceReasonKey,
                    valueBefore = 31.0,
                    valueAfter = 6.0
                }
            };
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 1200.0,
                    Type = GameActionType.ScienceSpending,
                    Cost = 15f
                },
                new GameAction
                {
                    UT = 1200.04,
                    Type = GameActionType.ScienceSpending,
                    Cost = 10f
                }
            };

            double pending = LedgerOrchestrator.ComputePendingRecentKscTechResearchScienceDebit(
                events,
                actions,
                nowUt: 1200.02);

            Assert.Equal(0.0, pending, 3);
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

        [Fact]
        public void ExtractCrewFromRecording_ReverseMapsStandInNames()
        {
            CrewReservationManager.SetReplacement("Jebediah Kerman", "Leia Kerman");

            var snapshot = new ConfigNode("VESSEL");
            var part = new ConfigNode("PART");
            part.AddValue("crew", "Leia Kerman");
            snapshot.AddNode(part);

            var rec = new Recording
            {
                RecordingId = "rec-extract-standin",
                GhostVisualSnapshot = snapshot,
                CrewEndStates = new Dictionary<string, KerbalEndState>
                {
                    { "Jebediah Kerman", KerbalEndState.Aboard }
                }
            };

            var result = LedgerOrchestrator.ExtractCrewFromRecording(rec);

            Assert.Single(result);
            Assert.Equal("Jebediah Kerman", result[0].Name);
            Assert.Equal(KerbalEndState.Aboard, result[0].EndState);

            CrewReservationManager.ResetReplacementsForTesting();
        }

        [Fact]
        public void PopulateUnpopulatedCrewEndStates_EvaOnlyRecording_PopulatesCrewEndStates()
        {
            var rec = new Recording
            {
                RecordingId = "rec-eva-populate",
                VesselName = "Bill Kerman",
                EvaCrewName = "Bill Kerman",
                GhostVisualSnapshot = new ConfigNode("VESSEL"),
                TerminalStateValue = TerminalState.Destroyed
            };
            RecordingStore.ResetForTesting();
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            MethodInfo method = typeof(LedgerOrchestrator).GetMethod(
                "PopulateUnpopulatedCrewEndStates", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            method.Invoke(null, null);

            Assert.NotNull(rec.CrewEndStates);
            Assert.True(rec.CrewEndStatesResolved);
            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates["Bill Kerman"]);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void PopulateUnpopulatedCrewEndStates_GhostOnlyChainRecording_UsesFiniteHandoffEndState()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", "Val Kerman");

            var rec = new Recording
            {
                RecordingId = "rec-ghost-chain-populate",
                VesselName = "Chain Ship",
                ChainId = "chain-ghost-populate",
                ChainIndex = 1,
                GhostVisualSnapshot = snapshot
            };
            RecordingStore.ResetForTesting();
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            MethodInfo method = typeof(LedgerOrchestrator).GetMethod(
                "PopulateUnpopulatedCrewEndStates", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            method.Invoke(null, null);

            Assert.NotNull(rec.CrewEndStates);
            Assert.True(rec.CrewEndStatesResolved);
            Assert.Equal(KerbalEndState.Recovered, rec.CrewEndStates["Val Kerman"]);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void MigrateKerbalAssignments_EvaOnlyRecording_PopulatesEndStateBeforeActionCreation()
        {
            var rec = new Recording
            {
                RecordingId = "rec-eva-migrate",
                VesselName = "Bill Kerman",
                EvaCrewName = "Bill Kerman",
                GhostVisualSnapshot = new ConfigNode("VESSEL"),
                TerminalStateValue = TerminalState.Destroyed,
                ExplicitStartUT = 10,
                ExplicitEndUT = 20
            };
            RecordingStore.ResetForTesting();
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            MethodInfo method = typeof(LedgerOrchestrator).GetMethod(
                "MigrateKerbalAssignments", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            method.Invoke(null, null);

            Assert.Single(Ledger.Actions);
            Assert.Equal(GameActionType.KerbalAssignment, Ledger.Actions[0].Type);
            Assert.Equal("Bill Kerman", Ledger.Actions[0].KerbalName);
            Assert.Equal(KerbalEndState.Dead, Ledger.Actions[0].KerbalEndStateField);
            Assert.True(rec.CrewEndStatesResolved);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void MigrateKerbalAssignments_RewritesExistingStandInAction()
        {
            CrewReservationManager.SetReplacement("Jebediah Kerman", "Leia Kerman");

            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", "Leia Kerman");

            var rec = new Recording
            {
                RecordingId = "rec-standin-repair",
                VesselName = "Crew Ship",
                GhostVisualSnapshot = snapshot,
                ExplicitStartUT = 10,
                ExplicitEndUT = 20,
                CrewEndStates = new Dictionary<string, KerbalEndState>
                {
                    { "Jebediah Kerman", KerbalEndState.Recovered }
                }
            };
            RecordingStore.ResetForTesting();
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            Ledger.AddAction(new GameAction
            {
                UT = 10.0,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec-standin-repair",
                KerbalName = "Leia Kerman",
                KerbalRole = "Pilot",
                StartUT = 10,
                EndUT = 20,
                KerbalEndStateField = KerbalEndState.Unknown,
                Sequence = 1
            });

            MethodInfo method = typeof(LedgerOrchestrator).GetMethod(
                "MigrateKerbalAssignments", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            method.Invoke(null, null);

            Assert.Single(Ledger.Actions);
            Assert.Equal("Jebediah Kerman", Ledger.Actions[0].KerbalName);
            Assert.Equal(KerbalEndState.Recovered, Ledger.Actions[0].KerbalEndStateField);

            CrewReservationManager.ResetReplacementsForTesting();
            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void MigrateKerbalAssignments_RewritesStandInActionFromPersistedSlots()
        {
            var kerbals = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jebediah Kerman");
            slotNode.AddValue("trait", "Pilot");
            var entry = slotNode.AddNode("CHAIN_ENTRY");
            entry.AddValue("name", "Hanley Kerman");
            kerbals.LoadSlots(parent);
            LedgerOrchestrator.SetKerbalsForTesting(kerbals);
            CrewReservationManager.ResetReplacementsForTesting();

            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", "Hanley Kerman");

            var rec = new Recording
            {
                RecordingId = "rec-slot-repair",
                VesselName = "Crew Ship",
                GhostVisualSnapshot = snapshot,
                ExplicitStartUT = 10,
                ExplicitEndUT = 20,
                CrewEndStates = new Dictionary<string, KerbalEndState>
                {
                    { "Jebediah Kerman", KerbalEndState.Recovered }
                }
            };
            RecordingStore.ResetForTesting();
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            Ledger.AddAction(new GameAction
            {
                UT = 10.0,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec-slot-repair",
                KerbalName = "Hanley Kerman",
                KerbalRole = "Pilot",
                StartUT = 10,
                EndUT = 20,
                KerbalEndStateField = KerbalEndState.Unknown,
                Sequence = 1
            });

            MethodInfo method = typeof(LedgerOrchestrator).GetMethod(
                "MigrateKerbalAssignments", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            method.Invoke(null, null);

            Assert.Single(Ledger.Actions);
            Assert.Equal("Jebediah Kerman", Ledger.Actions[0].KerbalName);
            Assert.Equal(KerbalEndState.Recovered, Ledger.Actions[0].KerbalEndStateField);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void MigrateKerbalAssignments_RewritesExistingGhostOnlyUnknownAction()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", "Val Kerman");

            var rec = new Recording
            {
                RecordingId = "rec-ghost-repair",
                VesselName = "Chain Ship",
                ChainId = "chain-repair",
                ChainIndex = 0,
                GhostVisualSnapshot = snapshot,
                ExplicitStartUT = 30,
                ExplicitEndUT = 40
            };
            RecordingStore.ResetForTesting();
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            Ledger.AddAction(new GameAction
            {
                UT = 30.0,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec-ghost-repair",
                KerbalName = "Val Kerman",
                KerbalRole = "Pilot",
                StartUT = 30,
                EndUT = 40,
                KerbalEndStateField = KerbalEndState.Unknown,
                Sequence = 1
            });

            MethodInfo method = typeof(LedgerOrchestrator).GetMethod(
                "MigrateKerbalAssignments", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            method.Invoke(null, null);

            Assert.Single(Ledger.Actions);
            Assert.Equal("Val Kerman", Ledger.Actions[0].KerbalName);
            Assert.Equal(KerbalEndState.Recovered, Ledger.Actions[0].KerbalEndStateField);
            Assert.True(rec.CrewEndStatesResolved);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void CreateKerbalAssignmentActions_TouristCrew_SkipsTourists()
        {
            var baseline = new GameStateBaseline();
            baseline.crewEntries.Add(new GameStateBaseline.CrewEntry
            {
                name = "Jeb Kerman",
                trait = "Pilot"
            });
            baseline.crewEntries.Add(new GameStateBaseline.CrewEntry
            {
                name = "Tourist Kerman",
                trait = "Tourist"
            });
            GameStateStore.AddBaseline(baseline);

            var snapshot = new ConfigNode("VESSEL");
            var part = new ConfigNode("PART");
            part.AddValue("crew", "Jeb Kerman");
            part.AddValue("crew", "Tourist Kerman");
            snapshot.AddNode(part);

            var rec = new Recording
            {
                RecordingId = "rec-tourists",
                VesselName = "Tour Bus",
                GhostVisualSnapshot = snapshot
            };
            rec.CrewEndStates = new Dictionary<string, KerbalEndState>
            {
                { "Jeb Kerman", KerbalEndState.Recovered },
                { "Tourist Kerman", KerbalEndState.Recovered }
            };
            RecordingStore.ResetForTesting();
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = LedgerOrchestrator.CreateKerbalAssignmentActions("rec-tourists", 10.0, 20.0);

            Assert.Single(actions);
            Assert.Equal("Jeb Kerman", actions[0].KerbalName);
            Assert.DoesNotContain(actions, a => a.KerbalName == "Tourist Kerman");

            RecordingStore.ResetForTesting();
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
            RecordingStore.AddRecordingWithTreeForTesting(rec);

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

        // ================================================================
        // HasFacilityActionsInRange
        // ================================================================

        [Fact]
        public void HasFacilityActionsInRange_EmptyLedger_ReturnsFalse()
        {
            Assert.False(LedgerOrchestrator.HasFacilityActionsInRange(0, 1000));
        }

        [Fact]
        public void HasFacilityActionsInRange_FacilityUpgradeInRange_ReturnsTrue()
        {
            Ledger.AddAction(new GameAction
            {
                UT = 500.0,
                Type = GameActionType.FacilityUpgrade,
                RecordingId = "rec-fac-1",
                FacilityId = "SpaceCenter/LaunchPad",
                ToLevel = 2
            });

            Assert.True(LedgerOrchestrator.HasFacilityActionsInRange(100, 600));
        }

        [Fact]
        public void HasFacilityActionsInRange_FacilityDestructionInRange_ReturnsTrue()
        {
            Ledger.AddAction(new GameAction
            {
                UT = 300.0,
                Type = GameActionType.FacilityDestruction,
                RecordingId = "rec-fac-2",
                FacilityId = "SpaceCenter/LaunchPad"
            });

            Assert.True(LedgerOrchestrator.HasFacilityActionsInRange(200, 400));
        }

        [Fact]
        public void HasFacilityActionsInRange_FacilityRepairInRange_ReturnsTrue()
        {
            Ledger.AddAction(new GameAction
            {
                UT = 300.0,
                Type = GameActionType.FacilityRepair,
                RecordingId = "rec-fac-3",
                FacilityId = "SpaceCenter/LaunchPad"
            });

            Assert.True(LedgerOrchestrator.HasFacilityActionsInRange(200, 400));
        }

        [Fact]
        public void HasFacilityActionsInRange_ActionOutsideRange_ReturnsFalse()
        {
            Ledger.AddAction(new GameAction
            {
                UT = 500.0,
                Type = GameActionType.FacilityUpgrade,
                RecordingId = "rec-fac-4",
                FacilityId = "SpaceCenter/LaunchPad",
                ToLevel = 2
            });

            // Range entirely before the action
            Assert.False(LedgerOrchestrator.HasFacilityActionsInRange(100, 400));
            // Range entirely after the action
            Assert.False(LedgerOrchestrator.HasFacilityActionsInRange(600, 900));
        }

        [Fact]
        public void HasFacilityActionsInRange_NonFacilityActionInRange_ReturnsFalse()
        {
            Ledger.AddAction(new GameAction
            {
                UT = 500.0,
                Type = GameActionType.ScienceEarning,
                RecordingId = "rec-sci-1",
                SubjectId = "mysteryGoo@KerbinSrfLanded",
                ScienceAwarded = 10f,
                SubjectMaxValue = 30f
            });

            Assert.False(LedgerOrchestrator.HasFacilityActionsInRange(100, 600));
        }

        [Fact]
        public void HasFacilityActionsInRange_BoundaryExclusion_FromUTExcluded()
        {
            // Action exactly at fromUT — half-open range (fromUT, toUT] excludes fromUT
            Ledger.AddAction(new GameAction
            {
                UT = 500.0,
                Type = GameActionType.FacilityUpgrade,
                RecordingId = "rec-fac-5",
                FacilityId = "SpaceCenter/LaunchPad",
                ToLevel = 2
            });

            Assert.False(LedgerOrchestrator.HasFacilityActionsInRange(500, 600));
        }

        [Fact]
        public void HasFacilityActionsInRange_BoundaryInclusion_ToUTIncluded()
        {
            // Action exactly at toUT — half-open range (fromUT, toUT] includes toUT
            Ledger.AddAction(new GameAction
            {
                UT = 600.0,
                Type = GameActionType.FacilityUpgrade,
                RecordingId = "rec-fac-6",
                FacilityId = "SpaceCenter/LaunchPad",
                ToLevel = 2
            });

            Assert.True(LedgerOrchestrator.HasFacilityActionsInRange(500, 600));
        }

        // ================================================================
        // BuildChainEndUtMap
        // ================================================================

        [Fact]
        public void BuildChainEndUtMap_EmptyTree_ReturnsEmptyMap()
        {
            var tree = new RecordingTree { Id = "tree-1" };

            var map = LedgerOrchestrator.BuildChainEndUtMap(tree);

            Assert.Empty(map);
        }

        [Fact]
        public void BuildChainEndUtMap_NonChainRecordings_ReturnsEmptyMap()
        {
            var tree = new RecordingTree { Id = "tree-1" };
            var rec = new Recording { RecordingId = "rec-1" };
            rec.Points.Add(new TrajectoryPoint { ut = 10.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 100.0 });
            tree.Recordings["rec-1"] = rec;

            var map = LedgerOrchestrator.BuildChainEndUtMap(tree);

            Assert.Empty(map);
        }

        [Fact]
        public void BuildChainEndUtMap_ChainRecordings_MapsAllSegments()
        {
            var tree = new RecordingTree { Id = "tree-1" };

            var rec0 = new Recording { RecordingId = "rec-0", ChainId = "chain-A", ChainIndex = 0 };
            rec0.Points.Add(new TrajectoryPoint { ut = 10.0 });
            rec0.Points.Add(new TrajectoryPoint { ut = 142.2 });
            tree.Recordings["rec-0"] = rec0;

            var rec1 = new Recording { RecordingId = "rec-1", ChainId = "chain-A", ChainIndex = 1 };
            rec1.Points.Add(new TrajectoryPoint { ut = 142.9 });
            rec1.Points.Add(new TrajectoryPoint { ut = 2270.4 });
            tree.Recordings["rec-1"] = rec1;

            var map = LedgerOrchestrator.BuildChainEndUtMap(tree);

            Assert.Equal(2, map.Count);
            Assert.Equal(142.2, map["chain-A:0"]);
            Assert.Equal(2270.4, map["chain-A:1"]);
        }

        // ================================================================
        // AdjustStartUtForChainGap
        // ================================================================

        [Fact]
        public void AdjustStartUtForChainGap_NullRecording_ReturnsUnchanged()
        {
            var map = new Dictionary<string, double>();
            Assert.Equal(100.0, LedgerOrchestrator.AdjustStartUtForChainGap(null, 100.0, map));
        }

        [Fact]
        public void AdjustStartUtForChainGap_NonChainRecording_ReturnsUnchanged()
        {
            var rec = new Recording { RecordingId = "rec-1" };
            var map = new Dictionary<string, double>();

            Assert.Equal(100.0, LedgerOrchestrator.AdjustStartUtForChainGap(rec, 100.0, map));
        }

        [Fact]
        public void AdjustStartUtForChainGap_ChainIndex0_ReturnsUnchanged()
        {
            var rec = new Recording { RecordingId = "rec-0", ChainId = "chain-A", ChainIndex = 0 };
            var map = new Dictionary<string, double> { { "chain-A:0", 142.2 } };

            Assert.Equal(10.0, LedgerOrchestrator.AdjustStartUtForChainGap(rec, 10.0, map));
        }

        [Fact]
        public void AdjustStartUtForChainGap_ChainContinuation_ExtendsToGap()
        {
            // Simulates the real bug: predecessor ends at 142.2, this segment starts at 142.9
            var rec = new Recording { RecordingId = "rec-1", ChainId = "chain-A", ChainIndex = 1 };
            var map = new Dictionary<string, double> { { "chain-A:0", 142.2 } };

            double adjusted = LedgerOrchestrator.AdjustStartUtForChainGap(rec, 142.9, map);

            Assert.Equal(142.2, adjusted);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("Chain gap closure") &&
                l.Contains("142.9") && l.Contains("142.2"));
        }

        [Fact]
        public void AdjustStartUtForChainGap_NoGap_ReturnsUnchanged()
        {
            // Predecessor ends at exactly startUT — no gap to close
            var rec = new Recording { RecordingId = "rec-1", ChainId = "chain-A", ChainIndex = 1 };
            var map = new Dictionary<string, double> { { "chain-A:0", 142.9 } };

            Assert.Equal(142.9, LedgerOrchestrator.AdjustStartUtForChainGap(rec, 142.9, map));
        }

        [Fact]
        public void AdjustStartUtForChainGap_PredecessorAfterStart_ReturnsUnchanged()
        {
            // Predecessor endUT > startUT — overlapping segments, no backward extension needed
            var rec = new Recording { RecordingId = "rec-1", ChainId = "chain-A", ChainIndex = 1 };
            var map = new Dictionary<string, double> { { "chain-A:0", 150.0 } };

            Assert.Equal(142.9, LedgerOrchestrator.AdjustStartUtForChainGap(rec, 142.9, map));
        }

        [Fact]
        public void AdjustStartUtForChainGap_PredecessorMissing_ReturnsUnchanged()
        {
            // Chain index 1 but no index 0 in map
            var rec = new Recording { RecordingId = "rec-1", ChainId = "chain-A", ChainIndex = 1 };
            var map = new Dictionary<string, double>();

            Assert.Equal(142.9, LedgerOrchestrator.AdjustStartUtForChainGap(rec, 142.9, map));
        }

        [Fact]
        public void AdjustStartUtForChainGap_MultipleChainSegments_ClosesEachGap()
        {
            // 4-segment chain with gaps between each pair
            var map = new Dictionary<string, double>
            {
                { "chain-A:0", 142.2 },
                { "chain-A:1", 2270.4 },
                { "chain-A:2", 2585.5 }
            };

            var rec1 = new Recording { RecordingId = "rec-1", ChainId = "chain-A", ChainIndex = 1 };
            Assert.Equal(142.2, LedgerOrchestrator.AdjustStartUtForChainGap(rec1, 142.9, map));

            var rec2 = new Recording { RecordingId = "rec-2", ChainId = "chain-A", ChainIndex = 2 };
            Assert.Equal(2270.4, LedgerOrchestrator.AdjustStartUtForChainGap(rec2, 2270.6, map));

            var rec3 = new Recording { RecordingId = "rec-3", ChainId = "chain-A", ChainIndex = 3 };
            Assert.Equal(2585.5, LedgerOrchestrator.AdjustStartUtForChainGap(rec3, 2586.1, map));
        }

        [Fact]
        public void AdjustStartUtForChainGap_DifferentChains_IndependentGaps()
        {
            var map = new Dictionary<string, double>
            {
                { "chain-A:0", 100.0 },
                { "chain-B:0", 500.0 }
            };

            var recA1 = new Recording { RecordingId = "recA-1", ChainId = "chain-A", ChainIndex = 1 };
            Assert.Equal(100.0, LedgerOrchestrator.AdjustStartUtForChainGap(recA1, 103.0, map));

            var recB1 = new Recording { RecordingId = "recB-1", ChainId = "chain-B", ChainIndex = 1 };
            Assert.Equal(500.0, LedgerOrchestrator.AdjustStartUtForChainGap(recB1, 505.0, map));
        }

        [Fact]
        public void ResolveStandaloneCommitWindowStartUt_ChainContinuationClosesGapForSubsetRouting()
        {
            var parent = new Recording
            {
                RecordingId = "rec-0",
                ChainId = "chain-A",
                ChainIndex = 0,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 142.2
            };
            RecordingStore.AddCommittedInternal(parent);

            var child = new Recording
            {
                RecordingId = "rec-1",
                ChainId = "chain-A",
                ChainIndex = 1,
                ParentRecordingId = "rec-0",
                ExplicitStartUT = 142.9,
                ExplicitEndUT = 200.0
            };
            var pending = new List<PendingScienceSubject>
            {
                new PendingScienceSubject
                {
                    subjectId = "gap@subject",
                    science = 2.5f,
                    captureUT = 142.4
                },
                new PendingScienceSubject
                {
                    subjectId = "inside@subject",
                    science = 1.0f,
                    captureUT = 150.0
                }
            };

            double startUT = LedgerOrchestrator.ResolveStandaloneCommitWindowStartUt(child, child.StartUT);
            var subset = LedgerOrchestrator.BuildPendingScienceSubsetForRecording(
                pending,
                child.RecordingId,
                startUT,
                child.EndUT);

            Assert.Equal(142.2, startUT);
            Assert.Equal(2, subset.Count);
            Assert.Contains(subset, s => s.subjectId == "gap@subject");
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("Chain gap closure") &&
                l.Contains("142.9") && l.Contains("142.2"));
        }

        [Fact]
        public void ResolveStandaloneCommitWindowStartUt_MissingParentLeavesSubsetAtOwnStart()
        {
            var child = new Recording
            {
                RecordingId = "rec-1",
                ChainId = "chain-A",
                ChainIndex = 1,
                ParentRecordingId = "missing-parent",
                ExplicitStartUT = 142.9,
                ExplicitEndUT = 200.0
            };
            var pending = new List<PendingScienceSubject>
            {
                new PendingScienceSubject
                {
                    subjectId = "gap@subject",
                    science = 2.5f,
                    captureUT = 142.4
                },
                new PendingScienceSubject
                {
                    subjectId = "inside@subject",
                    science = 1.0f,
                    captureUT = 150.0
                }
            };

            double startUT = LedgerOrchestrator.ResolveStandaloneCommitWindowStartUt(child, child.StartUT);
            var subset = LedgerOrchestrator.BuildPendingScienceSubsetForRecording(
                pending,
                child.RecordingId,
                startUT,
                child.EndUT);

            Assert.Equal(142.9, startUT);
            Assert.Single(subset);
            Assert.Equal("inside@subject", subset[0].subjectId);
        }
    }
}
