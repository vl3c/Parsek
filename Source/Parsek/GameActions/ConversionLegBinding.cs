using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The cause a strategy conversion leg was captured for (design 3.15 step 7): the cause
    /// row's type, the currency stock's award paid in (the query's one input currency) and the
    /// cause row's identity key. Stored on the leg as <see cref="GameAction.ConversionCause"/>
    /// text, <c>Type|Pool|Key</c>.
    /// </summary>
    internal struct ConversionCause
    {
        internal GameActionType Type;
        internal StockPool SourcePool;
        internal string Key;

        internal string Format()
        {
            return Type.ToString() + "|" + SourcePool.ToString() + "|" + (Key ?? "");
        }

        /// <summary>Pure: parses <see cref="Format"/> text. False on null, a malformed text, an
        /// unknown type or pool, a type no leg can follow, or an empty key.</summary>
        internal static bool TryParse(string raw, out ConversionCause cause)
        {
            cause = default(ConversionCause);
            if (string.IsNullOrEmpty(raw))
                return false;
            int first = raw.IndexOf('|');
            if (first <= 0)
                return false;
            int second = raw.IndexOf('|', first + 1);
            if (second <= first + 1)
                return false;
            GameActionType type;
            StockPool pool;
            if (!Enum.TryParse(raw.Substring(0, first), false, out type)
                || !Enum.IsDefined(typeof(GameActionType), type)
                || !Enum.TryParse(raw.Substring(first + 1, second - first - 1), false, out pool)
                || !Enum.IsDefined(typeof(StockPool), pool))
                return false;
            string key = raw.Substring(second + 1);
            if (string.IsNullOrEmpty(key) || !IsCauseType(type))
                return false;
            cause = new ConversionCause { Type = type, SourcePool = pool, Key = key };
            return true;
        }

        /// <summary>Pure: a row type a strategy leg can follow (a stock award with a
        /// <see cref="ConversionCauseScope"/> around its payment, or a gross purchase / repair
        /// row whose discount the leg carries).</summary>
        internal static bool IsCauseType(GameActionType type)
        {
            switch (type)
            {
                case GameActionType.FundsSpending:
                case GameActionType.FacilityRepair:
                case GameActionType.MilestoneAchievement:
                case GameActionType.ContractComplete:
                case GameActionType.ContractParameterComplete:
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                case GameActionType.ScienceEarning:
                case GameActionType.ScienceLabEarning:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Pure: the identity key of a cause row, the same text the capture scope names: the
        /// qualified milestone id, the contract guid (with the parameter path for a parameter),
        /// the science subject id, the surveyed body, the purchased part or upgrade
        /// (<see cref="PartPurchaseKey"/> / <see cref="PartUpgradeKey"/>) or the repaired
        /// building. Null for any other row, a strategy leg's own <c>FundsSpending</c> among
        /// them.
        /// </summary>
        internal static string KeyOf(GameAction row)
        {
            if (row == null)
                return null;
            switch (row.Type)
            {
                case GameActionType.FundsSpending:
                    if (row.FundsSpendingSource == FundsSpendingSource.Other)
                        return PartPurchaseKey(row.DedupKey);
                    if (row.FundsSpendingSource == FundsSpendingSource.PartUpgrade)
                        return PartUpgradeKey(row.DedupKey);
                    return null;
                case GameActionType.FacilityRepair:
                    return Empty(row.FacilityId);
                case GameActionType.MilestoneAchievement:
                    return Empty(row.MilestoneId);
                case GameActionType.ContractComplete:
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                    return Empty(row.ContractId);
                case GameActionType.ContractParameterComplete:
                    return ParameterKey(row.ContractId, row.ContractParameterId);
                case GameActionType.ScienceEarning:
                    return Empty(row.SubjectId);
                case GameActionType.ScienceLabEarning:
                    return Empty(row.ScanBodyName);
                default:
                    return null;
            }
        }

        /// <summary>Pure: the key of contract parameter <paramref name="parameterId"/> of
        /// contract <paramref name="contractGuid"/>; null when either is missing.</summary>
        internal static string ParameterKey(string contractGuid, string parameterId)
        {
            if (string.IsNullOrEmpty(contractGuid) || string.IsNullOrEmpty(parameterId))
                return null;
            return contractGuid + "/" + parameterId;
        }

        /// <summary>Pure: the cause key of the purchase of part <paramref name="partName"/>
        /// (prefixed, so a part and an upgrade of one name never share a key); null when
        /// empty.</summary>
        internal static string PartPurchaseKey(string partName)
        {
            return string.IsNullOrEmpty(partName) ? null : "Part:" + partName;
        }

        /// <summary>Pure: the cause key of the purchase of part upgrade
        /// <paramref name="upgradeName"/>; null when empty.</summary>
        internal static string PartUpgradeKey(string upgradeName)
        {
            return string.IsNullOrEmpty(upgradeName) ? null : "PartUpgrade:" + upgradeName;
        }

        private static string Empty(string s)
        {
            return string.IsNullOrEmpty(s) ? null : s;
        }
    }

    /// <summary>
    /// The award stock is paying right now, opened by the Harmony scopes around stock's award
    /// methods (<c>Patches/ConversionCauseScopePatches.cs</c>) and read by the strategy
    /// conversion door inside stock's currency-modifier query (design 3.15 step 7). Only the
    /// innermost scope counts, and only for a query whose reason is one that award pays with:
    /// anything else is no positive evidence of a cause, so the leg stays as captured. Main
    /// thread only; a finalizer closes every scope it opened even when stock throws.
    /// </summary>
    internal static class ConversionCauseScope
    {
        private const string Tag = "ConversionCause";

        private struct Frame
        {
            internal GameActionType Type;
            internal string Key;
            internal string[] Reasons;
        }

        private static readonly List<Frame> stack = new List<Frame>();

        /// <summary>How many scopes are open.</summary>
        internal static int Depth => stack.Count;

        /// <summary>
        /// Opens a scope for an award of <paramref name="type"/> keyed <paramref name="key"/>,
        /// paid with one of <paramref name="reasons"/> (stock <c>TransactionReasons</c> names).
        /// Returns the token <see cref="Close"/> takes. An empty key still opens a scope, so an
        /// outer one cannot claim the inner award's legs; it resolves nothing.
        /// </summary>
        internal static int Open(GameActionType type, string key, params string[] reasons)
        {
            stack.Add(new Frame { Type = type, Key = key, Reasons = reasons ?? new string[0] });
            // Rate-limited per award type: a world record's node can pay on many frames in a row.
            ParsekLog.VerboseRateLimited(Tag, "open-" + type,
                $"Scope opened: {type} key='{key ?? "(none)"}' reasons=[{string.Join(",", reasons ?? new string[0])}] " +
                $"depth={stack.Count.ToString(CultureInfo.InvariantCulture)}");
            return stack.Count;
        }

        /// <summary>Closes the scope <see cref="Open"/> returned <paramref name="token"/> for,
        /// and any scope opened inside it that was left open. A token of 0 or below is a no-op.</summary>
        internal static void Close(int token)
        {
            if (token <= 0 || token > stack.Count)
                return;
            int leaked = stack.Count - token;
            stack.RemoveRange(token - 1, stack.Count - token + 1);
            if (leaked > 0)
                ParsekLog.Warn(Tag,
                    $"Scope closed with {leaked.ToString(CultureInfo.InvariantCulture)} inner scope(s) still open; closed them too");
        }

        internal static void ResetForTesting()
        {
            stack.Clear();
        }

        /// <summary>
        /// The cause of a conversion captured from a query with reason <paramref name="reason"/>
        /// and source currency <paramref name="sourcePool"/>, read from the innermost open
        /// scope. False when no scope is open, the innermost one has no key, or the reason is
        /// not one its award pays with.
        /// </summary>
        internal static bool TryResolve(string reason, StockPool sourcePool, out ConversionCause cause)
        {
            cause = default(ConversionCause);
            if (stack.Count == 0)
                return false;
            Frame top = stack[stack.Count - 1];
            if (!IsExpectedReason(top.Reasons, reason) || string.IsNullOrEmpty(top.Key))
                return false;
            cause = new ConversionCause { Type = top.Type, SourcePool = sourcePool, Key = top.Key };
            return true;
        }

        /// <summary>Pure: <paramref name="reason"/> is one of <paramref name="expected"/> (ordinal).</summary>
        internal static bool IsExpectedReason(string[] expected, string reason)
        {
            if (expected == null || string.IsNullOrEmpty(reason))
                return false;
            for (int i = 0; i < expected.Length; i++)
                if (string.Equals(expected[i], reason, StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>
        /// Pure: the source currency of a query: the one currency with a nonzero input. Stock's
        /// <c>AddFunds</c> / <c>AddScience</c> / <c>AddReputation</c> each run a query with one
        /// input, so an award paying in three currencies makes three queries. False for a query
        /// with no or several nonzero inputs (no single cause currency).
        /// </summary>
        internal static bool TryGetSourcePool(StrategyConversionQuery query, out StockPool pool)
        {
            pool = StockPool.Funds;
            int count = 0;
            if (query.InputFunds != 0.0) { pool = StockPool.Funds; count++; }
            if (query.InputScience != 0.0) { pool = StockPool.Science; count++; }
            if (query.InputReputation != 0.0) { pool = StockPool.Reputation; count++; }
            return count == 1;
        }
    }

    /// <summary>One conversion leg's binding and the walk's decision for it (design 3.15 step 7).</summary>
    internal sealed class ConversionLegDecision
    {
        internal GameAction Leg;
        internal GameAction Cause;
        internal float Share = 1f;
        internal float TakeBackFunds;
        internal float TakeBackScience;
        internal float TakeBackRep;
        internal string Outcome;

        internal bool Rescales => Share != 1f || TakeBackFunds != 0f || TakeBackScience != 0f || TakeBackRep != 0f;
    }

    /// <summary>
    /// Conversion legs follow their cause (design 3.15 step 7, owner ruling 2026-10-26). A
    /// strategy conversion leg that names its cause (<see cref="GameAction.ConversionCause"/>)
    /// is bound to the cause row of that type and key captured with it, and the walk scales
    /// the leg by the share of the cause it counted, in the cause's source currency: a cause
    /// the walk does not count (a duplicate award) converts nothing, a partly credited one
    /// (a world record, a capped science subject) converts its credited part. A leg with no
    /// named cause, or one whose cause row is missing or ambiguous, stays as captured: a mod
    /// may pay with a strategy reason, and nothing is taken back without positive evidence.
    ///
    /// <para>The decision needs the cause's walk outcome, and a leg is often captured (and
    /// walked) before its cause, so <see cref="RecalculationEngine"/> walks once, asks
    /// <see cref="Decide"/>, and walks again with <see cref="Install"/>'s shares only when a
    /// leg actually moves. In a career where every cause counts in full nothing moves and the
    /// walk is the old one, exactly.</para>
    ///
    /// <para>Take-back (step 5): a leg scaled down because its cause is an identity duplicate
    /// stock paid again after a rewind takes back the same fraction of what stock converted,
    /// the cause's own take-back fraction in its source currency, on an eligible leg only. A
    /// leg scaled for any other reason takes nothing back.</para>
    /// </summary>
    internal static class ConversionLegBinding
    {
        internal const string Tag = "ConversionLeg";

        /// <summary>The leg and its cause are captured in one stock call, one frame apart at
        /// most; a cause row further than this from the leg is not its cause.</summary>
        internal const double BindWindowSeconds = 1.0;

        /// <summary>A share within this of 1 is 1 (float noise on a fully counted cause).</summary>
        internal const float FullShareTolerance = 1e-4f;

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>Pure: a strategy conversion leg that names a cause.</summary>
        internal static bool IsCausedLeg(GameAction row)
        {
            return row != null && !string.IsNullOrEmpty(row.ConversionCause)
                && LedgerOrchestrator.IsStrategyConversionRow(row);
        }

        /// <summary>Pure: any row of <paramref name="actions"/> is a caused leg.</summary>
        internal static bool AnyCausedLeg(IList<GameAction> actions)
        {
            if (actions == null)
                return false;
            for (int i = 0; i < actions.Count; i++)
                if (IsCausedLeg(actions[i]))
                    return true;
            return false;
        }

        /// <summary>Pure: the pool a leg moves and its signed captured amount there.</summary>
        internal static bool TryGetLegAmount(GameAction leg, out StockPool pool, out float signedAmount)
        {
            pool = StockPool.Funds;
            signedAmount = 0f;
            if (leg == null)
                return false;
            switch (leg.Type)
            {
                case GameActionType.FundsEarning:
                    pool = StockPool.Funds; signedAmount = leg.FundsAwarded; return true;
                case GameActionType.FundsSpending:
                    pool = StockPool.Funds; signedAmount = -leg.FundsSpent; return true;
                case GameActionType.StrategyScienceCredit:
                    pool = StockPool.Science; signedAmount = leg.ScienceAwarded; return true;
                case GameActionType.StrategyScienceDebit:
                    pool = StockPool.Science; signedAmount = -leg.Cost; return true;
                case GameActionType.ReputationEarning:
                    pool = StockPool.Reputation; signedAmount = leg.NominalRep; return true;
                case GameActionType.ReputationPenalty:
                    pool = StockPool.Reputation; signedAmount = -leg.NominalPenalty; return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Pure: the magnitude stock's award paid in <paramref name="pool"/> for a cause row
        /// (the captured, gross amount; a penalty's magnitude). 0 when the row pays nothing
        /// there.
        /// </summary>
        internal static double CapturedAmount(GameAction cause, StockPool pool)
        {
            if (cause == null)
                return 0.0;
            switch (cause.Type)
            {
                case GameActionType.MilestoneAchievement:
                    return pool == StockPool.Funds ? cause.MilestoneFundsAwarded
                        : pool == StockPool.Science ? cause.MilestoneScienceAwarded
                        : cause.MilestoneRepAwarded;
                case GameActionType.ContractComplete:
                case GameActionType.ContractParameterComplete:
                    return pool == StockPool.Funds ? cause.FundsReward
                        : pool == StockPool.Science ? cause.ScienceReward
                        : cause.RepReward;
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                    return pool == StockPool.Funds ? cause.FundsPenalty
                        : pool == StockPool.Reputation ? cause.RepPenalty
                        : 0.0;
                case GameActionType.ScienceEarning:
                    return pool == StockPool.Science
                        ? (double)cause.ScienceAwarded * GameAction.NormalizeScienceGainMultiplier(cause.ScienceGainMultiplier)
                        : 0.0;
                case GameActionType.ScienceLabEarning:
                    return pool == StockPool.Science ? cause.ScienceAwarded : 0.0;
                case GameActionType.FundsSpending:
                    return pool == StockPool.Funds ? cause.FundsSpent : 0.0;
                case GameActionType.FacilityRepair:
                    return pool == StockPool.Funds ? cause.FacilityCost : 0.0;
                default:
                    return 0.0;
            }
        }

        /// <summary>
        /// Pure, over a cause row the walk has decided: the share of its stock award in
        /// <paramref name="pool"/> the walk counts, in [0, 1]. A row not counted is 0; a world
        /// record credited in part, its credited over its captured amount; a science subject,
        /// its effective over its captured pool value; anything else counted is 1. A share
        /// within <see cref="FullShareTolerance"/> of 1 is 1.
        /// </summary>
        internal static float CauseShare(GameAction cause, StockPool pool)
        {
            if (cause == null)
                return 1f;
            if (!cause.Effective)
                return 0f;
            double captured = CapturedAmount(cause, pool);
            double credited;
            switch (cause.Type)
            {
                case GameActionType.MilestoneAchievement:
                    if (!cause.RecordPartlyCredited)
                        return 1f;
                    if (captured > 0.0)
                    {
                        credited = pool == StockPool.Funds ? cause.CreditedMilestoneFunds
                            : pool == StockPool.Science ? cause.CreditedMilestoneScience
                            : cause.CreditedMilestoneRep;
                    }
                    else if (cause.RecordThresholdsTotal > 0)
                    {
                        // No amount in this currency recorded: the threshold count is the share.
                        captured = cause.RecordThresholdsTotal;
                        credited = cause.RecordThresholdsCredited;
                    }
                    else
                    {
                        return 1f;
                    }
                    break;
                case GameActionType.ScienceEarning:
                    if (!(captured > 0.0))
                        return 1f;
                    credited = cause.EffectiveScience;
                    break;
                default:
                    return 1f;
            }
            return Clamp01Snapped(credited / captured);
        }

        /// <summary>
        /// Pure, over a decided cause row: the fraction of its award in <paramref name="pool"/>
        /// stock paid again for an identity a rewind made it forget (the cause's own take-back
        /// over its captured amount), in [0, 1]. 0 when the cause takes nothing back there.
        /// </summary>
        internal static float CauseTakeBackFraction(GameAction cause, StockPool pool)
        {
            if (cause == null)
                return 0f;
            double takeBack = pool == StockPool.Funds ? cause.TakeBackFunds
                : pool == StockPool.Science ? cause.TakeBackScience
                : cause.TakeBackRep;
            if (takeBack == 0.0)
                return 0f;
            double captured = CapturedAmount(cause, pool);
            if (!(captured > 0.0))
                return 0f;
            double f = Math.Abs(takeBack) / captured;
            if (double.IsNaN(f) || f <= 0.0)
                return 0f;
            return f >= 1.0 ? 1f : (float)f;
        }

        private static float Clamp01Snapped(double share)
        {
            if (double.IsNaN(share) || double.IsInfinity(share))
                return 1f;
            if (share <= 0.0)
                return 0f;
            if (share >= 1.0 - FullShareTolerance)
                return 1f;
            return (float)share;
        }

        /// <summary>
        /// Pure: the cause row of <paramref name="leg"/> among <paramref name="candidates"/>
        /// (rows of the cause's type and key): the one captured nearest the leg within
        /// <see cref="BindWindowSeconds"/>, by capture number when both carry one, else by
        /// UT. A world record folds later breaks into one row, so for one with no row in the
        /// window the latest row of the same recording at or before the leg is its cause. Null
        /// when none qualifies or two qualify equally (no positive evidence).
        /// </summary>
        internal static GameAction FindCause(GameAction leg, ConversionCause cause, IList<GameAction> candidates,
            out string outcome)
        {
            outcome = "no-cause-row";
            if (leg == null || candidates == null || candidates.Count == 0)
                return null;
            GameAction best = null;
            double bestDistance = double.MaxValue;
            bool tie = false;
            for (int i = 0; i < candidates.Count; i++)
            {
                GameAction c = candidates[i];
                if (c == null || ReferenceEquals(c, leg))
                    continue;
                if (Math.Abs(c.UT - leg.UT) > BindWindowSeconds)
                    continue;
                double distance = c.CaptureSequence > 0 && leg.CaptureSequence > 0
                    ? Math.Abs((double)(c.CaptureSequence - leg.CaptureSequence))
                    : 1e12 + Math.Abs(c.UT - leg.UT);
                if (distance < bestDistance)
                {
                    best = c;
                    bestDistance = distance;
                    tie = false;
                }
                else if (distance == bestDistance)
                {
                    tie = true;
                }
            }
            if (best != null)
            {
                if (tie)
                {
                    outcome = "ambiguous-cause";
                    return null;
                }
                outcome = "bound";
                return best;
            }

            if (cause.Type == GameActionType.MilestoneAchievement
                && MilestonesModule.IsRepeatableWorldRecordMilestone(cause.Key))
            {
                GameAction folded = null;
                for (int i = 0; i < candidates.Count; i++)
                {
                    GameAction c = candidates[i];
                    if (c == null || c.UT > leg.UT + BindWindowSeconds)
                        continue;
                    if (!string.Equals(c.RecordingId ?? "", leg.RecordingId ?? "", StringComparison.Ordinal))
                        continue;
                    if (folded == null || c.UT > folded.UT)
                        folded = c;
                }
                if (folded != null)
                {
                    outcome = "bound-folded-record";
                    return folded;
                }
            }
            return null;
        }

        /// <summary>
        /// After a walk decided every cause row: binds each caused leg of
        /// <paramref name="sorted"/> to its cause and returns the decisions. Rows outside
        /// <paramref name="inputRows"/> (a module's injected rows) are never causes. Logs one
        /// summary line and one line per leg that moves.
        /// </summary>
        internal static List<ConversionLegDecision> Decide(IList<GameAction> sorted, ICollection<GameAction> inputRows)
        {
            var decisions = new List<ConversionLegDecision>();
            if (sorted == null)
                return decisions;

            Dictionary<string, List<GameAction>> causesByKey = null;
            int bound = 0, unbound = 0, rescaled = 0, takingBack = 0, malformed = 0;
            for (int i = 0; i < sorted.Count; i++)
            {
                GameAction leg = sorted[i];
                if (!IsCausedLeg(leg))
                    continue;
                ConversionCause cause;
                if (!ConversionCause.TryParse(leg.ConversionCause, out cause))
                {
                    malformed++;
                    ParsekLog.VerboseRateLimited(Tag, "malformed-" + (leg.ActionId ?? "?"),
                        $"Leg {leg.Type} id={leg.ActionId ?? "(none)"} has malformed cause '{leg.ConversionCause}' " +
                        "- applied as captured");
                    continue;
                }
                if (causesByKey == null)
                    causesByKey = IndexCauses(sorted, inputRows);

                List<GameAction> candidates;
                causesByKey.TryGetValue(IndexKey(cause.Type, cause.Key), out candidates);
                string outcome;
                GameAction causeRow = FindCause(leg, cause, candidates, out outcome);
                var d = new ConversionLegDecision { Leg = leg, Cause = causeRow, Outcome = outcome };
                decisions.Add(d);
                if (causeRow == null)
                {
                    unbound++;
                    // Every walk re-decides every leg: one line per leg and outcome.
                    ParsekLog.VerboseRateLimited(Tag, "unbound-" + (leg.ActionId ?? "?") + "-" + outcome,
                        $"Leg {leg.Type} id={leg.ActionId ?? "(none)"} ut={leg.UT.ToString("R", IC)} " +
                        $"cause={leg.ConversionCause} {outcome} - applied as captured");
                    continue;
                }
                bound++;
                d.Share = CauseShare(causeRow, cause.SourcePool);
                if (d.Share < 1f)
                {
                    float fraction = CauseTakeBackFraction(causeRow, cause.SourcePool);
                    if (fraction > 0f && StockShadowTakeBack.IsEligibleRow(leg))
                    {
                        StockPool legPool;
                        float signed;
                        if (TryGetLegAmount(leg, out legPool, out signed))
                        {
                            // Never more than the walk left out of the leg.
                            float part = Math.Min(fraction, 1f - d.Share);
                            float amount = signed * part;
                            if (legPool == StockPool.Funds) d.TakeBackFunds = amount;
                            else if (legPool == StockPool.Science) d.TakeBackScience = amount;
                            else d.TakeBackRep = amount;
                            takingBack++;
                        }
                    }
                }
                if (d.Rescales)
                {
                    rescaled++;
                    ParsekLog.Verbose(Tag,
                        $"Leg {leg.Type} id={leg.ActionId ?? "(none)"} ut={leg.UT.ToString("R", IC)} follows " +
                        $"{causeRow.Type} '{cause.Key}' (id={causeRow.ActionId ?? "(none)"} " +
                        $"counted={causeRow.Effective} reason={causeRow.NotCountedReason}): " +
                        $"share={d.Share.ToString("R", IC)} in {cause.SourcePool}" +
                        (d.TakeBackFunds != 0f || d.TakeBackScience != 0f || d.TakeBackRep != 0f
                            ? $" takeBack funds={d.TakeBackFunds.ToString("R", IC)} sci={d.TakeBackScience.ToString("R", IC)} " +
                              $"rep={d.TakeBackRep.ToString("R", IC)}"
                            : ""));
                }
            }

            if (decisions.Count > 0 || malformed > 0)
            {
                string counts = $"caused={(decisions.Count + malformed).ToString(IC)} bound={bound.ToString(IC)} " +
                    $"unbound={unbound.ToString(IC)} malformed={malformed.ToString(IC)} " +
                    $"rescaled={rescaled.ToString(IC)} takingBack={takingBack.ToString(IC)}";
                // Runs on every walk: the counts are in the key, so a changed result prints at once.
                ParsekLog.VerboseRateLimited(Tag, "summary-" + counts, "Conversion legs: " + counts);
            }
            return decisions;
        }

        /// <summary>Pure: some decision moves its leg, so the walk must run again.</summary>
        internal static bool NeedsRewalk(IList<ConversionLegDecision> decisions)
        {
            if (decisions == null)
                return false;
            for (int i = 0; i < decisions.Count; i++)
                if (decisions[i] != null && decisions[i].Rescales)
                    return true;
            return false;
        }

        /// <summary>
        /// Stamps the decisions on their legs, after the walk's derived-field reset and before
        /// its pre-pass: the share, the take-back amounts, and for a share of 0 the not-counted
        /// reason. A no-op for null.
        /// </summary>
        internal static void Install(IList<ConversionLegDecision> decisions)
        {
            if (decisions == null)
                return;
            for (int i = 0; i < decisions.Count; i++)
            {
                ConversionLegDecision d = decisions[i];
                if (d == null || d.Leg == null || !d.Rescales)
                    continue;
                GameAction leg = d.Leg;
                leg.ConversionShare = d.Share;
                if (d.Share == 0f)
                {
                    leg.Effective = false;
                    leg.NotCountedReason = GameActionNotCountedReason.ConversionCauseNotCounted;
                }
                if (d.TakeBackFunds != 0f || d.TakeBackScience != 0f || d.TakeBackRep != 0f)
                {
                    leg.TakeBackFunds = d.TakeBackFunds;
                    leg.TakeBackScience = d.TakeBackScience;
                    leg.TakeBackRep = d.TakeBackRep;
                    leg.TakeBackWitness = "the conversion of " + StockShadowTakeBack.DescribeIdentity(d.Cause)
                        + (string.IsNullOrEmpty(d.Cause.TakeBackWitness) ? "" : " (" + d.Cause.TakeBackWitness + ")");
                }
            }
        }

        private static Dictionary<string, List<GameAction>> IndexCauses(
            IList<GameAction> sorted, ICollection<GameAction> inputRows)
        {
            var index = new Dictionary<string, List<GameAction>>(StringComparer.Ordinal);
            for (int i = 0; i < sorted.Count; i++)
            {
                GameAction row = sorted[i];
                if (row == null || !ConversionCause.IsCauseType(row.Type))
                    continue;
                if (inputRows != null && !inputRows.Contains(row))
                    continue;
                string key = ConversionCause.KeyOf(row);
                if (key == null)
                    continue;
                string k = IndexKey(row.Type, key);
                List<GameAction> list;
                if (!index.TryGetValue(k, out list))
                {
                    list = new List<GameAction>();
                    index[k] = list;
                }
                list.Add(row);
            }
            return index;
        }

        private static string IndexKey(GameActionType type, string key)
        {
            return ((int)type).ToString(IC) + "|" + key;
        }
    }
}
