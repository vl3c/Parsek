using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Tests.Analyzer.Rules
{
    // INV1 UT monotonicity (design doc "The invariant rules", test plan INV1).
    //
    // TrajectoryMath's sampler binary-searches trajectory sequences by UT, which
    // requires non-decreasing UT. This rule asserts:
    //   - each TrackSection's frames / bodyFixedFrames UT sequence is non-decreasing,
    //   - each TrackSection's checkpoints startUT sequence is non-decreasing,
    //   - each TrackSection has startUT <= endUT,
    //   - the flat Recording.Points UT sequence is non-decreasing.
    //
    // EQUAL UT is allowed: the recorder appends a structural-event snapshot at the
    // exact UT of a dock/undock/EVA (TrajectoryPointFlags.StructuralEventSnapshot),
    // so two adjacent samples legitimately share a UT. Only a STRICT back-step
    // (uts[i] < uts[i-1]) is a violation -> FAIL. Pure over the model, no file.
    internal sealed class Inv1UtMonotonic : IRecordingInvariant
    {
        internal const string RuleIdConst = "INV1-UT-MONOTONIC";

        public string RuleId => RuleIdConst;

        public string CitedContract => "TrajectoryPoint.ut / TrackSection.startUT/endUT";

        public IEnumerable<Finding> Evaluate(AnalyzerModel model)
        {
            var findings = new List<Finding>();
            if (model?.Recordings == null)
                return findings;

            foreach (Recording rec in model.Recordings)
            {
                if (rec == null)
                    continue;

                CheckPointSequence(rec, "Points", -1, PointUts(rec.Points), findings);

                if (rec.TrackSections == null)
                    continue;

                for (int i = 0; i < rec.TrackSections.Count; i++)
                {
                    TrackSection s = rec.TrackSections[i];

                    // NaN section bound (double.IsNaN): a NaN startUT/endUT makes
                    // section dispatch and the > comparison ill-defined (NaN > x is
                    // always false, so the ordering check silently passes). Report
                    // it explicitly as one finding; the ordering check below is
                    // mutually exclusive because a NaN bound can never satisfy >.
                    if (double.IsNaN(s.startUT) || double.IsNaN(s.endUT))
                    {
                        findings.Add(new Finding(
                            RuleIdConst,
                            VerdictLevel.Fail,
                            rec.RecordingId,
                            i,
                            Inv("INV1 nan recording={0} seq=SectionSpan section={1} startUT={2} endUT={3}",
                                rec.RecordingId, i, s.startUT, s.endUT),
                            "TrackSection.startUT/endUT"));
                    }
                    else if (s.startUT > s.endUT)
                    {
                        findings.Add(new Finding(
                            RuleIdConst,
                            VerdictLevel.Fail,
                            rec.RecordingId,
                            i,
                            Inv("INV1 backstep recording={0} seq=SectionSpan section={1} startUT={2} endUT={3}",
                                rec.RecordingId, i, s.startUT, s.endUT),
                            "TrackSection.startUT/endUT"));
                    }

                    CheckPointSequence(rec, "frames", i, PointUts(s.frames), findings);
                    CheckPointSequence(rec, "bodyFixedFrames", i, PointUts(s.bodyFixedFrames), findings);
                    CheckPointSequence(rec, "checkpoints", i, CheckpointUts(s.checkpoints), findings);
                }
            }

            return findings;
        }

        private static void CheckPointSequence(
            Recording rec, string seqName, int sectionIndex, List<double> uts, List<Finding> findings)
        {
            if (uts == null)
                return;

            // NaN UT: a NaN sample UT breaks TrajectoryMath's binary-search sampler
            // (every comparison against NaN is false, so the sort/search silently
            // misbehaves) and never trips the strict back-step check below. Report
            // the first NaN per sequence, same bounding style as the back-step
            // reporting (one finding per sequence).
            for (int i = 0; i < uts.Count; i++)
            {
                if (double.IsNaN(uts[i]))
                {
                    findings.Add(new Finding(
                        RuleIdConst,
                        VerdictLevel.Fail,
                        rec.RecordingId,
                        sectionIndex,
                        Inv("INV1 nan recording={0} seq={1} section={2} at={3}",
                            rec.RecordingId, seqName, sectionIndex, i),
                        "TrajectoryPoint.ut"));
                    return;
                }
            }

            if (uts.Count < 2)
                return;

            for (int i = 1; i < uts.Count; i++)
            {
                if (uts[i] < uts[i - 1])
                {
                    findings.Add(new Finding(
                        RuleIdConst,
                        VerdictLevel.Fail,
                        rec.RecordingId,
                        sectionIndex,
                        Inv("INV1 backstep recording={0} seq={1} section={2} at={3} ut={4}->{5}",
                            rec.RecordingId, seqName, sectionIndex, i, uts[i - 1], uts[i]),
                        "TrajectoryPoint.ut"));
                    // Report the first back-step per sequence only; one is enough
                    // to flag the corrupt sidecar and keeps findings bounded.
                    return;
                }
            }
        }

        private static List<double> PointUts(List<TrajectoryPoint> pts)
        {
            if (pts == null)
                return null;
            var uts = new List<double>(pts.Count);
            foreach (TrajectoryPoint p in pts)
                uts.Add(p.ut);
            return uts;
        }

        private static List<double> CheckpointUts(List<OrbitSegment> segs)
        {
            if (segs == null)
                return null;
            var uts = new List<double>(segs.Count);
            foreach (OrbitSegment s in segs)
                uts.Add(s.startUT);
            return uts;
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
