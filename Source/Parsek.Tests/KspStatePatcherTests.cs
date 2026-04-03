using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for KspStatePatcher. Since KSP singletons (Funding.Instance,
    /// ResearchAndDevelopment.Instance, Reputation.Instance, ScenarioUpgradeableFacilities)
    /// are null in the test environment, these tests verify that null singletons are
    /// handled gracefully without crashing, and that logging occurs as expected.
    /// </summary>
    [Collection("Sequential")]
    public class KspStatePatcherTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public KspStatePatcherTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            // Ensure suppression flags start clean
            GameStateRecorder.SuppressResourceEvents = false;
            GameStateRecorder.IsReplayingActions = false;

            // FindObjectsOfType crashes outside Unity
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            KspStatePatcher.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GameStateRecorder.SuppressResourceEvents = false;
            GameStateRecorder.IsReplayingActions = false;
        }

        // ================================================================
        // PatchScience — null singleton
        // ================================================================

        [Fact]
        public void PatchScience_NullSingleton_DoesNotCrash()
        {
            // ResearchAndDevelopment.Instance is null in test environment
            var module = new ScienceModule();
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.ScienceEarning,
                SubjectId = "test@subject",
                ScienceAwarded = 10f,
                SubjectMaxValue = 50f
            });

            // Should not throw — null singleton is handled gracefully
            KspStatePatcher.PatchScience(module);

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("ResearchAndDevelopment.Instance is null"));
        }

        [Fact]
        public void PatchScience_NullModule_LogsWarning()
        {
            KspStatePatcher.PatchScience(null);

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("null ScienceModule"));
        }

        // ================================================================
        // PatchFunds — null singleton
        // ================================================================

        [Fact]
        public void PatchFunds_NullSingleton_DoesNotCrash()
        {
            var module = new FundsModule();

            KspStatePatcher.PatchFunds(module);

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("Funding.Instance is null"));
        }

        // ================================================================
        // PatchReputation — null singleton
        // ================================================================

        [Fact]
        public void PatchReputation_NullSingleton_DoesNotCrash()
        {
            var module = new ReputationModule();

            KspStatePatcher.PatchReputation(module);

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("Reputation.Instance is null"));
        }

        // ================================================================
        // PatchFacilities — null protoUpgradeables
        // ================================================================

        [Fact]
        public void PatchFacilities_NullModule_LogsWarning()
        {
            KspStatePatcher.PatchFacilities(null);

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("null FacilitiesModule"));
        }

        // ================================================================
        // PatchAll — suppression flags
        // ================================================================

        // ================================================================
        // PatchScience — per-subject patching with null singleton
        // ================================================================

        [Fact]
        public void PatchScience_NullSingleton_SkipsPerSubjectPatching()
        {
            // ResearchAndDevelopment.Instance is null in test environment.
            // PatchScience should skip entirely (including per-subject patching) without crashing.
            var module = new ScienceModule();
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.ScienceEarning,
                SubjectId = "crewReport@KerbinSrfLanded",
                ScienceAwarded = 5f,
                SubjectMaxValue = 10f
            });
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.ScienceEarning,
                SubjectId = "temperatureScan@KerbinSrfLanded",
                ScienceAwarded = 8f,
                SubjectMaxValue = 16f
            });

            Assert.Equal(2, module.SubjectCount);

            KspStatePatcher.PatchScience(module);

            // Should log that R&D is null — per-subject patching is not reached
            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("ResearchAndDevelopment.Instance is null"));
        }

        [Fact]
        public void PatchPerSubjectScience_NullModule_LogsWarning()
        {
            KspStatePatcher.PatchPerSubjectScience(null);

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("null ScienceModule"));
        }

        [Fact]
        public void PatchPerSubjectScience_NullSingleton_SkipsGracefully()
        {
            // ResearchAndDevelopment.Instance is null in test environment
            var module = new ScienceModule();
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.ScienceEarning,
                SubjectId = "test@subject",
                ScienceAwarded = 10f,
                SubjectMaxValue = 50f
            });

            KspStatePatcher.PatchPerSubjectScience(module);

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("ResearchAndDevelopment.Instance is null"));
        }

        // ================================================================
        // PatchFacilities — destruction state with no DestructibleBuildings
        // (Full integration testing of Demolish/Repair requires in-game verification
        //  since DestructibleBuilding is a Unity MonoBehaviour unavailable in tests)
        // ================================================================

        [Fact]
        public void PatchFacilities_WithFacilities_SkipsDestructionPatchingInTests()
        {
            // protoUpgradeables is an empty dict in the test environment (not null),
            // so the level loop runs but finds nothing. Destruction patching is suppressed
            // via SuppressUnityCallsForTesting (FindObjectsOfType crashes outside Unity).
            var module = new FacilitiesModule();
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.FacilityDestruction,
                FacilityId = "SpaceCenter/LaunchPad/%.%.%.%"
            });

            Assert.True(module.IsFacilityDestroyed("SpaceCenter/LaunchPad/%.%.%.%"));

            KspStatePatcher.PatchFacilities(module);

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("SuppressUnityCallsForTesting"));
        }

        // ================================================================
        // PatchAll — suppression flags
        // ================================================================

        [Fact]
        public void PatchAll_SetsSuppressFlagsAndRestores()
        {
            // Verify flags are clean before
            Assert.False(GameStateRecorder.SuppressResourceEvents);
            Assert.False(GameStateRecorder.IsReplayingActions);

            var science = new ScienceModule();
            var funds = new FundsModule();
            var reputation = new ReputationModule();
            var milestones = new MilestonesModule();
            var facilities = new FacilitiesModule();

            KspStatePatcher.PatchAll(science, funds, reputation, milestones, facilities);

            // Flags must be restored after PatchAll
            Assert.False(GameStateRecorder.SuppressResourceEvents);
            Assert.False(GameStateRecorder.IsReplayingActions);

            // Should have logged completion
            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("PatchAll complete"));
        }
    }
}
