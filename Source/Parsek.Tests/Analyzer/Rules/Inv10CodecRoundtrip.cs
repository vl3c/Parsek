using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Parsek;

namespace Parsek.Tests.Analyzer.Rules
{
    // INV10 codec round-trips (design doc "The invariant rules" INV10, edge
    // case 25).
    //
    // For each loaded recording, re-serialize and re-deserialize at the CODEC
    // seams and assert structural equality, so a codec that silently drops a field
    // on save or load is caught:
    //   - RecordingTreeRecordCodec.SaveRecordingInto -> LoadRecordingFrom
    //     (in-memory ConfigNode; run on a DeepClone so the model recording is not
    //     mutated by SaveRecordingInto's generation stamp);
    //   - RecordingManifestCodec serialize/deserialize (in-memory ConfigNode);
    //   - TrajectorySidecarBinary Write/Read (to a scratch temp file, NEVER the
    //     analyzed save).
    // A non-round-tripping field -> FAIL naming the field. Comparison is NaN-aware
    // (a stable NaN round-trips equal, edge case 25) and null/empty-string aware.
    // Any codec exception is contained as a WARN, never propagated, so the
    // core-purity contract (no rule throws over an in-memory model) holds.
    internal sealed class Inv10CodecRoundtrip : IRecordingInvariant
    {
        internal const string RuleIdConst = "INV10-CODEC-ROUNDTRIP";

        public string RuleId => RuleIdConst;

        public string CitedContract =>
            "RecordingTreeRecordCodec.SaveRecordingInto / RecordingManifestCodec / TrajectorySidecarBinary";

        public IEnumerable<Finding> Evaluate(AnalyzerModel model)
        {
            var findings = new List<Finding>();
            if (model?.Recordings == null)
                return findings;

            foreach (Recording rec in model.Recordings)
            {
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;

                TreeRecordRoundTrip(rec, findings);
                ManifestRoundTrip(rec, findings);
                TrajectoryRoundTrip(rec, findings);
            }

            return findings;
        }

        // --- RecordingTreeRecordCodec ---

        private static void TreeRecordRoundTrip(Recording rec, List<Finding> findings)
        {
            try
            {
                // SaveRecordingInto stamps the current generation onto its argument;
                // operate on a clone so the model recording is untouched.
                Recording clone = Recording.DeepClone(rec);
                var node = new ConfigNode("RECORDING");
                RecordingTreeRecordCodec.SaveRecordingInto(node, clone);
                var rec2 = new Recording();
                RecordingTreeRecordCodec.LoadRecordingFrom(node, rec2);

                CompareString(rec.RecordingId, "RecordingId", rec.RecordingId, rec2.RecordingId, findings);
                CompareString(rec.RecordingId, "ChainId", rec.ChainId, rec2.ChainId, findings);
                CompareString(rec.RecordingId, "ParentRecordingId", rec.ParentRecordingId, rec2.ParentRecordingId, findings);
                CompareString(rec.RecordingId, "ParentAnchorRecordingId", rec.ParentAnchorRecordingId, rec2.ParentAnchorRecordingId, findings);
                CompareString(rec.RecordingId, "SupersedeTargetId", rec.SupersedeTargetId, rec2.SupersedeTargetId, findings);
                CompareInt(rec.RecordingId, "ChainIndex", rec.ChainIndex, rec2.ChainIndex, findings);
                CompareInt(rec.RecordingId, "ChainBranch", rec.ChainBranch, rec2.ChainBranch, findings);
            }
            catch (Exception ex)
            {
                findings.Add(Warn(rec.RecordingId,
                    Inv("INV10 codec-threw recording={0} seam=tree-record ex={1}", rec.RecordingId, ex.GetType().Name)));
            }
        }

        // --- RecordingManifestCodec ---

        private static void ManifestRoundTrip(Recording rec, List<Finding> findings)
        {
            bool hasStart = rec.StartResources != null && rec.StartResources.Count > 0;
            bool hasEnd = rec.EndResources != null && rec.EndResources.Count > 0;
            if (!hasStart && !hasEnd)
                return;

            try
            {
                var scratch = new ConfigNode("SCRATCH");
                RecordingManifestCodec.SerializeResourceManifest(scratch, rec);
                var rec2 = new Recording { RecordingId = rec.RecordingId };
                RecordingManifestCodec.DeserializeResourceManifest(scratch, rec2);

                CompareManifest(rec.RecordingId, "StartResources", rec.StartResources, rec2.StartResources, findings);
                CompareManifest(rec.RecordingId, "EndResources", rec.EndResources, rec2.EndResources, findings);
            }
            catch (Exception ex)
            {
                findings.Add(Warn(rec.RecordingId,
                    Inv("INV10 codec-threw recording={0} seam=manifest ex={1}", rec.RecordingId, ex.GetType().Name)));
            }
        }

        // --- TrajectorySidecarBinary ---

