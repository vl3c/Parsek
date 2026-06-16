using System.Collections.Generic;
using Parsek;

namespace Parsek.Tests.Generators
{
    /// <summary>
    /// P0 golden-master fixtures for the re-aim whole-chain synthesis fix (reaim-fix-plan.md, the
    /// "CRITICAL SCOPE CORRECTION" seg#8..seg#21 table). These are pure-data <see cref="OrbitSegment"/>
    /// lists - NO Unity, NO recorded-file I/O - that reproduce the REAL Duna One transfer-member topology
    /// the chain synthesis ships for, instead of the 3-leg [parking, heliocentric, arrival] idealization
    /// the existing tests use. The values match the recording read from
    /// logs/2026-06-15_1906_duna-mission-investigation/KSP.log:13185-13198 (UT spans + sma signs).
    ///
    /// <para>The chain shape is: Kerbin ESCAPE HYPERBOLA (sma&lt;0) + THREE Sun heliocentric coasts +
    /// Duna CAPTURE HYPERBOLA (sma&lt;0, two fragments) + an IKE-SOI thread (two short Ike segs, the
    /// secondary-moon / Phase-4 case) + a Duna DESCENT (several ellipses). A leading circular Kerbin
    /// parking orbit is prepended so the escape leg is selected by index, not by the misnamed
    /// ParkingOrbit field. All segments tile contiguously (endUT == next startUT) so the continuity
    /// harness sees zero gaps in the recorded topology.</para>
    /// </summary>
    internal static class ReaimChainSynthesisFixtures
    {
        // common-ancestor body name for the Kerbin->Duna transfer.
        internal const string CommonAncestor = "Sun";
        internal const string LaunchBody = "Kerbin";
        internal const string TargetBody = "Duna";

        // Recorded transfer window: the heliocentric run spans the three Sun coasts (seg#9..#11).
        internal const double RecordedDepartureUT = 64044033.0;
        internal const double RecordedArrivalUT = 70898646.0;

        private static OrbitSegment Seg(
            string body, double start, double end, double sma, double ecc,
            double epoch = double.NaN, bool predicted = false)
        {
            return new OrbitSegment
            {
                bodyName = body,
                startUT = start,
                endUT = end,
                semiMajorAxis = sma,
                eccentricity = ecc,
                inclination = 5.0,
                longitudeOfAscendingNode = 30.0,
                argumentOfPeriapsis = 45.0,
                meanAnomalyAtEpoch = 0.5,
                epoch = double.IsNaN(epoch) ? start : epoch,
                isPredicted = predicted,
            };
        }

        /// <summary>
        /// The real 14-segment Duna One transfer member (seg#8..#21) with a leading circular Kerbin
        /// parking orbit prepended (so the escape hyperbola is an interior launch-body segment, not the
        /// first one). Contiguous UT tiling, no gaps. The list is freshly allocated on each call so a
        /// caller can never mutate shared fixture state.
        /// </summary>
        internal static List<OrbitSegment> BuildDunaOneTransferMember()
        {
            return new List<OrbitSegment>
            {
                // Leading circular Kerbin parking orbit (kept verbatim; the escape leg is selected by index).
                Seg("Kerbin", 63900000.0, 63966986.0, sma: 700000.0, ecc: 0.0),

                // seg#8  Kerbin ESCAPE HYPERBOLA (sma<0, ecc>1).
                Seg("Kerbin", 63966986.0, 64044033.0, sma: -3818300.0, ecc: 1.19),

                // seg#9/10/11 Sun heliocentric coasts (collapse into one re-aimed arc today).
                Seg("Sun", 64044033.0, 65004887.0, sma: 17604964390.0, ecc: 0.21),
                Seg("Sun", 65004937.0, 67901773.0, sma: 17604964390.0, ecc: 0.21),
                Seg("Sun", 67901797.0, 70898646.0, sma: 17604191980.0, ecc: 0.21),

                // seg#12/13 Duna CAPTURE HYPERBOLA (sma<0, ecc>1; two fragments).
                Seg("Duna", 70898646.0, 70912684.0, sma: -563351.0, ecc: 1.05),
                Seg("Duna", 70912757.0, 70956143.0, sma: -563351.0, ecc: 1.05),

                // seg#14/15 IKE-SOI thread (secondary moon - the Phase-4 multi-moon case).
                Seg("Ike", 70956143.0, 70956471.0, sma: -29873.0, ecc: 1.02),
                Seg("Ike", 70956481.0, 70958361.0, sma: -29873.0, ecc: 1.02),

                // seg#16/17 Duna re-capture after the Ike flyby.
                Seg("Duna", 70958361.0, 70958731.0, sma: -536838.0, ecc: 1.04),
                Seg("Duna", 70958733.0, 70960611.0, sma: -536838.0, ecc: 1.04),

                // seg#18-21 Duna DESCENT ellipses (aerobrake / land).
                Seg("Duna", 70960696.0, 70962487.0, sma: 492310.0, ecc: 0.45),
                Seg("Duna", 70962516.0, 70963373.0, sma: 379874.0, ecc: 0.30),
                Seg("Duna", 70963396.0, 70963441.0, sma: 340660.0, ecc: 0.10),
                Seg("Duna", 70963444.0, 70963653.0, sma: 340660.0, ecc: 0.10),
            };
        }

        /// <summary>
        /// A representative re-aimed heliocentric transfer segment (per-window orientation + epoch; the
        /// live resolver builds this from the Lambert solve and shifts it into recorded-span time before
        /// the assembler runs). startUT/endUT are zeroed - <see cref="OrbitSegment"/> values the assembler
        /// re-stamps to the render span. The sma is the re-aimed arc (distinct from the recorded coasts'
        /// 1.76e10) so a test can tell the replacement happened.
        /// </summary>
        internal static OrbitSegment BuildReaimedTransferSegment()
        {
            return Seg("Sun", 0.0, 0.0, sma: 18200000000.0, ecc: 0.23, epoch: 0.0);
        }
    }
}
