using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Contract twins stay out of the offer pool (design 3.15 step 8, owner rulings 2026-10-26
    /// and 2026-10-27). After a rewind, stock can generate the goal of a contract the committed
    /// future accepts again, under a new guid, before that accept's UT: as an offer, or as a
    /// contract the clock accepts on its own (<c>AutoAccept</c>). Such a twin is kept out.
    ///
    /// <para>A contract twins a committed future accept (UT strictly after the clock, another
    /// guid) when EITHER identity matches:</para>
    /// <list type="bullet">
    /// <item>stock's own uniqueness id, <c>Contract.ContractID</c> (a hash of the contract type
    /// and its and its parameters' hash strings), the value
    /// <c>ContractSystem.GenerateContract</c> already refuses a new offer on while a contract
    /// with it is offered or active (decompiled KSP 1.12.5). It names the goal for the part
    /// test with <c>ONCEPERPART</c> / <c>BODYANDSITUATION</c>, the science collection contract
    /// and the Breaking Ground science contracts;</item>
    /// <item>the goal key (<see cref="ContractGoalKey"/>), for the types whose ContractID is
    /// seeded by the guid but whose goal stock itself offers only once per career: the
    /// exploration contract (its progress milestones), the Significant plant flag (its body)
    /// and the comet sample (its comet). Compared only within one contract type; a shared
    /// token is a match.</item>
    /// </list>
    /// <para>Repeatable types (tourism, rescue, satellite, station, base, ISRU, ARM, survey,
    /// construction, repair, grand tour, the Trivial plant flag, part test <c>ALWAYS</c>) and
    /// Contract Configurator contracts carry no goal key, so a legitimate repeat is never
    /// blocked; the rule never keys on type and target alone.</para>
    ///
    /// <para>Positive evidence only: an accept row with neither identity captured (an older
    /// save) is never a holder, a candidate with id 0 and no goal key is never judged, and the
    /// committed contract itself (same guid) is never its own twin. The rule only blocks or
    /// withdraws offers; it never zeroes a ledger row, and with no committed future accept
    /// (a career that never rewinds) it changes nothing.</para>
    /// </summary>
    internal static class ContractTwinRule
    {
        internal const string Tag = "ContractTwin";

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>Which identity made a contract a twin.</summary>
        internal enum TwinIdentity
        {
            None,
            /// <summary>Stock's <c>Contract.ContractID</c>.</summary>
            StockId,
            /// <summary>The goal key (<see cref="ContractGoalKey"/>).</summary>
            Goal
        }

        /// <summary>
        /// Pure: the committed future accept a contract with <paramref name="stockContractId"/>
        /// and <paramref name="contractGuid"/> would twin at <paramref name="nowUT"/>, or null
        /// (stock id only).
        /// </summary>
        internal static CommittedFutureEntry FindTwinHolder(
            CommittedFutureIndex index, long stockContractId, string contractGuid, double nowUT)
        {
            TwinIdentity ignored;
            return FindTwinHolder(index, stockContractId, null, contractGuid, nowUT, out ignored);
        }

        /// <summary>
        /// Pure: the committed future accept a contract with <paramref name="stockContractId"/>,
        /// <paramref name="goalKey"/> and <paramref name="contractGuid"/> would twin at
        /// <paramref name="nowUT"/>, or null: the earliest future accept of another guid whose
        /// stock id equals the candidate's, or whose goal key overlaps the candidate's
        /// (<see cref="ContractGoalKey.Overlaps"/>). <paramref name="matched"/> names the
        /// identity that matched (the stock id when both do).
        /// </summary>
        internal static CommittedFutureEntry FindTwinHolder(
            CommittedFutureIndex index, long stockContractId, string goalKey, string contractGuid, double nowUT,
            out TwinIdentity matched)
        {
            matched = TwinIdentity.None;
            bool hasGoal = !string.IsNullOrEmpty(goalKey);
            if (index == null || (stockContractId == 0 && !hasGoal))
                return null;
            List<CommittedFutureEntry> future = index.FutureEntriesOfKind(CommittedFutureKind.ContractAccept, nowUT);
            for (int i = 0; i < future.Count; i++)
            {
                CommittedFutureEntry e = future[i];
                if (!string.IsNullOrEmpty(contractGuid)
                    && string.Equals(e.Key, contractGuid, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (stockContractId != 0 && e.StockContractId == stockContractId)
                {
                    matched = TwinIdentity.StockId;
                    return e;
                }
                if (hasGoal && ContractGoalKey.Overlaps(goalKey, e.GoalKey))
                {
                    matched = TwinIdentity.Goal;
                    return e;
                }
            }
            return null;
        }

        /// <summary>
        /// Pure: true when a committed future accept of another contract at
        /// <paramref name="nowUT"/> carries a goal key of <paramref name="contractTypeName"/>.
        /// The live doors read a candidate's goal key only then, so a career with no such
        /// accept (one that never rewinds) never pays for it.
        /// </summary>
        internal static bool AnyFutureGoalHolderOfType(CommittedFutureIndex index, string contractTypeName, double nowUT)
        {
            if (index == null || !ContractGoalKey.IsGoalType(contractTypeName))
                return false;
            List<CommittedFutureEntry> future = index.FutureEntriesOfKind(CommittedFutureKind.ContractAccept, nowUT);
            for (int i = 0; i < future.Count; i++)
                if (string.Equals(ContractGoalKey.TypeOf(future[i].GoalKey), contractTypeName, StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>Pure: the identity a twin matched, for its log line.</summary>
        internal static string DescribeMatch(TwinIdentity matched, CommittedFutureEntry holder)
        {
            switch (matched)
            {
                case TwinIdentity.StockId:
                    return "matched=stockId";
                case TwinIdentity.Goal:
                    return "matched=goal(" + (holder != null ? holder.GoalKey : "") + ")";
                default:
                    return "matched=none";
            }
        }

        /// <summary>Pure: one line naming the holder a twin is refused for.</summary>
        internal static string DescribeHolder(CommittedFutureEntry holder)
        {
            if (holder == null)
                return "(none)";
            return "'" + (holder.Title ?? holder.Key) + "' (guid=" + holder.Key
                + " stockId=" + holder.StockContractId.ToString(IC)
                + (holder.GoalKey != null ? " goal=" + holder.GoalKey : "")
                + ", accepted by the committed timeline at ut=" + holder.UT.ToString("R", IC)
                + (holder.RecordingName != null ? " in " + holder.RecordingName : "") + ")";
        }

        /// <summary>One Offered contract as the withdrawal pass sees it.</summary>
        internal struct OfferedContract
        {
            /// <summary>The contract's position in the caller's list.</summary>
            internal int Index;
            internal string Guid;
            internal long StockContractId;
            /// <summary>The goal key, or null (<see cref="ContractGoalKey"/>).</summary>
            internal string GoalKey;
            internal string Title;
            internal bool Offered;
        }

        /// <summary>
        /// Pure: the Offered contracts that are twins of a committed future accept at
        /// <paramref name="nowUT"/>, each with its holder, in input order.
        /// </summary>
        internal static List<KeyValuePair<OfferedContract, CommittedFutureEntry>> SelectOfferedTwins(
            IList<OfferedContract> contracts, CommittedFutureIndex index, double nowUT)
        {
            var result = new List<KeyValuePair<OfferedContract, CommittedFutureEntry>>();
            if (contracts == null || index == null)
                return result;
            for (int i = 0; i < contracts.Count; i++)
            {
                OfferedContract c = contracts[i];
                if (!c.Offered)
                    continue;
                TwinIdentity matched;
                CommittedFutureEntry holder = FindTwinHolder(index, c.StockContractId, c.GoalKey, c.Guid, nowUT, out matched);
                if (holder != null)
                    result.Add(new KeyValuePair<OfferedContract, CommittedFutureEntry>(c, holder));
            }
            return result;
        }

        /// <summary>Pure: the identity an offered twin matched its holder on (the stock id when
        /// both match), for the withdrawal's log line.</summary>
        internal static TwinIdentity MatchOf(OfferedContract c, CommittedFutureEntry holder)
        {
            if (holder == null)
                return TwinIdentity.None;
            if (c.StockContractId != 0 && c.StockContractId == holder.StockContractId)
                return TwinIdentity.StockId;
            return ContractGoalKey.Overlaps(c.GoalKey, holder.GoalKey) ? TwinIdentity.Goal : TwinIdentity.None;
        }

        /// <summary>
        /// The generation and auto-accept doors: true when a contract stock just generated (or
        /// is accepting on its own) twins a committed future accept, with one Info line naming
        /// both and the identity that matched. False, silently, otherwise. Reads the shared
        /// committed-future index. <paramref name="goalKey"/> is the candidate's goal key, read
        /// by the door only when <see cref="AnyFutureGoalHolderOfType"/> holds.
        /// </summary>
        internal static bool IsTwinNow(long stockContractId, string contractGuid, string title, string door,
            bool rateLimitPerHolder = false, string goalKey = null)
        {
            if (stockContractId == 0 && string.IsNullOrEmpty(goalKey))
                return false;
            double nowUT = CommittedFutureIndexCache.CurrentUT();
            TwinIdentity matched;
            CommittedFutureEntry holder = FindTwinHolder(CommittedFutureIndexCache.Current, stockContractId, goalKey,
                contractGuid, nowUT, out matched);
            if (holder == null)
                return false;
            string line = $"{door}: '{title ?? contractGuid}' (guid={contractGuid ?? "(none)"} " +
                $"stockId={stockContractId.ToString(IC)}{(string.IsNullOrEmpty(goalKey) ? "" : " goal=" + goalKey)}) " +
                $"twins {DescribeHolder(holder)} at ut={nowUT.ToString("R", IC)} {DescribeMatch(matched, holder)} " +
                "- kept out of the offer pool";
            // The generation door refuses one candidate per seed, so one offer pass can refuse
            // the same twin many times: one line per holder there.
            if (rateLimitPerHolder)
                ParsekLog.InfoRateLimited(Tag, "twin-" + door + "-" + holder.Key, line);
            else
                ParsekLog.Info(Tag, line);
            return true;
        }
    }
}
