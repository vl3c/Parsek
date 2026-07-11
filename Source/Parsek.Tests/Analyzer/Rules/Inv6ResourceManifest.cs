using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek;

namespace Parsek.Tests.Analyzer.Rules
{
    // INV6 resource manifest consistency (design doc "The invariant rules" INV6,
    // edge case 9).
    //
    // Where a resource manifest is present, it must round-trip through
    // RecordingManifestCodec.SerializeResourceManifest / DeserializeResourceManifest
    // (into a scratch in-memory ConfigNode, no file I/O) and its per-resource
    // deltas (ResourceManifest.ComputeResourceDelta) must survive the round-trip
    // unchanged. A resource that changes or vanishes across the round-trip -> FAIL
    // naming the resource.
    //
    // Manifests are OPTIONAL: a recording with no manifest (a Gloops / showcase
    // ghost, or any recording that carried none) produces NO finding. The design
    // describes the absent case as "INFO, not a finding"; this rule takes the
    // no-finding reading so a healthy save with manifest-less recordings stays
    // finding-free (the clean-data-is-green convention the Phase-2 rules and
    // InvariantRegistryTests pin). Pure over the model.
    internal sealed class Inv6ResourceManifest : IRecordingInvariant
    {
        internal const string RuleIdConst = "INV6-RESOURCE-MANIFEST";

        public string RuleId => RuleIdConst;

        public string CitedContract =>
            "RecordingManifestCodec.SerializeResourceManifest / ResourceManifest.ComputeResourceDelta";

        public IEnumerable<Finding> Evaluate(AnalyzerModel model)
        {
            var findings = new List<Finding>();
            if (model?.Recordings == null)
                return findings;

            foreach (Recording rec in model.Recordings)
            {
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;

                bool hasStart = rec.StartResources != null && rec.StartResources.Count > 0;
                bool hasEnd = rec.EndResources != null && rec.EndResources.Count > 0;
                if (!hasStart && !hasEnd)
                    continue; // manifest optional -> no finding

                // Round-trip the manifest through the codec into a scratch node.
                var scratch = new ConfigNode("SCRATCH");
                RecordingManifestCodec.SerializeResourceManifest(scratch, rec);
                var rec2 = new Recording { RecordingId = rec.RecordingId };
                RecordingManifestCodec.DeserializeResourceManifest(scratch, rec2);

                CompareDictionaries(rec.RecordingId, "start",
                    rec.StartResources, rec2.StartResources, findings);
                CompareDictionaries(rec.RecordingId, "end",
                    rec.EndResources, rec2.EndResources, findings);

                // Delta consistency: the per-resource delta must be identical before
                // and after the round-trip (ComputeResourceDelta is the production
                // consumer of the manifest).
                Dictionary<string, double> before =
                    ResourceManifest.ComputeResourceDelta(rec.StartResources, rec.EndResources);
                Dictionary<string, double> after =
                    ResourceManifest.ComputeResourceDelta(rec2.StartResources, rec2.EndResources);
                CompareDeltas(rec.RecordingId, before, after, findings);
            }

            return findings;
        }

        private static void CompareDictionaries(
            string recId, string which,
            Dictionary<string, ResourceAmount> a,
            Dictionary<string, ResourceAmount> b,
            List<Finding> findings)
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

                if (inA != inB)
                {
                    findings.Add(Fail(recId,
                        Inv("INV6 manifest recording={0} field={1} resource='{2}' error=present-mismatch inA={3} inB={4}",
                            recId, which, key, inA ? "True" : "False", inB ? "True" : "False")));
                    continue;
                }
                if (!inA)
                    continue;

                if (!DoubleEqual(ra.amount, rb.amount) || !DoubleEqual(ra.maxAmount, rb.maxAmount))
                {
                    findings.Add(Fail(recId,
                        Inv("INV6 manifest recording={0} field={1} resource='{2}' error=value-drift a={3}/{4} b={5}/{6}",
                            recId, which, key, ra.amount, ra.maxAmount, rb.amount, rb.maxAmount)));
                }
            }
        }

        private static void CompareDeltas(
            string recId,
            Dictionary<string, double> before,
            Dictionary<string, double> after,
            List<Finding> findings)
        {
            if (before == null && after == null)
                return;

            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (before != null) foreach (string k in before.Keys) keys.Add(k);
            if (after != null) foreach (string k in after.Keys) keys.Add(k);

            foreach (string key in keys)
            {
                double db = 0.0;
                double da = 0.0;
                bool inB = before != null && before.TryGetValue(key, out db);
                bool inA = after != null && after.TryGetValue(key, out da);

                if (inB != inA || !DoubleEqual(db, da))
                {
                    findings.Add(Fail(recId,
                        Inv("INV6 manifest recording={0} field=delta resource='{1}' error=delta-drift before={2} after={3}",
                            recId, key, inB ? db.ToString("R", CultureInfo.InvariantCulture) : "<absent>",
                            inA ? da.ToString("R", CultureInfo.InvariantCulture) : "<absent>")));
                }
            }
        }

        private static Finding Fail(string recId, string message)
        {
            return new Finding(RuleIdConst, VerdictLevel.Fail, recId, -1, message,
                "RecordingManifestCodec.SerializeResourceManifest");
        }

        // NaN-aware: two NaNs are treated equal (a stable NaN round-trip is not a
        // failure); otherwise bitwise-exact equality after the lossless "R" codec.
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
