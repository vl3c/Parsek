using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Analyzer.Rules
{
    // INV11 payload-free TrackSection.
    //
    // A TrackSection that carries NO authored surface at all - no frames, no
    // bodyFixedFrames, no checkpoints - is an empty shell. Nothing can render or read
    // it, and its presence in a recording's section list actively DAMAGES the recording:
    // TrajectoryTextSidecarCodec.HasCompleteTrackSectionPayloadForFlatSync and
    // TryBuildBodyFixedPrimaryFlatPointsForRelativeSections both used to read one empty
    // shell as "this recording's section payload is incomplete" and give up on the whole
    // recording. With both defences off, a Relative section's frames - anchor-local
    // Cartesian METRES in latitude/longitude/altitude by contract - were persisted
    // verbatim as flat POINT lat/lon, and VesselSpawner.BackfillMaxDistance resolved them
    // through body.GetWorldSurfacePosition into maxDist ~735 km for a rover that never
    // left the pad area (todo UNDOCK-BG-CHILD-WRITES-RELATIVE-METRES-AS-FLAT-LAT-LON).
    //
    // The producers no longer emit one (TrackSectionCloseClassifier.Classify discards a
    // payload-free section at close in both recorders), so this rule is the durable
    // instrument: it names the shape on any save that still carries it, and it reds a
    // future producer that starts emitting them again.
    //
    // WARN, not FAIL, and the severity is a deliberate corpus decision rather than a
    // judgement about the shape. Two COMMITTED harness fixtures ship recordings with this
    // exact damage (rover-relay-recorded 49eaec92... / 0f391265..., rover-relay-c-recorded
    // 5c847692... / ec4bf428... / a597f168...), staged by lanes RVR-5 / RVR-6 / RVR-7. A
    // FAIL would red the analyzer verifier on every one of those lanes; the findings
    // cannot be baselined away either, because the harness verifier and the CI fixture
    // floor both run in BaselineMode.Forbid, where the mere PRESENCE of a baseline.cfg
    // beside a save is itself a FAIL. Nor can the rule be scoped to post-fix recordings:
    // the on-disk `sectionAuthoritative` flag would be a vacuous gate (a payload-free
    // section is exactly what makes a recording NON-section-authoritative), and inventing
    // a persisted marker for the purpose is schema churn the format contract forbids.
    // WARN reports the damage on those bytes without gating, which is exactly the state
    // the RVR-5 / RVR-6 spec headers describe today ("a green analyzer row on this lane
    // is NOT a clean bill for that defect") - except now the row is no longer silent.
    //
    // Pure over the model; reads the section lists only.
    internal sealed class Inv11EmptyTrackSection : IRecordingInvariant
    {
        internal const string EmptySectionRuleId = "INV11-EMPTY-SECTION";

        public string RuleId => EmptySectionRuleId;

        public string CitedContract =>
            "TrackSectionCloseClassifier.Classify / TrajectoryTextSidecarCodec.IsPayloadFreeTrackSection";

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
                    if (!IsPayloadFree(s))
                        continue;

                    findings.Add(new Finding(
                        EmptySectionRuleId,
                        VerdictLevel.Warn,
                        rec.RecordingId,
                        i,
                        Inv("INV11 empty-section recording={0}#{1} env={2} ref={3} ut=[{4},{5}] "
                            + "frames=0 bodyFixedFrames=0 checkpoints=0",
                            rec.RecordingId, i, s.environment, s.referenceFrame, s.startUT, s.endUT),
                        "TrackSectionCloseClassifier.Classify"));
                }
            }

            return findings;
        }

        /// <summary>
        /// The analyzer-side statement of
        /// <c>TrajectoryTextSidecarCodec.IsPayloadFreeTrackSection</c>. Kept here rather
        /// than calling into the codec so the rule stays pure over the model and reads
        /// as the contract it cites.
        /// </summary>
        private static bool IsPayloadFree(TrackSection s)
        {
            return (s.frames == null || s.frames.Count == 0)
                && (s.bodyFixedFrames == null || s.bodyFixedFrames.Count == 0)
                && (s.checkpoints == null || s.checkpoints.Count == 0);
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
