using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Pure arithmetic of the once-only reward top-up (owner ruling 2026-11-04, replay audit
    /// T2): the earliest payment of a once-only reward wins the timing, and each later
    /// payment of the same reward pays its own full value minus what earlier payments already
    /// paid, per currency, never below 0. The career total is then the largest payment, never
    /// lower, whatever reward setting each payment was made under.
    /// </summary>
    internal static class MilestoneTopUp
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>A reward in the three currencies (reputation nominal).</summary>
        internal struct Paid
        {
            public float Funds;
            public float Rep;
            public float Science;

            internal bool Any => Funds > 0f || Rep > 0f || Science > 0f;

            internal static Paid Of(GameAction milestoneRow)
            {
                return milestoneRow == null
                    ? default(Paid)
                    : new Paid
                    {
                        Funds = milestoneRow.MilestoneFundsAwarded,
                        Rep = milestoneRow.MilestoneRepAwarded,
                        Science = milestoneRow.MilestoneScienceAwarded,
                    };
            }

            internal static Paid Max(Paid a, Paid b)
            {
                return new Paid
                {
                    Funds = a.Funds > b.Funds ? a.Funds : b.Funds,
                    Rep = a.Rep > b.Rep ? a.Rep : b.Rep,
                    Science = a.Science > b.Science ? a.Science : b.Science,
                };
            }

            internal string Describe()
            {
                return "funds=" + Funds.ToString("R", IC) + " rep=" + Rep.ToString("R", IC)
                    + " sci=" + Science.ToString("R", IC);
            }
        }

        /// <summary>Per currency, <paramref name="own"/> minus <paramref name="alreadyPaid"/>,
        /// never below 0 (a sub-cent rounding remainder reads as 0).</summary>
        internal static Paid Difference(Paid own, Paid alreadyPaid)
        {
            return new Paid
            {
                Funds = Positive(own.Funds - alreadyPaid.Funds),
                Rep = Positive(own.Rep - alreadyPaid.Rep),
                Science = Positive(own.Science - alreadyPaid.Science),
            };
        }

        private static float Positive(float v)
        {
            return v > 1e-3f ? v : 0f;
        }
    }
}
