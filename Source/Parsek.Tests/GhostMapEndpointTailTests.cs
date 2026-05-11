using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class GhostMapEndpointTailTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly CelestialBody kerbin;

        public GhostMapEndpointTailTests()
        {
            GhostMapPresence.ResetForTesting();
            RecordingStore.ResetForTesting();
            kerbin = TestBodyRegistry.CreateBody("Kerbin", 600000.0, 3.5316e12);
            GhostMapPresence.FindBodyByNameForTesting = bodyName => kerbin;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            GhostMapPresence.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void ShouldOverrideVisibleSegmentWithEndpointTail_StaleEndpointSegment_ReturnsTrue()
        {
            OrbitSegment selected = Segment(startUT: 120.0, endUT: 150.0);
            TailDerivedOrbitSeed tailSeed = AcceptedTailSeed(
                tailUT: 453.66,
                latestStoredSegmentEndUT: 150.0);

            bool shouldOverride = GhostMapPresence.ShouldOverrideVisibleSegmentWithEndpointTail(
                selected,
                preferredEndpointBody: "Kerbin",
                endpointSeedSource: "endpoint-segment",
                tailSeed,
                terminalMapPresenceRegion: true);

            Assert.True(shouldOverride);
        }

        [Fact]
        public void ShouldOverrideVisibleSegmentWithEndpointTail_LegitimateInWindowCheckpoint_ReturnsFalse()
        {
            OrbitSegment selected = Segment(startUT: 440.0, endUT: 470.0);
            TailDerivedOrbitSeed tailSeed = AcceptedTailSeed(
                tailUT: 453.66,
                latestStoredSegmentEndUT: 150.0);

            bool shouldOverride = GhostMapPresence.ShouldOverrideVisibleSegmentWithEndpointTail(
                selected,
                preferredEndpointBody: "Kerbin",
                endpointSeedSource: "endpoint-segment",
                tailSeed,
                terminalMapPresenceRegion: true);

            Assert.False(shouldOverride);
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_EndpointTailOverridesStaleVisibleSegmentDespiteLargeDrift()
        {
            Recording rec = BuildEndpointRecording(Segment(startUT: 120.0, endUT: 150.0));
            bool seamCalled = false;
            OrbitSeedResolver.TailSeedResolverForTesting =
                (IPlaybackTrajectory traj, CelestialBody body, double currentUT, TailSeedUse use, out TailDerivedOrbitSeed seed) =>
                {
                    seamCalled = true;
                    Assert.Equal(TailSeedUse.MapPresence, use);
                    Assert.Equal(135.7, currentUT, 1);
                    seed = AcceptedTailSeed(
                        tailUT: 453.66,
                        latestStoredSegmentEndUT: 150.0,
                        rotationDriftSeconds: currentUT - 453.66);
                    return true;
                };
            Assert.True(RecordingEndpointResolver.TryGetEndpointAlignedOrbitSeed(
                rec,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                out RecordingEndpointResolver.EndpointOrbitSeedDiagnostics endpointDiagnostics));
            Assert.Equal("endpoint-segment", endpointDiagnostics.Source);
            Assert.True(PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(rec) <= 135.7);

            int cached = -1;
            GhostMapPresence.TrackingStationGhostSource source =
                GhostMapPresence.ResolveMapPresenceGhostSource(
                    rec,
                    isSuppressed: false,
                    alreadyMaterialized: false,
                    currentUT: 135.7,
                    allowTerminalOrbitFallback: true,
                    logOperationName: "test-endpoint-tail",
                    ref cached,
                    out OrbitSegment segment,
                    out _,
                    out string skipReason);

            Assert.True(seamCalled);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.EndpointTail, source);
            Assert.Null(skipReason);
            Assert.Equal(453.66, segment.epoch, 2);
            Assert.Equal(4_547_677.0, segment.semiMajorAxis);
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_HistoricalTailDeclines_KeepsVisibleSegment()
        {
            Recording rec = BuildEndpointRecording(Segment(startUT: 120.0, endUT: 150.0));
            OrbitSeedResolver.TailSeedResolverForTesting =
                (IPlaybackTrajectory traj, CelestialBody body, double currentUT, TailSeedUse use, out TailDerivedOrbitSeed seed) =>
                {
                    seed = new TailDerivedOrbitSeed
                    {
                        Accepted = false,
                        DeclineReason = "historical-rotation-unavailable",
                        BodyName = "Kerbin",
                        TailUT = 453.66,
                        LatestStoredSegmentEndUT = 150.0,
                        RotationDriftSeconds = currentUT - 453.66,
                        TailFrameSource = "absolute"
                    };
                    return false;
                };

            int cached = -1;
            GhostMapPresence.TrackingStationGhostSource source =
                GhostMapPresence.ResolveMapPresenceGhostSource(
                    rec,
                    isSuppressed: false,
                    alreadyMaterialized: false,
                    currentUT: 135.7,
                    allowTerminalOrbitFallback: true,
                    logOperationName: "test-endpoint-tail-decline",
                    ref cached,
                    out OrbitSegment segment,
                    out _,
                    out string skipReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.Segment, source);
            Assert.Null(skipReason);
            Assert.Equal(120.0, segment.startUT);
            Assert.Equal(150.0, segment.endUT);
            Assert.Contains(logLines, line =>
                line.Contains("source=Segment")
                && line.Contains("endpointTailSeed=decline")
                && line.Contains("tailDecline=historical-rotation-unavailable"));
        }

        [Fact]
        public void ResolveTrackingStationGhostSource_HistoricalTailDeclines_LogsTailDetailOnSegmentPath()
        {
            Recording rec = BuildEndpointRecording(Segment(startUT: 120.0, endUT: 150.0));
            OrbitSeedResolver.TailSeedResolverForTesting =
                (IPlaybackTrajectory traj, CelestialBody body, double currentUT, TailSeedUse use, out TailDerivedOrbitSeed seed) =>
                {
                    seed = new TailDerivedOrbitSeed
                    {
                        Accepted = false,
                        DeclineReason = "historical-rotation-unavailable",
                        BodyName = "Kerbin",
                        TailUT = 453.66,
                        LatestStoredSegmentEndUT = 150.0,
                        RotationDriftSeconds = currentUT - 453.66,
                        TailFrameSource = "absolute"
                    };
                    return false;
                };

            GhostMapPresence.TrackingStationGhostSource source =
                GhostMapPresence.ResolveTrackingStationGhostSource(
                    rec,
                    isSuppressed: false,
                    currentUT: 135.7,
                    out OrbitSegment segment,
                    out string skipReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.Segment, source);
            Assert.Null(skipReason);
            Assert.Equal(120.0, segment.startUT);
            Assert.Equal(150.0, segment.endUT);
            Assert.Contains(logLines, line =>
                line.Contains("ResolveTrackingStationGhostSource")
                && line.Contains("source=Segment")
                && line.Contains("endpointTailSeed=decline")
                && line.Contains("tailDecline=historical-rotation-unavailable"));
        }

        [Fact]
        public void ResolveTrackingStationGhostSource_EndpointTail_LogsTailAccepted()
        {
            Recording rec = BuildEndpointRecording(Segment(startUT: 120.0, endUT: 150.0));
            OrbitSeedResolver.TailSeedResolverForTesting =
                (IPlaybackTrajectory traj, CelestialBody body, double currentUT, TailSeedUse use, out TailDerivedOrbitSeed seed) =>
                {
                    seed = AcceptedTailSeed(
                        tailUT: 453.66,
                        latestStoredSegmentEndUT: 150.0,
                        rotationDriftSeconds: currentUT - 453.66);
                    return true;
                };

            GhostMapPresence.TrackingStationGhostSource source =
                GhostMapPresence.ResolveTrackingStationGhostSource(
                    rec,
                    isSuppressed: false,
                    currentUT: 135.7,
                    out OrbitSegment segment,
                    out string skipReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.EndpointTail, source);
            Assert.Null(skipReason);
            Assert.Equal(453.66, segment.epoch, 2);
            Assert.Contains(logLines, line =>
                line.Contains("ResolveTrackingStationGhostSource")
                && line.Contains("source=EndpointTail")
                && line.Contains("endpointTailSeed=accept")
                && line.Contains("tailSma=4547677.0"));
        }

        [Fact]
        public void ResolveTrackingStationGhostSource_NonTerminalSegment_LogsTailBypass()
        {
            Recording rec = BuildEndpointRecording(Segment(startUT: 120.0, endUT: 150.0));
            rec.TerminalStateValue = TerminalState.SubOrbital;
            OrbitSeedResolver.TailSeedResolverForTesting =
                (IPlaybackTrajectory traj, CelestialBody body, double currentUT, TailSeedUse use, out TailDerivedOrbitSeed seed) =>
                {
                    seed = AcceptedTailSeed(
                        tailUT: 453.66,
                        latestStoredSegmentEndUT: 150.0,
                        rotationDriftSeconds: currentUT - 453.66);
                    return true;
                };

            GhostMapPresence.TrackingStationGhostSource source =
                GhostMapPresence.ResolveTrackingStationGhostSource(
                    rec,
                    isSuppressed: false,
                    currentUT: 135.7,
                    out OrbitSegment segment,
                    out string skipReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.Segment, source);
            Assert.Null(skipReason);
            Assert.Equal(120.0, segment.startUT);
            Assert.Equal(150.0, segment.endUT);
            Assert.Contains(logLines, line =>
                line.Contains("ResolveTrackingStationGhostSource")
                && line.Contains("source=Segment")
                && line.Contains("endpointTailSeed=bypass")
                && line.Contains("tailDecline=not-terminal-map-presence"));
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_TerminalOrbitBackedSegmentDoesNotChooseEndpointTail()
        {
            Recording rec = BuildEndpointRecording(Segment(startUT: 120.0, endUT: 150.0));
            rec.EndpointPhase = RecordingEndpointPhase.Unknown;
            rec.EndpointBodyName = null;
            OrbitSeedResolver.TailSeedResolverForTesting =
                (IPlaybackTrajectory traj, CelestialBody body, double currentUT, TailSeedUse use, out TailDerivedOrbitSeed seed) =>
                {
                    seed = AcceptedTailSeed(
                        tailUT: 453.66,
                        latestStoredSegmentEndUT: 150.0,
                        rotationDriftSeconds: currentUT - 453.66);
                    return true;
                };

            int cached = -1;
            GhostMapPresence.TrackingStationGhostSource source =
                GhostMapPresence.ResolveMapPresenceGhostSource(
                    rec,
                    isSuppressed: false,
                    alreadyMaterialized: false,
                    currentUT: 135.7,
                    allowTerminalOrbitFallback: true,
                    logOperationName: "test-terminal-backed-segment",
                    ref cached,
                    out OrbitSegment segment,
                    out _,
                    out string skipReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.Segment, source);
            Assert.Null(skipReason);
            Assert.Equal(120.0, segment.startUT);
            Assert.Equal(150.0, segment.endUT);
            Assert.Contains(logLines, line =>
                line.Contains("source=Segment")
                && line.Contains("endpointTailSeed=decline")
                && line.Contains("tailDecline=endpoint-family-not-segment"));
        }

        [Fact]
        public void TryGetVisibleOrbitBoundsForGhostVessel_EndpointTailUsesStoredBoundsBeforeCommittedSegments()
        {
            Recording rec = BuildEndpointRecording(Segment(startUT: 120.0, endUT: 150.0));
            RecordingStore.AddCommittedInternal(rec);
            const uint ghostPid = 987654321u;
            GhostMapPresence.TrackEndpointTailGhostBoundsForTesting(
                ghostPid,
                recordingIndex: 0,
                recordingId: rec.RecordingId,
                bodyName: "Kerbin",
                startUT: 100.0,
                endUT: 600.0);

            bool resolved = GhostMapPresence.TryGetVisibleOrbitBoundsForGhostVessel(
                ghostPid,
                currentUT: 135.7,
                out double startUT,
                out double endUT);

            Assert.True(resolved);
            Assert.Equal(100.0, startUT);
            Assert.Equal(600.0, endUT);
            Assert.Contains(logLines, line =>
                line.Contains("source=stored-bounds-endpoint-tail")
                && line.Contains("windowUT=100.00-600.00"));
        }

        private static Recording BuildEndpointRecording(OrbitSegment selectedSegment)
        {
            return new Recording
            {
                RecordingId = "rec_f1363fc127ab47a28812ce4be6515453",
                VesselName = "Kerbal X Probe",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 600.0,
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 4_547_677.0,
                TerminalOrbitEccentricity = 0.822,
                EndpointPhase = RecordingEndpointPhase.OrbitSegment,
                EndpointBodyName = "Kerbin",
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100.0,
                        bodyName = "Kerbin",
                        altitude = 80_000.0,
                        velocity = new Vector3(1000f, 0f, 0f)
                    },
                    new TrajectoryPoint
                    {
                        ut = 600.0,
                        bodyName = "Kerbin",
                        altitude = 208_283.0,
                        velocity = new Vector3(296.0f, 3.8f, -2806.1f)
                    }
                },
                OrbitSegments = new List<OrbitSegment> { selectedSegment }
            };
        }

        private static OrbitSegment Segment(double startUT, double endUT)
        {
            return new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = startUT,
                endUT = endUT,
                semiMajorAxis = 512_941.0,
                eccentricity = 0.5746,
                inclination = 0.0977,
                longitudeOfAscendingNode = 75.6,
                argumentOfPeriapsis = 342.3,
                meanAnomalyAtEpoch = 1.6818,
                epoch = startUT
            };
        }

        private static TailDerivedOrbitSeed AcceptedTailSeed(
            double tailUT,
            double latestStoredSegmentEndUT,
            double rotationDriftSeconds = -317.96)
        {
            return new TailDerivedOrbitSeed
            {
                Accepted = true,
                BodyName = "Kerbin",
                TailUT = tailUT,
                LatestStoredSegmentEndUT = latestStoredSegmentEndUT,
                RotationDriftSeconds = rotationDriftSeconds,
                TailFrameSource = "absolute",
                UsedHistoricalBodyRotation = true,
                HistoricalLongitude = -31.0,
                Segment = new OrbitSegment
                {
                    bodyName = "Kerbin",
                    startUT = 100.0,
                    endUT = 600.0,
                    semiMajorAxis = 4_547_677.0,
                    eccentricity = 0.822,
                    inclination = 0.1,
                    longitudeOfAscendingNode = 76.0,
                    argumentOfPeriapsis = 340.0,
                    meanAnomalyAtEpoch = 1.0,
                    epoch = tailUT
                }
            };
        }
    }
}
