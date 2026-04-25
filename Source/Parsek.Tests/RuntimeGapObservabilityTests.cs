using System;
using System.Collections.Generic;
using Parsek.InGameTests;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class RuntimeGapObservabilityTests : IDisposable
    {
        public RuntimeGapObservabilityTests()
        {
            GhostMapPresence.ResetForTesting();
        }

        public void Dispose()
        {
            GhostMapPresence.ResetForTesting();
        }

        [Fact]
        public void BackgroundStateDriftFormatters_SurfaceCountsAndIdentity()
        {
            var summary = new BackgroundRecorder.BackgroundStateDriftSummary
            {
                BackgroundMapCount = 3,
                MissingRecording = 1,
                MissingTrackingState = 2,
                LoadedWithoutLoadedState = 1,
                PackedWithoutOnRailsState = 1
            };

            string aggregate = BackgroundRecorder.FormatBackgroundStateDriftSummary(
                summary,
                "UpdateOnRails");
            string vessel = BackgroundRecorder.FormatBackgroundVesselStateDrift(
                42,
                "rec-bg",
                "missing-recording",
                vesselLoaded: true,
                vesselPacked: false,
                hasLoadedState: false,
                hasOnRailsState: true,
                hasRecording: false);

            Assert.Contains("reason=UpdateOnRails", aggregate);
            Assert.Contains("map=3", aggregate);
            Assert.Contains("missingRecording=1", aggregate);
            Assert.Contains("pid=42", vessel);
            Assert.Contains("recId=rec-bg", vessel);
            Assert.Contains("reason=missing-recording", vessel);
        }

        [Fact]
        public void TransitionToBackgroundMissingVesselWarning_IncludesFinalizerContext()
        {
            var cache = new RecordingFinalizationCache
            {
                Owner = FinalizationCacheOwner.ActiveRecorder,
                Status = FinalizationCacheStatus.Fresh,
                TerminalState = TerminalState.Orbiting,
                CachedAtUT = 123.4
            };

            string line = FlightRecorder.FormatTransitionToBackgroundMissingVesselWarning(
                99,
                "rec-live",
                "Wayfarer",
                pointCount: 10,
                orbitSegmentCount: 2,
                trackSectionCount: 1,
                finalizationCache: cache);

            Assert.Contains("pid=99", line);
            Assert.Contains("recId=rec-live", line);
            Assert.Contains("vessel='Wayfarer'", line);
            Assert.Contains("points=10", line);
            Assert.Contains("orbitSegments=2", line);
            Assert.Contains("trackSections=1", line);
            Assert.Contains("status=Fresh", line);
            Assert.Contains("terminal=Orbiting", line);
        }

        [Fact]
        public void PostSwitchSummaries_FormatDecisionAndManifestDeltas()
        {
            string stateKey = ParsekFlight.BuildPostSwitchAutoRecordDecisionStateKey(
                ParsekFlight.PostSwitchAutoRecordSuppressionReason.WarpActive,
                baselineCaptured: true,
                waitingForSettle: false,
                ParsekFlight.PostSwitchAutoRecordTrigger.None);
            string decision = ParsekFlight.FormatPostSwitchAutoRecordDecisionSummary(
                armedPid: 42,
                activePid: 42,
                baselineCaptured: true,
                suppression: ParsekFlight.PostSwitchAutoRecordSuppressionReason.WarpActive,
                waitingForSettle: false,
                comparisonsReadyUt: 20.0,
                currentUt: 21.5,
                trigger: ParsekFlight.PostSwitchAutoRecordTrigger.None);
            string manifest = ParsekFlight.FormatPostSwitchManifestDeltaSummary(
                vesselPid: 42,
                crewDeltaKeys: 1,
                resourceDeltaKeys: 2,
                inventoryDeltaKeys: 3,
                partStateTokenDelta: 4,
                crewChanged: true,
                resourceChanged: true,
                partStateChanged: true,
                nextEvaluationUt: 22.0);

            Assert.Contains("suppression=WarpActive", stateKey);
            Assert.Contains("pid=42", decision);
            Assert.Contains("suppression=WarpActive", decision);
            Assert.Contains("trigger=None", decision);
            Assert.Contains("crewDeltaKeys=1", manifest);
            Assert.Contains("resourceDeltaKeys=2", manifest);
            Assert.Contains("inventoryDeltaKeys=3", manifest);
            Assert.Contains("partStateTokenDelta=4", manifest);
        }

        [Fact]
        public void SplitSkipSummary_IncludesSourceAndPids()
        {
            string line = ParsekFlight.FormatSplitSkipSummary(
                "OnCrewOnEva",
                "source-vessel-mismatch",
                "rec-active",
                sourcePid: 7,
                targetPid: 8,
                pendingSplitInProgress: true);

            Assert.Contains("OnCrewOnEva", line);
            Assert.Contains("reason=source-vessel-mismatch", line);
            Assert.Contains("activeRec=rec-active", line);
            Assert.Contains("sourcePid=7", line);
            Assert.Contains("targetPid=8", line);
        }

        [Fact]
        public void MapMarkerSummary_IncludesPrioritySkipCounters()
        {
            var summary = new ParsekUI.MapMarkerSummary
            {
                IsMapView = true,
                Candidates = 5,
                Drawn = 1,
                NativeIcon = 2,
                ChainNonTip = 1,
                PositionFailure = 1,
                MissingBody = 1
            };

            string line = ParsekUI.FormatMapMarkerSummary(summary);

            Assert.Contains("view=map", line);
            Assert.Contains("candidates=5", line);
            Assert.Contains("nativeIcon=2", line);
            Assert.Contains("chainNonTip=1", line);
            Assert.Contains("positionFailure=1", line);
            Assert.Contains("missingBody=1", line);
        }

        [Fact]
        public void AtmosphericMarkerSummaryAndClassifier_ReportSkips()
        {
            var summary = new ParsekTrackingStation.AtmosphericMarkerSummary
            {
                EventTypeName = "Repaint",
                Candidates = 4,
                Drawn = 1,
                NativeIconActive = 1,
                SuppressedByChainFilter = 1,
                MissingBody = 1
            };
            var debris = new Recording { IsDebris = true };

            string line = ParsekTrackingStation.FormatAtmosphericMarkerSummary(summary);
            var reason = ParsekTrackingStation.ClassifyAtmosphericMarkerSkip(
                debris,
                recordingIndex: 0,
                currentUT: 10.0,
                suppressedIds: null);

            Assert.Contains("event=Repaint", line);
            Assert.Contains("nativeIcon=1", line);
            Assert.Contains("chainSuppressed=1", line);
            Assert.Contains("missingBody=1", line);
            Assert.Equal(ParsekTrackingStation.AtmosphericMarkerSkipReason.Debris, reason);
        }

        [Fact]
        public void GhostOrbitLineDecisionFormat_IncludesSuppressionReason()
        {
            string stateKey = GhostOrbitLinePatch.BuildGhostOrbitLineDecisionStateKey(
                lineActive: false,
                drawIcons: OrbitRendererBase.DrawIcons.NONE,
                iconSuppressed: true,
                reason: "below-atmosphere",
                hasBounds: true);
            string line = GhostOrbitLinePatch.FormatGhostOrbitLineDecision(
                vesselPid: 123,
                reason: "below-atmosphere",
                lineActive: false,
                drawIcons: OrbitRendererBase.DrawIcons.NONE,
                iconSuppressed: true,
                belowAtmosphere: true,
                hasBounds: true,
                currentUT: 50.0,
                startUT: 10.0,
                endUT: 90.0);

            Assert.Contains("reason=below-atmosphere", stateKey);
            Assert.Contains("pid=123", line);
            Assert.Contains("lineActive=False", line);
            Assert.Contains("iconSuppressed=True", line);
            Assert.Contains("bounds=[10.0,90.0]", line);
        }

        [Fact]
        public void EventAndKerbalSummaryFormatters_AreStable()
        {
            string reject = GameStateRecorder.FormatEventRejectSummary(
                "TechResearched",
                "operation-not-successful",
                "advRocketry",
                "Failed");
            string skippedTypes = GameStateEventConverter.FormatEventTypeCounts(
                new Dictionary<GameStateEventType, int>
                {
                    { GameStateEventType.FundsChanged, 2 },
                    { GameStateEventType.ScienceChanged, 1 }
                });
            string prePass = KerbalsModule.FormatPrePassSummary(
                examined: 5,
                cached: 3,
                nullRecordings: 1,
                missingRecordingIds: 1,
                rawCrewRecordings: 2,
                rawCrewMembers: 4,
                loopingChains: 1);
            string postWalk = KerbalsModule.FormatPostWalkSummary(
                reservationCount: 3,
                permanentReservations: 1,
                temporaryReservations: 2,
                slotCount: 2,
                retiredCount: 1,
                slotsCreated: 1);

            Assert.Contains("source=TechResearched", reject);
            Assert.Contains("reason=operation-not-successful", reject);
            Assert.Contains("FundsChanged:2", skippedTypes);
            Assert.Contains("ScienceChanged:1", skippedTypes);
            Assert.Contains("examined=5", prePass);
            Assert.Contains("rawCrewMembers=4", prePass);
            Assert.Contains("permanent=1", postWalk);
            Assert.Contains("temporary=2", postWalk);
        }

        [Fact]
        public void SpawnControlAndTestRunnerSummaries_AreCompact()
        {
            string reason = SpawnControlUI.ResolveAutoCloseReason(
                inFlight: true,
                hasFlight: true,
                candidateCount: 0);
            string spawn = SpawnControlUI.FormatAutoCloseSummary(reason, 0);
            string testRunner = InGameTestRunner.FormatSceneEligibilitySkipSummary(
                skipped: 2,
                currentScene: GameScenes.FLIGHT,
                skippedByRequiredScene: new Dictionary<GameScenes, int>
                {
                    { GameScenes.SPACECENTER, 1 },
                    { GameScenes.TRACKSTATION, 1 }
                });

            Assert.Equal("zero-candidates", reason);
            Assert.Contains("reason=zero-candidates", spawn);
            Assert.Contains("skipped=2", testRunner);
            Assert.Contains("currentScene=FLIGHT", testRunner);
            Assert.Contains("SPACECENTER:1", testRunner);
            Assert.Contains("TRACKSTATION:1", testRunner);
        }
    }
}
