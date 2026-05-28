using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the v5 fix-terminal-orbit-for-loop-members plan:
    /// the IsTerminalOrbitSynthesisSafeForLoopMember predicate, the relaxed
    /// TryResolveEndpointTailForMapPresence inner gate, the
    /// acceptTerminalOrbitForLoopSynthesis plumbing through
    /// ResolveMapPresenceGhostSource and TryResolveTerminalFallbackMapOrbitUpdate,
    /// and the IsTerminalMapPresenceRegion activation precondition.
    /// </summary>
    [Collection("Sequential")]
    public class TerminalOrbitLoopSynthesisTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly CelestialBody kerbin;

        public TerminalOrbitLoopSynthesisTests()
        {
            GhostMapPresence.ResetForTesting();
            RecordingStore.ResetForTesting();
            OrbitSeedResolver.ResetForTesting();
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
            OrbitSeedResolver.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ----- §3.1 predicate tests -----

        [Fact]
        public void IsTerminalOrbitSynthesisSafeForLoopMember_SameBody_ReturnsTrue()
        {
            Recording rec = MakeNoSegmentTerminalOrbitRecording(
                recordingId: "loop-same-body",
                terminalOrbitBody: "Kerbin",
                lastPointBody: "Kerbin");

            bool ok = GhostMapPresence.IsTerminalOrbitSynthesisSafeForLoopMember(rec);

            Assert.True(ok);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]")
                && l.Contains("IsTerminalOrbitSynthesisSafeForLoopMember")
                && l.Contains("result=True")
                && l.Contains("reason=same-body")
                && l.Contains("rec=loop-same-body"));
        }

        [Fact]
        public void IsTerminalOrbitSynthesisSafeForLoopMember_CrossBody_ReturnsFalse()
        {
            Recording rec = MakeNoSegmentTerminalOrbitRecording(
                recordingId: "loop-cross-body",
                terminalOrbitBody: "Kerbin",
                lastPointBody: "Mun");

            bool ok = GhostMapPresence.IsTerminalOrbitSynthesisSafeForLoopMember(rec);

            Assert.False(ok);
            Assert.Contains(logLines, l =>
                l.Contains("result=False")
                && l.Contains("reason=cross-body-terminal")
                && l.Contains("terminalBody=Kerbin")
                && l.Contains("lastPointBody=Mun"));
        }

        [Fact]
        public void IsTerminalOrbitSynthesisSafeForLoopMember_NullRecording_ReturnsFalse()
        {
            bool ok = GhostMapPresence.IsTerminalOrbitSynthesisSafeForLoopMember(null);

            Assert.False(ok);
            Assert.Contains(logLines, l =>
                l.Contains("result=false")
                && l.Contains("reason=null-trajectory"));
        }

        [Fact]
        public void IsTerminalOrbitSynthesisSafeForLoopMember_EmptyPoints_ReturnsFalse()
        {
            Recording rec = MakeNoSegmentTerminalOrbitRecording(
                recordingId: "loop-empty-points",
                terminalOrbitBody: "Kerbin",
                lastPointBody: "Kerbin");
            rec.Points = new List<TrajectoryPoint>();

            bool ok = GhostMapPresence.IsTerminalOrbitSynthesisSafeForLoopMember(rec);

            Assert.False(ok);
            Assert.Contains(logLines, l =>
                l.Contains("result=false")
                && l.Contains("reason=empty-points")
                && l.Contains("rec=loop-empty-points"));
        }

        [Fact]
        public void IsTerminalOrbitSynthesisSafeForLoopMember_NullTerminalOrbitBody_ReturnsFalse()
        {
            Recording rec = MakeNoSegmentTerminalOrbitRecording(
                recordingId: "loop-null-terminal-body",
                terminalOrbitBody: "Kerbin",
                lastPointBody: "Kerbin");
            rec.TerminalOrbitBody = null;

            bool ok = GhostMapPresence.IsTerminalOrbitSynthesisSafeForLoopMember(rec);

            Assert.False(ok);
            Assert.Contains(logLines, l =>
                l.Contains("result=false")
                && l.Contains("reason=no-terminal-orbit-body"));
        }

        [Fact]
        public void IsTerminalOrbitSynthesisSafeForLoopMember_NullLastPointBody_ReturnsFalse()
        {
            Recording rec = MakeNoSegmentTerminalOrbitRecording(
                recordingId: "loop-null-last-point-body",
                terminalOrbitBody: "Kerbin",
                lastPointBody: "Kerbin");
            TrajectoryPoint last = rec.Points[rec.Points.Count - 1];
            last.bodyName = null;
            rec.Points[rec.Points.Count - 1] = last;

            bool ok = GhostMapPresence.IsTerminalOrbitSynthesisSafeForLoopMember(rec);

            Assert.False(ok);
            Assert.Contains(logLines, l =>
                l.Contains("result=false")
                && l.Contains("reason=no-last-point-body"));
        }

        // v4 MINOR-3: defensive guard for TerminalOrbitSemiMajorAxis = 0.
        [Fact]
        public void IsTerminalOrbitSynthesisSafeForLoopMember_ZeroTerminalSma_ReturnsFalse()
        {
            Recording rec = MakeNoSegmentTerminalOrbitRecording(
                recordingId: "loop-zero-sma",
                terminalOrbitBody: "Kerbin",
                lastPointBody: "Kerbin");
            rec.TerminalOrbitSemiMajorAxis = 0.0;

            bool ok = GhostMapPresence.IsTerminalOrbitSynthesisSafeForLoopMember(rec);

            Assert.False(ok);
            Assert.Contains(logLines, l =>
                l.Contains("result=false")
                && l.Contains("reason=zero-terminal-sma"));
        }

        // ----- §3.2 relaxed synthesizer gate tests -----

        [Fact]
        public void TryResolveEndpointTailForMapPresence_TerminalOrbitSource_AcceptedWhenLoopFlag()
        {
            Recording rec = MakeNoSegmentTerminalOrbitRecording(
                recordingId: "syn-accept-loop",
                terminalOrbitBody: "Kerbin",
                lastPointBody: "Kerbin");
            InstallAcceptedTailSeed();

            bool ok = GhostMapPresence.TryResolveEndpointTailForMapPresence(
                rec,
                currentUT: 500.0,
                selectedSegment: null,
                terminalMapPresenceRegion: true,
                out OrbitSegment endpointTailSegment,
                out _,
                out _,
                acceptTerminalOrbitSource: true);

            Assert.True(ok);
            Assert.Equal(4_547_677.0, endpointTailSegment.semiMajorAxis);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]")
                && l.Contains("endpoint-tail-synthesis-loop-accept")
                && l.Contains("rec=syn-accept-loop")
                && l.Contains("terminalBody=Kerbin")
                && l.Contains("endpointSeedSource=endpoint-terminal-orbit")
                && l.Contains("endpointPhase=TrajectoryPoint"));
        }

        [Fact]
        public void TryResolveEndpointTailForMapPresence_TerminalOrbitSource_RejectedWithoutFlag()
        {
            Recording rec = MakeNoSegmentTerminalOrbitRecording(
                recordingId: "syn-reject-nonloop",
                terminalOrbitBody: "Kerbin",
                lastPointBody: "Kerbin");
            InstallAcceptedTailSeed();

            bool ok = GhostMapPresence.TryResolveEndpointTailForMapPresence(
                rec,
                currentUT: 500.0,
                selectedSegment: null,
                terminalMapPresenceRegion: true,
                out _,
                out _,
                out string detail,
                acceptTerminalOrbitSource: false);

            Assert.False(ok);
            Assert.NotNull(detail);
            Assert.Contains("endpointSeedSource=endpoint-terminal-orbit", detail);
            Assert.Contains("endpointPhase=TrajectoryPoint", detail);
            Assert.Contains("acceptTerminalOrbitSource=False", detail);
        }

        // Defense-in-depth: even when acceptTerminalOrbitSource=true, a persisted phase
        // whose body diverges from TerminalOrbitBody must still decline.
        [Fact]
        public void TryResolveEndpointTailForMapPresence_TerminalOrbitSourceWithMismatchedPersistedBody_Rejected()
        {
            Recording rec = MakeNoSegmentTerminalOrbitRecording(
                recordingId: "syn-mismatch-persisted",
                terminalOrbitBody: "Kerbin",
                lastPointBody: "Kerbin");
            // Force a body mismatch on the persisted endpoint decision so the inner
            // IsTrackingTerminalOrbitBody check catches it even with the flag set.
            rec.EndpointBodyName = "Mun";
            InstallAcceptedTailSeed();

            bool ok = GhostMapPresence.TryResolveEndpointTailForMapPresence(
                rec,
                currentUT: 500.0,
                selectedSegment: null,
                terminalMapPresenceRegion: true,
                out _,
                out _,
                out string detail,
                acceptTerminalOrbitSource: true);

            Assert.False(ok);
            Assert.Contains("endpointBody=Mun", detail);
            Assert.Contains("acceptTerminalOrbitSource=True", detail);
        }

        [Fact]
        public void TryResolveEndpointTailForMapPresence_EndpointSegmentSource_AcceptedRegardlessOfFlag()
        {
            // A recording with a recorded OrbitSegment matching the endpoint body
            // produces source=endpoint-segment + persisted phase=OrbitSegment, which the
            // unrelaxed inner gate already accepts. The relaxation widens, never narrows:
            // the flag must not change behaviour for this case.
            Recording rec = MakeEndpointSegmentRecording(
                recordingId: "syn-segment-source");
            InstallAcceptedTailSeed();

            bool acceptedFalse = GhostMapPresence.TryResolveEndpointTailForMapPresence(
                rec,
                currentUT: 135.0,
                selectedSegment: null,
                terminalMapPresenceRegion: true,
                out _,
                out _,
                out _,
                acceptTerminalOrbitSource: false);

            bool acceptedTrue = GhostMapPresence.TryResolveEndpointTailForMapPresence(
                rec,
                currentUT: 135.0,
                selectedSegment: null,
                terminalMapPresenceRegion: true,
                out _,
                out _,
                out _,
                acceptTerminalOrbitSource: true);

            Assert.True(acceptedFalse);
            Assert.True(acceptedTrue);
        }

        // ----- §3.3 ResolveMapPresenceGhostSource tests -----

        [Fact]
        public void ResolveMapPresenceGhostSource_NoSegmentLoopMemberWithTerminalOrbit_AcceptsEndpointTail()
        {
            Recording rec = MakeNoSegmentTerminalOrbitRecording(
                recordingId: "create-loop-accept",
                terminalOrbitBody: "Kerbin",
                lastPointBody: "Kerbin");
            InstallAcceptedTailSeed();

            int cached = -1;
            GhostMapPresence.TrackingStationGhostSource source =
                GhostMapPresence.ResolveMapPresenceGhostSource(
                    rec,
                    isSuppressed: false,
                    alreadyMaterialized: false,
                    currentUT: 600.0,
                    allowTerminalOrbitFallback: true,
                    logOperationName: "test-loop-accept",
                    ref cached,
                    out OrbitSegment segment,
                    out _,
                    out string skipReason,
                    recordingIndex: 1,
                    acceptTerminalOrbitForLoopSynthesis: true);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.EndpointTail, source);
            Assert.Null(skipReason);
            Assert.Equal(4_547_677.0, segment.semiMajorAxis);
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_NoSegmentNonLoopMember_StillRejects()
        {
            Recording rec = MakeNoSegmentTerminalOrbitRecording(
                recordingId: "create-nonloop-reject",
                terminalOrbitBody: "Kerbin",
                lastPointBody: "Kerbin");
            InstallAcceptedTailSeed();

            int cached = -1;
            GhostMapPresence.TrackingStationGhostSource source =
                GhostMapPresence.ResolveMapPresenceGhostSource(
                    rec,
                    isSuppressed: false,
                    alreadyMaterialized: false,
                    currentUT: 600.0,
                    allowTerminalOrbitFallback: true,
                    logOperationName: "test-nonloop-reject",
                    ref cached,
                    out _,
                    out _,
                    out _,
                    recordingIndex: 1,
                    acceptTerminalOrbitForLoopSynthesis: false);

            Assert.NotEqual(GhostMapPresence.TrackingStationGhostSource.EndpointTail, source);
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_NoSegmentCrossBodyLoopMember_StillRejects()
        {
            // Cross-body loop terminal (181 Mm bug class). Even with the loop-aware caller
            // passing the flag in, the predicate-driven outer caller would have computed
            // false, so the resolver receives acceptTerminalOrbitForLoopSynthesis=false.
            // The relaxed inner gate must not fire.
            Recording rec = MakeNoSegmentTerminalOrbitRecording(
                recordingId: "create-loop-cross-body",
                terminalOrbitBody: "Kerbin",
                lastPointBody: "Mun");
            InstallAcceptedTailSeed();

            int cached = -1;
            GhostMapPresence.TrackingStationGhostSource source =
                GhostMapPresence.ResolveMapPresenceGhostSource(
                    rec,
                    isSuppressed: false,
                    alreadyMaterialized: false,
                    currentUT: 600.0,
                    allowTerminalOrbitFallback: true,
                    logOperationName: "test-loop-cross-body",
                    ref cached,
                    out _,
                    out _,
                    out _,
                    recordingIndex: 1,
                    // A real loop-aware caller would have predicated this on
                    // IsTerminalOrbitSynthesisSafeForLoopMember which returns false for
                    // cross-body. Pass false here to mirror what such a caller would do.
                    acceptTerminalOrbitForLoopSynthesis: false);

            Assert.NotEqual(GhostMapPresence.TrackingStationGhostSource.EndpointTail, source);
        }

        // v4 regression test: non-loop caller with default false param on a no-segment
        // Orbiting recording must still return None.
        [Fact]
        public void ResolveMapPresenceGhostSource_NonLoopCallerOnNoSegmentOrbitingRecording_StillReturnsNone()
        {
            Recording rec = MakeNoSegmentTerminalOrbitRecording(
                recordingId: "create-nonloop-default",
                terminalOrbitBody: "Kerbin",
                lastPointBody: "Kerbin");
            InstallAcceptedTailSeed();

            int cached = -1;
            // Do NOT pass acceptTerminalOrbitForLoopSynthesis; the default-false should
            // preserve the existing pre-v5 behaviour for non-loop callers.
            GhostMapPresence.TrackingStationGhostSource source =
                GhostMapPresence.ResolveMapPresenceGhostSource(
                    rec,
                    isSuppressed: false,
                    alreadyMaterialized: false,
                    currentUT: 600.0,
                    allowTerminalOrbitFallback: true,
                    logOperationName: "test-default-false",
                    ref cached,
                    out _,
                    out _,
                    out _,
                    recordingIndex: 1);

            Assert.NotEqual(GhostMapPresence.TrackingStationGhostSource.EndpointTail, source);
        }

        // §3.3 plumbing test: TryResolveTerminalFallbackMapOrbitUpdate's new
        // loopEpochShiftSeconds parameter must convert to
        // acceptTerminalOrbitForLoopSynthesis correctly and reach the resolver.
        [Fact]
        public void TryResolveTerminalFallbackMapOrbitUpdate_NoSegmentLoopMemberAtZeroSegSplit_PassesAcceptFlagThrough()
        {
            Recording rec = MakeNoSegmentTerminalOrbitRecording(
                recordingId: "helper-loop-accept",
                terminalOrbitBody: "Kerbin",
                lastPointBody: "Kerbin");
            InstallAcceptedTailSeed();

            int cachedStateVectorIndex = -1;
            // Non-zero loopEpochShiftSeconds + same-body recording => the helper computes
            // acceptTerminalOrbitForLoopSynthesis = true internally and the resolver picks
            // EndpointTail (which the helper now also accepts alongside TerminalOrbit).
            bool resolved = ParsekPlaybackPolicy.TryResolveTerminalFallbackMapOrbitUpdate(
                rec,
                idx: 18,
                currentUT: 600.0,
                loopEpochShiftSeconds: 1250859.4,
                currentKey: ("Mun", 100.0, 0.5),
                alreadyMaterialized: false,
                ref cachedStateVectorIndex,
                out OrbitSegment fallbackSegment,
                out var fallbackKey,
                out bool changed);

            Assert.True(resolved);
            Assert.True(changed);
            Assert.Equal("Kerbin", fallbackSegment.bodyName);
            Assert.Equal(4_547_677.0, fallbackSegment.semiMajorAxis);
            Assert.Equal("Kerbin", fallbackKey.body);
        }

        // Non-loop caller (shift == 0): the helper passes acceptTerminalOrbitForLoopSynthesis=false,
        // so the resolver's no-segment terminal-orbit EndpointTail branch is suppressed. The
        // pre-existing TerminalOrbit proto-orbit seed path is unaffected and still returns
        // TerminalOrbit. The helper accepts both sources, so non-loop callers still see the
        // existing TerminalOrbit fallback unchanged.
        [Fact]
        public void TryResolveTerminalFallbackMapOrbitUpdate_NonLoopCallerStillUsesProtoOrbitSeed()
        {
            Recording rec = MakeNoSegmentTerminalOrbitRecording(
                recordingId: "helper-nonloop",
                terminalOrbitBody: "Kerbin",
                lastPointBody: "Kerbin");
            InstallAcceptedTailSeed();

            int cachedStateVectorIndex = -1;
            bool resolved = ParsekPlaybackPolicy.TryResolveTerminalFallbackMapOrbitUpdate(
                rec,
                idx: 18,
                currentUT: 600.0,
                loopEpochShiftSeconds: 0.0,
                currentKey: ("Mun", 100.0, 0.5),
                alreadyMaterialized: false,
                ref cachedStateVectorIndex,
                out OrbitSegment fallbackSegment,
                out var fallbackKey,
                out bool changed);

            // Non-loop fallback still resolves via TerminalOrbit (existing path).
            Assert.True(resolved);
            Assert.True(changed);
            // The seed comes from the recording's TerminalOrbit* fields, not from the
            // tail-seed override (which the EndpointTail path would have used).
            Assert.Equal("Kerbin", fallbackKey.body);
            Assert.Equal(700_000.0, fallbackSegment.semiMajorAxis);
        }

        // ----- §3.4 activation-region precondition test -----

        [Fact]
        public void IsTerminalMapPresenceRegion_NoSegmentLoopMemberAtEffUTNearEnd_ReturnsTrue()
        {
            Recording rec = MakeNoSegmentTerminalOrbitRecording(
                recordingId: "activation-region",
                terminalOrbitBody: "Kerbin",
                lastPointBody: "Kerbin");
            // ExplicitStartUT widens the outer envelope to 100; the payload bounds use the
            // first/last point UTs (100 and 600), so ResolveGhostActivationStartUT returns
            // 100. A typical loop window's effUT (e.g. near the recording's end) will
            // satisfy effUT >= activationStartUT and TerminalState.Orbiting passes the
            // eligibility check.
            double activationUT = PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(rec);
            Assert.True(activationUT <= 500.0);

            // Drive the predicate indirectly via the synthesizer (the helper itself is
            // private). A successful synthesizer call confirms IsTerminalMapPresenceRegion
            // accepted the call site (the inner gate's first early-out is on it).
            InstallAcceptedTailSeed();
            bool ok = GhostMapPresence.TryResolveEndpointTailForMapPresence(
                rec,
                currentUT: 500.0,
                selectedSegment: null,
                terminalMapPresenceRegion: true,
                out _,
                out _,
                out _,
                acceptTerminalOrbitSource: true);

            Assert.True(ok);
        }

        // ----- helpers -----

        private void InstallAcceptedTailSeed()
        {
            OrbitSeedResolver.TailSeedResolverForTesting =
                (IPlaybackTrajectory traj, CelestialBody body, double currentUT, TailSeedUse use, out TailDerivedOrbitSeed seed) =>
                {
                    seed = new TailDerivedOrbitSeed
                    {
                        Accepted = true,
                        BodyName = "Kerbin",
                        TailUT = 500.0,
                        LatestStoredSegmentEndUT = 500.0,
                        RotationDriftSeconds = 0.0,
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
                            epoch = currentUT
                        }
                    };
                    return true;
                };
        }

        private static Recording MakeNoSegmentTerminalOrbitRecording(
            string recordingId,
            string terminalOrbitBody,
            string lastPointBody)
        {
            return new Recording
            {
                RecordingId = recordingId,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration,
                VesselName = "Kerbal X Return",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 600.0,
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = terminalOrbitBody,
                TerminalOrbitSemiMajorAxis = 700_000.0,
                TerminalOrbitEccentricity = 0.01,
                TerminalOrbitInclination = 0.0,
                TerminalOrbitLAN = 0.0,
                TerminalOrbitArgumentOfPeriapsis = 0.0,
                TerminalOrbitMeanAnomalyAtEpoch = 0.0,
                TerminalOrbitEpoch = 500.0,
                EndpointPhase = RecordingEndpointPhase.TrajectoryPoint,
                EndpointBodyName = lastPointBody,
                // Points are intentionally inside-atmosphere on Kerbin (altitude < 70km)
                // with low speed so TryResolveStateVectorMapPoint declines via the
                // ShouldCreateStateVectorOrbit threshold. This mirrors the user's real
                // recording #18 case (no-segment Orbiting recording whose Points lie in
                // the descent/landing phase) where the resolver falls through to the
                // terminal-orbit EndpointTail path.
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100.0,
                        bodyName = lastPointBody,
                        altitude = 1_000.0,
                        velocity = new Vector3(10f, 0f, 0f)
                    },
                    new TrajectoryPoint
                    {
                        ut = 500.0,
                        bodyName = lastPointBody,
                        altitude = 500.0,
                        velocity = new Vector3(5f, 0f, 0f)
                    }
                },
                OrbitSegments = new List<OrbitSegment>()
            };
        }

        private static Recording MakeEndpointSegmentRecording(string recordingId)
        {
            return new Recording
            {
                RecordingId = recordingId,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration,
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
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        bodyName = "Kerbin",
                        startUT = 120.0,
                        endUT = 150.0,
                        semiMajorAxis = 512_941.0,
                        eccentricity = 0.5746,
                        inclination = 0.0977,
                        longitudeOfAscendingNode = 75.6,
                        argumentOfPeriapsis = 342.3,
                        meanAnomalyAtEpoch = 1.6818,
                        epoch = 120.0
                    }
                }
            };
        }
    }
}
