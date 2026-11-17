using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.InGameTests.Spikes
{
    // R0 spike branch only. Spike S9: which stock debug actions a mod can observe.
    // Pure parts (xUnit-tested): the line and CSV formats, the cheat scope stack, the
    // poll diff. The live side is SpikeS9Cheats.cs; the hook table is SpikeS9Hooks.cs.
    //
    // One line kind per thing seen, each a [Spike.S9] log line and one row of
    // parsek-spike/S9/observed.csv:
    //   observed: a hooked debug entry point ran (logged on entry, once per call).
    //   event:    a stock GameEvent fired, tagged with the debug scope it fired in (or none).
    //   poll:     a polled debug switch changed between two 1 Hz polls.
    //   rebaseline / baseline / inventory: bookkeeping, never counted as an observation.
    internal static class SpikeS9Format
    {
        internal const string SpikeId = "S9";
        internal const string CsvName = "observed";
        internal const string NoScope = "none";

        internal const string KindObserved = "observed";
        internal const string KindEvent = "event";
        internal const string KindPoll = "poll";
        internal const string KindBaseline = "baseline";
        internal const string KindRebaseline = "rebaseline";
        internal const string KindInventory = "inventory";

        internal static readonly string[] CsvHeader =
        {
            "session", "seq", "frame", "realtime_s", "ut", "scene", "kind",
            "tool", "layer", "entry", "scope", "depth", "detail",
        };

        /// <summary>
        /// The log message body (after the [Parsek][INFO][Spike.S9] prefix):
        /// "kind: seq=N k=v ... detail=free text". Values are made single-token
        /// (spaces become '_'); the detail runs to the end of the line.
        /// </summary>
        internal static string FormatLine(string kind, long seq, IList<KeyValuePair<string, string>> fields, string detail)
        {
            var sb = new StringBuilder();
            sb.Append(kind).Append(": seq=").Append(seq.ToString(CultureInfo.InvariantCulture));
            if (fields != null)
            {
                foreach (var kv in fields)
                    sb.Append(' ').Append(kv.Key).Append('=').Append(Token(kv.Value));
            }
            sb.Append(" detail=").Append(OneLine(detail));
            return sb.ToString();
        }

        /// <summary>A single-token value: null -> "-", empty -> "-", whitespace -> '_'.</summary>
        internal static string Token(string value)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
                sb.Append(char.IsWhiteSpace(c) ? '_' : c);
            return sb.ToString();
        }

        /// <summary>CR / LF folded to spaces, null -> "".</summary>
        internal static string OneLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        }

        /// <summary>Argument values for a log line: "(a, b)", strings quoted, invariant numbers.</summary>
        internal static string FormatArgs(object[] args)
        {
            if (args == null || args.Length == 0) return "()";
            var parts = new string[args.Length];
            for (int i = 0; i < args.Length; i++) parts[i] = FormatArg(args[i]);
            return "(" + string.Join(", ", parts) + ")";
        }

        internal static string FormatArg(object value)
        {
            if (value == null) return "null";
            if (value is string s) return "\"" + s + "\"";
            if (value is bool b) return b ? "true" : "false";
            if (value is double d) return d.ToString("R", CultureInfo.InvariantCulture);
            if (value is float f) return f.ToString("R", CultureInfo.InvariantCulture);
            if (value is Enum e) return e.ToString();
            if (value is IFormattable fmt) return fmt.ToString(null, CultureInfo.InvariantCulture);
            return "<" + value.GetType().Name + ">";
        }

        /// <summary>
        /// True when a currency change carries the stock cheat reason. Same rule as
        /// GameStateRecorder.IsCheatCurrencyReason (equality, not a flag test).
        /// </summary>
        internal static bool IsCheatingReason(TransactionReasons reason)
            => reason == TransactionReasons.Cheating;

        /// <summary>"OnRendezvousCommand" from a hook's method, "Type.Method" for an entry label.</summary>
        internal static string EntryLabel(string typeName, string methodName)
        {
            string t = typeName ?? "?";
            int dot = t.LastIndexOf('.');
            if (dot >= 0) t = t.Substring(dot + 1);
            int plus = t.LastIndexOf('+');
            if (plus >= 0) t = t.Substring(plus + 1);
            return t + "." + (methodName ?? "?");
        }

        /// <summary>One change between two poll snapshots.</summary>
        internal struct PollChange
        {
            internal string Key;
            internal string Old;
            internal string New;
        }

        /// <summary>
        /// Keys whose value differs between <paramref name="prev"/> and <paramref name="cur"/>,
        /// sorted by key (ordinal). A key present on one side only reads "absent" on the other.
        /// </summary>
        internal static List<PollChange> Diff(IDictionary<string, string> prev, IDictionary<string, string> cur)
        {
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            if (prev != null) foreach (var k in prev.Keys) keys.Add(k);
            if (cur != null) foreach (var k in cur.Keys) keys.Add(k);
            var changes = new List<PollChange>();
            foreach (string k in keys)
            {
                string a = null, b = null;
                bool hasA = prev != null && prev.TryGetValue(k, out a);
                bool hasB = cur != null && cur.TryGetValue(k, out b);
                string oldV = hasA ? (a ?? "null") : "absent";
                string newV = hasB ? (b ?? "null") : "absent";
                if (!string.Equals(oldV, newV, StringComparison.Ordinal))
                    changes.Add(new PollChange { Key = k, Old = oldV, New = newV });
            }
            return changes;
        }

        /// <summary>"k=v k=v" over a snapshot, sorted by key.</summary>
        internal static string FormatSnapshot(IDictionary<string, string> snap)
        {
            if (snap == null || snap.Count == 0) return "";
            var keys = new List<string>(snap.Keys);
            keys.Sort(StringComparer.Ordinal);
            var parts = new string[keys.Count];
            for (int i = 0; i < keys.Count; i++) parts[i] = keys[i] + "=" + Token(snap[keys[i]]);
            return string.Join(" ", parts);
        }

        /// <summary>"k:old->new,..." over a list of changes.</summary>
        internal static string FormatChanges(IList<PollChange> changes)
        {
            if (changes == null || changes.Count == 0) return "none";
            var parts = new string[changes.Count];
            for (int i = 0; i < changes.Count; i++)
                parts[i] = changes[i].Key + ":" + Token(changes[i].Old) + "->" + Token(changes[i].New);
            return string.Join(",", parts);
        }

        /// <summary>
        /// A poll that sees another Game instance or another loaded scene re-takes its
        /// baseline instead of reporting changes (a load or a scene switch resets stock
        /// state; those resets are not debug actions).
        /// </summary>
        internal static bool ShouldRebaseline(object prevGame, object curGame, string prevScene, string curScene)
            => !ReferenceEquals(prevGame, curGame) || !string.Equals(prevScene, curScene, StringComparison.Ordinal);
    }

    /// <summary>
    /// The debug scope: the stack of hooked entry points currently running. The root is
    /// the tool of the outermost one; an event that fires inside reads that root, so a
    /// milestone completed by Max Progression is told apart from one a flight completes.
    /// Keyed by an opaque method key so a finalizer pops only its own frame.
    /// </summary>
    internal sealed class SpikeS9Scope
    {
        private readonly List<KeyValuePair<object, string>> frames = new List<KeyValuePair<object, string>>();

        internal int Depth => frames.Count;

        internal string Root => frames.Count == 0 ? SpikeS9Format.NoScope : frames[0].Value;

        /// <summary>Pushes one frame; returns the new depth.</summary>
        internal int Push(object key, string tool)
        {
            frames.Add(new KeyValuePair<object, string>(key, tool ?? "?"));
            return frames.Count;
        }

        /// <summary>
        /// Pops the top frame when it belongs to <paramref name="key"/>; returns whether it did.
        /// A call that never pushed (a currency change without the cheat reason) pops nothing.
        /// </summary>
        internal bool PopIfTop(object key)
        {
            if (frames.Count == 0) return false;
            if (!Equals(frames[frames.Count - 1].Key, key)) return false;
            frames.RemoveAt(frames.Count - 1);
            return true;
        }

        internal void Clear() => frames.Clear();
    }
}
