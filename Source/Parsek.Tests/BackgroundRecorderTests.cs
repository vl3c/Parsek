using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class BackgroundRecorderTests : IDisposable
    {
        public BackgroundRecorderTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
        }

        /// <summary>
        /// Helper: creates a minimal RecordingTree with the given background vessel PIDs.
        /// Each background vessel gets a Recording in tree.Recordings with matching VesselPersistentId.
        /// </summary>
        private RecordingTree MakeTree(params (uint pid, string recId)[] backgroundVessels)
        {
            var tree = new RecordingTree
            {
                Id = "tree_bg_test",
                TreeName = "BG Test Tree",
                RootRecordingId = "rec_root",
                ActiveRecordingId = "rec_active"
            };

            // Active recording (not in background)
            tree.Recordings["rec_active"] = new Recording
            {
                RecordingId = "rec_active",
                VesselName = "Active Vessel",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            };

            for (int i = 0; i < backgroundVessels.Length; i++)
            {
                var (pid, recId) = backgroundVessels[i];
                tree.Recordings[recId] = new Recording
                {
                    RecordingId = recId,
                    VesselName = $"Background Vessel {i}",
                    VesselPersistentId = pid,
                    ExplicitStartUT = 100.0,
                    ExplicitEndUT = 200.0
                };
                tree.BackgroundMap[pid] = recId;
            }

            return tree;
        }

        #region 9.1 On-Rails State Management

        [Fact]
        public void Constructor_InitializesOnRailsState_ForEachBackgroundVessel()
        {
            // Arrange: tree with two background vessels
            var tree = MakeTree((100, "rec_bg1"), (200, "rec_bg2"));

            // Act
            var bgRecorder = new BackgroundRecorder(tree);

            // Assert: both vessels have on-rails state
            Assert.Equal(2, bgRecorder.OnRailsStateCount);
            Assert.True(bgRecorder.HasOnRailsState(100));
            Assert.True(bgRecorder.HasOnRailsState(200));
            Assert.Equal(0, bgRecorder.LoadedStateCount);
        }

        [Fact]
        public void Constructor_OnRailsState_HasNoOpenOrbitSegment()
        {
            // Constructor creates minimal on-rails state (no vessel available)
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            // Minimal state has no open orbit segment and is not landed
            Assert.False(bgRecorder.GetOnRailsHasOpenSegment(100));
            Assert.False(bgRecorder.GetOnRailsIsLanded(100));
        }

        [Fact]
        public void Constructor_OnRailsState_LastExplicitEndUpdate_IsNegativeOne()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            // Minimal state has lastExplicitEndUpdate = -1
            Assert.Equal(-1.0, bgRecorder.GetOnRailsLastExplicitEndUpdate(100));
        }

        [Fact]
        public void RefreshOnRailsFinalizationCacheForTesting_CachesStableOrbit()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);
            bgRecorder.InjectOpenOrbitSegmentForTesting(100, new OrbitSegment
            {
                startUT = 50.0,
                endUT = 100.0,
                bodyName = "Mun",
                semiMajorAxis = 250000.0,
                eccentricity = 0.01,
                inclination = 2.0,
                longitudeOfAscendingNode = 3.0,
                argumentOfPeriapsis = 4.0,
                meanAnomalyAtEpoch = 0.5,
                epoch = 50.0
            });

            bool refreshed = bgRecorder.RefreshOnRailsFinalizationCacheForTesting(
                100,
                currentUT: 120.0,
                force: true);

            Assert.True(refreshed);
            Assert.True(bgRecorder.HasFinalizationCache(100));
            RecordingFinalizationCache cache = bgRecorder.GetFinalizationCacheForTesting(100);
            Assert.Equal("rec_bg1", cache.RecordingId);
            Assert.Equal(FinalizationCacheOwner.BackgroundOnRails, cache.Owner);
            Assert.Equal(TerminalState.Orbiting, cache.TerminalState);
            Assert.Equal("Mun", cache.TerminalOrbit.Value.bodyName);
        }

        [Fact]
        public void RefreshOnRailsFinalizationCacheForTesting_TouchesStableCacheWhenDigestUnchanged()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);
            bgRecorder.InjectOpenOrbitSegmentForTesting(100, new OrbitSegment
            {
                startUT = 50.0,
                endUT = 100.0,
                bodyName = "Mun",
                semiMajorAxis = 250000.0,
                eccentricity = 0.01,
                inclination = 2.0,
                longitudeOfAscendingNode = 3.0,
                argumentOfPeriapsis = 4.0,
                meanAnomalyAtEpoch = 0.5,
                epoch = 50.0
            });

            bool firstRefresh = bgRecorder.RefreshOnRailsFinalizationCacheForTesting(
                100,
                currentUT: 120.0,
                force: true);
            bool rebuilt = bgRecorder.RefreshOnRailsFinalizationCacheForTesting(
                100,
                currentUT: 140.0,
                force: false);

            RecordingFinalizationCache cache = bgRecorder.GetFinalizationCacheForTesting(100);
            Assert.True(firstRefresh);
            Assert.False(rebuilt);
            Assert.Equal(140.0, cache.TerminalUT);
            Assert.Equal(140.0, cache.LastObservedUT);
            Assert.Equal("test_on_rails", cache.RefreshReason);
        }

        [Fact]
        public void RefreshOnRailsFinalizationCache_AlreadyDestroyedSkipRefreshesOnCadence()
        {
            var logLines = new List<string>();
            double logClock = 0.0;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ClockOverrideForTesting = () => logClock;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();

            var tree = MakeTree((100, "rec_bg1"));
            Recording recording = tree.Recordings["rec_bg1"];
            recording.TerminalStateValue = TerminalState.Destroyed;
            recording.ExplicitEndUT = 180.0;
            recording.TerminalOrbitBody = "Kerbin";

            var bgRecorder = new BackgroundRecorder(tree);
            bgRecorder.InjectOpenOrbitSegmentForTesting(100, new OrbitSegment
            {
                startUT = 50.0,
                endUT = 100.0,
                bodyName = "Mun",
                semiMajorAxis = 250000.0,
                eccentricity = 0.01,
                inclination = 2.0,
                longitudeOfAscendingNode = 3.0,
                argumentOfPeriapsis = 4.0,
                meanAnomalyAtEpoch = 0.5,
                epoch = 50.0
            });

            bool firstRefresh = bgRecorder.RefreshOnRailsFinalizationCacheForTesting(
                100,
                currentUT: 120.0,
                force: true);
            logClock = 10.0;
            bool secondRefresh = bgRecorder.RefreshOnRailsFinalizationCacheForTesting(
                100,
                currentUT: 130.0,
                force: false);

            RecordingFinalizationCache cache = bgRecorder.GetFinalizationCacheForTesting(100);
            Assert.False(firstRefresh);
            Assert.False(secondRefresh);
            Assert.NotNull(cache);
            Assert.Equal(FinalizationCacheStatus.Failed, cache.Status);
            Assert.Equal(
                RecordingFinalizationCacheProducer.AlreadyClassifiedDestroyedDeclineReason,
                cache.DeclineReason);
            Assert.Equal(130.0, cache.CachedAtUT);
            Assert.Equal(180.0, cache.TerminalUT);
            Assert.Equal(TerminalState.Destroyed, recording.TerminalStateValue);
            Assert.Equal(180.0, recording.ExplicitEndUT);

            Assert.Equal(2, logLines.FindAll(line =>
                line.Contains("[VERBOSE][Extrapolator]")
                && line.Contains("already classified Destroyed at terminalUT=180.0; skipping re-run")).Count);
            // Demoted to Verbose because newlyClassified=0; see
            // RecordingFinalizationCacheProducer.LogRefreshSummary's
            // log-hygiene gate (no real classification work happened, just a
            // "still already destroyed" reaffirmation).
            Assert.Single(logLines.FindAll(line =>
                line.Contains("[VERBOSE][Extrapolator] FinalizerCache refresh summary: owner=BackgroundOnRails reason=test_on_rails recordingsExamined=1 alreadyClassified=1 newlyClassified=0")));
        }

        [Fact]
        public void TryTouchSkippedFinalizationCache_PeriodicRequired_DoesNotAdvanceCacheCadence()
        {
            var cache = new RecordingFinalizationCache
            {
                RecordingId = "rec_bg1",
                VesselPersistentId = 100u,
                Owner = FinalizationCacheOwner.BackgroundLoaded,
                Status = FinalizationCacheStatus.Fresh,
                TerminalState = TerminalState.Landed,
                CachedAtUT = 120.0,
                TerminalUT = 120.0,
                LastObservedUT = 120.0,
                TailStartsAtUT = 120.0
            };

            bool touched = BackgroundRecorder.TryTouchSkippedFinalizationCache(
                cache,
                currentUT: 124.0,
                reason: "background_periodic",
                currentDigest: "Kerbin|sit=LANDED|atmo=False|thrust=False",
                requiresPeriodicRefresh: true);

            Assert.False(touched);
            Assert.Equal(120.0, cache.CachedAtUT);
            Assert.Equal(120.0, cache.TerminalUT);
            Assert.Equal(120.0, cache.LastObservedUT);
        }

        [Fact]
        public void TryTouchSkippedFinalizationCache_StableBackgroundOrbit_AdvancesObservedTerminal()
        {
            var cache = new RecordingFinalizationCache
            {
                RecordingId = "rec_bg1",
                VesselPersistentId = 100u,
                Owner = FinalizationCacheOwner.BackgroundLoaded,
                Status = FinalizationCacheStatus.Fresh,
                TerminalState = TerminalState.Orbiting,
                CachedAtUT = 120.0,
                TerminalUT = 120.0,
                LastObservedUT = 120.0,
                TailStartsAtUT = 120.0
            };

            bool touched = BackgroundRecorder.TryTouchSkippedFinalizationCache(
                cache,
                currentUT: 140.0,
                reason: "background_periodic",
                currentDigest: "Kerbin|sit=ORBITING|atmo=False|thrust=False",
                requiresPeriodicRefresh: false);

            Assert.True(touched);
            Assert.Equal(140.0, cache.CachedAtUT);
            Assert.Equal(140.0, cache.TerminalUT);
            Assert.Equal(140.0, cache.LastObservedUT);
        }

        [Fact]
        public void AdoptFinalizationCacheForTesting_PreservesOwnerUntilRefresh()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);
            var inherited = new RecordingFinalizationCache
            {
                RecordingId = "pending-active",
                VesselPersistentId = 999u,
                Owner = FinalizationCacheOwner.ActiveRecorder,
                Status = FinalizationCacheStatus.Fresh,
                TerminalState = TerminalState.Destroyed,
                TerminalUT = 250.0
            };

            bgRecorder.AdoptFinalizationCacheForTesting(100, "rec_bg1", inherited);

            RecordingFinalizationCache cache = bgRecorder.GetFinalizationCacheForTesting(100);
            Assert.NotNull(cache);
            Assert.Equal("rec_bg1", cache.RecordingId);
            Assert.Equal(100u, cache.VesselPersistentId);
            Assert.Equal(FinalizationCacheOwner.ActiveRecorder, cache.Owner);
            Assert.Equal(TerminalState.Destroyed, cache.TerminalState);
            Assert.Null(tree.Recordings["rec_bg1"].TerminalStateValue);
        }

        [Fact]
        public void OnVesselRemovedFromBackground_ClearsFinalizationCache()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);
            bgRecorder.AdoptFinalizationCacheForTesting(100, "rec_bg1", new RecordingFinalizationCache
            {
                RecordingId = "rec_bg1",
                VesselPersistentId = 100u,
                Owner = FinalizationCacheOwner.BackgroundOnRails,
                Status = FinalizationCacheStatus.Fresh,
                TerminalState = TerminalState.Orbiting,
                TerminalUT = 200.0
            });

            Assert.True(bgRecorder.HasFinalizationCache(100));

            bgRecorder.OnVesselRemovedFromBackground(100);

            Assert.False(bgRecorder.HasFinalizationCache(100));
        }

        [Fact]
        public void ForgetFinalizationCache_RemovesDeferredDestructionCache()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);
            bgRecorder.AdoptFinalizationCacheForTesting(100, "rec_bg1", new RecordingFinalizationCache
            {
                RecordingId = "rec_bg1",
                VesselPersistentId = 100u,
                Owner = FinalizationCacheOwner.BackgroundOnRails,
                Status = FinalizationCacheStatus.Fresh,
                TerminalState = TerminalState.Destroyed,
                TerminalUT = 200.0
            });

            Assert.True(bgRecorder.HasFinalizationCache(100));

            bgRecorder.ForgetFinalizationCache(100);

            Assert.False(bgRecorder.HasFinalizationCache(100));
        }

        [Fact]
        public void GetFinalizationCacheForRecording_ResolvesByRecordingIdWhenPidUnknown()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);
            bgRecorder.AdoptFinalizationCacheForTesting(100, "rec_bg1", new RecordingFinalizationCache
            {
                RecordingId = "old-active",
                VesselPersistentId = 999u,
                Owner = FinalizationCacheOwner.ActiveRecorder,
                Status = FinalizationCacheStatus.Fresh,
                TerminalState = TerminalState.Orbiting,
                TerminalUT = 250.0
            });

            var recording = new Recording
            {
                RecordingId = "rec_bg1",
                VesselPersistentId = 0
            };

            RecordingFinalizationCache stored =
                bgRecorder.GetFinalizationCacheForTesting(100);
            RecordingFinalizationCache cache =
                bgRecorder.GetFinalizationCacheForRecording(recording);

            Assert.NotNull(cache);
            Assert.NotSame(stored, cache);
            Assert.Equal("rec_bg1", cache.RecordingId);
            Assert.Equal(100u, cache.VesselPersistentId);
            Assert.Equal(TerminalState.Orbiting, cache.TerminalState);

            cache.RecordingId = "mutated-returned-copy";
            Assert.Equal("rec_bg1", stored.RecordingId);
        }

        [Fact]
        public void FinalizeAllForCommit_MissingLoadedVessel_KeepsCacheApplicableFromLastSample()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);
            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                100,
                "rec_bg1",
                SegmentEnvironment.Atmospheric,
                ut: 100.0,
                initialPoint: new TrajectoryPoint
                {
                    ut = 100.0,
                    altitude = 5000.0,
                    bodyName = "Kerbin"
                });
            var cache = new RecordingFinalizationCache
            {
                RecordingId = "rec_bg1",
                VesselPersistentId = 100u,
                Owner = FinalizationCacheOwner.BackgroundLoaded,
                Status = FinalizationCacheStatus.Fresh,
                TerminalState = TerminalState.Destroyed,
                TerminalUT = 180.0,
                TailStartsAtUT = 100.0
            };
            cache.PredictedSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 100.0,
                endUT = 180.0,
                semiMajorAxis = 700000.0,
                isPredicted = true
            });
            bgRecorder.AdoptFinalizationCacheForTesting(100, "rec_bg1", cache);

            bgRecorder.FinalizeAllForCommit(commitUT: 200.0);

            Recording rec = tree.Recordings["rec_bg1"];
            Assert.Single(rec.TrackSections);
            Assert.Equal(100.0, rec.TrackSections[0].endUT);
            RecordingFinalizationCache resolved =
                bgRecorder.GetFinalizationCacheForRecording(rec);

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                rec,
                resolved,
                new RecordingFinalizationCacheApplyOptions
                {
                    ConsumerPath = "test_missing_loaded_commit",
                    AllowStale = true
                },
                out RecordingFinalizationCacheApplyResult result);

            Assert.True(applied, result.Status.ToString());
            Assert.Equal(180.0, rec.ExplicitEndUT);
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.True(rec.OrbitSegments.Exists(s => s.isPredicted && s.endUT == 180.0));
        }

        [Fact]
        public void ScopeFinalizationCacheToBackgroundEnd_DestroyedCapsAtDeletionUT()
        {
            RecordingFinalizationCache cache = MakeFinalizationCache(
                "rec_bg1",
                100u,
                TerminalState.Destroyed,
                terminalUT: 180.0,
                Segment(100.0, 180.0));

            RecordingFinalizationCache scoped =
                BackgroundRecorder.ScopeFinalizationCacheToBackgroundEndForTesting(
                    cache,
                    endUT: 130.0);

            Assert.Equal(130.0, scoped.TerminalUT);
            Assert.Equal(180.0, cache.TerminalUT);
            Assert.Single(scoped.PredictedSegments);
            Assert.Single(cache.PredictedSegments);
            Assert.NotSame(cache.PredictedSegments, scoped.PredictedSegments);
        }

        [Fact]
        public void TryApplyFinalizationCacheForBackgroundEnd_RejectsNonDestroyedCacheForConfirmedDestroy()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);
            Recording rec = tree.Recordings["rec_bg1"];
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" });
            bgRecorder.AdoptFinalizationCacheForTesting(
                100,
                "rec_bg1",
                MakeFinalizationCache("rec_bg1", 100u, TerminalState.Orbiting, terminalUT: 120.0));

            bool applied = bgRecorder.TryApplyFinalizationCacheForBackgroundEnd(
                rec,
                100u,
                endUT: 130.0,
                consumerPath: "unit-confirmed-destroy",
                allowStale: true,
                requireDestroyedTerminal: true,
                out RecordingFinalizationCacheApplyResult result);

            Assert.False(applied);
            Assert.Null(rec.TerminalStateValue);
            Assert.Equal(200.0, rec.ExplicitEndUT);
        }

        [Fact]
        public void CheckDebrisTTL_MissingVessel_AppliesCacheBeforeDestroyedFallback()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);
            Recording rec = tree.Recordings["rec_bg1"];
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100.0,
                altitude = 5000.0,
                bodyName = "Kerbin"
            });
            bgRecorder.AdoptFinalizationCacheForTesting(
                100,
                "rec_bg1",
                MakeFinalizationCache(
                    "rec_bg1",
                    100u,
                    TerminalState.Destroyed,
                    terminalUT: 180.0,
                    Segment(100.0, 180.0)));
            bgRecorder.InjectDebrisTTLForTesting(100, 500.0);

            bgRecorder.CheckDebrisTTL(130.0);

            Assert.False(tree.BackgroundMap.ContainsKey(100));
            Assert.False(bgRecorder.HasFinalizationCache(100));
            Assert.Equal(0, bgRecorder.DebrisTTLCount);
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.Equal(130.0, rec.ExplicitEndUT);
            Assert.Single(rec.OrbitSegments);
            Assert.True(rec.OrbitSegments[0].isPredicted);
            Assert.Equal(100.0, rec.OrbitSegments[0].startUT);
            Assert.Equal(130.0, rec.OrbitSegments[0].endUT);
        }

        [Fact]
        public void CheckDebrisTTL_MissingVessel_UsesStableCacheInsteadOfDestroyedFallback()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);
            Recording rec = tree.Recordings["rec_bg1"];
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100.0,
                altitude = 90000.0,
                bodyName = "Kerbin"
            });
            RecordingFinalizationCache cache = MakeFinalizationCache(
                "rec_bg1",
                100u,
                TerminalState.Orbiting,
                terminalUT: 120.0);
            cache.TerminalOrbit = new RecordingFinalizationTerminalOrbit
            {
                bodyName = "Kerbin",
                semiMajorAxis = 700000.0,
                eccentricity = 0.01,
                inclination = 2.0,
                longitudeOfAscendingNode = 3.0,
                argumentOfPeriapsis = 4.0,
                meanAnomalyAtEpoch = 0.5,
                epoch = 120.0
            };
            bgRecorder.AdoptFinalizationCacheForTesting(100, "rec_bg1", cache);
            bgRecorder.InjectDebrisTTLForTesting(100, 500.0);

            bgRecorder.CheckDebrisTTL(130.0);

            Assert.False(bgRecorder.HasFinalizationCache(100));
            Assert.Equal(TerminalState.Orbiting, rec.TerminalStateValue);
            Assert.Equal(130.0, rec.ExplicitEndUT);
            Assert.Empty(rec.OrbitSegments);
            Assert.Equal("Kerbin", rec.TerminalOrbitBody);
        }

        [Fact]
        public void Constructor_EmptyBackgroundMap_CreatesNoStates()
        {
            var tree = MakeTree(); // no background vessels
            var bgRecorder = new BackgroundRecorder(tree);

            Assert.Equal(0, bgRecorder.OnRailsStateCount);
            Assert.Equal(0, bgRecorder.LoadedStateCount);
        }

        [Fact]
        public void UpdateOnRails_UpdatesExplicitEndUT_AfterInterval()
        {
            // Arrange
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            // Act: call UpdateOnRails at UT that exceeds interval (lastExplicitEndUpdate is -1)
            bgRecorder.UpdateOnRails(50.0);

            // Assert: ExplicitEndUT is updated on the tree recording
            Assert.Equal(50.0, tree.Recordings["rec_bg1"].ExplicitEndUT);
            Assert.Equal(50.0, bgRecorder.GetOnRailsLastExplicitEndUpdate(100));
        }

        [Fact]
        public void UpdateOnRails_DoesNotUpdate_WhenIntervalNotElapsed()
        {
            // Arrange
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            // First update to set the baseline
            bgRecorder.UpdateOnRails(50.0);
            Assert.Equal(50.0, tree.Recordings["rec_bg1"].ExplicitEndUT);

            // Act: call again at UT only 10s later (interval is 30s)
            bgRecorder.UpdateOnRails(60.0);

            // Assert: ExplicitEndUT should NOT be updated (still 50.0)
            Assert.Equal(50.0, tree.Recordings["rec_bg1"].ExplicitEndUT);
            Assert.Equal(50.0, bgRecorder.GetOnRailsLastExplicitEndUpdate(100));
        }

        [Fact]
        public void UpdateOnRails_Updates_WhenIntervalElapsed()
        {
            // Arrange
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            // First update
            bgRecorder.UpdateOnRails(50.0);

            // Act: call again at UT 30+ seconds later
            bgRecorder.UpdateOnRails(81.0);

            // Assert: ExplicitEndUT is updated
            Assert.Equal(81.0, tree.Recordings["rec_bg1"].ExplicitEndUT);
            Assert.Equal(81.0, bgRecorder.GetOnRailsLastExplicitEndUpdate(100));
        }

        [Fact]
        public void UpdateOnRails_MultipleVessels_UpdatesIndependently()
        {
            // Arrange
            var tree = MakeTree((100, "rec_bg1"), (200, "rec_bg2"));
            var bgRecorder = new BackgroundRecorder(tree);

            // First update for both
            bgRecorder.UpdateOnRails(50.0);
            Assert.Equal(50.0, tree.Recordings["rec_bg1"].ExplicitEndUT);
            Assert.Equal(50.0, tree.Recordings["rec_bg2"].ExplicitEndUT);

            // Act: second update at 81.0 (30+ seconds later)
            bgRecorder.UpdateOnRails(81.0);

            // Assert: both updated
            Assert.Equal(81.0, tree.Recordings["rec_bg1"].ExplicitEndUT);
            Assert.Equal(81.0, tree.Recordings["rec_bg2"].ExplicitEndUT);
        }

        [Fact]
        public void UpdateOnRails_NullTree_DoesNotThrow()
        {
            // Construct with a valid tree, but if tree were set to null internally...
            // Actually the constructor requires a non-null tree. Test that calling with
            // an empty background map doesn't throw.
            var tree = MakeTree();
            var bgRecorder = new BackgroundRecorder(tree);

            // Should not throw
            bgRecorder.UpdateOnRails(100.0);
        }

        #endregion

        #region 9.5 Vessel Lifecycle

        [Fact]
        public void Constructor_CreatesMinimalOnRailsState_ForBackgroundVessels()
        {
            // The constructor creates a minimal on-rails state for each vessel in
            // the BackgroundMap (equivalent to the "vessel not found" path in
            // OnVesselBackgrounded, since no actual Vessel objects exist).
            var tree = MakeTree((100, "rec_bg1"), (200, "rec_bg2"));
            var bgRecorder = new BackgroundRecorder(tree);

            // Verify both vessels have on-rails state with minimal defaults
            Assert.True(bgRecorder.HasOnRailsState(100));
            Assert.False(bgRecorder.HasLoadedState(100));
            Assert.False(bgRecorder.GetOnRailsHasOpenSegment(100));
            Assert.Equal(-1.0, bgRecorder.GetOnRailsLastExplicitEndUpdate(100));

            Assert.True(bgRecorder.HasOnRailsState(200));
            Assert.False(bgRecorder.HasLoadedState(200));
            Assert.False(bgRecorder.GetOnRailsHasOpenSegment(200));
            Assert.Equal(-1.0, bgRecorder.GetOnRailsLastExplicitEndUpdate(200));
        }

        [Fact]
        public void Constructor_VesselNotInBackgroundMap_HasNoState()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            // PID 999 is not in the BackgroundMap
            Assert.False(bgRecorder.HasOnRailsState(999));
            Assert.False(bgRecorder.HasLoadedState(999));
        }

        [Fact]
        public void Constructor_MultipleVessels_AllGetIndependentState()
        {
            var tree = MakeTree((100, "rec_bg1"), (200, "rec_bg2"), (300, "rec_bg3"));
            var bgRecorder = new BackgroundRecorder(tree);

            // All three should have independent on-rails state
            Assert.Equal(3, bgRecorder.OnRailsStateCount);
            Assert.True(bgRecorder.HasOnRailsState(100));
            Assert.True(bgRecorder.HasOnRailsState(200));
            Assert.True(bgRecorder.HasOnRailsState(300));
        }

        #endregion

        #region 9.3 Part Event Polling (Static Method Integration)

        [Fact]
        public void CheckParachuteTransition_WorksWithBackgroundStateCollections()
        {
            // Verify that the static CheckParachuteTransition method works with
            // the same Dictionary<uint, int> type used by BackgroundVesselState
            var parachuteStates = new Dictionary<uint, int>();
            parachuteStates[42] = 0; // STOWED

            // Transition STOWED -> SEMI-DEPLOYED (state 0 -> 1)
            var evt = FlightRecorder.CheckParachuteTransition(42, "parachuteSingle", 1, parachuteStates, 100.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.ParachuteSemiDeployed, evt.Value.eventType);
            Assert.Equal(42u, evt.Value.partPersistentId);
            Assert.Equal(100.0, evt.Value.ut);
        }

        [Fact]
        public void CheckEngineTransition_WorksWithBackgroundStateCollections()
        {
            // Verify that CheckEngineTransition works with the same collection types
            // used by BackgroundVesselState
            var activeEngineKeys = new HashSet<ulong>();
            var lastThrottle = new Dictionary<ulong, float>();

            ulong key = FlightRecorder.EncodeEngineKey(42, 0);

            // Engine off -> on at 80% throttle
            var events = new List<PartEvent>();
            FlightRecorder.CheckEngineTransition(
                key, 42, 0, "liquidEngine1-2",
                true, 0.8f,
                activeEngineKeys, lastThrottle, 100.0, events);

            Assert.NotNull(events);
            Assert.True(events.Count > 0);
            Assert.Equal(PartEventType.EngineIgnited, events[0].eventType);
        }

        [Fact]
        public void CheckDeployableTransition_WorksWithBackgroundStateCollections()
        {
            // Verify that CheckDeployableTransition works with the same HashSet<uint>
            // used by BackgroundVesselState
            var extendedDeployables = new HashSet<uint>();

            // Deploy: RETRACTED -> EXTENDED
            var evt = FlightRecorder.CheckDeployableTransition(
                42, "solarPanel", true, extendedDeployables, 100.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.DeployableExtended, evt.Value.eventType);
        }

        [Fact]
        public void CheckLightTransition_WorksWithBackgroundStateCollections()
        {
            var lightsOn = new HashSet<uint>();

            // Light off -> on
            var evt = FlightRecorder.CheckLightTransition(
                42, "spotLight1", true, lightsOn, 100.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.LightOn, evt.Value.eventType);
        }

        [Fact]
        public void CheckGearTransition_WorksWithBackgroundStateCollections()
        {
            var deployedGear = new HashSet<uint>();

            // Gear retracted -> deployed
            var evt = FlightRecorder.CheckGearTransition(
                42, "gear1", true, deployedGear, 100.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.GearDeployed, evt.Value.eventType);
        }

        [Fact]
        public void CheckCargoBayTransition_WorksWithBackgroundStateCollections()
        {
            var openCargoBays = new HashSet<uint>();

            // Cargo bay closed -> opened
            var evt = FlightRecorder.CheckCargoBayTransition(
                42, "cargoBay", true, openCargoBays, 100.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.CargoBayOpened, evt.Value.eventType);
        }

        [Fact]
        public void CheckFairingTransition_WorksWithBackgroundStateCollections()
        {
            var deployedFairings = new HashSet<uint>();

            // Fairing intact -> deployed
            var evt = FlightRecorder.CheckFairingTransition(
                42, "fairingSize1", true, deployedFairings, 100.0);

            Assert.NotNull(evt);
            Assert.Equal(PartEventType.FairingJettisoned, evt.Value.eventType);
        }

        [Fact]
        public void CheckRcsTransition_WorksWithBackgroundStateCollections()
        {
            var activeRcsKeys = new HashSet<ulong>();
            var lastRcsThrottle = new Dictionary<ulong, float>();

            ulong key = FlightRecorder.EncodeEngineKey(42, 0);

            // RCS off -> on
            var events = new List<PartEvent>();
            FlightRecorder.CheckRcsTransition(
                key, 42, 0, "RCSBlock",
                true, 0.5f,
                activeRcsKeys, lastRcsThrottle, 100.0, events);

            Assert.NotNull(events);
            Assert.True(events.Count > 0);
            Assert.Equal(PartEventType.RCSActivated, events[0].eventType);
        }

        #endregion

        #region 9.6 Data Flow

        [Fact]
        public void UpdateOnRails_WritesExplicitEndUT_ToTreeRecording()
        {
            // Verify that UpdateOnRails directly modifies the tree Recording object
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            double originalEndUT = tree.Recordings["rec_bg1"].ExplicitEndUT;

            // Act: update at a time that exceeds the interval threshold
            bgRecorder.UpdateOnRails(1000.0);

            // Assert: ExplicitEndUT was updated to the new time
            Assert.Equal(1000.0, tree.Recordings["rec_bg1"].ExplicitEndUT);
            Assert.NotEqual(originalEndUT, tree.Recordings["rec_bg1"].ExplicitEndUT);
        }

        [Fact]
        public void Constructor_PreservesTreeRecordingReferences()
        {
            // The BackgroundRecorder should work with the same Recording objects
            // that are in the tree (not copies). This ensures writes are visible
            // to the tree immediately.
            var tree = MakeTree((100, "rec_bg1"));

            // Get reference to the recording before constructing BackgroundRecorder
            var recBefore = tree.Recordings["rec_bg1"];

            var bgRecorder = new BackgroundRecorder(tree);

            // Update via BackgroundRecorder
            bgRecorder.UpdateOnRails(500.0);

            // The same object should be updated
            Assert.Equal(500.0, recBefore.ExplicitEndUT);
            Assert.Same(recBefore, tree.Recordings["rec_bg1"]);
        }

        #endregion

        #region 9.1 cont: On-Rails SurfacePosition (integration paths)

        [Fact]
        public void OnVesselBackgrounded_NotInTree_DoesNotCreateState()
        {
            // Vessel PID 999 is not in BackgroundMap
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.OnVesselBackgrounded(999);

            // Should still only have PID 100
            Assert.Equal(1, bgRecorder.OnRailsStateCount);
            Assert.False(bgRecorder.HasOnRailsState(999));
        }

        [Fact]
        public void OnVesselBackgrounded_WithInitialEnvironmentOverride_QueuesPendingOverride()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.OnVesselBackgrounded(100,
                initialEnvironmentOverride: SegmentEnvironment.SurfaceStationary);

            Assert.Equal(1, bgRecorder.PendingInitialEnvironmentOverrideCount);
            Assert.Equal(SegmentEnvironment.SurfaceStationary,
                bgRecorder.PeekPendingInitialEnvironmentOverrideForTesting(100));
        }

        [Fact]
        public void ConsumePendingInitialEnvironmentOverrideForTesting_RemovesQueuedOverride()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.OnVesselBackgrounded(100,
                initialEnvironmentOverride: SegmentEnvironment.SurfaceStationary);

            Assert.Equal(SegmentEnvironment.SurfaceStationary,
                bgRecorder.ConsumePendingInitialEnvironmentOverrideForTesting(100));
            Assert.Equal(0, bgRecorder.PendingInitialEnvironmentOverrideCount);
        }

        [Fact]
        public void OnVesselRemovedFromBackground_ClearsPendingInitialEnvironmentOverride()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.OnVesselBackgrounded(100,
                initialEnvironmentOverride: SegmentEnvironment.SurfaceStationary);
            bgRecorder.OnVesselRemovedFromBackground(100);

            Assert.Equal(0, bgRecorder.PendingInitialEnvironmentOverrideCount);
            Assert.Null(bgRecorder.PeekPendingInitialEnvironmentOverrideForTesting(100));
        }

        [Fact]
        public void OnVesselBackgrounded_WithInitialTrajectoryPoint_QueuesPendingPoint()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);
            var point = new TrajectoryPoint
            {
                ut = 123.45,
                latitude = 1.0,
                longitude = 2.0,
                altitude = 345.0,
                rotation = Quaternion.identity,
                velocity = new Vector3(1f, 2f, 3f),
                bodyName = "Kerbin"
            };

            bgRecorder.OnVesselBackgrounded(100, initialTrajectoryPoint: point);

            Assert.Equal(1, bgRecorder.PendingInitialTrajectoryPointCount);
            TrajectoryPoint? queued = bgRecorder.PeekPendingInitialTrajectoryPointForTesting(100);
            Assert.True(queued.HasValue);
            Assert.Equal(point.ut, queued.Value.ut, 6);
            Assert.Equal(point.altitude, queued.Value.altitude, 6);
            Assert.Equal(point.bodyName, queued.Value.bodyName);
        }

        [Fact]
        public void ConsumePendingInitialTrajectoryPointForTesting_RemovesQueuedPoint()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);
            var point = new TrajectoryPoint
            {
                ut = 123.45,
                latitude = 1.0,
                longitude = 2.0,
                altitude = 345.0,
                rotation = Quaternion.identity,
                velocity = new Vector3(1f, 2f, 3f),
                bodyName = "Kerbin"
            };

            bgRecorder.OnVesselBackgrounded(100, initialTrajectoryPoint: point);

            TrajectoryPoint? consumed = bgRecorder.ConsumePendingInitialTrajectoryPointForTesting(100);

            Assert.True(consumed.HasValue);
            Assert.Equal(point.ut, consumed.Value.ut, 6);
            Assert.Equal(0, bgRecorder.PendingInitialTrajectoryPointCount);
        }

        [Fact]
        public void OnVesselRemovedFromBackground_ClearsPendingInitialTrajectoryPoint()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);
            var point = new TrajectoryPoint
            {
                ut = 123.45,
                latitude = 1.0,
                longitude = 2.0,
                altitude = 345.0,
                rotation = Quaternion.identity,
                velocity = new Vector3(1f, 2f, 3f),
                bodyName = "Kerbin"
            };

            bgRecorder.OnVesselBackgrounded(100, initialTrajectoryPoint: point);
            bgRecorder.OnVesselRemovedFromBackground(100);

            Assert.Equal(0, bgRecorder.PendingInitialTrajectoryPointCount);
            Assert.Null(bgRecorder.PeekPendingInitialTrajectoryPointForTesting(100));
        }

        [Fact]
        public void InjectOnRailsStateForTesting_ConsumesQueuedInitialPoint_AndWritesSeedToRecording()
        {
            var tree = MakeTree((100, "rec_bg1"));
            tree.Recordings["rec_bg1"].Points.Clear();
            tree.Recordings["rec_bg1"].ExplicitEndUT = double.NaN;

            var bgRecorder = new BackgroundRecorder(tree);
            var point = new TrajectoryPoint
            {
                ut = 123.45,
                latitude = 1.0,
                longitude = 2.0,
                altitude = 345.0,
                rotation = Quaternion.identity,
                velocity = new Vector3(1f, 2f, 3f),
                bodyName = "Kerbin"
            };

            bgRecorder.OnVesselBackgrounded(100, initialTrajectoryPoint: point);
            bgRecorder.InjectOnRailsStateForTesting(100, "rec_bg1", 130.0);

            Assert.Equal(0, bgRecorder.PendingInitialTrajectoryPointCount);
            Assert.Single(tree.Recordings["rec_bg1"].Points);
            Assert.Equal(point.ut, tree.Recordings["rec_bg1"].Points[0].ut, 6);
            Assert.Equal(point.ut, tree.Recordings["rec_bg1"].ExplicitEndUT, 6);
        }

        #endregion

        #region EncodeEngineKey consistency

        [Fact]
        public void EncodeEngineKey_SameEncodingForBackgroundAndActive()
        {
            // BackgroundRecorder uses FlightRecorder.EncodeEngineKey for engine/RCS/robotic keys.
            // Verify the encoding is deterministic.
            ulong key1 = FlightRecorder.EncodeEngineKey(42, 0);
            ulong key2 = FlightRecorder.EncodeEngineKey(42, 0);
            Assert.Equal(key1, key2);

            // Different module index -> different key
            ulong key3 = FlightRecorder.EncodeEngineKey(42, 1);
            Assert.NotEqual(key1, key3);

            // Different PID -> different key
            ulong key4 = FlightRecorder.EncodeEngineKey(43, 0);
            Assert.NotEqual(key1, key4);
        }

        [Fact]
        public void ComputeRcsPower_WorksForBackgroundRecording()
        {
            // BackgroundRecorder calls FlightRecorder.ComputeRcsPower
            // with thrust forces from background RCS modules.
            float power = FlightRecorder.ComputeRcsPower(
                new float[] { 1.0f, 1.0f, 1.0f, 1.0f }, 1.0f);

            // 4 thrusters at 1.0f each, thrusterPower = 1.0f
            // sum(forces) / (thrusterPower * numForces) = 4.0 / 4.0 = 1.0
            Assert.Equal(1.0f, power);
        }

        [Fact]
        public void ComputeRcsPower_GuardsZeroThrusterPower()
        {
            // thrusterPower = 0 should return 0 (not throw)
            float power = FlightRecorder.ComputeRcsPower(
                new float[] { 1.0f }, 0f);

            Assert.Equal(0f, power);
        }

        [Fact]
        public void ComputeRcsPower_GuardsEmptyForces()
        {
            float power = FlightRecorder.ComputeRcsPower(
                new float[] { }, 1.0f);

            Assert.Equal(0f, power);
        }

        #endregion

        #region 13.1 Orbital Checkpoint Capture

        [Fact]
        public void IsOrbitalCheckpointSituation_Orbiting_ReturnsTrue()
        {
            Assert.True(BackgroundRecorder.IsOrbitalCheckpointSituation(
                Vessel.Situations.ORBITING));
        }

        [Fact]
        public void IsOrbitalCheckpointSituation_SubOrbital_ReturnsTrue()
        {
            Assert.True(BackgroundRecorder.IsOrbitalCheckpointSituation(
                Vessel.Situations.SUB_ORBITAL));
        }

        [Fact]
        public void IsOrbitalCheckpointSituation_Escaping_ReturnsTrue()
        {
            Assert.True(BackgroundRecorder.IsOrbitalCheckpointSituation(
                Vessel.Situations.ESCAPING));
        }

        [Fact]
        public void IsOrbitalCheckpointSituation_Landed_ReturnsFalse()
        {
            Assert.False(BackgroundRecorder.IsOrbitalCheckpointSituation(
                Vessel.Situations.LANDED));
        }

        [Fact]
        public void IsOrbitalCheckpointSituation_Splashed_ReturnsFalse()
        {
            Assert.False(BackgroundRecorder.IsOrbitalCheckpointSituation(
                Vessel.Situations.SPLASHED));
        }

        [Fact]
        public void IsOrbitalCheckpointSituation_Flying_ReturnsFalse()
        {
            Assert.False(BackgroundRecorder.IsOrbitalCheckpointSituation(
                Vessel.Situations.FLYING));
        }

        [Fact]
        public void IsOrbitalCheckpointSituation_PreLaunch_ReturnsFalse()
        {
            Assert.False(BackgroundRecorder.IsOrbitalCheckpointSituation(
                Vessel.Situations.PRELAUNCH));
        }

        [Fact]
        public void ShouldPersistNoPayloadOnRailsBoundaryTrackSection_AtmoToSurfaceWithoutPayload_ReturnsTrue()
        {
            Assert.True(BackgroundRecorder.ShouldPersistNoPayloadOnRailsBoundaryTrackSection(
                SegmentEnvironment.Atmospheric,
                SegmentEnvironment.SurfaceStationary,
                willHavePlayableOnRailsPayload: false));
        }

        [Fact]
        public void ShouldPersistNoPayloadOnRailsBoundaryTrackSection_SameCoarseClass_ReturnsFalse()
        {
            Assert.False(BackgroundRecorder.ShouldPersistNoPayloadOnRailsBoundaryTrackSection(
                SegmentEnvironment.SurfaceMobile,
                SegmentEnvironment.SurfaceStationary,
                willHavePlayableOnRailsPayload: false));
        }

        [Fact]
        public void ShouldPersistNoPayloadOnRailsBoundaryTrackSection_WithPlayablePayload_ReturnsFalse()
        {
            Assert.False(BackgroundRecorder.ShouldPersistNoPayloadOnRailsBoundaryTrackSection(
                SegmentEnvironment.Atmospheric,
                SegmentEnvironment.SurfaceStationary,
                willHavePlayableOnRailsPayload: true));
        }

        [Fact]
        public void CheckpointAllVessels_NoOpenSegments_SkipsAll()
        {
            // Arrange: tree with two background vessels, neither has open orbit segments
            // (constructor creates minimal state with hasOpenOrbitSegment=false)
            var tree = MakeTree((100, "rec_bg1"), (200, "rec_bg2"));
            var bgRecorder = new BackgroundRecorder(tree);
            bgRecorder.SetVesselFinderForTesting(pid => null); // no KSP runtime

            // Sanity: neither has open orbit segment
            Assert.False(bgRecorder.GetOnRailsHasOpenSegment(100));
            Assert.False(bgRecorder.GetOnRailsHasOpenSegment(200));

            // Act: checkpoint at UT=500
            bgRecorder.CheckpointAllVessels(500.0);

            // Assert: no orbit segments added (nothing to close/reopen)
            Assert.Empty(tree.Recordings["rec_bg1"].OrbitSegments);
            Assert.Empty(tree.Recordings["rec_bg2"].OrbitSegments);
        }

        [Fact]
        public void CheckpointAllVessels_WithOpenSegment_ClosesSegmentAndSkipsReopenWhenVesselNotFound()
        {
            // Arrange: tree with one background vessel with an open orbit segment
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);
            bgRecorder.SetVesselFinderForTesting(pid => null); // no KSP runtime

            // Inject an open orbit segment (simulating a vessel that went on rails)
            var segment = new OrbitSegment
            {
                startUT = 300.0,
                inclination = 45.0,
                eccentricity = 0.01,
                semiMajorAxis = 700000.0,
                longitudeOfAscendingNode = 90.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.5,
                epoch = 300.0,
                bodyName = "Kerbin"
            };
            bgRecorder.InjectOpenOrbitSegmentForTesting(100, segment);
            Assert.True(bgRecorder.GetOnRailsHasOpenSegment(100));

            // Act: checkpoint at UT=500
            bgRecorder.CheckpointAllVessels(500.0);

            // Assert: the segment was closed (endUT set to checkpoint UT)
            Assert.Single(tree.Recordings["rec_bg1"].OrbitSegments);
            var closedSegment = tree.Recordings["rec_bg1"].OrbitSegments[0];
            Assert.Equal(300.0, closedSegment.startUT);
            Assert.Equal(500.0, closedSegment.endUT);
            Assert.Equal("Kerbin", closedSegment.bodyName);

            // Segment is closed but NOT reopened (vessel not found)
            Assert.False(bgRecorder.GetOnRailsHasOpenSegment(100));
        }

        [Fact]
        public void CheckpointAllVessels_EmptyBackgroundMap_DoesNotThrow()
        {
            var tree = MakeTree(); // no background vessels
            var bgRecorder = new BackgroundRecorder(tree);
            bgRecorder.SetVesselFinderForTesting(pid => null);

            // Should not throw
            bgRecorder.CheckpointAllVessels(100.0);
        }

        [Fact]
        public void CheckpointAllVessels_MixedStates_OnlyClosesOpenSegments()
        {
            // Arrange: two background vessels — one with open segment, one without
            var tree = MakeTree((100, "rec_bg1"), (200, "rec_bg2"));
            var bgRecorder = new BackgroundRecorder(tree);
            bgRecorder.SetVesselFinderForTesting(pid => null); // no KSP runtime

            // Only vessel 100 has an open orbit segment
            var segment = new OrbitSegment
            {
                startUT = 300.0,
                inclination = 30.0,
                eccentricity = 0.0,
                semiMajorAxis = 600000.0,
                longitudeOfAscendingNode = 0.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.0,
                epoch = 300.0,
                bodyName = "Mun"
            };
            bgRecorder.InjectOpenOrbitSegmentForTesting(100, segment);

            // Act
            bgRecorder.CheckpointAllVessels(400.0);

            // Assert: vessel 100 had its segment closed
            Assert.Single(tree.Recordings["rec_bg1"].OrbitSegments);
            Assert.Equal(400.0, tree.Recordings["rec_bg1"].OrbitSegments[0].endUT);
            Assert.Equal("Mun", tree.Recordings["rec_bg1"].OrbitSegments[0].bodyName);

            // Vessel 200 had no open segment, so no orbit segments were added
            Assert.Empty(tree.Recordings["rec_bg2"].OrbitSegments);
        }

        [Fact]
        public void CheckpointAllVessels_LogsCheckpointCounts()
        {
            // Arrange: capture log output
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            try
            {
                var tree = MakeTree((100, "rec_bg1"), (200, "rec_bg2"));
                var bgRecorder = new BackgroundRecorder(tree);
                bgRecorder.SetVesselFinderForTesting(pid => null); // no KSP runtime

                // One vessel with open segment, one without
                bgRecorder.InjectOpenOrbitSegmentForTesting(100, new OrbitSegment
                {
                    startUT = 100.0, bodyName = "Kerbin"
                });

                // Act
                bgRecorder.CheckpointAllVessels(200.0);

                // Assert: log should contain the summary with correct counts
                // vessel 100 has open segment but no vessel found -> skippedNoVessel=1
                // vessel 200 has no open segment -> skippedNotOrbital=1
                Assert.Contains(logLines, l =>
                    l.Contains("[BgRecorder]") &&
                    l.Contains("CheckpointAllVessels") &&
                    l.Contains("skippedNotOrbital=1") &&
                    l.Contains("skippedNoVessel=1"));
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
                ParsekLog.SuppressLogging = true;
            }
        }

        [Fact]
        public void CheckpointAllVessels_RepeatedCallsSameShape_RateLimitedToOneLine()
        {
            // Bug #592: KSP's onTimeWarpRateChanged GameEvent fires very
            // chattily — a single 30-min playtest produced 1122 identical
            // "CheckpointAllVessels at UT=N: checkpointed=0, ..." lines from
            // ~1090 redundant 1.0x->1.0x event re-fires. The summary now
            // routes through VerboseRateLimited keyed by the (checkpointed,
            // skippedNotOrbital, skippedNoVessel) shape so a burst of
            // identical no-op summaries collapses into one line per window
            // while a real change in counts still surfaces immediately on the
            // first call.
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            try
            {
                var tree = MakeTree((100, "rec_bg1"));
                var bgRecorder = new BackgroundRecorder(tree);
                bgRecorder.SetVesselFinderForTesting(pid => null);

                // Call CheckpointAllVessels 50 times back-to-back with the
                // same UT and the same recording-tree shape — every call
                // produces the exact same (0, 1, 0) summary tuple.
                for (int i = 0; i < 50; i++)
                {
                    bgRecorder.CheckpointAllVessels(100.0);
                }

                int summaryLines = logLines.Count(l =>
                    l.Contains("[BgRecorder]") &&
                    l.Contains("CheckpointAllVessels at UT=") &&
                    l.Contains("checkpointed=0") &&
                    l.Contains("skippedNotOrbital=1") &&
                    l.Contains("skippedNoVessel=0"));
                Assert.Equal(1, summaryLines);
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
                ParsekLog.SuppressLogging = true;
            }
        }

        [Fact]
        public void CheckpointAllVessels_ClosesSegmentEvenWhenVesselNotFound()
        {
            // Verifies that the segment is always closed at checkpoint UT,
            // preserving recorded orbital data, even if vessel can't be found
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);
            bgRecorder.SetVesselFinderForTesting(pid => null);

            bgRecorder.InjectOpenOrbitSegmentForTesting(100, new OrbitSegment
            {
                startUT = 200.0,
                semiMajorAxis = 750000.0,
                bodyName = "Kerbin"
            });

            bgRecorder.CheckpointAllVessels(350.0);

            // Segment was closed with correct endUT
            Assert.Single(tree.Recordings["rec_bg1"].OrbitSegments);
            Assert.Equal(200.0, tree.Recordings["rec_bg1"].OrbitSegments[0].startUT);
            Assert.Equal(350.0, tree.Recordings["rec_bg1"].OrbitSegments[0].endUT);

            // No new segment opened (vessel not found)
            Assert.False(bgRecorder.GetOnRailsHasOpenSegment(100));
        }

        [Fact]
        public void CheckpointAllVessels_UpdatesExplicitEndUT()
        {
            // Closing an orbit segment also updates ExplicitEndUT via CloseOrbitSegment
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);
            bgRecorder.SetVesselFinderForTesting(pid => null);

            bgRecorder.InjectOpenOrbitSegmentForTesting(100, new OrbitSegment
            {
                startUT = 100.0, bodyName = "Kerbin"
            });

            bgRecorder.CheckpointAllVessels(500.0);

            Assert.Equal(500.0, tree.Recordings["rec_bg1"].ExplicitEndUT);
        }

        [Fact]
        public void InjectOpenOrbitSegmentForTesting_SetsOpenSegmentState()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            // Initially no open segment
            Assert.False(bgRecorder.GetOnRailsHasOpenSegment(100));

            // Inject
            bgRecorder.InjectOpenOrbitSegmentForTesting(100, new OrbitSegment
            {
                startUT = 100.0,
                semiMajorAxis = 700000.0,
                bodyName = "Kerbin"
            });

            // Now has open segment
            Assert.True(bgRecorder.GetOnRailsHasOpenSegment(100));
        }

        [Fact]
        public void InjectOpenOrbitSegmentForTesting_NoState_DoesNotThrow()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            // PID 999 is not in the on-rails state — should not throw
            bgRecorder.InjectOpenOrbitSegmentForTesting(999, new OrbitSegment());

            // No state was created (only modifies existing state)
            Assert.False(bgRecorder.HasOnRailsState(999));
        }

        #endregion

        #region MergeInheritedEngineState (Bug #298)

        [Fact]
        public void MergeInheritedEngineState_NullInherited_ReturnsZero()
        {
            var activeEngineKeys = new HashSet<ulong>();
            var lastThrottle = new Dictionary<ulong, float>();
            var activeRcsKeys = new HashSet<ulong>();
            var lastRcsThrottle = new Dictionary<ulong, float>();
            var childPartPids = new HashSet<uint> { 100 };

            int merged = BackgroundRecorder.MergeInheritedEngineState(
                null, activeEngineKeys, lastThrottle,
                activeRcsKeys, lastRcsThrottle, childPartPids);

            Assert.Equal(0, merged);
            Assert.Empty(activeEngineKeys);
            Assert.Empty(activeRcsKeys);
        }

        [Fact]
        public void MergeInheritedEngineState_EngineOnChild_Merged()
        {
            // Parent had engine active on part pid=500, moduleIndex=0
            ulong key = FlightRecorder.EncodeEngineKey(500, 0);
            var inherited = new InheritedEngineState
            {
                activeEngineKeys = new HashSet<ulong> { key },
                engineThrottles = new Dictionary<ulong, float> { { key, 0.85f } },
                activeRcsKeys = null,
                rcsThrottles = null
            };

            var activeEngineKeys = new HashSet<ulong>();
            var lastThrottle = new Dictionary<ulong, float>();
            var activeRcsKeys = new HashSet<ulong>();
            var lastRcsThrottle = new Dictionary<ulong, float>();
            // Child vessel has part pid=500
            var childPartPids = new HashSet<uint> { 500, 600 };

            int merged = BackgroundRecorder.MergeInheritedEngineState(
                inherited, activeEngineKeys, lastThrottle,
                activeRcsKeys, lastRcsThrottle, childPartPids);

            Assert.Equal(1, merged);
            Assert.Contains(key, activeEngineKeys);
            Assert.Equal(0.85f, lastThrottle[key]);
        }

        [Fact]
        public void MergeInheritedEngineState_EngineNotOnChild_Skipped()
        {
            // Parent had engine active on part pid=500
            ulong key = FlightRecorder.EncodeEngineKey(500, 0);
            var inherited = new InheritedEngineState
            {
                activeEngineKeys = new HashSet<ulong> { key },
                engineThrottles = new Dictionary<ulong, float> { { key, 1.0f } },
                activeRcsKeys = null,
                rcsThrottles = null
            };

            var activeEngineKeys = new HashSet<ulong>();
            var lastThrottle = new Dictionary<ulong, float>();
            var activeRcsKeys = new HashSet<ulong>();
            var lastRcsThrottle = new Dictionary<ulong, float>();
            // Child vessel does NOT have part pid=500
            var childPartPids = new HashSet<uint> { 600, 700 };

            int merged = BackgroundRecorder.MergeInheritedEngineState(
                inherited, activeEngineKeys, lastThrottle,
                activeRcsKeys, lastRcsThrottle, childPartPids);

            Assert.Equal(0, merged);
            Assert.Empty(activeEngineKeys);
        }

        [Fact]
        public void MergeInheritedEngineState_AlreadySeeded_UpgradesThrottle()
        {
            // Parent had engine at throttle 0.85, child seeded at 0.0 (KSP timing lag).
            // Inherited throttle should upgrade the zero to the parent's value.
            ulong key = FlightRecorder.EncodeEngineKey(500, 0);
            var inherited = new InheritedEngineState
            {
                activeEngineKeys = new HashSet<ulong> { key },
                engineThrottles = new Dictionary<ulong, float> { { key, 0.85f } },
                activeRcsKeys = null,
                rcsThrottles = null
            };

            var activeEngineKeys = new HashSet<ulong> { key }; // already seeded
            var lastThrottle = new Dictionary<ulong, float> { { key, 0.0f } }; // zero from SeedEngines
            var activeRcsKeys = new HashSet<ulong>();
            var lastRcsThrottle = new Dictionary<ulong, float>();
            var childPartPids = new HashSet<uint> { 500 };

            int merged = BackgroundRecorder.MergeInheritedEngineState(
                inherited, activeEngineKeys, lastThrottle,
                activeRcsKeys, lastRcsThrottle, childPartPids);

            Assert.Equal(1, merged); // throttle upgraded
            Assert.Equal(0.85f, lastThrottle[key]); // inherited value replaces zero
        }

        [Fact]
        public void MergeInheritedEngineState_AlreadySeeded_HigherThrottlePreserved()
        {
            // Parent had engine at throttle 0.5, child already seeded at 0.8 (live state is better).
            // Inherited throttle should NOT downgrade.
            ulong key = FlightRecorder.EncodeEngineKey(500, 0);
            var inherited = new InheritedEngineState
            {
                activeEngineKeys = new HashSet<ulong> { key },
                engineThrottles = new Dictionary<ulong, float> { { key, 0.5f } },
                activeRcsKeys = null,
                rcsThrottles = null
            };

            var activeEngineKeys = new HashSet<ulong> { key };
            var lastThrottle = new Dictionary<ulong, float> { { key, 0.8f } };
            var activeRcsKeys = new HashSet<ulong>();
            var lastRcsThrottle = new Dictionary<ulong, float>();
            var childPartPids = new HashSet<uint> { 500 };

            int merged = BackgroundRecorder.MergeInheritedEngineState(
                inherited, activeEngineKeys, lastThrottle,
                activeRcsKeys, lastRcsThrottle, childPartPids);

            Assert.Equal(0, merged); // not upgraded — existing is higher
            Assert.Equal(0.8f, lastThrottle[key]); // original preserved
        }

        [Fact]
        public void MergeInheritedEngineState_RcsOnChild_Merged()
        {
            // Parent had RCS active on part pid=300, moduleIndex=1
            ulong key = FlightRecorder.EncodeEngineKey(300, 1);
            var inherited = new InheritedEngineState
            {
                activeEngineKeys = null,
                engineThrottles = null,
                activeRcsKeys = new HashSet<ulong> { key },
                rcsThrottles = new Dictionary<ulong, float> { { key, 0.6f } }
            };

            var activeEngineKeys = new HashSet<ulong>();
            var lastThrottle = new Dictionary<ulong, float>();
            var activeRcsKeys = new HashSet<ulong>();
            var lastRcsThrottle = new Dictionary<ulong, float>();
            var childPartPids = new HashSet<uint> { 300 };

            int merged = BackgroundRecorder.MergeInheritedEngineState(
                inherited, activeEngineKeys, lastThrottle,
                activeRcsKeys, lastRcsThrottle, childPartPids);

            Assert.Equal(1, merged);
            Assert.Contains(key, activeRcsKeys);
            Assert.Equal(0.6f, lastRcsThrottle[key]);
        }

        [Fact]
        public void MergeInheritedEngineState_MixedEngineAndRcs_BothMerged()
        {
            // Parent had one engine and one RCS, both on child vessel
            ulong engineKey = FlightRecorder.EncodeEngineKey(500, 0);
            ulong rcsKey = FlightRecorder.EncodeEngineKey(600, 0);
            var inherited = new InheritedEngineState
            {
                activeEngineKeys = new HashSet<ulong> { engineKey },
                engineThrottles = new Dictionary<ulong, float> { { engineKey, 1.0f } },
                activeRcsKeys = new HashSet<ulong> { rcsKey },
                rcsThrottles = new Dictionary<ulong, float> { { rcsKey, 0.3f } }
            };

            var activeEngineKeys = new HashSet<ulong>();
            var lastThrottle = new Dictionary<ulong, float>();
            var activeRcsKeys = new HashSet<ulong>();
            var lastRcsThrottle = new Dictionary<ulong, float>();
            var childPartPids = new HashSet<uint> { 500, 600 };

            int merged = BackgroundRecorder.MergeInheritedEngineState(
                inherited, activeEngineKeys, lastThrottle,
                activeRcsKeys, lastRcsThrottle, childPartPids);

            Assert.Equal(2, merged);
            Assert.Contains(engineKey, activeEngineKeys);
            Assert.Contains(rcsKey, activeRcsKeys);
            Assert.Equal(1.0f, lastThrottle[engineKey]);
            Assert.Equal(0.3f, lastRcsThrottle[rcsKey]);
        }

        [Fact]
        public void MergeInheritedEngineState_MissingThrottleDict_DefaultsToOne()
        {
            // Parent had engine active but no throttle dictionary
            ulong key = FlightRecorder.EncodeEngineKey(500, 0);
            var inherited = new InheritedEngineState
            {
                activeEngineKeys = new HashSet<ulong> { key },
                engineThrottles = null, // no throttle data
                activeRcsKeys = null,
                rcsThrottles = null
            };

            var activeEngineKeys = new HashSet<ulong>();
            var lastThrottle = new Dictionary<ulong, float>();
            var activeRcsKeys = new HashSet<ulong>();
            var lastRcsThrottle = new Dictionary<ulong, float>();
            var childPartPids = new HashSet<uint> { 500 };

            int merged = BackgroundRecorder.MergeInheritedEngineState(
                inherited, activeEngineKeys, lastThrottle,
                activeRcsKeys, lastRcsThrottle, childPartPids);

            Assert.Equal(1, merged);
            Assert.Equal(1f, lastThrottle[key]); // throttle defaults to 1f when dict is null
        }

        [Fact]
        public void MergeInheritedEngineState_ThrottleDictPresentButKeyMissing_DefaultsToOne()
        {
            // Parent had engine active but throttle dict doesn't contain this key
            // (edge case: activeEngineKeys and engineThrottles out of sync)
            ulong key = FlightRecorder.EncodeEngineKey(500, 0);
            ulong otherKey = FlightRecorder.EncodeEngineKey(600, 0);
            var inherited = new InheritedEngineState
            {
                activeEngineKeys = new HashSet<ulong> { key },
                engineThrottles = new Dictionary<ulong, float> { { otherKey, 0.5f } }, // has entries, but NOT for 'key'
                activeRcsKeys = null,
                rcsThrottles = null
            };

            var activeEngineKeys = new HashSet<ulong>();
            var lastThrottle = new Dictionary<ulong, float>();
            var activeRcsKeys = new HashSet<ulong>();
            var lastRcsThrottle = new Dictionary<ulong, float>();
            var childPartPids = new HashSet<uint> { 500 };

            int merged = BackgroundRecorder.MergeInheritedEngineState(
                inherited, activeEngineKeys, lastThrottle,
                activeRcsKeys, lastRcsThrottle, childPartPids);

            Assert.Equal(1, merged);
            Assert.Equal(1f, lastThrottle[key]); // throttle defaults to 1f when key missing from dict
        }

        [Fact]
        public void MergeInheritedEngineState_MultipleEngines_OnlyChildPidsMerged()
        {
            // Parent had 3 engines, child vessel only has 2 of those parts
            ulong key1 = FlightRecorder.EncodeEngineKey(100, 0); // on child
            ulong key2 = FlightRecorder.EncodeEngineKey(200, 0); // NOT on child
            ulong key3 = FlightRecorder.EncodeEngineKey(300, 0); // on child
            var inherited = new InheritedEngineState
            {
                activeEngineKeys = new HashSet<ulong> { key1, key2, key3 },
                engineThrottles = new Dictionary<ulong, float>
                {
                    { key1, 0.9f }, { key2, 0.8f }, { key3, 0.7f }
                },
                activeRcsKeys = null,
                rcsThrottles = null
            };

            var activeEngineKeys = new HashSet<ulong>();
            var lastThrottle = new Dictionary<ulong, float>();
            var activeRcsKeys = new HashSet<ulong>();
            var lastRcsThrottle = new Dictionary<ulong, float>();
            var childPartPids = new HashSet<uint> { 100, 300 }; // no 200

            int merged = BackgroundRecorder.MergeInheritedEngineState(
                inherited, activeEngineKeys, lastThrottle,
                activeRcsKeys, lastRcsThrottle, childPartPids);

            Assert.Equal(2, merged);
            Assert.Contains(key1, activeEngineKeys);
            Assert.DoesNotContain(key2, activeEngineKeys);
            Assert.Contains(key3, activeEngineKeys);
            Assert.Equal(0.9f, lastThrottle[key1]);
            Assert.Equal(0.7f, lastThrottle[key3]);
        }

        [Fact]
        public void MergeInheritedEngineState_LogsPerItemAndSummary()
        {
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            try
            {
                ulong key = FlightRecorder.EncodeEngineKey(500, 2);
                var inherited = new InheritedEngineState
                {
                    activeEngineKeys = new HashSet<ulong> { key },
                    engineThrottles = new Dictionary<ulong, float> { { key, 0.75f } },
                    activeRcsKeys = null,
                    rcsThrottles = null
                };

                var activeEngineKeys = new HashSet<ulong>();
                var lastThrottle = new Dictionary<ulong, float>();
                var activeRcsKeys = new HashSet<ulong>();
                var lastRcsThrottle = new Dictionary<ulong, float>();
                var childPartPids = new HashSet<uint> { 500 };

                BackgroundRecorder.MergeInheritedEngineState(
                    inherited, activeEngineKeys, lastThrottle,
                    activeRcsKeys, lastRcsThrottle, childPartPids);

                // Per-item verbose log
                Assert.Contains(logLines, l =>
                    l.Contains("[BgRecorder]") &&
                    l.Contains("Inherited engine key merged") &&
                    l.Contains("pid=500") &&
                    l.Contains("midx=2") &&
                    l.Contains("#298"));
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
                ParsekLog.SuppressLogging = true;
            }
        }

        [Fact]
        public void MergeInheritedEngineState_NonOperationalOnChild_SkippedT58()
        {
            // T58: Parent had engine running at 0.8, child has the engine part (in allEngineKeys)
            // but SeedEngines determined it's non-operational (not in activeEngineKeys).
            // This happens after staging when fuel is severed. Should NOT inherit.
            ulong key = FlightRecorder.EncodeEngineKey(500, 0);
            var inherited = new InheritedEngineState
            {
                activeEngineKeys = new HashSet<ulong> { key },
                engineThrottles = new Dictionary<ulong, float> { { key, 0.8f } }
            };
            var activeEngineKeys = new HashSet<ulong>(); // NOT seeded (non-operational)
            var lastThrottle = new Dictionary<ulong, float>();
            var activeRcsKeys = new HashSet<ulong>();
            var lastRcsThrottle = new Dictionary<ulong, float>();
            var childPartPids = new HashSet<uint> { 500 };
            var allEngineKeys = new HashSet<ulong> { key }; // SeedEngines found it, but not operational

            int merged = BackgroundRecorder.MergeInheritedEngineState(
                inherited, activeEngineKeys, lastThrottle,
                activeRcsKeys, lastRcsThrottle, childPartPids, allEngineKeys);

            Assert.Equal(0, merged);
            Assert.DoesNotContain(key, activeEngineKeys);
            Assert.False(lastThrottle.ContainsKey(key));
        }

        [Fact]
        public void MergeInheritedEngineState_NotInAllEngineKeys_StillMergedT58()
        {
            // Engine not found by SeedEngines at all (KSP timing) — should still inherit.
            ulong key = FlightRecorder.EncodeEngineKey(500, 0);
            var inherited = new InheritedEngineState
            {
                activeEngineKeys = new HashSet<ulong> { key },
                engineThrottles = new Dictionary<ulong, float> { { key, 0.8f } }
            };
            var activeEngineKeys = new HashSet<ulong>();
            var lastThrottle = new Dictionary<ulong, float>();
            var activeRcsKeys = new HashSet<ulong>();
            var lastRcsThrottle = new Dictionary<ulong, float>();
            var childPartPids = new HashSet<uint> { 500 };
            var allEngineKeys = new HashSet<ulong>(); // SeedEngines didn't find this engine at all

            int merged = BackgroundRecorder.MergeInheritedEngineState(
                inherited, activeEngineKeys, lastThrottle,
                activeRcsKeys, lastRcsThrottle, childPartPids, allEngineKeys);

            Assert.Equal(1, merged);
            Assert.Contains(key, activeEngineKeys);
            Assert.Equal(0.8f, lastThrottle[key]);
        }

        [Fact]
        public void MergeInheritedEngineState_NullAllEngineKeys_BackwardCompatT58()
        {
            // allEngineKeys=null (backward compat) — should merge like before.
            ulong key = FlightRecorder.EncodeEngineKey(500, 0);
            var inherited = new InheritedEngineState
            {
                activeEngineKeys = new HashSet<ulong> { key },
                engineThrottles = new Dictionary<ulong, float> { { key, 0.7f } }
            };
            var activeEngineKeys = new HashSet<ulong>();
            var lastThrottle = new Dictionary<ulong, float>();
            var activeRcsKeys = new HashSet<ulong>();
            var lastRcsThrottle = new Dictionary<ulong, float>();
            var childPartPids = new HashSet<uint> { 500 };

            int merged = BackgroundRecorder.MergeInheritedEngineState(
                inherited, activeEngineKeys, lastThrottle,
                activeRcsKeys, lastRcsThrottle, childPartPids, allEngineKeys: null);

            Assert.Equal(1, merged);
            Assert.Contains(key, activeEngineKeys);
        }

        [Fact]
        public void MergeInheritedEngineState_NonOperationalLogs_T58()
        {
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                ulong key = FlightRecorder.EncodeEngineKey(500, 0);
                var inherited = new InheritedEngineState
                {
                    activeEngineKeys = new HashSet<ulong> { key },
                    engineThrottles = new Dictionary<ulong, float> { { key, 0.8f } }
                };
                var activeEngineKeys = new HashSet<ulong>();
                var lastThrottle = new Dictionary<ulong, float>();
                var activeRcsKeys = new HashSet<ulong>();
                var lastRcsThrottle = new Dictionary<ulong, float>();
                var childPartPids = new HashSet<uint> { 500 };
                var allEngineKeys = new HashSet<ulong> { key };

                BackgroundRecorder.MergeInheritedEngineState(
                    inherited, activeEngineKeys, lastThrottle,
                    activeRcsKeys, lastRcsThrottle, childPartPids, allEngineKeys);

                Assert.Contains(logLines, l =>
                    l.Contains("[BgRecorder]") &&
                    l.Contains("non-operational on child") &&
                    l.Contains("T58"));
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
                ParsekLog.SuppressLogging = true;
            }
        }

        private static RecordingFinalizationCache MakeFinalizationCache(
            string recordingId,
            uint vesselPid,
            TerminalState terminalState,
            double terminalUT,
            params OrbitSegment[] predictedSegments)
        {
            var cache = new RecordingFinalizationCache
            {
                RecordingId = recordingId,
                VesselPersistentId = vesselPid,
                Owner = FinalizationCacheOwner.BackgroundLoaded,
                Status = FinalizationCacheStatus.Fresh,
                CachedAtUT = terminalUT - 5.0,
                RefreshReason = "unit-test",
                LastObservedUT = terminalUT - 5.0,
                LastObservedBodyName = "Kerbin",
                LastSituation = terminalState == TerminalState.Orbiting
                    ? Vessel.Situations.ORBITING
                    : Vessel.Situations.FLYING,
                LastWasInAtmosphere = terminalState == TerminalState.Destroyed,
                TailStartsAtUT = predictedSegments != null && predictedSegments.Length > 0
                    ? predictedSegments[0].startUT
                    : terminalUT,
                TerminalUT = terminalUT,
                TerminalState = terminalState,
                TerminalBodyName = "Kerbin",
                PredictedSegments = new List<OrbitSegment>()
            };

            if (predictedSegments != null)
            {
                for (int i = 0; i < predictedSegments.Length; i++)
                    cache.PredictedSegments.Add(predictedSegments[i]);
            }

            return cache;
        }

        private static OrbitSegment Segment(double startUT, double endUT)
        {
            return new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = startUT,
                endUT = endUT,
                semiMajorAxis = 700000.0,
                eccentricity = 0.1,
                inclination = 2.0,
                longitudeOfAscendingNode = 3.0,
                argumentOfPeriapsis = 4.0,
                meanAnomalyAtEpoch = 0.5,
                epoch = startUT,
                isPredicted = true
            };
        }

        #endregion
    }
}
