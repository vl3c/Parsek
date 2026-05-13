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

        // P2 round 2-3: EngineShutdown sentinels are NOT inert in
        // RecordingOptimizer.IsInertPartEventForTailTrim, so stamping them at the
        // current (promotion) UT would still move FindLastInterestingUT forward and
        // block boring-tail trim on an ordinary quickload-resume of an empty-engine
        // recording with live engine parts. ResolveChainPromotionSeedUT anchors the
        // sentinels at the recording's actual StartUT for any populated recording so
        // they can never outpace the recording's own data, while keeping currentUT
        // for genuinely fresh forks whose StartUT is not yet established.
        //
        // The discriminator is Recording.HasActualTrajectoryBounds, NOT a StartUT > 0
        // check: 0.0 is a valid KSP UT (sandbox-epoch starts, debug worlds), and a
        // recording with Points[0].ut == 0.0 must keep that anchor.

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
            // Recording.HasActualTrajectoryBounds is false. Current UT IS the
            // fork-start UT, so use it.
            var freshFork = new Recording { RecordingId = "rec_fresh_fork" };

            Assert.False(freshFork.HasActualTrajectoryBounds);
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

            Assert.True(resumedFork.HasActualTrajectoryBounds);
            double seedUT = FlightRecorder.ResolveChainPromotionSeedUT(
                resumedFork, currentUT: 2000.0);

            Assert.Equal(128.27, seedUT);
        }

        [Fact]
        public void SeedUT_PopulatedRecWithStartUTZero_ReturnsZero_SandboxEpochAnchor()
        {
            // Reviewer's P2 round-3 case: 0.0 is a legitimate KSP UT. A sandbox
            // game started recording immediately gets Points[0].ut == 0.0, so
            // Recording.StartUT == 0.0 is a real anchor, NOT the empty-recording
            // fallback. The discriminator is HasActualTrajectoryBounds (true here),
            // not the sign of StartUT.
            var sandboxStartRec = new Recording { RecordingId = "rec_sandbox_epoch" };
            sandboxStartRec.Points.Add(new TrajectoryPoint { ut = 0.0 });
            sandboxStartRec.Points.Add(new TrajectoryPoint { ut = 30.0 });

            Assert.True(sandboxStartRec.HasActualTrajectoryBounds);
            Assert.Equal(0.0, sandboxStartRec.StartUT);
            double seedUT = FlightRecorder.ResolveChainPromotionSeedUT(
                sandboxStartRec, currentUT: 500.0);

            Assert.Equal(0.0, seedUT);
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
            var rec = new Recording { RecordingId = "rec_future_start" };
            rec.Points.Add(new TrajectoryPoint { ut = 5000.0 });

            double seedUT = FlightRecorder.ResolveChainPromotionSeedUT(rec, currentUT: 1000.0);

            Assert.Equal(1000.0, seedUT);
        }

        [Fact]
        public void SeedUT_EmptyRecWithExplicitStartUTSet_ReturnsCurrentUT()
        {
            // A recording with ExplicitStartUT set but no actual trajectory data is
            // still empty in the sense that matters: there is no real anchor to
            // attach engine sentinels to. The ExplicitStartUT can legitimately be
            // set forward of the eventual trajectory, so trusting it as an anchor
            // would risk poisoning FindLastInterestingUT just like the resume-UT
            // case the StartUT anchor was meant to fix. Fall back to currentUT.
            var rec = new Recording
            {
                RecordingId = "rec_explicit_start_only",
                ExplicitStartUT = 100.0
            };

            Assert.False(rec.HasActualTrajectoryBounds);
            double seedUT = FlightRecorder.ResolveChainPromotionSeedUT(rec, currentUT: 200.0);

            Assert.Equal(200.0, seedUT);
        }

        #endregion

        #region Quickload trim x gate interaction

        // P2 round 4: the empty-engine gate must see the POST-trim active recording.
        // StartRecording calls PrepareQuickloadResumeStateIfNeeded (which calls
        // ParsekScenario.TrimRecordingPastUT) BEFORE ResetPartEventTrackingState so
        // engine events from the abandoned future (past the quickload cutoff UT) are
        // removed before the gate counts them. If the order were reversed the gate
        // could count an abandoned-future EngineIgnited, skip emission, and then the
        // trim would strip that event — leaving the resumed recording with zero
        // engine events and re-tripping the playback orphan-engine auto-start. This
        // tests the gate's contract on a recording that has been through that trim.

        [Fact]
        public void Trim_ThenGate_ResumedForkWithAbandonedFutureEngineEvents_EmitsSeeds()
        {
            // Scenario: a Re-Fly fork has accumulated trajectory points and an
            // EngineIgnited event during the abandoned future of the player's
            // earlier flight. The quickload resume cutoff lands BEFORE that engine
            // event. After TrimRecordingPastUT, the recording has no engine events
            // and the gate must decide to emit sentinels.
            var resumedFork = new Recording { RecordingId = "rec_abandoned_future_engine" };
            resumedFork.Points.Add(new TrajectoryPoint { ut = 128.27 });
            resumedFork.Points.Add(new TrajectoryPoint { ut = 158.46 });
            resumedFork.Points.Add(new TrajectoryPoint { ut = 200.0 });
            // Future EngineIgnited that the player abandoned at quickload time
            resumedFork.PartEvents.Add(new PartEvent
            {
                ut = 175.0,
                partPersistentId = 2485666303,
                eventType = PartEventType.EngineIgnited,
                value = 1.0f
            });

            // Pre-trim: gate would (incorrectly) see the future engine event
            bool preTrimShouldEmit = FlightRecorder.ChainPromotionShouldEmitEngineSeeds(
                resumedFork, out int preTrimEngineCount, out _);
            Assert.False(preTrimShouldEmit);
            Assert.Equal(1, preTrimEngineCount);

            // Trim at cutoff = quickload resume UT, dropping the future EngineIgnited
            const double cutoffUT = 160.0;
            bool mutated = ParsekScenario.TrimRecordingPastUT(resumedFork, cutoffUT);
            Assert.True(mutated);

            // Post-trim: gate correctly sees zero engine events and emits
            bool postTrimShouldEmit = FlightRecorder.ChainPromotionShouldEmitEngineSeeds(
                resumedFork, out int postTrimEngineCount, out _);
            Assert.True(postTrimShouldEmit);
            Assert.Equal(0, postTrimEngineCount);

            // And the seed UT correctly anchors at the recording's actual StartUT
            // (128.27 — the surviving first trajectory point), not at the resume UT.
            double seedUT = FlightRecorder.ResolveChainPromotionSeedUT(
                resumedFork, currentUT: 5000.0);
            Assert.Equal(128.27, seedUT);
        }

        [Fact]
        public void Trim_PreservesPreCutoffEngineEvents_GateStillSkips()
        {
            // Counterpart: when the abandoned future was preceded by a genuine
            // engine event INSIDE the surviving cutoff window, trim leaves that
            // event in place and the gate correctly takes the skip branch — no
            // late sentinels needed.
            var resumedRec = new Recording { RecordingId = "rec_preserved_engine_event" };
            resumedRec.Points.Add(new TrajectoryPoint { ut = 100.0 });
            resumedRec.Points.Add(new TrajectoryPoint { ut = 150.0 });
            resumedRec.Points.Add(new TrajectoryPoint { ut = 200.0 });
            // Real engine event from pre-quicksave flight (BEFORE cutoff)
            resumedRec.PartEvents.Add(new PartEvent
            {
                ut = 130.0,
                partPersistentId = 2485666303,
                eventType = PartEventType.EngineIgnited,
                value = 0.75f
            });
            // Abandoned-future engine event (AFTER cutoff)
            resumedRec.PartEvents.Add(new PartEvent
            {
                ut = 175.0,
                partPersistentId = 2485666303,
                eventType = PartEventType.EngineShutdown,
                value = 0f
            });

            ParsekScenario.TrimRecordingPastUT(resumedRec, cutoffUT: 160.0);

            // The pre-cutoff EngineIgnited survives; the post-cutoff EngineShutdown is gone
            Assert.Single(resumedRec.PartEvents);
            Assert.Equal(PartEventType.EngineIgnited, resumedRec.PartEvents[0].eventType);

            bool shouldEmit = FlightRecorder.ChainPromotionShouldEmitEngineSeeds(
                resumedRec, out int engineCount, out int totalCount);
            Assert.False(shouldEmit);
            Assert.Equal(1, engineCount);
            Assert.Equal(1, totalCount);
        }

        #endregion
    }
}
