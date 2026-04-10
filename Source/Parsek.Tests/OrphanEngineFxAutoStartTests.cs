using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for orphan engine FX auto-start logic (#295).
    /// Debris booster engines that were running at breakup have no seed events
    /// (BackgroundRecorder finds isOperational=false after fuel is severed).
    /// The playback side compensates by auto-starting FX when the recording
    /// has ZERO engine events (pure debris pattern).
    ///
    /// Tests exercise the extracted pure static methods (BuildEngineEventKeySet,
    /// FindOrphanKeys) which do not require Unity runtime.
    /// </summary>
    [Collection("Sequential")]
    public class OrphanEngineFxAutoStartTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public OrphanEngineFxAutoStartTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        #region BuildEngineEventKeySet

        [Fact]
        public void BuildEngineEventKeySet_EmptyEvents_ReturnsEmptySet()
        {
            var keys = GhostPlaybackLogic.BuildEngineEventKeySet(new List<PartEvent>());
            Assert.Empty(keys);
        }

        [Fact]
        public void BuildEngineEventKeySet_NullEvents_ReturnsEmptySet()
        {
            var keys = GhostPlaybackLogic.BuildEngineEventKeySet(null);
            Assert.Empty(keys);
        }

        [Fact]
        public void BuildEngineEventKeySet_EngineIgnited_CollectsKey()
        {
            var events = new List<PartEvent>
            {
                new PartEvent { partPersistentId = 100, moduleIndex = 0, eventType = PartEventType.EngineIgnited }
            };

            var keys = GhostPlaybackLogic.BuildEngineEventKeySet(events);

            ulong expectedKey = FlightRecorder.EncodeEngineKey(100, 0);
            Assert.Contains(expectedKey, keys);
        }

        [Fact]
        public void BuildEngineEventKeySet_EngineThrottle_CollectsKey()
        {
            var events = new List<PartEvent>
            {
                new PartEvent { partPersistentId = 200, moduleIndex = 1, eventType = PartEventType.EngineThrottle, value = 0.5f }
            };

            var keys = GhostPlaybackLogic.BuildEngineEventKeySet(events);

            ulong expectedKey = FlightRecorder.EncodeEngineKey(200, 1);
            Assert.Contains(expectedKey, keys);
        }

        [Fact]
        public void BuildEngineEventKeySet_RcsEvents_Ignored()
        {
            // RCS events should NOT be collected — only engine events matter
            var events = new List<PartEvent>
            {
                new PartEvent { partPersistentId = 300, moduleIndex = 0, eventType = PartEventType.RCSActivated },
                new PartEvent { partPersistentId = 400, moduleIndex = 0, eventType = PartEventType.RCSThrottle, value = 0.7f }
            };

            var keys = GhostPlaybackLogic.BuildEngineEventKeySet(events);
            Assert.Empty(keys);
        }

        [Fact]
        public void BuildEngineEventKeySet_MixedEvents_CollectsOnlyEngineKeys()
        {
            var events = new List<PartEvent>
            {
                new PartEvent { partPersistentId = 100, moduleIndex = 0, eventType = PartEventType.EngineIgnited },
                new PartEvent { partPersistentId = 300, moduleIndex = 0, eventType = PartEventType.RCSActivated },
                new PartEvent { partPersistentId = 100, moduleIndex = 0, eventType = PartEventType.EngineThrottle, value = 0.8f },
                new PartEvent { partPersistentId = 500, moduleIndex = 0, eventType = PartEventType.Decoupled }
            };

            var keys = GhostPlaybackLogic.BuildEngineEventKeySet(events);

            Assert.Single(keys); // pid=100 counted once (dedup via HashSet)
        }

        [Fact]
        public void BuildEngineEventKeySet_EngineShutdown_NotCollected()
        {
            // EngineShutdown must NOT count as "has events" — debris recordings
            // may have a shutdown event (engine burns out) but no ignited/throttle seed
            var events = new List<PartEvent>
            {
                new PartEvent { partPersistentId = 100, moduleIndex = 0, eventType = PartEventType.EngineShutdown }
            };

            var keys = GhostPlaybackLogic.BuildEngineEventKeySet(events);
            Assert.Empty(keys);
        }

        #endregion

        #region FindOrphanKeys

        [Fact]
        public void FindOrphanKeys_NoEvents_AllKeysOrphan()
        {
            ulong key1 = FlightRecorder.EncodeEngineKey(100, 0);
            ulong key2 = FlightRecorder.EncodeEngineKey(200, 0);
            var infoKeys = new List<ulong> { key1, key2 };
            var keysWithEvents = new HashSet<ulong>(); // empty — no events

            var orphans = GhostPlaybackLogic.FindOrphanKeys(infoKeys, keysWithEvents);

            Assert.Equal(2, orphans.Count);
            Assert.Contains(key1, orphans);
            Assert.Contains(key2, orphans);
        }

        [Fact]
        public void FindOrphanKeys_SomeEvents_OnlyMissingKeysOrphan()
        {
            ulong key1 = FlightRecorder.EncodeEngineKey(100, 0);
            ulong key2 = FlightRecorder.EncodeEngineKey(200, 0);
            var infoKeys = new List<ulong> { key1, key2 };
            var keysWithEvents = new HashSet<ulong> { key1 }; // key1 has events

            var orphans = GhostPlaybackLogic.FindOrphanKeys(infoKeys, keysWithEvents);

            Assert.Single(orphans);
            Assert.Contains(key2, orphans);
        }

        [Fact]
        public void FindOrphanKeys_AllHaveEvents_NoOrphans()
        {
            ulong key1 = FlightRecorder.EncodeEngineKey(100, 0);
            ulong key2 = FlightRecorder.EncodeEngineKey(200, 0);
            var infoKeys = new List<ulong> { key1, key2 };
            var keysWithEvents = new HashSet<ulong> { key1, key2 };

            var orphans = GhostPlaybackLogic.FindOrphanKeys(infoKeys, keysWithEvents);

            Assert.Empty(orphans);
        }

        [Fact]
        public void FindOrphanKeys_NullKeysWithEvents_ReturnsEmpty()
        {
            ulong key1 = FlightRecorder.EncodeEngineKey(100, 0);
            var infoKeys = new List<ulong> { key1 };

            var orphans = GhostPlaybackLogic.FindOrphanKeys(infoKeys, null);

            Assert.Empty(orphans);
        }

        [Fact]
        public void FindOrphanKeys_MultiModulePart_DistinguishesByModuleIndex()
        {
            ulong key0 = FlightRecorder.EncodeEngineKey(100, 0);
            ulong key1 = FlightRecorder.EncodeEngineKey(100, 1);
            var infoKeys = new List<ulong> { key0, key1 };
            var keysWithEvents = new HashSet<ulong> { key0 };

            var orphans = GhostPlaybackLogic.FindOrphanKeys(infoKeys, keysWithEvents);

            Assert.Single(orphans);
            Assert.Contains(key1, orphans);
        }

        #endregion

        #region Integration

        [Fact]
        public void EndToEnd_DebrisBoosterPattern_ZeroEventsIdentifiesAllOrphans()
        {
            // Simulate a debris booster recording: one engine, no engine events at all
            ulong boosterEngineKey = FlightRecorder.EncodeEngineKey(545928558, 0);
            var events = new List<PartEvent>(); // empty — no seed events in debris recording

            var engineKeys = GhostPlaybackLogic.BuildEngineEventKeySet(events);

            // Zero engine events = pure debris pattern → all engines are orphans
            Assert.Empty(engineKeys);
            var orphans = GhostPlaybackLogic.FindOrphanKeys(
                new List<ulong> { boosterEngineKey }, engineKeys);
            Assert.Single(orphans);
            Assert.Equal(boosterEngineKey, orphans[0]);
        }

        [Fact]
        public void EndToEnd_MainVesselWithSeeds_EnginesNotOrphan()
        {
            // Simulate a main vessel recording: engines have EngineThrottle seed events
            ulong mainsailKey = FlightRecorder.EncodeEngineKey(2485666303, 0);
            ulong boosterKey = FlightRecorder.EncodeEngineKey(2527095907, 0);
            var events = new List<PartEvent>
            {
                new PartEvent { partPersistentId = 2485666303, moduleIndex = 0, eventType = PartEventType.EngineThrottle, value = 1f },
                new PartEvent { partPersistentId = 2527095907, moduleIndex = 0, eventType = PartEventType.EngineThrottle, value = 1f }
            };

            var engineKeys = GhostPlaybackLogic.BuildEngineEventKeySet(events);

            // Non-empty key set → NOT the zero-event pattern → no auto-start
            Assert.NotEmpty(engineKeys);
        }

        #endregion
    }
}
