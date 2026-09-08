using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>Which handle family a <c>ListHandles</c> command enumerates.</summary>
    internal enum ListHandlesKind
    {
        /// <summary>The <c>kind=</c> arg did not parse (the command is a REJECTED).</summary>
        None = 0,

        /// <summary><c>kind=rewindpoints</c>: <c>ParsekScenario.RewindPoints</c> and their slots.</summary>
        RewindPoints = 1,

        /// <summary><c>kind=committed</c>: every recording in every committed tree.</summary>
        Committed = 2,

        /// <summary><c>kind=active</c>: the live flight tree and its background members.</summary>
        Active = 3,
    }

    /// <summary>One child slot of a rewind point, already resolved to open/closed.</summary>
    internal struct SlotRow
    {
        internal string OriginChildRecordingId;

        /// <summary>
        /// True iff the slot's EFFECTIVE tip resolves to a committed recording that is
        /// not <see cref="MergeState.Immutable"/>. Resolved by the applier (the walk
        /// needs the live store); the mapping itself is
        /// <see cref="TestCommandListHandles.SlotOpen"/>.
        /// </summary>
        internal bool Open;
    }

    /// <summary>One rewind point, in <c>ParsekScenario.RewindPoints</c> list order.</summary>
    internal struct RewindPointRow
    {
        internal string Id;
        internal double Ut;
        internal bool Provisional;
        internal bool Corrupted;

        /// <summary>Every slot the rewind point carries, in <c>ChildSlots</c> list order.
        /// The enumeration is capped; the emitted <c>slots=</c> value is this list's
        /// FULL length, so a reader sees what was cut.</summary>
        internal List<SlotRow> Slots;
    }

    /// <summary>One committed recording.</summary>
    internal struct CommittedRow
    {
        internal string RecordingId;
        internal string TreeId;
        internal uint Pid;
        /// <summary>The KSP-unique pid of the really-spawned vessel, 0 when none: the
        /// LIVE handle a switch onto a committed spawned clone needs (the craft-baked
        /// <see cref="Pid"/> is reused by every launch of the same craft).</summary>
        internal uint SpawnedPid;
        internal string Name;
        internal bool Spawned;
        internal MergeState State;
    }

    /// <summary>One background member of the live tree.</summary>
    internal struct BackgroundRow
    {
        internal uint Pid;
        internal string RecordingId;
    }

    /// <summary>The live flight tree, or the all-empty answer outside a live FLIGHT.</summary>
    internal struct ActiveRow
    {
        internal string TreeId;
        internal string ActiveRecordingId;
        internal uint ActivePid;

        /// <summary>Background members in whatever order the live map yielded them; the
        /// builder sorts by pid ascending. Null is read as empty.</summary>
        internal List<BackgroundRow> Background;
    }

    /// <summary>
    /// Pure decision half of the ADDITIVE <c>ListHandles kind=&lt;family&gt;</c> seam verb
    /// (R10 runtime-handle plumbing). The applier
    /// (<c>ParsekTestCommandAddon.ListHandles.cs</c>) owns the live walks and hands the
    /// rows here; the arg parse, the caps, the orderings and the key sequences live here
    /// so they are xUnit-covered.
    ///
    /// <para><b>Why the caps exist.</b> A seam response is a SINGLE wire line, so an
    /// unbounded family would produce an unbounded token list. Each builder therefore
    /// enumerates at most its cap and reports the untruncated total in <c>count</c> (in
    /// <c>bg</c> for the active family) plus <c>truncated=true</c>: the reader always
    /// learns that something was cut, and a cut is never silent.</para>
    ///
    /// <para><b>Why the orderings are pinned here.</b> A spec names a member by index
    /// (<c>${handles.rp0}</c>), so the index must mean the same thing on every run. A
    /// dictionary walk has no order contract, hence the ordinal sort of recording ids
    /// within each tree and the pid-ascending sort of the background members; the
    /// rewind-point and committed-tree list orders are the store's own append order and
    /// are preserved as-is.</para>
    /// </summary>
    internal static class TestCommandListHandles
    {
        /// <summary>No <c>kind=</c> arg was supplied. REQUIRED, so this is a REJECTED and
        /// never a default family: guessing one would make a typo'd spec read GREEN
        /// against a family it never asked for.</summary>
        internal const string KindArgMissingReason = "kind-arg-missing";

        /// <summary>A <c>kind=</c> value outside the closed set. Fail-closed and
        /// CASE-SENSITIVE, the <c>LoadGame scene=</c> rule.</summary>
        internal const string KindArgInvalidReason = "kind-arg-invalid";

        /// <summary>Wire literal for <see cref="ListHandlesKind.RewindPoints"/>.</summary>
        internal const string RewindPointsKindToken = "rewindpoints";

        /// <summary>Wire literal for <see cref="ListHandlesKind.Committed"/>.</summary>
        internal const string CommittedKindToken = "committed";

        /// <summary>Wire literal for <see cref="ListHandlesKind.Active"/>.</summary>
        internal const string ActiveKindToken = "active";

        /// <summary>Most rewind points enumerated in one response.</summary>
        internal const int MaxRewindPoints = 16;

        /// <summary>Most child slots enumerated per rewind point.</summary>
        internal const int MaxSlotsPerRewindPoint = 8;

        /// <summary>Most committed recordings enumerated in one response.</summary>
        internal const int MaxCommittedRecordings = 32;

        /// <summary>Most background members enumerated in one response.</summary>
        internal const int MaxBackgroundMembers = 16;

        /// <summary>
        /// Parses the REQUIRED <c>kind=</c> arg. Fail-closed and case-sensitive: only the
        /// three lowercase literals are accepted, so <c>Rewindpoints</c> / <c>RP</c> /
        /// an empty value are all <see cref="KindArgInvalidReason"/> rather than a
        /// tolerated spelling. Absent is its own reason so a spec author reads "you did
        /// not ask for a family" separately from "that is not a family".
        /// </summary>
        internal static bool ParseKind(string raw, out ListHandlesKind kind, out string rejectReason)
        {
            kind = ListHandlesKind.None;
            if (raw == null)
            {
                rejectReason = KindArgMissingReason;
                return false;
            }
            switch (raw)
            {
                case RewindPointsKindToken:
                    kind = ListHandlesKind.RewindPoints;
                    rejectReason = null;
                    return true;
                case CommittedKindToken:
                    kind = ListHandlesKind.Committed;
                    rejectReason = null;
                    return true;
                case ActiveKindToken:
                    kind = ListHandlesKind.Active;
                    rejectReason = null;
                    return true;
                default:
                    rejectReason = KindArgInvalidReason;
                    return false;
            }
        }

        /// <summary>The wire literal for a parsed kind (what the payload echoes back).</summary>
        internal static string KindToken(ListHandlesKind kind)
        {
            switch (kind)
            {
                case ListHandlesKind.RewindPoints: return RewindPointsKindToken;
                case ListHandlesKind.Committed: return CommittedKindToken;
                case ListHandlesKind.Active: return ActiveKindToken;
                default: return string.Empty;
            }
        }

        /// <summary>
        /// A slot is OPEN iff its effective tip resolved AND that tip is not
        /// <see cref="MergeState.Immutable"/>. An unresolvable tip reads closed: nothing
        /// could be invoked or sealed through it, so reporting it open would hand a spec
        /// a target that cannot be acted on.
        /// </summary>
        internal static bool SlotOpen(bool tipResolved, MergeState tipState)
            => tipResolved && tipState != MergeState.Immutable;

        // ----- payload builders -----
        //
        // Every builder emits `kind` first (the family the reader is looking at), then
        // the family's count + truncated pair, then the members. Values are
        // InvariantCulture; the response writer owns the percent-encoding, so nothing
        // here pre-encodes.

        /// <summary>
        /// <c>kind=rewindpoints count=&lt;n&gt; truncated=&lt;b&gt;</c> then, per enumerated
        /// rewind point, <c>rp&lt;i&gt; rp&lt;i&gt;ut rp&lt;i&gt;provisional rp&lt;i&gt;corrupted
        /// rp&lt;i&gt;slots</c> and, per enumerated slot, <c>rp&lt;i&gt;slot&lt;j&gt;
        /// rp&lt;i&gt;slot&lt;j&gt;open</c>. List order is preserved, so <c>rp&lt;count-1&gt;</c>
        /// is the newest.
        ///
        /// <para><c>truncated</c> is true when EITHER cap bit: more rewind points than
        /// <see cref="MaxRewindPoints"/>, or any enumerated rewind point carrying more
        /// slots than <see cref="MaxSlotsPerRewindPoint"/>. A slot cut does not show up in
        /// <c>count</c> (which counts rewind points), so without this the slot cap would
        /// be the one silent one.</para>
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildRewindPointsPayload(
            IReadOnlyList<RewindPointRow> rows)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            int total = rows != null ? rows.Count : 0;
            int shown = Math.Min(total, MaxRewindPoints);
            bool truncated = total > MaxRewindPoints;

            var body = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < shown; i++)
            {
                RewindPointRow row = rows[i];
                string prefix = "rp" + i.ToString(ic);
                int slotTotal = row.Slots != null ? row.Slots.Count : 0;
                int slotShown = Math.Min(slotTotal, MaxSlotsPerRewindPoint);
                if (slotTotal > MaxSlotsPerRewindPoint)
                    truncated = true;

                body.Add(Kv(prefix, row.Id ?? string.Empty));
                body.Add(Kv(prefix + "ut", row.Ut.ToString("R", ic)));
                body.Add(Kv(prefix + "provisional", Bool(row.Provisional)));
                body.Add(Kv(prefix + "corrupted", Bool(row.Corrupted)));
                body.Add(Kv(prefix + "slots", slotTotal.ToString(ic)));
                for (int j = 0; j < slotShown; j++)
                {
                    SlotRow slot = row.Slots[j];
                    string slotKey = prefix + "slot" + j.ToString(ic);
                    body.Add(Kv(slotKey, slot.OriginChildRecordingId ?? string.Empty));
                    body.Add(Kv(slotKey + "open", Bool(slot.Open)));
                }
            }

            var payload = new List<KeyValuePair<string, string>>
            {
                Kv("kind", RewindPointsKindToken),
                Kv("count", total.ToString(ic)),
                Kv("truncated", Bool(truncated)),
            };
            payload.AddRange(body);
            return payload;
        }

        /// <summary>
        /// <c>kind=committed count=&lt;n&gt; truncated=&lt;b&gt;</c> then, per enumerated
        /// recording, <c>rec&lt;i&gt; rec&lt;i&gt;tree rec&lt;i&gt;pid rec&lt;i&gt;spawnedPid
        /// rec&lt;i&gt;name rec&lt;i&gt;spawned rec&lt;i&gt;state</c>.
        ///
        /// <para><paramref name="treeGroups"/> is one group per committed tree IN TREE
        /// LIST ORDER; each group is sorted here by recording id (ordinal) because the
        /// live source is a dictionary and a dictionary walk has no order contract. A
        /// null group, and a null id inside one, sort first and emit empty rather than
        /// throwing: this verb is an observation and must never fail on shabby state.</para>
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildCommittedPayload(
            IReadOnlyList<IReadOnlyList<CommittedRow>> treeGroups)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            var ordered = new List<CommittedRow>();
            if (treeGroups != null)
            {
                for (int g = 0; g < treeGroups.Count; g++)
                {
                    IReadOnlyList<CommittedRow> group = treeGroups[g];
                    if (group == null || group.Count == 0)
                        continue;
                    var sorted = new List<CommittedRow>(group);
                    sorted.Sort((a, b) => string.CompareOrdinal(
                        a.RecordingId ?? string.Empty, b.RecordingId ?? string.Empty));
                    ordered.AddRange(sorted);
                }
            }

            int total = ordered.Count;
            int shown = Math.Min(total, MaxCommittedRecordings);

            var payload = new List<KeyValuePair<string, string>>
            {
                Kv("kind", CommittedKindToken),
                Kv("count", total.ToString(ic)),
                Kv("truncated", Bool(total > MaxCommittedRecordings)),
            };
            for (int i = 0; i < shown; i++)
            {
                CommittedRow row = ordered[i];
                string prefix = "rec" + i.ToString(ic);
                payload.Add(Kv(prefix, row.RecordingId ?? string.Empty));
                payload.Add(Kv(prefix + "tree", row.TreeId ?? string.Empty));
                payload.Add(Kv(prefix + "pid", row.Pid.ToString(ic)));
                payload.Add(Kv(prefix + "spawnedPid", row.SpawnedPid.ToString(ic)));
                payload.Add(Kv(prefix + "name", row.Name ?? string.Empty));
                payload.Add(Kv(prefix + "spawned", Bool(row.Spawned)));
                payload.Add(Kv(prefix + "state", row.State.ToString()));
            }
            return payload;
        }

        /// <summary>
        /// <c>kind=active tree=&lt;TreeId or empty&gt; activeRec=&lt;RecordingId or empty&gt;
        /// activePid=&lt;pid or 0&gt; bg=&lt;n&gt; truncated=&lt;b&gt;</c> then, per enumerated
        /// background member, <c>bg&lt;i&gt;pid bg&lt;i&gt;rec</c>, sorted by pid ascending.
        ///
        /// <para>The family's count key is <c>bg</c>, not <c>count</c>: the tree fields
        /// are scalars, so the only list here is the background map. Outside a live
        /// FLIGHT the caller hands a default <see cref="ActiveRow"/> and this is the
        /// empty-tree answer (<c>tree=</c> empty, <c>bg=0</c>) - never a defer and never a
        /// reject, since "no live tree" is a true observation, not a missing
        /// precondition.</para>
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildActivePayload(ActiveRow row)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            var members = row.Background != null
                ? new List<BackgroundRow>(row.Background)
                : new List<BackgroundRow>();
            members.Sort((a, b) => a.Pid.CompareTo(b.Pid));

            int total = members.Count;
            int shown = Math.Min(total, MaxBackgroundMembers);

            var payload = new List<KeyValuePair<string, string>>
            {
                Kv("kind", ActiveKindToken),
                Kv("tree", row.TreeId ?? string.Empty),
                Kv("activeRec", row.ActiveRecordingId ?? string.Empty),
                Kv("activePid", row.ActivePid.ToString(ic)),
                Kv("bg", total.ToString(ic)),
                Kv("truncated", Bool(total > MaxBackgroundMembers)),
            };
            for (int i = 0; i < shown; i++)
            {
                string prefix = "bg" + i.ToString(ic);
                payload.Add(Kv(prefix + "pid", members[i].Pid.ToString(ic)));
                payload.Add(Kv(prefix + "rec", members[i].RecordingId ?? string.Empty));
            }
            return payload;
        }

        /// <summary>
        /// The value the Info line and the payload agree on: the enumerated family's
        /// untruncated total. Read out of the built payload so the log line can never
        /// disagree with the wire (the two would otherwise be two independent counts).
        /// </summary>
        internal static string CountFromPayload(
            IReadOnlyList<KeyValuePair<string, string>> payload, ListHandlesKind kind)
        {
            string key = kind == ListHandlesKind.Active ? "bg" : "count";
            return ValueOrEmpty(payload, key);
        }

        /// <summary>The built payload's <c>truncated</c> value, for the same reason.</summary>
        internal static string TruncatedFromPayload(
            IReadOnlyList<KeyValuePair<string, string>> payload)
            => ValueOrEmpty(payload, "truncated");

        private static string ValueOrEmpty(
            IReadOnlyList<KeyValuePair<string, string>> payload, string key)
        {
            if (payload == null)
                return string.Empty;
            for (int i = 0; i < payload.Count; i++)
                if (payload[i].Key == key)
                    return payload[i].Value ?? string.Empty;
            return string.Empty;
        }

        private static KeyValuePair<string, string> Kv(string key, string value)
            => new KeyValuePair<string, string>(key, value);

        private static string Bool(bool b) => b ? "true" : "false";
    }
}
