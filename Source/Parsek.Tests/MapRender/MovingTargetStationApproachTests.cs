using System;
using Parsek;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-7 guard for <see cref="MovingTargetStationApproach"/> (migration plan §9 / design §9.2): the
    /// typed moving-target station rendezvous — a <see cref="HeliocentricTransferPhase"/> anchored to a
    /// LIVE station vessel (<see cref="AnchorFrame.LiveVesselAnchor"/>), joined by a
    /// <c>{ FlexibleSoi, G0 }</c> seam to an arrival-side station-period <see cref="HoldPhase"/>.
    /// DEFINE-ONLY — the composite is the fail-closed classification's payload, never a v1 geometry
    /// producer.
    ///
    /// Each assertion states the design contract it locks: the transfer arrival anchor is the live moving
    /// vessel (not a body center — the depot-double / dock-renders-absolute bug class); the seam is
    /// FlexibleSoi+G0 (no body SOI tangent at a moving target); the hold is a station-period HoldPhase (not
    /// an ArrivalLoiterPhase — there is no body to loiter around); the transfer provenance is
    /// FaithfulFallback in v1 (the synthetic moving-target producer is deferred).
    /// </summary>
    public class MovingTargetStationApproachTests
    {
        private static OrbitSegment TransferConic()
            => new OrbitSegment
            {
                startUT = 100.0, endUT = 500.0, bodyName = "Sun",
                semiMajorAxis = 13_000_000_000.0, eccentricity = 0.1,
            };

        private static MovingTargetStationApproach Build(Guid guid)
            => MovingTargetStationApproach.Build(
                transferId: new PhaseId("rec-station", 0, 0),
                holdId: new PhaseId("rec-station", 0, 1),
                stationLaunchGuid: guid,
                transferConic: TransferConic(),
                transferStartUt: 100.0, transferEndUt: 500.0,
                holdStartUt: 500.0, holdEndUt: 800.0);

        [Fact]
        public void Build_TransferArrivalAnchor_IsTheLiveStationVessel()
        {
            var guid = Guid.NewGuid();
            MovingTargetStationApproach approach = Build(guid);

            Assert.NotNull(approach.Transfer);
            Assert.IsType<AnchorFrame.LiveVesselAnchor>(approach.Transfer.Anchor);
            var live = (AnchorFrame.LiveVesselAnchor)approach.Transfer.Anchor;
            Assert.Equal(guid, live.LaunchGuid);
            Assert.Equal(guid, approach.StationLaunchGuid);
        }

        [Fact]
        public void Build_TransferProvenance_IsFaithfulFallback_InV1()
        {
            // v1: the synthetic moving-target producer is deferred, so the transfer is rendered faithfully
            // (FaithfulFallback), NOT Synthesized. A future producer would re-stamp it Synthesized.
            MovingTargetStationApproach approach = Build(Guid.NewGuid());
            Assert.Equal(SegmentProvenance.FaithfulFallback, approach.Transfer.Provenance);
            Assert.Equal(PhaseKind.HeliocentricTransfer, approach.Transfer.Kind);
        }

        [Fact]
        public void Build_ArrivalSeam_IsFlexibleSoiG0_OffCamera()
        {
            // design §9.2: the transfer arrival -> station hold seam is FlexibleSoi + G0 (position only —
            // there is no body SOI to enforce a tangent against at a moving target).
            MovingTargetStationApproach approach = Build(Guid.NewGuid());
            Assert.Equal(PhaseSeamKind.FlexibleSoi, approach.ArrivalSeam.Kind);
            Assert.Equal(ContinuityOrder.G0, approach.ArrivalSeam.Continuity);
            Assert.False(approach.ArrivalSeam.RequiresTangentMatch);
            Assert.Null(approach.ArrivalSeam.Crossing); // no body SOI crossing at a moving target
        }

        [Fact]
        public void Build_StationHold_IsHoldPhase_AnchoredToStation_NotALoiter()
        {
            // The station-period hold is a HoldPhase (renders quietly), NOT an ArrivalLoiterPhase — there is
            // no body to loiter around. It is anchored to the same live station vessel.
            var guid = Guid.NewGuid();
            MovingTargetStationApproach approach = Build(guid);

            Assert.NotNull(approach.StationHold);
            Assert.IsType<HoldPhase>(approach.StationHold);
            Assert.Equal(PhaseKind.Hold, approach.StationHold.Kind);
            Assert.Equal(Treatment.None, approach.StationHold.ResolveTreatment()); // a hold draws nothing
            Assert.IsType<AnchorFrame.LiveVesselAnchor>(approach.StationHold.Anchor);
            Assert.Equal(guid, ((AnchorFrame.LiveVesselAnchor)approach.StationHold.Anchor).LaunchGuid);
        }

        [Fact]
        public void Build_SeamsLinkTransferAndHold()
        {
            // The transfer's trailing seam and the hold's leading seam are the SAME arrival seam (the join).
            MovingTargetStationApproach approach = Build(Guid.NewGuid());
            Assert.Same(approach.ArrivalSeam, approach.Transfer.TrailingSeam);
            Assert.Same(approach.ArrivalSeam, approach.StationHold.LeadingSeam);
        }

        [Fact]
        public void Build_PhaseWindows_AreContiguous()
        {
            // The hold begins where the transfer ends (the rendezvous instant), so the assembled-chain
            // windows are contiguous.
            MovingTargetStationApproach approach = Build(Guid.NewGuid());
            Assert.Equal(approach.Transfer.EndUt, approach.StationHold.StartUt);
        }

        [Fact]
        public void SummaryToken_NamesStationGuidWindowsAndSeam()
        {
            var guid = Guid.NewGuid();
            string token = Build(guid).ToSummaryToken();
            Assert.Contains(guid.ToString(), token);
            Assert.Contains("flexible-soi", token);
            Assert.Contains("transferUT=", token);
            Assert.Contains("holdUT=", token);
        }
    }
}
