using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Parsek.Tests.Analyzer
{
    // The baseline-file codec (design doc "Baseline file (frozen output contract)").
    // Lives in Parsek.Tests because it touches the filesystem (ConfigNode + the
    // house FileIOUtils safe-write); the pure filter it feeds lives in
    // Parsek.Analyzer. The file is a KSP ConfigNode: ConfigNode.Load is already a
    // dependency, FileIOUtils provides the .tmp+rename safe-write, and .cfg is the
    // house serialization format (diffable, hand-editable). All ints use
    // InvariantCulture.
    //
    // Per the house ConfigNode I/O rule, ConfigNode.Save writes node CONTENTS only,
    // so Write passes a node whose values + ENTRY children become the file body
    // (no PARSEK_ANALYSIS_BASELINE wrapper line), and Load reads those directly off
    // the node ConfigNode.Load returns. A hand-authored file that DID include the
    // wrapper (as the design example shows for readability) is still accepted: Load
    // unwraps a RootNodeName child when present.
    internal static class BaselineCodec
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// Loads a baseline file into an <see cref="AnalysisBaseline"/> plus a fault
        /// list the pure filter folds into meta-findings. A whole-file parse failure
        /// or a future format version returns a null baseline with a hard fault (the
        /// filter reds the run). A missing file returns (null, empty): the caller
        /// distinguishes present-vs-absent and drives NOT-FOUND itself.
        /// </summary>
        internal static (AnalysisBaseline baseline, List<BaselineLoadFault> faults) Load(string path)
        {
            var faults = new List<BaselineLoadFault>();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return (null, faults); // absent: caller handles via baselinePresent

            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                faults.Add(new BaselineLoadFault(BaselineFaultKind.ParseFault, path,
                    ex.GetType().Name + ": " + ex.Message));
                return (null, faults);
            }

            // KSP's ConfigNode.Load parses unbalanced braces leniently, so a brace
            // check is the deterministic corruption signal (mirrors SaveDirectoryLoader).
            if (!BracesBalanced(text))
            {
                faults.Add(new BaselineLoadFault(BaselineFaultKind.ParseFault, path, "unbalanced-braces"));
                return (null, faults);
            }

            ConfigNode loaded;
            try
            {
                loaded = ConfigNode.Load(path);
            }
            catch (Exception ex)
            {
                faults.Add(new BaselineLoadFault(BaselineFaultKind.ParseFault, path,
                    ex.GetType().Name + ": " + ex.Message));
                return (null, faults);
            }

            if (loaded == null)
            {
                faults.Add(new BaselineLoadFault(BaselineFaultKind.ParseFault, path,
                    "configNode-load-returned-null"));
                return (null, faults);
            }

            // Contents-only body -> read off `loaded`; tolerate a hand-authored wrapper.
            ConfigNode node = loaded.GetNode(BaselineFormat.RootNodeName) ?? loaded;

            // A structurally empty parse of a non-empty file is corrupt.
            if (node.values.Count == 0 && node.nodes.Count == 0 && text.Trim().Length > 0)
            {
                faults.Add(new BaselineLoadFault(BaselineFaultKind.ParseFault, path, "empty-or-unparseable"));
                return (null, faults);
            }

            var baseline = new AnalysisBaseline
            {
                BaselineFormatVersion = ParseInt(node.GetValue("baselineFormatVersion"), 0),
                CreatedAtAnalyzerVersion = node.GetValue("createdAtAnalyzerVersion") ?? "",
                SubjectSchemaGeneration = ParseInt(node.GetValue("subjectSchemaGeneration"), 0),
                CreatedAtUtc = node.GetValue("createdAtUtc") ?? "",
                Reason = node.GetValue("reason") ?? "",
            };

            if (baseline.BaselineFormatVersion > BaselineFormat.CurrentBaselineFormatVersion)
            {
                faults.Add(new BaselineLoadFault(BaselineFaultKind.VersionFuture, path,
                    "baselineFormatVersion=" + baseline.BaselineFormatVersion
                    + " > " + BaselineFormat.CurrentBaselineFormatVersion));
                return (null, faults); // a future format cannot be safely applied
            }

            var entries = new List<BaselineEntry>();
            var seen = new HashSet<BaselineKey>();
            foreach (ConfigNode e in node.GetNodes("ENTRY"))
            {
                string ruleId = e.GetValue("ruleId");
                string target = e.GetValue("target");
                string digest = e.GetValue("messageDigest");
                string levelTok = e.GetValue("capturedLevel");
                string reason = e.GetValue("reason") ?? "";
                int section = ParseInt(e.GetValue("sectionIndex"), -1);

                if (string.IsNullOrEmpty(ruleId) || string.IsNullOrEmpty(target)
                    || string.IsNullOrEmpty(digest) || !TryParseLevel(levelTok, out VerdictLevel lvl))
                {
                    faults.Add(new BaselineLoadFault(BaselineFaultKind.EntryMalformed,
                        target ?? ruleId ?? "<entry>",
                        "ruleId='" + (ruleId ?? "") + "' capturedLevel='" + (levelTok ?? "") + "'"));
                    continue;
                }

                var entry = new BaselineEntry(ruleId, target, section, digest, lvl, reason);
                BaselineKey key = entry.Key;
                if (seen.Contains(key))
                {
                    faults.Add(new BaselineLoadFault(BaselineFaultKind.DuplicateEntry, target,
                        "ruleId='" + ruleId + "' section=" + section.ToString(IC)));
                    continue; // first kept, rest dropped (design edge case 7 / DUPLICATE-ENTRY)
                }
                seen.Add(key);
                entries.Add(entry);
            }

            baseline.Entries = entries;
            return (baseline, faults);
        }

        /// <summary>
        /// Writes the baseline to <paramref name="path"/> via the house safe-write
        /// (.tmp + rename). Contents-only (no wrapper line), so <see cref="Load"/>
        /// reads it back directly.
        /// </summary>
        internal static void Write(AnalysisBaseline baseline, string path)
        {
            var node = new ConfigNode(BaselineFormat.RootNodeName);
            node.AddValue("baselineFormatVersion", baseline.BaselineFormatVersion.ToString(IC));
            node.AddValue("createdAtAnalyzerVersion", baseline.CreatedAtAnalyzerVersion ?? "");
            node.AddValue("subjectSchemaGeneration", baseline.SubjectSchemaGeneration.ToString(IC));
            node.AddValue("createdAtUtc", baseline.CreatedAtUtc ?? "");
            node.AddValue("reason", baseline.Reason ?? "");

            if (baseline.Entries != null)
            {
                foreach (BaselineEntry entry in baseline.Entries)
                {
                    ConfigNode en = node.AddNode("ENTRY");
                    en.AddValue("ruleId", entry.RuleId ?? "");
                    en.AddValue("target", entry.Target ?? "");
                    en.AddValue("sectionIndex", entry.SectionIndex.ToString(IC));
                    en.AddValue("messageDigest", entry.MessageDigest ?? "");
                    en.AddValue("capturedLevel", ReportWriter.LevelToken(entry.CapturedLevel));
                    en.AddValue("reason", entry.Reason ?? "");
                }
            }

            FileIOUtils.SafeWriteConfigNode(node, path, "Analyzer");
        }

        private static bool BracesBalanced(string text)
        {
            if (string.IsNullOrEmpty(text))
                return true;
            int depth = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth < 0) return false;
                }
            }
            return depth == 0;
        }

        private static int ParseInt(string value, int fallback)
        {
            if (!string.IsNullOrEmpty(value)
                && int.TryParse(value, NumberStyles.Integer, IC, out int parsed))
                return parsed;
            return fallback;
        }

        /// <summary>
        /// Inverse of <see cref="ReportWriter.LevelToken"/>, restricted to the three
        /// baseline-eligible levels. "STALE" is deliberately REJECTED: StaleFixture is
        /// excluded from the baseline scope entirely (the fresh-managed fixture scope
        /// is never baselinable), and because StaleFixture(3) &gt; Fail(2) a
        /// hand-authored STALE entry would be an un-escalatable maximum that silently
        /// baselines a matching FAIL and never trips BASELINE-SEVERITY-ESCALATED. So a
        /// "STALE" capturedLevel is treated as an unparsable level -&gt; ENTRY-MALFORMED
        /// (the entry is dropped, the FAIL stays unbaselined and gates). Unknown
        /// -&gt; false.
        /// </summary>
        private static bool TryParseLevel(string token, out VerdictLevel level)
        {
            switch (token)
            {
                case "FAIL": level = VerdictLevel.Fail; return true;
                case "WARN": level = VerdictLevel.Warn; return true;
                case "INFO": level = VerdictLevel.Info; return true;
                default: level = VerdictLevel.Info; return false;
            }
        }
    }
}
