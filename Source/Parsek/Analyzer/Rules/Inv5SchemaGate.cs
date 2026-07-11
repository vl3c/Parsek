using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Analyzer.Rules
{
    // INV5 schema gate (design doc "The invariant rules" INV5, edge cases 1-4, 20, 22).
    //
    // Pure over the model. The loader (SaveDirectoryLoader, tasks 1.1/1.2) has
    // already done all file I/O: it parsed each RECORDING's metadata generation
    // via RecordingTreeRecordCodec.LoadRecordingFrom, captured each trajectory
    // sidecar's probed (generation, formatVersion) into model.SidecarSchema
    // (correction C2), and recorded every unloadable sidecar as a
    // LoadFault{FileKind="trajectory"} carrying the exact production reason
    // string. INV5 reads only that already-loaded model and emits:
    //
    //   - metadata gate FAIL: a recording whose (formatVersion, schemaGeneration)
    //     fails RecordingStore.IsRecordingSchemaCompatible, with the exact reason
    //     (generation-missing / generation-older / generation-newer /
    //     format-version-mismatch). Re-running the production gate reproduces the
    //     reason even after LoadRecordingFrom parks a rejected recording at
    //     RecordingFormatVersion == -1, because the gate checks the generation
    //     first (preserved) and a -1 format still yields format-version-mismatch.
    //   - generation-mismatch FAIL: metadata passes the gate but the paired
    //     trajectory sidecar reported a different generation (edge case 4).
    //   - trajectory LoadFault FAIL: truncated / text-format / pre-reset-magic
    //     sidecars (edge cases 1-3), surfaced with the production reason string so
    //     a triage grep matches KSP.log.
    //   - orphan-sidecar WARN: a SidecarSchema key with no matching recording is a
    //     stray .prec the tree no longer references (edge case 20).
    //   - generations inventory INFO: emitted only when informative (more than the
    //     single current generation is present), so a homogeneous current-gen save
    //     stays finding-free (the established clean-data-is-green convention the
    //     Phase-2 rules and InvariantRegistryTests pin).
    internal sealed class Inv5SchemaGate : IRecordingInvariant
    {
        internal const string SchemaGateRuleId = "INV5-SCHEMA-GATE";
        internal const string OrphanSidecarRuleId = "INV5-ORPHAN-SIDECAR";
        internal const string GenerationsRuleId = "INV5-GENERATIONS";

        public string RuleId => SchemaGateRuleId;

        public string CitedContract =>
            "RecordingStore.IsRecordingSchemaCompatible / RecordingStore.CurrentRecordingSchemaGeneration";

        public IEnumerable<Finding> Evaluate(AnalyzerModel model)
        {
            var findings = new List<Finding>();
            if (model == null)
                return findings;

            var recIds = new HashSet<string>(System.StringComparer.Ordinal);
            var faultedTrajectoryIds = new HashSet<string>(System.StringComparer.Ordinal);
            if (model.LoadFaults != null)
            {
                foreach (LoadFault f in model.LoadFaults)
                {
                    if (f.FileKind == "trajectory" && !string.IsNullOrEmpty(f.RecordingId))
                        faultedTrajectoryIds.Add(f.RecordingId);
                }
            }

            var distinctGenerations = new HashSet<int>();

            if (model.Recordings != null)
            {
                foreach (Recording rec in model.Recordings)
                {
                    if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                        continue;
                    recIds.Add(rec.RecordingId);
                    distinctGenerations.Add(rec.RecordingSchemaGeneration);

                    int sidecarGen = -1;
                    bool hasSidecar =
                        model.SidecarSchema != null
                        && model.SidecarSchema.TryGetValue(rec.RecordingId, out var s);
                    if (hasSidecar)
                        sidecarGen = model.SidecarSchema[rec.RecordingId].Generation;

                    if (!RecordingStore.IsRecordingSchemaCompatible(
                            rec.RecordingFormatVersion,
                            rec.RecordingSchemaGeneration,
                            out string reason))
                    {
                        findings.Add(new Finding(
                            SchemaGateRuleId,
                            VerdictLevel.Fail,
                            rec.RecordingId,
                            -1,
                            Inv("INV5 reject recording={0} reason={1} metadataGen={2} sidecarGen={3}",
                                rec.RecordingId, reason, rec.RecordingSchemaGeneration, sidecarGen),
                            "RecordingStore.IsRecordingSchemaCompatible"));
                        continue;
                    }

                    // Metadata passed the gate: flag a sidecar whose probed generation
                    // disagrees. Skipped when the sidecar itself failed to load (a
                    // trajectory LoadFault already reports the sidecar problem below).
                    if (hasSidecar
                        && sidecarGen != rec.RecordingSchemaGeneration
                        && !faultedTrajectoryIds.Contains(rec.RecordingId))
                    {
                        findings.Add(new Finding(
                            SchemaGateRuleId,
                            VerdictLevel.Fail,
                            rec.RecordingId,
                            -1,
                            Inv("INV5 reject recording={0} reason=generation-mismatch metadataGen={1} sidecarGen={2}",
                                rec.RecordingId, rec.RecordingSchemaGeneration, sidecarGen),
                            "RecordingStore.IsRecordingSchemaCompatible"));
                    }
                }
            }

            // Unloadable trajectory sidecars (truncated / text / pre-reset magic).
            if (model.LoadFaults != null)
            {
                foreach (LoadFault f in model.LoadFaults)
                {
                    if (f.FileKind != "trajectory")
                        continue;
                    findings.Add(new Finding(
                        SchemaGateRuleId,
                        VerdictLevel.Fail,
                        f.RecordingId ?? "<trajectory>",
                        -1,
                        Inv("INV5 reject recording={0} reason={1} kind=load-fault",
                            f.RecordingId ?? "<trajectory>", f.Reason ?? "unknown"),
                        "RecordingStore.IsRecordingSchemaCompatible"));
                }
            }

            // Orphan sidecars: a SidecarSchema key with no matching tree recording.
            if (model.SidecarSchema != null)
            {
                foreach (KeyValuePair<string, (int Generation, int FormatVersion)> kv in model.SidecarSchema)
                {
                    if (recIds.Contains(kv.Key))
                        continue;
                    findings.Add(new Finding(
                        OrphanSidecarRuleId,
                        VerdictLevel.Warn,
                        kv.Key,
                        -1,
                        Inv("INV5 orphan-sidecar recording={0} sidecarGen={1}", kv.Key, kv.Value.Generation),
                        "RecordingStore.IsRecordingSchemaCompatible"));
                }
            }

            // Generations inventory, informative only (a homogeneous current-gen
            // save stays finding-free).
            if (distinctGenerations.Count > 0
                && (distinctGenerations.Count > 1
                    || !distinctGenerations.Contains(RecordingStore.CurrentRecordingSchemaGeneration)))
            {
                var sorted = new List<int>(distinctGenerations);
                sorted.Sort();
                findings.Add(new Finding(
                    GenerationsRuleId,
                    VerdictLevel.Info,
                    model.SaveName ?? "<save>",
                    -1,
                    Inv("INV5 generations={0} current={1}",
                        string.Join(",", sorted.ConvertAll(g => g.ToString(CultureInfo.InvariantCulture))),
                        RecordingStore.CurrentRecordingSchemaGeneration),
                    "RecordingStore.CurrentRecordingSchemaGeneration"));
            }

            return findings;
        }

        private static string Inv(string format, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }
    }
}
