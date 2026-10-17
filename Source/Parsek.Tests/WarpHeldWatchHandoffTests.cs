using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A watched ghost that ends under time warp keeps its watch on the held ghost until warp
    /// ends; only then does the watch end, the vessel spawn and the camera move to it. Before,
    /// the watch ended mid-warp and retargeted the camera onto the player's vessel while rails
    /// warp kept it packed, which the player saw as the view turning brown.
    /// </summary>
    [Collection("Sequential")]
    public class WarpHeldWatchHandoffTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public WarpHeldWatchHandoffTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            GhostPlaybackLogic.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.SetInstanceForTesting(null);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
            GhostPlaybackLogic.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.SetInstanceForTesting(null);
        }

        [Theory]
        [InlineData(true, true, true)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(false, false, false)]
        public void ShouldKeepWatchOnHeldGhostDuringWarp_NeedsAWatchedLiveGhost(
            bool isWatched, bool ghostWasActive, bool expected)
        {
            Assert.Equal(expected,
                ParsekPlaybackPolicy.ShouldKeepWatchOnHeldGhostDuringWarp(isWatched, ghostWasActive));
        }

        [Theory]
        [InlineData(true, 3, 3, "rec", "rec", true, true)]
        [InlineData(false, 3, 3, "rec", "rec", true, false)]   // warp over: the flush owns it
        [InlineData(true, 3, 4, "rec", "rec", true, false)]    // player watches something else
        [InlineData(true, 3, -1, "rec", "rec", true, false)]   // player left the watch
        [InlineData(true, 3, 3, "rec", "other", true, false)]  // not the warp-held watch
        [InlineData(true, 3, 3, "rec", null, true, false)]
        [InlineData(true, 3, 3, "rec", "rec", false, false)]   // no warp-end spawn queued
        [InlineData(true, -1, -1, "rec", "rec", true, false)]
        public void ShouldHoldWatchedGhostForWarp_TruthTable(
            bool warpActive, int heldIndex, int watchedIndex, string heldId, string warpHeldId,
            bool queued, bool expected)
        {
            Assert.Equal(expected, ParsekPlaybackPolicy.ShouldHoldWatchedGhostForWarp(
                warpActive, heldIndex, watchedIndex, heldId, warpHeldId, queued));
        }

        [Fact]
        public void DecideHeldGhostAction_WarpWatchHold_SkipsRetryAndTimeout()
        {
            var rec = MakeRecording("rec-hold");
            var committed = new List<Recording> { rec };
            var info = new HeldGhostInfo { holdStartTime = 0f, recordingId = rec.RecordingId };

            // Far past the 5 s timeout and the 1 s retry interval.
            Assert.Equal(HeldGhostAction.Timeout,
                ParsekPlaybackPolicy.DecideHeldGhostAction(0, info, committed, 60f, 5f));
            Assert.Equal(HeldGhostAction.Hold,
                ParsekPlaybackPolicy.DecideHeldGhostAction(0, info, committed, 60f, 5f,
                    holdForWarpWatch: true));

            // Every release still applies.
            rec.VesselSpawned = true;
            Assert.Equal(HeldGhostAction.ReleaseSpawned,
                ParsekPlaybackPolicy.DecideHeldGhostAction(0, info, committed, 60f, 5f,
                    holdForWarpWatch: true));
        }

        [Fact]
        public void WatchedGhostEndingUnderWarp_KeepsWatchUntilWarpEnds_ThenExitsSpawnsAndActivates()
        {
            var rec = MakeRecording("rec-rover");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            int index = IndexOf(rec);

            bool warp = true;
            float now = 30f;
            int watchedIndex = index;
            var calls = new List<string>();
            var engine = new GhostPlaybackEngine(null) { DestroyGhostResourcesOverrideForTesting = s => { } };
            var policy = new ParsekPlaybackPolicy(engine, NewHost())
            {
                IsWarpActiveOverrideForTesting = () => warp,
                CurrentRealTimeOverrideForTesting = () => now,
                CurrentUTOverrideForTesting = () => 3000.0,
                TimelineInactiveIdsOverrideForTesting = c => new Dictionary<string, TimelineInactiveReason>(),
                WatchedRecordingIndexOverrideForTesting = () => watchedIndex,
                ExitWatchModeOverrideForTesting = () =>
                {
                    calls.Add("exit-watch");
                    watchedIndex = -1;
                },
                SpawnVesselOrChainTipOverrideForTesting = (r, i) =>
                {
                    calls.Add("spawn");
                    r.VesselSpawned = true;
                    r.SpawnedVesselPersistentId = 110406740u;
                },
                DeferredActivateVesselOverrideForTesting = pid => calls.Add("activate:" + pid),
            };
            engine.ghostStates[index] = new GhostPlaybackState { vesselName = rec.VesselName };

            engine.RunPastEndFrameTailForTesting(index, rec, MakeFlags(rec), rec.EndUT + 1.0, queueCompletion: true);

            // Completion under warp: the watch stays on the held ghost, nothing spawns.
            Assert.Empty(calls);
            Assert.True(policy.heldGhosts.ContainsKey(index));
            Assert.True(engine.HasGhost(index));
            Assert.Equal(rec.RecordingId, policy.warpHeldWatchRecordingId);
            Assert.Contains(logLines, l => l.Contains("[Policy]")
                && l.Contains("Watch kept on held ghost during warp") && l.Contains("Mining Rover RC"));

            // Warp runs on well past the held-ghost timeout: no retry spawn, no release.
            for (int k = 1; k <= 3; k++)
            {
                now = 30f + ParsekPlaybackPolicy.HeldGhostTimeoutSeconds + k * 2f;
                policy.RetryHeldGhostSpawns();
            }
            Assert.Empty(calls);
            Assert.True(engine.HasGhost(index));
            Assert.DoesNotContain(logLines, l => l.Contains("Held ghost timed out"));

            // Warp ends: the flush leaves the watch, spawns, then activates the spawned vessel.
            warp = false;
            policy.FlushDeferredSpawns();
            Assert.Equal(new List<string> { "exit-watch", "spawn", "activate:110406740" }, calls);
            Assert.Null(policy.warpHeldWatchRecordingId);
            Assert.Null(policy.pendingWatchRecordingId);
            Assert.Contains(logLines, l => l.Contains("Warp ended - leaving the watch kept on held ghost")
                && l.Contains("reason=deferred-spawn"));

            // Same frame, the held ghost is released; the watch is already gone.
            policy.RetryHeldGhostSpawns();
            Assert.False(engine.HasGhost(index));
            Assert.False(policy.heldGhosts.ContainsKey(index));
            Assert.Equal(3, calls.Count);
        }

        [Fact]
        public void PlayerLeavesTheHeldWatchDuringWarp_SpawnsAtWarpEndWithoutSwitching()
        {
            var rec = MakeRecording("rec-left");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            int index = IndexOf(rec);

            bool warp = true;
            int watchedIndex = index;
            var calls = new List<string>();
            var engine = new GhostPlaybackEngine(null) { DestroyGhostResourcesOverrideForTesting = s => { } };
            var policy = new ParsekPlaybackPolicy(engine, NewHost())
            {
                IsWarpActiveOverrideForTesting = () => warp,
                CurrentRealTimeOverrideForTesting = () => 30f,
                CurrentUTOverrideForTesting = () => 3000.0,
                TimelineInactiveIdsOverrideForTesting = c => new Dictionary<string, TimelineInactiveReason>(),
                WatchedRecordingIndexOverrideForTesting = () => watchedIndex,
                ExitWatchModeOverrideForTesting = () => calls.Add("exit-watch"),
                SpawnVesselOrChainTipOverrideForTesting = (r, i) =>
                {
                    calls.Add("spawn");
                    r.VesselSpawned = true;
                    r.SpawnedVesselPersistentId = 4242u;
                },
                DeferredActivateVesselOverrideForTesting = pid => calls.Add("activate:" + pid),
            };
            engine.ghostStates[index] = new GhostPlaybackState { vesselName = rec.VesselName };
            engine.RunPastEndFrameTailForTesting(index, rec, MakeFlags(rec), rec.EndUT + 1.0, queueCompletion: true);

            // The player backs out of the watch while warp still runs.
            watchedIndex = -1;
            warp = false;
            policy.FlushDeferredSpawns();

            Assert.Equal(new List<string> { "spawn" }, calls);
            Assert.Contains(logLines, l => l.Contains("Deferred watch dropped") && l.Contains("rec-left"));
        }

        [Fact]
        public void WatchedCompletionUnderWarpWithNoGhost_ExitsTheWatchAtOnce()
        {
            var rec = MakeRecording("rec-no-ghost");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            int index = IndexOf(rec);

            int exits = 0;
            var engine = new GhostPlaybackEngine(null) { DestroyGhostResourcesOverrideForTesting = s => { } };
            var policy = new ParsekPlaybackPolicy(engine, NewHost())
            {
                IsWarpActiveOverrideForTesting = () => true,
                CurrentRealTimeOverrideForTesting = () => 30f,
                WatchedRecordingIndexOverrideForTesting = () => index,
                ExitWatchModeOverrideForTesting = () => exits++,
                SpawnVesselOrChainTipOverrideForTesting = (r, i) => { },
            };

            engine.RunPastEndFrameTailForTesting(index, rec, MakeFlags(rec), rec.EndUT + 1.0, queueCompletion: true);

            Assert.Equal(1, exits);
            Assert.Null(policy.warpHeldWatchRecordingId);
            Assert.Equal(rec.RecordingId, policy.pendingWatchRecordingId);
        }

        [Fact]
        public void ReleasingAWatchedHeldGhost_LeavesTheWatchBeforeTheGhostGoes()
        {
            var rec = MakeRecording("rec-release");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            int index = IndexOf(rec);

            int watchedIndex = index;
            var calls = new List<string>();
            var engine = new GhostPlaybackEngine(null) { DestroyGhostResourcesOverrideForTesting = s => { } };
            var policy = new ParsekPlaybackPolicy(engine, NewHost())
            {
                IsWarpActiveOverrideForTesting = () => false,
                CurrentRealTimeOverrideForTesting = () => 60f,
                CurrentUTOverrideForTesting = () => 3000.0,
                TimelineInactiveIdsOverrideForTesting = c => new Dictionary<string, TimelineInactiveReason>(),
                WatchedRecordingIndexOverrideForTesting = () => watchedIndex,
                ExitWatchModeOverrideForTesting = () =>
                {
                    calls.Add("exit-watch:hasGhost=" + engine.HasGhost(index));
                    watchedIndex = -1;
                },
                SpawnVesselOrChainTipOverrideForTesting = (r, i) => { },
            };
            engine.ghostStates[index] = new GhostPlaybackState { vesselName = rec.VesselName };
            policy.heldGhosts[index] = new HeldGhostInfo
            {
                holdStartTime = 0f,
                recordingId = rec.RecordingId,
                vesselName = rec.VesselName,
            };

            policy.RetryHeldGhostSpawns();   // past the timeout, not warp-held

            Assert.Equal(new List<string> { "exit-watch:hasGhost=True" }, calls);
            Assert.False(engine.HasGhost(index));
            Assert.Contains(logLines, l => l.Contains("Leaving watch before releasing held ghost")
                && l.Contains("held-spawn-timeout"));
        }

        private static Recording MakeRecording(string id)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "Mining Rover RC",
                VesselSnapshot = new ConfigNode("VESSEL"),
                TerminalStateValue = TerminalState.Landed,
            };
        }

        private static TrajectoryPlaybackFlags MakeFlags(Recording rec)
        {
            return new TrajectoryPlaybackFlags
            {
                recordingId = rec.RecordingId,
                needsSpawn = true,
                chainEndUT = rec.EndUT,
            };
        }

        private static ParsekFlight NewHost()
        {
            return (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
        }

        private static int IndexOf(Recording rec)
        {
            var committed = RecordingStore.CommittedRecordings;
            for (int k = 0; k < committed.Count; k++)
                if (ReferenceEquals(committed[k], rec)) return k;
            return -1;
        }
    }
}