        private static void TrajectoryRoundTrip(Recording rec, List<Finding> findings)
        {
            string tmp = Path.Combine(Path.GetTempPath(),
                "parsek-inv10-" + Guid.NewGuid().ToString("N") + ".prec");
            try
            {
                TrajectorySidecarBinary.Write(tmp, rec, sidecarEpoch: 1);
                if (!TrajectorySidecarBinary.TryProbe(tmp, out TrajectorySidecarProbe probe) || !probe.Supported)
                {
                    findings.Add(Warn(rec.RecordingId,
                        Inv("INV10 codec-threw recording={0} seam=trajectory-probe reason={1}",
                            rec.RecordingId, probe.FailureReason ?? "unsupported")));
                    return;
                }
                var rec2 = new Recording();
                TrajectorySidecarBinary.Read(tmp, rec2, probe);

                ComparePoints(rec.RecordingId, "Points", rec.Points, rec2.Points, findings);
                CompareOrbitSegments(rec.RecordingId, rec.OrbitSegments, rec2.OrbitSegments, findings);
                ComparePartEvents(rec.RecordingId, rec.PartEvents, rec2.PartEvents, findings);
            }
            catch (Exception ex)
            {
                findings.Add(Warn(rec.RecordingId,
                    Inv("INV10 codec-threw recording={0} seam=trajectory ex={1}", rec.RecordingId, ex.GetType().Name)));
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        // --- comparison helpers ---

        private static void ComparePoints(
            string recId, string field, List<TrajectoryPoint> a, List<TrajectoryPoint> b, List<Finding> findings)
        {
            int ca = a?.Count ?? 0;
            int cb = b?.Count ?? 0;
            if (ca != cb)
            {
                findings.Add(Fail(recId, Inv("INV10 roundtrip recording={0} field={1} error=count a={2} b={3}", recId, field, ca, cb)));
                return;
            }
            for (int i = 0; i < ca; i++)
            {
                TrajectoryPoint pa = a[i];
                TrajectoryPoint pb = b[i];
                if (!DoubleEqual(pa.ut, pb.ut)
                    || !DoubleEqual(pa.latitude, pb.latitude)
                    || !DoubleEqual(pa.longitude, pb.longitude)
                    || !DoubleEqual(pa.altitude, pb.altitude))
                {
                    findings.Add(Fail(recId, Inv("INV10 roundtrip recording={0} field={1} error=point-drift at={2}", recId, field, i)));
                    return;
                }
            }
        }

        private static void CompareOrbitSegments(
            string recId, List<OrbitSegment> a, List<OrbitSegment> b, List<Finding> findings)
        {
            int ca = a?.Count ?? 0;
            int cb = b?.Count ?? 0;
            if (ca != cb)
            {
                findings.Add(Fail(recId, Inv("INV10 roundtrip recording={0} field=OrbitSegments error=count a={1} b={2}", recId, ca, cb)));
                return;
            }
            for (int i = 0; i < ca; i++)
            {
                OrbitSegment sa = a[i];
                OrbitSegment sb = b[i];
                if (!DoubleEqual(sa.startUT, sb.startUT)
                    || !DoubleEqual(sa.endUT, sb.endUT)
                    || !DoubleEqual(sa.semiMajorAxis, sb.semiMajorAxis)
                    || !DoubleEqual(sa.eccentricity, sb.eccentricity)
                    || !DoubleEqual(sa.inclination, sb.inclination))
                {
                    findings.Add(Fail(recId, Inv("INV10 roundtrip recording={0} field=OrbitSegments error=segment-drift at={1}", recId, i)));
                    return;
                }
            }
        }

        private static void ComparePartEvents(
            string recId, List<PartEvent> a, List<PartEvent> b, List<Finding> findings)
        {
            int ca = a?.Count ?? 0;
            int cb = b?.Count ?? 0;
            if (ca != cb)
            {
                findings.Add(Fail(recId, Inv("INV10 roundtrip recording={0} field=PartEvents error=count a={1} b={2}", recId, ca, cb)));
                return;
            }
            for (int i = 0; i < ca; i++)
            {
                if (a[i].partPersistentId != b[i].partPersistentId
                    || a[i].eventType != b[i].eventType
                    || !DoubleEqual(a[i].ut, b[i].ut))
                {
                    findings.Add(Fail(recId, Inv("INV10 roundtrip recording={0} field=PartEvents error=event-drift at={1}", recId, i)));
                    return;
                }
            }
        }

        private static void CompareManifest(
            string recId, string field,
            Dictionary<string, ResourceAmount> a, Dictionary<string, ResourceAmount> b, List<Finding> findings)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (a != null) foreach (string k in a.Keys) keys.Add(k);
            if (b != null) foreach (string k in b.Keys) keys.Add(k);

            foreach (string key in keys)
            {
                ResourceAmount ra = default(ResourceAmount);
                ResourceAmount rb = default(ResourceAmount);
                bool inA = a != null && a.TryGetValue(key, out ra);
                bool inB = b != null && b.TryGetValue(key, out rb);
                if (inA != inB || !DoubleEqual(ra.amount, rb.amount) || !DoubleEqual(ra.maxAmount, rb.maxAmount))
                {
                    findings.Add(Fail(recId,
                        Inv("INV10 roundtrip recording={0} field={1} error=resource-drift resource='{2}'", recId, field, key)));
                }
            }
        }

        private static void CompareString(string recId, string field, string a, string b, List<Finding> findings)
        {
            if (!string.Equals(a ?? "", b ?? "", StringComparison.Ordinal))
                findings.Add(Fail(recId, Inv("INV10 roundtrip recording={0} field={1} error=drift a='{2}' b='{3}'", recId, field, a ?? "", b ?? "")));
        }

        private static void CompareInt(string recId, string field, int a, int b, List<Finding> findings)
        {
            if (a != b)
                findings.Add(Fail(recId, Inv("INV10 roundtrip recording={0} field={1} error=drift a={2} b={3}", recId, field, a, b)));
        }

        private static Finding Fail(string recId, string message) =>
            new Finding(RuleIdConst, VerdictLevel.Fail, recId, -1, message, "RecordingTreeRecordCodec.SaveRecordingInto");

        private static Finding Warn(string recId, string message) =>
            new Finding(RuleIdConst, VerdictLevel.Warn, recId, -1, message, "RecordingTreeRecordCodec.SaveRecordingInto");

        private static bool DoubleEqual(double x, double y)
        {
            if (double.IsNaN(x) && double.IsNaN(y))
                return true;
            return x.Equals(y);
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
