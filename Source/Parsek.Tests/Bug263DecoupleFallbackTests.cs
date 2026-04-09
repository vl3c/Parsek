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
        public void RecordFallbackDecoupleEvent_Skips_WhenPidAlreadyDecoupled()
        {
            var rec = new FlightRecorder();

            // First call: adds the event
            int firstAdded = rec.RecordFallbackDecoupleEvent(2057942744u, "radialDecoupler1-2", 72.4);
            Assert.Equal(1, firstAdded);
            Assert.Single(rec.PartEvents);

            logLines.Clear();

            // Second call for same pid: should skip
            int secondAdded = rec.RecordFallbackDecoupleEvent(2057942744u, "radialDecoupler1-2", 72.4);
            Assert.Equal(0, secondAdded);
            Assert.Single(rec.PartEvents); // still only one event

            // Verify "already in decoupled set" log
            Assert.Contains(logLines, l => l.Contains("[Recorder]") &&
                l.Contains("RecordFallbackDecoupleEvent") &&
                l.Contains("already in decoupled set"));
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
            // Simulates: OnPartJointBreak captured pid X at UT 72.4 (core-side, race-won),
            // then the deferred fallback runs and tries to re-record at UT 72.45
            // (slightly later). The fallback should skip the duplicate.
            var rec = new FlightRecorder();

            // Manually seed decoupledPartIds to simulate OnPartJointBreak's effect.
            // (The field is private, so we seed via a fallback call at the original UT.)
            rec.RecordFallbackDecoupleEvent(2057942744u, "radialDecoupler1-2", 72.4);
            Assert.Single(rec.PartEvents);
            double originalUt = rec.PartEvents[0].ut;

            // "Fallback for same pid at later UT" — should be skipped
            int added = rec.RecordFallbackDecoupleEvent(2057942744u, "radialDecoupler1-2", 72.45);
            Assert.Equal(0, added);
            Assert.Single(rec.PartEvents);

            // The original event's UT is preserved (not overwritten)
            Assert.Equal(originalUt, rec.PartEvents[0].ut);
        }
    }
}
