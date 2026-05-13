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

        #region ChainPromotionShouldEmitEngineSeeds

        // The Re-Fly fork bug: RewindInvoker creates a new fork recording with zero
        // PartEvents and calls FlightRecorder.StartRecording(isPromotion: true). The
        // recorder used to unconditionally skip seed-event emission on promotion,
        // leaving the fork's PartEvents empty on disk. On a subsequent Re-Fly the
        // fork was loaded as a ghost; BuildEngineEventKeySet returned an empty set
        // and AutoStartOrphanEnginePlayback lit every engine on the ghost at full
        // power. The gate below scopes the emit decision narrowly to engine events,
        // matching the orphan guard's contract: skip only when prior engine events
        // already cover the guard, so a recording that has LightOn / DeployableExtended
        // alone still gets engine sentinels emitted. Non-engine seeds remain skipped
        // on promotion to preserve the bug A / #263 FindLastInterestingUT invariant.

        [Fact]
        public void ChainPromotion_NullActiveRec_EmitsEngineSeeds()
        {
            // A chain promotion before any recording has been attached to the tree
            // (defensive null guard) — treat as empty and emit engine seeds.
            bool shouldEmit = FlightRecorder.ChainPromotionShouldEmitEngineSeeds(
                activeRec: null, out int engineEventCount, out int totalEventCount);

            Assert.True(shouldEmit);
            Assert.Equal(0, engineEventCount);
            Assert.Equal(0, totalEventCount);
        }

        [Fact]
        public void ChainPromotion_RecWithNullPartEvents_EmitsEngineSeeds()
        {
            // Recording exists but its PartEvents list is null — still empty, emit.
            var rec = new Recording { RecordingId = "rec_null_events" };
            rec.PartEvents = null;

            bool shouldEmit = FlightRecorder.ChainPromotionShouldEmitEngineSeeds(
                rec, out int engineEventCount, out int totalEventCount);

            Assert.True(shouldEmit);
            Assert.Equal(0, engineEventCount);
            Assert.Equal(0, totalEventCount);
        }

        [Fact]
        public void ChainPromotion_EmptyRec_EmitsEngineSeeds_ReFlyForkScenario()
        {
            // Re-Fly fork: RewindInvoker.AtomicMarkerWrite creates a fresh fork
            // recording with no events copied over (chain promotion intentionally
            // skips parent-event inheritance). Without engine seeds here ghost
            // playback sees zero engine events and the booster engine FX lights up.
            var reFlyFork = new Recording { RecordingId = "rec_152453a952804ee7b54f129bdfe2fdc1" };

            bool shouldEmit = FlightRecorder.ChainPromotionShouldEmitEngineSeeds(
                reFlyFork, out int engineEventCount, out int totalEventCount);

            Assert.True(shouldEmit);
            Assert.Equal(0, engineEventCount);
            Assert.Equal(0, totalEventCount);
        }

        [Fact]
        public void ChainPromotion_RecWithEngineEvents_SkipsSeeds_QuickloadResume()
        {
            // Quickload-resume path: the active recording was populated by the
            // pre-quicksave flight and already carries engine events. The orphan
            // guard is satisfied; emitting more engine seeds at the resume UT
            // would duplicate state and risk poisoning FindLastInterestingUT.
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

            bool shouldEmit = FlightRecorder.ChainPromotionShouldEmitEngineSeeds(
                resumedRec, out int engineEventCount, out int totalEventCount);

            Assert.False(shouldEmit);
            Assert.Equal(1, engineEventCount);
            Assert.Equal(2, totalEventCount);
        }

        [Fact]
        public void ChainPromotion_RecWithOnlyNonEngineEvents_EmitsEngineSeeds_LoneLightOn()
        {
            // The orphan guard only counts EngineIgnited / EngineThrottle / EngineShutdown.
            // A recording with a lone LightOn (or any combination of non-engine events)
            // still leaves BuildEngineEventKeySet empty, so a ghost with engines would
            // auto-start them. The engine-event-aware gate must emit seeds even when
            // the recording has other (non-engine) events.
            var rec = new Recording { RecordingId = "rec_lone_lighton" };
            rec.PartEvents.Add(new PartEvent
            {
                ut = 1.0,
                partPersistentId = 100,
                eventType = PartEventType.LightOn
            });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 2.0,
                partPersistentId = 200,
                eventType = PartEventType.DeployableExtended
            });

            bool shouldEmit = FlightRecorder.ChainPromotionShouldEmitEngineSeeds(
                rec, out int engineEventCount, out int totalEventCount);

            Assert.True(shouldEmit);
            Assert.Equal(0, engineEventCount);
            Assert.Equal(2, totalEventCount);
        }

        [Theory]
        [InlineData(PartEventType.EngineIgnited)]
        [InlineData(PartEventType.EngineThrottle)]
        [InlineData(PartEventType.EngineShutdown)]
        public void ChainPromotion_RecWithSingleEngineEvent_SkipsSeeds(PartEventType engineEvt)
        {
            // Any one of the three engine event types satisfies the orphan guard via
            // BuildEngineEventKeySet, so a single sentinel is enough to take the
            // skip branch — pins the engine-event-aware gate's contract.
            var rec = new Recording { RecordingId = "rec_single_engine_evt" };
            rec.PartEvents.Add(new PartEvent
            {
                ut = 50.0,
                partPersistentId = 2485666303,
                eventType = engineEvt,
                value = engineEvt == PartEventType.EngineThrottle ? 0.5f : 0f
            });

            bool shouldEmit = FlightRecorder.ChainPromotionShouldEmitEngineSeeds(
                rec, out int engineEventCount, out int totalEventCount);

            Assert.False(shouldEmit);
            Assert.Equal(1, engineEventCount);
            Assert.Equal(1, totalEventCount);
        }

        #endregion

        #region ResolveChainPromotionSeedUT

        // P2 round 2: EngineShutdown sentinels are NOT inert in
        // RecordingOptimizer.IsInertPartEventForTailTrim, so stamping them at the
        // current (promotion) UT would still move FindLastInterestingUT forward and
        // block boring-tail trim on an ordinary quickload-resume of an empty-engine
        // recording with live engine parts. ResolveChainPromotionSeedUT anchors the
        // sentinels at the recording's actual StartUT for any populated recording so
        // they can never outpace the recording's own data, while keeping currentUT
        // for genuinely fresh forks whose StartUT is not yet established.

        [Fact]
        public void SeedUT_NullActiveRec_ReturnsCurrentUT()
        {
            // Fresh-fork defensive null path: no recording attached yet, so the
            // current frame IS the fork's creation UT.
            double seedUT = FlightRecorder.ResolveChainPromotionSeedUT(
                activeRec: null, currentUT: 5000.0);

            Assert.Equal(5000.0, seedUT);
        }

        [Fact]
        public void SeedUT_EmptyRec_ReturnsCurrentUT_ReFlyForkScenario()
        {
            // Re-Fly fork at creation: no trajectory points, no track sections, so
            // Recording.StartUT falls back to ExplicitStartUT or 0.0. Current UT IS
            // the fork-start UT, so use it.
            var freshFork = new Recording { RecordingId = "rec_fresh_fork" };

            double seedUT = FlightRecorder.ResolveChainPromotionSeedUT(freshFork, currentUT: 128.27);

            Assert.Equal(128.27, seedUT);
        }

        [Fact]
        public void SeedUT_PopulatedRecBeingResumed_ReturnsRecordingStartUT()
        {
            // Quickload-resume of a recording that has accumulated trajectory points
            // but no engine events (e.g. a Re-Fly fork that coasted ballistic for
            // 30 minutes before F5/F9). StartUT is the fork's actual creation UT,
            // far in the past relative to the resume UT — anchor sentinels there so
            // FindLastInterestingUT cannot be pushed to the resume UT and block
            // boring-tail trim.
            var resumedFork = new Recording { RecordingId = "rec_resumed_empty_engine_fork" };
            resumedFork.Points.Add(new TrajectoryPoint { ut = 128.27 });
            resumedFork.Points.Add(new TrajectoryPoint { ut = 158.46 });

            double seedUT = FlightRecorder.ResolveChainPromotionSeedUT(
                resumedFork, currentUT: 2000.0);

            Assert.Equal(128.27, seedUT);
        }

        [Fact]
        public void SeedUT_RecordingStartUTAtCurrentUT_ReturnsCurrentUT()
        {
            // Edge case: brand-new recording whose first trajectory point landed
            // exactly at the current UT (fresh fork on first physics frame). Both
            // values are equivalent; the helper returns currentUT for stability.
            var rec = new Recording { RecordingId = "rec_simultaneous" };
            rec.Points.Add(new TrajectoryPoint { ut = 500.0 });

            double seedUT = FlightRecorder.ResolveChainPromotionSeedUT(rec, currentUT: 500.0);

            Assert.Equal(500.0, seedUT);
        }

        [Fact]
        public void SeedUT_RecordingStartUTInFuture_ReturnsCurrentUT()
        {
            // Defensive: a recording whose StartUT somehow lands ahead of currentUT
            // (e.g. ExplicitStartUT set further forward than the trajectory)
            // should never be used as the anchor — fall back to currentUT.
            var rec = new Recording
            {
                RecordingId = "rec_future_start",
                ExplicitStartUT = 5000.0
            };

            double seedUT = FlightRecorder.ResolveChainPromotionSeedUT(rec, currentUT: 1000.0);

            Assert.Equal(1000.0, seedUT);
        }

        [Fact]
        public void SeedUT_NegativeOrNaNRecordingStartUT_ReturnsCurrentUT()
        {
            // Defensive: degenerate StartUT values should never propagate as the
            // seed anchor. KSP UTs can technically be negative (pre-epoch debug
            // worlds), but treat them as unset for our purposes.
            var rec = new Recording
            {
                RecordingId = "rec_negative_start",
                ExplicitStartUT = -100.0
            };

            double seedUT = FlightRecorder.ResolveChainPromotionSeedUT(rec, currentUT: 200.0);

            Assert.Equal(200.0, seedUT);
        }

        #endregion
    }
}
