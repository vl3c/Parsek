using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for #431 — events captured during a live recording share the recording's
    /// commit/discard fate. Covers Emit tagging, PurgeEventsForRecordings, the
    /// resource-coalesce tag gate, contract snapshot purge, round-trip serialization,
    /// and the flush-then-discard milestone path.
    /// All tests inject a fixed-id tag resolver via <see cref="GameStateRecorder.TagResolverForTesting"/>
    /// so they don't need a live <see cref="ParsekFlight"/> singleton.
    /// </summary>
    [Collection("Sequential")]
    public class DiscardFateTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly GameScenes originalScene;

        public DiscardFateTests()
        {
            // Capture log output for outcome + log assertions.
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            // Reset every store that a test in this file can touch.
            GameStateStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();

            originalScene = HighLogic.LoadedScene;
        }

        public void Dispose()
        {
            HighLogic.LoadedScene = originalScene;
            GameStateRecorder.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // --- Helpers ---

        private static GameStateEvent MakeEvent(GameStateEventType type, string key, double ut,
            string recordingId = "", double valueBefore = 0, double valueAfter = 0)
        {
            return new GameStateEvent
            {
                ut = ut,
                eventType = type,
                key = key,
                detail = "",
                valueBefore = valueBefore,
                valueAfter = valueAfter,
                recordingId = recordingId ?? ""
            };
        }

        private static Recording MakeRecording(string id, string treeId = "tree001")
        {
            return new Recording
            {
                RecordingId = id,
                TreeId = treeId,
                VesselName = "V-" + id,
                VesselPersistentId = 0
            };
        }

        private static RecordingTree MakeTree(string treeId, params string[] recordingIds)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "TestTree-" + treeId,
                RootRecordingId = recordingIds.Length > 0 ? recordingIds[0] : null,
                ActiveRecordingId = recordingIds.Length > 0 ? recordingIds[0] : null
            };
            for (int i = 0; i < recordingIds.Length; i++)
                tree.Recordings[recordingIds[i]] = MakeRecording(recordingIds[i], treeId);
            return tree;
        }

        // --- #1: commit preserves tagged events (invariant test) ---

        [Fact]
        public void CapturedEvent_OnCommit_EventSurvivesAndRetainsTag()
        {
            GameStateRecorder.TagResolverForTesting = () => "rec-A";
            HighLogic.LoadedScene = GameScenes.FLIGHT;

            // Seed a contract snapshot so we can check it also survives.
            var contractNode = new ConfigNode("CONTRACT");
            GameStateStore.AddContractSnapshot("guid-A", contractNode);

            GameStateRecorder.Emit(
                MakeEvent(GameStateEventType.ContractAccepted, "guid-A", 100.0),
                "test");

            var tree = MakeTree("tree001", "rec-A");
            RecordingStore.StashPendingTree(tree);

            // CommitPendingTree moves the tree into committed state — our tagged event
            // stays in GameStateStore because commit never calls PurgeEventsForRecordings.
            RecordingStore.CommitPendingTree();

            var match = GameStateStore.Events
                .FirstOrDefault(e => e.key == "guid-A" && e.eventType == GameStateEventType.ContractAccepted);
            Assert.Equal("guid-A", match.key);
            Assert.Equal("rec-A", match.recordingId);
            Assert.NotNull(GameStateStore.GetContractSnapshot("guid-A"));
        }

        // --- #2: discard purges tagged events + snapshots ---

        [Fact]
        public void CapturedEvent_OnDiscard_EventAndSnapshotPurged()
        {
            GameStateRecorder.TagResolverForTesting = () => "rec-A";
            HighLogic.LoadedScene = GameScenes.FLIGHT;

            var contractNode = new ConfigNode("CONTRACT");
            GameStateStore.AddContractSnapshot("guid-A", contractNode);

            GameStateRecorder.Emit(
                MakeEvent(GameStateEventType.ContractAccepted, "guid-A", 100.0),
                "test");

            var tree = MakeTree("tree001", "rec-A");
            RecordingStore.StashPendingTree(tree);

            RecordingStore.DiscardPendingTree();

            Assert.DoesNotContain(GameStateStore.Events, e => e.key == "guid-A");
            Assert.Null(GameStateStore.GetContractSnapshot("guid-A"));
            Assert.Contains(logLines, l =>
                l.Contains("[GameStateStore]") && l.Contains("PurgeEventsForRecordings") &&
                l.Contains("DiscardPendingTree"));
        }

        // --- #3: untagged KSC event survives an unrelated discard ---

        [Fact]
        public void KscEvent_NoLiveRecording_SurvivesDiscardOfUnrelatedTree()
        {
            // Resolver returns empty — simulating a career-scene event.
            GameStateRecorder.TagResolverForTesting = () => "";
            HighLogic.LoadedScene = GameScenes.SPACECENTER;

            GameStateRecorder.Emit(
                MakeEvent(GameStateEventType.ContractAccepted, "guid-KSC", 50.0),
                "ksc");

            // Tree contains ids that don't match the KSC event's (empty) tag.
            var tree = MakeTree("tree001", "rec-A", "rec-B");
            RecordingStore.StashPendingTree(tree);

            RecordingStore.DiscardPendingTree();

            Assert.Contains(GameStateStore.Events, e => e.key == "guid-KSC");
            var kscMatch = GameStateStore.Events.First(e => e.key == "guid-KSC");
            Assert.Equal("", kscMatch.recordingId ?? "");
        }

        // --- #4: EVA-split tree, both recordings' events purged on whole-tree discard ---

        [Fact]
        public void EvaSplitTree_BothRecordings_EventsPurgedOnDiscard()
        {
            HighLogic.LoadedScene = GameScenes.FLIGHT;

            // Emit one event per recording by swapping the resolver between calls.
            GameStateRecorder.TagResolverForTesting = () => "rec-parent";
            GameStateRecorder.Emit(
                MakeEvent(GameStateEventType.TechResearched, "node-1", 100.0), "parent");

            GameStateRecorder.TagResolverForTesting = () => "rec-eva";
            GameStateRecorder.Emit(
                MakeEvent(GameStateEventType.TechResearched, "node-2", 200.0), "eva");

            var tree = MakeTree("tree001", "rec-parent", "rec-eva");
            RecordingStore.StashPendingTree(tree);

            RecordingStore.DiscardPendingTree();

            Assert.Empty(GameStateStore.Events);
            Assert.Contains(logLines, l =>
                l.Contains("[GameStateStore]") && l.Contains("PurgeEventsForRecordings") &&
                l.Contains("live=2"));
        }

        // --- #5: pre-#431 untagged (empty recordingId) events never over-purged ---

        [Fact]
        public void PreFixUntaggedEvent_TreeHasNonEmptyIds_EventSurvives()
        {
            // Directly seed an event with no tag (simulating a loaded pre-#431 save).
            GameStateStore.AddEvent(MakeEvent(GameStateEventType.TechResearched, "legacy-tech", 10.0));

            var tree = MakeTree("tree001", "rec-A", "rec-B");
            RecordingStore.StashPendingTree(tree);

            RecordingStore.DiscardPendingTree();

            Assert.Single(GameStateStore.Events);
            var survivor = GameStateStore.Events[0];
            Assert.Equal("legacy-tech", survivor.key);
            Assert.True(string.IsNullOrEmpty(survivor.recordingId));
        }

        // --- #6: recordingId round-trips through SerializeInto / DeserializeFrom ---

        [Fact]
        public void RecordingId_RoundTripsThroughSerialization()
        {
            var evt = MakeEvent(GameStateEventType.ContractAccepted, "guid-rt", 123.456,
                recordingId: "rec-rt");
            evt.valueBefore = 1000.5;
            evt.valueAfter = 2000.75;
            evt.detail = "title=Round Trip";

            var node = new ConfigNode("GAME_STATE_EVENT");
            evt.SerializeInto(node);

            Assert.Equal("rec-rt", node.GetValue("recordingId"));

            var loaded = GameStateEvent.DeserializeFrom(node);
            Assert.Equal("rec-rt", loaded.recordingId);
            Assert.Equal("guid-rt", loaded.key);
            Assert.Equal(GameStateEventType.ContractAccepted, loaded.eventType);
            Assert.Equal(123.456, loaded.ut, 5);
            Assert.Equal(1000.5, loaded.valueBefore, 5);
            Assert.Equal(2000.75, loaded.valueAfter, 5);
            Assert.Equal("title=Round Trip", loaded.detail);
        }

        // --- #7: contract snapshot heuristic — accept at KSC, complete in discarded flight ---

        [Fact]
        public void ContractAcceptedAtKsc_CompletedInDiscardedMission_SnapshotStays()
        {
            // Accept at KSC (untagged, before the recording).
            GameStateRecorder.TagResolverForTesting = () => "";
            HighLogic.LoadedScene = GameScenes.SPACECENTER;
            var contractNode = new ConfigNode("CONTRACT");
            GameStateStore.AddContractSnapshot("guid-cross", contractNode);
            GameStateRecorder.Emit(
                MakeEvent(GameStateEventType.ContractAccepted, "guid-cross", 10.0), "ksc");

            // Complete during the discarded mission (tagged).
            GameStateRecorder.TagResolverForTesting = () => "rec-A";
            HighLogic.LoadedScene = GameScenes.FLIGHT;
            GameStateRecorder.Emit(
                MakeEvent(GameStateEventType.ContractCompleted, "guid-cross", 200.0), "flight");

            var tree = MakeTree("tree001", "rec-A");
            RecordingStore.StashPendingTree(tree);

            RecordingStore.DiscardPendingTree();

            // Accept survives — it's untagged. Completion is purged.
            Assert.Contains(GameStateStore.Events, e =>
                e.eventType == GameStateEventType.ContractAccepted && e.key == "guid-cross");
            Assert.DoesNotContain(GameStateStore.Events, e =>
                e.eventType == GameStateEventType.ContractCompleted && e.key == "guid-cross");
            // Snapshot belongs to the retained accept event — stays.
            Assert.NotNull(GameStateStore.GetContractSnapshot("guid-cross"));
        }

        [Fact]
        public void ContractAcceptedAndCompletedInDiscardedMission_SnapshotAndEventsPurged()
        {
            GameStateRecorder.TagResolverForTesting = () => "rec-A";
            HighLogic.LoadedScene = GameScenes.FLIGHT;

            var contractNode = new ConfigNode("CONTRACT");
            GameStateStore.AddContractSnapshot("guid-mission", contractNode);

            GameStateRecorder.Emit(
                MakeEvent(GameStateEventType.ContractAccepted, "guid-mission", 100.0), "flight");
            GameStateRecorder.Emit(
                MakeEvent(GameStateEventType.ContractCompleted, "guid-mission", 200.0), "flight");

            var tree = MakeTree("tree001", "rec-A");
            RecordingStore.StashPendingTree(tree);

            RecordingStore.DiscardPendingTree();

            Assert.DoesNotContain(GameStateStore.Events, e => e.key == "guid-mission");
            Assert.Null(GameStateStore.GetContractSnapshot("guid-mission"));
        }

        // --- #8: LimboVesselSwitch resolver fallback ---

        [Fact(Skip = "LimboVesselSwitch fallback requires RecordingStore.PendingTree + state setup " +
            "that StashPendingTree doesn't expose — state is always reset to Finalized. " +
            "Exercised indirectly via production-only test path in QuickloadResumeTests; revisit " +
            "if a test-only setter for pendingTreeState gets added.")]
        public void LimboVesselSwitch_EmitsTaggedWithPendingTreeActiveId()
        {
        }

        // --- #9: Drift warnings ---
        // Emit's drift-warn branches (GameStateRecorder.cs:69-74):
        //   (a) !inFlight && !midSwitch && !string.IsNullOrEmpty(tag) — stale tag outside flight.
        //   (b) inFlight && string.IsNullOrEmpty(tag) && HasLiveRecorder()
        //       — in-flight empty tag with live recorder (needs ParsekFlight.Instance, not testable
        //         from xUnit; covered by branch (a) plus the no-warn negative case below).

        [Fact]
        public void DriftWarn_OutsideFlight_ResolverReturnsTag_LogsWarn()
        {
            // Outside flight, no pending switch, resolver returns a tag (stale fallback).
            // Emit should log the "tagged outside flight" drift-warn.
            GameStateRecorder.TagResolverForTesting = () => "rec-stale";
            HighLogic.LoadedScene = GameScenes.SPACECENTER;

            GameStateRecorder.Emit(
                MakeEvent(GameStateEventType.TechResearched, "node-manual", 100.0),
                "drift-test");

            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("[GameStateRecorder]") &&
                l.Contains("Emit drift") && l.Contains("rec-stale"));
        }

        [Fact]
        public void DriftWarn_OutsideFlightWithEmptyTag_NoWarn()
        {
            GameStateRecorder.TagResolverForTesting = () => "";
            HighLogic.LoadedScene = GameScenes.SPACECENTER;

            GameStateRecorder.Emit(
                MakeEvent(GameStateEventType.TechResearched, "node-clean", 100.0), "clean");

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[WARN]") && l.Contains("[GameStateRecorder]") &&
                l.Contains("Emit drift"));
        }

        // --- #10: UpdateEventDetail preserves recordingId ---

        [Fact]
        public void UpdateEventDetail_PreservesRecordingId()
        {
            GameStateRecorder.TagResolverForTesting = () => "rec-detail";
            HighLogic.LoadedScene = GameScenes.FLIGHT;

            GameStateRecorder.Emit(
                MakeEvent(GameStateEventType.MilestoneAchieved, "first-orbit", 500.0), "test");

            var original = GameStateStore.Events.First();
            bool updated = GameStateStore.UpdateEventDetail(
                original.ut, original.eventType, original.key, original.epoch,
                "reward=25000");

            Assert.True(updated);
            var after = GameStateStore.Events.First();
            Assert.Equal("rec-detail", after.recordingId);
            Assert.Equal("reward=25000", after.detail);
        }

        // --- #11: Reset hygiene — ResetForTesting hooks work independently ---

        [Fact]
        public void ResetHygiene_EachResetWorksIndependently()
        {
            GameStateRecorder.TagResolverForTesting = () => "rec-reset";
            HighLogic.LoadedScene = GameScenes.FLIGHT;

            GameStateRecorder.Emit(
                MakeEvent(GameStateEventType.TechResearched, "node", 10.0), "test");
            RecordingStore.StashPendingTree(MakeTree("tree-reset", "rec-reset"));
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "m1",
                Epoch = 0,
                Events = new List<GameStateEvent>
                {
                    MakeEvent(GameStateEventType.TechResearched, "node", 10.0, "rec-reset")
                },
                Committed = true
            });

            Assert.NotEmpty(GameStateStore.Events);
            Assert.True(RecordingStore.HasPendingTree);
            Assert.Equal(1, MilestoneStore.MilestoneCount);
            Assert.NotNull(GameStateRecorder.TagResolverForTesting);

            // Each reset touches only its own store.
            GameStateStore.ResetForTesting();
            Assert.Empty(GameStateStore.Events);
            Assert.True(RecordingStore.HasPendingTree); // still there
            Assert.Equal(1, MilestoneStore.MilestoneCount); // still there

            RecordingStore.ResetForTesting();
            Assert.False(RecordingStore.HasPendingTree);
            Assert.Equal(1, MilestoneStore.MilestoneCount); // still there

            MilestoneStore.ResetForTesting();
            Assert.Equal(0, MilestoneStore.MilestoneCount);

            GameStateRecorder.ResetForTesting();
            Assert.Null(GameStateRecorder.TagResolverForTesting);
        }

        // --- #12: F5-then-discard — events moved to milestone, then purged on discard ---

        [Fact]
        public void FlushThenDiscard_EventsAndMilestonePurged()
        {
            GameStateRecorder.TagResolverForTesting = () => "rec-flush";
            HighLogic.LoadedScene = GameScenes.FLIGHT;

            var contractNode = new ConfigNode("CONTRACT");
            GameStateStore.AddContractSnapshot("guid-flush", contractNode);

            GameStateRecorder.Emit(
                MakeEvent(GameStateEventType.ContractAccepted, "guid-flush", 100.0), "flight");

            // Simulate F5 mid-flight — flush pulls the event into a committed milestone,
            // PruneProcessedEvents then removes it from the live events list.
            var milestone = MilestoneStore.FlushPendingEvents(150.0);
            Assert.NotNull(milestone);
            GameStateStore.PruneProcessedEvents();

            Assert.DoesNotContain(GameStateStore.Events, e => e.key == "guid-flush");
            Assert.Equal(1, MilestoneStore.MilestoneCount);
            Assert.Contains(MilestoneStore.Milestones[0].Events,
                e => e.key == "guid-flush" && e.recordingId == "rec-flush");

            var tree = MakeTree("tree001", "rec-flush");
            RecordingStore.StashPendingTree(tree);

            RecordingStore.DiscardPendingTree();

            // Event gone from both live and milestone, milestone empty → dropped,
            // snapshot removed.
            Assert.DoesNotContain(GameStateStore.Events, e => e.key == "guid-flush");
            Assert.Equal(0, MilestoneStore.MilestoneCount);
            Assert.Null(GameStateStore.GetContractSnapshot("guid-flush"));
            Assert.Contains(logLines, l =>
                l.Contains("[MilestoneStore]") && l.Contains("PurgeTaggedEvents") &&
                l.Contains("milestones dropped"));
        }

        // --- #13: Resource coalesce tag gate ---

        [Fact]
        public void ResourceCoalesce_DifferentTags_NoMerge()
        {
            // Two FundsChanged events within the 0.1s epsilon window, different tags.
            var untagged = MakeEvent(GameStateEventType.FundsChanged, "", 100.0,
                valueBefore: 1000, valueAfter: 2000);
            var tagged = MakeEvent(GameStateEventType.FundsChanged, "", 100.05,
                recordingId: "rec-B", valueBefore: 2000, valueAfter: 3000);

            GameStateStore.AddEvent(untagged);
            GameStateStore.AddEvent(tagged);

            // Both events present — tag-equality gate prevented coalesce.
            var fundsEvents = GameStateStore.Events
                .Where(e => e.eventType == GameStateEventType.FundsChanged).ToList();
            Assert.Equal(2, fundsEvents.Count);
            Assert.Contains(fundsEvents, e => string.IsNullOrEmpty(e.recordingId));
            Assert.Contains(fundsEvents, e => e.recordingId == "rec-B");
        }

        [Fact]
        public void ResourceCoalesce_SameTag_Merges()
        {
            var first = MakeEvent(GameStateEventType.FundsChanged, "", 100.0,
                recordingId: "rec-same", valueBefore: 1000, valueAfter: 1500);
            var second = MakeEvent(GameStateEventType.FundsChanged, "", 100.05,
                recordingId: "rec-same", valueBefore: 1500, valueAfter: 2000);

            GameStateStore.AddEvent(first);
            GameStateStore.AddEvent(second);

            var fundsEvents = GameStateStore.Events
                .Where(e => e.eventType == GameStateEventType.FundsChanged).ToList();
            Assert.Single(fundsEvents);
            // Merged slot's valueAfter updated to the incoming event's value.
            Assert.Equal(2000.0,
                Convert.ToDouble(fundsEvents[0].valueAfter, CultureInfo.InvariantCulture));
        }

        // --- #14: Mixed-tag milestone purge ---

        [Fact]
        public void MixedTagMilestone_PartialDiscard_OnlyMatchingTagRemoved()
        {
            var milestone = new Milestone
            {
                MilestoneId = "m-mixed",
                StartUT = 0,
                EndUT = 500,
                RecordingId = "",
                Epoch = 0,
                Committed = true,
                Events = new List<GameStateEvent>
                {
                    MakeEvent(GameStateEventType.TechResearched, "tech-A", 100.0, "rec-A"),
                    MakeEvent(GameStateEventType.TechResearched, "tech-B", 200.0, "rec-B"),
                    MakeEvent(GameStateEventType.TechResearched, "tech-untagged", 300.0)
                }
            };
            MilestoneStore.AddMilestoneForTesting(milestone);

            var tree = MakeTree("tree-mixed", "rec-A");
            RecordingStore.StashPendingTree(tree);

            RecordingStore.DiscardPendingTree();

            // Milestone not dropped (still has rec-B + untagged event).
            Assert.Equal(1, MilestoneStore.MilestoneCount);
            var remaining = MilestoneStore.Milestones[0].Events;
            Assert.Equal(2, remaining.Count);
            Assert.DoesNotContain(remaining, e => e.key == "tech-A");
            Assert.Contains(remaining, e => e.key == "tech-B");
            Assert.Contains(remaining, e => e.key == "tech-untagged");
        }
    }
}
