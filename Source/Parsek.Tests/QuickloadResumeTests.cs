using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for the quickload-resume recording pipeline (Bug C fix).
    /// Covers:
    /// - <see cref="PendingTreeState"/> transitions
    /// - <see cref="RecordingStore.StashPendingTree(RecordingTree, PendingTreeState)"/>
    /// - <see cref="RecordingStore.PopPendingTree"/> (non-destructive pop)
    /// - <see cref="RecordingStore.MarkPendingTreeFinalized"/>
    /// - ScenarioWriter round-trip of an active tree flagged with isActive=True
    /// - <see cref="ParsekScenario.IsActiveTreeNode"/> dispatch
    ///
    /// End-to-end testing (OnSave → scene reload → OnLoad → resume coroutine)
    /// requires Unity runtime and is covered by in-game tests, not here.
    /// </summary>
    [Collection("Sequential")]
    public class QuickloadResumeTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public QuickloadResumeTests()
        {
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            RewindContext.ResetForTesting();
            ParsekLog.ResetTestOverrides();

            // Enable logging through the test sink (matches TreeLogVerificationTests pattern)
            RecordingStore.SuppressLogging = false;
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            // Suppress side effects that would crash outside Unity
            GameStateStore.SuppressLogging = true;
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ClearPendingQuickloadResumeContext();
            ParsekScenario.pendingActiveTreeResumeRewindSave = null;
            FlightRecorder.QuickloadResumeUTProviderForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
        }

        // ============================================================
        // PendingTreeState transitions
        // ============================================================

        [Fact]
        public void DefaultState_IsFinalized()
        {
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
        }

        [Fact]
        public void ShouldLoadGroupHierarchyFromSave_InitialLoad_ReturnsTrue()
        {
            Assert.True(ParsekScenario.ShouldLoadGroupHierarchyFromSave(
                initialLoadDone: false, isRewinding: false));
        }

        [Fact]
        public void ShouldLoadGroupHierarchyFromSave_InSessionLoad_ReturnsFalse()
        {
            Assert.False(ParsekScenario.ShouldLoadGroupHierarchyFromSave(
                initialLoadDone: true, isRewinding: false));
        }

        [Fact]
        public void ShouldLoadGroupHierarchyFromSave_Rewind_ReturnsFalse()
        {
            Assert.False(ParsekScenario.ShouldLoadGroupHierarchyFromSave(
                initialLoadDone: false, isRewinding: true));
        }

        [Fact]
        public void LoadCrewAndGroupState_InSessionLoad_PreservesRootLevelDebrisGroup_Unchanged()
        {
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "Rocket",
                RecordingGroups = new List<string> { "Rocket" }
            });
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "Rocket Debris",
                IsDebris = true,
                RecordingGroups = new List<string> { "Rocket / Debris" }
            });

            GroupHierarchyStore.SetGroupParent("Booster / Debris", "Booster");
            Assert.False(GroupHierarchyStore.TryGetGroupParent("Rocket / Debris", out _));

            var scenarioNode = new ConfigNode("SCENARIO");
            var hierarchyNode = scenarioNode.AddNode("GROUP_HIERARCHY");
            var staleEntry = hierarchyNode.AddNode("ENTRY");
            staleEntry.AddValue("child", "Stale / Debris");
            staleEntry.AddValue("parent", "Stale");

            logLines.Clear();
            InvokeLoadCrewAndGroupState(scenarioNode, initialLoadDoneValue: true);

            Assert.False(GroupHierarchyStore.TryGetGroupParent("Rocket / Debris", out _));
            Assert.True(GroupHierarchyStore.TryGetGroupParent("Booster / Debris", out string boosterParent));
            Assert.Equal("Booster", boosterParent);
            Assert.False(GroupHierarchyStore.TryGetGroupParent("Stale / Debris", out _));
            Assert.Contains(logLines, l => l.Contains("preserving in-memory group hierarchy"));
        }

        [Fact]
        public void LoadCrewAndGroupState_InitialLoad_ReplacesHierarchyFromSave()
        {
            GroupHierarchyStore.SetGroupParent("Old / Debris", "Old");

            var scenarioNode = new ConfigNode("SCENARIO");
            var hierarchyNode = scenarioNode.AddNode("GROUP_HIERARCHY");
            var entry = hierarchyNode.AddNode("ENTRY");
            entry.AddValue("child", "Fresh / Debris");
            entry.AddValue("parent", "Fresh");

            InvokeLoadCrewAndGroupState(scenarioNode, initialLoadDoneValue: false);

            Assert.False(GroupHierarchyStore.TryGetGroupParent("Old / Debris", out _));
            Assert.True(GroupHierarchyStore.TryGetGroupParent("Fresh / Debris", out string freshParent));
            Assert.Equal("Fresh", freshParent);
        }

        [Fact]
        public void PrepareForIsolatedBatchFlightBaselineRestore_ClearsSaveScopedInMemoryState()
        {
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                RecordingId = "rec",
                VesselName = "Rocket"
            });
            RecordingStore.PendingCleanupPids = new HashSet<uint> { 42u };
            RecordingStore.PendingCleanupNames = new HashSet<string> { "Ghost" };

            var pendingTree = new RecordingTree
            {
                Id = "pending-tree",
                TreeName = "Pending",
                RootRecordingId = "pending-root",
                ActiveRecordingId = "pending-root"
            };
            pendingTree.Recordings["pending-root"] = new Recording
            {
                RecordingId = "pending-root",
                VesselName = "Pending"
            };
            RecordingStore.StashPendingTree(pendingTree, PendingTreeState.Limbo);

            GroupHierarchyStore.SetGroupParent("Child", "Parent");
            GroupHierarchyStore.AddHiddenGroup("Hidden");

            var crewNode = new ConfigNode("SCENARIO");
            var replacementsNode = crewNode.AddNode("CREW_REPLACEMENTS");
            var replacementEntry = replacementsNode.AddNode("ENTRY");
            replacementEntry.AddValue("original", "Jeb");
            replacementEntry.AddValue("replacement", "Bill");
            CrewReservationManager.LoadCrewReplacements(crewNode);

            var e = new GameStateEvent
            {
                eventType = GameStateEventType.FacilityUpgraded,
                key = "KSC",
                ut = 1.0,
                epoch = 3
            };
            GameStateStore.AddEvent(ref e);

            GameStateRecorder.PendingScienceSubjects.Add(new PendingScienceSubject
            {
                subjectId = "subject",
                science = 5f,
                subjectMaxValue = 10f
            });
            RevertDetector.SetPendingForTesting(RevertKind.Launch);

            SetPrivateStaticField(typeof(ParsekScenario), "initialLoadDone", true);
            SetPrivateStaticField(typeof(ParsekScenario), "budgetDeductionApplied", true);
            SetPrivateStaticField(typeof(ParsekScenario), "vesselSwitchPending", true);
            SetPrivateStaticField(typeof(ParsekScenario), "vesselSwitchPendingFrame", 123);
            ParsekScenario.MergeDialogPending = true;
            ParsekScenario.pendingActiveTreeResumeRewindSave = "parsek_rw_restore";
            ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady =
                ParsekScenario.ActiveTreeRestoreMode.Quickload;

            bool unsubscribeCalled = false;
            bool stateStillPopulatedDuringUnsubscribe = false;

            ParsekScenario.PrepareForIsolatedBatchFlightBaselineRestore(() =>
            {
                unsubscribeCalled = true;
                stateStillPopulatedDuringUnsubscribe =
                    RecordingStore.CommittedRecordings.Count > 0
                    && RecordingStore.HasPendingTree
                    && GameStateStore.EventCount > 0
                    && GameStateRecorder.PendingScienceSubjects.Count > 0;
            });

            Assert.True(unsubscribeCalled);
            Assert.True(stateStillPopulatedDuringUnsubscribe);
            Assert.False((bool)GetPrivateStaticField(typeof(ParsekScenario), "initialLoadDone"));
            Assert.False((bool)GetPrivateStaticField(typeof(ParsekScenario), "budgetDeductionApplied"));
            Assert.False((bool)GetPrivateStaticField(typeof(ParsekScenario), "vesselSwitchPending"));
            Assert.Equal(-1, (int)GetPrivateStaticField(typeof(ParsekScenario), "vesselSwitchPendingFrame"));
            Assert.False(ParsekScenario.MergeDialogPending);
            Assert.Null(ParsekScenario.pendingActiveTreeResumeRewindSave);
            Assert.Equal(ParsekScenario.ActiveTreeRestoreMode.None,
                ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady);
            Assert.Empty(RecordingStore.CommittedRecordings);
            Assert.False(RecordingStore.HasPendingTree);
            Assert.Null(RecordingStore.PendingCleanupPids);
            Assert.Null(RecordingStore.PendingCleanupNames);
            Assert.Empty(GroupHierarchyStore.GroupParents);
            Assert.Empty(GroupHierarchyStore.HiddenGroups);
            Assert.Equal(0, GameStateStore.EventCount);
            Assert.Empty(GameStateRecorder.PendingScienceSubjects);
            Assert.Equal(RevertKind.None, RevertDetector.PendingKind);

            var savedCrewNode = new ConfigNode("SCENARIO");
            CrewReservationManager.SaveCrewReplacements(savedCrewNode);
            Assert.Null(savedCrewNode.GetNode("CREW_REPLACEMENTS"));
        }

        [Fact]
        public void LoadCrewAndGroupState_InitialLoad_InitializesKerbalsBeforeLoadingSlots()
        {
            LedgerOrchestrator.ResetForTesting();

            var scenarioNode = new ConfigNode("SCENARIO");
            var slotsNode = scenarioNode.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jebediah Kerman");
            slotNode.AddValue("trait", "Pilot");
            slotNode.AddValue("permanentlyGone", "False");
            var entry = slotNode.AddNode("CHAIN_ENTRY");
            entry.AddValue("name", "Hanley Kerman");

            InvokeLoadCrewAndGroupState(scenarioNode, initialLoadDoneValue: false);

            Assert.NotNull(LedgerOrchestrator.Kerbals);
            Assert.True(LedgerOrchestrator.Kerbals.Slots.ContainsKey("Jebediah Kerman"));
            Assert.Single(LedgerOrchestrator.Kerbals.Slots["Jebediah Kerman"].Chain);
            Assert.Equal("Hanley Kerman", LedgerOrchestrator.Kerbals.Slots["Jebediah Kerman"].Chain[0]);

            LedgerOrchestrator.ResetForTesting();
        }

        [Fact]
        public void StashPendingTree_DefaultsToFinalized()
        {
            var tree = MakeTree("tree_a", "Launch", 2);
            RecordingStore.StashPendingTree(tree);
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
            Assert.Same(tree, RecordingStore.PendingTree);
        }

        [Fact]
        public void StashPendingTree_WithLimboState_IsLimbo()
        {
            var tree = MakeTree("tree_a", "Launch", 2);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);
            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);
            Assert.Contains(logLines, l => l.Contains("state=Limbo"));
        }

        [Fact]
        public void StashPendingTree_Overwriting_LogsWarning()
        {
            var first = MakeTree("first", "First", 1);
            var second = MakeTree("second", "Second", 1);
            RecordingStore.StashPendingTree(first, PendingTreeState.Limbo);
            logLines.Clear();

            RecordingStore.StashPendingTree(second, PendingTreeState.Finalized);

            Assert.Same(second, RecordingStore.PendingTree);
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
            Assert.Contains(logLines,
                l => l.Contains("overwriting existing pending tree") && l.Contains("'First'"));
        }

        [Fact]
        public void MarkPendingTreeFinalized_FlipsLimboToFinalized()
        {
            var tree = MakeTree("tree_a", "Mun", 2);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);
            logLines.Clear();

            RecordingStore.MarkPendingTreeFinalized();

            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
            Assert.Contains(logLines, l => l.Contains("Limbo → Finalized"));
        }

        [Fact]
        public void MarkPendingTreeFinalized_OnAlreadyFinalized_NoOp()
        {
            var tree = MakeTree("tree_a", "Mun", 2);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);
            logLines.Clear();

            RecordingStore.MarkPendingTreeFinalized();

            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
            // No Limbo → Finalized log line since no transition happened
            Assert.DoesNotContain(logLines, l => l.Contains("Limbo → Finalized"));
        }

        [Fact]
        public void MarkPendingTreeFinalized_NoPendingTree_NoOp()
        {
            RecordingStore.MarkPendingTreeFinalized();
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
            Assert.Null(RecordingStore.PendingTree);
        }

        // ============================================================
        // PopPendingTree non-destructive behavior
        // ============================================================

        [Fact]
        public void PopPendingTree_ReturnsTreeAndClearsSlot()
        {
            var tree = MakeTree("tree_a", "Mun", 2);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);

            var popped = RecordingStore.PopPendingTree();

            Assert.Same(tree, popped);
            Assert.Null(RecordingStore.PendingTree);
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
        }

        [Fact]
        public void PopPendingTree_NoTree_ReturnsNull()
        {
            var popped = RecordingStore.PopPendingTree();
            Assert.Null(popped);
        }

        // ============================================================
        // Discard and Clear reset state
        // ============================================================

        [Fact]
        public void DiscardPendingTree_ResetsStateToFinalized()
        {
            var tree = MakeTree("tree_a", "Mun", 2);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);
            RecordingStore.DiscardPendingTree();
            Assert.Null(RecordingStore.PendingTree);
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
        }

        [Fact]
        public void CommitPendingTree_ResetsStateToFinalized()
        {
            var tree = MakeTree("tree_a", "Mun", 2);
            // CommitTree needs file writes which fail outside Unity, so this test
            // only verifies the guard path (no pending tree → no-op)
            RecordingStore.ResetForTesting();
            RecordingStore.CommitPendingTree();
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
        }

        [Fact]
        public void Clear_ResetsStateToFinalized()
        {
            var tree = MakeTree("tree_a", "Mun", 2);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);
            RecordingStore.Clear();
            Assert.Null(RecordingStore.PendingTree);
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
        }

        // ============================================================
        // IsActiveTreeNode dispatch
        // ============================================================

        [Fact]
        public void IsActiveTreeNode_NullNode_ReturnsFalse()
        {
            Assert.False(ParsekScenario.IsActiveTreeNode(null));
        }

        [Fact]
        public void IsActiveTreeNode_MissingFlag_ReturnsFalse()
        {
            var node = new ConfigNode("RECORDING_TREE");
            node.AddValue("id", "tree_x");
            Assert.False(ParsekScenario.IsActiveTreeNode(node));
        }

        [Fact]
        public void IsActiveTreeNode_FlagFalse_ReturnsFalse()
        {
            var node = new ConfigNode("RECORDING_TREE");
            node.AddValue("isActive", "False");
            Assert.False(ParsekScenario.IsActiveTreeNode(node));
        }

        [Fact]
        public void IsActiveTreeNode_FlagTrue_ReturnsTrue()
        {
            var node = new ConfigNode("RECORDING_TREE");
            node.AddValue("isActive", "True");
            Assert.True(ParsekScenario.IsActiveTreeNode(node));
        }

        [Fact]
        public void IsActiveTreeNode_FlagTrueMixedCase_ReturnsTrue()
        {
            var node = new ConfigNode("RECORDING_TREE");
            node.AddValue("isActive", "true");
            Assert.True(ParsekScenario.IsActiveTreeNode(node));
        }

        // ============================================================
        // TryRestoreActiveTreeNode end-to-end (parse → stash as Limbo)
        // ============================================================

        [Fact]
        public void TryRestoreActiveTreeNode_NoActiveTreeInSave_ReturnsFalseAndLeavesPendingEmpty()
        {
            var scenarioNode = new ConfigNode("PARSEK_SCENARIO");
            // Add a committed (non-active) tree to verify it's NOT stashed as Limbo
            var committedTreeNode = scenarioNode.AddNode("RECORDING_TREE");
            var committedTree = MakeTree("committed", "Launched", 2);
            committedTree.Save(committedTreeNode);

            bool result = ParsekScenario.TryRestoreActiveTreeNode(scenarioNode);

            Assert.False(result);
            Assert.Null(RecordingStore.PendingTree);
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
        }

        [Fact]
        public void TryRestoreActiveTreeNode_WithActiveTree_StashesAsLimbo()
        {
            var scenarioNode = new ConfigNode("PARSEK_SCENARIO");
            var activeTreeNode = scenarioNode.AddNode("RECORDING_TREE");
            var activeTree = MakeTree("in_flight", "Launching", 2);
            activeTree.Save(activeTreeNode);
            activeTreeNode.AddValue("isActive", "True");

            bool result = ParsekScenario.TryRestoreActiveTreeNode(scenarioNode);

            Assert.True(result);
            Assert.NotNull(RecordingStore.PendingTree);
            Assert.Equal("Launching", RecordingStore.PendingTree.TreeName);
            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);
        }

        [Fact]
        public void TryRestoreActiveTreeNode_SkipsCommittedTreeStashesActiveTree()
        {
            var scenarioNode = new ConfigNode("PARSEK_SCENARIO");
            // Committed tree first (no isActive flag)
            var committedNode = scenarioNode.AddNode("RECORDING_TREE");
            var committedTree = MakeTree("committed", "Prior Mission", 3);
            committedTree.Save(committedNode);
            // Then the active tree
            var activeNode = scenarioNode.AddNode("RECORDING_TREE");
            var activeTree = MakeTree("in_flight", "Current Mission", 2);
            activeTree.Save(activeNode);
            activeNode.AddValue("isActive", "True");

            bool result = ParsekScenario.TryRestoreActiveTreeNode(scenarioNode);

            Assert.True(result);
            Assert.Equal("Current Mission", RecordingStore.PendingTree.TreeName);
            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);
        }

        [Fact]
        public void TryRestoreActiveTreeNode_ParsesResumeRewindSave()
        {
            var scenarioNode = new ConfigNode("PARSEK_SCENARIO");
            var activeNode = scenarioNode.AddNode("RECORDING_TREE");
            var activeTree = MakeTree("in_flight", "Mun Return", 1);
            activeTree.Save(activeNode);
            activeNode.AddValue("isActive", "True");
            activeNode.AddValue("resumeRewindSave", "parsek_rw_abc123");

            bool result = ParsekScenario.TryRestoreActiveTreeNode(scenarioNode);

            Assert.True(result);
            Assert.Equal("parsek_rw_abc123", ParsekScenario.pendingActiveTreeResumeRewindSave);

            // Cleanup for subsequent tests
            ParsekScenario.pendingActiveTreeResumeRewindSave = null;
        }

        [Fact]
        public void TryRestoreActiveTreeNode_NoResumeRewindSave_ClearsStaleValue()
        {
            // Pre-populate a stale resume hint from a prior restore
            ParsekScenario.pendingActiveTreeResumeRewindSave = "stale_save";

            var scenarioNode = new ConfigNode("PARSEK_SCENARIO");
            var activeNode = scenarioNode.AddNode("RECORDING_TREE");
            var activeTree = MakeTree("in_flight", "Pad Walk", 1);
            activeTree.Save(activeNode);
            activeNode.AddValue("isActive", "True");
            // No resumeRewindSave key

            ParsekScenario.TryRestoreActiveTreeNode(scenarioNode);

            // Value becomes null (no key present)
            Assert.Null(ParsekScenario.pendingActiveTreeResumeRewindSave);
        }

        [Fact]
        public void ConfigurePendingQuickloadResumeContext_MatchesActiveTreeEvenIfResumedRecordingChanges()
        {
            var tree = MakeTree("resume_tree", "Resume", 1);
            var other = MakeTree("other_tree", "Other", 1);

            ParsekScenario.ConfigurePendingQuickloadResumeContext(tree);

            Assert.True(ParsekScenario.MatchesPendingQuickloadResumeContext(tree.Id));

            tree.ActiveRecordingId = "parent_after_boarding";
            Assert.True(ParsekScenario.MatchesPendingQuickloadResumeContext(tree.Id));
            Assert.False(ParsekScenario.MatchesPendingQuickloadResumeContext(other.Id));

            ParsekScenario.ClearPendingQuickloadResumeContext();
            Assert.False(ParsekScenario.MatchesPendingQuickloadResumeContext(tree.Id));
        }

        [Fact]
        public void PrepareQuickloadResumeStateIfNeeded_LogsResumePrepSummary()
        {
            var tree = MakeTree("resume_prep_tree", "Resume Prep Tree", 2);
            var activeRec = tree.Recordings[tree.ActiveRecordingId];
            activeRec.Points.Clear();
            activeRec.TrackSections.Clear();
            activeRec.ExplicitStartUT = 100.0;
            activeRec.ExplicitEndUT = 180.0;
            activeRec.Points.Add(new TrajectoryPoint { ut = 100.0 });
            activeRec.Points.Add(new TrajectoryPoint { ut = 180.0 });
            activeRec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 180.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, altitude = 0.0 },
                    new TrajectoryPoint { ut = 180.0, altitude = 10.0 },
                },
                checkpoints = new List<OrbitSegment>(),
            });

            var recorder = new FlightRecorder
            {
                ActiveTree = tree,
            };
            FlightRecorder.QuickloadResumeUTProviderForTesting = () => 150.0;
            ParsekScenario.ConfigurePendingQuickloadResumeContext(tree);

            logLines.Clear();
            InvokePrepareQuickloadResumeStateIfNeeded(recorder);

            Assert.Equal(150.0, activeRec.ExplicitEndUT);
            Assert.False(ParsekScenario.MatchesPendingQuickloadResumeContext(tree.Id));
            Assert.Contains(logLines, l =>
                l.Contains("Quickload resume prep:") &&
                l.Contains($"activeRec='{activeRec.RecordingId}'") &&
                l.Contains("cutoffUT=150.00") &&
                l.Contains("preTrimEndUT=180.00") &&
                l.Contains("treeTrimmed=True") &&
                l.Contains("envResyncTarget=Atmospheric"));
        }

        [Fact]
        public void PrepareQuickloadResumeStateIfNeeded_RunwaySurfaceTail_KeepsRealTakeoffBoundary()
        {
            var tree = MakeTree("runway_resume_tree", "Runway Resume Tree", 1);
            var activeRec = tree.Recordings[tree.ActiveRecordingId];
            activeRec.Points.Clear();
            activeRec.TrackSections.Clear();
            activeRec.ExplicitStartUT = 100.0;
            activeRec.ExplicitEndUT = 110.0;
            activeRec.Points.Add(new TrajectoryPoint { ut = 100.0 });
            activeRec.Points.Add(new TrajectoryPoint { ut = 107.0 });
            activeRec.Points.Add(new TrajectoryPoint { ut = 110.0 });
            activeRec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceStationary,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 110.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, altitude = 0.0 },
                    new TrajectoryPoint { ut = 107.0, altitude = 0.0 },
                    new TrajectoryPoint { ut = 110.0, altitude = 1.0 },
                },
                checkpoints = new List<OrbitSegment>(),
            });

            var recorder = new FlightRecorder
            {
                ActiveTree = tree,
            };
            FlightRecorder.QuickloadResumeUTProviderForTesting = () => 107.0;
            ParsekScenario.ConfigurePendingQuickloadResumeContext(tree);

            logLines.Clear();
            InvokePrepareQuickloadResumeStateIfNeeded(recorder);

            recorder.StartNewTrackSection(SegmentEnvironment.SurfaceStationary, ReferenceFrame.Absolute, 107.0);
            bool relabeled = recorder.TryApplyRestoreEnvironmentResync(
                SegmentEnvironment.Atmospheric,
                109.0);
            recorder.CloseCurrentTrackSection(110.0);

            Assert.False(relabeled);
            Assert.Equal(107.0, activeRec.ExplicitEndUT);
            Assert.Equal(2, activeRec.Points.Count);
            Assert.Single(activeRec.TrackSections);
            Assert.Equal(SegmentEnvironment.SurfaceStationary, activeRec.TrackSections[0].environment);
            Assert.Equal(107.0, activeRec.TrackSections[0].endUT);
            Assert.Equal(2, activeRec.TrackSections[0].frames.Count);
            Assert.Contains(logLines, l =>
                l.Contains("Quickload resume prep:") &&
                l.Contains($"activeRec='{activeRec.RecordingId}'") &&
                l.Contains("envResyncTarget=SurfaceStationary"));
            Assert.Contains(logLines, l =>
                l.Contains("Restore environment resync disarmed:") &&
                l.Contains("transition=Atmospheric") &&
                l.Contains("target=SurfaceStationary"));
            Assert.Single(recorder.TrackSections);
            Assert.Equal(SegmentEnvironment.SurfaceStationary, recorder.TrackSections[0].environment);
        }

        [Fact]
        public void TrimRecordingPastUT_RemovesFuturePayloadAcrossBuffers()
        {
            var rec = new Recording
            {
                RecordingId = "trim_buffers",
                VesselName = "Buffer Trim",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 180.0,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 120.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 160.0 });
            rec.OrbitSegments.Add(new OrbitSegment { startUT = 110.0, endUT = 170.0 });
            rec.PartEvents.Add(new PartEvent { ut = 115.0 });
            rec.PartEvents.Add(new PartEvent { ut = 165.0 });
            rec.FlagEvents.Add(new FlagEvent { ut = 118.0 });
            rec.FlagEvents.Add(new FlagEvent { ut = 168.0 });
            rec.SegmentEvents.Add(new SegmentEvent { ut = 125.0, type = SegmentEventType.TimeJump });
            rec.SegmentEvents.Add(new SegmentEvent { ut = 175.0, type = SegmentEventType.TimeJump });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 170.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, altitude = 10.0 },
                    new TrajectoryPoint { ut = 120.0, altitude = 20.0 },
                    new TrajectoryPoint { ut = 160.0, altitude = 30.0 },
                },
                checkpoints = new List<OrbitSegment>(),
            });

            bool trimmed = ParsekScenario.TrimRecordingPastUT(rec, 130.0);

            Assert.True(trimmed);
            Assert.True(rec.FilesDirty);
            Assert.Equal(2, rec.Points.Count);
            Assert.Equal(100.0, rec.Points[0].ut);
            Assert.Equal(120.0, rec.Points[1].ut);
            Assert.Single(rec.OrbitSegments);
            Assert.Equal(130.0, rec.OrbitSegments[0].endUT);
            Assert.Single(rec.PartEvents);
            Assert.Equal(115.0, rec.PartEvents[0].ut);
            Assert.Single(rec.FlagEvents);
            Assert.Equal(118.0, rec.FlagEvents[0].ut);
            Assert.Single(rec.SegmentEvents);
            Assert.Equal(125.0, rec.SegmentEvents[0].ut);
            Assert.Single(rec.TrackSections);
            Assert.Equal(130.0, rec.TrackSections[0].endUT);
            Assert.Equal(2, rec.TrackSections[0].frames.Count);
            Assert.Equal(130.0, rec.ExplicitEndUT);
        }

        [Fact]
        public void TrimRecordingPastUT_CutoffBeforeFirstSample_RemovesFutureOnlyRecording()
        {
            var rec = new Recording
            {
                RecordingId = "trim_future_only",
                VesselName = "Future Only",
                ExplicitStartUT = 324.92,
                ExplicitEndUT = 333.40,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 324.92 });
            rec.Points.Add(new TrajectoryPoint { ut = 333.40 });
            rec.PartEvents.Add(new PartEvent { ut = 329.70 });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 324.92,
                endUT = 333.40,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 324.92, altitude = 71.0 },
                    new TrajectoryPoint { ut = 333.40, altitude = 94.0 },
                },
                checkpoints = new List<OrbitSegment>(),
            });

            bool trimmed = ParsekScenario.TrimRecordingPastUT(rec, 323.08);

            Assert.True(trimmed);
            Assert.True(rec.FilesDirty);
            Assert.Empty(rec.Points);
            Assert.Empty(rec.PartEvents);
            Assert.Empty(rec.TrackSections);
            Assert.Equal(323.08, rec.ExplicitStartUT);
            Assert.Equal(323.08, rec.ExplicitEndUT);
        }

        [Fact]
        public void TrimRecordingPastUT_TailEnvironmentAfterTrimUsesRemainingSection()
        {
            var rec = new Recording
            {
                RecordingId = "trim_tail_env",
                VesselName = "Tail Env",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 170.0,
            };
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceStationary,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 130.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, altitude = 0.0 },
                    new TrajectoryPoint { ut = 120.0, altitude = 0.0 },
                },
                checkpoints = new List<OrbitSegment>(),
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 130.0,
                endUT = 170.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 140.0, altitude = 10.0 },
                    new TrajectoryPoint { ut = 160.0, altitude = 30.0 },
                },
                checkpoints = new List<OrbitSegment>(),
            });

            bool trimmed = ParsekScenario.TrimRecordingPastUT(rec, 125.0);
            bool hasTailEnv = FlightRecorder.TryGetTailTrackSectionEnvironment(rec, out SegmentEnvironment tailEnv);

            Assert.True(trimmed);
            Assert.True(hasTailEnv);
            Assert.Equal(SegmentEnvironment.SurfaceStationary, tailEnv);
        }

        [Fact]
        public void TrimRecordingTreePastUT_TrimsSiblingRecordingsAcrossTree()
        {
            var tree = MakeTree("trim_tree", "Trim Tree", 2);
            var activeRec = tree.Recordings[tree.ActiveRecordingId];
            activeRec.Points.Clear();
            activeRec.TrackSections.Clear();
            activeRec.ExplicitStartUT = 100.0;
            activeRec.ExplicitEndUT = 160.0;
            activeRec.Points.Add(new TrajectoryPoint { ut = 100.0 });
            activeRec.Points.Add(new TrajectoryPoint { ut = 160.0 });
            activeRec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceStationary,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 160.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, altitude = 0.0 },
                    new TrajectoryPoint { ut = 160.0, altitude = 5.0 },
                },
                checkpoints = new List<OrbitSegment>(),
            });

            var siblingRec = tree.Recordings["child_trim_tree_1"];
            siblingRec.Points.Clear();
            siblingRec.PartEvents.Clear();
            siblingRec.TrackSections.Clear();
            siblingRec.ExplicitStartUT = 120.0;
            siblingRec.ExplicitEndUT = 180.0;
            siblingRec.Points.Add(new TrajectoryPoint { ut = 120.0 });
            siblingRec.Points.Add(new TrajectoryPoint { ut = 175.0 });
            siblingRec.PartEvents.Add(new PartEvent { ut = 176.0 });
            siblingRec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 120.0,
                endUT = 180.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 120.0, altitude = 10.0 },
                    new TrajectoryPoint { ut = 175.0, altitude = 30.0 },
                },
                checkpoints = new List<OrbitSegment>(),
            });

            logLines.Clear();
            bool trimmed = ParsekScenario.TrimRecordingTreePastUT(tree, 150.0);

            Assert.True(trimmed);
            Assert.Equal(150.0, activeRec.ExplicitEndUT);
            Assert.Single(activeRec.Points);
            Assert.Equal(150.0, siblingRec.ExplicitEndUT);
            Assert.Single(siblingRec.Points);
            Assert.Empty(siblingRec.PartEvents);
            Assert.Single(siblingRec.TrackSections);
            Assert.Equal(150.0, siblingRec.TrackSections[0].endUT);
            Assert.Contains(logLines, l =>
                l.Contains("Quickload tree trim:") &&
                l.Contains("tree='Trim Tree'") &&
                l.Contains("cutoffUT=150.00") &&
                l.Contains("trimmedRecordings=2/2") &&
                l.Contains("prunedFutureRecordings=0") &&
                l.Contains("backgroundEntries=0"));
        }

        [Fact]
        public void TrimRecordingTreePastUT_PrunesFutureOnlyBranchStateAndRebuildsBackgroundMap()
        {
            var tree = MakeTree("future_branch_tree", "Future Branch Tree", 2);
            var rootRec = tree.Recordings[tree.RootRecordingId];
            rootRec.VesselPersistentId = 111;

            var futureRec = tree.Recordings["child_future_branch_tree_1"];
            futureRec.Points.Clear();
            futureRec.TrackSections.Clear();
            futureRec.ExplicitStartUT = 170.0;
            futureRec.ExplicitEndUT = 180.0;
            futureRec.VesselPersistentId = 222;
            futureRec.ParentBranchPointId = "bp_future";
            futureRec.Points.Add(new TrajectoryPoint { ut = 170.0 });
            futureRec.Points.Add(new TrajectoryPoint { ut = 180.0 });

            rootRec.ChildBranchPointId = "bp_future";
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp_future",
                UT = 140.0,
                Type = BranchPointType.EVA,
                ParentRecordingIds = new List<string> { rootRec.RecordingId },
                ChildRecordingIds = new List<string> { futureRec.RecordingId }
            });
            tree.BackgroundMap[futureRec.VesselPersistentId] = futureRec.RecordingId;

            logLines.Clear();
            bool trimmed = ParsekScenario.TrimRecordingTreePastUT(tree, 150.0);

            Assert.True(trimmed);
            Assert.False(tree.Recordings.ContainsKey(futureRec.RecordingId));
            Assert.Empty(tree.BranchPoints);
            Assert.Null(rootRec.ChildBranchPointId);
            Assert.Empty(tree.BackgroundMap);
            Assert.Contains(logLines, l =>
                l.Contains("Quickload tree trim:") &&
                l.Contains("tree='Future Branch Tree'") &&
                l.Contains("prunedFutureRecordings=1") &&
                l.Contains("prunedBranchPoints=1") &&
                l.Contains("backgroundEntries=0"));
        }

        [Fact]
        public void TrimRecordingTreePastUT_PrunesFutureOnlyBranchEvenWhenPayloadAlreadyCollapsedAtCutoff()
        {
            var tree = MakeTree("future_empty_tree", "Future Empty Tree", 2);
            var rootRec = tree.Recordings[tree.RootRecordingId];
            rootRec.VesselPersistentId = 111;

            var futureRec = tree.Recordings["child_future_empty_tree_1"];
            futureRec.Points.Clear();
            futureRec.TrackSections.Clear();
            futureRec.PartEvents.Clear();
            futureRec.FlagEvents.Clear();
            futureRec.SegmentEvents.Clear();
            futureRec.ExplicitStartUT = 150.0;
            futureRec.ExplicitEndUT = 150.0;
            futureRec.VesselPersistentId = 222;
            futureRec.ParentBranchPointId = "bp_future_empty";

            rootRec.ChildBranchPointId = "bp_future_empty";
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp_future_empty",
                UT = 150.0,
                Type = BranchPointType.EVA,
                ParentRecordingIds = new List<string> { rootRec.RecordingId },
                ChildRecordingIds = new List<string> { futureRec.RecordingId }
            });
            tree.BackgroundMap[futureRec.VesselPersistentId] = futureRec.RecordingId;

            logLines.Clear();
            bool trimmed = ParsekScenario.TrimRecordingTreePastUT(tree, 150.0);

            Assert.True(trimmed);
            Assert.False(tree.Recordings.ContainsKey(futureRec.RecordingId));
            Assert.Empty(tree.BranchPoints);
            Assert.Null(rootRec.ChildBranchPointId);
            Assert.Empty(tree.BackgroundMap);
            Assert.Contains(logLines, l =>
                l.Contains("Quickload tree trim:") &&
                l.Contains("tree='Future Empty Tree'") &&
                l.Contains("prunedFutureRecordings=1") &&
                l.Contains("prunedBranchPoints=1") &&
                l.Contains("backgroundEntries=0"));
        }

        // ============================================================
        // Bug #610: ChooseQuickloadTrimScope (Re-Fly carve-out)
        // ============================================================

        [Fact]
        public void ChooseQuickloadTrimScope_NoMarker_ReturnsTreeWide()
        {
            var scope = ParsekScenario.ChooseQuickloadTrimScope("tree_a", null, out string reason);

            Assert.Equal(ParsekScenario.QuickloadTrimScope.TreeWide, scope);
            Assert.Equal("no-active-refly-marker", reason);
        }

        [Fact]
        public void ChooseQuickloadTrimScope_MarkerWithoutTreeId_ReturnsTreeWide()
        {
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_x",
                TreeId = "",
                ActiveReFlyRecordingId = "rec_a",
                OriginChildRecordingId = "rec_a",
            };

            var scope = ParsekScenario.ChooseQuickloadTrimScope("tree_a", marker, out string reason);

            Assert.Equal(ParsekScenario.QuickloadTrimScope.TreeWide, scope);
            Assert.Contains("refly-marker-has-no-treeid", reason);
            Assert.Contains("sess=sess_x", reason);
        }

        [Fact]
        public void ChooseQuickloadTrimScope_MarkerForDifferentTree_ReturnsTreeWide()
        {
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_other",
                TreeId = "tree_other",
                ActiveReFlyRecordingId = "rec_other",
                OriginChildRecordingId = "rec_other",
            };

            var scope = ParsekScenario.ChooseQuickloadTrimScope("tree_a", marker, out string reason);

            Assert.Equal(ParsekScenario.QuickloadTrimScope.TreeWide, scope);
            Assert.Contains("refly-marker-tree-mismatch", reason);
            Assert.Contains("markerTree=tree_other", reason);
            Assert.Contains("resumeTree=tree_a", reason);
        }

        [Fact]
        public void ChooseQuickloadTrimScope_MarkerForThisTree_ReturnsActiveRecOnly()
        {
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_match",
                TreeId = "tree_a",
                ActiveReFlyRecordingId = "rec_origin",
                OriginChildRecordingId = "rec_origin",
            };

            var scope = ParsekScenario.ChooseQuickloadTrimScope("tree_a", marker, out string reason);

            Assert.Equal(ParsekScenario.QuickloadTrimScope.ActiveRecOnly, scope);
            Assert.Contains("refly-active", reason);
            Assert.Contains("sess=sess_match", reason);
            Assert.Contains("markerTree=tree_a", reason);
            Assert.Contains("originRec=rec_origin", reason);
        }

        [Fact]
        public void ChooseQuickloadTrimScope_NullResumeTreeId_ReturnsTreeWide()
        {
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_match",
                TreeId = "tree_a",
                ActiveReFlyRecordingId = "rec_origin",
                OriginChildRecordingId = "rec_origin",
            };

            var scope = ParsekScenario.ChooseQuickloadTrimScope(null, marker, out string reason);

            Assert.Equal(ParsekScenario.QuickloadTrimScope.TreeWide, scope);
            Assert.Contains("resume-tree-has-no-id", reason);
        }

        // ============================================================
        // Bug #610: PrepareQuickloadResumeStateIfNeeded with Re-Fly active
        // ============================================================

        [Fact]
        public void PrepareQuickloadResumeStateIfNeeded_ReFlyActive_TrimsActiveOnlyKeepsSibling()
        {
            // Models the post-splice tree shape from Re-Fly load:
            //   active rec (booster atmo) — needs tail trimmed so the recorder can
            //   append fresh post-cutoff data without colliding with the pre-cutoff
            //   timeline.
            //   sibling rec (capsule exo, started past cutoff) — represents the OTHER
            //   vessel's continued timeline; tree-wide trim would prune it.
            var tree = MakeTree("refly_tree", "ReFly Tree", 2);
            var activeRec = tree.Recordings[tree.ActiveRecordingId];
            activeRec.Points.Clear();
            activeRec.TrackSections.Clear();
            activeRec.PartEvents.Clear();
            activeRec.ExplicitStartUT = 200.0;
            activeRec.ExplicitEndUT = 280.0;
            activeRec.Points.Add(new TrajectoryPoint { ut = 200.0 });
            activeRec.Points.Add(new TrajectoryPoint { ut = 250.0 });
            activeRec.Points.Add(new TrajectoryPoint { ut = 280.0 });
            activeRec.PartEvents.Add(new PartEvent { ut = 270.0 });
            activeRec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 200.0,
                endUT = 280.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 200.0, altitude = 50.0 },
                    new TrajectoryPoint { ut = 250.0, altitude = 60.0 },
                    new TrajectoryPoint { ut = 280.0, altitude = 70.0 },
                },
                checkpoints = new List<OrbitSegment>(),
            });

            var siblingRec = tree.Recordings["child_refly_tree_1"];
            siblingRec.Points.Clear();
            siblingRec.PartEvents.Clear();
            siblingRec.TrackSections.Clear();
            siblingRec.ExplicitStartUT = 280.0;
            siblingRec.ExplicitEndUT = 696.0;
            siblingRec.Points.Add(new TrajectoryPoint { ut = 280.0 });
            siblingRec.Points.Add(new TrajectoryPoint { ut = 500.0 });
            siblingRec.Points.Add(new TrajectoryPoint { ut = 696.0 });
            siblingRec.PartEvents.Add(new PartEvent { ut = 400.0 });
            siblingRec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 280.0,
                endUT = 696.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 280.0, altitude = 70.0 },
                    new TrajectoryPoint { ut = 500.0, altitude = 100000.0 },
                    new TrajectoryPoint { ut = 696.0, altitude = 80000.0 },
                },
                checkpoints = new List<OrbitSegment>(),
            });

            var scenario = new ParsekScenario
            {
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = "sess_refly_610",
                    TreeId = tree.Id,
                    ActiveReFlyRecordingId = activeRec.RecordingId,
                    OriginChildRecordingId = activeRec.RecordingId,
                    RewindPointId = "rp_refly_610",
                    InvokedUT = 203.0,
                },
            };
            ParsekScenario.SetInstanceForTesting(scenario);

            try
            {
                var recorder = new FlightRecorder { ActiveTree = tree };
                FlightRecorder.QuickloadResumeUTProviderForTesting = () => 203.0;
                ParsekScenario.ConfigurePendingQuickloadResumeContext(tree);

                logLines.Clear();
                InvokePrepareQuickloadResumeStateIfNeeded(recorder);

                // Active rec was trimmed at cutoff.
                Assert.Equal(203.0, activeRec.ExplicitEndUT);
                Assert.Empty(activeRec.PartEvents);

                // Sibling preserved untouched (tree-wide trim/prune did NOT run).
                Assert.True(tree.Recordings.ContainsKey(siblingRec.RecordingId));
                Assert.Equal(696.0, siblingRec.ExplicitEndUT);
                Assert.Equal(3, siblingRec.Points.Count);
                Assert.Single(siblingRec.PartEvents);
                Assert.Equal(400.0, siblingRec.PartEvents[0].ut);
                Assert.Single(siblingRec.TrackSections);
                Assert.Equal(696.0, siblingRec.TrackSections[0].endUT);
                Assert.Equal(3, siblingRec.TrackSections[0].frames.Count);

                // Resume-prep log carries the chosen scope + reason for auditability.
                Assert.Contains(logLines, l =>
                    l.Contains("Quickload resume prep:") &&
                    l.Contains($"activeRec='{activeRec.RecordingId}'") &&
                    l.Contains("trimScope=ActiveRecOnly") &&
                    l.Contains("refly-active") &&
                    l.Contains("sess=sess_refly_610") &&
                    l.Contains($"markerTree={tree.Id}") &&
                    l.Contains("recordingsInTree=2"));

                // Tree-wide trim line MUST NOT appear (only the per-rec path ran).
                Assert.DoesNotContain(logLines, l => l.Contains("Quickload tree trim:"));
            }
            finally
            {
                ParsekScenario.SetInstanceForTesting(null);
            }
        }

        [Fact]
        public void PrepareQuickloadResumeStateIfNeeded_NoReFlyMarker_KeepsTreeWideTrim()
        {
            // Sanity: without a Re-Fly marker, the existing tree-wide trim still
            // runs (preserves F9 quickload semantics).
            var tree = MakeTree("plain_quickload", "Plain Quickload", 2);
            var activeRec = tree.Recordings[tree.ActiveRecordingId];
            activeRec.Points.Clear();
            activeRec.TrackSections.Clear();
            activeRec.ExplicitStartUT = 100.0;
            activeRec.ExplicitEndUT = 180.0;
            activeRec.Points.Add(new TrajectoryPoint { ut = 100.0 });
            activeRec.Points.Add(new TrajectoryPoint { ut = 180.0 });
            activeRec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 180.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, altitude = 0.0 },
                    new TrajectoryPoint { ut = 180.0, altitude = 10.0 },
                },
                checkpoints = new List<OrbitSegment>(),
            });

            var siblingRec = tree.Recordings["child_plain_quickload_1"];
            siblingRec.Points.Clear();
            siblingRec.TrackSections.Clear();
            siblingRec.ExplicitStartUT = 170.0;
            siblingRec.ExplicitEndUT = 200.0;
            siblingRec.Points.Add(new TrajectoryPoint { ut = 170.0 });
            siblingRec.Points.Add(new TrajectoryPoint { ut = 200.0 });

            // No ParsekScenario instance with a marker -> ChooseQuickloadTrimScope
            // returns TreeWide.
            ParsekScenario.SetInstanceForTesting(null);

            var recorder = new FlightRecorder { ActiveTree = tree };
            FlightRecorder.QuickloadResumeUTProviderForTesting = () => 150.0;
            ParsekScenario.ConfigurePendingQuickloadResumeContext(tree);

            logLines.Clear();
            InvokePrepareQuickloadResumeStateIfNeeded(recorder);

            Assert.Equal(150.0, activeRec.ExplicitEndUT);
            Assert.Equal(150.0, siblingRec.ExplicitEndUT); // tree-wide trim clipped sibling too
            Assert.Contains(logLines, l =>
                l.Contains("Quickload resume prep:") &&
                l.Contains("trimScope=TreeWide") &&
                l.Contains("no-active-refly-marker"));
            Assert.Contains(logLines, l => l.Contains("Quickload tree trim:"));
        }

        [Fact]
        public void PrepareQuickloadResumeStateIfNeeded_ReFlyMarkerForOtherTree_KeepsTreeWideTrim()
        {
            // Marker exists but pins a DIFFERENT tree — fall through to tree-wide
            // trim so a stale or unrelated marker can't accidentally protect a
            // separate tree's post-cutoff payload.
            var tree = MakeTree("isolated_tree", "Isolated Tree", 2);
            var activeRec = tree.Recordings[tree.ActiveRecordingId];
            activeRec.Points.Clear();
            activeRec.TrackSections.Clear();
            activeRec.ExplicitStartUT = 100.0;
            activeRec.ExplicitEndUT = 180.0;
            activeRec.Points.Add(new TrajectoryPoint { ut = 100.0 });
            activeRec.Points.Add(new TrajectoryPoint { ut = 180.0 });
            activeRec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 180.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, altitude = 0.0 },
                    new TrajectoryPoint { ut = 180.0, altitude = 10.0 },
                },
                checkpoints = new List<OrbitSegment>(),
            });

            var siblingRec = tree.Recordings["child_isolated_tree_1"];
            siblingRec.Points.Clear();
            siblingRec.TrackSections.Clear();
            siblingRec.ExplicitStartUT = 170.0;
            siblingRec.ExplicitEndUT = 200.0;
            siblingRec.Points.Add(new TrajectoryPoint { ut = 170.0 });
            siblingRec.Points.Add(new TrajectoryPoint { ut = 200.0 });

            var scenario = new ParsekScenario
            {
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = "sess_other",
                    TreeId = "some_other_tree",
                    ActiveReFlyRecordingId = "rec_x",
                    OriginChildRecordingId = "rec_x",
                },
            };
            ParsekScenario.SetInstanceForTesting(scenario);

            try
            {
                var recorder = new FlightRecorder { ActiveTree = tree };
                FlightRecorder.QuickloadResumeUTProviderForTesting = () => 150.0;
                ParsekScenario.ConfigurePendingQuickloadResumeContext(tree);

                logLines.Clear();
                InvokePrepareQuickloadResumeStateIfNeeded(recorder);

                Assert.Equal(150.0, activeRec.ExplicitEndUT);
                Assert.Equal(150.0, siblingRec.ExplicitEndUT);
                Assert.Contains(logLines, l =>
                    l.Contains("Quickload resume prep:") &&
                    l.Contains("trimScope=TreeWide") &&
                    l.Contains("refly-marker-tree-mismatch") &&
                    l.Contains("markerTree=some_other_tree") &&
                    l.Contains($"resumeTree={tree.Id}"));
            }
            finally
            {
                ParsekScenario.SetInstanceForTesting(null);
            }
        }

        [Fact]
        public void SaveActiveTreeIfAny_CopiesRecorderRewindSaveToSerializedRoot()
        {
            string srcRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "Parsek"));
            string scenarioSrc = System.IO.File.ReadAllText(
                System.IO.Path.Combine(srcRoot, "ParsekScenario.cs"));

            int methodStart = scenarioSrc.IndexOf(
                "private static void SaveActiveTreeIfAny(ConfigNode node)",
                StringComparison.Ordinal);
            Assert.True(methodStart >= 0, "SaveActiveTreeIfAny method signature not found");

            int copyIdx = scenarioSrc.IndexOf(
                "ParsekFlight.CopyRewindSaveToRoot(",
                methodStart,
                StringComparison.Ordinal);
            int saveIdx = scenarioSrc.IndexOf(
                "activeTree.Save(treeNode);",
                methodStart,
                StringComparison.Ordinal);

            Assert.True(copyIdx >= 0, "SaveActiveTreeIfAny no longer copies rewind metadata to the root");
            Assert.True(saveIdx >= 0, "SaveActiveTreeIfAny serialization site not found");
            Assert.True(copyIdx < saveIdx,
                "SaveActiveTreeIfAny must copy rewind metadata before serializing the active tree");
            string methodSrc = scenarioSrc.Substring(methodStart, saveIdx - methodStart);
            Assert.Contains("ParsekFlight.CopyRewindSaveToRoot(", methodSrc);
            Assert.Contains("activeTree,", methodSrc);
            Assert.Contains("recorder,", methodSrc);
        }

        [Fact]
        public void RecorderBackedCommitPaths_UseRecorderRootCopyOverload()
        {
            string srcRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "Parsek"));
            string flightSrc = System.IO.File.ReadAllText(
                System.IO.Path.Combine(srcRoot, "ParsekFlight.cs"));

            Assert.Contains("CopyRewindSaveToRoot(activeTree, recorder,", flightSrc);
            Assert.Contains("CopyRewindSaveToRoot(activeTree, splitRecorder);", flightSrc);
            Assert.Contains("CopyRewindSaveToRoot(tree, recorder,", flightSrc);
        }

        [Fact]
        public void RestoreActiveTreeFromPending_RearmsRecorderSessionHooks_AfterRecorderStart_BugKerbalXF5F9()
        {
            string srcRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "Parsek"));
            string flightSrc = System.IO.File.ReadAllText(
                System.IO.Path.Combine(srcRoot, "ParsekFlight.cs"));

            int methodStart = flightSrc.IndexOf(
                "IEnumerator RestoreActiveTreeFromPending()",
                StringComparison.Ordinal);
            Assert.True(methodStart >= 0,
                "RestoreActiveTreeFromPending() method signature not found");

            int methodEnd = flightSrc.IndexOf(
                "IEnumerator RestoreActiveTreeFromPendingForVesselSwitch()",
                methodStart,
                StringComparison.Ordinal);
            Assert.True(methodEnd > methodStart,
                "RestoreActiveTreeFromPending() method end not found");

            string methodSrc = flightSrc.Substring(methodStart, methodEnd - methodStart);

            int startIdx = methodSrc.IndexOf(
                "recorder.StartRecording(isPromotion: true);",
                StringComparison.Ordinal);
            int hookIdx = methodSrc.IndexOf(
                "PrepareSessionStateForRecorderStart(\"RestoreActiveTreeFromPending\")",
                StringComparison.Ordinal);
            int resumeIdx = methodSrc.IndexOf(
                "RestoreActiveTreeFromPending: resumed recording tree",
                StringComparison.Ordinal);

            Assert.True(startIdx >= 0,
                "RestoreActiveTreeFromPending() no longer starts the resumed recorder");
            Assert.True(hookIdx >= 0,
                "RestoreActiveTreeFromPending() must re-arm recorder session hooks after quickload resume");
            Assert.True(resumeIdx >= 0,
                "RestoreActiveTreeFromPending() resumed-recording log not found");
            Assert.True(startIdx < hookIdx,
                "RestoreActiveTreeFromPending() must re-arm recorder session hooks after recorder.StartRecording()");
            Assert.True(hookIdx < resumeIdx,
                "RestoreActiveTreeFromPending() must arm recorder session hooks before reporting resume success");
        }

        [Fact]
        public void TryRestoreActiveTreeNode_DoesNotWriteDeadBoundaryAnchorUT()
        {
            // BoundaryAnchor can't round-trip (needs full TrajectoryPoint, not just UT),
            // so we removed the resumeBoundaryAnchorUT serialization and parsing. This
            // test documents that a save with a legacy resumeBoundaryAnchorUT key from
            // an older build is simply ignored, not parsed back into anything.
            var scenarioNode = new ConfigNode("PARSEK_SCENARIO");
            var activeNode = scenarioNode.AddNode("RECORDING_TREE");
            var activeTree = MakeTree("in_flight", "Legacy Save", 1);
            activeTree.Save(activeNode);
            activeNode.AddValue("isActive", "True");
            activeNode.AddValue("resumeBoundaryAnchorUT", "99999.99"); // legacy key

            bool result = ParsekScenario.TryRestoreActiveTreeNode(scenarioNode);

            Assert.True(result);
            // The active tree is still restored; we just don't try to parse the anchor.
            Assert.Equal("Legacy Save", RecordingStore.PendingTree.TreeName);
        }

        [Fact]
        public void TryRestoreActiveTreeNode_DedupeCommittedTreeById()
        {
            // Reviewer edge case: flight → quicksave (writes isActive=True) →
            // exit to TS (commits tree into committedTrees) → quickload.
            // In-memory committedTrees still has the T3 version, disk save has the
            // T2 active version. Without dedupe, next OnSave writes the tree twice
            // with the same id. TryRestoreActiveTreeNode must remove the committed
            // copy before stashing the active copy.
            var committedTree = MakeTree("tree_x", "Duplicate Id", 2);
            RecordingStore.CommitTree(committedTree);
            Assert.Contains(committedTree, RecordingStore.CommittedTrees);

            // Build a save node with an isActive=True tree using the SAME id
            var scenarioNode = new ConfigNode("PARSEK_SCENARIO");
            var activeNode = scenarioNode.AddNode("RECORDING_TREE");
            var activeTree = MakeTree("tree_x", "Duplicate Id (active)", 2);
            activeTree.Save(activeNode);
            activeNode.AddValue("isActive", "True");

            ParsekScenario.TryRestoreActiveTreeNode(scenarioNode);

            // Committed copy should be gone; pending-Limbo holds the active version.
            Assert.DoesNotContain(
                RecordingStore.CommittedTrees,
                t => t.Id == "tree_x");
            Assert.Equal("Duplicate Id (active)", RecordingStore.PendingTree.TreeName);
            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);
        }

        [Fact]
        public void LastRecordedAltitude_DefaultsToNaN()
        {
            // LastRecordedAltitude caches the last committed point's altitude so
            // HandleSoiAutoSplit can read it even after FlushRecorderIntoActiveTreeForSerialization
            // clears the Recording buffer. The default is NaN (no points recorded yet),
            // which HandleSoiAutoSplit treats as "exo" (no altitude → fall through to
            // the default phase classification).
            var recorder = new FlightRecorder();
            Assert.True(double.IsNaN(recorder.LastRecordedAltitude));
        }

        [Fact]
        public void TryRestoreActiveTreeNode_HydrationFailureSalvagesIntoDiskTreeWithoutOverwriteWarning()
        {
            // The save-backed tree below has no real sidecars in the unit-test environment,
            // so TryRestoreActiveTreeNode will hit sidecar hydration failure(s). The restore
            // path should now salvage the failed recordings from the matching in-memory pending
            // tree into the disk tree rather than replacing the entire tree, and it should do
            // so without firing the overwrite-warning path.
            var staleInMemoryTree = MakeTree("tree_y", "Stale In-Memory", 1);
            RecordingStore.StashPendingTree(staleInMemoryTree, PendingTreeState.Limbo);

            logLines.Clear();

            var scenarioNode = new ConfigNode("PARSEK_SCENARIO");
            var activeNode = scenarioNode.AddNode("RECORDING_TREE");
            var diskTree = MakeTree("tree_y", "Disk Version", 1);
            diskTree.Save(activeNode);
            activeNode.AddValue("isActive", "True");

            ParsekScenario.TryRestoreActiveTreeNode(scenarioNode);

            Assert.Equal("Disk Version", RecordingStore.PendingTree.TreeName);
            Assert.DoesNotContain(logLines,
                l => l.Contains("overwriting existing pending tree"));
        }

        [Fact]
        public void ShouldKeepPendingTreeAfterHydrationFailure_MatchingPendingTreeAndStaleEpoch_ReturnsTrue()
        {
            var pendingTree = MakeTree("tree_hydration", "In-Memory Pending", 1);
            RecordingStore.StashPendingTree(pendingTree, PendingTreeState.Limbo);
            var diskTree = MakeTree("tree_hydration", "Disk Active", 1);

            bool keepPending = ParsekScenario.ShouldKeepPendingTreeAfterHydrationFailure(
                diskTree, staleEpochHydrationFailures: 1);

            Assert.True(keepPending);
        }

        [Fact]
        public void ShouldKeepPendingTreeAfterHydrationFailure_MatchingPendingTreeAndGenericFailure_ReturnsFalse()
        {
            var pendingTree = MakeTree("tree_hydration_generic", "In-Memory Pending", 1);
            RecordingStore.StashPendingTree(pendingTree, PendingTreeState.Limbo);
            var diskTree = MakeTree("tree_hydration_generic", "Disk Active", 1);

            bool keepPending = ParsekScenario.ShouldKeepPendingTreeAfterHydrationFailure(
                diskTree, staleEpochHydrationFailures: 0);

            Assert.False(keepPending);
        }

        [Fact]
        public void RestoreHydrationFailedRecordingsFromPendingTree_RestoresOnlyFailedMatches()
        {
            var pendingTree = MakeTree("tree_salvage", "Pending", 2);
            pendingTree.Recordings["child_tree_salvage_1"].VesselName = "Pending Child";
            pendingTree.Recordings["child_tree_salvage_1"].Points.Add(new TrajectoryPoint { ut = 999 });
            RecordingStore.StashPendingTree(pendingTree, PendingTreeState.Limbo);

            var loadedTree = MakeTree("tree_salvage", "Disk", 2);
            loadedTree.Recordings["child_tree_salvage_1"].SidecarLoadFailed = true;
            loadedTree.Recordings["child_tree_salvage_1"].SidecarLoadFailureReason = "trajectory-missing";

            int restored = ParsekScenario.RestoreHydrationFailedRecordingsFromPendingTree(loadedTree);

            Assert.Equal(1, restored);
            Assert.Equal("Disk #0", loadedTree.Recordings["root_tree_salvage"].VesselName);
            Assert.Equal("Pending Child", loadedTree.Recordings["child_tree_salvage_1"].VesselName);
            Assert.Equal(3, loadedTree.Recordings["child_tree_salvage_1"].Points.Count);
            Assert.True(loadedTree.Recordings["child_tree_salvage_1"].FilesDirty);
            Assert.False(loadedTree.Recordings["child_tree_salvage_1"].SidecarLoadFailed);
            Assert.Null(loadedTree.Recordings["child_tree_salvage_1"].SidecarLoadFailureReason);
        }

        [Fact]
        public void RestoreHydrationFailedRecordingsFromCommittedTree_RestoresFailedActiveTreeMatches()
        {
            var committedTree = MakeTree("tree_refly_salvage", "Committed", 2);
            var committedRoot = committedTree.Recordings["root_tree_refly_salvage"];
            committedRoot.VesselName = "Committed Root";
            committedRoot.Points.Add(new TrajectoryPoint { ut = 999 });
            committedRoot.SidecarEpoch = 6;
            committedRoot.FilesDirty = false;
            RecordingStore.AddCommittedTreeForTesting(committedTree);
            foreach (var rec in committedTree.Recordings.Values)
                RecordingStore.AddCommittedInternal(rec);

            var activeTree = MakeTree("tree_refly_salvage", "Active", 2);
            var activeRoot = activeTree.Recordings["root_tree_refly_salvage"];
            Recording activeRootReference = activeRoot;
            activeRoot.Points.Clear();
            activeRoot.OrbitSegments.Clear();
            activeRoot.TrackSections.Clear();
            activeRoot.SidecarEpoch = 2;
            activeRoot.SidecarLoadFailed = true;
            activeRoot.SidecarLoadFailureReason = "stale-sidecar-epoch";
            activeRoot.FilesDirty = true;
            activeRoot.MergeState = MergeState.NotCommitted;
            activeRoot.CreatingSessionId = "sess_refly";
            activeRoot.SupersedeTargetId = "old_root";
            activeRoot.ProvisionalForRpId = "rp_refly";

            int restored = ParsekScenario.RestoreHydrationFailedRecordingsFromCommittedTree(activeTree);

            Assert.Equal(1, restored);
            var restoredRoot = activeTree.Recordings["root_tree_refly_salvage"];
            Assert.Same(activeRootReference, restoredRoot);
            Assert.Equal("Committed Root", restoredRoot.VesselName);
            Assert.Equal(3, restoredRoot.Points.Count);
            Assert.Equal(6, restoredRoot.SidecarEpoch);
            Assert.False(restoredRoot.FilesDirty);
            Assert.False(restoredRoot.SidecarLoadFailed);
            Assert.Null(restoredRoot.SidecarLoadFailureReason);
            Assert.Equal(MergeState.NotCommitted, restoredRoot.MergeState);
            Assert.Equal("sess_refly", restoredRoot.CreatingSessionId);
            Assert.Equal("old_root", restoredRoot.SupersedeTargetId);
            Assert.Equal("rp_refly", restoredRoot.ProvisionalForRpId);
        }

        // PR #572 P2 review follow-up: the repair must be scoped to records
        // that explicitly failed to hydrate from sidecar. A dirty + empty
        // record without `SidecarLoadFailed=true` is a legitimate metadata
        // / snapshot-only edit; copying the committed trajectory over it
        // would silently overwrite the in-memory mutation.
        [Fact]
        public void RestoreHydrationFailedRecordingsFromCommittedTree_DirtyEmptyButNotHydrationFailed_NotRepaired()
        {
            var committedTree = MakeTree("tree_p2_scope", "Committed", 1);
            var committedRoot = committedTree.Recordings["root_tree_p2_scope"];
            committedRoot.VesselName = "Committed Root";
            committedRoot.Points.Add(new TrajectoryPoint { ut = 999 });
            committedRoot.SidecarEpoch = 4;
            committedRoot.FilesDirty = false;
            RecordingStore.AddCommittedTreeForTesting(committedTree);
            foreach (var rec in committedTree.Recordings.Values)
                RecordingStore.AddCommittedInternal(rec);

            var activeTree = MakeTree("tree_p2_scope", "Active", 1);
            var activeRoot = activeTree.Recordings["root_tree_p2_scope"];
            activeRoot.Points.Clear();
            activeRoot.OrbitSegments.Clear();
            activeRoot.TrackSections.Clear();
            activeRoot.VesselName = "Mid-edit Name";
            // No SidecarLoadFailed: the record is dirty for a legitimate
            // reason (e.g. mid-flight metadata edit before any trajectory
            // sample was flushed).
            activeRoot.SidecarLoadFailed = false;
            activeRoot.SidecarLoadFailureReason = null;
            activeRoot.FilesDirty = true;

            int restored = ParsekScenario.RestoreHydrationFailedRecordingsFromCommittedTree(activeTree);

            Assert.Equal(0, restored);
            Assert.Empty(activeRoot.Points);
            Assert.Equal("Mid-edit Name", activeRoot.VesselName);
            Assert.True(activeRoot.FilesDirty);
        }

        // PR #572 P2 review follow-up: snapshot-only hydration failures route
        // through the pending-tree salvage path; the committed-tree repair
        // must NOT overwrite their in-memory state with a stale committed
        // copy (the snapshot-only-rescue path's #585 carve-out already
        // restores the snapshot bytes from the pending tree).
        [Fact]
        public void RestoreHydrationFailedRecordingsFromCommittedTree_SnapshotOnlyHydrationFailure_NotRepaired()
        {
            var committedTree = MakeTree("tree_p2_snapshot", "Committed", 1);
            var committedRoot = committedTree.Recordings["root_tree_p2_snapshot"];
            committedRoot.Points.Add(new TrajectoryPoint { ut = 999 });
            committedRoot.SidecarEpoch = 5;
            committedRoot.FilesDirty = false;
            RecordingStore.AddCommittedTreeForTesting(committedTree);
            foreach (var rec in committedTree.Recordings.Values)
                RecordingStore.AddCommittedInternal(rec);

            var activeTree = MakeTree("tree_p2_snapshot", "Active", 1);
            var activeRoot = activeTree.Recordings["root_tree_p2_snapshot"];
            activeRoot.Points.Clear();
            activeRoot.OrbitSegments.Clear();
            activeRoot.TrackSections.Clear();
            activeRoot.SidecarLoadFailed = true;
            // Snapshot-only failure: the trajectory sidecar was fine, only
            // the snapshot blob failed to hydrate.
            activeRoot.SidecarLoadFailureReason = "snapshot-vessel-invalid";
            activeRoot.FilesDirty = true;

            int restored = ParsekScenario.RestoreHydrationFailedRecordingsFromCommittedTree(activeTree);

            Assert.Equal(0, restored);
            Assert.Empty(activeRoot.Points);
            Assert.True(activeRoot.SidecarLoadFailed);
        }

        // PR #572 P2 review follow-up: ApplyPersistenceArtifactsFrom copies
        // CrewEndStatesResolved but NOT the CrewEndStates dictionary itself.
        // Verify that the repair copies both, so the safety-net population
        // doesn't skip an already-resolved record with a null/stale dict.
        [Fact]
        public void RestoreHydrationFailedRecordingsFromCommittedTree_CopiesCrewEndStatesAndSpawnSuppression()
        {
            var committedTree = MakeTree("tree_p2_crew", "Committed", 1);
            var committedRoot = committedTree.Recordings["root_tree_p2_crew"];
            committedRoot.Points.Add(new TrajectoryPoint { ut = 999 });
            committedRoot.SidecarEpoch = 7;
            committedRoot.FilesDirty = false;
            committedRoot.CrewEndStates = new Dictionary<string, KerbalEndState>(StringComparer.Ordinal)
            {
                { "Jeb", KerbalEndState.Recovered },
                { "Bob", KerbalEndState.Aboard },
            };
            committedRoot.CrewEndStatesResolved = true;
            committedRoot.SpawnSuppressedByRewind = true;
            committedRoot.SpawnSuppressedByRewindReason = "same-recording-active-source";
            committedRoot.SpawnSuppressedByRewindUT = 12.5;
            RecordingStore.AddCommittedTreeForTesting(committedTree);
            foreach (var rec in committedTree.Recordings.Values)
                RecordingStore.AddCommittedInternal(rec);

            var activeTree = MakeTree("tree_p2_crew", "Active", 1);
            var activeRoot = activeTree.Recordings["root_tree_p2_crew"];
            activeRoot.Points.Clear();
            activeRoot.OrbitSegments.Clear();
            activeRoot.TrackSections.Clear();
            activeRoot.CrewEndStates = null;
            activeRoot.CrewEndStatesResolved = false;
            activeRoot.SpawnSuppressedByRewind = false;
            activeRoot.SpawnSuppressedByRewindReason = null;
            activeRoot.SpawnSuppressedByRewindUT = double.NaN;
            activeRoot.SidecarEpoch = 2;
            activeRoot.SidecarLoadFailed = true;
            activeRoot.SidecarLoadFailureReason = "stale-sidecar-epoch";
            activeRoot.FilesDirty = true;

            int restored = ParsekScenario.RestoreHydrationFailedRecordingsFromCommittedTree(activeTree);

            Assert.Equal(1, restored);
            var restoredRoot = activeTree.Recordings["root_tree_p2_crew"];
            // CrewEndStates: dictionary populated, NOT null + resolved=true.
            Assert.True(restoredRoot.CrewEndStatesResolved);
            Assert.NotNull(restoredRoot.CrewEndStates);
            Assert.Equal(2, restoredRoot.CrewEndStates.Count);
            Assert.Equal(KerbalEndState.Recovered, restoredRoot.CrewEndStates["Jeb"]);
            Assert.Equal(KerbalEndState.Aboard, restoredRoot.CrewEndStates["Bob"]);
            // The dictionary must be a fresh instance, not a shared reference
            // (mutation of the source must not affect the restored target).
            Assert.NotSame(committedRoot.CrewEndStates, restoredRoot.CrewEndStates);
            // SpawnSuppressedByRewind* triplet round-trips.
            Assert.True(restoredRoot.SpawnSuppressedByRewind);
            Assert.Equal("same-recording-active-source", restoredRoot.SpawnSuppressedByRewindReason);
            Assert.Equal(12.5, restoredRoot.SpawnSuppressedByRewindUT);
        }

        [Fact]
        public void RestoreHydrationFailedRecordingsFromCommittedTree_SkipsActiveRecording()
        {
            var committedTree = MakeTree("tree_refly_active_skip", "Committed", 1);
            var committedRoot = committedTree.Recordings["root_tree_refly_active_skip"];
            committedRoot.Points.Add(new TrajectoryPoint { ut = 999 });
            RecordingStore.AddCommittedTreeForTesting(committedTree);
            foreach (var rec in committedTree.Recordings.Values)
                RecordingStore.AddCommittedInternal(rec);

            var activeTree = MakeTree("tree_refly_active_skip", "Active", 1);
            var activeRoot = activeTree.Recordings["root_tree_refly_active_skip"];
            activeRoot.Points.Clear();
            activeRoot.OrbitSegments.Clear();
            activeRoot.TrackSections.Clear();
            activeRoot.SidecarLoadFailed = true;
            activeRoot.SidecarLoadFailureReason = "stale-sidecar-epoch";
            activeRoot.FilesDirty = true;

            int restored = ParsekScenario.RestoreHydrationFailedRecordingsFromCommittedTree(
                activeTree,
                activeTree.ActiveRecordingId);

            Assert.Equal(0, restored);
            Assert.Empty(activeRoot.Points);
            Assert.True(activeRoot.FilesDirty);
            Assert.True(activeRoot.SidecarLoadFailed);
        }

        [Fact]
        public void RestoreHydrationFailedRecordingsFromPendingTree_SnapshotFailure_KeepsDiskTrajectory()
        {
            var pendingTree = MakeTree("tree_snapshot_salvage", "Pending", 2);
            var pendingRec = pendingTree.Recordings["child_tree_snapshot_salvage_1"];
            pendingRec.VesselSnapshot = BuildSnapshot("Pending Vessel", 7001);
            pendingRec.GhostVisualSnapshot = BuildSnapshot("Pending Ghost", 7002);
            pendingRec.GhostSnapshotMode = GhostSnapshotMode.Separate;
            pendingRec.Points.Clear();
            pendingRec.Points.Add(new TrajectoryPoint { ut = 999 });
            pendingRec.Points.Add(new TrajectoryPoint { ut = 1009 });
            RecordingStore.StashPendingTree(pendingTree, PendingTreeState.Limbo);

            var loadedTree = MakeTree("tree_snapshot_salvage", "Disk", 2);
            var loadedRec = loadedTree.Recordings["child_tree_snapshot_salvage_1"];
            loadedRec.VesselSnapshot = BuildSnapshot("Disk Vessel", 7101);
            loadedRec.GhostSnapshotMode = GhostSnapshotMode.Separate;
            loadedRec.SidecarLoadFailed = true;
            loadedRec.SidecarLoadFailureReason = "snapshot-ghost-invalid";

            int restored = ParsekScenario.RestoreHydrationFailedRecordingsFromPendingTree(loadedTree);

            Assert.Equal(1, restored);
            Assert.Equal(2, loadedRec.Points.Count);
            Assert.Equal(110, loadedRec.Points[0].ut);
            Assert.Equal(120, loadedRec.Points[1].ut);
            Assert.Equal("Disk Vessel", loadedRec.VesselSnapshot.GetValue("name"));
            Assert.Equal("Pending Ghost", loadedRec.GhostVisualSnapshot.GetValue("name"));
            Assert.True(loadedRec.FilesDirty);
            Assert.False(loadedRec.SidecarLoadFailed);
            Assert.Null(loadedRec.SidecarLoadFailureReason);
        }

        [Fact]
        public void RestoreHydrationFailedRecordingsFromPendingTree_AliasSnapshotFailure_RestoresCoherentAliasState()
        {
            var pendingTree = MakeTree("tree_snapshot_alias", "Pending", 2);
            var pendingRec = pendingTree.Recordings["child_tree_snapshot_alias_1"];
            pendingRec.VesselSnapshot = BuildSnapshot("Pending Alias Vessel", 7201);
            pendingRec.GhostVisualSnapshot = BuildSnapshot("Pending Alias Ghost", 7202);
            pendingRec.GhostSnapshotMode = GhostSnapshotMode.AliasVessel;
            RecordingStore.StashPendingTree(pendingTree, PendingTreeState.Limbo);

            var loadedTree = MakeTree("tree_snapshot_alias", "Disk", 2);
            var loadedRec = loadedTree.Recordings["child_tree_snapshot_alias_1"];
            loadedRec.VesselSnapshot = null;
            loadedRec.GhostVisualSnapshot = null;
            loadedRec.GhostSnapshotMode = GhostSnapshotMode.AliasVessel;
            loadedRec.SidecarLoadFailed = true;
            loadedRec.SidecarLoadFailureReason = "snapshot-vessel-invalid";

            int restored = ParsekScenario.RestoreHydrationFailedRecordingsFromPendingTree(loadedTree);

            Assert.Equal(1, restored);
            Assert.Equal(GhostSnapshotMode.AliasVessel, loadedRec.GhostSnapshotMode);
            Assert.NotNull(loadedRec.VesselSnapshot);
            Assert.NotNull(loadedRec.GhostVisualSnapshot);
            Assert.Equal("Pending Alias Vessel", loadedRec.VesselSnapshot.GetValue("name"));
            Assert.Equal("Pending Alias Vessel", loadedRec.GhostVisualSnapshot.GetValue("name"));
            Assert.False(loadedRec.SidecarLoadFailed);
        }

        [Fact]
        public void RestoreHydrationFailedRecordingsFromPendingTree_MultiSideSnapshotFailure_RestoresMissingSnapshotSet()
        {
            var pendingTree = MakeTree("tree_snapshot_multi", "Pending", 2);
            var pendingRec = pendingTree.Recordings["child_tree_snapshot_multi_1"];
            pendingRec.VesselSnapshot = BuildSnapshot("Pending Multi Vessel", 7301);
            pendingRec.GhostVisualSnapshot = BuildSnapshot("Pending Multi Ghost", 7302);
            pendingRec.GhostSnapshotMode = GhostSnapshotMode.Separate;
            RecordingStore.StashPendingTree(pendingTree, PendingTreeState.Limbo);

            var loadedTree = MakeTree("tree_snapshot_multi", "Disk", 2);
            var loadedRec = loadedTree.Recordings["child_tree_snapshot_multi_1"];
            loadedRec.VesselSnapshot = null;
            loadedRec.GhostVisualSnapshot = null;
            loadedRec.GhostSnapshotMode = GhostSnapshotMode.Separate;
            loadedRec.SidecarLoadFailed = true;
            loadedRec.SidecarLoadFailureReason = "snapshot-vessel-invalid";

            int restored = ParsekScenario.RestoreHydrationFailedRecordingsFromPendingTree(loadedTree);

            Assert.Equal(1, restored);
            Assert.NotNull(loadedRec.VesselSnapshot);
            Assert.NotNull(loadedRec.GhostVisualSnapshot);
            Assert.Equal("Pending Multi Vessel", loadedRec.VesselSnapshot.GetValue("name"));
            Assert.Equal("Pending Multi Ghost", loadedRec.GhostVisualSnapshot.GetValue("name"));
            Assert.False(loadedRec.SidecarLoadFailed);
        }

        [Fact]
        public void RestoreHydrationFailedRecordingsFromPendingTree_UnrecoverableSnapshotFailure_DoesNotReplaceDiskTrajectory()
        {
            var pendingTree = MakeTree("tree_snapshot_unrecoverable", "Pending", 2);
            var pendingRec = pendingTree.Recordings["child_tree_snapshot_unrecoverable_1"];
            pendingRec.VesselSnapshot = null;
            pendingRec.GhostVisualSnapshot = null;
            pendingRec.GhostSnapshotMode = GhostSnapshotMode.Separate;
            pendingRec.Points.Clear();
            pendingRec.Points.Add(new TrajectoryPoint { ut = 999 });
            pendingRec.Points.Add(new TrajectoryPoint { ut = 1009 });
            RecordingStore.StashPendingTree(pendingTree, PendingTreeState.Limbo);

            var loadedTree = MakeTree("tree_snapshot_unrecoverable", "Disk", 2);
            var loadedRec = loadedTree.Recordings["child_tree_snapshot_unrecoverable_1"];
            loadedRec.VesselSnapshot = null;
            loadedRec.GhostVisualSnapshot = null;
            loadedRec.GhostSnapshotMode = GhostSnapshotMode.Separate;
            loadedRec.SidecarLoadFailed = true;
            loadedRec.SidecarLoadFailureReason = "snapshot-ghost-invalid";

            int restored = ParsekScenario.RestoreHydrationFailedRecordingsFromPendingTree(loadedTree);

            Assert.Equal(0, restored);
            Assert.Equal(2, loadedRec.Points.Count);
            Assert.Equal(110, loadedRec.Points[0].ut);
            Assert.Equal(120, loadedRec.Points[1].ut);
            Assert.True(loadedRec.SidecarLoadFailed);
            Assert.Equal("snapshot-ghost-invalid", loadedRec.SidecarLoadFailureReason);
            Assert.False(loadedRec.FilesDirty);
        }

        [Fact]
        public void RestoreHydrationFailedRecordingsFromPendingTree_ThenSaveRecordingFiles_HealsSidecarsAndClearsFilesDirty()
        {
            // T61 end-to-end slice: prove a later save after salvage actually rewrites
            // the .prec sidecar to disk and clears FilesDirty. The other salvage tests
            // stop at "FilesDirty = true" after restore; this one drives the subsequent
            // SaveRecordingFilesToPathsForTesting call and asserts the resulting bytes.
            var pendingTree = MakeTree("tree_t61_heal", "Pending", 2);
            var pendingChild = pendingTree.Recordings["child_tree_t61_heal_1"];
            pendingChild.VesselName = "Pending Child";
            // The v3 binary writer requires bodyName to be non-null on every point.
            // MakeTree leaves it unset; set it explicitly on the points that will be
            // DeepCloned into the disk tree so the post-salvage save path succeeds.
            for (int i = 0; i < pendingChild.Points.Count; i++)
            {
                var p = pendingChild.Points[i];
                p.bodyName = "Kerbin";
                pendingChild.Points[i] = p;
            }
            pendingChild.Points.Add(new TrajectoryPoint { ut = 999, bodyName = "Kerbin" });
            RecordingStore.StashPendingTree(pendingTree, PendingTreeState.Limbo);

            var loadedTree = MakeTree("tree_t61_heal", "Disk", 2);
            loadedTree.Recordings["child_tree_t61_heal_1"].SidecarLoadFailed = true;
            loadedTree.Recordings["child_tree_t61_heal_1"].SidecarLoadFailureReason = "trajectory-missing";
            int preSaveEpoch = loadedTree.Recordings["child_tree_t61_heal_1"].SidecarEpoch;

            int restored = ParsekScenario.RestoreHydrationFailedRecordingsFromPendingTree(loadedTree);
            Assert.Equal(1, restored);

            Recording restoredRec = loadedTree.Recordings["child_tree_t61_heal_1"];
            Assert.True(restoredRec.FilesDirty);
            Assert.False(restoredRec.SidecarLoadFailed);
            Assert.Equal("Pending Child", restoredRec.VesselName);
            Assert.Equal(3, restoredRec.Points.Count);

            string dir = Path.Combine(Path.GetTempPath(),
                "parsek-t61-salvage-save-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            bool? previousMirrorOverride = RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting;
            RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting = false;
            try
            {
                string precPath = Path.Combine(dir, restoredRec.RecordingId + ".prec");
                string vesselPath = Path.Combine(dir, restoredRec.RecordingId + "_vessel.craft");
                string ghostPath = Path.Combine(dir, restoredRec.RecordingId + "_ghost.craft");

                Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                    restoredRec, precPath, vesselPath, ghostPath, incrementEpoch: true));

                Assert.False(restoredRec.FilesDirty);
                Assert.Equal(preSaveEpoch + 1, restoredRec.SidecarEpoch);
                Assert.True(File.Exists(precPath));
                Assert.False(File.Exists(vesselPath));
                Assert.False(File.Exists(ghostPath));

                TrajectorySidecarProbe probe;
                Assert.True(RecordingStore.TryProbeTrajectorySidecar(precPath, out probe));
                Assert.Equal(restoredRec.SidecarEpoch, probe.SidecarEpoch);
            }
            finally
            {
                RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting = previousMirrorOverride;
                try { Directory.Delete(dir, true); }
                catch { /* best-effort cleanup; test has already asserted */ }
            }
        }

        [Fact]
        public void RestoreHydrationFailedRecordingsFromPendingTree_MixedRestorabilitySubset_RestoresOnlyTheRecoverableRecordings()
        {
            // T61 mixed-case: one tree with three SidecarLoadFailed recordings; one full
            // restore, one snapshot-only restore, one unrecoverable because the pending
            // tree no longer has a matching id. Confirms the salvage loop counts exactly
            // the restorable subset and leaves unrecoverable entries in their disk state.
            var pendingTree = MakeTree("tree_t61_mixed", "Pending", 4);

            var pendingFull = pendingTree.Recordings["child_tree_t61_mixed_1"];
            pendingFull.VesselName = "Pending Full";
            pendingFull.Points.Add(new TrajectoryPoint { ut = 999 });

            var pendingSnapshot = pendingTree.Recordings["child_tree_t61_mixed_2"];
            pendingSnapshot.VesselSnapshot = BuildSnapshot("Pending Snapshot Vessel", 7601);
            pendingSnapshot.GhostVisualSnapshot = BuildSnapshot("Pending Snapshot Ghost", 7602);
            pendingSnapshot.GhostSnapshotMode = GhostSnapshotMode.Separate;

            pendingTree.Recordings.Remove("child_tree_t61_mixed_3");

            RecordingStore.StashPendingTree(pendingTree, PendingTreeState.Limbo);

            var loadedTree = MakeTree("tree_t61_mixed", "Disk", 4);

            var loadedFull = loadedTree.Recordings["child_tree_t61_mixed_1"];
            loadedFull.SidecarLoadFailed = true;
            loadedFull.SidecarLoadFailureReason = "trajectory-missing";

            var loadedSnapshot = loadedTree.Recordings["child_tree_t61_mixed_2"];
            loadedSnapshot.VesselSnapshot = BuildSnapshot("Disk Snapshot Vessel", 7701);
            loadedSnapshot.GhostSnapshotMode = GhostSnapshotMode.Separate;
            loadedSnapshot.SidecarLoadFailed = true;
            loadedSnapshot.SidecarLoadFailureReason = "snapshot-ghost-invalid";

            var loadedMissing = loadedTree.Recordings["child_tree_t61_mixed_3"];
            loadedMissing.SidecarLoadFailed = true;
            loadedMissing.SidecarLoadFailureReason = "snapshot-ghost-invalid";
            int missingPointCountBefore = loadedMissing.Points.Count;

            logLines.Clear();
            int restored = ParsekScenario.RestoreHydrationFailedRecordingsFromPendingTree(loadedTree);

            Assert.Equal(2, restored);

            Assert.Equal("Pending Full", loadedTree.Recordings["child_tree_t61_mixed_1"].VesselName);
            Assert.Equal(3, loadedTree.Recordings["child_tree_t61_mixed_1"].Points.Count);
            Assert.True(loadedTree.Recordings["child_tree_t61_mixed_1"].FilesDirty);
            Assert.False(loadedTree.Recordings["child_tree_t61_mixed_1"].SidecarLoadFailed);

            var restoredSnapshot = loadedTree.Recordings["child_tree_t61_mixed_2"];
            Assert.Equal(2, restoredSnapshot.Points.Count);
            Assert.Equal(120, restoredSnapshot.Points[0].ut);
            Assert.Equal(130, restoredSnapshot.Points[1].ut);
            Assert.Equal("Disk Snapshot Vessel", restoredSnapshot.VesselSnapshot.GetValue("name"));
            Assert.Equal("Pending Snapshot Ghost", restoredSnapshot.GhostVisualSnapshot.GetValue("name"));
            Assert.True(restoredSnapshot.FilesDirty);
            Assert.False(restoredSnapshot.SidecarLoadFailed);

            Assert.True(loadedMissing.SidecarLoadFailed);
            Assert.Equal("snapshot-ghost-invalid", loadedMissing.SidecarLoadFailureReason);
            Assert.False(loadedMissing.FilesDirty);
            Assert.Equal(missingPointCountBefore, loadedMissing.Points.Count);

            Assert.Equal("Disk #0", loadedTree.Recordings["root_tree_t61_mixed"].VesselName);
            Assert.False(loadedTree.Recordings["root_tree_t61_mixed"].FilesDirty);

            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]")
                && l.Contains("restored 2 hydration-failed recording")
                && l.Contains("(1 snapshot-only, 1 full)"));
        }

        // ============================================================
        // isRevert logic: removal of || isFlightToFlight clause
        // ============================================================

        [Fact]
        public void IsRevert_EpochDecreased_IsTrue()
        {
            // Pure logic test of the isRevert condition after fix (no FLIGHT→FLIGHT clause)
            bool isRevert = ComputeIsRevert(
                isVesselSwitch: false,
                savedEpoch: 5,
                liveEpoch: 6,
                totalSavedRecCount: 10,
                memoryRecordingsCount: 10);
            Assert.True(isRevert);
        }

        [Fact]
        public void IsRevert_CountDecreased_IsTrue()
        {
            bool isRevert = ComputeIsRevert(
                isVesselSwitch: false,
                savedEpoch: 5,
                liveEpoch: 5,
                totalSavedRecCount: 8,
                memoryRecordingsCount: 10);
            Assert.True(isRevert);
        }

        [Fact]
        public void IsRevert_QuickloadSameEpochSameCount_IsFalse()
        {
            // Quickload: both epoch and count match the memory state (since quicksave
            // captured both at the current moment).
            bool isRevert = ComputeIsRevert(
                isVesselSwitch: false,
                savedEpoch: 5,
                liveEpoch: 5,
                totalSavedRecCount: 10,
                memoryRecordingsCount: 10);
            Assert.False(isRevert);
        }

        [Fact]
        public void IsRevert_OrphanedLimboTree_FlightToFlight_IsTrue_Bug300()
        {
            // Bug #300: first-ever flight, no prior commits. Epoch and count both
            // zero on both sides. The orphaned Limbo tree (stashed from memory but
            // NOT found in the save file) is the revert signal.
            bool isRevert = ComputeIsRevert(
                isVesselSwitch: false,
                savedEpoch: 0,
                liveEpoch: 0,
                totalSavedRecCount: 0,
                memoryRecordingsCount: 0,
                isFlightToFlight: true,
                hasOrphanedLimboTree: true);
            Assert.True(isRevert);
        }

        [Fact]
        public void IsRevert_OrphanedLimboTree_NotFlightToFlight_IsFalse_Bug300()
        {
            // Safety: orphaned Limbo tree should only trigger revert detection in
            // FLIGHT→FLIGHT transitions, not on e.g. SPACECENTER→FLIGHT.
            bool isRevert = ComputeIsRevert(
                isVesselSwitch: false,
                savedEpoch: 0,
                liveEpoch: 0,
                totalSavedRecCount: 0,
                memoryRecordingsCount: 0,
                isFlightToFlight: false,
                hasOrphanedLimboTree: true);
            Assert.False(isRevert);
        }

        [Fact]
        public void IsRevert_LimboTreeRestoredFromSave_IsFalse_Bug300()
        {
            // Quickload (F5/F9): the save file contained the active tree, so
            // TryRestoreActiveTreeNode returned true → hasOrphanedLimboTree=false.
            // Should NOT be detected as a revert.
            bool isRevert = ComputeIsRevert(
                isVesselSwitch: false,
                savedEpoch: 0,
                liveEpoch: 0,
                totalSavedRecCount: 0,
                memoryRecordingsCount: 0,
                isFlightToFlight: true,
                hasOrphanedLimboTree: false);
            Assert.False(isRevert);
        }

        [Fact]
        public void IsRevert_OrphanedLimboTree_VesselSwitch_IsFalse_Bug300()
        {
            // Vessel switch suppresses revert even with an orphaned Limbo tree.
            bool isRevert = ComputeIsRevert(
                isVesselSwitch: true,
                savedEpoch: 0,
                liveEpoch: 0,
                totalSavedRecCount: 0,
                memoryRecordingsCount: 0,
                isFlightToFlight: true,
                hasOrphanedLimboTree: true);
            Assert.False(isRevert);
        }

        [Fact]
        public void IsRevert_VesselSwitch_IsFalseEvenIfEpochRegresses()
        {
            // Vessel switch flag suppresses isRevert regardless of other indicators
            // (defensive — in practice vessel switches preserve epoch/count anyway)
            bool isRevert = ComputeIsRevert(
                isVesselSwitch: true,
                savedEpoch: 4,
                liveEpoch: 6,
                totalSavedRecCount: 5,
                memoryRecordingsCount: 10);
            Assert.False(isRevert);
        }

        /// <summary>
        /// Mirrors the isRevert computation in ParsekScenario.OnLoad after bug #300.
        /// Kept here as a pure function so it can be unit-tested without needing a
        /// full ParsekScenario instance.
        /// </summary>
        private static bool ComputeIsRevert(
            bool isVesselSwitch, uint savedEpoch, uint liveEpoch,
            int totalSavedRecCount, int memoryRecordingsCount,
            bool isFlightToFlight = false, bool hasOrphanedLimboTree = false)
        {
            return !isVesselSwitch
                && (savedEpoch < liveEpoch
                    || totalSavedRecCount < memoryRecordingsCount
                    || (isFlightToFlight && hasOrphanedLimboTree));
        }

        /// <summary>
        /// Disposition the OnLoad Limbo-dispatch can take. Mirrors the four branches
        /// in ParsekScenario.cs after the bug #266 fix:
        /// <list type="bullet">
        ///   <item><c>Finalize</c> — real revert (terminal state set, merge dialog).</item>
        ///   <item><c>VesselSwitchRestore</c> — pre-transitioned tree (#266) reinstalled
        ///   via the new restore coroutine.</item>
        ///   <item><c>QuickloadRestore</c> — quickload / cold-start, name-match resume.</item>
        ///   <item><c>SafetyNetFinalize</c> — Limbo state but the OnLoad classifier still
        ///   says vessel switch (the stash didn't pre-transition because a guard bailed,
        ///   e.g. pendingTreeDockMerge). Falls back to pre-#266 finalize.</item>
        /// </list>
        /// </summary>
        internal enum LimboDispatchOutcome
        {
            Finalize = 0,
            VesselSwitchRestore = 1,
            QuickloadRestore = 2,
            SafetyNetFinalize = 3,
        }

        /// <summary>
        /// Mirrors the Limbo-dispatch decision in ParsekScenario.OnLoad after the
        /// bug #266 fix. Pure function — keeps the four-way decision tree unit-testable.
        /// </summary>
        internal static LimboDispatchOutcome ComputeLimboDispatch(
            bool isRevert, bool isVesselSwitch, PendingTreeState pendState)
        {
            if (isRevert) return LimboDispatchOutcome.Finalize;
            if (pendState == PendingTreeState.LimboVesselSwitch)
                return LimboDispatchOutcome.VesselSwitchRestore;
            if (isVesselSwitch) return LimboDispatchOutcome.SafetyNetFinalize;
            return LimboDispatchOutcome.QuickloadRestore;
        }

        [Fact]
        public void HasOrphanedLimboTree_LimboStashedButNotRestoredFromSave_IsTrue_Bug300()
        {
            // Revert-to-launch scenario: StashActiveTreeAsPendingLimbo put a tree
            // into Limbo, then TryRestoreActiveTreeNode scans the launch quicksave
            // which has NO active tree → returns false → orphaned.
            var tree = MakeTree("tree_revert", "Kerbal X", 3);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);

            // Launch quicksave: no RECORDING_TREE with isActive=True
            var launchSave = new ConfigNode("PARSEK_SCENARIO");
            bool activeTreeRestoredFromSave = ParsekScenario.TryRestoreActiveTreeNode(launchSave);

            Assert.False(activeTreeRestoredFromSave);
            bool hasOrphanedLimboTree = RecordingStore.HasPendingTree
                && RecordingStore.PendingTreeStateValue == PendingTreeState.Limbo
                && !activeTreeRestoredFromSave;
            Assert.True(hasOrphanedLimboTree);
        }

        [Fact]
        public void HasOrphanedLimboTree_LimboOverwrittenByRestore_IsFalse_Bug300()
        {
            // Quickload (F5/F9) scenario: StashActiveTreeAsPendingLimbo put a tree
            // into Limbo, then TryRestoreActiveTreeNode finds the save-file version
            // (F5 save has isActive=True) → returns true → not orphaned.
            var tree = MakeTree("tree_ql", "Kerbal X", 3);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);

            // F5 save: has RECORDING_TREE with isActive=True
            var f5Save = new ConfigNode("PARSEK_SCENARIO");
            var activeNode = f5Save.AddNode("RECORDING_TREE");
            var saveTree = MakeTree("tree_ql", "Kerbal X (save)", 3);
            saveTree.Save(activeNode);
            activeNode.AddValue("isActive", "True");

            bool activeTreeRestoredFromSave = ParsekScenario.TryRestoreActiveTreeNode(f5Save);

            Assert.True(activeTreeRestoredFromSave);
            bool hasOrphanedLimboTree = RecordingStore.HasPendingTree
                && RecordingStore.PendingTreeStateValue == PendingTreeState.Limbo
                && !activeTreeRestoredFromSave;
            Assert.False(hasOrphanedLimboTree);
        }

        [Fact]
        public void HasOrphanedLimboTree_FinalizedState_IsFalse_Bug300()
        {
            // Finalized trees are committed scene-exit trees, not Limbo. Even when
            // TryRestoreActiveTreeNode returns false, the Limbo state check rejects.
            var tree = MakeTree("tree_fin", "Committed Flight", 2);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);

            var emptySave = new ConfigNode("PARSEK_SCENARIO");
            bool activeTreeRestoredFromSave = ParsekScenario.TryRestoreActiveTreeNode(emptySave);

            Assert.False(activeTreeRestoredFromSave);
            bool hasOrphanedLimboTree = RecordingStore.HasPendingTree
                && RecordingStore.PendingTreeStateValue == PendingTreeState.Limbo
                && !activeTreeRestoredFromSave;
            Assert.False(hasOrphanedLimboTree);
        }

        [Fact]
        public void LimboDispatch_OrphanedLimboTree_RoutesToFinalize_Bug300()
        {
            // End-to-end dispatch: orphaned Limbo tree (revert) → isRevert=true → Finalize
            bool isRevert = ComputeIsRevert(
                isVesselSwitch: false,
                savedEpoch: 0,
                liveEpoch: 0,
                totalSavedRecCount: 0,
                memoryRecordingsCount: 0,
                isFlightToFlight: true,
                hasOrphanedLimboTree: true);
            Assert.True(isRevert);

            var outcome = ComputeLimboDispatch(isRevert, isVesselSwitch: false,
                PendingTreeState.Limbo);
            Assert.Equal(LimboDispatchOutcome.Finalize, outcome);
        }

        [Fact]
        public void LimboDispatch_Revert_Finalizes()
        {
            // Real revert wipes the in-progress mission regardless of state.
            Assert.Equal(LimboDispatchOutcome.Finalize,
                ComputeLimboDispatch(isRevert: true, isVesselSwitch: false,
                    pendState: PendingTreeState.Limbo));
        }

        [Fact]
        public void LimboDispatch_Revert_OverridesLimboVesselSwitch()
        {
            // Even if the stash pre-transitioned for a vessel switch, a real revert
            // (epoch/count regression) takes priority. The pre-#266 behavior is
            // preserved for the revert path.
            Assert.Equal(LimboDispatchOutcome.Finalize,
                ComputeLimboDispatch(isRevert: true, isVesselSwitch: true,
                    pendState: PendingTreeState.LimboVesselSwitch));
        }

        [Fact]
        public void LimboDispatch_VesselSwitch_PreTransitioned_Restores_Bug266()
        {
            // Bug #266: tree was pre-transitioned at stash time
            // (StashActiveTreeForVesselSwitch). OnLoad routes to the vessel-switch
            // restore coroutine instead of finalizing. The mission is preserved
            // across the FLIGHT→FLIGHT scene reload.
            Assert.Equal(LimboDispatchOutcome.VesselSwitchRestore,
                ComputeLimboDispatch(isRevert: false, isVesselSwitch: true,
                    pendState: PendingTreeState.LimboVesselSwitch));
        }

        [Fact]
        public void LimboDispatch_VesselSwitch_NotPreTransitioned_FinalizesViaSafetyNet_Bug266()
        {
            // Safety net: vessel-switch detected at OnLoad time, but the stash did
            // NOT pre-transition (the in-flight pre-transition guard bailed because
            // pendingTreeDockMerge / pendingSplit was active). Fall back to pre-#266
            // finalize behavior — better to lose the tree than to leak a half-
            // transitioned state into the restore path.
            Assert.Equal(LimboDispatchOutcome.SafetyNetFinalize,
                ComputeLimboDispatch(isRevert: false, isVesselSwitch: true,
                    pendState: PendingTreeState.Limbo));
        }

        [Fact]
        public void LimboDispatch_Quickload_DefersToQuickloadRestore()
        {
            // Quickload / cold-start resume: tree should be restored-and-resumed,
            // not finalized.
            Assert.Equal(LimboDispatchOutcome.QuickloadRestore,
                ComputeLimboDispatch(isRevert: false, isVesselSwitch: false,
                    pendState: PendingTreeState.Limbo));
        }

        [Fact]
        public void LimboDispatch_LimboVesselSwitch_WithoutSwitchFlag_StillRestores_Bug266()
        {
            // Cold-start path: the .sfs holds a LimboVesselSwitch tree (player F5'd
            // in outsider state, then quit, then resumed). vesselSwitchPending is
            // false because no live switch happened in this session, but the saved
            // state still needs the vessel-switch restore.
            Assert.Equal(LimboDispatchOutcome.VesselSwitchRestore,
                ComputeLimboDispatch(isRevert: false, isVesselSwitch: false,
                    pendState: PendingTreeState.LimboVesselSwitch));
        }

        // ============================================================
        // Bug #266: TryRestoreActiveTreeNode picks state based on
        // whether the saved tree has an active recording.
        // ============================================================

        [Fact]
        public void TryRestoreActiveTreeNode_TreeWithActiveRecording_StashesAsLimbo_Bug266()
        {
            // Tree has a populated ActiveRecordingId — quickload-resume path.
            var scenarioNode = new ConfigNode("PARSEK_SCENARIO");
            var activeNode = scenarioNode.AddNode("RECORDING_TREE");
            var tree = MakeTree("tree_alive", "Live Recording", 2);
            // MakeTree sets ActiveRecordingId = "root_tree_alive" by default.
            tree.Save(activeNode);
            activeNode.AddValue("isActive", "True");

            ParsekScenario.TryRestoreActiveTreeNode(scenarioNode);

            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);
        }

        [Fact]
        public void TryRestoreActiveTreeNode_TreeWithoutActiveRecording_StashesAsLimboVesselSwitch_Bug266()
        {
            // Outsider state: tree was alive at OnSave time but had no active
            // recording (player switched to a vessel with no recording context).
            // Bug #266: stash as LimboVesselSwitch so the restore coroutine
            // doesn't try to name-match a non-existent active vessel.
            var scenarioNode = new ConfigNode("PARSEK_SCENARIO");
            var activeNode = scenarioNode.AddNode("RECORDING_TREE");
            var tree = MakeTree("tree_outsider", "Outsider Hop", 2);
            tree.ActiveRecordingId = null; // outsider state
            tree.Save(activeNode);
            activeNode.AddValue("isActive", "True");

            ParsekScenario.TryRestoreActiveTreeNode(scenarioNode);

            Assert.Equal(PendingTreeState.LimboVesselSwitch, RecordingStore.PendingTreeStateValue);
            Assert.Null(RecordingStore.PendingTree.ActiveRecordingId);
            Assert.Contains(logLines, l => l.Contains("LimboVesselSwitch"));
        }

        // ============================================================
        // Bug #266: pre-transition logic — calls the real production
        // helper ParsekFlight.ApplyPreTransitionForVesselSwitch so the
        // tests stay locked to the actual implementation.
        // ============================================================

        [Fact]
        public void PreTransition_RecorderPidPreferred_MovesActiveToBackgroundMap_Bug266()
        {
            var tree = MakeTree("tree_t", "Launch", 2);
            tree.Recordings["root_tree_t"].VesselPersistentId = 999; // stale
            // recorder PID is the live source of truth
            uint recorderPid = 12345;

            bool moved = ParsekFlight.ApplyPreTransitionForVesselSwitch(tree, recorderPid);

            Assert.True(moved);
            Assert.Null(tree.ActiveRecordingId);
            Assert.True(tree.BackgroundMap.ContainsKey(12345));
            Assert.Equal("root_tree_t", tree.BackgroundMap[12345]);
            // Stale tree-rec PID is NOT used since recorder PID was live
            Assert.False(tree.BackgroundMap.ContainsKey(999));
        }

        [Fact]
        public void PreTransition_FallbackToTreeRecPid_WhenRecorderPidZero_Bug266()
        {
            var tree = MakeTree("tree_t", "Launch", 2);
            tree.Recordings["root_tree_t"].VesselPersistentId = 4242;

            // Recorder PID = 0 (e.g. recorder was already torn down before stash)
            bool moved = ParsekFlight.ApplyPreTransitionForVesselSwitch(tree, 0);

            Assert.True(moved);
            Assert.Null(tree.ActiveRecordingId);
            Assert.True(tree.BackgroundMap.ContainsKey(4242));
            Assert.Equal("root_tree_t", tree.BackgroundMap[4242]);
        }

        [Fact]
        public void PreTransition_NullActiveRec_NullsAndDoesNotMove_Bug266()
        {
            var tree = MakeTree("tree_t", "Launch", 2);
            tree.ActiveRecordingId = null;

            bool moved = ParsekFlight.ApplyPreTransitionForVesselSwitch(tree, 12345);

            Assert.False(moved);
            Assert.Null(tree.ActiveRecordingId);
            Assert.Empty(tree.BackgroundMap);
        }

        [Fact]
        public void PreTransition_BothPidsZero_NullsAndDoesNotMove_Bug266()
        {
            var tree = MakeTree("tree_t", "Launch", 2);
            tree.Recordings["root_tree_t"].VesselPersistentId = 0;

            // No PID source available (degenerate case — recorder gone, tree
            // recording was never populated). Tree is still nulled out, but
            // there's no entry in BackgroundMap. Restore will treat the new
            // active vessel as outsider regardless of who it is.
            bool moved = ParsekFlight.ApplyPreTransitionForVesselSwitch(tree, 0);

            Assert.False(moved);
            Assert.Null(tree.ActiveRecordingId);
            Assert.Empty(tree.BackgroundMap);
        }

        [Fact]
        public void PreTransition_PreservesExistingBackgroundMapEntries_Bug266()
        {
            // Round-trip case: tree already has background entries from prior
            // hops. The new switch should add a new entry, not clear existing ones.
            var tree = MakeTree("tree_t", "Multi-Hop", 3);
            tree.BackgroundMap[5555] = "child_tree_t_1";
            tree.BackgroundMap[6666] = "child_tree_t_2";

            bool moved = ParsekFlight.ApplyPreTransitionForVesselSwitch(tree, 7777);

            Assert.True(moved);
            Assert.Equal(3, tree.BackgroundMap.Count);
            Assert.Equal("child_tree_t_1", tree.BackgroundMap[5555]);
            Assert.Equal("child_tree_t_2", tree.BackgroundMap[6666]);
            Assert.Equal("root_tree_t", tree.BackgroundMap[7777]);
        }

        // ============================================================
        // Test helpers
        // ============================================================

        private static RecordingTree MakeTree(string id, string name, int recordingCount)
        {
            var tree = new RecordingTree
            {
                Id = id,
                TreeName = name,
                RootRecordingId = "root_" + id,
                ActiveRecordingId = "root_" + id,
            };
            for (int i = 0; i < recordingCount; i++)
            {
                string recId = i == 0 ? "root_" + id : $"child_{id}_{i}";
                var rec = new Recording
                {
                    RecordingId = recId,
                    VesselName = $"{name} #{i}",
                    TreeId = id,
                    ExplicitStartUT = 100 + i * 10,
                    ExplicitEndUT = 110 + i * 10,
                };
                rec.Points.Add(new TrajectoryPoint { ut = rec.ExplicitStartUT });
                rec.Points.Add(new TrajectoryPoint { ut = rec.ExplicitEndUT });
                tree.Recordings[recId] = rec;
            }
            return tree;
        }

        private static ConfigNode BuildSnapshot(string vesselName, uint pid)
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("name", vesselName);
            snapshot.AddValue("persistentId", pid.ToString());
            return snapshot;
        }

        private static void InvokeLoadCrewAndGroupState(
            ConfigNode node, bool initialLoadDoneValue)
        {
            FieldInfo initialLoadDoneField = typeof(ParsekScenario).GetField(
                "initialLoadDone", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo loadMethod = typeof(ParsekScenario).GetMethod(
                "LoadCrewAndGroupState", BindingFlags.Static | BindingFlags.NonPublic);

            Assert.NotNull(initialLoadDoneField);
            Assert.NotNull(loadMethod);

            bool previousInitialLoadDone = (bool)initialLoadDoneField.GetValue(null);
            try
            {
                initialLoadDoneField.SetValue(null, initialLoadDoneValue);
                loadMethod.Invoke(null, new object[] { node });
            }
            finally
            {
                initialLoadDoneField.SetValue(null, previousInitialLoadDone);
            }
        }

        private static void SetPrivateStaticField(Type type, string fieldName, object value)
        {
            FieldInfo field = type.GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field.SetValue(null, value);
        }

        private static object GetPrivateStaticField(Type type, string fieldName)
        {
            FieldInfo field = type.GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return field.GetValue(null);
        }

        private static void InvokePrepareQuickloadResumeStateIfNeeded(FlightRecorder recorder)
        {
            MethodInfo method = typeof(FlightRecorder).GetMethod(
                "PrepareQuickloadResumeStateIfNeeded",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(method);
            method.Invoke(recorder, null);
        }
    }
}
