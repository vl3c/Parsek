using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>What <see cref="HeldFinalOrbitComparison.Compare"/> concluded.</summary>
    internal enum HeldFinalOrbitVerdict
    {
        // Nothing the recording claims at this UT can be compared with stock's orbit (no live
        // orbit, before the prediction, past the final conic's horizon, unpropagatable).
        NotCompared,
        // Stock holds the vessel where the prediction says it is.
        Agrees,
        // Stock holds the vessel somewhere else (another body, another orbit, on the ground).
        Differs,
    }

    /// <summary>One comparison of a held predicted final orbit with stock's live orbit.</summary>
    internal struct HeldFinalOrbitResult
    {
        public HeldFinalOrbitVerdict Verdict;
        public string Reason;
        public double CompareUT;
        public double MissMeters;
        public double ToleranceMeters;
        public string PredictedBody;
        public string LiveBody;
        // The next UT at which the prediction changes conic (a predicted sphere change or the
        // end of the coast), plus the margin: worth comparing again then. +Infinity for none.
        public double NextCheckUT;
    }

    /// <summary>
    /// Owner ruling P-STOCK for a predicted final orbit (owner ruling Q8) whose vessel stock
    /// still holds: Parsek's prediction is a forecast, stock's on-rails orbit is the fact. Pure:
    /// compares the recording's predicted conic at <c>nowUT</c> with the live vessel's orbit,
    /// both propagated in their stored element frame (KSP-native elements, no Zup and no live
    /// frame read, the frame of <see cref="PredictedEndingTakeover.CoastFollowedPredictedConic"/>),
    /// with the live body's gravity.
    /// <para>The reference is the predicted segment that covers <c>nowUT</c>; past the last
    /// predicted segment it is the final conic propagated on, but only up to one period after
    /// its start for a closed conic (the predictor checked that span for sphere changes; an
    /// encounter after it is the vessel's own future, design edge case 26). An open final conic
    /// (around the Sun) has no such limit.</para>
    /// <para>Tolerance: on rails stock and Parsek move the vessel on the same drag-free conic,
    /// so a held orbit agrees to metres; the honest noise is the sphere-change timing (stock
    /// switches the body at a frame, Parsek at the computed crossing) and the physics seconds
    /// before stock packed the stage, about 1e-3 of the distance after a long heliocentric
    /// coast. <see cref="RelativeTolerance"/> (1%) keeps that noise and still catches what
    /// matters: another body, a burn, an encounter the predictor missed.</para>
    /// </summary>
    internal static class HeldFinalOrbitComparison
    {
        internal const double MinToleranceMeters = 1000.0;
        internal const double RelativeTolerance = 1e-2;

        /// <summary>Game seconds after a predicted conic change before it is worth a look.</summary>
        internal const double NextCheckMarginSeconds = 10.0;

        private const double UtEpsilon = 1e-6;

        /// <summary>
        /// Pure: the comparison at <paramref name="nowUT"/>. <paramref name="gravParameterOfBody"/>
        /// answers a body's gravitational parameter by name (NaN or null when unknown); it is read
        /// only to bound a closed final conic around a body other than the live one.
        /// </summary>
        internal static HeldFinalOrbitResult Compare(
            Recording rec,
            double nowUT,
            bool liveLandedOrSplashed,
            TakeoverCoastEvidence? liveOrbit,
            Func<string, double> gravParameterOfBody)
        {
            var result = new HeldFinalOrbitResult
            {
                Verdict = HeldFinalOrbitVerdict.NotCompared,
                CompareUT = nowUT,
                MissMeters = double.NaN,
                ToleranceMeters = double.NaN,
                NextCheckUT = double.PositiveInfinity,
            };
            if (rec == null || !IsFinite(nowUT))
                return NotCompared(ref result, "no-recording");
            if (liveLandedOrSplashed)
            {
                result.Verdict = HeldFinalOrbitVerdict.Differs;
                result.Reason = "stock-has-it-down";
                return result;
            }
            if (!liveOrbit.HasValue)
                return NotCompared(ref result, "no-live-orbit");
            TakeoverCoastEvidence live = liveOrbit.Value;
            result.LiveBody = live.LiveOrbit.bodyName;

            if (!TryFindReference(rec, nowUT, out OrbitSegment reference, out bool pastPrediction,
                    out double nextCheckUT, out string refusal))
                return NotCompared(ref result, refusal);
            result.PredictedBody = reference.bodyName;
            result.NextCheckUT = nextCheckUT;
            bool sameBody = string.Equals(reference.bodyName, live.LiveOrbit.bodyName, StringComparison.Ordinal);

            if (pastPrediction && !WithinFinalConicHorizon(
                    reference, nowUT, sameBody ? live.GravParameter : ReadGravParameter(gravParameterOfBody, reference.bodyName),
                    out string horizonRefusal))
                return NotCompared(ref result, horizonRefusal);

            if (!sameBody)
            {
                result.Verdict = HeldFinalOrbitVerdict.Differs;
                result.Reason = "body-differs";
                return result;
            }
            return CompareOnOneBody(ref result, reference, live, nowUT, pastPrediction);
        }

        /// <summary>
        /// Pure: the predicted segment that covers <paramref name="nowUT"/>, else the final conic
        /// (the predicted segment that ends last) when <paramref name="nowUT"/> is past it.
        /// <paramref name="nextCheckUT"/> is the next predicted segment end after it.
        /// </summary>
        private static bool TryFindReference(
            Recording rec, double nowUT, out OrbitSegment reference, out bool pastPrediction,
            out double nextCheckUT, out string refusal)
        {
            reference = default(OrbitSegment);
            pastPrediction = false;
            nextCheckUT = double.PositiveInfinity;
            refusal = null;
            List<OrbitSegment> segments = rec.OrbitSegments;
            bool found = false, any = false;
            OrbitSegment last = default(OrbitSegment);
            double firstStart = double.PositiveInfinity;
            for (int i = 0; segments != null && i < segments.Count; i++)
            {
                OrbitSegment seg = segments[i];
                if (!seg.isPredicted)
                    continue;
                if (!any || seg.endUT > last.endUT)
                    last = seg;
                any = true;
                firstStart = Math.Min(firstStart, seg.startUT);
                if (seg.endUT > nowUT + UtEpsilon)
                    nextCheckUT = Math.Min(nextCheckUT, seg.endUT + NextCheckMarginSeconds);
                if (!found && seg.startUT - UtEpsilon <= nowUT && nowUT <= seg.endUT + UtEpsilon)
                {
                    reference = seg;
                    found = true;
                }
            }
            if (found)
                return true;
            if (!any)
            {
                refusal = "no-predicted-segment";
                return false;
            }
            if (nowUT < firstStart)
            {
                refusal = "before-the-prediction";
                return false;
            }
            if (nowUT < last.endUT)
            {
                refusal = "between-predicted-segments";
                return false;
            }
            reference = last;
            pastPrediction = true;
            return true;
        }

        /// <summary>
        /// Pure: a closed final conic is compared only up to one period after its start (the span
        /// the predictor checked for sphere changes); an open one has no limit.
        /// </summary>
        private static bool WithinFinalConicHorizon(
            OrbitSegment finalConic, double nowUT, double gravParameter, out string refusal)
        {
            refusal = null;
            if (finalConic.eccentricity >= 1.0)
                return true;
            if (!(gravParameter > 0.0) || !IsFinite(gravParameter)
                || !BallisticExtrapolator.TwoBodyOrbit.TryCreateFromSegmentStoredFrame(
                    finalConic, gravParameter, out BallisticExtrapolator.TwoBodyOrbit orbit)
                || !IsFinite(orbit.Period) || !(orbit.Period > 0.0))
            {
                refusal = "final-conic-period-unknown";
                return false;
            }
            if (nowUT > finalConic.startUT + orbit.Period)
            {
                refusal = "past-the-final-conic-horizon";
                return false;
            }
            return true;
        }

        private static HeldFinalOrbitResult CompareOnOneBody(
            ref HeldFinalOrbitResult result, OrbitSegment reference, TakeoverCoastEvidence live,
            double nowUT, bool pastPrediction)
        {
            if (!(live.GravParameter > 0.0) || !IsFinite(live.GravParameter)
                || !BallisticExtrapolator.TwoBodyOrbit.TryCreateFromSegmentStoredFrame(
                    reference, live.GravParameter, out BallisticExtrapolator.TwoBodyOrbit predictedOrbit)
                || !BallisticExtrapolator.TwoBodyOrbit.TryCreateFromSegmentStoredFrame(
                    live.LiveOrbit, live.GravParameter, out BallisticExtrapolator.TwoBodyOrbit liveConic))
                return NotCompared(ref result, "orbit-not-propagatable");
            Vector3d predicted = predictedOrbit.GetPositionAtUT(nowUT);
            Vector3d actual = liveConic.GetPositionAtUT(nowUT);
            double miss = (predicted - actual).magnitude;
            double tolerance = Math.Max(MinToleranceMeters, RelativeTolerance * predicted.magnitude);
            if (!IsFinite(miss) || !IsFinite(tolerance))
                return NotCompared(ref result, "orbit-not-propagatable");
            result.MissMeters = miss;
            result.ToleranceMeters = tolerance;
            if (miss > tolerance)
            {
                result.Verdict = HeldFinalOrbitVerdict.Differs;
                result.Reason = pastPrediction ? "off-the-final-conic" : "off-the-predicted-coast";
                return result;
            }
            result.Verdict = HeldFinalOrbitVerdict.Agrees;
            result.Reason = pastPrediction ? "on-the-final-conic" : "on-the-predicted-coast";
            return result;
        }

        private static HeldFinalOrbitResult NotCompared(ref HeldFinalOrbitResult result, string reason)
        {
            result.Verdict = HeldFinalOrbitVerdict.NotCompared;
            result.Reason = reason;
            return result;
        }

        private static double ReadGravParameter(Func<string, double> lookup, string bodyName)
        {
            if (lookup == null || string.IsNullOrEmpty(bodyName))
                return double.NaN;
            return lookup(bodyName);
        }

        /// <summary>One log line's worth of the comparison.</summary>
        internal static string Format(HeldFinalOrbitResult r)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "verdict={0} reason={1} compareUT={2} predictedBody={3} liveBody={4} miss={5} tolerance={6}",
                r.Verdict,
                r.Reason ?? "(none)",
                FormatNumber(r.CompareUT, "F3"),
                r.PredictedBody ?? "(none)",
                r.LiveBody ?? "(none)",
                FormatNumber(r.MissMeters, "F1"),
                FormatNumber(r.ToleranceMeters, "F1"));
        }

        private static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        private static string FormatNumber(double v, string format)
        {
            return IsFinite(v) ? v.ToString(format, CultureInfo.InvariantCulture) : "NaN";
        }
    }
}
