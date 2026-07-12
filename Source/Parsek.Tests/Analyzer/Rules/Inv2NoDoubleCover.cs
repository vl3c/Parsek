using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Tests.Analyzer.Rules
{
    // INV2 no double-cover (design doc "The invariant rules", edge cases 7 + 8).
    //
    // Within one recording, the TrackSection [startUT,endUT] spans partition the
    // recorded timeline into DISJOINT producer runs (RecordingOptimizer splits at
    // env/body boundaries so each producer owns a distinct UT run). Two sections
    // that overlap in INTERIOR UT mean an ambiguous playback position at the
    // overlapping UT -> FAIL. Gaps are LEGITIMATE (on-rails BG spans emit no
    // TrackSections, TrackSection.boundaryDiscontinuityMeters models seams); an
    // uncovered gap NOT bridged by an OrbitSegment is a soft WARN, never a FAIL.
    //
    // Pure over the loaded model: reuses the exact UT arithmetic the sampler and
    // optimizer assume, touches no file.
    internal sealed class Inv2NoDoubleCover : IRecordingInvariant
    {
        internal const string OverlapRuleId = "INV2-NO-DOUBLE-COVER";
        internal const string UncoveredRuleId = "INV2-UNCOVERED-SPAN";

        // Uncovered-span tolerance floor. Sections are built from sampled frames:
        // a section's startUT/endUT are its first/last frame UT. At a section
        // boundary the last frame of section A and the first frame of section B are
        // one sample apart, so a sub-sample-step gap between end(A) and start(B) is
        // a legitimate boundary seam, not a coverage hole. The recorder's coarsest
        // single sample step is ParsekSettings.GetMaxSampleInterval(SamplingDensity.Low)
        // = 8.0s (Medium 3.0s, High 1.0s); a gap at or below that is indistinguishable
        // from one sparse sample and must not WARN. Real coverage gaps in flown saves
        // are hundreds to millions of seconds (e.g. a stray far-future OrbitalCheckpoint
        // that leaves a ~1.5M-second hole), far above this floor, so they still WARN.
        // Chosen at the coarsest cadence (not per-section 1/sampleRateHz) so the floor
        // is density-agnostic; the huge gap between the observed micro-seams (<= 0.3s)
        // and the smallest real gap (hundreds of seconds) leaves ample margin.
        internal const double UncoveredSpanToleranceSeconds = 8.0;

        public string RuleId => OverlapRuleId;

        // The disjoint-producer contract (sections are non-overlapping producers,
        // gaps modeled by boundaryDiscontinuityMeters / the on-rails no-section
        // contract) that this rule checks.
        public string CitedContract =>
            "RecordingOptimizer.IsSplittableEnvOrBodyBoundary / TrackSection.boundaryDiscontinuityMeters";

        public IEnumerable<Finding> Evaluate(AnalyzerModel model)
        {
            var findings = new List<Finding>();
            if (model?.Recordings == null)
                return findings;

            foreach (Recording rec in model.Recordings)
            {
                if (rec?.TrackSections == null || rec.TrackSections.Count < 2)
                    continue;

                EvaluateRecording(rec, findings);
            }

            return findings;
        }

        private static void EvaluateRecording(Recording rec, List<Finding> findings)
        {
            // Pair each section with its original index so findings cite the real
            // section position, then order by span start (ties by end) so a single
            // running-max pass detects overlap AND containment.
            var ordered = new List<(int Index, double Start, double End)>(rec.TrackSections.Count);
            for (int i = 0; i < rec.TrackSections.Count; i++)
            {
                TrackSection s = rec.TrackSections[i];
                ordered.Add((i, s.startUT, s.endUT));
            }

            ordered.Sort((a, b) =>
            {
                int c = a.Start.CompareTo(b.Start);
                return c != 0 ? c : a.End.CompareTo(b.End);
            });

            List<(double Start, double End)> orbitCover = BuildMergedOrbitCoverage(rec);

            // Running-max sweep: coverEnd is the furthest UT covered so far, and
            // coverIdx/coverEnd's owning section is the one an overlap is reported
            // against.
            int coverIdx = ordered[0].Index;
            double coverStart = ordered[0].Start;
            double coverEnd = ordered[0].End;

            for (int k = 1; k < ordered.Count; k++)
            {
                (int Index, double Start, double End) s = ordered[k];

                if (s.Start < coverEnd)
                {
                    // Interior overlap: the new section starts before the covered
                    // run ends. Double-covered UT -> ambiguous playback -> FAIL.
                    findings.Add(new Finding(
                        OverlapRuleId,
                        VerdictLevel.Fail,
                        rec.RecordingId,
                        s.Index,
                        Inv("INV2 overlap recording={0} a=[{1},{2}] b=[{3},{4}]",
                            rec.RecordingId, coverStart, coverEnd, s.Start, s.End),
                        "RecordingOptimizer.IsSplittableEnvOrBodyBoundary"));
                }
                else if (s.Start - coverEnd > UncoveredSpanToleranceSeconds)
                {
                    // Genuine gap beyond one sample step. Legitimate unless nothing
                    // (section or orbit coast) bridges it -> soft WARN, never FAIL.
                    // Sub-tolerance gaps are section-boundary seams, not holes, and
                    // are suppressed by the guard condition above.
                    bool bridged = IntervalCovered(orbitCover, coverEnd, s.Start);
                    if (!bridged)
                    {
                        findings.Add(new Finding(
                            UncoveredRuleId,
                            VerdictLevel.Warn,
                            rec.RecordingId,
                            s.Index,
                            Inv("INV2 uncovered recording={0} span=[{1},{2}] orbitBridged=False",
                                rec.RecordingId, coverEnd, s.Start),
                            "TrackSection.boundaryDiscontinuityMeters"));
                    }
                }
                // else s.Start == coverEnd: sections touch exactly, no finding.

                if (s.End > coverEnd)
                {
                    coverEnd = s.End;
                    coverStart = s.Start;
                    coverIdx = s.Index;
                }
            }

            // Suppress the unused-warning without changing behavior; coverIdx is
            // retained for readability of the running-cover triple.
            _ = coverIdx;
        }

        /// <summary>
        /// Merges the recording's OrbitSegment spans into a sorted list of
        /// non-overlapping [start,end] intervals so a gap can be tested for
        /// coast-bridging coverage in one pass.
        /// </summary>
        private static List<(double Start, double End)> BuildMergedOrbitCoverage(Recording rec)
        {
            var merged = new List<(double Start, double End)>();
            if (rec.OrbitSegments == null || rec.OrbitSegments.Count == 0)
                return merged;

            var spans = new List<(double Start, double End)>(rec.OrbitSegments.Count);
            foreach (OrbitSegment seg in rec.OrbitSegments)
            {
                if (seg.endUT >= seg.startUT)
                    spans.Add((seg.startUT, seg.endUT));
            }
            if (spans.Count == 0)
                return merged;

            spans.Sort((a, b) => a.Start.CompareTo(b.Start));

            double curStart = spans[0].Start;
            double curEnd = spans[0].End;
            for (int i = 1; i < spans.Count; i++)
            {
                if (spans[i].Start <= curEnd)
                {
                    if (spans[i].End > curEnd)
                        curEnd = spans[i].End;
                }
                else
                {
                    merged.Add((curStart, curEnd));
                    curStart = spans[i].Start;
                    curEnd = spans[i].End;
                }
            }
            merged.Add((curStart, curEnd));
            return merged;
        }

        /// <summary>
        /// True when [gapStart,gapEnd] lies wholly inside one merged orbit interval.
        /// </summary>
        private static bool IntervalCovered(
            List<(double Start, double End)> merged, double gapStart, double gapEnd)
        {
            if (merged == null)
                return false;
            for (int i = 0; i < merged.Count; i++)
            {
                if (merged[i].Start <= gapStart && merged[i].End >= gapEnd)
                    return true;
            }
            return false;
        }

        private static string Inv(string format, params object[] args)
        {
            var ic = CultureInfo.InvariantCulture;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] is double d)
                    args[i] = d.ToString("R", ic);
            }
            return string.Format(ic, format, args);
        }
    }
}
