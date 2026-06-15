using System;
using System.Collections.Generic;
using Parsek;

namespace Parsek.Tests.Generators
{
    /// <summary>
    /// PURE (no Unity) UT-tiling continuity harness for an assembled <see cref="OrbitSegment"/> list.
    /// This is the machine-checkable proxy for the reverted gap-between-orbit-segments regression
    /// (reaim-fix-plan.md guarantee 7): a re-aim assembler must NEVER return a segment list with a UT gap
    /// inside its synth span, because a gap is where the orbit ghost was destroyed and the line restarted
    /// displaced.
    ///
    /// <para>It works on the <see cref="OrbitSegment"/> value structs ONLY - startUT / endUT - and never
    /// calls Unity's <c>Orbit.getRelativePositionAtUT</c> (unavailable in xUnit). At each consecutive
    /// boundary it measures the UT discontinuity: <c>nextStartUT - prevEndUT</c>. A positive value is a
    /// GAP (dead time the ghost is not covered); a negative value is an OVERLAP (two segments claim the
    /// same UT). Both are reported so a test can assert exact contiguity (max |discontinuity| == 0) or a
    /// tolerance band.</para>
    /// </summary>
    internal static class SegmentTilingHarness
    {
        /// <summary>One inter-segment boundary's UT discontinuity.</summary>
        internal struct BoundaryMetric
        {
            /// <summary>Index of the earlier segment in the (sorted) list; the boundary is between
            /// <c>Index</c> and <c>Index + 1</c>.</summary>
            public int Index;
            /// <summary>The earlier segment's endUT.</summary>
            public double PrevEndUT;
            /// <summary>The later segment's startUT.</summary>
            public double NextStartUT;
            /// <summary><c>NextStartUT - PrevEndUT</c>. &gt; 0 = gap, &lt; 0 = overlap, 0 = contiguous.</summary>
            public double Discontinuity;
            public string PrevBody;
            public string NextBody;

            public bool IsGap => Discontinuity > 0.0;
            public bool IsOverlap => Discontinuity < 0.0;
        }

        /// <summary>Aggregate tiling metrics across all consecutive boundaries of a segment list.</summary>
        internal struct TilingReport
        {
            public IReadOnlyList<BoundaryMetric> Boundaries;
            /// <summary>Count of boundaries with a positive UT gap.</summary>
            public int GapCount;
            /// <summary>Count of boundaries with a negative UT overlap.</summary>
            public int OverlapCount;
            /// <summary>Largest gap (positive UT discontinuity), or 0 if none.</summary>
            public double MaxGapSeconds;
            /// <summary>Largest overlap magnitude (absolute value of the most negative discontinuity),
            /// or 0 if none.</summary>
            public double MaxOverlapSeconds;
            /// <summary>Largest absolute discontinuity at any boundary (gap or overlap).</summary>
            public double MaxAbsDiscontinuitySeconds;
        }

        /// <summary>
        /// Computes the per-boundary and aggregate UT-tiling metrics for <paramref name="segments"/>.
        /// The segments are scanned in their LIST ORDER (the assembler sorts by startUT before returning,
        /// so the caller passes the already-sorted assembler output). Pure: reads only startUT / endUT /
        /// bodyName and allocates a fresh report; never mutates the input.
        /// </summary>
        internal static TilingReport ComputeTiling(IReadOnlyList<OrbitSegment> segments)
        {
            var boundaries = new List<BoundaryMetric>();
            int gapCount = 0;
            int overlapCount = 0;
            double maxGap = 0.0;
            double maxOverlap = 0.0;
            double maxAbs = 0.0;

            if (segments != null)
            {
                for (int i = 0; i + 1 < segments.Count; i++)
                {
                    OrbitSegment prev = segments[i];
                    OrbitSegment next = segments[i + 1];
                    double disc = next.startUT - prev.endUT;

                    boundaries.Add(new BoundaryMetric
                    {
                        Index = i,
                        PrevEndUT = prev.endUT,
                        NextStartUT = next.startUT,
                        Discontinuity = disc,
                        PrevBody = prev.bodyName,
                        NextBody = next.bodyName,
                    });

                    if (disc > 0.0)
                    {
                        gapCount++;
                        if (disc > maxGap) maxGap = disc;
                    }
                    else if (disc < 0.0)
                    {
                        overlapCount++;
                        double mag = -disc;
                        if (mag > maxOverlap) maxOverlap = mag;
                    }

                    double abs = Math.Abs(disc);
                    if (abs > maxAbs) maxAbs = abs;
                }
            }

            return new TilingReport
            {
                Boundaries = boundaries,
                GapCount = gapCount,
                OverlapCount = overlapCount,
                MaxGapSeconds = maxGap,
                MaxOverlapSeconds = maxOverlap,
                MaxAbsDiscontinuitySeconds = maxAbs,
            };
        }
    }
}
