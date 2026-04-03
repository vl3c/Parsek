using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Encapsulates all rewind state that survives scene changes via static fields.
    /// Mutation is controlled through BeginRewind/EndRewind/SetAdjustedUT/SetQuicksaveVesselPids.
    /// Previously these 8 fields were scattered across RecordingStore.
    /// </summary>
    internal static class RewindContext
    {
        internal static bool IsRewinding { get; private set; }
        internal static double RewindUT { get; private set; }
        internal static double RewindAdjustedUT { get; private set; }
        internal static BudgetSummary RewindReserved { get; private set; }

        /// <summary>
        /// Baseline resource values from the rewind-target recording's PreLaunch snapshot.
        /// Used by the deferred coroutine to compute absolute-target resource corrections
        /// (idempotent regardless of what Funding.OnLoad restores from the save).
        /// </summary>
        internal static double RewindBaselineFunds { get; private set; }
        internal static double RewindBaselineScience { get; private set; }
        internal static float RewindBaselineRep { get; private set; }

        /// <summary>
        /// PIDs of vessels that existed in the rewind quicksave.
        /// Used by StripFuturePrelaunchVessels to whitelist known-good PRELAUNCH vessels
        /// (e.g. the player's pad vessel) and strip only unknown ones from the future.
        /// </summary>
        internal static HashSet<uint> RewindQuicksaveVesselPids { get; private set; }

        /// <summary>
        /// Sets all rewind state at the start of a rewind operation.
        /// RewindAdjustedUT and RewindQuicksaveVesselPids are set separately
        /// (after LoadGame and PreProcessRewindSave respectively).
        /// </summary>
        internal static void BeginRewind(double ut, BudgetSummary reserved,
            double baselineFunds, double baselineScience, float baselineRep)
        {
            IsRewinding = true;
            RewindUT = ut;
            RewindReserved = reserved;
            RewindBaselineFunds = baselineFunds;
            RewindBaselineScience = baselineScience;
            RewindBaselineRep = baselineRep;

            ParsekLog.Info("RewindContext",
                $"BeginRewind: UT={ut:F1}, baselineFunds={baselineFunds:F1}, " +
                $"baselineSci={baselineScience:F1}, baselineRep={baselineRep:F1}, " +
                $"reservedFunds={reserved.reservedFunds:F1}, " +
                $"reservedSci={reserved.reservedScience:F1}, " +
                $"reservedRep={reserved.reservedReputation:F1}");
        }

        /// <summary>
        /// Clears all rewind state at the end of a rewind operation.
        /// </summary>
        internal static void EndRewind()
        {
            IsRewinding = false;
            RewindUT = 0;
            RewindAdjustedUT = 0;
            RewindReserved = default(BudgetSummary);
            RewindBaselineFunds = 0;
            RewindBaselineScience = 0;
            RewindBaselineRep = 0;
            RewindQuicksaveVesselPids = null;

            ParsekLog.Info("RewindContext", "EndRewind: all rewind flags cleared");
        }

        /// <summary>
        /// Sets the adjusted UT captured from the preprocessed save file after LoadGame.
        /// Called separately from BeginRewind because the adjusted UT is only known
        /// after PreProcessRewindSave + LoadGame.
        /// </summary>
        internal static void SetAdjustedUT(double ut)
        {
            RewindAdjustedUT = ut;
            ParsekLog.Verbose("RewindContext", $"SetAdjustedUT: {ut:F1}");
        }

        /// <summary>
        /// Sets the PIDs of vessels surviving in the rewind quicksave.
        /// Called from PreProcessRewindSave after stripping future vessels.
        /// </summary>
        internal static void SetQuicksaveVesselPids(HashSet<uint> pids)
        {
            RewindQuicksaveVesselPids = pids;
            int count = pids?.Count ?? 0;
            ParsekLog.Verbose("RewindContext",
                $"SetQuicksaveVesselPids: {count} PID(s)");
        }

        /// <summary>
        /// Resets all state without logging. For unit tests only.
        /// </summary>
        internal static void ResetForTesting()
        {
            IsRewinding = false;
            RewindUT = 0;
            RewindAdjustedUT = 0;
            RewindReserved = default(BudgetSummary);
            RewindBaselineFunds = 0;
            RewindBaselineScience = 0;
            RewindBaselineRep = 0;
            RewindQuicksaveVesselPids = null;
        }
    }
}
