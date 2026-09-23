using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

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

        /// <summary><c>kind=chains</c>: the live flight scene's derived ghost chains
        /// (<c>ParsekFlight.ActiveGhostChains</c>).</summary>
        Chains = 4,
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

    /// <summary>One derived ghost chain, as the flight scene holds it.</summary>
    internal struct ChainRow
    {
        /// <summary>The claimed vessel's pid (the chain map's key).</summary>
        internal uint Pid;
        internal int Links;
        internal string TipRecordingId;
        internal double SpawnUt;
        internal bool Terminated;

        /// <summary>Each link's tree id, one entry per link in the chain's own link
        /// order. Null is read as empty. Only the DISTINCT set reaches the wire.</summary>
        internal List<string> LinkTreeIds;
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

        /// <summary>Wire literal for <see cref="ListHandlesKind.Chains"/>.</summary>
        internal const string ChainsKindToken = "chains";

        /// <summary>The OPTIONAL <c>expectDigest=</c> arg's key. Read only by
        /// <c>kind=chains</c>: the digest of an earlier capture, carried onto this
        /// step's wire by the harness's <c>${label.digest}</c> substitution, so the seam
        /// itself answers whether the two chain sets are identical.</summary>
        internal const string ExpectDigestArgKey = "expectDigest";

        /// <summary><c>expectDigest=</c> on a family other than <c>chains</c>: only the
        /// chains family computes a digest, so the arg would otherwise be silently
        /// ignored and a readback the author believes is compared would not be.</summary>
        internal const string ExpectDigestKindMismatchReason = "expect-digest-kind-mismatch";

        /// <summary><c>expectDigest=</c> that is not exactly eight lowercase hex digits
        /// (the digest's own wire shape). Covers an UNSUBSTITUTED <c>${...}</c> literal,
        /// which is what a handle that failed to resolve would put on the wire.</summary>
        internal const string ExpectDigestInvalidReason = "expect-digest-invalid";

        /// <summary>Per-chain key suffix for the number of DISTINCT tree ids among the
        /// chain's links (<c>chain&lt;i&gt;trees</c>). Two or more is a chain pooled by pid
        /// from links in different committed trees.</summary>
        internal const string ChainTreesKeySuffix = "trees";

        /// <summary>Per-chain key suffix for those distinct tree ids, ordinal-sorted and
        /// comma-joined (<c>chain&lt;i&gt;treeIds</c>).</summary>
        internal const string ChainTreeIdsKeySuffix = "treeIds";

        /// <summary>Prefix of the one Info line the applier writes per ENUMERATED chain,
        /// so a spec can pin a chain's tree set from the log (the wire alone is not in
        /// KSP.log). Separate from the summary line, whose pinned shape does not move.</summary>
        internal const string ChainLogLinePrefix = "listhandles chain";

        /// <summary>The FNV-1a 32-bit offset basis: the digest of the empty chain set.</summary>
        internal const uint DigestOffsetBasis = 2166136261u;

        private const uint DigestPrime = 16777619u;

        /// <summary>Most rewind points enumerated in one response.</summary>
        internal const int MaxRewindPoints = 16;

        /// <summary>Most child slots enumerated per rewind point.</summary>
        internal const int MaxSlotsPerRewindPoint = 8;

        /// <summary>Most committed recordings enumerated in one response.</summary>
        internal const int MaxCommittedRecordings = 32;

        /// <summary>Most background members enumerated in one response.</summary>
        internal const int MaxBackgroundMembers = 16;

        /// <summary>Most ghost chains enumerated in one response. The digest always
        /// covers EVERY chain, so a cut never hides a difference from the readback.</summary>
        internal const int MaxChains = 16;

        /// <summary>
        /// Parses the REQUIRED <c>kind=</c> arg. Fail-closed and case-sensitive: only the
        /// four lowercase literals are accepted, so <c>Rewindpoints</c> / <c>RP</c> /
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
                case ChainsKindToken:
                    kind = ListHandlesKind.Chains;
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
                case ListHandlesKind.Chains: return ChainsKindToken;
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

        /// <summary>
        /// Parses the OPTIONAL <c>expectDigest=</c> arg. Absent is fine (the capture is a
        /// plain read, <paramref name="expected"/> null). Present is accepted only on
        /// <c>kind=chains</c> and only as exactly eight lowercase hex digits; anything else
        /// is a REJECTED, fail-closed like <c>kind=</c>, because a comparison the seam
        /// silently skipped would read as a comparison that matched.
        /// </summary>
        internal static bool ParseExpectDigest(string raw, ListHandlesKind kind,
            out string expected, out string rejectReason)
        {
            expected = null;
            rejectReason = null;
            if (raw == null)
                return true;
            if (kind != ListHandlesKind.Chains)
            {
                rejectReason = ExpectDigestKindMismatchReason;
                return false;
            }
            if (!IsDigestShape(raw))
            {
                rejectReason = ExpectDigestInvalidReason;
                return false;
            }
            expected = raw;
            return true;
        }

        private static bool IsDigestShape(string raw)
        {
            if (raw.Length != 8)
                return false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!hex)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// The chain set's identity digest: FNV-1a 32 over the UTF-8 bytes of one
        /// canonical line per chain, <c>pid|links|tip|spawnUT(R)|terminated;</c>, in pid
        /// order, as eight lowercase hex digits. Pid order is taken here (a dictionary
        /// walk has no order contract), and the spawn UT is "R"-formatted so two chains
        /// that differ in the last bit of the UT still differ. The empty set digests to
        /// the offset basis, <c>811c9dc5</c>. Deterministic across processes by
        /// construction: no hash seed, no culture, no platform string hash.
        /// </summary>
        internal static string ChainsDigest(IReadOnlyList<ChainRow> rows)
        {
            List<ChainRow> sorted = SortChains(rows);
            CultureInfo ic = CultureInfo.InvariantCulture;
            uint hash = DigestOffsetBasis;
            for (int i = 0; i < sorted.Count; i++)
            {
                ChainRow row = sorted[i];
                string line = string.Concat(
                    row.Pid.ToString(ic), "|",
                    row.Links.ToString(ic), "|",
                    row.TipRecordingId ?? string.Empty, "|",
                    row.SpawnUt.ToString("R", ic), "|",
                    Bool(row.Terminated), ";");
                byte[] bytes = Encoding.UTF8.GetBytes(line);
                for (int b = 0; b < bytes.Length; b++)
                {
                    hash ^= bytes[b];
                    hash = unchecked(hash * DigestPrime);
                }
            }
            return hash.ToString("x8", ic);
        }

        private static List<ChainRow> SortChains(IReadOnlyList<ChainRow> rows)
        {
            var sorted = rows != null ? new List<ChainRow>(rows) : new List<ChainRow>();
            sorted.Sort((a, b) => a.Pid.CompareTo(b.Pid));
            return sorted;
        }

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
        /// <c>kind=chains count=&lt;n&gt; truncated=&lt;b&gt; evaluated=&lt;b&gt;
        /// digest=&lt;hex8&gt;</c>, then <c>expected=&lt;hex8&gt; match=&lt;b&gt;</c> when an
        /// <paramref name="expectedDigest"/> was supplied, then, per enumerated chain in
        /// pid order, <c>chain&lt;i&gt;pid chain&lt;i&gt;links chain&lt;i&gt;tip
        /// chain&lt;i&gt;spawnUT chain&lt;i&gt;terminated chain&lt;i&gt;trees
        /// chain&lt;i&gt;treeIds</c>. The two tree keys are NOT part of the digest, so a
        /// digest pinned before they existed still holds.
        ///
        /// <para><paramref name="evaluated"/> says whether the flight scene has run its
        /// ghost-chain evaluation at all since it was created. It is what separates "this
        /// scene derived an EMPTY chain set" from "this scene never derived one" (an
        /// <c>OnFlightReady</c> path that returns before the evaluation, or any scene
        /// other than FLIGHT, where the answer is the empty, unevaluated one). The digest
        /// covers every chain, not only the enumerated ones.</para>
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildChainsPayload(
            IReadOnlyList<ChainRow> rows, bool evaluated, string expectedDigest)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            List<ChainRow> sorted = SortChains(rows);
            int total = sorted.Count;
            int shown = Math.Min(total, MaxChains);
            string digest = ChainsDigest(sorted);

            var payload = new List<KeyValuePair<string, string>>
            {
                Kv("kind", ChainsKindToken),
                Kv("count", total.ToString(ic)),
                Kv("truncated", Bool(total > MaxChains)),
                Kv("evaluated", Bool(evaluated)),
                Kv("digest", digest),
            };
            if (expectedDigest != null)
            {
                payload.Add(Kv("expected", expectedDigest));
                payload.Add(Kv("match", Bool(string.Equals(
                    digest, expectedDigest, StringComparison.Ordinal))));
            }
            for (int i = 0; i < shown; i++)
            {
                ChainRow row = sorted[i];
                string prefix = "chain" + i.ToString(ic);
                payload.Add(Kv(prefix + "pid", row.Pid.ToString(ic)));
                payload.Add(Kv(prefix + "links", row.Links.ToString(ic)));
                payload.Add(Kv(prefix + "tip", row.TipRecordingId ?? string.Empty));
                payload.Add(Kv(prefix + "spawnUT", row.SpawnUt.ToString("R", ic)));
                payload.Add(Kv(prefix + "terminated", Bool(row.Terminated)));
                List<string> treeIds = DistinctTreeIds(row.LinkTreeIds);
                payload.Add(Kv(prefix + ChainTreesKeySuffix, treeIds.Count.ToString(ic)));
                payload.Add(Kv(prefix + ChainTreeIdsKeySuffix, string.Join(",", treeIds.ToArray())));
            }
            return payload;
        }

        /// <summary>
        /// The distinct, non-empty tree ids of a chain's links, ordinal-sorted so the
        /// joined value is the same on every run whatever order the walker kept.
        /// </summary>
        internal static List<string> DistinctTreeIds(IReadOnlyList<string> linkTreeIds)
        {
            var distinct = new List<string>();
            if (linkTreeIds == null)
                return distinct;
            for (int i = 0; i < linkTreeIds.Count; i++)
            {
                string id = linkTreeIds[i];
                if (string.IsNullOrEmpty(id) || distinct.Contains(id))
                    continue;
                distinct.Add(id);
            }
            distinct.Sort(StringComparer.Ordinal);
            return distinct;
        }

        /// <summary>
        /// One log line per enumerated chain, read out of the built payload (so the log
        /// cannot disagree with the wire): <c>listhandles chain index=&lt;i&gt; pid=..
        /// links=.. trees=.. treeIds=.. tip=..</c>. Empty for every other family. Bounded
        /// by <see cref="MaxChains"/>.
        /// </summary>
        internal static List<string> ChainLogLines(
            IReadOnlyList<KeyValuePair<string, string>> payload, ListHandlesKind kind)
        {
            var lines = new List<string>();
            if (kind != ListHandlesKind.Chains || payload == null)
                return lines;
            CultureInfo ic = CultureInfo.InvariantCulture;
            for (int i = 0; i < MaxChains; i++)
            {
                string prefix = "chain" + i.ToString(ic);
                string pid = ValueOrEmpty(payload, prefix + "pid");
                if (pid.Length == 0)
                    break;
                lines.Add(ChainLogLinePrefix
                    + " index=" + i.ToString(ic)
                    + " pid=" + pid
                    + " links=" + ValueOrEmpty(payload, prefix + "links")
                    + " " + ChainTreesKeySuffix + "=" + ValueOrEmpty(payload, prefix + ChainTreesKeySuffix)
                    + " " + ChainTreeIdsKeySuffix + "=" + ValueOrEmpty(payload, prefix + ChainTreeIdsKeySuffix)
                    + " tip=" + ValueOrEmpty(payload, prefix + "tip"));
            }
            return lines;
        }

        /// <summary>
        /// The chains family's extra Info-line tail, read out of the built payload for
        /// the reason <see cref="CountFromPayload"/> is: <c> evaluated=.. digest=..</c>,
        /// plus <c> expected=.. match=..</c> when the payload carries a comparison. Empty
        /// for every other family, so their pinned log lines do not move.
        /// </summary>
        internal static string ChainsLogTail(
            IReadOnlyList<KeyValuePair<string, string>> payload, ListHandlesKind kind)
        {
            if (kind != ListHandlesKind.Chains)
                return string.Empty;
            string tail = " evaluated=" + ValueOrEmpty(payload, "evaluated")
                + " digest=" + ValueOrEmpty(payload, "digest");
            string expected = ValueOrEmpty(payload, "expected");
            if (expected.Length > 0)
                tail += " expected=" + expected + " match=" + ValueOrEmpty(payload, "match");
            return tail;
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
