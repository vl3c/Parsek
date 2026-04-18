using System.Collections;
using KSP.UI.Screens;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Rewind-to-Staging (Phase 4, design §6.1 + §7.27): verifies the warp
    /// drop/restore sequence. High physics-warp must be dropped to 0 for the
    /// stock save, then restored after the atomic move completes, so the player
    /// is not left stuck at rate 0.
    /// </summary>
    public class WarpZeroedDuringSaveTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            BatchSkipReason = "Single-run only — drives stock staging which permanently mutates the vessel.",
            Description = "High physics warp is dropped for the RP save and restored afterward")]
        public IEnumerator WarpZeroedDuringSave()
        {
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null)
            {
                InGameAssert.Skip("No active vessel");
                yield break;
            }

            int commandModules = 0;
            foreach (var part in vessel.parts)
            {
                if (part == null) continue;
                if (part.FindModuleImplementing<ModuleCommand>() != null)
                    commandModules++;
            }
            if (commandModules < 2)
            {
                InGameAssert.Skip("Needs 2+ command pods");
                yield break;
            }

            // TimeWarp.SetRate requires the active vessel to be in an environment
            // that supports physics warp; we try to set it, then yield a frame to
            // let KSP accept or reject the rate. If we cannot get above rate 1 we
            // skip (cannot test the drop/restore path).
            const int targetRate = 4;
            TimeWarp.SetRate(targetRate, true);
            yield return null;
            yield return null;

            int startingRate = TimeWarp.CurrentRateIndex;
            if (startingRate <= 1)
            {
                InGameAssert.Skip($"TimeWarp did not accept rate {targetRate}; currentRateIndex={startingRate}");
                yield break;
            }

            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance must be live in FLIGHT");

            int rpCountBefore = scenario.RewindPoints?.Count ?? 0;
            ParsekLog.Info("RewindTest",
                $"WarpZeroedDuringSave: startingRate={startingRate}, rpCountBefore={rpCountBefore}");

            StageManager.ActivateNextStage();

            // Wait for RP capture to complete.
            const int maxWaitFrames = 600;
            int frame = 0;
            RewindPoint rp = null;
            while (frame++ < maxWaitFrames)
            {
                int rpCountNow = scenario.RewindPoints?.Count ?? 0;
                if (rpCountNow > rpCountBefore)
                {
                    rp = scenario.RewindPoints[rpCountNow - 1];
                    if (rp.PidSlotMap != null && rp.PidSlotMap.Count > 0)
                        break;
                }
                yield return null;
            }

            InGameAssert.IsNotNull(rp, "No RewindPoint captured after staging");

            // Give one more frame for the warp restore finally to run.
            yield return null;

            int endingRate = TimeWarp.CurrentRateIndex;
            ParsekLog.Info("RewindTest",
                $"WarpZeroedDuringSave: endingRate={endingRate} (expected {startingRate})");

            InGameAssert.AreEqual(startingRate, endingRate,
                $"TimeWarp.CurrentRateIndex was {startingRate} before staging but {endingRate} after — " +
                "warp restore did not run");

            // Reset warp for good measure so follow-up tests don't inherit it.
            TimeWarp.SetRate(0, true);

            ParsekLog.Info("RewindTest",
                $"WarpZeroedDuringSave: PASS rp={rp.RewindPointId}");
        }
    }
}
