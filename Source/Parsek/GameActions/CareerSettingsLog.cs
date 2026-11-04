using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// When the career's "Allow negative funds / science" setting changed, by capture number
    /// (career settings audit 2026-11-04 section 3.4). Stock applies the setting live: a loss
    /// stops at 0 while it is off. The ledger walk replays every past loss, so it judges each
    /// row under the setting that was in force when the row was captured, never under today's:
    /// flipping the switch later must not change a committed past (adding or removing the part
    /// stock forgave, which a toggle could farm). The pool WRITE keeps the live setting, which
    /// is what stock does from now on.
    ///
    /// <para>Entries are (first capture number, value). A row at or above an entry's number
    /// and below the next one's reads that entry; a row below the first entry (an older row,
    /// or one with no capture number) reads the first entry, the value when the log began. An
    /// empty log reads the live value, as before the log existed. Saved as additive keys in
    /// the LEDGER node, so the ledger version does not change.</para>
    /// </summary>
    internal static class CareerSettingsLog
    {
        internal const string Tag = "CareerSettings";
        internal const string AllowNegativeKey = "allowNegativeFrom";

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        internal struct Entry
        {
            internal long FromCaptureSequence;
            internal bool Value;
        }

        private static readonly List<Entry> allowNegative = new List<Entry>();

        internal static IReadOnlyList<Entry> AllowNegativeEntries => allowNegative;

        internal static void ResetForTesting()
        {
            allowNegative.Clear();
        }

        /// <summary>Pure: the setting a row captured at <paramref name="captureSequence"/>
        /// was judged under (see the class remarks); <paramref name="live"/> when there is no
        /// entry.</summary>
        internal static bool ValueAt(IList<Entry> entries, long captureSequence, bool live)
        {
            if (entries == null || entries.Count == 0)
                return live;
            bool value = entries[0].Value;
            for (int i = 1; i < entries.Count; i++)
            {
                if (entries[i].FromCaptureSequence <= captureSequence)
                    value = entries[i].Value;
                else
                    break;
            }
            return value;
        }

        /// <summary>The allow-negative setting a row was captured under.</summary>
        internal static bool AllowNegativeFor(GameAction row, bool live)
        {
            return ValueAt(allowNegative, row != null ? row.CaptureSequence : 0, live);
        }

        /// <summary>
        /// Pure: records <paramref name="live"/> from capture <paramref name="fromCaptureSequence"/>
        /// on when it differs from the last entry. A change at the same capture number replaces
        /// that entry (no row was captured in between), and drops it when the value is back to
        /// the one before. Returns true when the list changed.
        /// </summary>
        internal static bool Note(List<Entry> entries, bool live, long fromCaptureSequence)
        {
            if (entries == null)
                return false;
            if (entries.Count == 0)
            {
                entries.Add(new Entry { FromCaptureSequence = fromCaptureSequence, Value = live });
                return true;
            }
            Entry last = entries[entries.Count - 1];
            if (last.Value == live)
                return false;
            if (last.FromCaptureSequence >= fromCaptureSequence)
            {
                entries.RemoveAt(entries.Count - 1);
                if (entries.Count == 0 || entries[entries.Count - 1].Value != live)
                    entries.Add(new Entry { FromCaptureSequence = last.FromCaptureSequence, Value = live });
                return true;
            }
            entries.Add(new Entry { FromCaptureSequence = fromCaptureSequence, Value = live });
            return true;
        }

        /// <summary>
        /// Records the live setting when it changed since the last entry. Called when the stock
        /// difficulty dialog closes with its changes applied, after every ledger load, and at
        /// every recalculation (which catches a load that restored an older value: a rewind
        /// restores the save's own settings).
        /// </summary>
        internal static void NoteLive(bool live, string reason)
        {
            long from = CaptureSequence.CurrentHighWater() + 1;
            if (!Note(allowNegative, live, from))
                return;
            ParsekLog.Info(Tag,
                $"Allow negative currency is {(live ? "on" : "off")} from capture {from.ToString(IC)} " +
                $"({reason ?? "unknown"}; entries={allowNegative.Count.ToString(IC)}): rows captured from here on " +
                "are judged under it, earlier rows keep the setting they were captured under");
        }

        internal static void SaveInto(ConfigNode root)
        {
            if (root == null)
                return;
            for (int i = 0; i < allowNegative.Count; i++)
            {
                Entry e = allowNegative[i];
                root.AddValue(AllowNegativeKey, e.FromCaptureSequence.ToString(IC) + " " + (e.Value ? "1" : "0"));
            }
        }

        /// <summary>Replaces the log with the node's (absent: empty).</summary>
        internal static void LoadFrom(ConfigNode root, string sourceLabel)
        {
            allowNegative.Clear();
            if (root == null)
                return;
            int bad = 0;
            string[] values = root.GetValues(AllowNegativeKey);
            for (int i = 0; i < values.Length; i++)
            {
                string[] parts = (values[i] ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                long from;
                if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.Integer, IC, out from)
                    || (parts[1] != "0" && parts[1] != "1")
                    || (allowNegative.Count > 0 && from < allowNegative[allowNegative.Count - 1].FromCaptureSequence))
                {
                    bad++;
                    continue;
                }
                allowNegative.Add(new Entry { FromCaptureSequence = from, Value = parts[1] == "1" });
            }
            string line = $"Loaded career settings log from '{sourceLabel ?? "unknown"}': " +
                $"allowNegative entries={allowNegative.Count.ToString(IC)} bad={bad.ToString(IC)}";
            if (bad > 0)
                ParsekLog.Warn(Tag, line);
            else
                ParsekLog.Verbose(Tag, line);
        }
    }
}
