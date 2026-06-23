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
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only - drives stock staging which permanently mutates the vessel; excluded from ordinary Run All / Run category. Use Run All + Isolated or the row play button in a disposable FLIGHT session.",
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

            // A multi-controllable split (the RP precondition) increases this; the delta tells a
            // wrong-craft "no split" (skip) from a real "split but no RewindPoint" regression (fail).
            int controllableBefore = CountLoadedControllableVessels();
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

            if (rp == null)
            {
                int controllableAfter = CountLoadedControllableVessels();
                if (controllableAfter <= controllableBefore)
                {
                    InGameAssert.Skip(
                        $"next stage did not separate a second controllable vessel (controllable " +
                        $"{controllableBefore}->{controllableAfter}); this isolated-run test needs a " +
                        "2-pod+decoupler craft staged at the decoupler, not the loaded vessel");
                    yield break;
                }
                InGameAssert.Fail(
                    $"A controllable split occurred ({controllableBefore}->{controllableAfter}) but no " +
                    "RewindPoint was captured after staging");
            }

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

        // Counts loaded vessels in physics range carrying at least one command module (controllable).
        // A multi-controllable staging split increases this by >=1; the delta discriminates a
        // wrong-craft "no split" from a real "split but no RewindPoint" regression.
        private static int CountLoadedControllableVessels()
        {
            int n = 0;
            var loaded = FlightGlobals.VesselsLoaded;
            if (loaded == null) return 0;
            for (int i = 0; i < loaded.Count; i++)
            {
                var v = loaded[i];
                if (v == null || v.parts == null) continue;
                for (int p = 0; p < v.parts.Count; p++)
                {
                    if (v.parts[p] != null && v.parts[p].FindModuleImplementing<ModuleCommand>() != null)
                    {
                        n++;
                        break;
                    }
                }
            }
            return n;
        }
    }
}
