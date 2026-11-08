using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization tests for small pure decisions of <see cref="GhostMapPresence"/>
    /// that no test called directly: the OrbitalCheckpoint state-vector SOI-gap carve-out,
    /// the spawn-suppression reason normalizer, the map orbit key, the terminal-state and
    /// materialized gates, the absolute-frame check and the committed-id lookup.
    /// </summary>
    public class GhostMapPresenceCharacterizationTests
    {
        private static TrajectoryPoint Pt(double ut, string body = "Kerbin")
        {
            return new TrajectoryPoint { ut = ut, bodyName = body };
        }

        private static OrbitSegment Seg(double start, double end, string body)
        {
            return new OrbitSegment
            {
                startUT = start,
                endUT = end,
                bodyName = body,
                semiMajorAxis = 700000.0,
                eccentricity = 0.01
            };
        }

        /// <summary>
        /// A trajectory whose playback window is [100, 300] with an SOI gap
        /// (Kerbin segment ends at 150, Mun segment starts at 180).
        /// </summary>
        private static Recording SoiGapRecording()
        {
            var rec = new Recording { RecordingId = "rec_soi" };
            rec.Points.Add(Pt(100.0));
            rec.Points.Add(Pt(300.0, "Mun"));
            rec.OrbitSegments.Add(Seg(100.0, 150.0, "Kerbin"));
            rec.OrbitSegments.Add(Seg(180.0, 300.0, "Mun"));
            return rec;
        }

        // ---------- EvaluateOrbitalCheckpointStateVectorFallback ----------------

        [Fact]
        public void EvaluateFallback_SoiGapWithMatchingBodyInsideWindow_Accepted()
        {
            var d = GhostMapPresence.EvaluateOrbitalCheckpointStateVectorFallback(
                SoiGapRecording(), 165.0, Pt(165.0, "Mun"),
                segmentSourceAvailable: false, allowSoiGapRecovery: true,
                expectedSoiGapBody: "Kerbin");

            Assert.True(d.Accepted);
            Assert.Equal(GhostMapPresence.SoiGapStateVectorFallbackReason, d.Reason);
            // The post-gap segment body overrides the caller's hint.
            Assert.Equal("Mun", d.ExpectedBody);
            Assert.Equal("Kerbin", d.GapPreviousBody);
            Assert.Equal("Mun", d.GapNextBody);
            Assert.True(d.GapBodyTransition);
            Assert.True(d.IsSoiGapRecovery);
            Assert.True(d.BodyMatches);
            Assert.True(d.WithinPlaybackWindow);
            Assert.Equal(100.0, d.ActivationStartUT);
            Assert.Equal(300.0, d.EndUT);
        }

        [Fact]
        public void EvaluateFallback_SegmentSourceAvailable_RejectedFirst()
        {
            var d = GhostMapPresence.EvaluateOrbitalCheckpointStateVectorFallback(
                SoiGapRecording(), 165.0, Pt(165.0, "Mun"),
                segmentSourceAvailable: true, allowSoiGapRecovery: true, expectedSoiGapBody: null);
            Assert.False(d.Accepted);
            Assert.Equal(GhostMapPresence.OrbitalCheckpointStateVectorRejectSaferSegment, d.Reason);
        }

        [Fact]
        public void EvaluateFallback_RecoveryNotAllowedOrNoGap_NotSoiGap()
        {
            var notAllowed = GhostMapPresence.EvaluateOrbitalCheckpointStateVectorFallback(
                SoiGapRecording(), 165.0, Pt(165.0, "Mun"),
                segmentSourceAvailable: false, allowSoiGapRecovery: false, expectedSoiGapBody: "Mun");
            Assert.False(notAllowed.Accepted);
            Assert.Equal(GhostMapPresence.OrbitalCheckpointStateVectorRejectNotSoiGap, notAllowed.Reason);

            // 120 is inside the first segment, so there is no gap and the hint stays.
            var noGap = GhostMapPresence.EvaluateOrbitalCheckpointStateVectorFallback(
                SoiGapRecording(), 120.0, Pt(120.0, "Kerbin"),
                segmentSourceAvailable: false, allowSoiGapRecovery: true, expectedSoiGapBody: "Kerbin");
            Assert.False(noGap.Accepted);
            Assert.Equal(GhostMapPresence.OrbitalCheckpointStateVectorRejectNotSoiGap, noGap.Reason);
            Assert.Equal("Kerbin", noGap.ExpectedBody);
            Assert.Null(noGap.GapPreviousBody);
        }

        [Fact]
        public void EvaluateFallback_SameBodyGap_NotSoiGap()
        {
            var rec = new Recording { RecordingId = "rec_same" };
            rec.Points.Add(Pt(100.0));
            rec.Points.Add(Pt(300.0));
            rec.OrbitSegments.Add(Seg(100.0, 150.0, "Kerbin"));
            rec.OrbitSegments.Add(Seg(180.0, 300.0, "Kerbin"));

            var d = GhostMapPresence.EvaluateOrbitalCheckpointStateVectorFallback(
                rec, 165.0, Pt(165.0, "Kerbin"),
                segmentSourceAvailable: false, allowSoiGapRecovery: true, expectedSoiGapBody: null);
            Assert.False(d.Accepted);
            Assert.False(d.GapBodyTransition);
            Assert.Equal(GhostMapPresence.OrbitalCheckpointStateVectorRejectNotSoiGap, d.Reason);
        }

        [Fact]
        public void EvaluateFallback_StateVectorOnWrongBody_BodyMismatch()
        {
            var d = GhostMapPresence.EvaluateOrbitalCheckpointStateVectorFallback(
                SoiGapRecording(), 165.0, Pt(165.0, "Kerbin"),
                segmentSourceAvailable: false, allowSoiGapRecovery: true, expectedSoiGapBody: "Kerbin");
            Assert.False(d.Accepted);
            Assert.False(d.BodyMatches);
            Assert.Equal(GhostMapPresence.OrbitalCheckpointStateVectorRejectBodyMismatch, d.Reason);
        }

        [Fact]
        public void EvaluateFallback_PointOutsidePlaybackWindow_OutsideWindow()
        {
            var d = GhostMapPresence.EvaluateOrbitalCheckpointStateVectorFallback(
                SoiGapRecording(), 165.0, Pt(350.0, "Mun"),
                segmentSourceAvailable: false, allowSoiGapRecovery: true, expectedSoiGapBody: null);
            Assert.False(d.Accepted);
            Assert.False(d.WithinPlaybackWindow);
            Assert.Equal(GhostMapPresence.OrbitalCheckpointStateVectorRejectOutsideWindow, d.Reason);
        }

        [Fact]
        public void EvaluateFallback_NullTrajectory_NotSoiGapAndNaNEnd()
        {
            var d = GhostMapPresence.EvaluateOrbitalCheckpointStateVectorFallback(
                null, 165.0, Pt(165.0, "Mun"),
                segmentSourceAvailable: false, allowSoiGapRecovery: true, expectedSoiGapBody: "Mun");
            Assert.False(d.Accepted);
            Assert.False(d.WithinPlaybackWindow);
            Assert.True(double.IsNaN(d.EndUT));
            Assert.Equal(GhostMapPresence.OrbitalCheckpointStateVectorRejectNotSoiGap, d.Reason);
        }

        // ---------- NormalizeTrackingStationSpawnSuppressionReason --------------

        [Fact]
        public void NormalizeTrackingStationSpawnSuppressionReason_MapsTheLegacyPhrase()
        {
            Assert.Equal(GhostMapPresence.TrackingStationSpawnSkipIntermediateGhostChainLink,
                GhostMapPresence.NormalizeTrackingStationSpawnSuppressionReason("intermediate ghost chain link"));
            Assert.Equal("something else",
                GhostMapPresence.NormalizeTrackingStationSpawnSuppressionReason("something else"));
            Assert.Null(GhostMapPresence.NormalizeTrackingStationSpawnSuppressionReason(null));
        }

        // ---------- TryGetMapOrbitKey -------------------------------------------

        [Fact]
        public void TryGetMapOrbitKey_OnlySegmentAndTerminalOrbitSourcesHaveAKey()
        {
            var seg = Seg(0.0, 10.0, "Mun");
            seg.semiMajorAxis = 250000.0;
            seg.eccentricity = 0.2;

            Assert.True(GhostMapPresence.TryGetMapOrbitKey(
                GhostMapPresence.TrackingStationGhostSource.Segment, seg, out var key));
            Assert.Equal(("Mun", 250000.0, 0.2), key);
            Assert.True(GhostMapPresence.TryGetMapOrbitKey(
                GhostMapPresence.TrackingStationGhostSource.TerminalOrbit, seg, out _));

            foreach (var source in new[]
            {
                GhostMapPresence.TrackingStationGhostSource.None,
                GhostMapPresence.TrackingStationGhostSource.StateVector,
                GhostMapPresence.TrackingStationGhostSource.StateVectorSoiGap,
                GhostMapPresence.TrackingStationGhostSource.EndpointTail,
            })
            {
                Assert.False(GhostMapPresence.TryGetMapOrbitKey(source, seg, out var none));
                Assert.Null(none.body);
            }
        }

        [Fact]
        public void IsStateVectorGhostSource_OnlyTheTwoStateVectorKinds()
        {
            Assert.True(GhostMapPresence.IsStateVectorGhostSource(
                GhostMapPresence.TrackingStationGhostSource.StateVector));
            Assert.True(GhostMapPresence.IsStateVectorGhostSource(
                GhostMapPresence.TrackingStationGhostSource.StateVectorSoiGap));
            Assert.False(GhostMapPresence.IsStateVectorGhostSource(
                GhostMapPresence.TrackingStationGhostSource.Segment));
            Assert.False(GhostMapPresence.IsStateVectorGhostSource(
                GhostMapPresence.TrackingStationGhostSource.EndpointTail));
        }

        // ---------- IsTerminalStateEligibleForMapPresence -----------------------

        [Fact]
        public void IsTerminalStateEligibleForMapPresence_NullAndFlightTerminalsOnly()
        {
            Assert.True(GhostMapPresence.IsTerminalStateEligibleForMapPresence(null));
            Assert.True(GhostMapPresence.IsTerminalStateEligibleForMapPresence(TerminalState.Orbiting));
            Assert.True(GhostMapPresence.IsTerminalStateEligibleForMapPresence(TerminalState.Docked));
            Assert.True(GhostMapPresence.IsTerminalStateEligibleForMapPresence(TerminalState.SubOrbital));
            Assert.False(GhostMapPresence.IsTerminalStateEligibleForMapPresence(TerminalState.Landed));
            Assert.False(GhostMapPresence.IsTerminalStateEligibleForMapPresence(TerminalState.Splashed));
            Assert.False(GhostMapPresence.IsTerminalStateEligibleForMapPresence(TerminalState.Destroyed));
            Assert.False(GhostMapPresence.IsTerminalStateEligibleForMapPresence(TerminalState.Recovered));
        }

        // ---------- IsTrackingStationRecordingMaterialized ----------------------

        [Fact]
        public void IsTrackingStationRecordingMaterialized_EachEvidenceSource()
        {
            Assert.False(GhostMapPresence.IsTrackingStationRecordingMaterialized(null, true));
            Assert.False(GhostMapPresence.IsTrackingStationRecordingMaterialized(new Recording(), true));
            Assert.True(GhostMapPresence.IsTrackingStationRecordingMaterialized(
                new Recording { VesselSpawned = true }, false));
            Assert.True(GhostMapPresence.IsTrackingStationRecordingMaterialized(
                new Recording { SpawnedVesselPersistentId = 42 }, false));
            // A recorded pid counts only when a real vessel for it exists.
            Assert.False(GhostMapPresence.IsTrackingStationRecordingMaterialized(
                new Recording { VesselPersistentId = 7 }, false));
            Assert.True(GhostMapPresence.IsTrackingStationRecordingMaterialized(
                new Recording { VesselPersistentId = 7 }, true));
        }

        // ---------- IsInAbsoluteFrame -------------------------------------------

        [Fact]
        public void IsInAbsoluteFrame_ReadsTheCoveringSectionFrame()
        {
            var rec = new Recording();
            Assert.False(GhostMapPresence.IsInAbsoluteFrame(null, 5.0));
            Assert.False(GhostMapPresence.IsInAbsoluteFrame(rec, 5.0));

            rec.TrackSections.Add(new TrackSection
            {
                startUT = 0.0, endUT = 10.0, referenceFrame = ReferenceFrame.Absolute
            });
            rec.TrackSections.Add(new TrackSection
            {
                startUT = 10.0, endUT = 20.0, referenceFrame = ReferenceFrame.Relative
            });

            Assert.True(GhostMapPresence.IsInAbsoluteFrame(rec, 5.0));
            // A shared boundary belongs to the later section.
            Assert.False(GhostMapPresence.IsInAbsoluteFrame(rec, 10.0));
            Assert.False(GhostMapPresence.IsInAbsoluteFrame(rec, 15.0));
            Assert.False(GhostMapPresence.IsInAbsoluteFrame(rec, 25.0));
        }

        // ---------- TryGetCommittedRecordingById ---------------------------------

        [Fact]
        public void TryGetCommittedRecordingById_FindsFirstOrdinalMatch()
        {
            var a = new Recording { RecordingId = "rec_a" };
            var b = new Recording { RecordingId = "rec_b" };
            var list = new List<Recording> { null, a, b };

            Assert.True(GhostMapPresence.TryGetCommittedRecordingById(list, "rec_b", out int idx, out Recording found));
            Assert.Equal(2, idx);
            Assert.Same(b, found);

            Assert.False(GhostMapPresence.TryGetCommittedRecordingById(list, "REC_B", out idx, out found));
            Assert.Equal(-1, idx);
            Assert.Null(found);
            Assert.False(GhostMapPresence.TryGetCommittedRecordingById(list, "", out _, out _));
            Assert.False(GhostMapPresence.TryGetCommittedRecordingById(null, "rec_a", out _, out _));
        }
    }
}
