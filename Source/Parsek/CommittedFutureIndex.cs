using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Parsek
{
    /// <summary>
    /// What a committed-future row does to a stock item. One member per stock-screen
    /// block or annotation kind the reservation overlay program keys on
    /// (docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md, section 10).
    /// </summary>
    internal enum CommittedFutureKind
    {
        /// <summary><c>ScienceSpending</c> with a <c>NodeId</c>. Key: tech id.</summary>
        TechResearch,
        /// <summary><c>FacilityUpgrade</c>. Key: facility id (<c>SpaceCenter/LaunchPad</c>).</summary>
        FacilityUpgrade,
        /// <summary><c>ContractAccept</c>. Key: contract guid string.</summary>
        ContractAccept,
        /// <summary><c>ContractComplete</c>. Key: contract guid string.</summary>
        ContractComplete,
        /// <summary><c>ContractFail</c>. Key: contract guid string.</summary>
        ContractFail,
        /// <summary><c>ContractCancel</c>. Key: contract guid string.</summary>
        ContractCancel,
        /// <summary><c>KerbalHire</c>. Key: kerbal name.</summary>
        KerbalHire,
        /// <summary>A committed <c>CrewRemoved</c> milestone event. NOT a ledger row:
        /// the converter drops <c>CrewRemoved</c>, so this kind comes from the narrow
        /// milestone fallback in <see cref="CommittedFutureIndexCache"/>. Key: kerbal name.</summary>
        KerbalRetire,
        /// <summary><c>StrategyActivate</c>. Key: strategy id.</summary>
        StrategyActivate,
        /// <summary><c>StrategyDeactivate</c>. Key: strategy id.</summary>
        StrategyDeactivate,
        /// <summary><c>FundsSpending</c> with <c>FundsSpendingSource.Other</c> and a
        /// <c>DedupKey</c>: a part entry-cost purchase. Key: part name.</summary>
        PartPurchase
    }

    /// <summary>
    /// One committed row the index knows about. Immutable. <see cref="RecordingId"/> and
    /// <see cref="RecordingName"/> are null for a KSC-origin row.
    /// </summary>
    internal sealed class CommittedFutureEntry
    {
        internal readonly CommittedFutureKind Kind;
        internal readonly string Key;
        internal readonly double UT;
        internal readonly string RecordingId;
        internal readonly string RecordingName;
        /// <summary>FacilityUpgrade only: the level the upgrade reaches (1-based), else 0.</summary>
        internal readonly int FacilityToLevel;
        /// <summary>The row's cost: science for a tech, funds for a facility, hire or part.</summary>
        internal readonly float Amount;
        /// <summary>Contract rows: the recorded title, or null.</summary>
        internal readonly string Title;
        /// <summary>True for a row read from the milestone fallback, not the ledger.</summary>
        internal readonly bool FromMilestoneFallback;

        internal CommittedFutureEntry(
            CommittedFutureKind kind,
            string key,
            double ut,
            string recordingId,
            string recordingName,
            int facilityToLevel = 0,
            float amount = 0f,
            string title = null,
            bool fromMilestoneFallback = false)
        {
            Kind = kind;
            Key = key ?? "";
            UT = ut;
            RecordingId = string.IsNullOrEmpty(recordingId) ? null : recordingId;
            RecordingName = string.IsNullOrEmpty(recordingName) ? null : recordingName;
            FacilityToLevel = facilityToLevel;
            Amount = amount;
            Title = string.IsNullOrEmpty(title) ? null : title;
            FromMilestoneFallback = fromMilestoneFallback;
        }
    }

    /// <summary>
    /// One committed kerbal assignment (a <c>KerbalAssignment</c> row of a committed
    /// flight), kept so a reservation's explanation can name the flight that holds the
    /// kerbal. Not filtered by UT: a hold is about a flight that may have started long ago.
    /// </summary>
    internal sealed class CommittedKerbalAssignment
    {
        internal readonly string KerbalName;
        internal readonly string RecordingId;
        internal readonly string RecordingName;
        internal readonly KerbalEndState EndState;
        /// <summary>The flight's end, or NaN when the row carries none.</summary>
        internal readonly double EndUT;

        internal CommittedKerbalAssignment(
            string kerbalName, string recordingId, string recordingName,
            KerbalEndState endState, double endUT)
        {
            KerbalName = kerbalName ?? "";
            RecordingId = recordingId;
            RecordingName = string.IsNullOrEmpty(recordingName) ? null : recordingName;
            EndState = endState;
            EndUT = endUT;
        }
    }

    /// <summary>
    /// The UT-keyed committed-future index: every COMMITTED, EFFECTIVE row a stock-screen
    /// block or annotation keys on, grouped by (kind, key) and sorted by UT. Built once per
    /// ledger change by <see cref="CommittedFutureIndexCache"/>; the marks and the
    /// click-blocks read the SAME instance, so they cannot disagree.
    ///
    /// <para><b>Committed.</b> A row counts when it is in the effective ledger
    /// (<c>EffectiveState.ComputeELS</c>: not tombstoned) AND either has no recording
    /// (a KSC-origin row, written the moment the player acted) or belongs to a recording
    /// in the Effective Recording Set (<c>EffectiveState.ComputeERS</c>: committed, not
    /// <c>MergeState.NotCommitted</c>, not superseded, not session-suppressed). A row
    /// tagged to the live or pending tree, or to a Re-Fly provisional, is never a
    /// reservation: a block only protects committed history.</para>
    ///
    /// <para><b>Future.</b> A row is committed FUTURE when <c>row.UT &gt; currentUT</c>,
    /// strictly. A row AT the current UT is already applied: the ledger walk's cutoff is
    /// <c>UT &lt;= cutoff</c> (<c>RecalculationEngine.Recalculate</c>), so at that instant
    /// the item has already happened and nothing is blocked any more. Every query takes
    /// <c>currentUT</c> explicitly, so the index itself never goes stale as the clock
    /// moves; only a ledger change rebuilds it.</para>
    /// </summary>
    internal sealed class CommittedFutureIndex
    {
        private static readonly List<CommittedFutureEntry> NoEntries = new List<CommittedFutureEntry>();

        private readonly Dictionary<CommittedFutureKind, Dictionary<string, List<CommittedFutureEntry>>> byKind =
            new Dictionary<CommittedFutureKind, Dictionary<string, List<CommittedFutureEntry>>>();

        private readonly Dictionary<string, List<CommittedKerbalAssignment>> assignmentsByKerbal =
            new Dictionary<string, List<CommittedKerbalAssignment>>(StringComparer.Ordinal);

        internal static readonly CommittedFutureIndex Empty = new CommittedFutureIndex();

        /// <summary>Rows the build skipped because their recording is not committed.</summary>
        internal int SkippedUncommittedRows { get; private set; }

        /// <summary>Total indexed entries (all kinds, past and future).</summary>
        internal int EntryCount { get; private set; }

        private CommittedFutureIndex() { }

        /// <summary>
        /// True when a row at <paramref name="entryUT"/> is still ahead of
        /// <paramref name="currentUT"/>. The single definition of the boundary.
        /// </summary>
        internal static bool IsFuture(double entryUT, double currentUT)
        {
            return entryUT > currentUT;
        }

        /// <summary>
        /// Maps one ledger row to its index kind and key. False for a row no stock-screen
        /// kind keys on.
        /// </summary>
        internal static bool TryClassify(GameAction action, out CommittedFutureKind kind, out string key)
        {
            kind = default(CommittedFutureKind);
            key = null;
            if (action == null) return false;
            switch (action.Type)
            {
                case GameActionType.ScienceSpending:
                    kind = CommittedFutureKind.TechResearch;
                    key = action.NodeId;
                    break;
                case GameActionType.FacilityUpgrade:
                    kind = CommittedFutureKind.FacilityUpgrade;
                    key = action.FacilityId;
                    break;
                case GameActionType.ContractAccept:
                    kind = CommittedFutureKind.ContractAccept;
                    key = action.ContractId;
                    break;
                case GameActionType.ContractComplete:
                    kind = CommittedFutureKind.ContractComplete;
                    key = action.ContractId;
                    break;
                case GameActionType.ContractFail:
                    kind = CommittedFutureKind.ContractFail;
                    key = action.ContractId;
                    break;
                case GameActionType.ContractCancel:
                    kind = CommittedFutureKind.ContractCancel;
                    key = action.ContractId;
                    break;
                case GameActionType.KerbalHire:
                    kind = CommittedFutureKind.KerbalHire;
                    key = action.KerbalName;
                    break;
                case GameActionType.StrategyActivate:
                    kind = CommittedFutureKind.StrategyActivate;
                    key = action.StrategyId;
                    break;
                case GameActionType.StrategyDeactivate:
                    kind = CommittedFutureKind.StrategyDeactivate;
                    key = action.StrategyId;
                    break;
                case GameActionType.FundsSpending:
                    if (action.FundsSpendingSource != FundsSpendingSource.Other) return false;
                    kind = CommittedFutureKind.PartPurchase;
                    key = action.DedupKey;
                    break;
                default:
                    return false;
            }
            return !string.IsNullOrEmpty(key);
        }

        /// <summary>
        /// Builds the index. Pure: every live lookup comes in through a delegate.
        /// </summary>
        /// <param name="effectiveActions">The effective ledger (ELS).</param>
        /// <param name="isCommittedRecording">True when a recording id belongs to the
        /// committed timeline (ERS membership in production). Null treats every tagged row
        /// as uncommitted.</param>
        /// <param name="recordingName">The display name of a recording, or null.</param>
        /// <param name="fallbackEntries">Rows for kinds with no ledger representation
        /// (<see cref="CommittedFutureKind.KerbalRetire"/>), already committed-filtered.</param>
        internal static CommittedFutureIndex Build(
            IReadOnlyList<GameAction> effectiveActions,
            Func<string, bool> isCommittedRecording,
            Func<string, string> recordingName,
            IEnumerable<CommittedFutureEntry> fallbackEntries)
        {
            var index = new CommittedFutureIndex();
            var nameCache = new Dictionary<string, string>(StringComparer.Ordinal);
            int skippedUncommitted = 0;
            int entries = 0;

            if (effectiveActions != null)
            {
                for (int i = 0; i < effectiveActions.Count; i++)
                {
                    GameAction a = effectiveActions[i];
                    if (a == null) continue;

                    bool isAssignment = a.Type == GameActionType.KerbalAssignment;
                    CommittedFutureKind kind = default(CommittedFutureKind);
                    string key = null;
                    if (!isAssignment && !TryClassify(a, out kind, out key)) continue;

                    if (!string.IsNullOrEmpty(a.RecordingId)
                        && (isCommittedRecording == null || !isCommittedRecording(a.RecordingId)))
                    {
                        skippedUncommitted++;
                        continue;
                    }

                    string name = ResolveName(a.RecordingId, recordingName, nameCache);
                    if (isAssignment)
                    {
                        if (string.IsNullOrEmpty(a.KerbalName)) continue;
                        index.AddAssignment(new CommittedKerbalAssignment(
                            a.KerbalName, a.RecordingId, name, a.KerbalEndStateField,
                            float.IsNaN(a.EndUT) ? double.NaN : a.EndUT));
                        continue;
                    }

                    float amount;
                    switch (kind)
                    {
                        case CommittedFutureKind.TechResearch: amount = a.Cost; break;
                        case CommittedFutureKind.FacilityUpgrade: amount = a.FacilityCost; break;
                        case CommittedFutureKind.KerbalHire: amount = a.HireCost; break;
                        case CommittedFutureKind.PartPurchase: amount = a.FundsSpent; break;
                        case CommittedFutureKind.StrategyActivate: amount = a.SetupCost; break;
                        default: amount = 0f; break;
                    }
                    index.Add(new CommittedFutureEntry(
                        kind, key, a.UT, a.RecordingId, name,
                        facilityToLevel: kind == CommittedFutureKind.FacilityUpgrade ? a.ToLevel : 0,
                        amount: amount,
                        title: a.ContractTitle));
                    entries++;
                }
            }

            if (fallbackEntries != null)
            {
                foreach (var e in fallbackEntries)
                {
                    if (e == null || string.IsNullOrEmpty(e.Key)) continue;
                    index.Add(e);
                    entries++;
                }
            }

            foreach (var perKind in index.byKind.Values)
                foreach (var list in perKind.Values)
                    list.Sort((x, y) => x.UT.CompareTo(y.UT));

            index.SkippedUncommittedRows = skippedUncommitted;
            index.EntryCount = entries;
            return index;
        }

        private static string ResolveName(
            string recordingId, Func<string, string> recordingName, Dictionary<string, string> cache)
        {
            if (string.IsNullOrEmpty(recordingId) || recordingName == null) return null;
            string name;
            if (cache.TryGetValue(recordingId, out name)) return name;
            name = recordingName(recordingId);
            cache[recordingId] = name;
            return name;
        }

        private void Add(CommittedFutureEntry entry)
        {
            Dictionary<string, List<CommittedFutureEntry>> perKey;
            if (!byKind.TryGetValue(entry.Kind, out perKey))
            {
                perKey = new Dictionary<string, List<CommittedFutureEntry>>(StringComparer.Ordinal);
                byKind[entry.Kind] = perKey;
            }
            List<CommittedFutureEntry> list;
            if (!perKey.TryGetValue(entry.Key, out list))
            {
                list = new List<CommittedFutureEntry>();
                perKey[entry.Key] = list;
            }
            list.Add(entry);
        }

        private void AddAssignment(CommittedKerbalAssignment assignment)
        {
            List<CommittedKerbalAssignment> list;
            if (!assignmentsByKerbal.TryGetValue(assignment.KerbalName, out list))
            {
                list = new List<CommittedKerbalAssignment>();
                assignmentsByKerbal[assignment.KerbalName] = list;
            }
            list.Add(assignment);
        }

        private List<CommittedFutureEntry> EntriesOf(CommittedFutureKind kind, string key)
        {
            if (string.IsNullOrEmpty(key)) return NoEntries;
            Dictionary<string, List<CommittedFutureEntry>> perKey;
            List<CommittedFutureEntry> list;
            if (byKind.TryGetValue(kind, out perKey) && perKey.TryGetValue(key, out list))
                return list;
            return NoEntries;
        }

        /// <summary>Every committed row of this kind and key, past and future, UT ascending.</summary>
        internal IReadOnlyList<CommittedFutureEntry> AllEntries(CommittedFutureKind kind, string key)
        {
            return EntriesOf(kind, key);
        }

        /// <summary>Every committed row of this kind still ahead of
        /// <paramref name="currentUT"/>, across all keys, UT ascending.</summary>
        internal List<CommittedFutureEntry> FutureEntriesOfKind(CommittedFutureKind kind, double currentUT)
        {
            var result = new List<CommittedFutureEntry>();
            Dictionary<string, List<CommittedFutureEntry>> perKey;
            if (!byKind.TryGetValue(kind, out perKey)) return result;
            foreach (var list in perKey.Values)
                for (int i = 0; i < list.Count; i++)
                    if (IsFuture(list[i].UT, currentUT)) result.Add(list[i]);
            result.Sort((x, y) =>
            {
                int c = x.UT.CompareTo(y.UT);
                return c != 0 ? c : string.CompareOrdinal(x.Key, y.Key);
            });
            return result;
        }

        /// <summary>The committed rows of this kind and key still ahead of
        /// <paramref name="currentUT"/>, UT ascending.</summary>
        internal List<CommittedFutureEntry> FutureEntries(
            CommittedFutureKind kind, string key, double currentUT)
        {
            var result = new List<CommittedFutureEntry>();
            var list = EntriesOf(kind, key);
            for (int i = 0; i < list.Count; i++)
                if (IsFuture(list[i].UT, currentUT)) result.Add(list[i]);
            return result;
        }

        /// <summary>True when a committed row of this kind and key is still ahead.</summary>
        internal bool HasFuture(CommittedFutureKind kind, string key, double currentUT)
        {
            var list = EntriesOf(kind, key);
            // UT ascending, so the last entry is the latest.
            return list.Count > 0 && IsFuture(list[list.Count - 1].UT, currentUT);
        }

        /// <summary>The earliest committed row still ahead, or null.</summary>
        internal CommittedFutureEntry FirstFuture(CommittedFutureKind kind, string key, double currentUT)
        {
            var list = EntriesOf(kind, key);
            for (int i = 0; i < list.Count; i++)
                if (IsFuture(list[i].UT, currentUT)) return list[i];
            return null;
        }

        /// <summary>The latest committed row still ahead, or null. A block that keys on
        /// "any future row" lifts once the clock passes this one.</summary>
        internal CommittedFutureEntry LastFuture(CommittedFutureKind kind, string key, double currentUT)
        {
            var list = EntriesOf(kind, key);
            if (list.Count == 0) return null;
            var last = list[list.Count - 1];
            return IsFuture(last.UT, currentUT) ? last : null;
        }

        /// <summary>The keys of this kind that have at least one committed row ahead.</summary>
        internal HashSet<string> FutureKeys(CommittedFutureKind kind, double currentUT)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, List<CommittedFutureEntry>> perKey;
            if (!byKind.TryGetValue(kind, out perKey)) return result;
            foreach (var kvp in perKey)
            {
                var list = kvp.Value;
                if (list.Count > 0 && IsFuture(list[list.Count - 1].UT, currentUT))
                    result.Add(kvp.Key);
            }
            return result;
        }

        /// <summary>Number of rows of this kind, past and future.</summary>
        internal int CountOf(CommittedFutureKind kind)
        {
            Dictionary<string, List<CommittedFutureEntry>> perKey;
            if (!byKind.TryGetValue(kind, out perKey)) return 0;
            int n = 0;
            foreach (var list in perKey.Values) n += list.Count;
            return n;
        }

        /// <summary>Every committed kerbal assignment of this kerbal.</summary>
        internal IReadOnlyList<CommittedKerbalAssignment> AssignmentsOf(string kerbalName)
        {
            List<CommittedKerbalAssignment> list;
            if (!string.IsNullOrEmpty(kerbalName) && assignmentsByKerbal.TryGetValue(kerbalName, out list))
                return list;
            return new List<CommittedKerbalAssignment>();
        }

        /// <summary>Number of kerbal-assignment rows indexed.</summary>
        internal int AssignmentCount
        {
            get
            {
                int n = 0;
                foreach (var list in assignmentsByKerbal.Values) n += list.Count;
                return n;
            }
        }

        /// <summary>
        /// The committed flight that holds a reserved kerbal, mirroring
        /// <c>KerbalsPresentation.ResolveHoldFlight</c>: an open-ended hold is the latest
        /// flight that ends with the kerbal aboard (or with no recorded ending); otherwise,
        /// and as the fallback, the latest-ending flight. Null when no committed flight
        /// names the kerbal.
        /// </summary>
        internal CommittedKerbalAssignment ResolveHoldAssignment(string kerbalName, bool openEnded)
        {
            var list = AssignmentsOf(kerbalName);
            if (list.Count == 0) return null;
            CommittedKerbalAssignment latest = null;
            CommittedKerbalAssignment latestOpen = null;
            for (int i = 0; i < list.Count; i++)
            {
                var a = list[i];
                if (latest == null || EndKey(a) > EndKey(latest)) latest = a;
                if (a.EndState != KerbalEndState.Aboard && a.EndState != KerbalEndState.Unknown) continue;
                if (latestOpen == null || EndKey(a) > EndKey(latestOpen)) latestOpen = a;
            }
            return openEnded && latestOpen != null ? latestOpen : latest;
        }

        private static double EndKey(CommittedKerbalAssignment a)
        {
            return double.IsNaN(a.EndUT) ? double.NegativeInfinity : a.EndUT;
        }

        /// <summary>
        /// The per-kind counters the rebuild log line prints, e.g.
        /// <c>tech=2 facility=1 accept=1 complete=0 ...</c>.
        /// </summary>
        internal string DescribeCounts()
        {
            var ic = CultureInfo.InvariantCulture;
            return string.Format(ic,
                "tech={0} facility={1} accept={2} complete={3} fail={4} cancel={5} hire={6} " +
                "retire={7} strategyOn={8} strategyOff={9} part={10} assignments={11} skippedUncommitted={12}",
                CountOf(CommittedFutureKind.TechResearch),
                CountOf(CommittedFutureKind.FacilityUpgrade),
                CountOf(CommittedFutureKind.ContractAccept),
                CountOf(CommittedFutureKind.ContractComplete),
                CountOf(CommittedFutureKind.ContractFail),
                CountOf(CommittedFutureKind.ContractCancel),
                CountOf(CommittedFutureKind.KerbalHire),
                CountOf(CommittedFutureKind.KerbalRetire),
                CountOf(CommittedFutureKind.StrategyActivate),
                CountOf(CommittedFutureKind.StrategyDeactivate),
                CountOf(CommittedFutureKind.PartPurchase),
                AssignmentCount,
                SkippedUncommittedRows);
        }

    }

    /// <summary>
    /// The one live <see cref="CommittedFutureIndex"/>. Rebuilt lazily, never per row or
    /// per frame: <see cref="Current"/> returns the cached instance until one of its
    /// inputs changes.
    ///
    /// <para><b>Invalidation.</b> The cache keys on the IDENTITY of the effective ledger
    /// and effective recording lists (<c>EffectiveState</c> replaces each list on every
    /// rebuild, which happens exactly when <c>Ledger.StateVersion</c>, the tombstone
    /// version, <c>RecordingStore.StateVersion</c>, the supersede version or the Re-Fly
    /// marker change), plus a milestone fingerprint for the retire fallback and an
    /// explicit generation that <see cref="Invalidate"/> bumps. The stock overlay
    /// controller calls <see cref="Invalidate"/> from
    /// <c>LedgerOrchestrator.OnTimelineDataChanged</c>; the version keys make the cache
    /// correct even when that delegate was reset (its <c>ResetForTesting</c> nulls it) or
    /// no subscriber exists, so it cannot go stale across a reset.</para>
    /// </summary>
    internal static class CommittedFutureIndexCache
    {
        private const string Tag = "CommittedFutureIndex";
        private static readonly object syncRoot = new object();

        private static CommittedFutureIndex cached;
        private static object cachedEls;
        private static object cachedErs;
        private static long cachedMilestoneFingerprint = long.MinValue;
        private static int cachedGeneration = int.MinValue;
        private static int generation;

        /// <summary>How many times the index has been rebuilt (tests pin the cache).</summary>
        internal static int RebuildCount { get; private set; }

        /// <summary>Test seam for the "now" UT the patches and the overlay pass read.
        /// Null in production (Planetarium).</summary>
        internal static Func<double> NowUtProviderForTesting;

        /// <summary>The current index, rebuilt only when an input changed.</summary>
        internal static CommittedFutureIndex Current
        {
            get
            {
                IReadOnlyList<GameAction> els = EffectiveState.ComputeELS();
                IReadOnlyList<Recording> ers = EffectiveState.ComputeERS();
                long fingerprint = MilestoneFingerprint();
                lock (syncRoot)
                {
                    if (cached != null
                        && ReferenceEquals(cachedEls, els)
                        && ReferenceEquals(cachedErs, ers)
                        && cachedMilestoneFingerprint == fingerprint
                        && cachedGeneration == generation)
                    {
                        return cached;
                    }

                    var committedIds = new HashSet<string>(StringComparer.Ordinal);
                    if (ers != null)
                        for (int i = 0; i < ers.Count; i++)
                            if (ers[i] != null && !string.IsNullOrEmpty(ers[i].RecordingId))
                                committedIds.Add(ers[i].RecordingId);

                    Func<string, bool> isCommitted = id => committedIds.Contains(id);
                    var index = CommittedFutureIndex.Build(
                        els,
                        isCommitted,
                        ResolveRecordingDisplayName,
                        CollectRetireFallbackEntries(isCommitted, ResolveRecordingDisplayName));

                    cached = index;
                    cachedEls = els;
                    cachedErs = ers;
                    cachedMilestoneFingerprint = fingerprint;
                    cachedGeneration = generation;
                    RebuildCount++;

                    ParsekLog.Verbose(Tag,
                        "Rebuilt committed-future index: entries=" +
                        index.EntryCount.ToString(CultureInfo.InvariantCulture) +
                        " from els=" + (els != null ? els.Count : 0).ToString(CultureInfo.InvariantCulture) +
                        " ers=" + committedIds.Count.ToString(CultureInfo.InvariantCulture) +
                        " " + index.DescribeCounts());
                    return index;
                }
            }
        }

        /// <summary>
        /// Drops the cached index so the next <see cref="Current"/> rebuilds. Cheap; the
        /// rebuild itself is lazy.
        /// </summary>
        internal static void Invalidate(string reason)
        {
            lock (syncRoot)
            {
                unchecked { generation++; }
            }
            ParsekLog.Verbose(Tag, "Invalidated committed-future index: reason=" + (reason ?? "(none)"));
        }

        /// <summary>The UT every committed-future query compares against.</summary>
        internal static double CurrentUT()
        {
            var provider = NowUtProviderForTesting;
            if (provider != null) return provider();
            try
            {
                return ReadPlanetariumUT();
            }
            catch (Exception ex)
            {
                // Over-block rather than under-block: UT 0 treats every committed row as ahead.
                ParsekLog.WarnRateLimited(Tag, "now-ut-unavailable",
                    "CurrentUT: Planetarium unavailable, using UT 0 (" + ex.GetType().Name + ")");
                return 0.0;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double ReadPlanetariumUT()
        {
            return Planetarium.GetUniversalTime();
        }

        /// <summary>
        /// The display name of a committed flight: its mission name (the Kerbals window's
        /// flight naming), else the recording's vessel name, else null.
        /// </summary>
        internal static string ResolveRecordingDisplayName(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            Recording rec = LedgerOrchestrator.FindRecordingById(recordingId);
            if (rec == null) return null;
            Mission mission = !string.IsNullOrEmpty(rec.TreeId)
                ? MissionStore.FindOriginalMission(rec.TreeId)
                : null;
            if (mission != null && !string.IsNullOrEmpty(mission.Name)) return mission.Name;
            return string.IsNullOrEmpty(rec.VesselName) ? null : rec.VesselName;
        }

        /// <summary>
        /// NARROW FALLBACK for <see cref="CommittedFutureKind.KerbalRetire"/> only.
        /// A kerbal retirement (<c>CrewRemoved</c>) has no ledger representation: the event
        /// converter drops it (<c>GameStateEventConverter.ConvertEvent</c>), so the only
        /// committed record of it is the milestone event. This reads committed milestones'
        /// <c>CrewRemoved</c> events whose recording is committed (or null) and returns them
        /// as index entries. Unlike the old unreplayed slice it does NOT use
        /// <c>LastReplayedEventIndex</c>: the index's UT comparison decides "future", so the
        /// mark lifts as soon as the clock passes the retirement. Informational only; no
        /// click-block reads this kind. Todo: STOCK-UI-RESERVATION-OVERLAYS-2026-09-25
        /// (retirement has no ledger row).
        /// </summary>
        internal static List<CommittedFutureEntry> CollectRetireFallbackEntries(
            Func<string, bool> isCommittedRecording, Func<string, string> recordingName)
        {
            var result = new List<CommittedFutureEntry>();
            var milestones = MilestoneStore.Milestones;
            for (int i = 0; i < milestones.Count; i++)
            {
                var m = milestones[i];
                if (m == null || !m.Committed || m.Events == null) continue;
                for (int j = 0; j < m.Events.Count; j++)
                {
                    GameStateEvent ev = m.Events[j];
                    if (ev.eventType != GameStateEventType.CrewRemoved || string.IsNullOrEmpty(ev.key))
                        continue;
                    string rid = !string.IsNullOrEmpty(ev.recordingId) ? ev.recordingId : m.RecordingId;
                    if (!string.IsNullOrEmpty(rid)
                        && (isCommittedRecording == null || !isCommittedRecording(rid)))
                        continue;
                    string name = !string.IsNullOrEmpty(rid) && recordingName != null ? recordingName(rid) : null;
                    result.Add(new CommittedFutureEntry(
                        CommittedFutureKind.KerbalRetire, ev.key, ev.ut, rid, name,
                        fromMilestoneFallback: true));
                }
            }
            return result;
        }

        private static long MilestoneFingerprint()
        {
            var milestones = MilestoneStore.Milestones;
            long events = 0;
            long committed = 0;
            for (int i = 0; i < milestones.Count; i++)
            {
                var m = milestones[i];
                if (m == null) continue;
                if (m.Committed) committed++;
                if (m.Events != null) events += m.Events.Count;
            }
            return (milestones.Count * 1000003L) ^ (events * 7919L) ^ (committed * 31L);
        }

        internal static void ResetForTesting()
        {
            lock (syncRoot)
            {
                cached = null;
                cachedEls = null;
                cachedErs = null;
                cachedMilestoneFingerprint = long.MinValue;
                cachedGeneration = int.MinValue;
                generation = 0;
                RebuildCount = 0;
            }
            NowUtProviderForTesting = null;
        }
    }
}
