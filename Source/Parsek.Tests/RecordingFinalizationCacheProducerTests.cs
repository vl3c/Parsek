using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class RecordingFinalizationCacheProducerTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RecordingFinalizationCacheProducerTests()
        {
            RecordingFinalizationCacheProducer.ResetForTesting();
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            RecordingFinalizationCacheProducer.ResetForTesting();
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void ShouldRefresh_StableCoastDigestUnchanged_DoesNotRefreshAfterCadence()
        {
            bool refresh = RecordingFinalizationCacheProducer.ShouldRefresh(
                lastRefreshUT: 100.0,
                currentUT: 120.0,
                intervalUT: 5.0,
                force: false,
                previousDigest: "Kerbin|sit=ORBITING|atmo=False|thrust=False",
                currentDigest: "Kerbin|sit=ORBITING|atmo=False|thrust=False",
                requiresPeriodicRefresh: false);

            Assert.False(refresh);
        }

        [Fact]
        public void ShouldRefresh_DynamicState_RefreshesAfterCadence()
        {
            bool beforeCadence = RecordingFinalizationCacheProducer.ShouldRefresh(
                100.0, 104.0, 5.0, false, "same", "same", requiresPeriodicRefresh: true);
            bool afterCadence = RecordingFinalizationCacheProducer.ShouldRefresh(
                100.0, 105.0, 5.0, false, "same", "same", requiresPeriodicRefresh: true);

            Assert.False(beforeCadence);
            Assert.True(afterCadence);
        }

        [Fact]
        public void ShouldRefresh_ForceBypassesDigestAndCadence()
        {
            bool refresh = RecordingFinalizationCacheProducer.ShouldRefresh(
                100.0, 100.5, 5.0, true, "same", "same", requiresPeriodicRefresh: false);

            Assert.True(refresh);
        }

        [Fact]
        public void ShouldRefresh_DigestChangeRefreshesImmediately()
        {
            bool refresh = RecordingFinalizationCacheProducer.ShouldRefresh(
                100.0, 101.0, 5.0, false,
                "Kerbin|sit=ORBITING|atmo=False|thrust=False",
                "Mun|sit=ORBITING|atmo=False|thrust=False",
                requiresPeriodicRefresh: false);

            Assert.True(refresh);
        }

        [Fact]
        public void HasMeaningfulThrust_UsesActiveEngineAndRcsThrottle()
        {
            var engineKeys = new HashSet<ulong> { 1ul };
            var engineThrottle = new Dictionary<ulong, float> { [1ul] = 0.0f };
            var rcsKeys = new HashSet<ulong> { 2ul };
            var rcsThrottle = new Dictionary<ulong, float> { [2ul] = 0.25f };

            Assert.True(RecordingFinalizationCacheProducer.HasMeaningfulThrust(
                engineKeys,
                engineThrottle,
                rcsKeys,
                rcsThrottle));
        }

        [Fact]
        public void IsSurfaceTerminalSituation_ClassifiesLandedSplashedAndPrelaunch()
        {
            Assert.True(RecordingFinalizationCacheProducer.IsSurfaceTerminalSituation(Vessel.Situations.LANDED));
            Assert.True(RecordingFinalizationCacheProducer.IsSurfaceTerminalSituation(Vessel.Situations.SPLASHED));
            Assert.True(RecordingFinalizationCacheProducer.IsSurfaceTerminalSituation(Vessel.Situations.PRELAUNCH));
            Assert.False(RecordingFinalizationCacheProducer.IsSurfaceTerminalSituation(Vessel.Situations.ORBITING));
        }

        [Fact]
        public void LogRefreshSummary_ZeroRecordNoClassification_Suppressed()
        {
            RecordingFinalizationCacheProducer.LogRefreshSummary(
                FinalizationCacheOwner.BackgroundOnRails,
                "unit-zero-records",
                recordingsExamined: 0,
                alreadyClassified: 0,
                newlyClassified: 0);

            Assert.Equal(0, CountLogLines(
                "[Parsek][INFO][Extrapolator]",
                "FinalizerCache refresh summary",
                "reason=unit-zero-records"));
        }

        /// <summary>
        /// 2026-04-25 log-hygiene fix: a no-delta refresh pass
        /// (newlyClassified=0) routes through Verbose instead of Info so the
        /// 156× per-session periodic Info spam stops without losing the
        /// diagnostic line entirely. Pinned shape:
        /// <c>recordingsExamined=1 alreadyClassified=0 newlyClassified=0</c>
        /// (matches the playtest's most common spammy form).
        /// </summary>
        [Fact]
        public void LogRefreshSummary_NoDeltaPass_RoutesThroughVerbose()
        {
            RecordingFinalizationCacheProducer.LogRefreshSummary(
                FinalizationCacheOwner.ActiveRecorder,
                "periodic",
                recordingsExamined: 1,
                alreadyClassified: 0,
                newlyClassified: 0);

            Assert.Equal(0, CountLogLines(
                "[Parsek][INFO][Extrapolator]",
                "FinalizerCache refresh summary",
                "reason=periodic",
                "newlyClassified=0"));
            Assert.Equal(1, CountLogLines(
                "[Parsek][VERBOSE][Extrapolator]",
                "FinalizerCache refresh summary",
                "reason=periodic",
                "alreadyClassified=0",
                "newlyClassified=0"));
        }

        /// <summary>
        /// A refresh pass that produces a fresh classification keeps Info,
        /// because crossing from unclassified to classified is the milestone
        /// the diagnostic was originally built to surface.
        /// </summary>
        [Fact]
        public void LogRefreshSummary_FreshClassification_StaysInfo()
        {
            RecordingFinalizationCacheProducer.LogRefreshSummary(
                FinalizationCacheOwner.ActiveRecorder,
                "periodic",
                recordingsExamined: 1,
                alreadyClassified: 0,
                newlyClassified: 1);

            Assert.Equal(1, CountLogLines(
                "[Parsek][INFO][Extrapolator]",
                "FinalizerCache refresh summary",
                "reason=periodic",
                "newlyClassified=1"));
        }

        /// <summary>
        /// Backstop: every Nth no-delta pass still fires Info so a long
        /// session has discoverable markers in case the diagnostic is needed
        /// post-hoc. Lines emitted at Info under the backstop are tagged with
        /// <c>backstop=every64thNoDeltaPass</c>.
        /// </summary>
        [Fact]
        public void LogRefreshSummary_NoDeltaBackstop_PromotesEvery64thPassToInfo()
        {
            // 64 no-delta passes is the configured cadence; the 64th
            // emission promotes back to Info, the next 63 stay Verbose.
            for (int i = 0; i < 64; i++)
            {
                RecordingFinalizationCacheProducer.LogRefreshSummary(
                    FinalizationCacheOwner.ActiveRecorder,
                    "periodic",
                    recordingsExamined: 1,
                    alreadyClassified: 0,
                    newlyClassified: 0);
            }

            Assert.Equal(63, CountLogLines(
                "[Parsek][VERBOSE][Extrapolator]",
                "FinalizerCache refresh summary",
                "reason=periodic"));
            Assert.Equal(1, CountLogLines(
                "[Parsek][INFO][Extrapolator]",
                "FinalizerCache refresh summary",
                "backstop=every64thNoDeltaPass",
                "suppressedNoDeltaPasses=63"));
        }

        [Fact]
        public void TryBuildFromLiveVessel_SurfaceTerminal_CachesSurfaceMetadataAndLogs()
        {
            var vessel = new FakeVesselView
            {
                PersistentIdValue = 101u,
                SituationValue = Vessel.Situations.LANDED,
                BodyNameValue = "Kerbin",
                LatitudeValue = 1.25,
                LongitudeValue = -74.5,
                AltitudeValue = 12.0,
                HeightFromTerrainValue = 8.5,
                SurfaceRelativeRotationValue = Quaternion.identity
            };
            var recording = new Recording
            {
                RecordingId = "rec-live-landed",
                VesselPersistentId = 101u
            };

            bool built = RecordingFinalizationCacheProducer.TryBuildFromLiveVessel(
                recording,
                vessel,
                125.0,
                FinalizationCacheOwner.ActiveRecorder,
                "unit-live-surface",
                hasMeaningfulThrust: false,
                out RecordingFinalizationCache cache);

            Assert.True(built);
            Assert.Equal(FinalizationCacheStatus.Fresh, cache.Status);
            Assert.Equal(TerminalState.Landed, cache.TerminalState);
            Assert.Equal(125.0, cache.TerminalUT);
            Assert.True(cache.TerminalPosition.HasValue);
            Assert.Equal(1.25, cache.TerminalPosition.Value.latitude);
            Assert.Equal(8.5, cache.TerrainHeightAtEnd);
            Assert.Contains(logLines, line =>
                line.Contains("[Parsek][VERBOSE][FinalizerCache]") &&
                line.Contains("Refresh accepted") &&
                line.Contains("rec=rec-live-landed") &&
                line.Contains("terminal=Landed"));
        }

        [Fact]
        public void TryBuildFromLiveVessel_ExtrapolatorBranch_CachesDefaultFinalizerTailAndLogs()
        {
            RecordingFinalizationCacheProducer.TryBuildDefaultFinalizationResultOverrideForTesting =
                (Recording recording, Vessel vessel, double refreshUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    result = new IncompleteBallisticFinalizationResult
                    {
                        terminalState = TerminalState.Destroyed,
                        terminalUT = 180.0,
                        extrapolatedSegmentCount = 1,
                        appendedOrbitSegments = new List<OrbitSegment>
                        {
                            new OrbitSegment
                            {
                                bodyName = "Kerbin",
                                startUT = refreshUT,
                                endUT = 180.0,
                                semiMajorAxis = 650000.0,
                                eccentricity = 0.2,
                                inclination = 1.0
                            }
                        }
                    };
                    return true;
                };

            var vessel = MakeFlyingView(202u, packed: false, loaded: true, inAtmosphere: false);
            var recording = new Recording
            {
                RecordingId = "rec-live-extrapolate",
                VesselPersistentId = 202u,
                ExplicitEndUT = 100.0
            };

            bool built = RecordingFinalizationCacheProducer.TryBuildFromLiveVessel(
                recording,
                vessel,
                120.0,
                FinalizationCacheOwner.BackgroundLoaded,
                "unit-live-extrapolate",
                hasMeaningfulThrust: true,
                out RecordingFinalizationCache cache);

            Assert.True(built);
            Assert.Equal(FinalizationCacheStatus.Fresh, cache.Status);
            Assert.Equal(TerminalState.Destroyed, cache.TerminalState);
            Assert.Equal(180.0, cache.TerminalUT);
            Assert.Single(cache.PredictedSegments);
            Assert.True(cache.PredictedSegments[0].isPredicted);
            Assert.Equal(120.0, cache.TailStartsAtUT);
            Assert.Contains(logLines, line =>
                line.Contains("[Parsek][VERBOSE][FinalizerCache]") &&
                line.Contains("Refresh accepted") &&
                line.Contains("rec=rec-live-extrapolate") &&
                line.Contains("segments=1"));
        }

        [Fact]
        public void TryBuildFromLiveVessel_AlreadyDestroyed_SkipsAcrossPeriodicTicksWithoutMutatingTerminal()
        {
            int defaultFinalizerCalls = 0;
            RecordingFinalizationCacheProducer.TryBuildDefaultFinalizationResultOverrideForTesting =
                (Recording recording, Vessel vessel, double refreshUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    defaultFinalizerCalls++;
                    result = new IncompleteBallisticFinalizationResult
                    {
                        terminalState = TerminalState.Destroyed,
                        terminalUT = 999.0
                    };
                    return true;
                };

            var vessel = MakeFlyingView(404u, packed: false, loaded: true, inAtmosphere: false);
            var recording = new Recording
            {
                RecordingId = "rec-already-destroyed",
                VesselPersistentId = 404u,
                TerminalStateValue = TerminalState.Destroyed,
                ExplicitEndUT = 180.0,
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 650000.0
            };

            bool firstBuilt = RecordingFinalizationCacheProducer.TryBuildFromLiveVessel(
                recording,
                vessel,
                120.0,
                FinalizationCacheOwner.BackgroundLoaded,
                "background_periodic",
                hasMeaningfulThrust: false,
                out RecordingFinalizationCache firstCache);
            bool secondBuilt = RecordingFinalizationCacheProducer.TryBuildFromLiveVessel(
                recording,
                vessel,
                126.0,
                FinalizationCacheOwner.BackgroundLoaded,
                "background_periodic",
                hasMeaningfulThrust: false,
                out RecordingFinalizationCache secondCache);

            Assert.False(firstBuilt);
            Assert.False(secondBuilt);
            Assert.Equal(0, defaultFinalizerCalls);
            Assert.Equal(180.0, recording.ExplicitEndUT);
            Assert.Equal("Kerbin", recording.TerminalOrbitBody);
            Assert.Equal(FinalizationCacheStatus.Failed, firstCache.Status);
            Assert.Equal(FinalizationCacheStatus.Failed, secondCache.Status);
            Assert.Equal(RecordingFinalizationCacheProducer.AlreadyClassifiedDestroyedDeclineReason, firstCache.DeclineReason);
            Assert.Equal(RecordingFinalizationCacheProducer.AlreadyClassifiedDestroyedDeclineReason, secondCache.DeclineReason);
            Assert.Equal(180.0, firstCache.TerminalUT);
            Assert.Equal(180.0, secondCache.TerminalUT);
            Assert.True(RecordingFinalizationCacheProducer.IsAlreadyClassifiedDestroyedSkip(firstCache));
            Assert.True(RecordingFinalizationCacheProducer.IsAlreadyClassifiedDestroyedSkip(secondCache));
            Assert.Contains(logLines, line =>
                line.Contains("[Parsek][VERBOSE][Extrapolator]") &&
                line.Contains("already classified Destroyed at terminalUT=180.0") &&
                line.Contains("rec-already-destroyed") &&
                line.Contains("skipping re-run"));
            // newlyClassified=0 routes through Verbose now (post-2026-04-25
            // log-hygiene pass) so 156 periodic no-op refreshes per session
            // no longer flood Info. Already-classified-only is still a no-op
            // from the player's perspective; only fresh classifications keep
            // their Info status.
            Assert.Equal(0, CountLogLines(
                "[Parsek][INFO][Extrapolator]",
                "FinalizerCache refresh summary",
                "recordingsExamined=1",
                "alreadyClassified=1",
                "newlyClassified=0"));
            Assert.Equal(2, CountLogLines(
                "[Parsek][VERBOSE][Extrapolator]",
                "FinalizerCache refresh summary",
                "recordingsExamined=1",
                "alreadyClassified=1",
                "newlyClassified=0"));
        }

        [Fact]
        public void TryBuildAlreadyClassifiedDestroyedSkip_NonFiniteTerminalUsesRefreshUt()
        {
            var recording = new Recording
            {
                RecordingId = "rec-destroyed-nonfinite-terminal",
                VesselPersistentId = 406u,
                TerminalStateValue = TerminalState.Destroyed,
                ExplicitEndUT = double.NaN
            };
            recording.Points.Add(new TrajectoryPoint { ut = double.NaN, bodyName = "Kerbin" });

            bool skipped = RecordingFinalizationCacheProducer.TryBuildAlreadyClassifiedDestroyedSkip(
                recording,
                406u,
                FinalizationCacheOwner.BackgroundLoaded,
                refreshUT: 222.0,
                reason: "unit-nonfinite-terminal",
                out RecordingFinalizationCache cache);

            Assert.True(skipped);
            Assert.Equal(FinalizationCacheStatus.Failed, cache.Status);
            Assert.Equal(RecordingFinalizationCacheProducer.AlreadyClassifiedDestroyedDeclineReason, cache.DeclineReason);
            Assert.Equal(222.0, cache.TerminalUT);
            Assert.Equal(222.0, cache.TailStartsAtUT);
        }

        [Fact]
        public void TryBuildFromLiveVessel_SubSurfaceDestroyed_LogsTransitionAndSummary()
        {
            RecordingFinalizationCacheProducer.TryBuildDefaultFinalizationResultOverrideForTesting =
                (Recording recording, Vessel vessel, double refreshUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    result = new IncompleteBallisticFinalizationResult
                    {
                        terminalState = TerminalState.Destroyed,
                        terminalUT = 180.0,
                        extrapolationFailureReason = ExtrapolationFailureReason.SubSurfaceStart,
                        subSurfaceDestroyedBodyName = "Kerbin",
                        subSurfaceDestroyedAltitude = -599979.0,
                        subSurfaceDestroyedThreshold = BallisticExtrapolator.SubSurfaceDestroyedAltitude
                    };
                    return true;
                };

            var vessel = MakeFlyingView(405u, packed: false, loaded: true, inAtmosphere: false);
            var recording = new Recording
            {
                RecordingId = "rec-subsurface-transition",
                VesselPersistentId = 405u,
                ExplicitEndUT = 100.0
            };

            bool built = RecordingFinalizationCacheProducer.TryBuildFromLiveVessel(
                recording,
                vessel,
                120.0,
                FinalizationCacheOwner.BackgroundLoaded,
                "background_periodic",
                hasMeaningfulThrust: false,
                out RecordingFinalizationCache cache);

            Assert.True(built);
            Assert.Equal(TerminalState.Destroyed, cache.TerminalState);
            Assert.Equal(180.0, cache.TerminalUT);
            Assert.Equal(1, CountLogLines(
                "[Parsek][WARN][Extrapolator]",
                "Start rejected: sub-surface state",
                "rec=rec-subsurface-transition",
                "alt=-599979.0"));
            Assert.Equal(1, CountLogLines(
                "[Parsek][INFO][Extrapolator]",
                "classified Destroyed by sub-surface path",
                "rec=rec-subsurface-transition",
                "terminalUT=180.0",
                "body=Kerbin"));
            Assert.Equal(1, CountLogLines(
                "[Parsek][INFO][Extrapolator]",
                "FinalizerCache refresh summary",
                "recordingsExamined=1",
                "alreadyClassified=0",
                "newlyClassified=1"));
        }

        [Fact]
        public void TryBuildFromLiveVessel_AtmosphericPackedDefaultDecline_CachesDestroyedFallbackAndLogs()
        {
            RecordingFinalizationCacheProducer.TryBuildDefaultFinalizationResultOverrideForTesting =
                (Recording recording, Vessel vessel, double refreshUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    result = default(IncompleteBallisticFinalizationResult);
                    return false;
                };

            var vessel = MakeFlyingView(303u, packed: true, loaded: false, inAtmosphere: true);
            var recording = new Recording
            {
                RecordingId = "rec-live-atmo-delete",
                VesselPersistentId = 303u,
                ExplicitEndUT = 100.0
            };

            bool built = RecordingFinalizationCacheProducer.TryBuildFromLiveVessel(
                recording,
                vessel,
                140.0,
                FinalizationCacheOwner.BackgroundOnRails,
                "unit-live-atmo-delete",
                hasMeaningfulThrust: false,
                out RecordingFinalizationCache cache);

            Assert.True(built);
            Assert.Equal(FinalizationCacheStatus.Fresh, cache.Status);
            Assert.Equal(TerminalState.Destroyed, cache.TerminalState);
            Assert.Equal(140.0, cache.TerminalUT);
            Assert.True(cache.LastWasInAtmosphere);
            Assert.Empty(cache.PredictedSegments);
            Assert.Contains(logLines, line =>
                line.Contains("[Parsek][VERBOSE][FinalizerCache]") &&
                line.Contains("Refresh accepted") &&
                line.Contains("rec=rec-live-atmo-delete") &&
                line.Contains("terminal=Destroyed"));
        }

        [Fact]
        public void TryBuildFromLiveVessel_NullVessel_DeclinesAndLogs()
        {
            bool built = RecordingFinalizationCacheProducer.TryBuildFromLiveVessel(
                new Recording { RecordingId = "rec-live-null" },
                (IRecordingFinalizationVesselView)null,
                150.0,
                FinalizationCacheOwner.ActiveRecorder,
                "unit-live-null",
                hasMeaningfulThrust: false,
                out RecordingFinalizationCache cache);

            Assert.False(built);
            Assert.Equal(FinalizationCacheStatus.Failed, cache.Status);
            Assert.Equal("vessel-null", cache.DeclineReason);
            Assert.Contains(logLines, line =>
                line.Contains("[Parsek][VERBOSE][FinalizerCache]") &&
                line.Contains("Refresh declined") &&
                line.Contains("rec=rec-live-null") &&
                line.Contains("reason=vessel-null"));
        }

        [Fact]
        public void TryBuildFromStableOrbitSegment_CachesOrbitingTerminal()
        {
            var segment = new OrbitSegment
            {
                startUT = 100.0,
                endUT = 200.0,
                bodyName = "Mun",
                semiMajorAxis = 250000.0,
                eccentricity = 0.02,
                inclination = 6.0,
                longitudeOfAscendingNode = 12.0,
                argumentOfPeriapsis = 34.0,
                meanAnomalyAtEpoch = 0.5,
                epoch = 100.0
            };

            bool built = RecordingFinalizationCacheProducer.TryBuildFromStableOrbitSegment(
                "rec-orbit",
                42u,
                segment,
                125.0,
                FinalizationCacheOwner.BackgroundOnRails,
                "unit",
                out RecordingFinalizationCache cache);

            Assert.True(built);
            Assert.Equal(FinalizationCacheStatus.Fresh, cache.Status);
            Assert.Equal(TerminalState.Orbiting, cache.TerminalState);
            Assert.Equal(125.0, cache.TerminalUT);
            Assert.Equal("Mun", cache.TerminalOrbit.Value.bodyName);
            Assert.Empty(cache.PredictedSegments);
        }

        [Fact]
        public void TryBuildFromStableOrbitSegment_InvalidSegment_PreservesDigestForCadence()
        {
            var segment = new OrbitSegment
            {
                startUT = 100.0,
                endUT = 100.0,
                bodyName = "",
                semiMajorAxis = double.NaN,
                eccentricity = 0.02,
                epoch = 100.0
            };

            bool built = RecordingFinalizationCacheProducer.TryBuildFromStableOrbitSegment(
                "rec-invalid-orbit",
                42u,
                segment,
                125.0,
                FinalizationCacheOwner.BackgroundOnRails,
                "unit-invalid-orbit",
                out RecordingFinalizationCache cache);

            Assert.False(built);
            Assert.Equal(FinalizationCacheStatus.Failed, cache.Status);
            Assert.Equal("orbit-segment-invalid", cache.DeclineReason);
            Assert.False(string.IsNullOrEmpty(cache.LastObservedOrbitDigest));
            Assert.Contains("sma=NaN", cache.LastObservedOrbitDigest);
            Assert.Contains(logLines, line =>
                line.Contains("[Parsek][VERBOSE][FinalizerCache]") &&
                line.Contains("Refresh declined") &&
                line.Contains("rec=rec-invalid-orbit") &&
                line.Contains("reason=orbit-segment-invalid"));
        }

        [Fact]
        public void BuildOrbitSegmentDigest_IncludesEpoch()
        {
            var first = new OrbitSegment
            {
                bodyName = "Mun",
                semiMajorAxis = 250000.0,
                eccentricity = 0.02,
                inclination = 6.0,
                longitudeOfAscendingNode = 12.0,
                argumentOfPeriapsis = 34.0,
                meanAnomalyAtEpoch = 0.5,
                epoch = 100.0
            };
            OrbitSegment second = first;
            second.epoch = 140.0;

            string firstDigest = RecordingFinalizationCacheProducer.BuildOrbitSegmentDigest(first);
            string secondDigest = RecordingFinalizationCacheProducer.BuildOrbitSegmentDigest(second);

            Assert.NotEqual(firstDigest, secondDigest);
            Assert.Contains("epoch=100.000", firstDigest);
            Assert.Contains("epoch=140.000", secondDigest);
        }

        [Fact]
        public void TryTouchObservedTerminalCache_AdvancesStableTerminalWithoutRebuild()
        {
            var cache = new RecordingFinalizationCache
            {
                RecordingId = "rec-orbit",
                VesselPersistentId = 42u,
                Owner = FinalizationCacheOwner.BackgroundOnRails,
                Status = FinalizationCacheStatus.Fresh,
                TerminalState = TerminalState.Orbiting,
                TerminalUT = 100.0,
                TailStartsAtUT = 100.0,
                LastObservedUT = 100.0,
                LastObservedOrbitDigest = "old"
            };

            bool touched = RecordingFinalizationCacheProducer.TryTouchObservedTerminalCache(
                cache,
                observedUT: 140.0,
                reason: "unit-touch",
                observedDigest: "new");

            Assert.True(touched);
            Assert.Equal(140.0, cache.TerminalUT);
            Assert.Equal(140.0, cache.TailStartsAtUT);
            Assert.Equal(140.0, cache.LastObservedUT);
            Assert.Equal("unit-touch", cache.RefreshReason);
            Assert.Equal("new", cache.LastObservedOrbitDigest);
        }

        [Fact]
        public void TryTouchObservedTerminalCache_AdvancesDockedTerminalWithoutRebuild()
        {
            var cache = new RecordingFinalizationCache
            {
                RecordingId = "rec-docked",
                VesselPersistentId = 42u,
                Owner = FinalizationCacheOwner.ActiveRecorder,
                Status = FinalizationCacheStatus.Fresh,
                TerminalState = TerminalState.Docked,
                TerminalUT = 100.0,
                TailStartsAtUT = 100.0,
                LastObservedUT = 100.0,
                LastObservedOrbitDigest = "old"
            };

            bool touched = RecordingFinalizationCacheProducer.TryTouchObservedTerminalCache(
                cache,
                observedUT: 140.0,
                reason: "unit-docked-touch",
                observedDigest: "new");

            Assert.True(touched);
            Assert.Equal(140.0, cache.TerminalUT);
            Assert.Equal(140.0, cache.LastObservedUT);
            Assert.Equal("unit-docked-touch", cache.RefreshReason);
        }

        [Fact]
        public void TryTouchObservedTerminalCache_DoesNotAdvancePredictedDestroyedTail()
        {
            var cache = new RecordingFinalizationCache
            {
                RecordingId = "rec-destroyed",
                VesselPersistentId = 42u,
                Owner = FinalizationCacheOwner.ActiveRecorder,
                Status = FinalizationCacheStatus.Fresh,
                TerminalState = TerminalState.Destroyed,
                TerminalUT = 160.0,
                TailStartsAtUT = 120.0,
                PredictedSegments = new List<OrbitSegment> { new OrbitSegment { startUT = 120.0, endUT = 160.0 } }
            };

            bool touched = RecordingFinalizationCacheProducer.TryTouchObservedTerminalCache(
                cache,
                observedUT: 180.0,
                reason: "unit-touch",
                observedDigest: "new");

            Assert.False(touched);
            Assert.Equal(160.0, cache.TerminalUT);
            Assert.Equal(120.0, cache.TailStartsAtUT);
        }

        [Fact]
        public void TryBuildAtmosphericDeletionCache_CachesDestroyedTerminal()
        {
            bool built = RecordingFinalizationCacheProducer.TryBuildAtmosphericDeletionCache(
                "rec-atmo",
                43u,
                "Kerbin",
                250.0,
                FinalizationCacheOwner.BackgroundOnRails,
                "unit",
                out RecordingFinalizationCache cache);

            Assert.True(built);
            Assert.Equal(FinalizationCacheStatus.Fresh, cache.Status);
            Assert.Equal(TerminalState.Destroyed, cache.TerminalState);
            Assert.Equal(250.0, cache.TerminalUT);
            Assert.True(cache.LastWasInAtmosphere);
            Assert.Equal("Kerbin", cache.TerminalBodyName);
        }

        private int CountLogLines(params string[] requiredFragments)
        {
            int count = 0;
            for (int i = 0; i < logLines.Count; i++)
            {
                bool matches = true;
                for (int j = 0; j < requiredFragments.Length; j++)
                {
                    if (!logLines[i].Contains(requiredFragments[j]))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                    count++;
            }

            return count;
        }

        private static FakeVesselView MakeFlyingView(
            uint vesselPid,
            bool packed,
            bool loaded,
            bool inAtmosphere)
        {
            return new FakeVesselView
            {
                PersistentIdValue = vesselPid,
                SituationValue = Vessel.Situations.FLYING,
                BodyNameValue = "Kerbin",
                IsPackedValue = packed,
                IsLoadedValue = loaded,
                IsInAtmosphereValue = inAtmosphere,
                OrbitValue = new RecordingFinalizationOrbitView
                {
                    ReferenceBodyName = "Kerbin",
                    ReferenceBodyHasAtmosphere = true,
                    ReferenceBodyAtmosphereDepth = 70000.0,
                    Inclination = 1.0,
                    Eccentricity = 0.2,
                    SemiMajorAxis = 650000.0,
                    LongitudeOfAscendingNode = 2.0,
                    ArgumentOfPeriapsis = 3.0,
                    MeanAnomalyAtEpoch = 0.4,
                    Epoch = 100.0,
                    PeriapsisAltitude = 10000.0
                }
            };
        }

        private sealed class FakeVesselView : IRecordingFinalizationVesselView
        {
            public uint PersistentIdValue;
            public Vessel.Situations SituationValue;
            public string BodyNameValue;
            public bool IsInAtmosphereValue;
            public bool IsPackedValue;
            public bool IsLoadedValue = true;
            public double LatitudeValue;
            public double LongitudeValue;
            public double AltitudeValue;
            public double HeightFromTerrainValue;
            public Quaternion SurfaceRelativeRotationValue;
            public RecordingFinalizationOrbitView? OrbitValue;

            public uint PersistentId => PersistentIdValue;
            public Vessel.Situations Situation => SituationValue;
            public string BodyName => BodyNameValue;
            public bool IsInAtmosphere => IsInAtmosphereValue;
            public bool IsPacked => IsPackedValue;
            public bool IsLoaded => IsLoadedValue;
            public double Latitude => LatitudeValue;
            public double Longitude => LongitudeValue;
            public double Altitude => AltitudeValue;
            public double HeightFromTerrain => HeightFromTerrainValue;
            public Quaternion SurfaceRelativeRotation => SurfaceRelativeRotationValue;
            public Vessel RawVessel => null;

            public bool TryGetOrbit(out RecordingFinalizationOrbitView orbit)
            {
                if (OrbitValue.HasValue)
                {
                    orbit = OrbitValue.Value;
                    return true;
                }

                orbit = default(RecordingFinalizationOrbitView);
                return false;
            }
        }
    }
}
