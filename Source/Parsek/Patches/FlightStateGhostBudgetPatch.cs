using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Keeps ghost map ProtoVessels out of stock's save-time vessel budget.
    ///
    /// <para>
    /// Stock <c>FlightState()</c> (the parameterless constructor <c>Game.Updated</c> calls
    /// when the scene has a planetarium) walks every non-dead vessel in
    /// <c>FlightGlobals.Vessels</c>, then, unless <c>GameSettings.MAX_VESSELS_BUDGET</c> is
    /// -1, drops non-persistent Debris-typed vessels (oldest first) until the list fits the
    /// budget. Ghost map vessels are live, persistent, non-Debris vessels in FLIGHT and
    /// TRACKSTATION, so they are never dropped themselves but each one COUNTS, and
    /// <c>GhostMapPresence.StripFromSave</c> removes them only later, from
    /// <c>ParsekScenario.OnSave</c>. Without this patch every ghost pushes one real debris
    /// vessel out of the save.
    /// </para>
    ///
    /// <para>
    /// The prefix raises the global budget by the number of ghost map vessels the
    /// constructor will walk; the finalizer restores the captured value whether the
    /// constructor returned or threw, so the raised value can never outlive the one
    /// synchronous build (and so can never be written to settings.cfg). A budget of -1
    /// (no limit) is left alone.
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(FlightState), MethodType.Constructor, new Type[0])]
    internal static class FlightStateGhostBudgetPatch
    {
        internal const int UnlimitedBudget = -1;

        internal struct BudgetAdjustment
        {
            public bool Applied;
            public int OriginalBudget;
            public int AdjustedBudget;
            public int GhostCount;
        }

        /// <summary>Test seam: replaces the live ghost-vessel count.</summary>
        internal static Func<int> GhostCountOverrideForTesting;

        internal static void ResetForTesting()
        {
            GhostCountOverrideForTesting = null;
        }

        /// <summary>
        /// True when the budget must be raised for this build: a budget is in force (not -1)
        /// and at least one ghost map vessel would be counted against it.
        /// </summary>
        internal static bool ShouldAdjust(int budget, int ghostCount)
        {
            return budget != UnlimitedBudget && ghostCount > 0;
        }

        /// <summary>
        /// The budget stock should see so that real vessels get the player's whole budget:
        /// the player's budget plus one slot per counted ghost, saturating at
        /// <see cref="int.MaxValue"/>. Returns the input unchanged when no adjustment applies.
        /// </summary>
        internal static int ComputeAdjustedBudget(int budget, int ghostCount)
        {
            if (!ShouldAdjust(budget, ghostCount))
                return budget;
            long sum = (long)budget + ghostCount;
            return sum > int.MaxValue ? int.MaxValue : (int)sum;
        }

        /// <summary>
        /// Counts the vessels stock's constructor will put on its list that are ghost map
        /// vessels. Each entry is (pid, isDead); dead vessels are skipped by stock before the
        /// budget walk, so they are not counted here either.
        /// </summary>
        internal static int CountGhostVessels(
            IEnumerable<KeyValuePair<uint, bool>> pidAndDead,
            Func<uint, bool> isGhostMapVessel)
        {
            if (pidAndDead == null || isGhostMapVessel == null)
                return 0;
            int count = 0;
            foreach (var entry in pidAndDead)
            {
                if (entry.Value)
                    continue;
                if (isGhostMapVessel(entry.Key))
                    count++;
            }
            return count;
        }

        internal static string FormatAdjustmentLog(BudgetAdjustment adjustment)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "FlightState vessel budget: excluded {0} ghost map vessel(s) from the stock " +
                "save budget, MAX_VESSELS_BUDGET {1} -> {2} for this build (restored after)",
                adjustment.GhostCount,
                adjustment.OriginalBudget,
                adjustment.AdjustedBudget);
        }

        /// <summary>
        /// Captures the current budget, raises it when ghosts would count, and returns what
        /// was done. Split from the Harmony prefix so an in-game test can drive the same
        /// prefix/finalizer pair around a real <c>new FlightState()</c>.
        /// </summary>
        internal static BudgetAdjustment Apply(int ghostCount)
        {
            var adjustment = new BudgetAdjustment
            {
                Applied = false,
                OriginalBudget = GameSettings.MAX_VESSELS_BUDGET,
                GhostCount = ghostCount
            };
            adjustment.AdjustedBudget = adjustment.OriginalBudget;
            if (!ShouldAdjust(adjustment.OriginalBudget, ghostCount))
                return adjustment;

            adjustment.AdjustedBudget = ComputeAdjustedBudget(adjustment.OriginalBudget, ghostCount);
            adjustment.Applied = true;
            // Log BEFORE the write: nothing after the write may throw, or the prefix would
            // lose the captured state and the finalizer could not restore the budget.
            ParsekLog.Info("GhostMap", FormatAdjustmentLog(adjustment));
            GameSettings.MAX_VESSELS_BUDGET = adjustment.AdjustedBudget;
            return adjustment;
        }

        internal static void Restore(BudgetAdjustment adjustment)
        {
            if (!adjustment.Applied)
                return;
            GameSettings.MAX_VESSELS_BUDGET = adjustment.OriginalBudget;
        }

        private static int CountLiveGhostMapVessels()
        {
            if (GhostCountOverrideForTesting != null)
                return GhostCountOverrideForTesting();
            if (GhostMapPresence.RegisteredGhostMapVesselCount == 0)
                return 0;

            List<Vessel> vessels = FlightGlobals.Vessels;
            if (vessels == null)
                return 0;
            var entries = new List<KeyValuePair<uint, bool>>(vessels.Count);
            for (int i = 0; i < vessels.Count; i++)
            {
                Vessel v = vessels[i];
                if (v == null)
                    continue;
                entries.Add(new KeyValuePair<uint, bool>(
                    v.persistentId, v.state == Vessel.State.DEAD));
            }
            return CountGhostVessels(entries, GhostMapPresence.IsGhostMapVessel);
        }

        static void Prefix(out BudgetAdjustment __state)
        {
            __state = default(BudgetAdjustment);
            try
            {
                __state = Apply(CountLiveGhostMapVessels());
            }
            catch (Exception ex)
            {
                // Never block a save: on any failure stock runs with whatever budget is set,
                // and the finalizer restores it if Apply got as far as raising it.
                ParsekLog.Warn("GhostMap",
                    "FlightState vessel budget: ghost exclusion failed, stock budget used as-is: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        static void Finalizer(BudgetAdjustment __state)
        {
            Restore(__state);
        }
    }
}
