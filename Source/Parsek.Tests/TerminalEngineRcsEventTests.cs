using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for FlightRecorder.EmitTerminalEngineAndRcsEvents — synthetic shutdown
    /// events emitted at recording boundary for engines/RCS/robotics still active.
    /// Bug #108.
    /// </summary>
    [Collection("Sequential")]
    public class TerminalEngineRcsEventTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public TerminalEngineRcsEventTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
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

        #region No active keys — empty result

        [Fact]
        public void EmitTerminal_NoActiveKeys_ReturnsEmptyList()
        {
            var engines = new HashSet<ulong>();
            var rcs = new HashSet<ulong>();
            var robotics = new HashSet<ulong>();
            var roboticPos = new Dictionary<ulong, float>();

            var events = FlightRecorder.EmitTerminalEngineAndRcsEvents(
                engines, rcs, robotics, roboticPos, 5000.0, "Test");

            Assert.Empty(events);
        }

        [Fact]
        public void EmitTerminal_NullSets_ReturnsEmptyList()
        {
            var events = FlightRecorder.EmitTerminalEngineAndRcsEvents(
                null, null, null, null, 5000.0, "Test");

            Assert.Empty(events);
        }

        [Fact]
        public void EmitTerminal_NoActiveKeys_NoLogEmitted()
        {
            var engines = new HashSet<ulong>();
            var rcs = new HashSet<ulong>();
            var robotics = new HashSet<ulong>();
            var roboticPos = new Dictionary<ulong, float>();

            FlightRecorder.EmitTerminalEngineAndRcsEvents(
                engines, rcs, robotics, roboticPos, 5000.0, "Test");

            // No summary log when zero events emitted
            Assert.DoesNotContain(logLines, l => l.Contains("terminal events"));
        }

        #endregion

        #region Engine terminal events

        [Fact]
        public void EmitTerminal_OneActiveEngine_EmitsEngineShutdown()
        {
            uint pid = 42000;
            int midx = 0;
            ulong key = FlightRecorder.EncodeEngineKey(pid, midx);

            var engines = new HashSet<ulong> { key };
            var rcs = new HashSet<ulong>();
            var robotics = new HashSet<ulong>();
            var roboticPos = new Dictionary<ulong, float>();

            var events = FlightRecorder.EmitTerminalEngineAndRcsEvents(
                engines, rcs, robotics, roboticPos, 5000.0, "Test");

            Assert.Single(events);
            Assert.Equal(PartEventType.EngineShutdown, events[0].eventType);
            Assert.Equal(pid, events[0].partPersistentId);
            Assert.Equal(midx, events[0].moduleIndex);
            Assert.Equal(5000.0, events[0].ut);
        }

        [Fact]
        public void EmitTerminal_MultipleActiveEngines_EmitsShutdownForEach()
        {
            ulong key1 = FlightRecorder.EncodeEngineKey(100, 0);
            ulong key2 = FlightRecorder.EncodeEngineKey(200, 1);
            ulong key3 = FlightRecorder.EncodeEngineKey(300, 2);

            var engines = new HashSet<ulong> { key1, key2, key3 };
            var rcs = new HashSet<ulong>();
            var robotics = new HashSet<ulong>();
            var roboticPos = new Dictionary<ulong, float>();

            var events = FlightRecorder.EmitTerminalEngineAndRcsEvents(
                engines, rcs, robotics, roboticPos, 9999.0, "Test");

            Assert.Equal(3, events.Count);
            Assert.All(events, e => Assert.Equal(PartEventType.EngineShutdown, e.eventType));
            Assert.All(events, e => Assert.Equal(9999.0, e.ut));

            var pids = events.Select(e => e.partPersistentId).OrderBy(p => p).ToList();
            Assert.Equal(new List<uint> { 100, 200, 300 }, pids);
        }

        [Fact]
        public void EmitTerminal_EngineWithNonZeroModuleIndex_PreservesModuleIndex()
        {
            uint pid = 55555;
            int midx = 3;
            ulong key = FlightRecorder.EncodeEngineKey(pid, midx);

            var engines = new HashSet<ulong> { key };

            var events = FlightRecorder.EmitTerminalEngineAndRcsEvents(
                engines, new HashSet<ulong>(), new HashSet<ulong>(),
                new Dictionary<ulong, float>(), 7000.0, "Test");

            Assert.Single(events);
            Assert.Equal(3, events[0].moduleIndex);
            Assert.Equal(pid, events[0].partPersistentId);
        }

        [Fact]
        public void EmitTerminal_ActiveEngine_LogsTerminalEvent()
        {
            ulong key = FlightRecorder.EncodeEngineKey(42000, 0);
            var engines = new HashSet<ulong> { key };

            FlightRecorder.EmitTerminalEngineAndRcsEvents(
                engines, new HashSet<ulong>(), new HashSet<ulong>(),
                new Dictionary<ulong, float>(), 5000.0, "TestTag");

            Assert.Contains(logLines, l =>
                l.Contains("[TestTag]") &&
                l.Contains("Terminal event: EngineShutdown") &&
                l.Contains("pid=42000") &&
                l.Contains("midx=0"));
        }

        #endregion

        #region RCS terminal events

        [Fact]
        public void EmitTerminal_OneActiveRcs_EmitsRcsStopped()
        {
            uint pid = 88000;
            int midx = 1;
            ulong key = FlightRecorder.EncodeEngineKey(pid, midx);

            var engines = new HashSet<ulong>();
            var rcs = new HashSet<ulong> { key };
            var robotics = new HashSet<ulong>();
            var roboticPos = new Dictionary<ulong, float>();

            var events = FlightRecorder.EmitTerminalEngineAndRcsEvents(
                engines, rcs, robotics, roboticPos, 6000.0, "Test");

            Assert.Single(events);
            Assert.Equal(PartEventType.RCSStopped, events[0].eventType);
            Assert.Equal(pid, events[0].partPersistentId);
            Assert.Equal(midx, events[0].moduleIndex);
            Assert.Equal(6000.0, events[0].ut);
        }

        [Fact]
        public void EmitTerminal_MultipleActiveRcs_EmitsStoppedForEach()
        {
            ulong key1 = FlightRecorder.EncodeEngineKey(400, 0);
            ulong key2 = FlightRecorder.EncodeEngineKey(500, 0);

            var rcs = new HashSet<ulong> { key1, key2 };

            var events = FlightRecorder.EmitTerminalEngineAndRcsEvents(
                new HashSet<ulong>(), rcs, new HashSet<ulong>(),
                new Dictionary<ulong, float>(), 8000.0, "Test");

            Assert.Equal(2, events.Count);
            Assert.All(events, e => Assert.Equal(PartEventType.RCSStopped, e.eventType));
            Assert.All(events, e => Assert.Equal(8000.0, e.ut));
        }

        [Fact]
        public void EmitTerminal_ActiveRcs_LogsTerminalEvent()
        {
            ulong key = FlightRecorder.EncodeEngineKey(88000, 1);
            var rcs = new HashSet<ulong> { key };

            FlightRecorder.EmitTerminalEngineAndRcsEvents(
                new HashSet<ulong>(), rcs, new HashSet<ulong>(),
                new Dictionary<ulong, float>(), 6000.0, "TestTag");

            Assert.Contains(logLines, l =>
                l.Contains("[TestTag]") &&
                l.Contains("Terminal event: RCSStopped") &&
                l.Contains("pid=88000") &&
                l.Contains("midx=1"));
        }

        #endregion

        #region Robotic terminal events

        [Fact]
        public void EmitTerminal_OneActiveRobotic_EmitsRoboticMotionStopped()
        {
            uint pid = 77000;
            int midx = 0;
            ulong key = FlightRecorder.EncodeEngineKey(pid, midx);

            var robotics = new HashSet<ulong> { key };
            var roboticPos = new Dictionary<ulong, float> { { key, 0.75f } };

            var events = FlightRecorder.EmitTerminalEngineAndRcsEvents(
                new HashSet<ulong>(), new HashSet<ulong>(), robotics,
                roboticPos, 7000.0, "Test");

            Assert.Single(events);
            Assert.Equal(PartEventType.RoboticMotionStopped, events[0].eventType);
            Assert.Equal(pid, events[0].partPersistentId);
            Assert.Equal(midx, events[0].moduleIndex);
            Assert.Equal(0.75f, events[0].value);
            Assert.Equal(7000.0, events[0].ut);
        }

        [Fact]
        public void EmitTerminal_ActiveRoboticNoPositionMap_UsesZero()
        {
            ulong key = FlightRecorder.EncodeEngineKey(77000, 0);
            var robotics = new HashSet<ulong> { key };

            var events = FlightRecorder.EmitTerminalEngineAndRcsEvents(
                new HashSet<ulong>(), new HashSet<ulong>(), robotics,
                null, 7000.0, "Test");

            Assert.Single(events);
            Assert.Equal(0f, events[0].value);
        }

        [Fact]
        public void EmitTerminal_ActiveRoboticKeyNotInPositionMap_UsesZero()
        {
            ulong key = FlightRecorder.EncodeEngineKey(77000, 0);
            var robotics = new HashSet<ulong> { key };
            var roboticPos = new Dictionary<ulong, float>(); // empty — key not present

            var events = FlightRecorder.EmitTerminalEngineAndRcsEvents(
                new HashSet<ulong>(), new HashSet<ulong>(), robotics,
                roboticPos, 7000.0, "Test");

            Assert.Single(events);
            Assert.Equal(0f, events[0].value);
        }

        [Fact]
        public void EmitTerminal_ActiveRobotic_LogsTerminalEvent()
        {
            ulong key = FlightRecorder.EncodeEngineKey(77000, 0);
            var robotics = new HashSet<ulong> { key };
            var roboticPos = new Dictionary<ulong, float> { { key, 0.5f } };

            FlightRecorder.EmitTerminalEngineAndRcsEvents(
                new HashSet<ulong>(), new HashSet<ulong>(), robotics,
                roboticPos, 7000.0, "TestTag");

            Assert.Contains(logLines, l =>
                l.Contains("[TestTag]") &&
                l.Contains("Terminal event: RoboticMotionStopped") &&
                l.Contains("pid=77000"));
        }

        #endregion

        #region Mixed active sets

        [Fact]
        public void EmitTerminal_EnginesAndRcsAndRobotics_EmitsAllTerminalEvents()
        {
            ulong engineKey = FlightRecorder.EncodeEngineKey(1000, 0);
            ulong rcsKey = FlightRecorder.EncodeEngineKey(2000, 0);
            ulong roboticKey = FlightRecorder.EncodeEngineKey(3000, 0);

            var engines = new HashSet<ulong> { engineKey };
            var rcs = new HashSet<ulong> { rcsKey };
            var robotics = new HashSet<ulong> { roboticKey };
            var roboticPos = new Dictionary<ulong, float> { { roboticKey, 0.3f } };

            var events = FlightRecorder.EmitTerminalEngineAndRcsEvents(
                engines, rcs, robotics, roboticPos, 10000.0, "Test");

            Assert.Equal(3, events.Count);
            Assert.Contains(events, e => e.eventType == PartEventType.EngineShutdown && e.partPersistentId == 1000);
            Assert.Contains(events, e => e.eventType == PartEventType.RCSStopped && e.partPersistentId == 2000);
            Assert.Contains(events, e => e.eventType == PartEventType.RoboticMotionStopped && e.partPersistentId == 3000);
            Assert.All(events, e => Assert.Equal(10000.0, e.ut));
        }

        [Fact]
        public void EmitTerminal_MixedActive_LogsSummaryWithCounts()
        {
            ulong engineKey1 = FlightRecorder.EncodeEngineKey(1000, 0);
            ulong engineKey2 = FlightRecorder.EncodeEngineKey(1001, 1);
            ulong rcsKey = FlightRecorder.EncodeEngineKey(2000, 0);

            var engines = new HashSet<ulong> { engineKey1, engineKey2 };
            var rcs = new HashSet<ulong> { rcsKey };

            FlightRecorder.EmitTerminalEngineAndRcsEvents(
                engines, rcs, new HashSet<ulong>(),
                new Dictionary<ulong, float>(), 10000.0, "TestTag");

            Assert.Contains(logLines, l =>
                l.Contains("[TestTag]") &&
                l.Contains("Emitted 3 terminal events") &&
                l.Contains("engines=2") &&
                l.Contains("rcs=1") &&
                l.Contains("robotics=0"));
        }

        #endregion

        #region UT correctness

        [Fact]
        public void EmitTerminal_AllEventsUseFinalUT()
        {
            double finalUT = 12345.678;
            ulong engineKey = FlightRecorder.EncodeEngineKey(100, 0);
            ulong rcsKey = FlightRecorder.EncodeEngineKey(200, 0);

            var events = FlightRecorder.EmitTerminalEngineAndRcsEvents(
                new HashSet<ulong> { engineKey },
                new HashSet<ulong> { rcsKey },
                new HashSet<ulong>(),
                new Dictionary<ulong, float>(),
                finalUT, "Test");

            Assert.All(events, e => Assert.Equal(finalUT, e.ut));
        }

        #endregion

        #region Part name is "unknown" (no live vessel lookup in static method)

        [Fact]
        public void EmitTerminal_PartNameIsUnknown()
        {
            ulong key = FlightRecorder.EncodeEngineKey(42000, 0);

            var events = FlightRecorder.EmitTerminalEngineAndRcsEvents(
                new HashSet<ulong> { key },
                new HashSet<ulong>(),
                new HashSet<ulong>(),
                new Dictionary<ulong, float>(),
                5000.0, "Test");

            Assert.Single(events);
            Assert.Equal("unknown", events[0].partName);
        }

        #endregion
    }
}
