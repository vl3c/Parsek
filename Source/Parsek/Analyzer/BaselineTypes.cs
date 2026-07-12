using System;
using System.Collections.Generic;

namespace Parsek.Analyzer
{
    // Pure value types for the per-save known-findings baseline (design doc
    // docs/dev/design-autotest-findings-baseline.md "Data Model"). These live in
    // Parsek.dll (Parsek.Analyzer) alongside the invariant core so the future
    // in-game H5 category could reuse the matching filter; they carry NO file I/O
    // (the codec + builder that touch disk live in Parsek.Tests). No store symbol
    // is referenced here (ERS grep gate stays clean).

    /// <summary>
    /// How a run treats a per-save baseline. Numeric values are stable (serialized
    /// nowhere, but the env parser maps to them): Ignore is the source-compatible
    /// default so an existing caller is byte-identical to the pre-feature analyzer.
    /// </summary>
    internal enum BaselineMode
    {
        /// <summary>Default. The baseline is never loaded; only a PRESENT-NOT-APPLIED
        /// INFO is emitted if a file exists beside the save.</summary>
        Ignore = 0,

        /// <summary>Triage / historical: load the baseline and mark matching findings
        /// Baselined; emit stale / multi-match / escalation meta-findings.</summary>
        Apply = 1,

        /// <summary>Fresh-save guard: the mere PRESENCE of a baseline beside the save
        /// is a BASELINE-FORBIDDEN FAIL. Nothing is applied.</summary>
        Forbid = 2,
    }

    /// <summary>
    /// The tuple that decides whether a live finding equals a baseline entry:
    /// (RuleId, Target, SectionIndex, MessageDigest), all ordinal. Value equality.
    /// </summary>
    internal struct BaselineKey : IEquatable<BaselineKey>
    {
        public string RuleId;
        public string Target;
        public int SectionIndex;

        /// <summary>NormalizeMessageDigest(finding.Message): numeric literals masked.</summary>
        public string MessageDigest;

        public BaselineKey(string ruleId, string target, int sectionIndex, string messageDigest)
        {
            RuleId = ruleId ?? "";
            Target = target ?? "";
            SectionIndex = sectionIndex;
            MessageDigest = messageDigest ?? "";
        }

        public bool Equals(BaselineKey other)
        {
            return SectionIndex == other.SectionIndex
                && string.Equals(RuleId ?? "", other.RuleId ?? "", StringComparison.Ordinal)
                && string.Equals(Target ?? "", other.Target ?? "", StringComparison.Ordinal)
                && string.Equals(MessageDigest ?? "", other.MessageDigest ?? "", StringComparison.Ordinal);
        }

