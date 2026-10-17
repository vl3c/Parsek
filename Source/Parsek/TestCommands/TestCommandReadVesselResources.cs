using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.TestCommands
{
    /// <summary>One resource total on one vessel (or summed over several).</summary>
    internal struct ResourceReading
    {
        internal double Amount;
        internal double MaxAmount;
    }

    /// <summary>What the applier read off one live vessel's tanks.</summary>
    internal sealed class VesselResourceReading
    {
        /// <summary>The recording id the vessel was named by (<c>rec=</c>), or null for
        /// <c>pid=</c>.</summary>
        internal string RecordingId;

        /// <summary>False when a <c>rec=</c> selector named a recording whose spawned vessel
        /// is not live: the reading is empty and the vessel counts in <c>missing=</c>.</summary>
        internal bool Found;

        internal uint Pid;
        internal string Name;
        internal bool Loaded;
        internal int Parts;

        /// <summary>The part uids (<c>Part.flightID</c> / <c>ProtoPartSnapshot.flightID</c>)
        /// the vessel carries, in part order.</summary>
        internal List<uint> PartUids = new List<uint>();

        /// <summary>Per-resource totals over every part, keyed by resource name.</summary>
        internal Dictionary<string, ResourceReading> Resources =
            new Dictionary<string, ResourceReading>(StringComparer.Ordinal);

        /// <summary>The per-resource totals of the recording's end snapshot (the vessel the
        /// spawn was built from), or null when not requested / unavailable.</summary>
        internal Dictionary<string, ResourceReading> Snapshot;
    }

    /// <summary>One expectation compared against a summed total.</summary>
    internal struct ResourceComparison
    {
        internal string Resource;
        internal double Expected;
        internal double Actual;
        internal bool Match;
    }

    /// <summary>
    /// Pure half of the automation-only <c>ReadVesselResources</c> verb (todo
    /// HARNESS-VERBS-FOR-TIMELINE-OPS, roadmap TC-1): reads what is ACTUALLY in the tanks of
    /// one or more live vessels, so a lane can check resources against numbers instead of
    /// trusting Parsek's own log lines about them.
    ///
    /// <para><b>Grammar.</b> <c>cmd=ReadVesselResources (pid=&lt;p&gt;[,&lt;p&gt;...] |
    /// rec=&lt;id&gt;[,&lt;id&gt;...]) [resources=&lt;R&gt;[,&lt;R&gt;...]]
    /// [expect=&lt;R&gt;:&lt;amount&gt;[;&lt;R&gt;:&lt;amount&gt;...]] [tolerance=&lt;abs&gt;]
    /// [expectSnapshot=true|false]</c>. Exactly one selector, 1..<see cref="MaxTargets"/>
    /// entries. <c>pid=</c> names live non-ghost vessels by persistentId; <c>rec=</c> names
    /// committed recordings, read through their spawned vessel
    /// (<c>Recording.SpawnedVesselPersistentId</c>). Issued in FLIGHT, the Space Center or the
    /// Tracking Station (<c>RequiresVesselListScene</c>), on loaded vessels (live
    /// <c>PartResource.amount</c>) or unloaded ones (their proto part snapshots, what KSP
    /// would load). SINGLE-PHASE and read-only.</para>
    ///
    /// <para><b>Conservation.</b> <c>expect=</c> compares the SUM over every named vessel per
    /// resource, within <c>tolerance=</c> (absolute, default <see cref="DefaultTolerance"/>).
    /// Naming two vessels that exchanged fuel and expecting the total they held before the
    /// exchange is the check that what one gave is what the other got.
    /// <c>expectSnapshot=true</c> (with <c>rec=</c> only) compares each vessel, per resource,
    /// with its recording's end snapshot; it holds only where no route delivered into or
    /// out of the vessel after its spawn.</para>
    ///
    /// <para><b>A rec= vessel that is not live is a finding, not a refusal.</b> It is
    /// counted in <c>missing=</c> on an OK answer (and fails any expectation), because a
    /// recording whose spawn did not happen is a Parsek outcome a lane must be able to red
    /// on as PARSEK-FAIL; a REJECTED would read as a driver fault and retry it away. A
    /// <c>pid=</c> that names no live vessel stays a spec fault.</para>
    ///
    /// <para><b>Answer.</b> OK payload <c>vessels= pids= names= loaded= parts= uidDigest=
    /// uidCollisions= missing= resources=</c>, then <c>&lt;R&gt;= &lt;R&gt;Max=</c> per
    /// reported resource (summed), then <c>match= mismatches=</c> and <c>snapshotMatch=
    /// snapshotMismatches=</c> (<c>none</c> when not asked). Amounts are invariant
    /// <c>F4</c>. <c>uidCollisions</c> counts part uids of the named vessels that another
    /// live vessel (or another named one) also carries: a duplicated spawn.</para>
    /// </summary>
    internal static class TestCommandReadVesselResources
    {
        internal const string Verb = "ReadVesselResources";
        internal const string PidKey = "pid";
        internal const string RecKey = "rec";
        internal const string ResourcesKey = "resources";
        internal const string ExpectKey = "expect";
        internal const string ToleranceKey = "tolerance";
        internal const string ExpectSnapshotKey = "expectSnapshot";

        internal const int MaxTargets = 8;
        internal const double DefaultTolerance = 0.01;

        /// <summary>The <c>match=</c> / <c>snapshotMatch=</c> value when the comparison was
        /// not asked for.</summary>
        internal const string NotAskedToken = "none";

        internal const string TargetArgMissingReason = "readvesselresources-target-arg-missing";
        internal const string TargetArgConflictReason = "readvesselresources-target-arg-conflict";
        internal const string PidArgInvalidReason = "readvesselresources-pid-arg-invalid";
        internal const string RecArgInvalidReason = "readvesselresources-rec-arg-invalid";
        internal const string TooManyTargetsReason = "readvesselresources-too-many-targets";
        internal const string ResourcesArgInvalidReason = "readvesselresources-resources-arg-invalid";
        internal const string ExpectArgInvalidReason = "readvesselresources-expect-arg-invalid";
        internal const string ToleranceArgInvalidReason = "readvesselresources-tolerance-arg-invalid";
        internal const string ExpectSnapshotArgInvalidReason = "readvesselresources-expect-snapshot-arg-invalid";
        internal const string ExpectSnapshotNeedsRecReason = "readvesselresources-expect-snapshot-needs-rec";
        internal const string UnknownRecordingReason = "readvesselresources-unknown-recording";
        internal const string VesselNotFoundReason = "readvesselresources-vessel-not-found";
        internal const string TargetIsGhostReason = "readvesselresources-target-is-ghost";

        /// <summary>The REJECTED vocabulary, in gate order. hlib.READVESSELRESOURCES_REASONS
        /// mirrors it (ReadVesselResourcesSourceSyncTests).</summary>
        internal static readonly string[] Reasons =
        {
            "readvesselresources-target-arg-missing",
            "readvesselresources-target-arg-conflict",
            "readvesselresources-pid-arg-invalid",
            "readvesselresources-rec-arg-invalid",
            "readvesselresources-too-many-targets",
            "readvesselresources-resources-arg-invalid",
            "readvesselresources-expect-arg-invalid",
            "readvesselresources-tolerance-arg-invalid",
            "readvesselresources-expect-snapshot-arg-invalid",
            "readvesselresources-expect-snapshot-needs-rec",
            "readvesselresources-unknown-recording",
            "readvesselresources-vessel-not-found",
            "readvesselresources-target-is-ghost",
        };

        /// <summary>The resources a snapshot comparison skips when <c>resources=</c> is not
        /// given: the ones <c>VesselSnapshotOps.ExtractResourceManifest</c> excludes as noise
        /// (they change with time, not with transfers).</summary>
        internal static readonly string[] SnapshotNoiseResources = { "ElectricCharge", "IntakeAir" };

        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        /// <summary>A resource name usable as a payload key (the harness's
        /// <c>${label.field}</c> field grammar).</summary>
        internal static bool IsResourceName(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                    || (c >= '0' && c <= '9') || c == '_';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>
        /// Parses the selector: exactly one of <c>pid=</c> / <c>rec=</c>, each a comma list
        /// of 1..<see cref="MaxTargets"/> entries (a pid is a nonzero decimal uint, a rec id
        /// passes <c>RecordingPaths.ValidateRecordingId</c>). Returns null on success or the
        /// refusal reason.
        /// </summary>
        internal static string ParseTargets(string rawPid, string rawRec,
            Func<string, bool> isValidRecordingId, out List<uint> pids, out List<string> recs)
        {
            pids = null;
            recs = null;
            bool hasPid = !string.IsNullOrEmpty(rawPid);
            bool hasRec = !string.IsNullOrEmpty(rawRec);
            if (!hasPid && !hasRec) return TargetArgMissingReason;
            if (hasPid && hasRec) return TargetArgConflictReason;
            string[] items = (hasPid ? rawPid : rawRec).Split(',');
            if (hasPid)
            {
                pids = new List<uint>();
                foreach (string item in items)
                {
                    if (!uint.TryParse(item, NumberStyles.None, Ic, out uint pid) || pid == 0)
                        return PidArgInvalidReason;
                    pids.Add(pid);
                }
            }
            else
            {
                recs = new List<string>();
                foreach (string item in items)
                {
                    if (string.IsNullOrEmpty(item) || isValidRecordingId == null || !isValidRecordingId(item))
                        return RecArgInvalidReason;
                    recs.Add(item);
                }
            }
            if (items.Length > MaxTargets) return TooManyTargetsReason;
            return null;
        }

        /// <summary>Parses the OPTIONAL <c>resources=</c> comma list (each a payload-key-safe
        /// name, no duplicates). Null / empty means every resource found. Returns null on
        /// success or the refusal reason.</summary>
        internal static string ParseResources(string raw, out List<string> resources)
        {
            resources = null;
            if (string.IsNullOrEmpty(raw)) return null;
            var list = new List<string>();
            foreach (string item in raw.Split(','))
            {
                if (!IsResourceName(item) || list.Contains(item)) return ResourcesArgInvalidReason;
                list.Add(item);
            }
            resources = list;
            return null;
        }

        /// <summary>Parses the OPTIONAL <c>expect=</c> list <c>R:amount;R:amount</c> (amount a
        /// finite invariant decimal &gt;= 0, no duplicate resource), in written order.</summary>
        internal static string ParseExpect(string raw, out List<KeyValuePair<string, double>> expect)
        {
            expect = null;
            if (string.IsNullOrEmpty(raw)) return null;
            var list = new List<KeyValuePair<string, double>>();
            foreach (string item in raw.Split(';'))
            {
                int colon = item.IndexOf(':');
                if (colon <= 0) return ExpectArgInvalidReason;
                string name = item.Substring(0, colon);
                string value = item.Substring(colon + 1);
                if (!IsResourceName(name)) return ExpectArgInvalidReason;
                if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, Ic, out double amount)
                    || double.IsNaN(amount) || double.IsInfinity(amount) || amount < 0)
                    return ExpectArgInvalidReason;
                foreach (var kv in list)
                    if (kv.Key == name) return ExpectArgInvalidReason;
                list.Add(new KeyValuePair<string, double>(name, amount));
            }
            expect = list;
            return null;
        }

        /// <summary>Parses the OPTIONAL <c>tolerance=</c> (finite invariant decimal &gt;= 0);
        /// absent is <see cref="DefaultTolerance"/>.</summary>
        internal static string ParseTolerance(string raw, out double tolerance)
        {
            tolerance = DefaultTolerance;
            if (string.IsNullOrEmpty(raw)) return null;
            if (!double.TryParse(raw, NumberStyles.AllowDecimalPoint, Ic, out double t)
                || double.IsNaN(t) || double.IsInfinity(t) || t < 0)
                return ToleranceArgInvalidReason;
            tolerance = t;
            return null;
        }

        /// <summary>Parses the OPTIONAL <c>expectSnapshot=</c> (<c>true</c> / <c>false</c>,
        /// ordinal); it needs a <c>rec=</c> selector (a pid names no recording).</summary>
        internal static string ParseExpectSnapshot(string raw, bool hasRec, out bool expectSnapshot)
        {
            expectSnapshot = false;
            if (string.IsNullOrEmpty(raw)) return null;
            if (raw == "true") expectSnapshot = true;
            else if (raw != "false") return ExpectSnapshotArgInvalidReason;
            if (expectSnapshot && !hasRec) return ExpectSnapshotNeedsRecReason;
            return null;
        }

        /// <summary>Adds one tank to a total.</summary>
        internal static void Accumulate(Dictionary<string, ResourceReading> into, string resource,
            double amount, double maxAmount)
        {
            if (into == null || string.IsNullOrEmpty(resource)) return;
            into.TryGetValue(resource, out ResourceReading r);
            r.Amount += amount;
            r.MaxAmount += maxAmount;
            into[resource] = r;
        }

        /// <summary>The per-resource totals of a vessel snapshot (PART &gt; RESOURCE nodes,
        /// every resource, ElectricCharge included), or null when the snapshot is null or has
        /// no parts.</summary>
        internal static Dictionary<string, ResourceReading> SnapshotTotals(ConfigNode vesselSnapshot)
        {
            if (vesselSnapshot == null) return null;
            ConfigNode[] parts = vesselSnapshot.GetNodes("PART");
            if (parts.Length == 0) return null;
            var totals = new Dictionary<string, ResourceReading>(StringComparer.Ordinal);
            foreach (ConfigNode part in parts)
            {
                foreach (ConfigNode res in part.GetNodes("RESOURCE"))
                {
                    double.TryParse(res.GetValue("amount"), NumberStyles.Float, Ic, out double amount);
                    double.TryParse(res.GetValue("maxAmount"), NumberStyles.Float, Ic, out double max);
                    Accumulate(totals, res.GetValue("name"), amount, max);
                }
            }
            return totals;
        }

        /// <summary>The per-resource sum over every FOUND reading.</summary>
        internal static Dictionary<string, ResourceReading> SumReadings(IList<VesselResourceReading> readings)
        {
            var sum = new Dictionary<string, ResourceReading>(StringComparer.Ordinal);
            if (readings == null) return sum;
            foreach (VesselResourceReading r in readings)
            {
                if (r == null || !r.Found) continue;
                foreach (var kv in r.Resources)
                    Accumulate(sum, kv.Key, kv.Value.Amount, kv.Value.MaxAmount);
            }
            return sum;
        }

        /// <summary>The resources the answer reports: the requested list in its order, else
        /// every resource found, ordinal-sorted.</summary>
        internal static List<string> ReportedResources(List<string> requested,
            Dictionary<string, ResourceReading> sum)
        {
            if (requested != null) return new List<string>(requested);
            var names = new List<string>();
            if (sum != null)
                foreach (string k in sum.Keys)
                    if (IsResourceName(k)) names.Add(k);
            names.Sort(StringComparer.Ordinal);
            return names;
        }

        /// <summary>Compares the summed totals with each expectation (a resource no named
        /// vessel carries reads 0). A missing vessel fails every comparison: its tanks are
        /// part of the total that was promised.</summary>
        internal static List<ResourceComparison> CompareExpect(
            List<KeyValuePair<string, double>> expect, Dictionary<string, ResourceReading> sum,
            double tolerance, int missing)
        {
            var result = new List<ResourceComparison>();
            if (expect == null) return result;
            foreach (var kv in expect)
            {
                double actual = 0;
                if (sum != null && sum.TryGetValue(kv.Key, out ResourceReading r)) actual = r.Amount;
                result.Add(new ResourceComparison
                {
                    Resource = kv.Key,
                    Expected = kv.Value,
                    Actual = actual,
                    Match = missing == 0 && Math.Abs(actual - kv.Value) <= tolerance,
                });
            }
            return result;
        }

        /// <summary>The resources a snapshot comparison covers for one vessel: the requested
        /// list, else the union of the live and snapshot names minus
        /// <see cref="SnapshotNoiseResources"/>, ordinal-sorted.</summary>
        internal static List<string> SnapshotCompareSet(List<string> requested,
            Dictionary<string, ResourceReading> live, Dictionary<string, ResourceReading> snapshot)
        {
            if (requested != null) return new List<string>(requested);
            var set = new SortedSet<string>(StringComparer.Ordinal);
            if (live != null) foreach (string k in live.Keys) set.Add(k);
            if (snapshot != null) foreach (string k in snapshot.Keys) set.Add(k);
            foreach (string noise in SnapshotNoiseResources) set.Remove(noise);
            return new List<string>(set);
        }

        /// <summary>Compares one found vessel with its recording's end snapshot. Returns the
        /// per-resource rows (Expected = snapshot, Actual = live).</summary>
        internal static List<ResourceComparison> CompareSnapshot(List<string> requested,
            VesselResourceReading reading, double tolerance)
        {
            var result = new List<ResourceComparison>();
            if (reading == null || reading.Snapshot == null) return result;
            foreach (string name in SnapshotCompareSet(requested, reading.Resources, reading.Snapshot))
            {
                reading.Resources.TryGetValue(name, out ResourceReading live);
                reading.Snapshot.TryGetValue(name, out ResourceReading snap);
                result.Add(new ResourceComparison
                {
                    Resource = name,
                    Expected = snap.Amount,
                    Actual = live.Amount,
                    Match = Math.Abs(live.Amount - snap.Amount) <= tolerance,
                });
            }
            return result;
        }

        /// <summary>
        /// Counts part uids of the named vessels that are not unique: carried twice among
        /// them, or also carried by another live vessel (<paramref name="otherUids"/>, the
        /// part uids of every live vessel NOT named). A duplicated spawn of one vessel shows
        /// here; two distinct vessels never share a uid.
        /// </summary>
        internal static int CountUidCollisions(IList<VesselResourceReading> readings,
            ICollection<uint> otherUids)
        {
            var seen = new HashSet<uint>();
            int collisions = 0;
            if (readings == null) return 0;
            foreach (VesselResourceReading r in readings)
            {
                if (r == null || !r.Found) continue;
                foreach (uint uid in r.PartUids)
                {
                    if (!seen.Add(uid)) collisions++;
                    else if (otherUids != null && otherUids.Contains(uid)) collisions++;
                }
            }
            return collisions;
        }

        /// <summary>FNV-1a over the sorted part uids of every found reading, eight lowercase
        /// hex digits (ListHandles' digest shape). Order-free, so it names the SET of parts.</summary>
        internal static string UidDigest(IList<VesselResourceReading> readings)
        {
            var uids = new List<uint>();
            if (readings != null)
                foreach (VesselResourceReading r in readings)
                    if (r != null && r.Found) uids.AddRange(r.PartUids);
            uids.Sort();
            uint h = TestCommandListHandles.DigestOffsetBasis;
            foreach (uint uid in uids)
            {
                for (int b = 0; b < 4; b++)
                {
                    h ^= (uid >> (8 * b)) & 0xFF;
                    h *= 16777619u;
                }
            }
            return h.ToString("x8", Ic);
        }

        internal static string Amount(double v) => v.ToString("F4", Ic);

        private static string JoinMap(IList<VesselResourceReading> readings,
            Func<VesselResourceReading, string> f)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < readings.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(f(readings[i]));
            }
            return sb.ToString();
        }

        private static string MismatchList(List<ResourceComparison> rows, string prefix)
        {
            var sb = new StringBuilder();
            foreach (ResourceComparison c in rows)
            {
                if (c.Match) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(prefix).Append(c.Resource);
            }
            return sb.ToString();
        }

        /// <summary>The <c>match=</c> value: <see cref="NotAskedToken"/> when nothing was
        /// expected.</summary>
        internal static string MatchToken(bool asked, bool match)
            => !asked ? NotAskedToken : (match ? "true" : "false");

        internal static bool AllMatch(List<ResourceComparison> rows)
        {
            foreach (ResourceComparison c in rows) if (!c.Match) return false;
            return true;
        }

        /// <summary>
        /// Folds the snapshot comparisons of every vessel into the <c>snapshotMatch=</c>
        /// verdict and its mismatch list (<c>&lt;rec&gt;:&lt;R&gt;</c>, or
        /// <c>&lt;rec&gt;:no-snapshot</c> / <c>&lt;rec&gt;:missing</c> for a vessel that
        /// cannot be compared).
        /// </summary>
        internal static bool FoldSnapshot(IList<VesselResourceReading> readings, List<string> requested,
            double tolerance, out string mismatches)
        {
            var parts = new List<string>();
            foreach (VesselResourceReading r in readings)
            {
                if (r == null) continue;
                if (!r.Found) { parts.Add(r.RecordingId + ":missing"); continue; }
                if (r.Snapshot == null) { parts.Add(r.RecordingId + ":no-snapshot"); continue; }
                string list = MismatchList(CompareSnapshot(requested, r, tolerance), r.RecordingId + ":");
                if (list.Length > 0) parts.Add(list);
            }
            mismatches = string.Join(",", parts.ToArray());
            return parts.Count == 0;
        }

        /// <summary>The OK payload, in wire order.</summary>
        internal static List<KeyValuePair<string, string>> BuildOkPayload(
            IList<VesselResourceReading> readings, List<string> reported,
            Dictionary<string, ResourceReading> sum, int uidCollisions,
            bool expectAsked, List<ResourceComparison> expectRows,
            bool snapshotAsked, bool snapshotMatch, string snapshotMismatches)
        {
            int missing = 0;
            foreach (VesselResourceReading r in readings) if (!r.Found) missing++;
            var p = new List<KeyValuePair<string, string>>
            {
                Kv("vessels", readings.Count.ToString(Ic)),
                Kv("pids", JoinMap(readings, r => r.Found ? r.Pid.ToString(Ic) : "0")),
                Kv("names", JoinMap(readings, r => r.Name ?? string.Empty)),
                Kv("loaded", JoinMap(readings, r => r.Found ? (r.Loaded ? "true" : "false") : "missing")),
                Kv("parts", JoinMap(readings, r => r.Parts.ToString(Ic))),
                Kv("uidDigest", UidDigest(readings)),
                Kv("uidCollisions", uidCollisions.ToString(Ic)),
                Kv("missing", missing.ToString(Ic)),
                Kv("resources", string.Join(",", reported.ToArray())),
            };
            foreach (string name in reported)
            {
                sum.TryGetValue(name, out ResourceReading total);
                p.Add(Kv(name, Amount(total.Amount)));
                p.Add(Kv(name + "Max", Amount(total.MaxAmount)));
            }
            p.Add(Kv("match", MatchToken(expectAsked, AllMatch(expectRows))));
            p.Add(Kv("mismatches", MismatchList(expectRows, string.Empty)));
            p.Add(Kv("snapshotMatch", MatchToken(snapshotAsked, snapshotMatch)));
            p.Add(Kv("snapshotMismatches", snapshotAsked ? snapshotMismatches ?? string.Empty : string.Empty));
            return p;
        }

        private static KeyValuePair<string, string> Kv(string k, string v)
            => new KeyValuePair<string, string>(k, v);

        private static string FormatResourceList(List<string> names,
            Dictionary<string, ResourceReading> totals)
        {
            var sb = new StringBuilder();
            foreach (string name in names)
            {
                ResourceReading r = default(ResourceReading);
                if (totals != null) totals.TryGetValue(name, out r);
                sb.Append(' ').Append(name).Append('=').Append(Amount(r.Amount))
                  .Append('/').Append(Amount(r.MaxAmount));
            }
            return sb.ToString();
        }

        /// <summary>One vessel's grep-stable line. The resource list is the vessel's own
        /// (requested list, else every resource it carries, sorted).</summary>
        internal static string FormatVesselLine(int index, VesselResourceReading r, List<string> requested)
        {
            string head = "readvesselresources vessel index=" + index.ToString(Ic)
                + " rec=" + (r.RecordingId ?? "-");
            if (!r.Found)
                return head + " found=false";
            List<string> names = requested != null
                ? requested
                : ReportedResources(null, r.Resources);
            return head + " found=true pid=" + r.Pid.ToString(Ic)
                + " name='" + (r.Name ?? string.Empty) + "'"
                + " loaded=" + (r.Loaded ? "true" : "false")
                + " parts=" + r.Parts.ToString(Ic)
                + FormatResourceList(names, r.Resources);
        }

        /// <summary>The summed line over every found vessel.</summary>
        internal static string FormatTotalLine(int vessels, List<string> reported,
            Dictionary<string, ResourceReading> sum)
            => "readvesselresources total vessels=" + vessels.ToString(Ic) + FormatResourceList(reported, sum);

        /// <summary>One <c>expect=</c> row.</summary>
        internal static string FormatExpectLine(ResourceComparison c, double tolerance)
            => "readvesselresources expect resource=" + c.Resource
                + " expected=" + Amount(c.Expected)
                + " actual=" + Amount(c.Actual)
                + " diff=" + Amount(c.Actual - c.Expected)
                + " tolerance=" + Amount(tolerance)
                + " match=" + (c.Match ? "true" : "false");

        /// <summary>One snapshot comparison row.</summary>
        internal static string FormatSnapshotLine(string recordingId, uint pid, ResourceComparison c)
            => "readvesselresources snapshot rec=" + recordingId
                + " pid=" + pid.ToString(Ic)
                + " resource=" + c.Resource
                + " live=" + Amount(c.Actual)
                + " snapshot=" + Amount(c.Expected)
                + " diff=" + Amount(c.Actual - c.Expected)
                + " match=" + (c.Match ? "true" : "false");

        /// <summary>The closing line: the verdicts a spec pins.</summary>
        internal static string FormatCompleteLine(int vessels, int missing, int uidCollisions,
            string uidDigest, string match, string snapshotMatch)
            => "readvesselresources complete vessels=" + vessels.ToString(Ic)
                + " missing=" + missing.ToString(Ic)
                + " uidCollisions=" + uidCollisions.ToString(Ic)
                + " uidDigest=" + uidDigest
                + " match=" + match
                + " snapshotMatch=" + snapshotMatch;
    }
}
