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

        /// <summary>
        /// True while the deferred plain-Rewind-to-Launch resource adjustment coroutine
        /// (<c>ParsekScenario.ApplyRewindResourceAdjustment</c>) is in flight, i.e. between
        /// scheduling the coroutine in <c>HandleRewindOnLoad</c> and the authoritative
        /// <c>RecalculateAndPatch(adjustedUT)</c> completing. This is the FIFTH
        /// time-travel signal for the recalc drawdown guard (Blocker 1): it is the ONLY
        /// authoritative-reduction signal true at the deferred recalc, because by then
        /// <see cref="EndRewind"/> has cleared <see cref="IsRewinding"/> and a plain rewind
        /// has no re-fly marker / merge journal / tombstone path. Owned by the coroutine's
        /// lifetime: set synchronously before scheduling, cleared by the coroutine's
        /// try/finally (authoritative) and by the next scene's <c>ParsekScenario.OnAwake</c>
        /// (race-free fail-safe for host-destroyed-mid-wait). NOT cleared by
        /// <see cref="EndRewind"/>, which runs before the coroutine resumes.
        /// </summary>
        internal static bool RewindResourceAdjustmentInProgress { get; private set; }
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
        /// Ids of committed recordings whose spawned vessel the plain rewind's pre-load strip
        /// KEPT as committed history (no recording that replays after the rewind re-produces
        /// it). The OnLoad playback reset preserves these recordings' spawn state so the kept
        /// vessel stays linked to its recording. Null when the rewind kept nothing or did not
        /// pre-process a save.
        /// </summary>
        internal static HashSet<string> RewindHistoricalSpawnKeepRecordingIds { get; private set; }

        /// <summary>
        /// The committed routes the rewind save's own <c>ParsekScenario</c> node carries, read
        /// from the parsed save before the scene load (the rewind's OnLoad node is persistent.sfs,
        /// not the rewind save). <c>HandleRewindOnLoad</c> gives each kept route its loop position
        /// back from it. Null when no rewind save was parsed or it held no Parsek scenario.
        /// </summary>
        internal static IReadOnlyList<Logistics.Route> RewindSaveRoutes { get; private set; }

        /// <summary>
        /// The parsed rewind save's own clock (<c>flightState.universalTime</c> after the
        /// lead-time windback, the value <see cref="RewindAdjustedUT"/> takes) for
        /// <see cref="RewindSaveRoutes"/>; NaN when unset.
        /// </summary>
        internal static double RewindSaveClockUT { get; private set; } = double.NaN;

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
            RewindHistoricalSpawnKeepRecordingIds = null;
            RewindSaveRoutes = null;
            RewindSaveClockUT = double.NaN;
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
            RewindHistoricalSpawnKeepRecordingIds = null;
            RewindSaveRoutes = null;
            RewindSaveClockUT = double.NaN;

            ParsekLog.Info("RewindContext", "EndRewind: all rewind flags cleared");
        }

        /// <summary>
        /// Marks the deferred plain-rewind resource adjustment as in progress (signal 5
        /// for the recalc drawdown guard). Called synchronously in
        /// <c>HandleRewindOnLoad</c> immediately before the coroutine is scheduled, so the
        /// flag is true before <see cref="EndRewind"/> clears <see cref="IsRewinding"/>.
        /// </summary>
        internal static void BeginRewindResourceAdjustment()
        {
            RewindResourceAdjustmentInProgress = true;
            ParsekLog.Info("RewindContext",
                "BeginRewindResourceAdjustment: deferred rewind resource adjustment in progress (drawdown-guard signal 5 ON)");
        }

        /// <summary>
        /// Clears the deferred plain-rewind resource adjustment flag (signal 5). Called from
        /// the coroutine's try/finally after the authoritative recalc and as a no-op
        /// fail-safe from the next scene's OnAwake. Idempotent.
        /// </summary>
        internal static void EndRewindResourceAdjustment()
        {
            if (!RewindResourceAdjustmentInProgress)
                return;
            RewindResourceAdjustmentInProgress = false;
            ParsekLog.Info("RewindContext",
                "EndRewindResourceAdjustment: deferred rewind resource adjustment complete (drawdown-guard signal 5 OFF)");
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
        /// Sets the ids of recordings whose spawned vessel the pre-load strip kept as
        /// committed history. Called from the rewind strip-scope resolver.
        /// </summary>
        internal static void SetHistoricalSpawnKeepRecordingIds(HashSet<string> ids)
        {
            RewindHistoricalSpawnKeepRecordingIds = ids != null && ids.Count > 0 ? ids : null;
            int count = RewindHistoricalSpawnKeepRecordingIds?.Count ?? 0;
            ParsekLog.Verbose("RewindContext",
                $"SetHistoricalSpawnKeepRecordingIds: {count} recording(s)");
        }

        /// <summary>
        /// Sets the rewind save's own committed routes and clock, read from the parsed save
        /// before the scene load (see <see cref="RewindSaveRoutes"/>).
        /// </summary>
        internal static void SetRewindSaveRoutes(IReadOnlyList<Logistics.Route> routes, double clockUT)
        {
            RewindSaveRoutes = routes;
            RewindSaveClockUT = clockUT;
            ParsekLog.Verbose("RewindContext",
                $"SetRewindSaveRoutes: {(routes == null ? "none" : routes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))} route(s) " +
                $"clockUT={clockUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Resets all state without logging. For unit tests only.
        /// </summary>
        internal static void ResetForTesting()
        {
            IsRewinding = false;
            RewindResourceAdjustmentInProgress = false;
            RewindUT = 0;
            RewindAdjustedUT = 0;
            RewindReserved = default(BudgetSummary);
            RewindBaselineFunds = 0;
            RewindBaselineScience = 0;
            RewindBaselineRep = 0;
            RewindQuicksaveVesselPids = null;
            RewindHistoricalSpawnKeepRecordingIds = null;
            RewindSaveRoutes = null;
            RewindSaveClockUT = double.NaN;
        }
    }
}
