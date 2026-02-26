using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Parsek.Tests.LogValidation
{
    internal static class ParsekKspLogParser
    {
        private static readonly Regex StructuredLinePattern = new Regex(
            @"\[Parsek\]\[(?<level>[^\]]+)\]\[(?<subsystem>[^\]]+)\]\s(?<message>.*)$",
            RegexOptions.Compiled);

        public static IReadOnlyList<KspLogEntry> ParseFile(string logPath)
        {
            if (string.IsNullOrWhiteSpace(logPath))
                throw new ArgumentException("Log path must be provided.", nameof(logPath));

            return ParseLines(File.ReadLines(logPath));
        }

        public static IReadOnlyList<KspLogEntry> ParseLines(IEnumerable<string> lines)
        {
            if (lines == null)
                throw new ArgumentNullException(nameof(lines));

            var entries = new List<KspLogEntry>();
            int lineNumber = 0;
            foreach (string line in lines)
            {
                lineNumber++;
                string rawLine = line ?? string.Empty;
                if (rawLine.IndexOf("[Parsek]", StringComparison.Ordinal) < 0)
                    continue;

                Match match = StructuredLinePattern.Match(rawLine);
                if (!match.Success)
                {
                    entries.Add(new KspLogEntry(
                        lineNumber: lineNumber,
                        rawLine: rawLine,
                        isStructured: false,
                        level: null,
                        subsystem: null,
                        message: null));
                    continue;
                }

                entries.Add(new KspLogEntry(
                    lineNumber: lineNumber,
                    rawLine: rawLine,
                    isStructured: true,
                    level: match.Groups["level"].Value,
                    subsystem: match.Groups["subsystem"].Value,
                    message: match.Groups["message"].Value));
            }

            return entries;
        }

        public static IReadOnlyList<KspLogEntry> SelectLatestSession(IReadOnlyList<KspLogEntry> parsekEntries)
        {
            if (parsekEntries == null)
                throw new ArgumentNullException(nameof(parsekEntries));

            int startIndex = -1;
            for (int i = 0; i < parsekEntries.Count; i++)
            {
                KspLogEntry entry = parsekEntries[i];
                if (!entry.IsStructured)
                    continue;

                if (!string.Equals(entry.Subsystem, "Init", StringComparison.Ordinal))
                    continue;

                if ((entry.Message ?? string.Empty).StartsWith("SessionStart runUtc=", StringComparison.Ordinal))
                    startIndex = i;
            }

            if (startIndex < 0)
                return parsekEntries;

            var latest = new List<KspLogEntry>(parsekEntries.Count - startIndex);
            for (int i = startIndex; i < parsekEntries.Count; i++)
                latest.Add(parsekEntries[i]);

            return latest;
        }
    }
}
