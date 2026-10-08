using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class StartupNoticesTests : System.IDisposable
    {
        private static readonly string GameData = Path.Combine(Path.GetTempPath(), "KSP", "GameData");

        public StartupNoticesTests()
        {
            StartupNotices.ResetForTesting();
        }

        public void Dispose()
        {
            StartupNotices.ResetForTesting();
        }

        private static string InGameData(params string[] parts)
            => Path.Combine(new[] { GameData }.Concat(parts).ToArray());

        [Fact]
        public void EveryHarmonyPatchClass_MapsToAPlayerFacingFeature()
        {
            // The sweep in ParsekHarmony.Awake applies exactly these classes; a new patch
            // class must get a feature rule so its failure notice names what broke.
            var unmapped = typeof(ParsekHarmony).Assembly.GetTypes()
                .Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0)
                .Where(t => StartupNotices.PatchFeatureFor(StartupNotices.PatchClassKey(t)) == null)
                .Select(t => t.FullName)
                .ToList();

            Assert.True(unmapped.Count == 0,
                "Harmony patch classes with no StartupNotices feature rule: " + string.Join(", ", unmapped));
        }

        [Theory]
        [InlineData("SwitchIntentTrackingStationFlyPatch", "vessel switch recording")]
        [InlineData("GhostTrackingFlyPatch", "ghost vessels")]
        [InlineData("TechResearchPatch", "tech tree reservations")]
        [InlineData("RDTechPurchasePartPatch", "tech tree reservations")]
        [InlineData("FacilityUpgradePatch", "facility upgrade reservations")]
        [InlineData("MissionControlAcceptPatch", "contract reservations")]
        [InlineData("AdministrationButtonBackstopPatch", "strategy reservations")]
        [InlineData("CrewDialogFillPatch", "crew reservations")]
        [InlineData("RecoveryScopeVesselRetrievalPatch", "science and milestone rewards")]
        [InlineData("PhysicsFramePatch", "flight recording")]
        [InlineData("RevertToLaunchInterceptor", "revert, rewind and scene changes")]
        [InlineData("FlightResultsDialogLogPatches.DisplayPatch", "diagnostic logging")]
        [InlineData("DisplayPatch", null)]
        [InlineData("SomethingNew", null)]
        [InlineData("", null)]
        public void PatchFeatureFor_FollowsTheOrderedRules(string className, string expected)
        {
            Assert.Equal(expected, StartupNotices.PatchFeatureFor(className));
        }

        [Fact]
        public void PatchNotice_NullWhenNothingFailed()
        {
            Assert.Null(StartupNotices.BuildPatchFailureNotice(90, new List<string>()));
            Assert.Null(StartupNotices.BuildPatchFailureNotice(90, null));
        }

        [Fact]
        public void PatchNotice_CountsAndDedupesFeaturesInFailureOrder()
        {
            string notice = StartupNotices.BuildPatchFailureNotice(90,
                new List<string> { "CrewDialogFillPatch", "GhostOrbitLinePatch", "KerbalHirePatch" });

            Assert.Equal(
                "3 of 90 game patches failed to load, so these may not work: crew reservations, ghost vessels. "
                + "Another mod or a different KSP version is the usual cause. Details are in KSP.log.",
                notice);
        }

        [Fact]
        public void PatchNotice_CapsTheListAndNamesUnmappedAsOtherFeatures()
        {
            string notice = StartupNotices.BuildPatchFailureNotice(90, new List<string>
            {
                "TechResearchPatch", "FacilityUpgradePatch", "ContractAcceptPatch", "StrategyActivatePatch", "Mystery",
            });

            Assert.Contains("5 of 90 game patches", notice);
            Assert.Contains(
                "tech tree reservations, facility upgrade reservations, contract reservations and 2 more.", notice);
        }

        [Fact]
        public void Install_CorrectLayoutIsSilent()
        {
            Assert.Null(StartupNotices.EvaluateInstallLocation(
                new[] { InGameData("Parsek", "Plugins", "Parsek.dll") }, GameData));
        }

        [Fact]
        public void Install_FolderNameCaseIsNotReported()
        {
            Assert.Null(StartupNotices.EvaluateInstallLocation(
                new[] { InGameData("parsek", "Plugins", "Parsek.dll") }, GameData));
        }

        [Fact]
        public void Install_NestedGameDataIsReported()
        {
            string notice = StartupNotices.EvaluateInstallLocation(
                new[] { InGameData("GameData", "Parsek", "Plugins", "Parsek.dll") }, GameData);

            Assert.Equal(
                "Parsek is loading from the wrong folder (GameData/GameData/Parsek/Plugins/Parsek.dll). "
                + "It must be in GameData/Parsek; if you have more than one copy, keep only that one.",
                notice);
        }

        [Fact]
        public void Install_ZipFolderWrapperIsReported()
        {
            string notice = StartupNotices.EvaluateInstallLocation(
                new[] { InGameData("Parsek-v0.10.5", "Parsek", "Plugins", "Parsek.dll") }, GameData);

            Assert.Contains("(GameData/Parsek-v0.10.5/Parsek/Plugins/Parsek.dll)", notice);
        }

        [Fact]
        public void Install_LooseDllInGameDataIsReported()
        {
            // A loose GameData/Parsek.dll has a first segment of "Parsek.dll", never the folder.
            string notice = StartupNotices.EvaluateInstallLocation(
                new[] { InGameData("Parsek.dll") }, GameData);

            Assert.Contains("(GameData/Parsek.dll)", notice);
        }

        [Fact]
        public void Install_StrayCopyThatWonTheLoadIsReported()
        {
            // AssemblyLoader keeps one entry per name (highest version), so a duplicate
            // install surfaces only when the stray copy is the one loaded.
            string notice = StartupNotices.EvaluateInstallLocation(
                new[] { InGameData("Parsek (old)", "Plugins", "Parsek.dll") }, GameData);

            Assert.Contains("(GameData/Parsek (old)/Plugins/Parsek.dll)", notice);
            Assert.Contains("if you have more than one copy, keep only that one", notice);
        }

        [Fact]
        public void Install_OnlyMisplacedPathsAreListed()
        {
            string notice = StartupNotices.EvaluateInstallLocation(new[]
            {
                InGameData("Parsek", "Plugins", "Parsek.dll"),
                InGameData("GameData", "Parsek", "Plugins", "Parsek.dll"),
            }, GameData);

            Assert.Contains("(GameData/GameData/Parsek/Plugins/Parsek.dll)", notice);
            Assert.DoesNotContain("GameData/Parsek/Plugins/Parsek.dll,", notice);
        }

        [Fact]
        public void Install_UnjudgeableInputsAreSilent()
        {
            Assert.Null(StartupNotices.EvaluateInstallLocation(null, GameData));
            Assert.Null(StartupNotices.EvaluateInstallLocation(new string[0], GameData));
            Assert.Null(StartupNotices.EvaluateInstallLocation(new[] { InGameData("Parsek", "Plugins", "Parsek.dll") }, ""));
            // Outside GameData entirely (a dev symlink, an unusual loader): no claim.
            Assert.Null(StartupNotices.EvaluateInstallLocation(
                new[] { Path.Combine(Path.GetTempPath(), "elsewhere", "Parsek.dll") }, GameData));
        }

        [Fact]
        public void RelativeToGameData_HandlesTrailingSlashAndMixedSeparators()
        {
            string withSlash = GameData + Path.DirectorySeparatorChar;
            string mixed = GameData.Replace('\\', '/') + "/Parsek\\Plugins/Parsek.dll";

            Assert.Equal("Parsek/Plugins/Parsek.dll", StartupNotices.RelativeToGameData(mixed, withSlash));
            Assert.Null(StartupNotices.RelativeToGameData(GameData, GameData));
        }

        [Fact]
        public void Queue_TakePendingDrainsAndSkipsEmpty()
        {
            StartupNotices.Enqueue(null);
            StartupNotices.Enqueue("");
            StartupNotices.Enqueue("a");
            StartupNotices.Enqueue("b");

            Assert.Equal(2, StartupNotices.PendingCount);
            Assert.Equal(new[] { "a", "b" }, StartupNotices.TakePending());
            Assert.Equal(0, StartupNotices.PendingCount);
        }

        [Theory]
        [InlineData(GameScenes.MAINMENU, true)]
        [InlineData(GameScenes.SPACECENTER, true)]
        [InlineData(GameScenes.FLIGHT, true)]
        [InlineData(GameScenes.EDITOR, true)]
        [InlineData(GameScenes.TRACKSTATION, true)]
        [InlineData(GameScenes.LOADING, false)]
        [InlineData(GameScenes.LOADINGBUFFER, false)]
        [InlineData(GameScenes.SETTINGS, false)]
        public void IsPostableScene(GameScenes scene, bool expected)
        {
            Assert.Equal(expected, StartupNotices.IsPostableScene(scene));
        }
    }
}
