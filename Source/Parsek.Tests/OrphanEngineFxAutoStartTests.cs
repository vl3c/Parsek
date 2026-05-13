using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for orphan engine FX auto-start logic (#298).
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
        public void BuildEngineEventKeySet_EngineShutdown_Collected()
        {
            // EngineShutdown IS counted as "has events" — dead-engine sentinel seeds
            // (#298) use EngineShutdown to prevent the Count==0 auto-start heuristic
            // from firing on debris with depleted-fuel engines.
            var events = new List<PartEvent>
            {
                new PartEvent { partPersistentId = 100, moduleIndex = 0, eventType = PartEventType.EngineShutdown }
            };

            var keys = GhostPlaybackLogic.BuildEngineEventKeySet(events);
            Assert.Single(keys);
        }

        #endregion

        #region Integration

        [Fact]
        public void EndToEnd_DebrisBoosterPattern_ZeroEventsTriggersAutoStart()
        {
            // Simulate a debris booster recording: one engine, no engine events at all
            var events = new List<PartEvent>(); // empty — no seed events in debris recording

            var engineKeys = GhostPlaybackLogic.BuildEngineEventKeySet(events);

            // Zero engine events = pure debris pattern → Count==0 triggers auto-start
            Assert.Empty(engineKeys);
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

        [Fact]
        public void EndToEnd_ZeroThrottleSeededEngine_ShutdownSentinelPreventsAutoStart()
        {
            var sets = new PartTrackingSets();
            uint pid = 4200;
            ulong key = FlightRecorder.EncodeEngineKey(pid, 0);
            sets.activeEngineKeys.Add(key);
            var names = new Dictionary<uint, string> { { pid, "solidBooster" } };

            var events = PartStateSeeder.EmitSeedEvents(sets, names, 20000.0, "Test");
            var engineKeys = GhostPlaybackLogic.BuildEngineEventKeySet(events);

            Assert.Contains(events, e => e.eventType == PartEventType.EngineShutdown && e.partPersistentId == pid);
            Assert.Contains(key, engineKeys);
        }

        #endregion

        #region ChainPromotionShouldEmitSeedEvents

        // The Re-Fly fork bug: RewindInvoker creates a new fork recording with zero
        // PartEvents and calls FlightRecorder.StartRecording(isPromotion: true). Before
        // this fix the recorder unconditionally skipped seed-event emission on
        // promotion, leaving the fork's PartEvents empty on disk. On a subsequent
        // Re-Fly the fork was loaded as a ghost; BuildEngineEventKeySet returned an
        // empty set and AutoStartOrphanEnginePlayback lit every engine on the ghost
        // at full power. The data-driven gate below mirrors
        // BackgroundRecorder.TrySeedLoadedPartEvents: skip only when prior events
        // already exist.

        [Fact]
        public void ChainPromotion_NullActiveRec_EmitsSeeds()
        {
            // A chain promotion before any recording has been attached to the tree
            // (defensive null guard) — treat as empty and seed.
            bool shouldEmit = FlightRecorder.ChainPromotionShouldEmitSeedEvents(
                activeRec: null, out int activeRecEventCount);

            Assert.True(shouldEmit);
            Assert.Equal(0, activeRecEventCount);
        }

        [Fact]
        public void ChainPromotion_RecWithNullPartEvents_EmitsSeeds()
        {
            // Recording exists but its PartEvents list is null — still empty, seed.
            var rec = new Recording { RecordingId = "rec_null_events" };
            rec.PartEvents = null;

            bool shouldEmit = FlightRecorder.ChainPromotionShouldEmitSeedEvents(
                rec, out int activeRecEventCount);

            Assert.True(shouldEmit);
            Assert.Equal(0, activeRecEventCount);
        }

        [Fact]
        public void ChainPromotion_EmptyRec_EmitsSeeds_ReFlyForkScenario()
        {
            // Re-Fly fork: RewindInvoker.AtomicMarkerWrite creates a fresh fork
            // recording with no events copied over (chain promotion intentionally
            // skips parent-event inheritance). Without seeds here ghost playback
            // sees zero engine events and the booster engine FX lights up.
            var refluFork = new Recording { RecordingId = "rec_152453a952804ee7b54f129bdfe2fdc1" };

            bool shouldEmit = FlightRecorder.ChainPromotionShouldEmitSeedEvents(
                refluFork, out int activeRecEventCount);

            Assert.True(shouldEmit);
            Assert.Equal(0, activeRecEventCount);
        }

        [Fact]
        public void ChainPromotion_RecWithPriorEvents_SkipsSeeds_QuickloadResume()
        {
            // Quickload-resume path: the active recording was populated by the
            // pre-quicksave flight. Re-emitting seeds at the resume UT would poison
            // FindLastInterestingUT and block boring-tail trimming (bug A / #263).
            var resumedRec = new Recording { RecordingId = "rec_quickload_resume" };
            resumedRec.PartEvents.Add(new PartEvent
            {
                ut = 100.0,
                partPersistentId = 12345,
                eventType = PartEventType.DeployableExtended
            });
            resumedRec.PartEvents.Add(new PartEvent
            {
                ut = 200.0,
                partPersistentId = 67890,
                eventType = PartEventType.EngineThrottle,
                value = 0.5f
            });

            bool shouldEmit = FlightRecorder.ChainPromotionShouldEmitSeedEvents(
                resumedRec, out int activeRecEventCount);

            Assert.False(shouldEmit);
            Assert.Equal(2, activeRecEventCount);
        }

        [Fact]
        public void ChainPromotion_RecWithSingleEvent_SkipsSeeds()
        {
            // Edge case: even one prior event means the recording is no longer empty.
            // Background promotion (PromoteRecordingFromBackground) lands here when
            // the BG recorder has flushed at least one event.
            var rec = new Recording { RecordingId = "rec_bg_promote" };
            rec.PartEvents.Add(new PartEvent
            {
                ut = 1.0,
                partPersistentId = 100,
                eventType = PartEventType.LightOn
            });

            bool shouldEmit = FlightRecorder.ChainPromotionShouldEmitSeedEvents(
                rec, out int activeRecEventCount);

            Assert.False(shouldEmit);
            Assert.Equal(1, activeRecEventCount);
        }

        #endregion
    }
}
