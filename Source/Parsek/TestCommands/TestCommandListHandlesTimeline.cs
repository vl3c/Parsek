using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.TestCommands
{
    /// <summary>One tombstoned ledger row as the timeline read-back reports it.</summary>
    internal struct RetiredTimelineRow
    {
        internal GameActionType Type;
        internal double Ut;

        /// <summary>The key part of the row's legacy twin (milestone id, contract id,
        /// facility id, ...); empty when the row type has no legacy twin.</summary>
        internal string Subject;

        /// <summary>Milestone-store events that would draw a legacy row for this row's
        /// twin with no ledger row hiding them except this retired one.</summary>
        internal int TwinCopies;

        /// <summary>Legacy rows the built Timeline actually carries for this twin.</summary>
        internal int TwinRows;
    }

    /// <summary>The counts <c>ListHandles kind=timeline</c> reports.</summary>
    internal struct TimelineReadback
    {
        internal int Entries;
        internal int RecordingRows;
        internal int ActionRows;
        internal int LegacyRows;

        /// <summary>Ledger <c>MilestoneAchievement</c> rows (after milestone compaction).</summary>
        internal int MilestoneRows;

        /// <summary>Legacy rows drawn from a <c>MilestoneAchieved</c> event: the leftover
        /// "Milestone" text rows TIMELINE-PHANTOM-MILESTONE-AFTER-REFLY-OF-LATER-SEGMENT is
        /// about.</summary>
        internal int LegacyMilestoneRows;

        internal int Retired;
        internal int RetiredWithTwin;
        internal int RetiredTwinCopies;
        internal int RetiredTwinRows;

        /// <summary>Every retired row, in ledger order. Null is read as empty.</summary>
        internal List<RetiredTimelineRow> RetiredRows;
    }

    /// <summary>
    /// Pure half of <c>ListHandles kind=timeline</c>: counts the Timeline's rows by source
    /// and kind, and checks them against the tombstoned ledger rows. The applier builds
    /// the rows through <c>TimelineWindowUI.BuildFromLiveState</c> (the window's own
    /// inputs) and hands them here with the ELS, the retired rows and the milestone store.
    ///
    /// <para><b>The phantom pair.</b> <c>retiredTwinCopies</c> is computed from the
    /// milestone store alone, without the built rows: committed events that reach the
    /// legacy dedup (<see cref="TimelineBuilder.IsLegacyRowCandidate"/>), are the twin of a
    /// retired row, and are not the twin of any effective ELS row. Each of them is a legacy
    /// row only the retired-row dedup can hide. <c>retiredTwinRows</c> counts the legacy
    /// rows the built Timeline still carries for a retired twin. A save holding the
    /// precondition reads <c>retiredTwinCopies</c> &gt;= 1; the fix reads
    /// <c>retiredTwinRows=0</c>, and a build without it reads the two equal.</para>
    /// </summary>
    internal static class TestCommandTimelineReadback
    {
        /// <summary>Most retired rows listed in one response; the counts cover all.</summary>
        internal const int MaxRetiredRows = 16;

        /// <summary>Prefix of the summary Info line and of the per-retired-row lines.</summary>
        internal const string TimelineLogLinePrefix = "listhandles timeline";

        /// <summary>Word after the prefix on a per-retired-row line.</summary>
        internal const string RetiredLogLineWord = "retired";

        /// <summary>The summary line's keys, in the order it writes them.</summary>
        internal static readonly string[] TimelineLogLineKeys =
        {
            "count", "recordingRows", "actionRows", "legacyRows", "milestoneRows",
            "legacyMilestoneRows", "retired", "retiredWithTwin", "retiredTwinCopies",
            "retiredTwinRows",
        };

        /// <summary>Each per-retired-row line's keys after <c>index=</c>, in order (each is
        /// <c>retired&lt;i&gt;</c> + key in the payload).</summary>
        internal static readonly string[] RetiredLogLineKeys =
        {
            "type", "subject", "ut", "twinCopies", "twinRows",
        };

        internal static TimelineReadback Count(
            IReadOnlyList<TimelineEntry> entries,
            IReadOnlyList<GameAction> effectiveActions,
            IReadOnlyList<GameAction> retiredActions,
            IReadOnlyList<Milestone> milestones,
            Func<GameStateEvent, bool> isLegacyEventVisible)
        {
            var r = new TimelineReadback { RetiredRows = new List<RetiredTimelineRow>() };

            var effectiveKeys = new HashSet<string>(StringComparer.Ordinal);
            if (effectiveActions != null)
            {
                for (int i = 0; i < effectiveActions.Count; i++)
                {
                    GameAction a = effectiveActions[i];
                    if (a == null || !a.Effective) continue;
                    string key = TimelineBuilder.GetLegacyDuplicateKey(a);
                    if (key != null) effectiveKeys.Add(key);
                }
            }

            // Retired twin key -> index into RetiredRows (the first retired row with it).
            var retiredKeyIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var rowKeys = new List<string>();
            if (retiredActions != null)
            {
                for (int i = 0; i < retiredActions.Count; i++)
                {
                    GameAction a = retiredActions[i];
                    if (a == null) continue;
                    string key = TimelineBuilder.GetLegacyDuplicateKey(a);
                    r.Retired++;
                    if (key != null)
                    {
                        r.RetiredWithTwin++;
                        if (!retiredKeyIndex.ContainsKey(key))
                            retiredKeyIndex[key] = r.RetiredRows.Count;
                    }
                    r.RetiredRows.Add(new RetiredTimelineRow
                    {
                        Type = a.Type,
                        Ut = a.UT,
                        Subject = SubjectOfKey(key),
                    });
                    rowKeys.Add(key);
                }
            }

            var copiesByKey = new Dictionary<string, int>(StringComparer.Ordinal);
            if (milestones != null && retiredKeyIndex.Count > 0)
            {
                for (int i = 0; i < milestones.Count; i++)
                {
                    Milestone m = milestones[i];
                    if (m == null || !m.Committed || m.Events == null) continue;
                    for (int j = 0; j < m.Events.Count; j++)
                    {
                        GameStateEvent e = m.Events[j];
                        if (!TimelineBuilder.IsLegacyRowCandidate(e, isLegacyEventVisible)) continue;
                        string key = TimelineBuilder.GetLegacyEventKey(e);
                        if (!retiredKeyIndex.ContainsKey(key) || effectiveKeys.Contains(key)) continue;
                        r.RetiredTwinCopies++;
                        Increment(copiesByKey, key);
                    }
                }
            }

            var rowsByKey = new Dictionary<string, int>(StringComparer.Ordinal);
            if (entries != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    TimelineEntry e = entries[i];
                    if (e == null) continue;
                    r.Entries++;
                    switch (e.Source)
                    {
                        case TimelineSource.Recording: r.RecordingRows++; break;
                        case TimelineSource.GameAction: r.ActionRows++; break;
                        case TimelineSource.Legacy: r.LegacyRows++; break;
                    }
                    if (e.Type == TimelineEntryType.MilestoneAchievement)
                        r.MilestoneRows++;
                    if (e.Source != TimelineSource.Legacy)
                        continue;
                    if (e.LegacyEventType == GameStateEventType.MilestoneAchieved)
                        r.LegacyMilestoneRows++;
                    if (e.LegacyKey != null && retiredKeyIndex.ContainsKey(e.LegacyKey))
                    {
                        r.RetiredTwinRows++;
                        Increment(rowsByKey, e.LegacyKey);
                    }
                }
            }

            // Attribute the per-twin counts to the first retired row carrying that twin.
            for (int i = 0; i < r.RetiredRows.Count; i++)
            {
                string key = rowKeys[i];
                if (key == null || retiredKeyIndex[key] != i) continue;
                RetiredTimelineRow row = r.RetiredRows[i];
                int n;
                row.TwinCopies = copiesByKey.TryGetValue(key, out n) ? n : 0;
                row.TwinRows = rowsByKey.TryGetValue(key, out n) ? n : 0;
                r.RetiredRows[i] = row;
            }
            return r;
        }

        /// <summary>
        /// <c>kind=timeline count=&lt;n&gt; truncated=&lt;b&gt;</c>, the summary counts in
        /// <see cref="TimelineLogLineKeys"/> order after <c>count</c>, then per listed retired
        /// row <c>retired&lt;i&gt;type retired&lt;i&gt;subject retired&lt;i&gt;ut
        /// retired&lt;i&gt;twinCopies retired&lt;i&gt;twinRows</c>. <c>count</c> is the
        /// Timeline's row count; <c>truncated</c> is true when more than
        /// <see cref="MaxRetiredRows"/> retired rows exist (the counts still cover all).
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildPayload(TimelineReadback r)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            int retiredListed = r.RetiredRows != null ? r.RetiredRows.Count : 0;
            int shown = Math.Min(retiredListed, MaxRetiredRows);
            var payload = new List<KeyValuePair<string, string>>
            {
                Kv("kind", TestCommandListHandles.TimelineKindToken),
                Kv("count", r.Entries.ToString(ic)),
                Kv("truncated", retiredListed > MaxRetiredRows ? "true" : "false"),
                Kv("recordingRows", r.RecordingRows.ToString(ic)),
                Kv("actionRows", r.ActionRows.ToString(ic)),
                Kv("legacyRows", r.LegacyRows.ToString(ic)),
                Kv("milestoneRows", r.MilestoneRows.ToString(ic)),
                Kv("legacyMilestoneRows", r.LegacyMilestoneRows.ToString(ic)),
                Kv("retired", r.Retired.ToString(ic)),
                Kv("retiredWithTwin", r.RetiredWithTwin.ToString(ic)),
                Kv("retiredTwinCopies", r.RetiredTwinCopies.ToString(ic)),
                Kv("retiredTwinRows", r.RetiredTwinRows.ToString(ic)),
            };
            for (int i = 0; i < shown; i++)
            {
                RetiredTimelineRow row = r.RetiredRows[i];
                string prefix = RetiredLogLineWord + i.ToString(ic);
                payload.Add(Kv(prefix + "type", row.Type.ToString()));
                payload.Add(Kv(prefix + "subject", row.Subject ?? string.Empty));
                payload.Add(Kv(prefix + "ut", row.Ut.ToString("R", ic)));
                payload.Add(Kv(prefix + "twinCopies", row.TwinCopies.ToString(ic)));
                payload.Add(Kv(prefix + "twinRows", row.TwinRows.ToString(ic)));
            }
            return payload;
        }

        /// <summary>
        /// The Info lines, read out of the built payload so the log cannot disagree with
        /// the wire: <c>listhandles timeline count=.. recordingRows=.. ...</c> in
        /// <see cref="TimelineLogLineKeys"/> order, then one
        /// <c>listhandles timeline retired index=&lt;i&gt; type=.. subject=.. ut=..
        /// twinCopies=.. twinRows=..</c> per listed retired row. Empty for every other
        /// family.
        /// </summary>
        internal static List<string> LogLines(
            IReadOnlyList<KeyValuePair<string, string>> payload, ListHandlesKind kind)
        {
            var lines = new List<string>();
            if (kind != ListHandlesKind.Timeline || payload == null)
                return lines;
            CultureInfo ic = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(TimelineLogLinePrefix);
            for (int k = 0; k < TimelineLogLineKeys.Length; k++)
                sb.Append(' ').Append(TimelineLogLineKeys[k]).Append('=')
                  .Append(ValueOrEmpty(payload, TimelineLogLineKeys[k]));
            lines.Add(sb.ToString());
            for (int i = 0; i < MaxRetiredRows; i++)
            {
                string prefix = RetiredLogLineWord + i.ToString(ic);
                if (ValueOrNull(payload, prefix + "type") == null)
                    break;
                var row = new StringBuilder(TimelineLogLinePrefix);
                row.Append(' ').Append(RetiredLogLineWord).Append(" index=").Append(i.ToString(ic));
                for (int k = 0; k < RetiredLogLineKeys.Length; k++)
                    row.Append(' ').Append(RetiredLogLineKeys[k]).Append('=')
                       .Append(ValueOrEmpty(payload, prefix + RetiredLogLineKeys[k]));
                lines.Add(row.ToString());
            }
            return lines;
        }

        // The twin key is "<eventType>:<ut R>:<key>"; neither the int nor the "R" UT holds a
        // colon, so the subject is everything after the second one.
        private static string SubjectOfKey(string key)
        {
            if (key == null) return string.Empty;
            string[] parts = key.Split(new[] { ':' }, 3);
            return parts.Length == 3 ? parts[2] : string.Empty;
        }

        private static void Increment(Dictionary<string, int> counts, string key)
        {
            int n;
            counts[key] = counts.TryGetValue(key, out n) ? n + 1 : 1;
        }

        private static string ValueOrNull(IReadOnlyList<KeyValuePair<string, string>> payload, string key)
        {
            for (int i = 0; i < payload.Count; i++)
                if (payload[i].Key == key)
                    return payload[i].Value ?? string.Empty;
            return null;
        }

        private static string ValueOrEmpty(IReadOnlyList<KeyValuePair<string, string>> payload, string key)
            => ValueOrNull(payload, key) ?? string.Empty;

        private static KeyValuePair<string, string> Kv(string key, string value)
            => new KeyValuePair<string, string>(key, value);
    }
}
