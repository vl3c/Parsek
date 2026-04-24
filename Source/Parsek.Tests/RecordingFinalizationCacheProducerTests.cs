using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class RecordingFinalizationCacheProducerTests
    {
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
    }
}
