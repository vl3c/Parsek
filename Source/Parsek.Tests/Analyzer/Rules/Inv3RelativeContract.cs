using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Tests.Analyzer.Rules
{
    // INV3 RELATIVE contract (design doc "The invariant rules", edge cases 5 + 6).
    //
    // The documented production hazard: in a ReferenceFrame.Relative TrackSection
    // the frames' latitude/longitude/altitude fields carry anchor-local METRE
    // offsets, NOT body-fixed lat/lon. Values routinely fall outside [-90,90] /
    // [-180,180]. A naive reader that range-checks (or, worse, resolves them
    // through body.GetWorldSurfacePosition) commits the exact bug this rule
    // exists to catch. Therefore INV3:
    //   - NEVER range-checks a RELATIVE section's frame lat/lon (metre offsets),
    //   - requires a RELATIVE section to carry an anchor: anchorRecordingId
    //     (non-loop) OR anchorVesselId != 0 (loop); neither -> FAIL,
    //   - range-checks only ABSOLUTE section frames, out-of-range -> WARN
    //     (INV3-ABSOLUTE-RANGE, not FAIL until KSP longitude normalization is
    //     cited),
    //   - skips OrbitalCheckpoint sections (no lat/lon frame surface).
    //
    // Pure over the model. Dispatches strictly on TrackSection.referenceFrame, the
    // same discriminator TrajectoryMath.ComputeRelativeLocalOffset keys on, so the
    // analyzer reads the offset contract, not the lat/lon reader.
    internal sealed class Inv3RelativeContract : IRecordingInvariant
    {
        internal const string RelativeRuleId = "INV3-RELATIVE-CONTRACT";
        internal const string AbsoluteRangeRuleId = "INV3-ABSOLUTE-RANGE";

        public string RuleId => RelativeRuleId;

        public string CitedContract =>
            "TrackSection.referenceFrame / TrajectoryMath.ComputeRelativeLocalOffset";

        public IEnumerable<Finding> Evaluate(AnalyzerModel model)
        {
            var findings = new List<Finding>();
            if (model?.Recordings == null)
                return findings;

            foreach (Recording rec in model.Recordings)
            {
                if (rec?.TrackSections == null)
                    continue;

                for (int i = 0; i < rec.TrackSections.Count; i++)
                {
                    TrackSection s = rec.TrackSections[i];
                    switch (s.referenceFrame)
                    {
                        case ReferenceFrame.Relative:
                            EvaluateRelative(rec, i, s, findings);
                            break;
                        case ReferenceFrame.Absolute:
                            EvaluateAbsolute(rec, i, s, findings);
                            break;
                        // OrbitalCheckpoint: no lat/lon frame surface; skip.
                    }
                }
            }

            return findings;
        }

        private static void EvaluateRelative(
            Recording rec, int sectionIndex, TrackSection s, List<Finding> findings)
        {
            bool hasRecordingAnchor = !string.IsNullOrEmpty(s.anchorRecordingId);
            bool hasVesselAnchor = s.anchorVesselId != 0;

            if (!hasRecordingAnchor && !hasVesselAnchor)
            {
                findings.Add(new Finding(
                    RelativeRuleId,
                    VerdictLevel.Fail,
                    rec.RecordingId,
                    sectionIndex,
                    Inv("INV3 relative recording={0}#{1} anchorRec=none anchorVessel=0 error=no-anchor",
                        rec.RecordingId, sectionIndex),
                    "TrackSection.referenceFrame"));
                return;
            }

            // Deliberately NO lat/lon range check here: the frame fields are
            // anchor-local metre offsets by contract. Emit a Verbose trace so a
            // reviewer can confirm the analyzer dispatched through the offset
            // contract rather than the lat/lon reader.
            string offsetSample = "none";
            if (s.frames != null && s.frames.Count > 0)
            {
                TrajectoryPoint f = s.frames[0];
                offsetSample = Inv("{0},{1},{2}", f.latitude, f.longitude, f.altitude);
            }
            ParsekLog.Verbose("Analyzer",
                Inv("INV3 relative recording={0}#{1} anchorRec={2} anchorVessel={3} offsetSample={4}",
                    rec.RecordingId, sectionIndex,
                    hasRecordingAnchor ? s.anchorRecordingId : "none",
                    s.anchorVesselId, offsetSample));
        }

        private static void EvaluateAbsolute(
            Recording rec, int sectionIndex, TrackSection s, List<Finding> findings)
        {
            if (s.frames == null)
                return;

            for (int i = 0; i < s.frames.Count; i++)
            {
                TrajectoryPoint f = s.frames[i];
                bool latBad = f.latitude < -90.0 || f.latitude > 90.0;
                bool lonBad = f.longitude < -180.0 || f.longitude > 180.0;
                if (latBad || lonBad)
                {
                    findings.Add(new Finding(
                        AbsoluteRangeRuleId,
                        VerdictLevel.Warn,
                        rec.RecordingId,
                        sectionIndex,
                        Inv("INV3 absolute-range recording={0}#{1} frame={2} lat={3} lon={4}",
                            rec.RecordingId, sectionIndex, i, f.latitude, f.longitude),
                        "TrackSection.referenceFrame"));
                    // One WARN per section is enough to flag the corrupt Absolute
                    // frame; keeps findings bounded.
                    return;
                }
            }
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
