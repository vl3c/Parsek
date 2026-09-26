using System;
using System.Globalization;

namespace Parsek
{
    internal struct BudgetSummary
    {
        public double reservedFunds;
        public double reservedScience;
        public double reservedReputation;
    }

    internal static class ResourceBudget
    {
        internal static double ParseCostFromDetail(string detail)
        {
            if (string.IsNullOrEmpty(detail)) return 0;

            // #451: `cost=` is the authoritative charged amount. PartPurchased events may
            // additionally carry `entryCost=` for the raw stock unlock price, but
            // bypass=true careers persist `cost=0;entryCost=<raw>` and must not reload as
            // a paid purchase. Fall back to `entryCost=` only when `cost=` is absent.
            string[] parts = detail.Split(';');
            double cost = 0;
            bool costFound = false;
            double entryCost = 0;
            bool entryCostFound = false;
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (!costFound && part.StartsWith("cost=", StringComparison.Ordinal))
                {
                    double parsed;
                    if (double.TryParse(part.Substring(5), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed))
                    {
                        cost = parsed;
                        costFound = true;
                    }
                    else
                        ParsekLog.Warn("ResourceBudget", $"ParseCostFromDetail: failed to parse '{part}' from detail='{detail}'");
                }
                else if (!entryCostFound && part.StartsWith("entryCost=", StringComparison.Ordinal))
                {
                    double parsed;
                    if (double.TryParse(part.Substring(10), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed))
                    {
                        entryCost = parsed;
                        entryCostFound = true;
                    }
                    else
                        ParsekLog.Warn("ResourceBudget", $"ParseCostFromDetail: failed to parse '{part}' from detail='{detail}'");
                }
            }
            if (costFound) return cost;
            if (entryCostFound) return entryCost;
            return 0;
        }
    }
}
