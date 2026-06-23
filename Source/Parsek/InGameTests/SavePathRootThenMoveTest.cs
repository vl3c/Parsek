using System.Collections;
using System.IO;
using KSP.UI.Screens;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Rewind-to-Staging (Phase 4, design §5.10 + §6.1): verifies the root-save
    /// -then-move flow. KSP <c>GamePersistence.SaveGame</c> writes to the save
    /// folder root; the RewindPointAuthor atomically moves the file into
    /// <c>Parsek/RewindPoints/</c>. After capture, neither a <c>Parsek_TempRP_*.sfs</c>
    /// nor its orphaned <c>Parsek_TempRP_*.loadmeta</c> sidecar should remain in the
    /// save root.
    /// </summary>
    public class SavePathRootThenMoveTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only - drives stock staging which permanently mutates the vessel; excluded from ordinary Run All / Run category. Use Run All + Isolated or the row play button in a disposable FLIGHT session.",
            Description = "RewindPoint quicksave ends up in Parsek/RewindPoints/, no leftover Parsek_TempRP_*.sfs or .loadmeta in save root")]
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
            // A multi-controllable split (the RP precondition) increases this; the delta tells a
            // wrong-craft "no split" (skip) from a real "split but no RewindPoint" regression (fail).
            int controllableBefore = CountLoadedControllableVessels();
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

            // No stray Parsek_TempRP_*.loadmeta sidecar in save root either: SaveGame
            // writes the .sfs + .loadmeta pair to the root, only the .sfs is moved, and
            // FileIOUtils.DeleteSaveSidecarLoadMeta must clean up the orphaned sidecar.
            string[] strayMeta = Directory.Exists(saveRoot)
                ? Directory.GetFiles(saveRoot, "Parsek_TempRP_*.loadmeta", SearchOption.TopDirectoryOnly)
                : new string[0];
            if (strayMeta.Length > 0)
            {
                ParsekLog.Error("RewindTest",
                    $"[CRITICAL] SavePathRootThenMove: {strayMeta.Length} orphaned Parsek_TempRP_*.loadmeta " +
                    $"sidecar(s) left in save root after move: " +
                    string.Join(", ", strayMeta));
            }
            InGameAssert.AreEqual(0, strayMeta.Length,
                $"Expected no Parsek_TempRP_*.loadmeta in save root; found {strayMeta.Length}");

            ParsekLog.Info("RewindTest",
                $"SavePathRootThenMove: PASS rp={rp.RewindPointId} path={expectedPath}");
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
