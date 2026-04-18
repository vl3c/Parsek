using System.Collections;
using System.IO;
using KSP.UI.Screens;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Rewind-to-Staging (Phase 4, design §5.10 + §6.1): verifies the root-save
    /// -then-move flow. KSP <c>GamePersistence.SaveGame</c> writes to the save
    /// folder root; the RewindPointAuthor atomically moves the file into
    /// <c>Parsek/RewindPoints/</c>. After capture, no <c>Parsek_TempRP_*.sfs</c>
    /// should remain in the save root.
    /// </summary>
    public class SavePathRootThenMoveTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            BatchSkipReason = "Single-run only — drives stock staging which permanently mutates the vessel.",
            Description = "RewindPoint quicksave ends up in Parsek/RewindPoints/, no leftover Parsek_TempRP_*.sfs in save root")]
        public IEnumerator SavePathRootThenMove()
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

            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance must be live in FLIGHT");

            int rpCountBefore = scenario.RewindPoints?.Count ?? 0;
            StageManager.ActivateNextStage();
            ParsekLog.Info("RewindTest", "SavePathRootThenMove: StageManager.ActivateNextStage() called");

            // Wait for the deferred save + move to complete.
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

            string root = KSPUtil.ApplicationRootPath ?? "";
            string saveFolder = HighLogic.SaveFolder ?? "";
            string saveRoot = Path.GetFullPath(Path.Combine(root, "saves", saveFolder));
            string rewindDir = Path.Combine(saveRoot, "Parsek", "RewindPoints");
            string expectedPath = Path.Combine(rewindDir, rp.RewindPointId + ".sfs");

            InGameAssert.IsTrue(File.Exists(expectedPath),
                $"Expected RP file at '{expectedPath}'");

            // No stray Parsek_TempRP_*.sfs in save root.
            string[] stray = Directory.Exists(saveRoot)
                ? Directory.GetFiles(saveRoot, "Parsek_TempRP_*.sfs", SearchOption.TopDirectoryOnly)
                : new string[0];
            if (stray.Length > 0)
            {
                ParsekLog.Error("RewindTest",
                    $"[CRITICAL] SavePathRootThenMove: {stray.Length} stray Parsek_TempRP_*.sfs " +
                    $"file(s) left in save root after move (move did not run or partially ran): " +
                    string.Join(", ", stray));
            }
            InGameAssert.AreEqual(0, stray.Length,
                $"Expected no Parsek_TempRP_*.sfs in save root; found {stray.Length}");

            ParsekLog.Info("RewindTest",
                $"SavePathRootThenMove: PASS rp={rp.RewindPointId} path={expectedPath}");
        }
    }
}
