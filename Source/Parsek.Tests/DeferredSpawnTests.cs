using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class DeferredSpawnTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public DeferredSpawnTests()
        {
            RecordingStore.ResetForTesting();
            GhostPlaybackLogic.ResetForTesting();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.TestSinkForTesting = null;
            ParsekLog.VerboseOverrideForTesting = false;
            RecordingStore.ResetForTesting();
            GhostPlaybackLogic.ResetForTesting();
        }

        #region ShouldFlushDeferredSpawns

        [Fact]
        public void ShouldFlush_PendingAndWarpInactive_ReturnsTrue()
        {
            Assert.True(GhostPlaybackLogic.ShouldFlushDeferredSpawns(3, false));
        }

        [Fact]
        public void ShouldFlush_PendingAndWarpActive_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldFlushDeferredSpawns(3, true));
        }

        [Fact]
        public void ShouldFlush_EmptyAndWarpInactive_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldFlushDeferredSpawns(0, false));
        }

        [Fact]
        public void ShouldFlush_EmptyAndWarpActive_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldFlushDeferredSpawns(0, true));
        }

        #endregion

        #region ShouldSkipDeferredSpawn

        [Fact]
        public void ShouldSkip_AlreadySpawned_ReturnsTrue()
        {
            Assert.True(GhostPlaybackLogic.ShouldSkipDeferredSpawn(true, true));
        }

        [Fact]
        public void ShouldSkip_NoSnapshot_ReturnsTrue()
        {
            Assert.True(GhostPlaybackLogic.ShouldSkipDeferredSpawn(false, false));
        }

        [Fact]
        public void ShouldSkip_SpawnedAndNoSnapshot_ReturnsTrue()
        {
            Assert.True(GhostPlaybackLogic.ShouldSkipDeferredSpawn(true, false));
        }

        [Fact]
        public void ShouldSkip_NotSpawnedWithSnapshot_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldSkipDeferredSpawn(false, true));
        }

        #endregion

        #region ShouldRestoreWatchMode

        [Fact]
        public void ShouldRestoreWatch_MatchingIdWithPid_ReturnsTrue()
        {
            Assert.True(GhostPlaybackLogic.ShouldRestoreWatchMode("rec-123", "rec-123", 42000));
        }

        [Fact]
        public void ShouldRestoreWatch_MatchingIdZeroPid_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldRestoreWatchMode("rec-123", "rec-123", 0));
        }

        [Fact]
        public void ShouldRestoreWatch_DifferentId_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldRestoreWatchMode("rec-123", "rec-456", 42000));
        }

        [Fact]
        public void ShouldRestoreWatch_NullPendingId_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldRestoreWatchMode(null, "rec-123", 42000));
        }

        [Fact]
        public void ShouldRestoreWatch_BothNull_ReturnsFalse()
        {
            // null == null is true in C#, but pid must be non-zero
            Assert.False(GhostPlaybackLogic.ShouldRestoreWatchMode(null, null, 0));
        }

        [Fact]
        public void ShouldRestoreWatch_BothNullWithPid_ReturnsFalse()
        {
            // Defensive: null pendingWatchId means no watch was active
            Assert.False(GhostPlaybackLogic.ShouldRestoreWatchMode(null, null, 42000));
        }

        [Fact]
        public void SpawnFlagVesselsUpToUT_SkipsExistingFlags_AndStopsAtFutureBoundary()
        {
            var rec = new Recording
            {
                RecordingId = "rec-flags",
                VesselName = "Flag Carrier",
                FlagEvents = new List<FlagEvent>
                {
                    new FlagEvent { ut = 100.0, flagSiteName = "existing" },
                    new FlagEvent { ut = 110.0, flagSiteName = "missing" },
                    new FlagEvent { ut = 130.0, flagSiteName = "future" }
                }
            };
            var spawnedFlags = new List<string>();

            GhostPlaybackLogic.SetFlagExistsOverrideForTesting(evt => evt.flagSiteName == "existing");
            GhostPlaybackLogic.SetSpawnFlagOverrideForTesting(evt =>
            {
                spawnedFlags.Add(evt.flagSiteName);
                return true;
            });

            var result = GhostPlaybackLogic.SpawnFlagVesselsUpToUT(rec, 120.0);

            Assert.Equal(2, result.eligibleCount);
            Assert.Equal(1, result.spawnedCount);
            Assert.Equal(1, result.alreadyPresentCount);
            Assert.Equal(0, result.failedCount);
            Assert.Equal(new[] { "missing" }, spawnedFlags);
        }

        [Fact]
        public void FlushDeferredSpawns_SpawnsEligibleFlagEvents_ForSpawnedRecording()
        {
            var rec = new Recording
            {
                RecordingId = "rec-deferred-flags",
                VesselName = "Bob Kerman",
                VesselSnapshot = new ConfigNode("VESSEL"),
                FlagEvents = new List<FlagEvent>
                {
                    new FlagEvent { ut = 119.0, flagSiteName = "a" },
                    new FlagEvent { ut = 130.0, flagSiteName = "future" }
                }
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var flushedFlags = new List<string>();
            GhostPlaybackLogic.SetFlagExistsOverrideForTesting(_ => false);
            GhostPlaybackLogic.SetSpawnFlagOverrideForTesting(evt =>
            {
                flushedFlags.Add(evt.flagSiteName);
                return true;
            });

            var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            var engine = new GhostPlaybackEngine(null);
            var policy = new ParsekPlaybackPolicy(engine, host)
            {
                IsWarpActiveOverrideForTesting = () => false,
                CurrentUTOverrideForTesting = () => 120.0,
                HasActiveVesselOverrideForTesting = () => false,
                SpawnVesselOrChainTipOverrideForTesting = (recording, index) =>
                {
                    recording.VesselSpawned = true;
                    recording.SpawnedVesselPersistentId = 4242;
                }
            };
            policy.pendingSpawnRecordingIds.Add(rec.RecordingId);

            policy.FlushDeferredSpawns();

            Assert.Equal(new[] { "a" }, flushedFlags);
            Assert.DoesNotContain(rec.RecordingId, policy.pendingSpawnRecordingIds);
            Assert.Contains(logLines, line =>
                line.Contains("[Policy]") &&
                line.Contains("Deferred flag flush") &&
                line.Contains("Bob Kerman") &&
                line.Contains("spawned 1/1 flag(s)") &&
                line.Contains("failed=0"));
        }

        [Fact]
        public void FlushDeferredSpawns_FailedFlagReplay_StaysQueuedUntilRetrySucceeds()
        {
            var rec = new Recording
            {
                RecordingId = "rec-deferred-flags-retry",
                VesselName = "Retry Kerbal",
                VesselSnapshot = new ConfigNode("VESSEL"),
                FlagEvents = new List<FlagEvent>
                {
                    new FlagEvent { ut = 119.0, flagSiteName = "retry-flag" }
                }
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            int spawnAttempts = 0;
            GhostPlaybackLogic.SetFlagExistsOverrideForTesting(_ => false);
            GhostPlaybackLogic.SetSpawnFlagOverrideForTesting(evt =>
            {
                spawnAttempts++;
                return spawnAttempts >= 4;
            });

            var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            var engine = new GhostPlaybackEngine(null);
            var policy = new ParsekPlaybackPolicy(engine, host)
            {
                IsWarpActiveOverrideForTesting = () => false,
                CurrentUTOverrideForTesting = () => 120.0,
                HasActiveVesselOverrideForTesting = () => false,
                SpawnVesselOrChainTipOverrideForTesting = (recording, index) =>
                {
                    recording.VesselSpawned = true;
                    recording.SpawnedVesselPersistentId = 5150;
                }
            };
            policy.pendingSpawnRecordingIds.Add(rec.RecordingId);

            policy.FlushDeferredSpawns();
            Assert.Contains(rec.RecordingId, policy.pendingFlagReplayRecordingIds);
            Assert.DoesNotContain(rec.RecordingId, policy.pendingSpawnRecordingIds);
            Assert.DoesNotContain(logLines, line => line.Contains("Deferred flag replay still failing"));

            policy.FlushDeferredSpawns();
            Assert.Contains(rec.RecordingId, policy.pendingFlagReplayRecordingIds);

            policy.FlushDeferredSpawns();
            Assert.Contains(rec.RecordingId, policy.pendingFlagReplayRecordingIds);
            Assert.Contains(logLines, line =>
                line.Contains("[Policy]") &&
                line.Contains("Deferred flag replay still failing after 3 flush attempt(s)") &&
                line.Contains("Retry Kerbal"));

            policy.FlushDeferredSpawns();
            Assert.DoesNotContain(rec.RecordingId, policy.pendingFlagReplayRecordingIds);
            Assert.Equal(4, spawnAttempts);
        }

        [Fact]
        public void HandleAllGhostsDestroying_ClearsPendingFlagReplayQueue()
        {
            var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            var engine = new GhostPlaybackEngine(null);
            var policy = new ParsekPlaybackPolicy(engine, host);
            policy.pendingSpawnRecordingIds.Add("spawn");
            policy.pendingFlagReplayRecordingIds.Add("flag");

            var method = typeof(ParsekPlaybackPolicy).GetMethod(
                "HandleAllGhostsDestroying",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(method);
            method.Invoke(policy, null);

            Assert.Empty(policy.pendingSpawnRecordingIds);
            Assert.Empty(policy.pendingFlagReplayRecordingIds);
        }

        #endregion

        #region ShouldCheckForSpawnDeath

        [Fact]
        public void ShouldCheckForSpawnDeath_SpawnedWithPid_ReturnsTrue()
        {
            Assert.True(GhostPlaybackLogic.ShouldCheckForSpawnDeath(true, 42000, false));
        }

        [Fact]
        public void ShouldCheckForSpawnDeath_NotSpawned_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldCheckForSpawnDeath(false, 42000, false));
        }

        [Fact]
        public void ShouldCheckForSpawnDeath_ZeroPid_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldCheckForSpawnDeath(true, 0, false));
        }

        [Fact]
        public void ShouldCheckForSpawnDeath_Abandoned_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldCheckForSpawnDeath(true, 42000, true));
        }

        [Fact]
        public void ShouldCheckForSpawnDeath_NotSpawnedZeroPidAbandoned_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldCheckForSpawnDeath(false, 0, true));
        }

        #endregion
    }
}
