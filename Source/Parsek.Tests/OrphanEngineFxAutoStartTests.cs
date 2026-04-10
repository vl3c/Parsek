using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for orphan engine/RCS FX auto-start logic.
    /// Debris booster engines that were running at breakup have no seed events
    /// (BackgroundRecorder finds isOperational=false after fuel is severed).
    /// The playback side compensates by auto-starting FX for orphan engines.
    ///
    /// Tests exercise the extracted pure static methods (BuildOrphanKeySets, FindOrphanKeys)
    /// which do not require Unity runtime.
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

        #region BuildOrphanKeySets

        [Fact]
        public void BuildOrphanKeySets_EmptyEvents_ReturnsEmptySets()
        {
            HashSet<ulong> engineKeys, rcsKeys;
            GhostPlaybackLogic.BuildOrphanKeySets(new List<PartEvent>(), out engineKeys, out rcsKeys);

            Assert.Empty(engineKeys);
            Assert.Empty(rcsKeys);
        }

        [Fact]
        public void BuildOrphanKeySets_NullEvents_ReturnsEmptySets()
        {
            HashSet<ulong> engineKeys, rcsKeys;
            GhostPlaybackLogic.BuildOrphanKeySets(null, out engineKeys, out rcsKeys);

            Assert.Empty(engineKeys);
            Assert.Empty(rcsKeys);
        }

        [Fact]
        public void BuildOrphanKeySets_EngineIgnited_CollectsKey()
        {
            var events = new List<PartEvent>
            {
                new PartEvent { partPersistentId = 100, moduleIndex = 0, eventType = PartEventType.EngineIgnited }
            };

            HashSet<ulong> engineKeys, rcsKeys;
            GhostPlaybackLogic.BuildOrphanKeySets(events, out engineKeys, out rcsKeys);

            ulong expectedKey = FlightRecorder.EncodeEngineKey(100, 0);
            Assert.Contains(expectedKey, engineKeys);
            Assert.Empty(rcsKeys);
        }

        [Fact]
        public void BuildOrphanKeySets_EngineThrottle_CollectsKey()
        {
            var events = new List<PartEvent>
            {
                new PartEvent { partPersistentId = 200, moduleIndex = 1, eventType = PartEventType.EngineThrottle, value = 0.5f }
            };

            HashSet<ulong> engineKeys, rcsKeys;
            GhostPlaybackLogic.BuildOrphanKeySets(events, out engineKeys, out rcsKeys);

            ulong expectedKey = FlightRecorder.EncodeEngineKey(200, 1);
            Assert.Contains(expectedKey, engineKeys);
            Assert.Empty(rcsKeys);
        }

        [Fact]
        public void BuildOrphanKeySets_RcsActivated_CollectsRcsKey()
        {
            var events = new List<PartEvent>
            {
                new PartEvent { partPersistentId = 300, moduleIndex = 0, eventType = PartEventType.RCSActivated }
            };

            HashSet<ulong> engineKeys, rcsKeys;
            GhostPlaybackLogic.BuildOrphanKeySets(events, out engineKeys, out rcsKeys);

            ulong expectedKey = FlightRecorder.EncodeEngineKey(300, 0);
            Assert.Empty(engineKeys);
            Assert.Contains(expectedKey, rcsKeys);
        }

        [Fact]
        public void BuildOrphanKeySets_RcsThrottle_CollectsRcsKey()
        {
            var events = new List<PartEvent>
            {
                new PartEvent { partPersistentId = 400, moduleIndex = 0, eventType = PartEventType.RCSThrottle, value = 0.7f }
            };

            HashSet<ulong> engineKeys, rcsKeys;
            GhostPlaybackLogic.BuildOrphanKeySets(events, out engineKeys, out rcsKeys);

            ulong expectedKey = FlightRecorder.EncodeEngineKey(400, 0);
            Assert.Empty(engineKeys);
            Assert.Contains(expectedKey, rcsKeys);
        }

        [Fact]
        public void BuildOrphanKeySets_MixedEvents_CollectsBothSets()
        {
            var events = new List<PartEvent>
            {
                new PartEvent { partPersistentId = 100, moduleIndex = 0, eventType = PartEventType.EngineIgnited },
                new PartEvent { partPersistentId = 300, moduleIndex = 0, eventType = PartEventType.RCSActivated },
                new PartEvent { partPersistentId = 100, moduleIndex = 0, eventType = PartEventType.EngineThrottle, value = 0.8f },
                new PartEvent { partPersistentId = 500, moduleIndex = 0, eventType = PartEventType.Decoupled } // ignored
            };

            HashSet<ulong> engineKeys, rcsKeys;
            GhostPlaybackLogic.BuildOrphanKeySets(events, out engineKeys, out rcsKeys);

            Assert.Single(engineKeys); // pid=100 counted once (dedup via HashSet)
            Assert.Single(rcsKeys);    // pid=300
        }

        [Fact]
        public void BuildOrphanKeySets_EngineShutdown_NotCollected()
        {
            // EngineShutdown must NOT prevent orphan detection — debris recordings
            // may have a shutdown event (engine burns out) but no ignited/throttle seed
            var events = new List<PartEvent>
            {
                new PartEvent { partPersistentId = 100, moduleIndex = 0, eventType = PartEventType.EngineShutdown }
            };

            HashSet<ulong> engineKeys, rcsKeys;
            GhostPlaybackLogic.BuildOrphanKeySets(events, out engineKeys, out rcsKeys);

            Assert.Empty(engineKeys); // Shutdown does NOT count as "has events"
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
            // A part with two engine modules (midx=0 and midx=1)
            ulong key0 = FlightRecorder.EncodeEngineKey(100, 0);
            ulong key1 = FlightRecorder.EncodeEngineKey(100, 1);
            var infoKeys = new List<ulong> { key0, key1 };
            var keysWithEvents = new HashSet<ulong> { key0 }; // only module 0 has events

            var orphans = GhostPlaybackLogic.FindOrphanKeys(infoKeys, keysWithEvents);

            Assert.Single(orphans);
            Assert.Contains(key1, orphans);
        }

        #endregion

        #region Integration: BuildOrphanKeySets + FindOrphanKeys

        [Fact]
        public void EndToEnd_DebrisBoosterPattern_IdentifiesOrphanEngine()
        {
            // Simulate a debris booster recording: one engine, no engine events at all
            ulong boosterEngineKey = FlightRecorder.EncodeEngineKey(545928558, 0);
            var events = new List<PartEvent>(); // empty — no seed events in debris recording

            HashSet<ulong> engineKeys, rcsKeys;
            GhostPlaybackLogic.BuildOrphanKeySets(events, out engineKeys, out rcsKeys);
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

            HashSet<ulong> engineKeys, rcsKeys;
            GhostPlaybackLogic.BuildOrphanKeySets(events, out engineKeys, out rcsKeys);
            var orphans = GhostPlaybackLogic.FindOrphanKeys(
                new List<ulong> { mainsailKey, boosterKey }, engineKeys);

            Assert.Empty(orphans);
        }

        #endregion
    }
}
