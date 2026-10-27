using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Whether a row the ledger replay leaves out may stay out of a Re-Fly seal (resources
    /// design section 3.15 item 10, owner ruling 2026-10-26; rewind design section 2.12).
    /// A row the replay counts seals, even at zero value (a first-reached milestone with no
    /// award). A row the replay does not count skips the seal only when BOTH hold:
    /// <list type="bullet">
    /// <item>its identity has a COUNTED holder row the timeline keeps (its reason names one:
    /// an earlier credit of the milestone, record threshold, subject or contract, the outcome
    /// that already ended the contract, the purchase that already owns the item), so a re-fly
    /// cannot move that credit without the replay netting it to one; and</item>
    /// <item>stock's payment of the row was prevented (it carries no captured amount) or the
    /// replay takes it back (the row carries a take-back, 3.15 step 5), so no payment of it is
    /// kept as an adjustment that sits in no retire set.</item>
    /// </list>
    /// Everything else seals, which only blocks a Re-Fly: a deadline expiry is no holder
    /// (<see cref="GameActionNotCountedReason.ContractDeadlinePassed"/>: stock's payment past
    /// the walk's deadline is kept, so a re-fly completing in time would be paid again); a
    /// repeat stock paid that the replay does not take back (below the take-back era, the
    /// same-tick explicit resolution, no timeline break) keeps its payment.
    ///
    /// <para>A purchase the walk already owns is the one spend that skips the seal while
    /// carrying an amount: what stock kept of it is a charge, the player's loss, never a
    /// credit a re-fly could collect twice, and the holder is the purchase that owns the
    /// item.</para>
    ///
    /// <para>Pure. The fields are read as a walk has JUST left them (the derived fields a
    /// walk resets and rewrites); a caller must never read them off a row some earlier walk
    /// touched, because a stale <c>Effective = false</c> would open a slot that holds a real
    /// counted gain. <see cref="RecalculationEngine.WalkBalancesOnClones"/> evaluates this
    /// right after its own walk.</para>
    /// </summary>
    internal static class ReplayCredit
    {
        /// <summary>Label for a science earning the subject cap left with nothing.</summary>
        internal const string SubjectCapReachedLabel = "ScienceSubjectCapReached";

        /// <summary>
        /// True for a reviewed reason whose not-counted row has a counted holder row the
        /// timeline keeps. False for <see cref="GameActionNotCountedReason.None"/>, for
        /// <see cref="GameActionNotCountedReason.ContractDeadlinePassed"/> (a deadline the
        /// walk synthesized is no holder, and stock's payment past it is kept),
        /// <see cref="GameActionNotCountedReason.FacilityAlreadyIntact"/> (an intact
        /// building has no holder row), and for any reason added later until it is reviewed
        /// into this list: sealing too much only blocks a Re-Fly, sealing too little could
        /// let a re-fly collect a credit twice.
        /// </summary>
        internal static bool IsNotCountedReason(GameActionNotCountedReason reason)
        {
            switch (reason)
            {
                case GameActionNotCountedReason.ContractAlreadyCompleted:
                case GameActionNotCountedReason.ContractAlreadyResolved:
                case GameActionNotCountedReason.ContractOutcomeAfterEnd:
                case GameActionNotCountedReason.MilestoneAlreadyAchieved:
                case GameActionNotCountedReason.ContractParameterAfterCompletion:
                case GameActionNotCountedReason.RecordThresholdsAlreadyCredited:
                case GameActionNotCountedReason.PurchaseAlreadyOwned:
                // A conversion leg follows its cause (3.15 item 7): it is taken back exactly when
                // its cause was taken back for an identity reason, so a settled leg's cause is
                // itself out of the seal or, with no holder (a passed deadline), seals the slot.
                case GameActionNotCountedReason.ConversionCauseNotCounted:
                // The body's first survey and the slice's earlier credit are the holders
                // (3.15 step 7); a repeat captured before its holder keeps stock's payment
                // (no take-back) and seals through IsStockPaymentSettled.
                case GameActionNotCountedReason.SurveyBodyAlreadyCredited:
                case GameActionNotCountedReason.ProducerSliceAlreadyCredited:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// True when the row carries an amount stock paid or charged at capture (any of the
        /// captured reward, award or penalty fields). A row with none was prevented: stock
        /// paid it nothing.
        /// </summary>
        internal static bool HasCapturedStockAmount(GameAction a)
        {
            if (a == null) return false;
            return a.FundsAwarded != 0f || a.FundsReward != 0f || a.FundsPenalty != 0f
                || a.RepReward != 0f || a.RepPenalty != 0f || a.NominalRep != 0f
                || a.ScienceAwarded != 0f || a.ScienceReward != 0f
                || a.MilestoneFundsAwarded != 0f || a.MilestoneRepAwarded != 0f
                || a.MilestoneScienceAwarded != 0f;
        }

        /// <summary>
        /// True when no payment of <paramref name="a"/> can be kept as an adjustment: stock
        /// paid it nothing, or the walk that just ran takes it back, or it is a purchase the
        /// walk already owns (see the class remarks).
        /// </summary>
        internal static bool IsStockPaymentSettled(GameAction a)
        {
            if (a == null) return false;
            if (a.NotCountedReason == GameActionNotCountedReason.PurchaseAlreadyOwned)
                return true;
            return !HasCapturedStockAmount(a) || a.HasTakeBack;
        }

        /// <summary>
        /// Why the walk that just ran left <paramref name="action"/> out of the seal, or null
        /// when the row seals. Out: <c>Effective</c> false with a holder reason
        /// (<see cref="IsNotCountedReason"/>) and a settled stock payment
        /// (<see cref="IsStockPaymentSettled"/>), or a science earning with a positive award
        /// whose subject cap left it nothing (its holder is the earlier credit of the
        /// subject) and a settled payment. The science case is read only when
        /// <paramref name="scienceWalked"/> (a science module took part in the walk): the
        /// walk resets the credited science to 0 before it runs, so without the module
        /// every science row would read as capped. A partly capped row counts.
        /// </summary>
        internal static string NotCountedLabel(GameAction action, bool scienceWalked)
        {
            if (action == null)
                return null;
            if (!action.Effective && IsNotCountedReason(action.NotCountedReason))
                return IsStockPaymentSettled(action) ? action.NotCountedReason.ToString() : null;
            if (scienceWalked
                && action.Type == GameActionType.ScienceEarning
                && action.ScienceAwarded > 0f
                && !(action.EffectiveScience > 0f))
                return IsStockPaymentSettled(action) ? SubjectCapReachedLabel : null;
            return null;
        }

        /// <summary>
        /// True when <paramref name="action"/> is NOT in <paramref name="notCounted"/> (by
        /// ActionId), the set a fresh walk produced. A row the walk never saw (no ActionId,
        /// absent from the set, or no set at all) seals: unknown is the safe side.
        /// </summary>
        internal static bool IsCounted(GameAction action, IReadOnlyDictionary<string, string> notCounted)
        {
            if (action == null)
                return false;
            if (notCounted == null || notCounted.Count == 0 || string.IsNullOrEmpty(action.ActionId))
                return true;
            return !notCounted.ContainsKey(action.ActionId);
        }
    }
}
