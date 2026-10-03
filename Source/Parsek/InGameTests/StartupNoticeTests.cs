namespace Parsek.InGameTests
{
    /// <summary>
    /// Live checks for the startup notices against the running install: the install-location
    /// check must be silent on a correctly installed Parsek (no false alarm from KSP's real
    /// paths), this session's patch sweep must have applied every class, and stock
    /// ScreenMessages must accept a post in a settled scene (the null-return contract the
    /// poster's retry depends on).
    /// </summary>
    public class StartupNoticeTests
    {
        [InGameTest(Category = "StartupNotices",
            Description = "Install-location check is silent on this install and sees the loaded Parsek.dll")]
        public void InstallLocationCheckIsSilentOnThisInstall()
        {
            var paths = ParsekHarmony.CollectLoadedParsekDllPaths();
            InGameAssert.IsNotEmpty(paths, "AssemblyLoader has no entry for Parsek");
            string gameDataDir = ParsekHarmony.ResolveGameDataDir();
            string notice = StartupNotices.EvaluateInstallLocation(paths, gameDataDir);
            InGameAssert.IsNull(notice,
                $"false alarm on this install: {notice} (paths={string.Join(", ", paths)}, gameData={gameDataDir})");
            InGameAssert.IsNotNull(StartupNotices.RelativeToGameData(paths[0], gameDataDir),
                $"loaded Parsek.dll is not under GameData: {paths[0]} vs {gameDataDir}");
        }

        [InGameTest(Category = "StartupNotices",
            Description = "This session's Harmony sweep applied every patch class and queued no failure notice")]
        public void PatchSweepAppliedEveryClass()
        {
            InGameAssert.IsGreaterThan(ParsekHarmony.LastAppliedPatchCount, 0,
                "no patch classes recorded as applied");
            InGameAssert.AreEqual(0, ParsekHarmony.LastFailedPatchNames.Count,
                "failed patch classes: " + string.Join(", ", ParsekHarmony.LastFailedPatchNames));
            InGameAssert.IsNull(StartupNotices.BuildPatchFailureNotice(
                    ParsekHarmony.LastAppliedPatchCount, new System.Collections.Generic.List<string>(
                        ParsekHarmony.LastFailedPatchNames)),
                "a clean sweep must not build a notice");
        }

        [InGameTest(Category = "StartupNotices",
            Description = "Stock ScreenMessages accepts a post in a settled scene (TryScreenMessage returns true)")]
        public void StockAcceptsAPostInASettledScene()
        {
            if (ScreenMessages.Instance == null)
            {
                InGameAssert.Skip("ScreenMessages has no instance in this scene.");
                return;
            }
            InGameAssert.IsTrue(
                ParsekLog.TryScreenMessage("Startup notice self-test (in-game test runner)", 2f),
                "stock dropped a post outside a scene load; the poster's retry would never end");
        }
    }
}