        public override bool Equals(object obj) => obj is BaselineKey k && Equals(k);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + StringComparer.Ordinal.GetHashCode(RuleId ?? "");
                h = h * 31 + StringComparer.Ordinal.GetHashCode(Target ?? "");
                h = h * 31 + SectionIndex;
                h = h * 31 + StringComparer.Ordinal.GetHashCode(MessageDigest ?? "");
                return h;
            }
        }
    }

    /// <summary>
    /// One accepted finding: the matching key components plus provenance (the level
    /// it was captured at, and a free-text reason for the acceptance).
    /// </summary>
    internal struct BaselineEntry
    {
        public string RuleId;
        public string Target;
        public int SectionIndex;
        public string MessageDigest;

        /// <summary>The level the finding had when it was baselined.</summary>
        public VerdictLevel CapturedLevel;

        /// <summary>Free text, per entry: why this finding was accepted.</summary>
        public string Reason;

        public BaselineEntry(
            string ruleId, string target, int sectionIndex, string messageDigest,
            VerdictLevel capturedLevel, string reason)
        {
            RuleId = ruleId ?? "";
            Target = target ?? "";
            SectionIndex = sectionIndex;
            MessageDigest = messageDigest ?? "";
            CapturedLevel = capturedLevel;
            Reason = reason ?? "";
        }

        public BaselineKey Key => new BaselineKey(RuleId, Target, SectionIndex, MessageDigest);
    }

    /// <summary>
    /// A loaded per-save baseline: file-level provenance plus the accepted entries.
    /// Built by the Tests-project codec (from baseline.cfg) or the builder (from a
    /// report). Carries a helper to project the entries into an O(1) match index.
    /// </summary>
    internal sealed class AnalysisBaseline
    {
        /// <summary>Schema of the baseline FILE itself (independent of the report schema).</summary>
        public int BaselineFormatVersion = BaselineFormat.CurrentBaselineFormatVersion;

        /// <summary>AnalysisReport.CurrentAnalyzerVersion at write time (provenance).</summary>
        public string CreatedAtAnalyzerVersion = "";

        /// <summary>Discovered subject schema generation at write time (provenance only).</summary>
        public int SubjectSchemaGeneration;

        /// <summary>ISO-8601 write time (provenance only; never emitted in reports).</summary>
        public string CreatedAtUtc = "";

        /// <summary>Free text, per file: why this baseline exists.</summary>
        public string Reason = "";

        public IReadOnlyList<BaselineEntry> Entries = new List<BaselineEntry>();

        /// <summary>
        /// Projects the entries into a first-wins key -&gt; entry index for O(1)
        /// matching. The codec already de-dupes (emitting DUPLICATE-ENTRY faults), so
        /// this is normally a straight projection; the first-wins guard here is
        /// belt-and-suspenders against a hand-built list.
        /// </summary>
        public Dictionary<BaselineKey, BaselineEntry> BuildIndex()
        {
            var index = new Dictionary<BaselineKey, BaselineEntry>();
            if (Entries == null)
                return index;
            foreach (BaselineEntry e in Entries)
            {
                BaselineKey key = e.Key;
                if (!index.ContainsKey(key))
                    index[key] = e;
            }
            return index;
        }
    }

    /// <summary>Baseline-file format constants (shared by codec + filter).</summary>
    internal static class BaselineFormat
    {
        /// <summary>The initial (and current) baseline-file schema. A file whose
        /// version exceeds this is a hard BASELINE-VERSION-FUTURE FAIL.</summary>
        public const int CurrentBaselineFormatVersion = 1;

        /// <summary>ConfigNode wrapper node name for the baseline file.</summary>
        public const string RootNodeName = "PARSEK_ANALYSIS_BASELINE";
    }

    /// <summary>The kind of fault the codec recorded while loading a baseline file.</summary>
    internal enum BaselineFaultKind
    {
        /// <summary>Whole-file unparseable ConfigNode -&gt; hard FAIL (Apply).</summary>
        ParseFault = 0,

        /// <summary>baselineFormatVersion exceeds the analyzer -&gt; hard FAIL (Apply).</summary>
        VersionFuture = 1,

        /// <summary>One ENTRY missing a required key / bad capturedLevel -&gt; WARN, dropped.</summary>
        EntryMalformed = 2,

        /// <summary>Two ENTRY nodes share a BaselineKey -&gt; WARN, deduped.</summary>
        DuplicateEntry = 3,
    }

    /// <summary>
    /// One fault the codec's <c>Load</c> returned. The pure filter folds these into
    /// meta-findings (it never touches the file itself). A file-level fault leaves
    /// <see cref="Target"/> null (the filter substitutes the save token); a
    /// per-entry fault carries the offending entry's best-effort Target.
    /// </summary>
    internal struct BaselineLoadFault
    {
        public BaselineFaultKind Kind;
        public string Target;
        public string Detail;

        public BaselineLoadFault(BaselineFaultKind kind, string target, string detail)
        {
            Kind = kind;
            Target = target;
            Detail = detail ?? "";
        }

        /// <summary>True for faults that must abort matching and red the run.</summary>
        public bool IsHard => Kind == BaselineFaultKind.ParseFault || Kind == BaselineFaultKind.VersionFuture;
    }
}
