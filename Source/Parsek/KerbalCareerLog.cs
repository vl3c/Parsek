using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek
{
    /// <summary>
    /// One archived career-log entry for a kerbal: the unit stock derives experience from.
    ///
    /// <para>
    /// Verified against the decompiled KSP 1.12.5 <c>Assembly-CSharp</c>: kerbal XP is NOT a
    /// stored number. <c>ProtoCrewMember.experience</c> is recomputed by
    /// <c>UpdateExperience()</c> from <c>KerbalRoster.CalculateExperience(careerLog)</c>,
    /// which walks <c>FlightLog.GetFlights()</c> — entries GROUPED BY their
    /// <c>flight</c> number, scored per group, with cross-group dedup of
    /// (type, target) pairs. The flight number is therefore load-bearing: the same
    /// (type, target) pair credited under a different flight scores differently.
    /// </para>
    ///
    /// <para>
    /// A rewind restores the roster from the rewind-point quicksave, which erases every
    /// career-log entry archived after it — including entries archived by flights the merge
    /// KEEPS. This is the unit Parsek records so those survivors can be re-asserted.
    /// </para>
    /// </summary>
    internal struct KerbalCareerLogEntry : IEquatable<KerbalCareerLogEntry>
    {
        /// <summary>Stock <c>FlightLog.Entry.flight</c> — the grouping key XP scoring uses.</summary>
        public int Flight;

        /// <summary>Stock <c>FlightLog.Entry.type</c> (e.g. <c>Orbit</c>, <c>Land</c>, <c>Flyby</c>).</summary>
        public string Type;

        /// <summary>Stock <c>FlightLog.Entry.target</c> — usually a body name; may be empty.</summary>
        public string Target;

        public KerbalCareerLogEntry(int flight, string type, string target)
        {
            Flight = flight;
            Type = type ?? string.Empty;
            Target = target ?? string.Empty;
        }

        public bool Equals(KerbalCareerLogEntry other)
        {
            return Flight == other.Flight
                && string.Equals(Type, other.Type, StringComparison.Ordinal)
                && string.Equals(Target, other.Target, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is KerbalCareerLogEntry && Equals((KerbalCareerLogEntry)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Flight;
                hash = (hash * 397) ^ (Type != null ? Type.GetHashCode() : 0);
                hash = (hash * 397) ^ (Target != null ? Target.GetHashCode() : 0);
                return hash;
            }
        }

        public override string ToString()
        {
            return Format(this);
        }

        /// <summary>
        /// Wire form of one entry: <c>flight,type,target</c> (target may be empty). Commas and
        /// pipes inside a value are escaped, because stock entry targets are body names that
        /// Parsek does not control and a mod could introduce either character.
        /// </summary>
        internal static string Format(KerbalCareerLogEntry entry)
        {
            return entry.Flight.ToString(CultureInfo.InvariantCulture)
                + "," + Escape(entry.Type)
                + "," + Escape(entry.Target);
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var sb = new StringBuilder(value.Length + 4);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case ',': sb.Append("\\c"); break;
                    case '|': sb.Append("\\p"); break;
                    case ';': sb.Append("\\s"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        private static string Unescape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.IndexOf('\\') < 0) return value;
            var sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c != '\\' || i + 1 >= value.Length)
                {
                    sb.Append(c);
                    continue;
                }
                char next = value[++i];
                switch (next)
                {
                    case '\\': sb.Append('\\'); break;
                    case 'c': sb.Append(','); break;
                    case 'p': sb.Append('|'); break;
                    case 's': sb.Append(';'); break;
                    default: sb.Append('\\').Append(next); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Wire form of a whole set: pipe-separated <see cref="Format"/> entries, in the order
        /// given. Null/empty produces the empty string.
        /// </summary>
        internal static string FormatSet(IReadOnlyList<KerbalCareerLogEntry> entries)
        {
            if (entries == null || entries.Count == 0) return string.Empty;
            var sb = new StringBuilder();
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) sb.Append('|');
                sb.Append(Format(entries[i]));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Parses the wire form back. Malformed segments are skipped rather than throwing —
        /// a single corrupt entry must not cost the kerbal every other entry in the row.
        /// </summary>
        internal static List<KerbalCareerLogEntry> ParseSet(string encoded)
        {
            var result = new List<KerbalCareerLogEntry>();
            if (string.IsNullOrEmpty(encoded)) return result;

            string[] segments = encoded.Split('|');
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];
                if (string.IsNullOrEmpty(segment)) continue;

                // Split into at most 3 on unescaped commas: flight, type, target.
                int firstComma = segment.IndexOf(',');
                if (firstComma < 0) continue;
                int secondComma = segment.IndexOf(',', firstComma + 1);
                if (secondComma < 0) continue;

                int flight;
                if (!int.TryParse(segment.Substring(0, firstComma),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out flight))
                {
                    continue;
                }

                string type = Unescape(segment.Substring(firstComma + 1, secondComma - firstComma - 1));
                string target = Unescape(segment.Substring(secondComma + 1));
                if (string.IsNullOrEmpty(type)) continue;

                result.Add(new KerbalCareerLogEntry(flight, type, target));
            }
            return result;
        }
    }

    /// <summary>
    /// Pure set-union accumulator for one kerbal's recorded career-log entries.
    ///
    /// <para>
    /// MONOTONE BY CONSTRUCTION: the only mutating operation is a union. That is what makes
    /// the roster re-assert safe to run on every recalc — it can only ever ADD entries the
    /// roster is missing, never remove ones the quicksave load legitimately took away.
    /// </para>
    /// </summary>
    internal sealed class KerbalCareerEntries
    {
        private readonly HashSet<KerbalCareerLogEntry> entries =
            new HashSet<KerbalCareerLogEntry>();

        internal int Count { get { return entries.Count; } }

        internal IEnumerable<KerbalCareerLogEntry> Entries { get { return entries; } }

        internal bool Contains(KerbalCareerLogEntry entry)
        {
            return entries.Contains(entry);
        }

        /// <summary>Unions <paramref name="incoming"/> in. Returns how many were new.</summary>
        internal int UnionWith(IEnumerable<KerbalCareerLogEntry> incoming)
        {
            if (incoming == null) return 0;
            int added = 0;
            foreach (var entry in incoming)
            {
                if (string.IsNullOrEmpty(entry.Type)) continue;
                if (entries.Add(entry)) added++;
            }
            return added;
        }

        /// <summary>
        /// A stable, deterministic ordering for logging and for the re-assert: by flight, then
        /// type, then target. Stock's XP scoring groups by flight and dedups by (type, target),
        /// so within a flight the order does not affect the result — but a stable order keeps
        /// logs diffable.
        /// </summary>
        internal List<KerbalCareerLogEntry> ToOrderedList()
        {
            var list = new List<KerbalCareerLogEntry>(entries);
            list.Sort((a, b) =>
            {
                int c = a.Flight.CompareTo(b.Flight);
                if (c != 0) return c;
                c = string.CompareOrdinal(a.Type, b.Type);
                if (c != 0) return c;
                return string.CompareOrdinal(a.Target, b.Target);
            });
            return list;
        }
    }
}
