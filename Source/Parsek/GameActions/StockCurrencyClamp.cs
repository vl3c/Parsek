using System;
using System.Runtime.CompilerServices;

namespace Parsek
{
    /// <summary>
    /// Stock's 0 clamp on the funds and science pools, as the ledger walk applies it
    /// (committed-value rule plan section 2.3, the no-cost-ahead case, built in phase 2 so
    /// the walk keeps a gain that follows a loss stock stopped at 0). Pure.
    /// </summary>
    internal static class StockCurrencyClamp
    {
        /// <summary>
        /// The part of one row's loss stock forgives: with <paramref name="allowNegativeCurrency"/>
        /// off, a row that is not a cost row and leaves the running value below 0 stops at 0, or
        /// at <paramref name="before"/> when that was already below 0 (a row never raises the
        /// value). 0 for a gain, a cost row, or a row that stays at or above 0.
        /// </summary>
        internal static double ForgivenBelowZero(
            double before, double after, bool costRow, bool allowNegativeCurrency)
        {
            if (allowNegativeCurrency || costRow)
                return 0.0;
            double delta = after - before;
            if (!(delta < 0.0) || !(after < 0.0))
                return 0.0;
            return Math.Min(-delta, -after);
        }

        /// <summary>
        /// Stock's <c>AllowNegativeCurrency</c> for a walk. The read sits behind a
        /// no-inlining call inside a try, so a host with no KSP runtime (the xUnit suite under
        /// mono, where a Unity type can fail at JIT of the method that touches it) reads off.
        /// </summary>
        internal static bool ReadAllowNegativeCurrency()
        {
            try
            {
                return ReadAllowNegativeCurrencyCore();
            }
            catch (Exception)
            {
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ReadAllowNegativeCurrencyCore()
        {
            return KspStatePatcher.ReadAllowNegativeCurrency();
        }
    }
}
