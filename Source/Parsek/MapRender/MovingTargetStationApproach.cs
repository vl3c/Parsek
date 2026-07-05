using System;
using System.Globalization;

namespace Parsek.MapRender
{
    /// <summary>
    /// Phase 7 (migration plan §9 / design §9.2 / §6): the typed model of a MOVING-TARGET station
    /// rendezvous — the least-supported phase, whose target is a moving LIVE vessel (a station), not a
    /// body center.
    ///
    /// <para>design §9.2 defines it as a composite of two first-class phases:
    /// <list type="bullet">
    ///   <item>a <see cref="HeliocentricTransferPhase"/> whose ARRIVAL
    ///     <see cref="AnchorFrame"/> is a <see cref="AnchorFrame.LiveVesselAnchor"/> (a moving vessel,
    ///     not a body center), joined by</item>
    ///   <item>a <see cref="PhaseSeam"/> <c>{ FlexibleSoi, G0 }</c> to an arrival-side
    ///     <see cref="HoldPhase"/> that hosts the STATION-PERIOD hold
    ///     (<c>ArrivalHoldPlanner</c>'s station-period branch — NOT an <see cref="ArrivalLoiterPhase"/>,
    ///     since there is no body to loiter around).</item>
    /// </list></para>
    ///
    /// <para><b>DEFINE-ONLY, v1 fail-closed-to-faithful (design §4 / §9.2).</b> v1 renders recorded
    /// approaches FAITHFULLY; the classifier returns unsupported (a free heliocentric station fails the
    /// direct-child-of-common-ancestor test) -> <see cref="SegmentProvenance.FaithfulFallback"/>. The
    /// SYNTHETIC moving-target producer is deferred (design §17). This type therefore BUILDS the two typed
    /// phases (so the moving-target shape has a real, test-assertable identity and a typed home for the
    /// future producer) but is never handed to the live draw spine in v1 — it is the fail-closed
    /// classification's payload, not a geometry producer.</para>
    ///
    /// <para><b>Pure + headless.</b> No Unity / KSP-API reads. The transfer conic is a value-copied
    /// <see cref="OrbitSegment"/> (carried for the future producer); the hold is a pure clock insertion.
    /// Building the composite never solves anything.</para>
    /// </summary>
    internal sealed class MovingTargetStationApproach
    {
        /// <summary>
        /// The heliocentric transfer leg whose arrival anchor is the LIVE station vessel
        /// (<see cref="AnchorFrame.LiveVesselAnchor"/>). Provenance is
        /// <see cref="SegmentProvenance.FaithfulFallback"/> in v1 (no synthetic moving-target solve).
        /// </summary>
        internal HeliocentricTransferPhase Transfer { get; }

        /// <summary>
        /// The arrival-side station-period <see cref="HoldPhase"/> (the <c>ArrivalHoldPlanner</c>
        /// station-period branch). Anchored to the same live station vessel; it renders quietly (held prior
        /// intent) but exists in the chain for debugging / composition.
        /// </summary>
        internal HoldPhase StationHold { get; }

        /// <summary>
        /// The <c>{ FlexibleSoi, G0 }</c> seam joining the transfer arrival to the station hold (design
        /// §9.2). G0 (position only): there is no body SOI to enforce a tangent against at a moving target.
        /// </summary>
        internal PhaseSeam ArrivalSeam { get; }

        /// <summary>The launch-unique guid (KSP <c>Vessel.id</c>) of the live station vessel the approach targets.</summary>
        internal Guid StationLaunchGuid { get; }

        internal MovingTargetStationApproach(
            HeliocentricTransferPhase transfer,
            HoldPhase stationHold,
            PhaseSeam arrivalSeam,
            Guid stationLaunchGuid)
        {
            Transfer = transfer;
            StationHold = stationHold;
            ArrivalSeam = arrivalSeam;
            StationLaunchGuid = stationLaunchGuid;
        }

        /// <summary>
        /// design §9.2: build the typed moving-target station-approach composite (the transfer leg
        /// anchored to the live station + the arrival station-period hold, joined by a FlexibleSoi G0
        /// seam). PURE — it builds the typed phases; it solves nothing and produces no synthetic geometry.
        /// In v1 the composite's transfer carries <see cref="SegmentProvenance.FaithfulFallback"/> (the
        /// synthetic moving-target producer is deferred); a future producer would re-stamp it
        /// <see cref="SegmentProvenance.Synthesized"/>.
        ///
        /// <para><paramref name="transferConic"/> is the recorded heliocentric leg (value-copied onto the
        /// phase); the <paramref name="holdStartUt"/>/<paramref name="holdEndUt"/> bracket the station hold
        /// in the assembled-chain clock.</para>
        /// </summary>
        internal static MovingTargetStationApproach Build(
            PhaseId transferId,
            PhaseId holdId,
            Guid stationLaunchGuid,
            OrbitSegment transferConic,
            double transferStartUt,
            double transferEndUt,
            double holdStartUt,
            double holdEndUt)
        {
            var liveAnchor = new AnchorFrame.LiveVesselAnchor(stationLaunchGuid);

            // The FlexibleSoi G0 seam joins the transfer arrival to the station hold (design §9.2). It
            // carries no SoiCrossing (there is no body SOI at a moving target), and is off-camera by
            // default (the seam at a moving rendezvous is not a distinguished on-screen kink).
            PhaseSeam arrivalSeam = PhaseSeam.FlexibleSoi(crossing: null, onCamera: false);

            var transfer = new HeliocentricTransferPhase(
                transferId,
                SegmentProvenance.FaithfulFallback,
                liveAnchor,
                transferStartUt,
                transferEndUt,
                transferConic,
                leadingSeam: null,
                trailingSeam: arrivalSeam);

            var stationHold = new HoldPhase(
                holdId,
                liveAnchor,
                holdStartUt,
                holdEndUt,
                leadingSeam: arrivalSeam,
                trailingSeam: null);

            return new MovingTargetStationApproach(transfer, stationHold, arrivalSeam, stationLaunchGuid);
        }

        /// <summary>
        /// Grep-stable summary token for the Tier-A <c>fail-closed-to-faithful</c> detail line (design
        /// §14). Pure; InvariantCulture.
        /// </summary>
        internal string ToSummaryToken()
            => string.Format(CultureInfo.InvariantCulture,
                "stationGuid={0} transferUT=[{1:F1},{2:F1}] holdUT=[{3:F1},{4:F1}] seam={5}",
                StationLaunchGuid, Transfer.StartUt, Transfer.EndUt,
                StationHold.StartUt, StationHold.EndUt,
                PhaseSeamClassifier.KindToken(ArrivalSeam.Kind));

        public override string ToString()
            => string.Format(CultureInfo.InvariantCulture,
                "MovingTargetStationApproach[{0}]", ToSummaryToken());
    }
}
