using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Contract twins stay out of the offer pool (design 3.15 step 8, owner ruling 2026-10-26).
    /// After a rewind, stock can generate the goal of a contract the committed future accepts
    /// again, under a new guid, before that accept's UT: as an offer, or as a contract the
    /// clock accepts on its own (<c>AutoAccept</c>). Such a twin is kept out.
    ///
    /// <para>The identity is stock's own uniqueness id, <c>Contract.ContractID</c> (a hash of the
    /// contract type and its and its parameters' hash strings), the value
    /// <c>ContractSystem.GenerateContract</c> already refuses a new offer on while a contract
    /// with it is offered or active (decompiled KSP 1.12.5). The rule only adds the committed
    /// future's accepts to that set, so it never keys on contract type and target: a contract
    /// is refused exactly where stock itself would refuse it with the committed contract
    /// active, and offered again once the committed accept is past.</para>
    ///
    /// <para>Scope: that id names the GOAL only where every hash string is goal-level, which in
    /// stock is the part test with <c>ONCEPERPART</c> or <c>BODYANDSITUATION</c> repeatability
    /// and the science collection contract (plus the Breaking Ground deployed-science and
    /// ROC science contracts, which hash subject id and body). Almost every other stock type (exploration, plant
    /// flag, grand tour, rescue, tourism, the FinePrint satellite / station / base / survey /
    /// ISRU / ARM / comet contracts, construction and repair, and a part test with
    /// <c>ALWAYS</c>) hashes <c>SystemUtilities.SuperSeed</c> (guid + mission seed) or a
    /// seeded unique id, so a regenerated copy carries a fresh id and is not caught. That gap
    /// is filed (todo CONTRACT-TWIN-GUID-SEEDED-TYPES) and needs an owner ruling, because any
    /// wider identity edges toward type-and-target keying.</para>
    ///
    /// <para>Positive evidence only: an accept row with no captured id (an older save) or a
    /// candidate with id 0 is never judged, and the committed contract itself (same guid) is
    /// never its own twin.</para>
    /// </summary>
    internal static class ContractTwinRule
    {
        internal const string Tag = "ContractTwin";

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// Pure: the committed future accept a contract with <paramref name="stockContractId"/>
        /// and <paramref name="contractGuid"/> would twin at <paramref name="nowUT"/>, or null.
        /// The earliest such accept in walk order when several exist.
        /// </summary>
        internal static CommittedFutureEntry FindTwinHolder(
            CommittedFutureIndex index, long stockContractId, string contractGuid, double nowUT)
        {
            if (index == null || stockContractId == 0)
                return null;
            List<CommittedFutureEntry> future = index.FutureEntriesOfKind(CommittedFutureKind.ContractAccept, nowUT);
            for (int i = 0; i < future.Count; i++)
            {
                CommittedFutureEntry e = future[i];
                if (e.StockContractId != stockContractId)
                    continue;
                if (!string.IsNullOrEmpty(contractGuid)
                    && string.Equals(e.Key, contractGuid, StringComparison.OrdinalIgnoreCase))
                    continue;
                return e;
            }
            return null;
        }

        /// <summary>Pure: one line naming the holder a twin is refused for.</summary>
        internal static string DescribeHolder(CommittedFutureEntry holder)
        {
            if (holder == null)
                return "(none)";
            return "'" + (holder.Title ?? holder.Key) + "' (guid=" + holder.Key
                + " stockId=" + holder.StockContractId.ToString(IC)
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
                CommittedFutureEntry holder = FindTwinHolder(index, c.StockContractId, c.Guid, nowUT);
                if (holder != null)
                    result.Add(new KeyValuePair<OfferedContract, CommittedFutureEntry>(c, holder));
            }
            return result;
        }

        /// <summary>
        /// The generation and auto-accept doors: true when a contract stock just generated (or
        /// is accepting on its own) twins a committed future accept, with one Info line naming
        /// both. False, silently, otherwise. Reads the shared committed-future index.
        /// </summary>
        internal static bool IsTwinNow(long stockContractId, string contractGuid, string title, string door,
            bool rateLimitPerHolder = false)
        {
            if (stockContractId == 0)
                return false;
            double nowUT = CommittedFutureIndexCache.CurrentUT();
            CommittedFutureEntry holder = FindTwinHolder(CommittedFutureIndexCache.Current, stockContractId, contractGuid, nowUT);
            if (holder == null)
                return false;
            string line = $"{door}: '{title ?? contractGuid}' (guid={contractGuid ?? "(none)"} " +
                $"stockId={stockContractId.ToString(IC)}) twins {DescribeHolder(holder)} at ut={nowUT.ToString("R", IC)} " +
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
