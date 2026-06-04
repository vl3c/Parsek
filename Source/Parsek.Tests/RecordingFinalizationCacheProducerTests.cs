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
        /// Stable no-delta refresh summaries are time-rate-limited by
        /// owner/recording/state. The second emission carries suppressed=N
        /// instead of producing one exact repeat per periodic refresh.
        /// </summary>
        [Fact]
        public void LogRefreshSummary_NoDeltaStablePass_RateLimitsRepeats()
        {
            double clockSeconds = 0.0;
            ParsekLog.ClockOverrideForTesting = () => clockSeconds;

            for (int i = 0; i < 10; i++)
            {
                RecordingFinalizationCacheProducer.LogRefreshSummary(
                    FinalizationCacheOwner.ActiveRecorder,
                    "periodic",
                    recordingsExamined: 1,
                    alreadyClassified: 0,
                    newlyClassified: 0,
                    recordingId: "rec-stable-finalizer",
                    vesselPid: 42u,
                    status: FinalizationCacheStatus.Fresh,
                    terminalState: TerminalState.Orbiting,
                    predictedSegmentCount: 1);
            }

            Assert.Equal(1, CountLogLines(
                "[Parsek][VERBOSE][Extrapolator]",
                "FinalizerCache refresh summary",
                "reason=periodic"));

            clockSeconds += 31.0;
            RecordingFinalizationCacheProducer.LogRefreshSummary(
                FinalizationCacheOwner.ActiveRecorder,
                "periodic",
                recordingsExamined: 1,
                alreadyClassified: 0,
                newlyClassified: 0,
                recordingId: "rec-stable-finalizer",
                vesselPid: 42u,
                status: FinalizationCacheStatus.Fresh,
                terminalState: TerminalState.Orbiting,
                predictedSegmentCount: 1);

            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][VERBOSE][Extrapolator]")
                && l.Contains("FinalizerCache refresh summary")
                && l.Contains("rec=rec-stable-finalizer")
                && l.Contains("suppressed=9"));
            Assert.Equal(0, CountLogLines(
                "[Parsek][INFO][Extrapolator]",
                "FinalizerCache refresh summary",
                "rec-stable-finalizer"));
        }

        [Fact]
        public void LogRefreshSummary_CrossRecordingIdentitiesDoNotShareRateLimitWindow()
        {
            double clockSeconds = 0.0;
            ParsekLog.ClockOverrideForTesting = () => clockSeconds;
            const uint sharedVesselPid = 111u;
            // Intentional: VerboseOnChange identity is keyed by recordingId, not vessel pid.
            var first = new Recording
            {
                RecordingId = "rec-refresh-a",
                VesselPersistentId = sharedVesselPid
            };
            var second = new Recording
            {
                RecordingId = "rec-refresh-b",
                VesselPersistentId = sharedVesselPid
            };

            LogStableRefresh(first);
            LogStableRefresh(second);

            Assert.Equal(1, CountLogLines(
                "[Parsek][VERBOSE][Extrapolator]",
                "FinalizerCache refresh summary",
                "rec=rec-refresh-a"));
            Assert.Equal(1, CountLogLines(
                "[Parsek][VERBOSE][Extrapolator]",
                "FinalizerCache refresh summary",
                "rec=rec-refresh-b"));

            LogStableRefresh(first);
            LogStableRefresh(second);

            Assert.Equal(1, CountLogLines(
                "[Parsek][VERBOSE][Extrapolator]",
                "FinalizerCache refresh summary",
                "rec=rec-refresh-a"));
            Assert.Equal(1, CountLogLines(
                "[Parsek][VERBOSE][Extrapolator]",
                "FinalizerCache refresh summary",
                "rec=rec-refresh-b"));

            clockSeconds += 31.0;
            LogStableRefresh(first);
            LogStableRefresh(second);

            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][VERBOSE][Extrapolator]")
                && l.Contains("FinalizerCache refresh summary")
                && l.Contains("rec=rec-refresh-a")
                && l.Contains("suppressed=1"));
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][VERBOSE][Extrapolator]")
                && l.Contains("FinalizerCache refresh summary")
                && l.Contains("rec=rec-refresh-b")
                && l.Contains("suppressed=1"));
        }

        [Fact]
        public void LogRefreshSummary_RepeatedClassification_InfoOnceThenVerboseRateLimited()
        {
            double clockSeconds = 0.0;
            ParsekLog.ClockOverrideForTesting = () => clockSeconds;

            for (int i = 0; i < 5; i++)
            {
                RecordingFinalizationCacheProducer.LogRefreshSummary(
                    FinalizationCacheOwner.BackgroundLoaded,
                    "background_periodic",
                    recordingsExamined: 1,
                    alreadyClassified: 0,
                    newlyClassified: 1,
                    recordingId: "rec-destroyed-finalizer",
                    vesselPid: 77u,
                    status: FinalizationCacheStatus.Fresh,
                    terminalState: TerminalState.Destroyed,
                    predictedSegmentCount: 0);
            }

            Assert.Equal(1, CountLogLines(
                "[Parsek][INFO][Extrapolator]",
                "FinalizerCache refresh summary",
                "rec=rec-destroyed-finalizer"));

            clockSeconds += 31.0;
            RecordingFinalizationCacheProducer.LogRefreshSummary(
                FinalizationCacheOwner.BackgroundLoaded,
                "background_periodic",
                recordingsExamined: 1,
                alreadyClassified: 0,
                newlyClassified: 1,
                recordingId: "rec-destroyed-finalizer",
                vesselPid: 77u,
                status: FinalizationCacheStatus.Fresh,
                terminalState: TerminalState.Destroyed,
                predictedSegmentCount: 0);

            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][VERBOSE][Extrapolator]")
                && l.Contains("FinalizerCache refresh summary")
                && l.Contains("rec=rec-destroyed-finalizer")
                && l.Contains("suppressed=3"));
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
        public void TryBuildFromLiveVessel_DestroyEventReason_OverridesSurfaceTerminal_StampsDestroyed()
        {
            // Repro for the v0.9.2 "auto-seal because landed" Re-Fly dialog
            // bug: when the active vessel impacts the ground, KSP flips
            // Situation to LANDED for a moment before the destroy_event
            // fires. Without this branch the surface-terminal short-circuit
            // would stamp TerminalState.Landed and the merge dialog's reasons
            // list would read "landed" on a recording the player saw as a
            // crash. What makes it fail: a producer that runs
            // TryBuildSurfaceTerminalCache before checking the
            // destroy_event reason would store TerminalState.Landed here.
            var vessel = new FakeVesselView
            {
                PersistentIdValue = 303u,
                SituationValue = Vessel.Situations.LANDED,
                BodyNameValue = "Kerbin",
                LatitudeValue = -0.0972,
                LongitudeValue = -74.5577,
                AltitudeValue = 75.2,
                HeightFromTerrainValue = 1.1,
                SurfaceRelativeRotationValue = Quaternion.identity
            };
            var recording = new Recording
            {
                RecordingId = "rec-live-destroyed",
                VesselPersistentId = 303u
            };

            bool built = RecordingFinalizationCacheProducer.TryBuildFromLiveVessel(
                recording,
                vessel,
                160.0,
                FinalizationCacheOwner.ActiveRecorder,
                "destroy_event",
                hasMeaningfulThrust: false,
                out RecordingFinalizationCache cache);

            Assert.True(built);
            Assert.Equal(FinalizationCacheStatus.Fresh, cache.Status);
            Assert.Equal(TerminalState.Destroyed, cache.TerminalState);
            Assert.Equal(160.0, cache.TerminalUT);
            Assert.Equal("Kerbin", cache.TerminalBodyName);
            // Eager recording mutation is what protects against same-frame
            // follow-up refreshes clobbering the stamp; see
            // TryBuildFromLiveVessel_DestroyEventThenPartDieSameLandedSituation_KeepsDestroyed.
            Assert.Equal(TerminalState.Destroyed, recording.TerminalStateValue);
            Assert.Contains(logLines, line =>
                line.Contains("[Parsek][INFO][FinalizerCache]") &&
                line.Contains("Destroy-reason override") &&
                line.Contains("rec=rec-live-destroyed") &&
                line.Contains("reason=destroy_event") &&
                line.Contains("situation=LANDED") &&
                line.Contains("eagerRecordingStamp=True"));
            Assert.Contains(logLines, line =>
                line.Contains("[Parsek][VERBOSE][FinalizerCache]") &&
                line.Contains("Refresh accepted") &&
                line.Contains("rec=rec-live-destroyed") &&
                line.Contains("terminal=Destroyed"));
        }

        [Fact]
        public void TryBuildFromLiveVessel_BackgroundDestroyReason_OverridesSurfaceTerminal_StampsDestroyed()
        {
            // Same bug class on the background-vessel destroy path: when
            // BackgroundRecorder.OnBackgroundVesselWillDestroy fires for a
            // BG vessel whose last sampled Situation was LANDED, the
            // background_destroy refresh must stamp Destroyed, not Landed.
            var vessel = new FakeVesselView
            {
                PersistentIdValue = 404u,
                SituationValue = Vessel.Situations.LANDED,
                BodyNameValue = "Kerbin",
                LatitudeValue = 1.0,
                LongitudeValue = 2.0,
                AltitudeValue = 30.0,
                SurfaceRelativeRotationValue = Quaternion.identity
            };
            var recording = new Recording
            {
                RecordingId = "rec-bg-destroyed",
                VesselPersistentId = 404u
            };

            bool built = RecordingFinalizationCacheProducer.TryBuildFromLiveVessel(
                recording,
                vessel,
                250.0,
                FinalizationCacheOwner.BackgroundLoaded,
                "background_destroy",
                hasMeaningfulThrust: false,
                out RecordingFinalizationCache cache);

            Assert.True(built);
            Assert.Equal(FinalizationCacheStatus.Fresh, cache.Status);
            Assert.Equal(TerminalState.Destroyed, cache.TerminalState);
            Assert.Equal(250.0, cache.TerminalUT);
            Assert.Equal("Kerbin", cache.TerminalBodyName);
            Assert.Equal(TerminalState.Destroyed, recording.TerminalStateValue);
            Assert.Contains(logLines, line =>
                line.Contains("[Parsek][INFO][FinalizerCache]") &&
                line.Contains("Destroy-reason override") &&
                line.Contains("rec=rec-bg-destroyed") &&
                line.Contains("reason=background_destroy") &&
                line.Contains("situation=LANDED"));
        }

        [Fact]
        public void TryBuildFromLiveVessel_DestroyEventThenPartDieSameLandedSituation_KeepsDestroyed()
        {
            // Sequencing race: KSP can fire onPartDie / onPartJointBreak for
            // residual parts AFTER OnVesselWillDestroy and BEFORE the vessel
            // is GC'd. If the destroy-reason branch only stamps cache state,
            // the follow-up part_die refresh (which is NOT a destroy reason)
            // would skip the new branch, then re-reach TryBuildSurfaceTerminalCache
            // on the still-LANDED situation and re-stamp Landed. The
            // canonical fix is the eager recording.TerminalStateValue mutation
            // in PopulateDestroyEventTerminalCache: the second refresh's
            // TryBuildAlreadyClassifiedDestroyedSkip catches it and the cache
            // preserves Destroyed. What makes it fail: omitting the eager
            // recording mutation would let the second refresh clobber the
            // cache back to Landed.
            var vessel = new FakeVesselView
            {
                PersistentIdValue = 606u,
                SituationValue = Vessel.Situations.LANDED,
                BodyNameValue = "Kerbin",
                LatitudeValue = 1.0,
                LongitudeValue = 2.0,
                AltitudeValue = 12.0,
                SurfaceRelativeRotationValue = Quaternion.identity
            };
            var recording = new Recording
            {
                RecordingId = "rec-destroy-then-part-die",
                VesselPersistentId = 606u
            };

            bool destroyBuilt = RecordingFinalizationCacheProducer.TryBuildFromLiveVessel(
                recording,
                vessel,
                400.0,
                FinalizationCacheOwner.ActiveRecorder,
                "destroy_event",
                hasMeaningfulThrust: false,
                out RecordingFinalizationCache destroyCache);

            Assert.True(destroyBuilt);
            Assert.Equal(TerminalState.Destroyed, destroyCache.TerminalState);
            Assert.Equal(TerminalState.Destroyed, recording.TerminalStateValue);

            bool partDieBuilt = RecordingFinalizationCacheProducer.TryBuildFromLiveVessel(
                recording,
                vessel,
                400.05,
                FinalizationCacheOwner.ActiveRecorder,
                "part_die",
                hasMeaningfulThrust: false,
                out RecordingFinalizationCache partDieCache);

            Assert.False(partDieBuilt);
            Assert.True(RecordingFinalizationCacheProducer.IsAlreadyClassifiedDestroyedSkip(partDieCache));
            Assert.Equal(TerminalState.Destroyed, partDieCache.TerminalState);
            Assert.Equal(TerminalState.Destroyed, recording.TerminalStateValue);

            bool periodicBuilt = RecordingFinalizationCacheProducer.TryBuildFromLiveVessel(
                recording,
                vessel,
                400.10,
                FinalizationCacheOwner.ActiveRecorder,
                "periodic",
                hasMeaningfulThrust: false,
                out RecordingFinalizationCache periodicCache);

            Assert.False(periodicBuilt);
            Assert.True(RecordingFinalizationCacheProducer.IsAlreadyClassifiedDestroyedSkip(periodicCache));
            Assert.Equal(TerminalState.Destroyed, periodicCache.TerminalState);
        }

        [Fact]
        public void TryBuildFromLiveVessel_DestroyEventReason_DoesNotOverwriteExistingTerminalStateValue()
        {
            // Defensive guard: PopulateDestroyEventTerminalCache only stamps
            // recording.TerminalStateValue when it is null. A recording that
            // already has a terminal verdict (e.g. set by the Applier on a
            // prior refresh, or by a separate code path) must not be silently
            // overwritten by an eager Destroyed stamp from this branch. The
            // cache itself still stamps Destroyed - that is the in-flight
            // signal - but the recording's authoritative terminal state is
            // left as-is for the Applier / call site to reconcile.
            var vessel = new FakeVesselView
            {
                PersistentIdValue = 707u,
                SituationValue = Vessel.Situations.LANDED,
                BodyNameValue = "Kerbin",
                SurfaceRelativeRotationValue = Quaternion.identity
            };
            var recording = new Recording
            {
                RecordingId = "rec-preexisting-terminal",
                VesselPersistentId = 707u,
                TerminalStateValue = TerminalState.Landed
            };

            bool built = RecordingFinalizationCacheProducer.TryBuildFromLiveVessel(
                recording,
                vessel,
                500.0,
                FinalizationCacheOwner.ActiveRecorder,
                "destroy_event",
                hasMeaningfulThrust: false,
                out RecordingFinalizationCache cache);

            Assert.True(built);
            Assert.Equal(TerminalState.Destroyed, cache.TerminalState);
            // Recording's pre-existing verdict preserved.
            Assert.Equal(TerminalState.Landed, recording.TerminalStateValue);
            Assert.Contains(logLines, line =>
                line.Contains("[Parsek][INFO][FinalizerCache]") &&
                line.Contains("Destroy-reason override") &&
                line.Contains("eagerRecordingStamp=False"));
        }

        [Fact]
        public void TryBuildFromLiveVessel_NonDestroyReason_LandedSituationStillStampsLanded()
        {
            // Negative pair to the destroy_event override: ordinary refresh
            // reasons (periodic, joint_break, part_die, etc.) must keep the
            // surface-terminal short-circuit so a vessel that genuinely
            // lands without dying still classifies as Landed. What makes it
            // fail: an over-broad IsDestroyRefreshReason that catches every
            // ground-contact reason would regress normal landed-terminal
            // recording.
            var vessel = new FakeVesselView
            {
                PersistentIdValue = 505u,
                SituationValue = Vessel.Situations.LANDED,
                BodyNameValue = "Kerbin",
                SurfaceRelativeRotationValue = Quaternion.identity
            };
            var recording = new Recording
            {
                RecordingId = "rec-live-landed-periodic",
                VesselPersistentId = 505u
            };

            bool built = RecordingFinalizationCacheProducer.TryBuildFromLiveVessel(
                recording,
                vessel,
                300.0,
                FinalizationCacheOwner.ActiveRecorder,
                "periodic",
                hasMeaningfulThrust: false,
                out RecordingFinalizationCache cache);

            Assert.True(built);
            Assert.Equal(TerminalState.Landed, cache.TerminalState);
        }

        [Fact]
        public void IsDestroyRefreshReason_MatchesDestroyEventAndBackgroundDestroyOnly()
        {
            Assert.True(RecordingFinalizationCacheProducer.IsDestroyRefreshReason("destroy_event"));
            Assert.True(RecordingFinalizationCacheProducer.IsDestroyRefreshReason("background_destroy"));
            Assert.False(RecordingFinalizationCacheProducer.IsDestroyRefreshReason("periodic"));
            Assert.False(RecordingFinalizationCacheProducer.IsDestroyRefreshReason("joint_break"));
            Assert.False(RecordingFinalizationCacheProducer.IsDestroyRefreshReason("part_die"));
            Assert.False(RecordingFinalizationCacheProducer.IsDestroyRefreshReason(null));
            Assert.False(RecordingFinalizationCacheProducer.IsDestroyRefreshReason(""));
            Assert.False(RecordingFinalizationCacheProducer.IsDestroyRefreshReason("DESTROY_EVENT"));
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
            Assert.Equal(1, CountLogLines(
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
            // Background / periodic-refresh path: the sub-surface Destroyed verdict is
            // a self-correcting transient, so it now logs at VERBOSE rather than WARN
            // (the classification, terminal=Destroyed above, is unchanged). The
            // companion "classified Destroyed" line is likewise Verbose, not INFO.
            Assert.Equal(1, CountLogLines(
                "[Parsek][VERBOSE][Extrapolator]",
                "Start rejected: sub-surface state",
                "rec=rec-subsurface-transition",
                "alt=-599979.0"));
            Assert.Equal(0, CountLogLines(
                "[Parsek][WARN][Extrapolator]",
                "Start rejected: sub-surface state",
                "rec=rec-subsurface-transition"));
            Assert.Equal(1, CountLogLines(
                "[Parsek][VERBOSE][Extrapolator]",
                "classified Destroyed by sub-surface path",
                "rec=rec-subsurface-transition",
                "terminalUT=180.0",
                "body=Kerbin"));
            // The aggregate refresh summary is a separate log, unaffected by the
            // sub-surface log-level change, and stays at INFO.
            Assert.Equal(1, CountLogLines(
                "[Parsek][INFO][Extrapolator]",
                "FinalizerCache refresh summary",
                "recordingsExamined=1",
                "alreadyClassified=0",
                "newlyClassified=1"));
        }

        [Fact]
        public void TryBuildFromLiveVessel_DefaultFinalizerSuppressed_DoesNotAcceptFreshDestroyedCache()
        {
            RecordingFinalizationCacheProducer.TryBuildDefaultFinalizationResultOverrideForTesting =
                (Recording recording, Vessel vessel, double refreshUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    result = new IncompleteBallisticFinalizationResult
                    {
                        terminalState = TerminalState.Destroyed,
                        terminalUT = refreshUT,
                        extrapolationFailureReason = ExtrapolationFailureReason.SubSurfaceStart,
                        subSurfaceDestroyedBodyName = "Kerbin",
                        subSurfaceDestroyedAltitude = -599979.0,
                        subSurfaceDestroyedThreshold = BallisticExtrapolator.SubSurfaceDestroyedAltitude
                    };
                    return false;
                };

            var vessel = MakeFlyingView(407u, packed: false, loaded: true, inAtmosphere: false);
            var recording = new Recording
            {
                RecordingId = "rec-subsurface-suppressed",
                VesselPersistentId = 407u,
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

            Assert.False(built);
            Assert.Equal(FinalizationCacheStatus.Failed, cache.Status);
            Assert.Equal("subsurface-destroyed-suppressed", cache.DeclineReason);
            Assert.NotEqual(TerminalState.Destroyed, cache.TerminalState);
            Assert.DoesNotContain(logLines, line =>
                line.Contains("Refresh accepted")
                && line.Contains("rec=rec-subsurface-suppressed")
                && line.Contains("terminal=Destroyed"));
            Assert.DoesNotContain(logLines, line =>
                line.Contains("classified Destroyed by sub-surface path")
                && line.Contains("rec=rec-subsurface-suppressed"));
        }

        [Fact]
        public void TryBuildFromLiveVessel_SuppressedSubSurfacePackedAtmospheric_DoesNotCacheDestroyedFallback()
        {
            RecordingFinalizationCacheProducer.TryBuildDefaultFinalizationResultOverrideForTesting =
                (Recording recording, Vessel vessel, double refreshUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    result = new IncompleteBallisticFinalizationResult
                    {
                        terminalState = TerminalState.Destroyed,
                        terminalUT = refreshUT,
                        extrapolationFailureReason = ExtrapolationFailureReason.SubSurfaceStart,
                        subSurfaceDestroyedBodyName = "Kerbin",
                        subSurfaceDestroyedAltitude = -599979.0,
                        subSurfaceDestroyedThreshold = BallisticExtrapolator.SubSurfaceDestroyedAltitude
                    };
                    return false;
                };

            var vessel = MakeFlyingView(408u, packed: true, loaded: false, inAtmosphere: true);
            var recording = new Recording
            {
                RecordingId = "rec-subsurface-suppressed-atmo-packed",
                VesselPersistentId = 408u,
                ExplicitEndUT = 100.0
            };

            bool built = RecordingFinalizationCacheProducer.TryBuildFromLiveVessel(
                recording,
                vessel,
                120.0,
                FinalizationCacheOwner.BackgroundOnRails,
                "background_periodic",
                hasMeaningfulThrust: false,
                out RecordingFinalizationCache cache);

            Assert.False(built);
            Assert.Equal(FinalizationCacheStatus.Failed, cache.Status);
            Assert.Equal("subsurface-destroyed-suppressed", cache.DeclineReason);
            Assert.NotEqual(TerminalState.Destroyed, cache.TerminalState);
            Assert.DoesNotContain(logLines, line =>
                line.Contains("Refresh accepted")
                && line.Contains("rec=rec-subsurface-suppressed-atmo-packed")
                && line.Contains("terminal=Destroyed"));
            Assert.DoesNotContain(logLines, line =>
                line.Contains("classified Destroyed by sub-surface path")
                && line.Contains("rec=rec-subsurface-suppressed-atmo-packed"));
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

        private static void LogStableRefresh(Recording recording)
        {
            RecordingFinalizationCacheProducer.LogRefreshSummary(
                FinalizationCacheOwner.ActiveRecorder,
                "periodic",
                recordingsExamined: 1,
                alreadyClassified: 0,
                newlyClassified: 0,
                recordingId: recording.RecordingId,
                vesselPid: recording.VesselPersistentId,
                status: FinalizationCacheStatus.Fresh,
                terminalState: TerminalState.Orbiting,
                predictedSegmentCount: 1);
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
