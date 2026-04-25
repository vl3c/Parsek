using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class DeferredSpawnTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public DeferredSpawnTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            GhostPlaybackLogic.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
            GhostPlaybackLogic.ResetForTesting();
            RewindContext.ResetForTesting();
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
        public void FlushDeferredSpawns_SpawnsQueuedSplashedSurvivorAfterWarpEnds()
        {
            var rec = new Recording
            {
                RecordingId = "rec-splashed-after-warp",
                VesselName = "Returned Capsule",
                VesselSnapshot = new ConfigNode("VESSEL"),
                TerminalStateValue = TerminalState.Splashed
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            int spawnCalls = 0;
            var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            var engine = new GhostPlaybackEngine(null);
            var policy = new ParsekPlaybackPolicy(engine, host)
            {
                IsWarpActiveOverrideForTesting = () => false,
                CurrentUTOverrideForTesting = () => 200.0,
                SpawnVesselOrChainTipOverrideForTesting = (recording, index) =>
                {
                    spawnCalls++;
                    recording.VesselSpawned = true;
                    recording.SpawnedVesselPersistentId = 9898;
                }
            };
            policy.pendingSpawnRecordingIds.Add(rec.RecordingId);

            policy.FlushDeferredSpawns();

            Assert.Equal(1, spawnCalls);
            Assert.True(rec.VesselSpawned);
            Assert.DoesNotContain(rec.RecordingId, policy.pendingSpawnRecordingIds);
            Assert.Contains(logLines, line =>
                line.Contains("[Policy]")
                && line.Contains("Deferred spawn executing")
                && line.Contains("Returned Capsule"));
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

        #region RunSpawnDeathChecks — re-fly session guard

        /// <summary>
        /// Regression: <see cref="PostLoadStripper.Strip"/> kills sibling
        /// vessels at re-fly invocation as part of the §6.4 setup. Without a
        /// session guard, <see cref="ParsekPlaybackPolicy.RunSpawnDeathChecks"/>
        /// interpreted those kills as "spawned vessel died, please re-spawn"
        /// and reset <see cref="Recording.VesselSpawned"/> to false — arming a
        /// duplicate materialization of the upper stage right next to the
        /// player's re-fly vessel (10:47 playtest, recording 307dc35b).
        /// </summary>
        [Fact]
        public void RunSpawnDeathChecks_SkippedWhileReFlySessionMarkerActive()
        {
            const uint kStrippedPid = 2708531065u; // matches the playtest
            var rec = new Recording
            {
                RecordingId = "rec-policy-marker-skip",
                VesselName = "Kerbal X",
                VesselSpawned = true,
                SpawnedVesselPersistentId = kStrippedPid,
                SpawnAbandoned = false,
                SpawnDeathCount = 0,
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var scenario = new ParsekScenario
            {
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = "sess_test_marker_skip",
                    OriginChildRecordingId = rec.RecordingId,
                    ActiveReFlyRecordingId = rec.RecordingId,
                    RewindPointId = "rp_test",
                    TreeId = rec.TreeId,
                },
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            try
            {
                var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
                var engine = new GhostPlaybackEngine(null);
                var policy = new ParsekPlaybackPolicy(engine, host);

                policy.RunSpawnDeathChecks();

                // Spawn-tracking state untouched: the recording must not get
                // VesselSpawned cleared, otherwise the next spawn-evaluation
                // pass would arm a duplicate materialization.
                Assert.True(rec.VesselSpawned);
                Assert.Equal(kStrippedPid, rec.SpawnedVesselPersistentId);
                Assert.Equal(0, rec.SpawnDeathCount);
                Assert.False(rec.SpawnAbandoned);
                Assert.Contains(logLines, l =>
                    l.Contains("[Policy]")
                    && l.Contains("RunSpawnDeathChecks: skipped during active re-fly session")
                    && l.Contains("sess_test_marker_skip"));
            }
            finally
            {
                ParsekScenario.SetInstanceForTesting(null);
            }
        }

        /// <summary>
        /// Discriminator: when no re-fly session is active, the spawn-death
        /// path runs normally. Without this assertion the previous test could
        /// trivially pass by no-op'ing the whole method.
        /// </summary>
        [Fact]
        public void RunSpawnDeathChecks_RunsWhenNoReFlySession()
        {
            var rec = new Recording
            {
                RecordingId = "rec-policy-no-marker",
                VesselName = "Healthy Spawned",
                VesselSpawned = true,
                SpawnedVesselPersistentId = 99999u,
                SpawnAbandoned = false,
                SpawnDeathCount = 0,
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var scenario = new ParsekScenario { ActiveReFlySessionMarker = null };
            ParsekScenario.SetInstanceForTesting(scenario);
            try
            {
                var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
                var engine = new GhostPlaybackEngine(null);
                var policy = new ParsekPlaybackPolicy(engine, host);

                // No "skipped during active re-fly session" log. The vessel
                // also looks "dead" to FindVesselByPid (no FlightGlobals in a
                // unit test), so the death path runs and resets state.
                policy.RunSpawnDeathChecks();

                Assert.False(rec.VesselSpawned);
                Assert.Equal(0u, rec.SpawnedVesselPersistentId);
                Assert.Equal(1, rec.SpawnDeathCount);
                Assert.DoesNotContain(logLines, l =>
                    l.Contains("RunSpawnDeathChecks: skipped during active re-fly session"));
                Assert.Contains(logLines, l =>
                    l.Contains("[Policy]")
                    && l.Contains("Spawn-death detected")
                    && l.Contains("Healthy Spawned"));
            }
            finally
            {
                ParsekScenario.SetInstanceForTesting(null);
            }
        }

        /// <summary>
        /// Defense-in-depth for the re-fly-style strip-kill contract: while
        /// <see cref="RewindContext.IsRewinding"/> is true (between
        /// <see cref="RewindContext.BeginRewind"/> and
        /// <see cref="RewindContext.EndRewind"/>), <see cref="ParsekPlaybackPolicy.RunSpawnDeathChecks"/>
        /// must short-circuit so any synthetic post-strip "vessel-gone" signal
        /// does not flip <c>VesselSpawned</c> back to false.
        /// <para>
        /// This is NOT the production-sequence fix for #573 — see the bug doc
        /// for the real mechanism (<see cref="Recording.SpawnSuppressedByRewind"/>).
        /// In production the rewind path drops <see cref="RewindContext.IsRewinding"/>
        /// inside <see cref="ParsekScenario.HandleRewindOnLoad"/> before any
        /// FLIGHT update fires, so this guard never trips on the production
        /// duplicate-spawn path; the test injects an artificial state to lock
        /// the guard in place against future refactors.
        /// </para>
        /// </summary>
        [Fact]
        public void RunSpawnDeathChecks_SkippedWhileRewindContextIsRewinding()
        {
            const uint kStrippedPid = 2708531065u;
            var rec = new Recording
            {
                RecordingId = "rec-policy-rewind-skip",
                VesselName = "Kerbal X (rewound)",
                VesselSpawned = true,
                SpawnedVesselPersistentId = kStrippedPid,
                SpawnAbandoned = false,
                SpawnDeathCount = 0,
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            // No re-fly marker on this rewind — it's a plain Rewind-to-Launch
            // R-button click. Without the new guard the spawn-death detector
            // would reset VesselSpawned post-strip.
            var scenario = new ParsekScenario { ActiveReFlySessionMarker = null };
            ParsekScenario.SetInstanceForTesting(scenario);
            RewindContext.BeginRewind(
                ut: 1442.0,
                reserved: default(BudgetSummary),
                baselineFunds: 0,
                baselineScience: 0,
                baselineRep: 0);
            try
            {
                var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
                var engine = new GhostPlaybackEngine(null);
                var policy = new ParsekPlaybackPolicy(engine, host);

                policy.RunSpawnDeathChecks();

                Assert.True(rec.VesselSpawned);
                Assert.Equal(kStrippedPid, rec.SpawnedVesselPersistentId);
                Assert.Equal(0, rec.SpawnDeathCount);
                Assert.False(rec.SpawnAbandoned);
                Assert.Contains(logLines, l =>
                    l.Contains("[Policy]")
                    && l.Contains("RunSpawnDeathChecks: defense-in-depth skip during active rewind"));
                Assert.DoesNotContain(logLines, l =>
                    l.Contains("Spawn-death detected"));
            }
            finally
            {
                ParsekScenario.SetInstanceForTesting(null);
                RewindContext.ResetForTesting();
            }
        }

        /// <summary>
        /// Discriminator: once <see cref="RewindContext.EndRewind"/> fires the
        /// guard releases. A spawned vessel that vanishes from
        /// <see cref="FlightGlobals.Vessels"/> after the rewind window closes
        /// is detected normally and reset for re-spawn (or abandoned). This
        /// test exists so a future "always skip rewinds" rewrite cannot pass
        /// the regression above by trivially short-circuiting the whole
        /// method.
        /// </summary>
        [Fact]
        public void RunSpawnDeathChecks_RunsAfterRewindContextCleared()
        {
            var rec = new Recording
            {
                RecordingId = "rec-policy-rewind-cleared",
                VesselName = "Healthy Post-Rewind",
                VesselSpawned = true,
                SpawnedVesselPersistentId = 88888u,
                SpawnAbandoned = false,
                SpawnDeathCount = 0,
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var scenario = new ParsekScenario { ActiveReFlySessionMarker = null };
            ParsekScenario.SetInstanceForTesting(scenario);
            RewindContext.BeginRewind(
                ut: 1442.0,
                reserved: default(BudgetSummary),
                baselineFunds: 0,
                baselineScience: 0,
                baselineRep: 0);
            RewindContext.EndRewind();
            try
            {
                var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
                var engine = new GhostPlaybackEngine(null);
                var policy = new ParsekPlaybackPolicy(engine, host);

                policy.RunSpawnDeathChecks();

                Assert.False(rec.VesselSpawned);
                Assert.Equal(0u, rec.SpawnedVesselPersistentId);
                Assert.Equal(1, rec.SpawnDeathCount);
                Assert.DoesNotContain(logLines, l =>
                    l.Contains("RunSpawnDeathChecks: defense-in-depth skip during active rewind"));
                Assert.Contains(logLines, l =>
                    l.Contains("[Policy]")
                    && l.Contains("Spawn-death detected")
                    && l.Contains("Healthy Post-Rewind"));
            }
            finally
            {
                ParsekScenario.SetInstanceForTesting(null);
            }
        }

        #endregion
    }
}
