using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Loop first-run-is-real (design 2.2 principle 9, 12.7; operator ruling 2026-09-23 on
    /// LOOP-ARMED-REWIND-LEAVES-ZERO-VESSELS): the first chronological run of a looping
    /// recording is the real run and spawns its terminal vessel once; every later loop cycle
    /// is ghost-only. Covers the pure predicates and a frame-by-frame model that chains the
    /// same real gates the flight engine and the Space Center wire together
    /// (PlaybackScopeTracker, ResolveHistoricalNeverReplayed, ShouldSpawnAtRecordingEnd,
    /// ShouldAttemptLoopFirstRunSpawn and a one-shot latch).
    /// </summary>
    [Collection("Sequential")]
    public class LoopFirstRunSpawnTests : IDisposable
    {
        public LoopFirstRunSpawnTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            PlaybackScopeTracker.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            PlaybackScopeTracker.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        #region Pure predicates

        [Fact]
        public void ShouldAttemptLoopFirstRunSpawn_LoopOff_ReturnsFalse()
        {
            // A non-looping index is owned by the ordinary past-end completion.
            Assert.False(GhostPlaybackLogic.ShouldAttemptLoopFirstRunSpawn(
                loopDriven: false, spawnEligible: true, currentUT: 2001,
                recordingEndUT: 2000, alreadyAttempted: false));
        }

        [Fact]
        public void ShouldAttemptLoopFirstRunSpawn_LoopingPastEndEligible_ReturnsTrue()
        {
            Assert.True(GhostPlaybackLogic.ShouldAttemptLoopFirstRunSpawn(
                loopDriven: true, spawnEligible: true, currentUT: 2000.01,
                recordingEndUT: 2000, alreadyAttempted: false));
        }

        [Fact]
        public void ShouldAttemptLoopFirstRunSpawn_AtOrBeforeEnd_ReturnsFalse()
        {
            // Strict past-end, the same comparison as the ordinary completion.
            Assert.False(GhostPlaybackLogic.ShouldAttemptLoopFirstRunSpawn(
                loopDriven: true, spawnEligible: true, currentUT: 2000,
                recordingEndUT: 2000, alreadyAttempted: false));
            Assert.False(GhostPlaybackLogic.ShouldAttemptLoopFirstRunSpawn(
                loopDriven: true, spawnEligible: true, currentUT: 1500,
                recordingEndUT: 2000, alreadyAttempted: false));
        }

        [Fact]
        public void ShouldAttemptLoopFirstRunSpawn_AlreadyAttempted_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldAttemptLoopFirstRunSpawn(
                loopDriven: true, spawnEligible: true, currentUT: 2001,
                recordingEndUT: 2000, alreadyAttempted: true));
        }

        [Fact]
        public void ShouldAttemptLoopFirstRunSpawn_NotEligible_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldAttemptLoopFirstRunSpawn(
                loopDriven: true, spawnEligible: false, currentUT: 2001,
                recordingEndUT: 2000, alreadyAttempted: false));
        }

        [Fact]
        public void ResolveHistoricalNeverReplayed_LoopRenderIsExempt_LoopSpawnIsGated()
        {
            // The loop's RENDER ignores the replay-scope gate (explicit live opt-in) ...
            Assert.False(GhostPlaybackLogic.ResolveHistoricalNeverReplayed(
                loopingLike: true, reFlyActive: false, scopeHistorical: true, forSpawn: false));
            // ... but its first-run SPAWN takes the same gate as any recording.
            Assert.True(GhostPlaybackLogic.ResolveHistoricalNeverReplayed(
                loopingLike: true, reFlyActive: false, scopeHistorical: true, forSpawn: true));
        }

        [Fact]
        public void ResolveHistoricalNeverReplayed_NonLoop_SameForRenderAndSpawn()
        {
            Assert.True(GhostPlaybackLogic.ResolveHistoricalNeverReplayed(
                loopingLike: false, reFlyActive: false, scopeHistorical: true, forSpawn: false));
            Assert.True(GhostPlaybackLogic.ResolveHistoricalNeverReplayed(
                loopingLike: false, reFlyActive: false, scopeHistorical: true, forSpawn: true));
            Assert.False(GhostPlaybackLogic.ResolveHistoricalNeverReplayed(
                loopingLike: false, reFlyActive: false, scopeHistorical: false, forSpawn: true));
        }

        [Fact]
        public void ResolveHistoricalNeverReplayed_ReFlyActive_ExemptFromBoth()
        {
            Assert.False(GhostPlaybackLogic.ResolveHistoricalNeverReplayed(
                loopingLike: true, reFlyActive: true, scopeHistorical: true, forSpawn: true));
            Assert.False(GhostPlaybackLogic.ResolveHistoricalNeverReplayed(
                loopingLike: false, reFlyActive: true, scopeHistorical: true, forSpawn: false));
        }

        #endregion

        #region Engine seam (GhostPlaybackEngine.TryQueueLoopFirstRunSpawn)

        private static TrajectoryPlaybackFlags SpawnableFlags(double chainEndUT = 2000)
        {
            return new TrajectoryPlaybackFlags
            {
                needsSpawn = true,
                chainEndUT = chainEndUT,
                recordingId = "lf-rec",
            };
        }

        private static int QueueFrames(GhostPlaybackEngine engine, MockTrajectory traj,
            TrajectoryPlaybackFlags flags, params double[] uts)
        {
            int queued = 0;
            for (int k = 0; k < uts.Length; k++)
            {
                var ctx = new FrameContext { currentUT = uts[k], warpRate = 1f };
                if (engine.TryQueueLoopFirstRunSpawn(0, traj, flags, ctx,
                        GhostPlaybackEngine.ResolveGhostActivationStartUT(traj), hasPointData: true))
                    queued++;
            }
            return queued;
        }

        [Fact]
        public void EngineSeam_LoopingTrajectory_QueuesOneMarkedSpawnOnlyCompletion()
        {
            var engine = new GhostPlaybackEngine(positioner: null);
            var traj = new MockTrajectory().WithTimeRange(1000, 2000).WithLoop();

            Assert.Equal(1, QueueFrames(engine, traj, SpawnableFlags(),
                1500, 2000, 2000.5, 2400, 3100, 3800, 4500));

            var queued = engine.PendingCompletedEventsForTesting;
            Assert.Single(queued);
            Assert.True(queued[0].LoopFirstRun);
            Assert.False(queued[0].GhostWasActive);
            Assert.Null(queued[0].State);
            Assert.True(queued[0].PastEffectiveEnd);
        }

        [Fact]
        public void EngineSeam_NonLoopingTrajectory_LeavesTheOrdinaryCompletion()
        {
            // Mirror direction: a non-looping trajectory must not get a second completion.
            var engine = new GhostPlaybackEngine(positioner: null);
            var traj = new MockTrajectory().WithTimeRange(1000, 2000);

            Assert.Equal(0, QueueFrames(engine, traj, SpawnableFlags(), 2000.5, 2400));
            Assert.Empty(engine.PendingCompletedEventsForTesting);
        }

        [Fact]
        public void EngineSeam_PlayheadBeforeRecording_RearmsTheLatch()
        {
            // A rewind drops the playhead before the recording: the next first run fires again
            // (the policy's VesselSpawned gate decides whether a vessel actually results).
            var engine = new GhostPlaybackEngine(positioner: null);
            var traj = new MockTrajectory().WithTimeRange(1000, 2000).WithLoop();

            Assert.Equal(1, QueueFrames(engine, traj, SpawnableFlags(), 2000.5, 2600));
            Assert.Equal(1, QueueFrames(engine, traj, SpawnableFlags(), 900, 1500, 2000.5, 2600));
        }

        [Fact]
        public void EngineSeam_NotSpawnableOrMidChain_QueuesNothing()
        {
            var engine = new GhostPlaybackEngine(positioner: null);
            var traj = new MockTrajectory().WithTimeRange(1000, 2000).WithLoop();
            var notDue = SpawnableFlags();
            notDue.needsSpawn = false;
            var midChain = SpawnableFlags();
            midChain.isMidChain = true;

            Assert.Equal(0, QueueFrames(engine, traj, notDue, 2000.5, 2600));
            Assert.Equal(0, QueueFrames(engine, traj, midChain, 2000.5, 2600));
        }

        [Fact]
        public void EngineSeam_WaitsForTheChainEffectiveEnd()
        {
            // A side-branch leaf whose chain's branch 0 ends later: the policy spawns only past
            // the effective end, so the one-shot latch must not be spent before it.
            var engine = new GhostPlaybackEngine(positioner: null);
            var traj = new MockTrajectory().WithTimeRange(1000, 2000).WithLoop();

            Assert.Equal(0, QueueFrames(engine, traj, SpawnableFlags(chainEndUT: 2500), 2000.5, 2400));
            Assert.Equal(1, QueueFrames(engine, traj, SpawnableFlags(chainEndUT: 2500), 2500.5));
            Assert.True(engine.PendingCompletedEventsForTesting[0].PastEffectiveEnd);
        }

        [Fact]
        public void IsWatchedCompletion_LoopFirstRunNeverEndsTheWatchedGhost()
        {
            var ordinary = new PlaybackCompletedEvent { Index = 3 };
            var loopFirstRun = new PlaybackCompletedEvent { Index = 3, LoopFirstRun = true };

            Assert.True(ParsekPlaybackPolicy.IsWatchedCompletion(3, ordinary));
            Assert.False(ParsekPlaybackPolicy.IsWatchedCompletion(3, loopFirstRun));
            Assert.False(ParsekPlaybackPolicy.IsWatchedCompletion(4, ordinary));
        }

        #endregion

        #region Lifecycle model

        /// <summary>
        /// One scene's worth of the first-run wiring. The latch is per-scene state (the flight
        /// engine's loopFirstRunSpawnFired, the Space Center's kscSpawnAttempted): a new
        /// instance is what a scene reload or a save/load produces. Each Frame returns true
        /// when the first-run spawn fires, and a fired spawn that succeeds sets
        /// VesselSpawned, exactly as the policy's SpawnVesselOrChainTip does.
        /// </summary>
        private sealed class FirstRunModel
        {
            private readonly HashSet<string> latch = new HashSet<string>();
            internal int Spawns;
            internal bool SpawnSucceeds = true;

            internal bool Frame(Recording rec, bool loopDriven, double ut)
            {
                double activationStartUT = rec.StartUT;
                PlaybackScopeTracker.NotePlayhead(rec.RecordingId, ut, activationStartUT);
                if (ut < activationStartUT)
                    latch.Remove(rec.RecordingId);

                bool spawnHistorical = GhostPlaybackLogic.ResolveHistoricalNeverReplayed(
                    loopDriven, reFlyActive: false,
                    scopeHistorical: PlaybackScopeTracker.IsHistoricalNeverReplayed(
                        rec.RecordingId, ut, activationStartUT),
                    forSpawn: true);
                var gate = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                    rec, isActiveChainMember: false, isChainLooping: false,
                    treeContext: null, liveSameLaunchVesselPresent: false);
                bool eligible = gate.needsSpawn && !spawnHistorical;

                if (!GhostPlaybackLogic.ShouldAttemptLoopFirstRunSpawn(
                        loopDriven, eligible, ut, rec.EndUT, latch.Contains(rec.RecordingId)))
                    return false;

                latch.Add(rec.RecordingId);
                Spawns++;
                if (SpawnSucceeds)
                {
                    rec.VesselSpawned = true;
                    rec.SpawnedVesselPersistentId = 900000u + (uint)Spawns;
                }
                return true;
            }

            internal int RunFrames(Recording rec, bool loopDriven, params double[] uts)
            {
                int fired = 0;
                for (int i = 0; i < uts.Length; i++)
                    if (Frame(rec, loopDriven, uts[i])) fired++;
                return fired;
            }
        }

        private static Recording MakeLoopSubject()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "LANDED");
            snapshot.AddValue("type", "Probe");
            snapshot.AddValue("name", "Logi Cargo Rig");
            return new Recording
            {
                RecordingId = "lf-rec",
                VesselName = "Logi Cargo Rig",
                VesselPersistentId = 4242,
                ExplicitStartUT = 1000,
                ExplicitEndUT = 2000,
                VesselSnapshot = snapshot,
                TerminalStateValue = TerminalState.Landed,
                PlaybackEnabled = true,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 1000, bodyName = "Kerbin", latitude = -0.09, longitude = -74.55, altitude = 70 },
                    new TrajectoryPoint { ut = 2000, bodyName = "Kerbin", latitude = -0.09, longitude = -74.55, altitude = 70 }
                }
            };
        }

        // Real playhead samples: before launch, inside the first run, just past its end, then
        // three loop cycles' worth of later frames (a loop cycle replays on its own clock; the
        // real playhead only moves forward past EndUT).
        private static readonly double[] FirstRunThenThreeCycles =
            { 990, 1500, 2000, 2000.5, 2400, 3100, 3800, 4500 };

        [Fact]
        public void LoopOff_NeverFiresTheFirstRunSeam()
        {
            // Mirror direction: a non-looping recording keeps the ordinary completion path.
            var rec = MakeLoopSubject();
            var model = new FirstRunModel();

            Assert.Equal(0, model.RunFrames(rec, loopDriven: false, FirstRunThenThreeCycles));
            Assert.False(rec.VesselSpawned);
        }

        [Fact]
        public void LoopArmedBeforeFirstRun_SpawnsExactlyOnceAcrossThreeCycles()
        {
            // The ruling: a loop armed before the real flight's window is crossed (the
            // loop-armed Rewind-to-Launch) still spawns the real vessel, once.
            var rec = MakeLoopSubject();
            var model = new FirstRunModel();

            Assert.Equal(1, model.RunFrames(rec, loopDriven: true, FirstRunThenThreeCycles));
            Assert.True(rec.VesselSpawned);
            Assert.Equal(1, model.Spawns);
        }

        [Fact]
        public void LoopArmedAfterFirstRun_AdoptedAtCommit_NeverSpawns()
        {
            // The ordinary forward-play loop: the commit adopted the live vessel
            // (VesselSpawned=true), then the player armed the loop. No cycle may spawn.
            var rec = MakeLoopSubject();
            rec.VesselSpawned = true;
            rec.SpawnedVesselPersistentId = 4242;
            var model = new FirstRunModel();

            Assert.Equal(0, model.RunFrames(rec, loopDriven: true, 2100, 2800, 3500, 4200));
            Assert.Equal(4242u, rec.SpawnedVesselPersistentId);
        }

        [Fact]
        public void LoopArmed_HistoricalNeverReplayed_DoesNotSpawn()
        {
            // BUG-B mirror: a recording the playhead only ever moved forward past is
            // historical. Looping lets it RENDER, but its spawn stays dormant like any
            // recording's (red if the spawn half of the gate exempts loops again).
            var rec = MakeLoopSubject();
            var model = new FirstRunModel();

            Assert.Equal(0, model.RunFrames(rec, loopDriven: true, 2100, 2800, 3500, 4200));
            Assert.False(rec.VesselSpawned);
        }

        [Fact]
        public void RewindWhileArmed_StripRearmsExactlyOneSpawn()
        {
            var rec = MakeLoopSubject();
            var model = new FirstRunModel();
            Assert.Equal(1, model.RunFrames(rec, loopDriven: true, FirstRunThenThreeCycles));
            uint firstPid = rec.SpawnedVesselPersistentId;

            // Rewind-to-Launch strip: the spawned vessel is removed and the recording's spawn
            // state reset (ReconcileSpawnStateAfterStrip); the playhead drops before launch.
            rec.VesselSpawned = false;
            rec.SpawnedVesselPersistentId = 0;

            Assert.Equal(1, model.RunFrames(rec, loopDriven: true, FirstRunThenThreeCycles));
            Assert.True(rec.VesselSpawned);
            Assert.NotEqual(firstPid, rec.SpawnedVesselPersistentId);
            Assert.Equal(2, model.Spawns);
        }

        [Fact]
        public void RewindWhileArmed_NoStrip_DoesNotDuplicate()
        {
            // A rewind that left the real vessel standing (VesselSpawned still true) must
            // not add a second copy when the playhead re-crosses EndUT.
            var rec = MakeLoopSubject();
            var model = new FirstRunModel();
            Assert.Equal(1, model.RunFrames(rec, loopDriven: true, FirstRunThenThreeCycles));

            Assert.Equal(0, model.RunFrames(rec, loopDriven: true, FirstRunThenThreeCycles));
            Assert.Equal(1, model.Spawns);
        }

        [Fact]
        public void ReloadWhileArmed_PersistedSpawnState_DoesNotDuplicate()
        {
            var rec = MakeLoopSubject();
            Assert.Equal(1, new FirstRunModel().RunFrames(rec, loopDriven: true, FirstRunThenThreeCycles));

            // Save/load: a fresh scene (empty latch) reads VesselSpawned back from the save.
            var reloaded = new FirstRunModel();
            Assert.Equal(0, reloaded.RunFrames(rec, loopDriven: true, 4600, 5300, 6000));

            // A return to the main menu also clears the scope latch; still no duplicate.
            PlaybackScopeTracker.Reset();
            Assert.Equal(0, new FirstRunModel().RunFrames(rec, loopDriven: true, 6100, 6800));
        }

        [Fact]
        public void BlockedSpawn_IsAttemptedOncePerRunNotPerCycle()
        {
            // A spawn that does not land (VesselSpawned stays false) must not be retried on
            // every loop cycle; the one-shot latch holds until the playhead returns before
            // the recording.
            var rec = MakeLoopSubject();
            var model = new FirstRunModel { SpawnSucceeds = false };

            Assert.Equal(1, model.RunFrames(rec, loopDriven: true, FirstRunThenThreeCycles));
            Assert.False(rec.VesselSpawned);
            Assert.Equal(1, model.RunFrames(rec, loopDriven: true, FirstRunThenThreeCycles));
        }

        [Fact]
        public void SpaceCenterGate_LoopingStandalone_FirstRunEligible_ThenAlreadySpawned()
        {
            // The KSC path feeds ShouldSpawnAtKscEnd after the first-run seam; a looping
            // standalone recording is eligible once, then VesselSpawned holds every cycle.
            var rec = MakeLoopSubject();
            rec.LoopPlayback = true;

            Assert.True(GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, rec.EndUT + 1).needsSpawn);
            rec.VesselSpawned = true;
            var later = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, rec.EndUT + 500);
            Assert.False(later.needsSpawn);
            Assert.Contains("already spawned", later.reason);
        }

        #endregion
    }
}
