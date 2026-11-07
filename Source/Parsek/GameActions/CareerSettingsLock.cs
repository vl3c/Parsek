using System;
using System.Runtime.CompilerServices;

namespace Parsek
{
    /// <summary>
    /// The career's "Allow negative funds / science" setting, locked at career start (owner
    /// ruling 2026-11-04, career settings audit section 3.4). Stock applies the setting live: a
    /// loss stops at 0 while it is off. The ledger walk replays every past loss, so a switch
    /// partway through a career would re-judge the committed past (add or remove what stock
    /// forgave, which a toggle could farm). The value the career had when Parsek first loaded
    /// its ledger is kept in the LEDGER node and is the only value the walk and the pool write
    /// read. Parsek greys the stock switch in the mid-career difficulty dialog
    /// (<c>Patches/AllowNegativeCurrencyLockPatch</c>), and a load whose live value differs
    /// (a save edited by hand, or written before the lock existed) is set back to the lock.
    /// </summary>
    internal static class CareerSettingsLock
    {
        internal const string Tag = "CareerSettings";
        internal const string AllowNegativeKey = "allowNegativeLocked";

        private static bool? allowNegative;

        /// <summary>The locked value, null before the career's first ledger load.</summary>
        internal static bool? AllowNegative => allowNegative;

        /// <summary>Test-only seam for the write of the locked value into the live
        /// <c>AdvancedParams</c>; returns true when it wrote.</summary>
        internal static Func<bool, bool> LiveWriterForTesting;

        internal static void ResetForTesting()
        {
            allowNegative = null;
            LiveWriterForTesting = null;
        }

        /// <summary>Pure: the value every reader uses, the lock when set, else the live one.</summary>
        internal static bool Resolve(bool? locked, bool live)
        {
            return locked ?? live;
        }

        internal static bool Resolve(bool live)
        {
            return Resolve(allowNegative, live);
        }

        internal enum LockDecision
        {
            None = 0,
            Lock,
            RewriteLive,
        }

        /// <summary>Pure: no lock yet takes the live value; a live value that differs from the
        /// lock is written back; otherwise nothing.</summary>
        internal static LockDecision Decide(bool? locked, bool live)
        {
            if (!locked.HasValue)
                return LockDecision.Lock;
            return locked.Value == live ? LockDecision.None : LockDecision.RewriteLive;
        }

        /// <summary>
        /// Locks the live value when the career has no lock yet, and sets the live value back
        /// to the lock when they differ. Called after every ledger load and when the stock
        /// difficulty dialog closes with its changes applied.
        /// </summary>
        internal static void EnsureAndEnforce(bool live, string reason)
        {
            switch (Decide(allowNegative, live))
            {
                case LockDecision.Lock:
                    allowNegative = live;
                    ParsekLog.Info(Tag,
                        $"Allow negative currency locked {(live ? "on" : "off")} for this career " +
                        $"({reason ?? "unknown"}): every loss is judged under it");
                    return;
                case LockDecision.RewriteLive:
                    bool wrote = WriteLive(allowNegative.Value);
                    ParsekLog.Info(Tag,
                        $"Allow negative currency is locked {(allowNegative.Value ? "on" : "off")} for this career " +
                        $"but the game had it {(live ? "on" : "off")} ({reason ?? "unknown"}): " +
                        $"{(wrote ? "set it back" : "could not write it back, the ledger keeps the lock")}");
                    return;
                default:
                    return;
            }
        }

        private static bool WriteLive(bool value)
        {
            var seam = LiveWriterForTesting;
            if (seam != null)
                return seam(value);
            try
            {
                return WriteLiveCore(value);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"Allow negative currency write failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool WriteLiveCore(bool value)
        {
            var game = HighLogic.CurrentGame;
            var advanced = game?.Parameters?.CustomParams<GameParameters.AdvancedParams>();
            if (advanced == null)
                return false;
            advanced.AllowNegativeCurrency = value;
            return true;
        }

        internal static void SaveInto(ConfigNode root)
        {
            if (root == null || !allowNegative.HasValue)
                return;
            root.AddValue(AllowNegativeKey, allowNegative.Value ? "1" : "0");
        }

        /// <summary>Replaces the lock with the node's (absent or unreadable: none, so the next
        /// <see cref="EnsureAndEnforce"/> locks the live value).</summary>
        internal static void LoadFrom(ConfigNode root, string sourceLabel)
        {
            allowNegative = null;
            string raw = root?.GetValue(AllowNegativeKey);
            if (raw == "1")
                allowNegative = true;
            else if (raw == "0")
                allowNegative = false;
            else if (raw != null)
                ParsekLog.Warn(Tag,
                    $"Unreadable {AllowNegativeKey}='{raw}' in '{sourceLabel ?? "unknown"}': the live value will be locked");
            ParsekLog.Verbose(Tag,
                $"Loaded career settings lock from '{sourceLabel ?? "unknown"}': allowNegative=" +
                (allowNegative.HasValue ? (allowNegative.Value ? "on" : "off") : "none"));
        }
    }
}
