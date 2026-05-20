using System;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live-wiring verification for the #895 watch-cycle freeze fix
    /// (logs/2026-05-20_2005_watch-freeze/). The W-cycle eligibility predicate in
    /// <see cref="WatchModeController.CycleToNextWatchable"/> excludes a ghost that
    /// the playback engine retired this frame (out of authored parent-anchored
    /// coverage) so the camera is never steered onto a target that would fail watch
    /// entry and leave the camera bound to a destroyed ghost transform. The pure
    /// resolver contract is covered by <c>WatchCycleResolutionTests</c>; this test
    /// pins that <see cref="WatchModeController.IsGhostCoverageRetired"/> reads the
    /// live engine state (<c>GhostPlaybackState.anchorRetiredThisFrame</c>) at the
    /// committed index the predicate is asked about.
    /// </summary>
    public class WatchCoverageRetiredEligibilityRuntimeTest
    {
        [InGameTest(Category = "Watch", Scene = GameScenes.FLIGHT,
            Description = "IsGhostCoverageRetired reads live ghostStates[index].anchorRetiredThisFrame for the W-cycle eligibility predicate")]
        public void WatchCoverageRetiredEligibilityReadsLiveState()
        {
            var flight = ParsekFlight.Instance;
            InGameAssert.IsNotNull(flight, "ParsekFlight.Instance is null");
            InGameAssert.IsNotNull(flight.Engine, "ParsekFlight.Instance.Engine is null");
            InGameAssert.IsNotNull(flight.WatchMode, "ParsekFlight.Instance.WatchMode is null");

            // Pick committed indices that are not currently populated so the
            // synthetic fixture cannot collide with a live playback session.
            int retiredIdx = 90001;
            int liveIdx = 90002;
            int absentIdx = 90003;

            bool retiredHadPrior = flight.Engine.ghostStates.TryGetValue(retiredIdx, out var priorRetired);
            bool liveHadPrior = flight.Engine.ghostStates.TryGetValue(liveIdx, out var priorLive);
            bool absentHadPrior = flight.Engine.ghostStates.TryGetValue(absentIdx, out var priorAbsent);

            try
            {
                flight.Engine.ghostStates[retiredIdx] = new GhostPlaybackState
                {
                    vesselName = "CoverageRetired_Child",
                    recordingId = "cov_retired_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    anchorRetiredThisFrame = true,
                };
                flight.Engine.ghostStates[liveIdx] = new GhostPlaybackState
                {
                    vesselName = "CoverageLive_Child",
                    recordingId = "cov_live_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    anchorRetiredThisFrame = false,
                };
                flight.Engine.ghostStates.Remove(absentIdx);

                InGameAssert.IsTrue(flight.WatchMode.IsGhostCoverageRetired(retiredIdx),
                    "IsGhostCoverageRetired should be true for a ghost retired this frame");
                InGameAssert.IsFalse(flight.WatchMode.IsGhostCoverageRetired(liveIdx),
                    "IsGhostCoverageRetired should be false for a ghost that is not retired");
                InGameAssert.IsFalse(flight.WatchMode.IsGhostCoverageRetired(absentIdx),
                    "IsGhostCoverageRetired should be false when no ghost state exists at the index");

                ParsekLog.Info("WatchTest",
                    "WatchCoverageRetiredEligibility: live IsGhostCoverageRetired read confirmed " +
                    "(retired=true, live=false, absent=false)");
            }
            finally
            {
                if (retiredHadPrior) flight.Engine.ghostStates[retiredIdx] = priorRetired;
                else flight.Engine.ghostStates.Remove(retiredIdx);
                if (liveHadPrior) flight.Engine.ghostStates[liveIdx] = priorLive;
                else flight.Engine.ghostStates.Remove(liveIdx);
                if (absentHadPrior) flight.Engine.ghostStates[absentIdx] = priorAbsent;
            }
        }
    }
}
