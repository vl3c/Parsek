using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Stock's active-crew count with each hold-plus-stand-in pair counted as the one seat
    /// it is (todo STAND-INS-EXCEED-THE-CREW-LIMIT, owner ruling 2026-09-26: option (a)).
    ///
    /// <para>Parsek generates a stand-in only because its timeline protection holds the
    /// seat's owner, so the player has ONE usable kerbal in that seat. Stock
    /// <c>KerbalRoster.GetActiveCrewCount</c> counts both, which inflates the Astronaut
    /// Complex header, engages its hire lock early and raises the next hire's cost. The
    /// postfix (<c>Patches.ActiveCrewCountPatch</c>) subtracts one per slot whose owner and
    /// ACTIVE stand-in are both counted by stock's own rule. A displaced or retired chain
    /// member, a slot whose owner is permanently gone (no active occupant), a slot whose
    /// owner is back in his own seat, and a pair either half of which stock does not count
    /// are left as stock counts them.</para>
    ///
    /// <para>The count is read by <c>AstronautComplex.UpdateCrewCounts</c> (header and
    /// hire lock), the editor auto-hire and its cost in <c>KerbalRoster</c>, and the int
    /// argument of <c>OnCrewmemberHired</c> / <c>OnCrewmemberSacked</c> /
    /// <c>OnCrewmemberLeftForDead</c>. Stock's <c>Funding.onCrewHired</c> charges
    /// <c>GetRecruitHireCost(arg)</c> and <c>GameStateRecorder.OnCrewmemberHired</c>
    /// records <c>ComputeHireCost(arg)</c> from the SAME argument, so the ledger's
    /// HireCost and stock's charge agree.</para>
    ///
    /// <para>No replay bypass: the count is a read-only view of the current roster, not a
    /// refusal, and a hire replayed while the ledger walks must price against the same
    /// seats a live hire would.</para>
    /// </summary>
    internal static class StandInSeatCount
    {
        internal const string Tag = "CrewCount";

        /// <summary>One slot whose owner and active stand-in share a seat.</summary>
        internal struct SeatPair
        {
            internal string Owner;
            internal string StandIn;
        }

        /// <summary>
        /// Stock's counting rule, mirrored from the decompiled
        /// <c>KerbalRoster.GetActiveCrewCount</c> (KSP 1.12.5): type Crew and roster status
        /// Assigned, Available or Missing (Dead is not counted; Applicant, Unowned and
        /// Tourist are not counted).
        /// </summary>
        internal static bool IsCountedByStock(
            ProtoCrewMember.KerbalType type, ProtoCrewMember.RosterStatus status)
        {
            if (type != ProtoCrewMember.KerbalType.Crew) return false;
            return status == ProtoCrewMember.RosterStatus.Assigned
                || status == ProtoCrewMember.RosterStatus.Available
                || status == ProtoCrewMember.RosterStatus.Missing;
        }

        /// <summary>
        /// The slot's active stand-in when the owner and that stand-in share one seat that
        /// stock counts twice, else null. The active occupant is
        /// <see cref="KerbalsModule.ResolveActiveChainIndex"/>, the rule the Kerbals window
        /// and <c>FindActiveStandInOwner</c> read.
        /// </summary>
        internal static string SeatSharingStandIn(
            KerbalsModule.KerbalSlot slot,
            Func<string, bool> isReserved,
            Func<string, bool> isStockCounted)
        {
            if (slot == null || slot.Chain == null || string.IsNullOrEmpty(slot.OwnerName))
                return null;
            if (isStockCounted == null)
                return null;
            int index = KerbalsModule.ResolveActiveChainIndex(slot.OwnerName, slot, isReserved);
            // ActiveOwnerIndex (owner in his own seat), NoActiveChainOccupant (owner
            // permanently gone) and Count (every chain member reserved) have no stand-in.
            if (index < 0 || index >= slot.Chain.Count)
                return null;
            string standIn = slot.Chain[index];
            if (string.IsNullOrEmpty(standIn)
                || string.Equals(standIn, slot.OwnerName, StringComparison.Ordinal))
                return null;
            if (!isStockCounted(slot.OwnerName) || !isStockCounted(standIn))
                return null;
            return standIn;
        }

        /// <summary>
        /// The seat-sharing pairs over every slot. A kerbal takes part in at most one pair,
        /// so every subtraction removes one of two distinct counted kerbals and the result
        /// can never drop below the number of seats. <paramref name="pairsOut"/> (optional)
        /// receives the pairs in slot order.
        /// </summary>
        internal static int CollectSeatSharingPairs(
            IEnumerable<KerbalsModule.KerbalSlot> slots,
            Func<string, bool> isReserved,
            Func<string, bool> isStockCounted,
            List<SeatPair> pairsOut)
        {
            if (slots == null) return 0;
            int pairs = 0;
            HashSet<string> paired = null;
            foreach (var slot in slots)
            {
                string standIn = SeatSharingStandIn(slot, isReserved, isStockCounted);
                if (standIn == null) continue;
                if (paired == null) paired = new HashSet<string>(StringComparer.Ordinal);
                if (paired.Contains(slot.OwnerName) || paired.Contains(standIn)) continue;
                paired.Add(slot.OwnerName);
                paired.Add(standIn);
                pairs++;
                if (pairsOut != null)
                    pairsOut.Add(new SeatPair { Owner = slot.OwnerName, StandIn = standIn });
            }
            return pairs;
        }

        /// <summary>Stock's raw count less one per seat-sharing pair, never below zero.</summary>
        internal static int ApplySeatSharing(int rawCount, int pairs)
        {
            if (pairs <= 0) return rawCount;
            int result = rawCount - pairs;
            return result < 0 ? 0 : result;
        }

        /// <summary>
        /// The owner whose seat <paramref name="standInName"/> shares under exactly the
        /// predicate the count subtracts by, or null. The stand-in's Astronaut Complex
        /// tooltip carries <see cref="SeatSharedSentence"/> only when this answers.
        /// </summary>
        internal static string SeatSharedOwner(
            string standInName,
            IEnumerable<KerbalsModule.KerbalSlot> slots,
            Func<string, bool> isReserved,
            Func<string, bool> isStockCounted)
        {
            if (string.IsNullOrEmpty(standInName)) return null;
            var pairs = new List<SeatPair>();
            CollectSeatSharingPairs(slots, isReserved, isStockCounted, pairs);
            for (int i = 0; i < pairs.Count; i++)
            {
                if (string.Equals(pairs[i].StandIn, standInName, StringComparison.Ordinal))
                    return pairs[i].Owner;
            }
            return null;
        }

        /// <summary>The one sentence the stand-in's tooltip gains.</summary>
        internal static string SeatSharedSentence(string ownerName)
        {
            return "They share " + ownerName + "'s seat and do not count against the Astronaut Complex limit.";
        }

        /// <summary>Appends <see cref="SeatSharedSentence"/> to a tooltip caption. Idempotent.</summary>
        internal static string AppendSeatSharedSentence(string caption, string ownerName)
        {
            if (string.IsNullOrEmpty(ownerName)) return caption;
            string sentence = SeatSharedSentence(ownerName);
            if (string.IsNullOrEmpty(caption)) return sentence;
            if (caption.Contains(sentence)) return caption;
            return caption + " " + sentence;
        }

        internal static string FormatCountLine(int rawCount, int pairs, int result)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Active crew count: raw={0} seatSharingPairs={1} result={2}",
                rawCount, pairs, result);
        }

        // ---------------- live gather ----------------

        /// <summary>Stock's rule over a live roster, by name (the roster's own name map,
        /// the same collection <c>GetActiveCrewCount</c> iterates).</summary>
        internal static Func<string, bool> StockCountedPredicate(KerbalRoster roster)
        {
            if (roster == null) return null;
            return name =>
            {
                if (string.IsNullOrEmpty(name)) return false;
                ProtoCrewMember pcm = roster[name];
                return pcm != null && IsCountedByStock(pcm.type, pcm.rosterStatus);
            };
        }

        /// <summary>The postfix body: stock's raw count adjusted for the live slots.</summary>
        internal static int AdjustLiveCount(KerbalRoster roster, int rawCount)
        {
            var kerbals = LedgerOrchestrator.Kerbals;
            if (kerbals == null || roster == null) return rawCount;
            var slots = kerbals.Slots;
            // Allocation-free for the common case: no slots, nothing to pair.
            if (slots == null || slots.Count == 0) return rawCount;

            int pairs = CollectSeatSharingPairs(
                slots.Values, kerbals.IsReservedNow, StockCountedPredicate(roster), null);
            int result = ApplySeatSharing(rawCount, pairs);
            ParsekLog.VerboseRateLimited(Tag, "active-crew-count-" + pairs.ToString(CultureInfo.InvariantCulture),
                FormatCountLine(rawCount, pairs, result));
            return result;
        }

        /// <summary>The live tooltip gate (<see cref="SeatSharedOwner"/> over the live
        /// slots and roster), or null.</summary>
        internal static string LiveSeatSharedOwner(string standInName)
        {
            var kerbals = LedgerOrchestrator.Kerbals;
            if (kerbals == null || string.IsNullOrEmpty(standInName)) return null;
            var slots = kerbals.Slots;
            if (slots == null || slots.Count == 0) return null;
            var roster = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.CrewRoster : null;
            return SeatSharedOwner(standInName, slots.Values, kerbals.IsReservedNow, StockCountedPredicate(roster));
        }
    }
}
