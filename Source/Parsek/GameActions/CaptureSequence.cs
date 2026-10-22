using System.Collections.Generic;
using System.Globalization;

// [ERS-exempt] reads every row Ledger.Actions physically holds: a capture number must stay
// above rows the effective ledger hides too, or a later row could reuse one.
namespace Parsek
{
    /// <summary>
    /// The real-time capture order of the career's game-state events and ledger rows
    /// (committed-value rule plan section 2.3, coordinator's definition of "a committed cost
    /// lies ahead"). A game-state event is stamped when it is captured
    /// (<see cref="GameStateStore.AddEvent"/>) and every row converted from it carries the
    /// event's value; a row written directly (a Space Center purchase, a strategy
    /// conversion, a rollout) is stamped when the ledger takes it
    /// (<see cref="Ledger.AddAction(GameAction, LedgerRowWriter)"/>).
    ///
    /// <para>The next value is one above everything the session has issued and everything
    /// the ledger and the event store hold, so a rewind that keeps committed future rows
    /// never reissues a lower number, and a load continues from the save's own rows. 0 means
    /// "no sequence" (an older save): such a row is not ahead of anything and nothing is
    /// ahead of it.</para>
    /// </summary>
    internal static class CaptureSequence
    {
        internal const string Key = "cseq";

        private static long highWater;

        /// <summary>The next capture sequence (always above every value in use).</summary>
        internal static long Next()
        {
            RaiseHighWaterToInUse();
            highWater++;
            return highWater;
        }

        private static void RaiseHighWaterToInUse()
        {
            long inUse = MaxInUse();
            if (highWater < inUse)
                highWater = inUse;
        }

        /// <summary>Stamps <paramref name="action"/> with the next value when it has none.</summary>
        internal static void StampIfMissing(GameAction action)
        {
            if (action != null && action.CaptureSequence <= 0)
                action.CaptureSequence = Next();
        }

        /// <summary>
        /// Stamps the unstamped rows of one batch, earliest UT first (stable): a batch has no
        /// capture order of its own, and in UT order no row of it lies ahead of another.
        /// </summary>
        internal static int StampBatchIfMissing(IList<GameAction> batch)
        {
            if (batch == null || batch.Count == 0)
                return 0;
            var missing = new List<KeyValuePair<int, GameAction>>();
            for (int i = 0; i < batch.Count; i++)
            {
                var a = batch[i];
                if (a != null && a.CaptureSequence <= 0)
                    missing.Add(new KeyValuePair<int, GameAction>(i, a));
            }
            if (missing.Count == 0)
                return 0;
            missing.Sort((x, y) =>
            {
                int c = x.Value.UT.CompareTo(y.Value.UT);
                return c != 0 ? c : x.Key.CompareTo(y.Key);
            });
            RaiseHighWaterToInUse();
            for (int i = 0; i < missing.Count; i++)
                missing[i].Value.CaptureSequence = ++highWater;
            return missing.Count;
        }

        private static long MaxInUse()
        {
            long result = 0;
            var rows = Ledger.Actions;
            for (int i = 0; i < rows.Count; i++)
            {
                var a = rows[i];
                if (a != null && a.CaptureSequence > result)
                    result = a.CaptureSequence;
            }
            var events = GameStateStore.Events;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i].captureSequence > result)
                    result = events[i].captureSequence;
            }
            return result;
        }

        /// <summary>Parses a saved value; anything unreadable or not positive is "none".</summary>
        internal static long Parse(string value)
        {
            long parsed;
            if (string.IsNullOrEmpty(value)
                || !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                || parsed < 0)
                return 0;
            return parsed;
        }

        internal static void ResetForTesting()
        {
            highWater = 0;
        }
    }
}
