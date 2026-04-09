using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for bug #263: decouple events missing for symmetric radial decouplers.
    ///
    /// When a symmetry group of radial decouplers fires, individual onPartJointBreak
    /// events race with KSP's vessel-split processing and some can be dropped
    /// mid-pipeline, leaving decoupled subtrees visible on the ghost during playback.
    /// The fix adds a deferred-check fallback that emits a Decoupled event for every
    /// new debris vessel's root part, with dedup against decoupledPartIds.
    ///
    /// These tests cover the pure logic of
    /// <see cref="FlightRecorder.RecordFallbackDecoupleEvent"/> — the recorder-level
    /// primitive. The ParsekFlight-level glue (EmitFallbackDecoupleEventsForNewVessels)
    /// requires live Unity Vessel objects and is covered by in-game tests elsewhere.
    /// </summary>
    [Collection("Sequential")]
    public class Bug263DecoupleFallbackTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug263DecoupleFallbackTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        [Fact]
        public void RecordFallbackDecoupleEvent_AddsEvent_WhenPidNotInDecoupledSet()
        {
            var rec = new FlightRecorder();
            int before = rec.PartEvents.Count;

            int added = rec.RecordFallbackDecoupleEvent(
                partPid: 1009856088u,
                partName: "radialDecoupler1-2",
                ut: 72.4);

            Assert.Equal(1, added);
            Assert.Equal(before + 1, rec.PartEvents.Count);

            var evt = rec.PartEvents[rec.PartEvents.Count - 1];
            Assert.Equal(PartEventType.Decoupled, evt.eventType);
            Assert.Equal(1009856088u, evt.partPersistentId);
            Assert.Equal("radialDecoupler1-2", evt.partName);
            Assert.Equal(72.4, evt.ut);
            Assert.Equal(0f, evt.value);
            Assert.Equal(0, evt.moduleIndex);

            // Verify log line emitted
            Assert.Contains(logLines, l => l.Contains("[Recorder]") &&
                l.Contains("Fallback Decoupled event recorded") &&
                l.Contains("pid=1009856088"));
        }

        [Fact]
        public void RecordFallbackDecoupleEvent_Skips_WhenPartEventsAlreadyHasDecoupledForPid()
        {
            var rec = new FlightRecorder();

            // First call: adds the event to PartEvents
            int firstAdded = rec.RecordFallbackDecoupleEvent(2057942744u, "radialDecoupler1-2", 72.4);
            Assert.Equal(1, firstAdded);
            Assert.Single(rec.PartEvents);

            logLines.Clear();

            // Second call for same pid: should skip because PartEvents already has it
            int secondAdded = rec.RecordFallbackDecoupleEvent(2057942744u, "radialDecoupler1-2", 72.4);
            Assert.Equal(0, secondAdded);
            Assert.Single(rec.PartEvents); // still only one event

            // Verify the dedup log reflects PartEvents as the source of truth
            Assert.Contains(logLines, l => l.Contains("[Recorder]") &&
                l.Contains("RecordFallbackDecoupleEvent") &&
                l.Contains("already has a Decoupled PartEvent"));
        }

        [Fact]
        public void RecordFallbackDecoupleEvent_Skips_WhenOtherEventTypeForSamePid()
        {
            // An EngineShutdown event for a pid should NOT prevent the fallback from
            // adding a Decoupled event for that pid — only existing Decoupled events
            // should dedup (otherwise we'd lose the decouple hide for a part that
            // happened to have an earlier engine shutdown).
            var rec = new FlightRecorder();
            rec.PartEvents.Add(new PartEvent
            {
                ut = 70.0,
                partPersistentId = 2527095907u,
                eventType = PartEventType.EngineShutdown,
                partName = "liquidEngine2",
                moduleIndex = 0,
                value = 0f
            });

            int added = rec.RecordFallbackDecoupleEvent(2527095907u, "liquidEngine2", 72.4);

            Assert.Equal(1, added);
            Assert.Equal(2, rec.PartEvents.Count);
            Assert.Equal(PartEventType.Decoupled, rec.PartEvents[1].eventType);
            Assert.Equal(2527095907u, rec.PartEvents[1].partPersistentId);
        }

        [Fact]
        public void RecordFallbackDecoupleEvent_NullPartName_UsesUnknownFallback()
        {
            var rec = new FlightRecorder();

            int added = rec.RecordFallbackDecoupleEvent(12345u, partName: null, ut: 100.0);

            Assert.Equal(1, added);
            var evt = rec.PartEvents[0];
            Assert.Equal("unknown", evt.partName);
        }

        [Fact]
        public void RecordFallbackDecoupleEvent_MultipleDistinctPids_AllRecorded()
        {
            // Simulates the Kerbal X scenario: 6 radial decouplers in 3 symmetry pairs.
            // Each pair fires at a different UT. The fallback should record all 6.
            var rec = new FlightRecorder();

            // Pair 1
            Assert.Equal(1, rec.RecordFallbackDecoupleEvent(2057942744u, "radialDecoupler1-2", 72.4));
            Assert.Equal(1, rec.RecordFallbackDecoupleEvent(1009856088u, "radialDecoupler1-2", 72.4));

            // Pair 2
            Assert.Equal(1, rec.RecordFallbackDecoupleEvent(3027027466u, "radialDecoupler1-2", 87.94));
            Assert.Equal(1, rec.RecordFallbackDecoupleEvent(2130796824u, "radialDecoupler1-2", 87.94));

            // Pair 3
            Assert.Equal(1, rec.RecordFallbackDecoupleEvent(3271565278u, "radialDecoupler1-2", 104.86));
            Assert.Equal(1, rec.RecordFallbackDecoupleEvent(633147235u,  "radialDecoupler1-2", 104.86));

            Assert.Equal(6, rec.PartEvents.Count);

            // Verify each pid appears exactly once with the correct UT
            var byPid = new Dictionary<uint, PartEvent>();
            foreach (var e in rec.PartEvents) byPid[e.partPersistentId] = e;
            Assert.Equal(6, byPid.Count);
            Assert.Equal(72.4, byPid[2057942744u].ut);
            Assert.Equal(72.4, byPid[1009856088u].ut);
            Assert.Equal(87.94, byPid[3027027466u].ut);
            Assert.Equal(87.94, byPid[2130796824u].ut);
            Assert.Equal(104.86, byPid[3271565278u].ut);
            Assert.Equal(104.86, byPid[633147235u].ut);
        }

        [Fact]
        public void RecordFallbackDecoupleEvent_DoesNotOverrideEarlierEvent_WhenPidWasRecordedByJointBreak()
        {
            // Simulates: OnPartJointBreak captured pid X at UT 72.4 (race-won), then the
            // deferred fallback runs and tries to re-record at UT 72.45 (slightly later).
            // The fallback should skip because PartEvents already has a Decoupled entry
            // for this pid — the original UT must be preserved.
            var rec = new FlightRecorder();

            rec.RecordFallbackDecoupleEvent(2057942744u, "radialDecoupler1-2", 72.4);
            Assert.Single(rec.PartEvents);
            double originalUt = rec.PartEvents[0].ut;

            int added = rec.RecordFallbackDecoupleEvent(2057942744u, "radialDecoupler1-2", 72.45);
            Assert.Equal(0, added);
            Assert.Single(rec.PartEvents);
            Assert.Equal(originalUt, rec.PartEvents[0].ut);
        }

        [Fact]
        public void RecordFallbackDecoupleEvent_RecoversEvent_WhenPartEventsDroppedButDecoupledSetStale()
        {
            // THE critical regression test for bug #263, specifically addressing PR #161
            // review feedback: the fallback must dedup against PartEvents (source of truth),
            // NOT against decoupledPartIds (parallel tracker that can drift out of sync).
            //
            // Scenario: OnPartJointBreak added pid X to both PartEvents and decoupledPartIds.
            // Something downstream (observed in the 2026-04-09 Kerbal X playtest, exact
            // mechanism still unidentified) removed the event from PartEvents before the
            // file write. decoupledPartIds is never pruned, so it still contains X.
            //
            // Expected behavior: the fallback scans PartEvents, finds no Decoupled entry
            // for X, and recovers the event by re-adding it.
            //
            // If the fallback dedups against decoupledPartIds instead, this test fails
            // (the recovery is silently skipped and the ghost stays broken) — which is
            // exactly the gap the reviewer flagged.
            var rec = new FlightRecorder();

            // Step 1: initial fallback add — populates both PartEvents and decoupledPartIds.
            int firstAdded = rec.RecordFallbackDecoupleEvent(2057942744u, "radialDecoupler1-2", 72.4);
            Assert.Equal(1, firstAdded);
            Assert.Single(rec.PartEvents);

            // Step 2: simulate the pipeline drop. Remove the event from PartEvents.
            // decoupledPartIds is NOT cleaned up — nothing in production prunes it
            // when events are stripped downstream, which is the core of the bug.
            rec.PartEvents.Clear();
            Assert.Empty(rec.PartEvents);

            logLines.Clear();

            // Step 3: the fallback runs again (simulating a second deferred-check pass,
            // or a later commit path that re-scans new vessels). It must recover the
            // event by checking PartEvents directly rather than trusting decoupledPartIds.
            int recovered = rec.RecordFallbackDecoupleEvent(2057942744u, "radialDecoupler1-2", 72.4);

            Assert.Equal(1, recovered);
            Assert.Single(rec.PartEvents);
            Assert.Equal(PartEventType.Decoupled, rec.PartEvents[0].eventType);
            Assert.Equal(2057942744u, rec.PartEvents[0].partPersistentId);
            Assert.Equal("radialDecoupler1-2", rec.PartEvents[0].partName);
            Assert.Equal(72.4, rec.PartEvents[0].ut);

            // The recovery path logs the successful add, not the dedup skip
            Assert.Contains(logLines, l => l.Contains("[Recorder]") &&
                l.Contains("Fallback Decoupled event recorded") &&
                l.Contains("pid=2057942744"));
            Assert.DoesNotContain(logLines, l => l.Contains("already has a Decoupled PartEvent"));
        }
    }
}
