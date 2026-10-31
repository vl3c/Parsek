using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Parsek
{
    /// <summary>The kinds of purchase the state rule decides (design 3.15 step 6).</summary>
    internal enum PurchaseKind
    {
        /// <summary><c>ScienceSpending</c> with a node id: the tech researched.</summary>
        Tech,
        /// <summary><c>FundsSpending</c> source <c>Other</c> with a part name: the part unlocked.</summary>
        Part,
        /// <summary><c>FundsSpending</c> source <c>PartUpgrade</c> with an upgrade name.</summary>
        PartUpgrade,
        /// <summary><c>FacilityUpgrade</c> (an upgrade or a Rebuild): the facility at that exact level.</summary>
        FacilityLevel,
        /// <summary><c>KerbalHire</c>: the kerbal on the roster.</summary>
        Hire
    }

    /// <summary>
    /// The purchase state rule (design 3.15 step 6, owner ruling 2026-10-26): a purchase is
    /// charged when the walk state at its (UT, cseq) does not already own the item. Pure.
    ///
    /// <para>"Already owns" is read together with capture order. The item is owned at the row
    /// only when the purchase that gave it was captured AFTER this row: a rewind separated
    /// them, so the later-captured purchase bought the item earlier on timeline and this one
    /// is its repeat. A holder captured before the row means stock itself sold the item
    /// again later in real time, which happens only when the walk cannot see the step that
    /// gave it up (an older save's Rebuild has no ledger row), so the row charges. A
    /// <see cref="GameActionType.KerbalDismissal"/> row is that step for a hire, and the
    /// walk sees it: it ends the holding (<see cref="PurchaseOwnershipBook.RecordRelease"/>,
    /// design section 9.12.1), so the next hire of that kerbal in walk order charges. In a career that never rewinds every holder was captured before
    /// every later purchase, so nothing is ever owned and the walk charges as before. A row
    /// or holder without a capture number (an older save) is never judged: it charges, as
    /// recorded.</para>
    ///
    /// <para>A legitimate repeat charges again: a facility taken a level down and raised again
    /// (the Rebuild row ends the holding of the higher level), a kerbal re-hired after a
    /// dismissal, a strategy activated again and a contract accepted again (those two keep
    /// their own once-per-activation and once-per-accept rules and are not decided here). A
    /// hire keeps the price captured with it.</para>
    ///
    /// <para>One predicate for every reader: the walk modules decide each row through
    /// <see cref="PurchaseOwnershipBook"/> in walk order, and the stock-screen reservation
    /// index (<see cref="CommittedFutureIndex"/>) reads <see cref="CollectAlreadyOwned"/>,
    /// which runs the same book over the same rows in the same order. The reserved floor, the
    /// projection and the click probe read the walk's row deltas, which leave a not-counted
    /// purchase out.</para>
    /// </summary>
    internal static class PurchaseStateRule
    {
        internal const string Tag = "PurchaseState";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// The item a purchase row buys, or false for a row the rule does not decide (not a
        /// purchase, or one with no key).
        /// </summary>
        internal static bool TryGetItem(GameAction action, out PurchaseKind kind, out string key)
        {
            kind = PurchaseKind.Tech;
            key = null;
            if (action == null)
                return false;
            switch (action.Type)
            {
                case GameActionType.ScienceSpending:
                    kind = PurchaseKind.Tech;
                    key = action.NodeId;
                    break;
                case GameActionType.FundsSpending:
                    if (action.FundsSpendingSource == FundsSpendingSource.Other)
                        kind = PurchaseKind.Part;
                    else if (action.FundsSpendingSource == FundsSpendingSource.PartUpgrade)
                        kind = PurchaseKind.PartUpgrade;
                    else
                        return false;
                    key = action.DedupKey;
                    break;
                case GameActionType.FacilityUpgrade:
                    if (action.ToLevel <= 0)
                        return false;
                    kind = PurchaseKind.FacilityLevel;
                    key = action.FacilityId;
                    break;
                case GameActionType.KerbalHire:
                    kind = PurchaseKind.Hire;
                    key = action.KerbalName;
                    break;
                default:
                    return false;
            }
            return !string.IsNullOrEmpty(key);
        }

        /// <summary>
        /// Pure: the holder of an item was captured after the row (both carry a capture
        /// number). Only then does the walk's ownership stop the row being charged.
        /// </summary>
        internal static bool IsCapturedAfter(long holderSequence, long rowSequence)
        {
            return holderSequence > 0 && rowSequence > 0 && holderSequence > rowSequence;
        }

        /// <summary>
        /// The rows of <paramref name="actions"/> the walk does not charge because it already
        /// owns the item there, by reference. Runs <see cref="PurchaseOwnershipBook"/> over the
        /// purchase rows in walk order (<see cref="RecalculationEngine.CompareWalkOrder"/>,
        /// stable), the order and book the walk modules use. Empty for a career that never
        /// rewound.
        /// </summary>
        internal static HashSet<GameAction> CollectAlreadyOwned(IReadOnlyList<GameAction> actions)
        {
            var owned = new HashSet<GameAction>(ReferenceComparer.Instance);
            if (actions == null || actions.Count == 0)
                return owned;
            var purchases = new List<GameAction>();
            var slots = new HashSet<string>(StringComparer.Ordinal);
            bool repeated = false;
            for (int i = 0; i < actions.Count; i++)
            {
                PurchaseKind kind;
                string key;
                if (IsRelease(actions[i]))
                {
                    purchases.Add(actions[i]);
                    continue;
                }
                if (!TryGetItem(actions[i], out kind, out key))
                    continue;
                purchases.Add(actions[i]);
                if (!slots.Add(kind.ToString() + "|" + key))
                    repeated = true;
            }
            // No item bought twice: nothing can be owned at a purchase (every career that
            // never bought one thing twice skips the sort).
            if (!repeated)
                return owned;
            var book = new PurchaseOwnershipBook();
            foreach (var row in purchases.OrderBy(a => a, WalkOrder.Instance))
            {
                string holder;
                if (IsRelease(row))
                    book.RecordRelease(row);
                else if (book.IsAlreadyOwned(row, out holder))
                    owned.Add(row);
                else
                    book.RecordCharged(row);
            }
            return owned;
        }

        /// <summary>A row that ends the holding of an item: a dismissal ends the hire's.</summary>
        internal static bool IsRelease(GameAction action)
        {
            return action != null && action.Type == GameActionType.KerbalDismissal
                && !string.IsNullOrEmpty(action.KerbalName);
        }

        /// <summary>One line naming a purchase's item, e.g. <c>tech 'basicRocketry'</c>.</summary>
        internal static string DescribeItem(PurchaseKind kind, string key, int level)
        {
            switch (kind)
            {
                case PurchaseKind.Tech: return "tech '" + key + "'";
                case PurchaseKind.Part: return "part '" + key + "'";
                case PurchaseKind.PartUpgrade: return "part upgrade '" + key + "'";
                case PurchaseKind.FacilityLevel: return "facility '" + key + "' level " + level.ToString(IC);
                case PurchaseKind.Hire: return "kerbal '" + key + "'";
                default: return key ?? "(none)";
            }
        }

        private sealed class WalkOrder : IComparer<GameAction>
        {
            internal static readonly WalkOrder Instance = new WalkOrder();
            public int Compare(GameAction x, GameAction y) => RecalculationEngine.CompareWalkOrder(x, y);
        }

        private sealed class ReferenceComparer : IEqualityComparer<GameAction>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(GameAction x, GameAction y) => ReferenceEquals(x, y);
            public int GetHashCode(GameAction obj) =>
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }

    /// <summary>
    /// What the walk owns so far, per purchased item (design 3.15 step 6). One instance per
    /// walk module, reset with it; the rows must be fed in walk order. Pure.
    ///
    /// <para>A facility is held at one level at a time, together with the row that took it to
    /// each level of the current run (a run starts at a Rebuild, at the first row seen for
    /// the facility, or at a row charged at or below the held level). Only a Rebuild (a row
    /// flagged <see cref="GameAction.FacilityDowngrade"/>) ends the run of the higher level:
    /// an ordinary upgrade row to a level at or below the held one is an old timeline's step
    /// the walk already has, owned when the row that took the run to that level (or started
    /// the run above it) was captured after it. A Rebuild is owned only at its exact
    /// level.</para>
    /// </summary>
    internal sealed class PurchaseOwnershipBook
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private struct Holder
        {
            internal long Sequence;
            internal string Id;
            internal double UT;
        }

        private sealed class Holding
        {
            /// <summary>The largest capture number among the charged rows that hold the item
            /// (not a facility).</summary>
            internal Holder Item;
            /// <summary>Facility only: the level the facility is at.</summary>
            internal int Level;
            /// <summary>Facility only: the row that took the current run to each level.</summary>
            internal readonly Dictionary<int, Holder> ByLevel = new Dictionary<int, Holder>();
        }

        private readonly Dictionary<string, Holding> held = new Dictionary<string, Holding>(StringComparer.Ordinal);

        internal int Count => held.Count;

        internal void Reset()
        {
            held.Clear();
        }

        private static string SlotOf(PurchaseKind kind, string key)
        {
            return kind.ToString() + "|" + key;
        }

        /// <summary>
        /// True when the walk already owns the row's item and its holder was captured after
        /// the row (<see cref="PurchaseStateRule.IsCapturedAfter"/>); <paramref name="holder"/>
        /// then names the holding row. False for a row the rule does not decide. A facility
        /// row is decided by <see cref="TryGetFacilityHolder"/>.
        /// </summary>
        internal bool IsAlreadyOwned(GameAction action, out string holder)
        {
            holder = null;
            PurchaseKind kind;
            string key;
            if (!PurchaseStateRule.TryGetItem(action, out kind, out key))
                return false;
            Holding h;
            if (!held.TryGetValue(SlotOf(kind, key), out h))
                return false;
            Holder by;
            if (kind == PurchaseKind.FacilityLevel)
            {
                if (!TryGetFacilityHolder(h, action, out by))
                    return false;
            }
            else
            {
                by = h.Item;
            }
            if (!PurchaseStateRule.IsCapturedAfter(by.Sequence, action.CaptureSequence))
                return false;
            holder = (by.Id ?? "(none)") + " ut=" + by.UT.ToString("R", IC)
                + " cseq=" + by.Sequence.ToString(IC);
            return true;
        }

        /// <summary>
        /// The row that gives the walk the level a facility row buys, or false when the walk is
        /// not at that level: a Rebuild needs the facility at exactly its level (the row that
        /// took the run there); an upgrade needs it at that level or higher (the row that took
        /// the run to the lowest level at or above it, which is the run's start when the
        /// level is below where the run began).
        /// </summary>
        private static bool TryGetFacilityHolder(Holding h, GameAction action, out Holder by)
        {
            by = default(Holder);
            int level = action.ToLevel;
            if (action.FacilityDowngrade)
                return h.Level == level && h.ByLevel.TryGetValue(level, out by);
            if (level > h.Level)
                return false;
            int best = int.MaxValue;
            foreach (var kv in h.ByLevel)
            {
                if (kv.Key >= level && kv.Key < best)
                {
                    best = kv.Key;
                    by = kv.Value;
                }
            }
            return best != int.MaxValue;
        }

        /// <summary>
        /// Records a row that ends a holding (<see cref="PurchaseStateRule.IsRelease"/>): a
        /// <see cref="GameActionType.KerbalDismissal"/> ends the kerbal's hire, so his next
        /// hire in walk order is charged. Returns true when a holding ended.
        /// </summary>
        internal bool RecordRelease(GameAction action)
        {
            if (!PurchaseStateRule.IsRelease(action))
                return false;
            return held.Remove(SlotOf(PurchaseKind.Hire, action.KerbalName));
        }

        /// <summary>Records a row the walk charged: its item is held from here on. A facility
        /// row above the held level extends the current run; a Rebuild, or any row at or below
        /// the held level (charged, so not owned), starts a new run at its level.</summary>
        internal void RecordCharged(GameAction action)
        {
            PurchaseKind kind;
            string key;
            if (!PurchaseStateRule.TryGetItem(action, out kind, out key))
                return;
            string slot = SlotOf(kind, key);
            Holding h;
            bool exists = held.TryGetValue(slot, out h);
            var row = new Holder { Sequence = action.CaptureSequence, Id = action.ActionId, UT = action.UT };

            if (kind != PurchaseKind.FacilityLevel)
            {
                if (!exists)
                {
                    h = new Holding { Item = row };
                    held[slot] = h;
                }
                else if (action.CaptureSequence > h.Item.Sequence)
                {
                    h.Item = row;
                }
                return;
            }

            bool extendsRun = exists && !action.FacilityDowngrade && action.ToLevel > h.Level;
            if (!extendsRun)
            {
                h = new Holding();
                held[slot] = h;
            }
            h.Level = action.ToLevel;
            h.ByLevel[action.ToLevel] = row;
        }
    }
}
