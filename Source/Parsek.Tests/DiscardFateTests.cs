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

            var evt = MakeEvent(GameStateEventType.ContractAccepted, "guid-A", 100.0);
            GameStateRecorder.Emit(ref evt, "test");

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

            var evt = MakeEvent(GameStateEventType.ContractAccepted, "guid-A", 100.0);
            GameStateRecorder.Emit(ref evt, "test");

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

            var evt = MakeEvent(GameStateEventType.ContractAccepted, "guid-KSC", 50.0);
            GameStateRecorder.Emit(ref evt, "ksc");

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
            var evtParent = MakeEvent(GameStateEventType.TechResearched, "node-1", 100.0);
            GameStateRecorder.Emit(ref evtParent, "parent");

            GameStateRecorder.TagResolverForTesting = () => "rec-eva";
            var evtEva = MakeEvent(GameStateEventType.TechResearched, "node-2", 200.0);
            GameStateRecorder.Emit(ref evtEva, "eva");

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
            var legacyEvt = MakeEvent(GameStateEventType.TechResearched, "legacy-tech", 10.0);
            GameStateStore.AddEvent(ref legacyEvt);

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
            var acceptEvt = MakeEvent(GameStateEventType.ContractAccepted, "guid-cross", 10.0);
            GameStateRecorder.Emit(ref acceptEvt, "ksc");

            // Complete during the discarded mission (tagged).
            GameStateRecorder.TagResolverForTesting = () => "rec-A";
            HighLogic.LoadedScene = GameScenes.FLIGHT;
            var completeEvt = MakeEvent(GameStateEventType.ContractCompleted, "guid-cross", 200.0);
            GameStateRecorder.Emit(ref completeEvt, "flight");

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

            var acceptEvt = MakeEvent(GameStateEventType.ContractAccepted, "guid-mission", 100.0);
            GameStateRecorder.Emit(ref acceptEvt, "flight");
            var completeEvt = MakeEvent(GameStateEventType.ContractCompleted, "guid-mission", 200.0);
            GameStateRecorder.Emit(ref completeEvt, "flight");

            var tree = MakeTree("tree001", "rec-A");
            RecordingStore.StashPendingTree(tree);

            RecordingStore.DiscardPendingTree();

            Assert.DoesNotContain(GameStateStore.Events, e => e.key == "guid-mission");
            Assert.Null(GameStateStore.GetContractSnapshot("guid-mission"));
        }

        // --- #8: LimboVesselSwitch resolver fallback ---

        [Fact]
        public void LimboVesselSwitch_EmitsTaggedWithPendingTreeActiveId()
        {
            // Events captured while the pending tree is in LimboVesselSwitch state belong to
            // the outgoing recording. ResolveCurrentRecordingTag falls back to
            // RecordingStore.PendingTree.ActiveRecordingId; Emit should stamp that tag without
            // firing the "outside flight" drift warn (midSwitch gates it off).
            var tree = new RecordingTree
            {
                Id = Guid.NewGuid().ToString("N"),
                TreeName = "switch-tree",
                ActiveRecordingId = "rec-outgoing",
                RootRecordingId = "rec-outgoing",
            };
            RecordingStore.StashPendingTree(tree);
            RecordingStore.SetPendingTreeStateForTesting(PendingTreeState.LimboVesselSwitch);
            GameStateRecorder.TagResolverForTesting = null; // use the production resolver
            HighLogic.LoadedScene = GameScenes.SPACECENTER; // scene reads SPACECENTER mid-switch

            var switchEvt = MakeEvent(GameStateEventType.ContractAccepted, "guid-switch", 100.0);
            GameStateRecorder.Emit(ref switchEvt, "LimboVesselSwitch-test");

            var stored = GameStateStore.Events.Single(e => e.key == "guid-switch");
            Assert.Equal("rec-outgoing", stored.recordingId);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[WARN]") && l.Contains("[GameStateRecorder]") && l.Contains("Emit drift"));
        }

        // --- #9: Drift warnings ---
        // Emit's drift-warn branches:
        //   (a) !inFlight && !midSwitch && !flightAlive && tag != "" — stale tag with no live flight
        //       (flightAlive gates out legitimate FLIGHT->KSC recovery windows where Instance lingers).
        //   (b) inFlight && tag == "" && HasLiveRecorder()
        //       — in-flight empty tag with live recorder (covered via the HasLiveRecorder seam).

        [Fact]
        public void DriftWarn_OutsideFlight_ResolverReturnsTag_LogsWarn()
        {
            // Outside flight, no pending switch, resolver returns a tag (stale fallback).
            // Emit should log the "tagged outside flight" drift-warn.
            GameStateRecorder.TagResolverForTesting = () => "rec-stale";
            HighLogic.LoadedScene = GameScenes.SPACECENTER;

            var techEvt = MakeEvent(GameStateEventType.TechResearched, "node-manual", 100.0);
            GameStateRecorder.Emit(ref techEvt, "drift-test");

            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("[GameStateRecorder]") &&
                l.Contains("Emit drift") && l.Contains("rec-stale"));
        }

        [Fact]
        public void DriftWarn_OutsideFlightWithEmptyTag_NoWarn()
        {
            GameStateRecorder.TagResolverForTesting = () => "";
            HighLogic.LoadedScene = GameScenes.SPACECENTER;

            var cleanEvt = MakeEvent(GameStateEventType.TechResearched, "node-clean", 100.0);
            GameStateRecorder.Emit(ref cleanEvt, "clean");

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

            var msEvt = MakeEvent(GameStateEventType.MilestoneAchieved, "first-orbit", 500.0);
            GameStateRecorder.Emit(ref msEvt, "test");

            var original = GameStateStore.Events.First();
            bool updated = GameStateStore.UpdateEventDetail(original, "reward=25000");

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

            var techEvt = MakeEvent(GameStateEventType.TechResearched, "node", 10.0);
            GameStateRecorder.Emit(ref techEvt, "test");
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

            var flushEvt = MakeEvent(GameStateEventType.ContractAccepted, "guid-flush", 100.0);
            GameStateRecorder.Emit(ref flushEvt, "flight");

            var tree = MakeTree("tree001", "rec-flush");
            RecordingStore.StashPendingTree(tree);

            // Simulate F5 mid-flight — flush pulls the event into a committed milestone,
            // PruneProcessedEvents then removes it from the live events list.
            var milestone = MilestoneStore.FlushPendingEvents(150.0);
            Assert.NotNull(milestone);
            GameStateStore.PruneProcessedEvents();

            Assert.DoesNotContain(GameStateStore.Events, e => e.key == "guid-flush");
            Assert.Equal(1, MilestoneStore.MilestoneCount);
            Assert.Contains(MilestoneStore.Milestones[0].Events,
                e => e.key == "guid-flush" && e.recordingId == "rec-flush");

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

            GameStateStore.AddEvent(ref untagged);
            GameStateStore.AddEvent(ref tagged);

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

            GameStateStore.AddEvent(ref first);
            GameStateStore.AddEvent(ref second);

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

        // --- #15: PurgeTaggedEvents keeps LastReplayedEventIndex pointing at the
        // same surviving event after shifting entries left.

        [Fact]
        public void PurgeTaggedEvents_AdjustsLastReplayedEventIndex()
        {
            // Mixed-tag milestone with LastReplayedEventIndex = 2 (pointing at event index 2,
            // "tech-C"). Purging rec-A (at indices 0 and 1, both at-or-before the boundary)
            // must drop the index by 2 to 0, which still points at the "tech-C" event
            // after it shifts left into slot 0.
            //
            // Without the fix, the boundary stays at 2 — the original slot now holds
            // "tech-E" (shifted from index 4), and consumers iterating from
            // LastReplayedEventIndex + 1 = 3 would skip "tech-D" (now at index 1) and
            // "tech-E" (now at index 2), treating both as already-replayed even though
            // they never were. That's exactly the drift the fix prevents.
            var events = new List<GameStateEvent>
            {
                MakeEvent(GameStateEventType.TechResearched, "tech-A1", 100.0, "rec-A"),  // idx 0, purged
                MakeEvent(GameStateEventType.TechResearched, "tech-A2", 150.0, "rec-A"),  // idx 1, purged
                MakeEvent(GameStateEventType.TechResearched, "tech-C",  200.0, "rec-C"),  // idx 2, survives — the boundary
                MakeEvent(GameStateEventType.TechResearched, "tech-D",  250.0, "rec-D"),  // idx 3, survives, unreplayed
                MakeEvent(GameStateEventType.TechResearched, "tech-E",  300.0, "rec-E"),  // idx 4, survives, unreplayed
                MakeEvent(GameStateEventType.TechResearched, "tech-A3", 350.0, "rec-A"),  // idx 5, purged (after boundary, no adjust)
            };
            var milestone = new Milestone
            {
                MilestoneId = "m-replay",
                StartUT = 0,
                EndUT = 500,
                RecordingId = "",
                Epoch = 0,
                Committed = true,
                Events = events,
                LastReplayedEventIndex = 2, // "tech-C" is the last replayed event
            };
            MilestoneStore.AddMilestoneForTesting(milestone);

            // Sanity before the purge — slot at boundary is tech-C.
            Assert.Equal("tech-C",
                MilestoneStore.Milestones[0].Events[MilestoneStore.Milestones[0].LastReplayedEventIndex].key);

            var ids = new HashSet<string> { "rec-A" };
            var removed = MilestoneStore.PurgeTaggedEvents(ids, "ReplayIdxTest");

            // Three purged events — both the two at-or-before the boundary and one after.
            Assert.Equal(3, removed.Count);

            // Milestone not dropped — three survivors remain.
            Assert.Equal(1, MilestoneStore.MilestoneCount);
            var survivingMilestone = MilestoneStore.Milestones[0];
            Assert.Equal(3, survivingMilestone.Events.Count);

            // Boundary dropped by 2 (two purges at-or-before the boundary) → index 0,
            // which still points at "tech-C" after left-shift.
            Assert.Equal(0, survivingMilestone.LastReplayedEventIndex);
            Assert.Equal("tech-C",
                survivingMilestone.Events[survivingMilestone.LastReplayedEventIndex].key);

            // The two unreplayed events (tech-D, tech-E) must both be visible to a
            // consumer iterating from LastReplayedEventIndex + 1.
            var unreplayed = new List<string>();
            for (int i = survivingMilestone.LastReplayedEventIndex + 1;
                i < survivingMilestone.Events.Count; i++)
                unreplayed.Add(survivingMilestone.Events[i].key);
            Assert.Equal(new[] { "tech-D", "tech-E" }, unreplayed);

            // Log reports the adjustment count.
            Assert.Contains(logLines, l =>
                l.Contains("[MilestoneStore]") && l.Contains("PurgeTaggedEvents") &&
                l.Contains("2 LastReplayedEventIndex adjustments"));
        }

        // --- #16: FLIGHT -> SPACECENTER teardown window does NOT leak the tagged
        // event to the ledger via OnKscSpending, while untagged pre-recording FLIGHT
        // events do get a direct ledger path when no live recorder exists because no
        // later recording commit can own them.
        //
        // Full coverage of the live teardown also needs an in-game test — see the
        // "GameState" category under InGameTests/RuntimeTests.cs, as ParsekFlight.Instance
        // lifecycle can't be mocked from xUnit.
        [Fact]
        public void TeardownWindow_TaggedEventNotForwardedToLedger()
        {
            // Arrange: tag resolver returns a recording id even though the scene reads
            // SPACECENTER. Emulates the teardown window where Emit legitimately tags
            // an event but IsFlightScene() is already false.
            GameStateRecorder.TagResolverForTesting = () => "rec-teardown";
            HighLogic.LoadedScene = GameScenes.SPACECENTER;

            var evt = MakeEvent(GameStateEventType.ContractCompleted, "guid-teardown", 100.0);
            GameStateRecorder.Emit(ref evt, "teardown-test");

            // We assert the forwarding predicate here rather than driving a GameEvents
            // subscription (which would need a full Contract object). The resolver
            // returns non-empty, so the gate skips the ledger forward.
            bool wouldForward = GameStateRecorder.ShouldForwardDirectLedgerEvent(
                evt.recordingId,
                GameStateRecorder.HasLiveRecorder());
            Assert.False(wouldForward,
                "Gate must suppress ledger forward when the event was tagged during teardown.");

            // And conversely: a true KSC event with an empty resolver passes the gate.
            GameStateRecorder.TagResolverForTesting = () => "";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => false;
            bool wouldForwardKsc = GameStateRecorder.ShouldForwardDirectLedgerEvent(
                GameStateRecorder.ResolveCurrentRecordingTag(),
                GameStateRecorder.HasLiveRecorder());
            Assert.True(wouldForwardKsc,
                "Genuine KSC events (no live recorder, empty tag) must still reach the ledger.");

            HighLogic.LoadedScene = GameScenes.FLIGHT;
            bool wouldForwardPreRecordingFlight = GameStateRecorder.ShouldForwardDirectLedgerEvent(
                GameStateRecorder.ResolveCurrentRecordingTag(),
                GameStateRecorder.HasLiveRecorder());
            Assert.True(wouldForwardPreRecordingFlight,
                "Untagged pre-recording FLIGHT events have no commit path and must reach the ledger directly.");

            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;
            bool wouldForwardTagDrift = GameStateRecorder.ShouldForwardDirectLedgerEvent(
                GameStateRecorder.ResolveCurrentRecordingTag(),
                GameStateRecorder.HasLiveRecorder());
            Assert.False(wouldForwardTagDrift,
                "An empty tag while a live recorder exists is tag drift, not an ownerless event.");
        }

        // --- Review follow-up: #553 scope expansion. The ShouldForwardDirectLedgerEvent
        // predicate was originally threaded only into the four contract handlers. Tech,
        // part-purchase, crew-hire, milestone, strategy (activate/deactivate), and
        // facility-upgrade handlers now use the same gate — same reasoning applies:
        // untagged pre-recording FLIGHT events have no later commit-time owner and must
        // reach the ledger directly. Each newly-gated handler emits an event through
        // Emit() which stamps evt.recordingId from the tag resolver, then checks the
        // predicate against that recordingId + HasLiveRecorder(). These tests pin the
        // predicate decisions each handler would make in the three canonical scenarios:
        // (a) tagged teardown — must NOT forward; (b) untagged KSC — must forward;
        // (c) untagged pre-recording FLIGHT — must forward.

        private static void AssertPredicatePinsAllThreeScenarios(GameStateEventType type, string key)
        {
            // (a) tagged teardown in SPACECENTER: tag resolver returns non-empty id
            // because ParsekFlight.Instance is still alive during FLIGHT -> KSC
            // teardown. The emitted event carries recordingId = the teardown tag.
            GameStateRecorder.TagResolverForTesting = () => "rec-teardown";
            HighLogic.LoadedScene = GameScenes.SPACECENTER;
            var evtTeardown = MakeEvent(type, key, 100.0);
            GameStateRecorder.Emit(ref evtTeardown, "predicate-pin-teardown");
            bool teardownWouldForward = GameStateRecorder.ShouldForwardDirectLedgerEvent(
                evtTeardown.recordingId,
                GameStateRecorder.HasLiveRecorder());
            Assert.False(teardownWouldForward,
                $"{type}: tagged teardown event must NOT reach the ledger via direct forward.");

            // (b) untagged KSC event: no live recorder, empty tag — classic
            // #405 KSC-only flow, must forward.
            GameStateRecorder.TagResolverForTesting = () => "";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => false;
            HighLogic.LoadedScene = GameScenes.SPACECENTER;
            var evtKsc = MakeEvent(type, key, 200.0);
            GameStateRecorder.Emit(ref evtKsc, "predicate-pin-ksc");
            bool kscWouldForward = GameStateRecorder.ShouldForwardDirectLedgerEvent(
                evtKsc.recordingId,
                GameStateRecorder.HasLiveRecorder());
            Assert.True(kscWouldForward,
                $"{type}: untagged KSC event must reach the ledger via direct forward.");

            // (c) untagged pre-recording FLIGHT: empty tag, no live recorder —
            // the #553 expansion case. Must forward because no later commit path
            // can own this event.
            GameStateRecorder.TagResolverForTesting = () => "";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => false;
            HighLogic.LoadedScene = GameScenes.FLIGHT;
            var evtFlight = MakeEvent(type, key, 300.0);
            GameStateRecorder.Emit(ref evtFlight, "predicate-pin-flight");
            bool flightWouldForward = GameStateRecorder.ShouldForwardDirectLedgerEvent(
                evtFlight.recordingId,
                GameStateRecorder.HasLiveRecorder());
            Assert.True(flightWouldForward,
                $"{type}: untagged pre-recording FLIGHT event must reach the ledger via direct forward.");

            // (d) empty tag but a live recorder exists — tag drift, not ownerless.
            // Must NOT forward so the ledger doesn't gain a null-owner action.
            GameStateRecorder.TagResolverForTesting = () => "";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;
            HighLogic.LoadedScene = GameScenes.FLIGHT;
            var evtDrift = MakeEvent(type, key, 400.0);
            GameStateRecorder.Emit(ref evtDrift, "predicate-pin-drift");
            bool driftWouldForward = GameStateRecorder.ShouldForwardDirectLedgerEvent(
                evtDrift.recordingId,
                GameStateRecorder.HasLiveRecorder());
            Assert.False(driftWouldForward,
                $"{type}: empty tag with live recorder is tag drift, direct forward must be suppressed.");
        }

        [Fact]
        public void TechResearched_PredicateGateMatchesContractHandlers()
        {
            AssertPredicatePinsAllThreeScenarios(GameStateEventType.TechResearched, "node-part-upgrade");
        }

        [Fact]
        public void PartPurchased_PredicateGateMatchesContractHandlers()
        {
            AssertPredicatePinsAllThreeScenarios(GameStateEventType.PartPurchased, "liquidEngineMini");
        }

        [Fact]
        public void CrewHired_PredicateGateMatchesContractHandlers()
        {
            AssertPredicatePinsAllThreeScenarios(GameStateEventType.CrewHired, "Jebediah Kerman");
        }

        [Fact]
        public void MilestoneAchieved_PredicateGateMatchesContractHandlers()
        {
            AssertPredicatePinsAllThreeScenarios(GameStateEventType.MilestoneAchieved, "Kerbin/FirstLaunch");
        }

        [Fact]
        public void StrategyActivated_PredicateGateMatchesContractHandlers()
        {
            AssertPredicatePinsAllThreeScenarios(GameStateEventType.StrategyActivated, "AggressiveNegotiations");
        }

        [Fact]
        public void StrategyDeactivated_PredicateGateMatchesContractHandlers()
        {
            AssertPredicatePinsAllThreeScenarios(GameStateEventType.StrategyDeactivated, "AggressiveNegotiations");
        }

        [Fact]
        public void FacilityUpgraded_PredicateGateMatchesContractHandlers()
        {
            AssertPredicatePinsAllThreeScenarios(GameStateEventType.FacilityUpgraded, "SPH");
        }

        // --- Review follow-up (Task 5): pin that the live handlers key off
        // evt.recordingId (the value stamped by Emit at capture time) rather than
        // ResolveCurrentRecordingTag() (which is a re-read at the forwarding-decision
        // moment and can drift if ParsekFlight.Instance teardown races the event).
        // The difference matters during FLIGHT -> KSC teardown: Emit captures the
        // outgoing recording's id on the event, but a later ResolveCurrentRecordingTag()
        // call can return the fresh SPACECENTER empty tag and flip the predicate to
        // "forward" — which is exactly what #431 was intended to prevent.
        [Fact]
        public void DirectForwardingPredicate_UsesEventRecordingId_NotLiveTagResolver()
        {
            // Arrange: event was captured while the recorder was live on "rec-teardown"
            // (tag stamped by Emit). Then the teardown proceeds and by the time the
            // forwarding decision runs, the tag resolver already reads empty — but
            // HasLiveRecorderProviderForTesting still returns true (ParsekFlight
            // lingers a frame).
            GameStateRecorder.TagResolverForTesting = () => "rec-teardown";
            HighLogic.LoadedScene = GameScenes.SPACECENTER;
            var evt = MakeEvent(GameStateEventType.ContractAccepted, "guid-late", 100.0);
            GameStateRecorder.Emit(ref evt, "late-teardown");
            Assert.Equal("rec-teardown", evt.recordingId);

            // Now flip the resolver to empty, as if teardown advanced one frame.
            GameStateRecorder.TagResolverForTesting = () => "";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;

            // If the handlers keyed off ResolveCurrentRecordingTag(), the second
            // argument would be "" + HasLiveRecorder()=true => tag drift => !forward.
            // If they key off evt.recordingId (what production code does), the
            // argument is "rec-teardown" which short-circuits to false anyway. Both
            // paths agree on "do not forward" in this scenario, but the LIVE handler
            // must pass evt.recordingId — verify by constructing an asymmetric case:
            //
            // Make the resolver return a DIFFERENT non-empty tag. If a handler wrongly
            // used ResolveCurrentRecordingTag(), it would still see non-empty and
            // suppress (same outcome here). The asymmetric failure mode is in the
            // OTHER direction: resolver empty, evt.recordingId non-empty. A buggy
            // handler reading the resolver would forward (empty + no live recorder
            // reflects the new scene). Simulate that:
            GameStateRecorder.TagResolverForTesting = () => "";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => false;

            // The event still carries its captured id.
            bool predicateWithCapturedId = GameStateRecorder.ShouldForwardDirectLedgerEvent(
                evt.recordingId,
                GameStateRecorder.HasLiveRecorder());
            Assert.False(predicateWithCapturedId,
                "Handler passing evt.recordingId='rec-teardown' must see !forward.");

            // If a handler wrongly re-resolved the tag at the decision moment, it
            // would pass "" and get a true.
            bool predicateWithResolvedTag = GameStateRecorder.ShouldForwardDirectLedgerEvent(
                GameStateRecorder.ResolveCurrentRecordingTag(),
                GameStateRecorder.HasLiveRecorder());
            Assert.True(predicateWithResolvedTag,
                "Sanity check: re-resolving at decision time would wrongly allow forward, " +
                "proving the two inputs disagree. Production handlers MUST pass evt.recordingId.");
        }
    }
}
