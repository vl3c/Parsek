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
        public void PatchFacilities_EmptyProtoUpgradeables_CompletesWithoutCrash()
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

        // ================================================================
        // #455 — IsRepeatableRecordType type pre-filter + missing-stock-field WARN silence
        //
        // Before the fix, PatchRepeatableRecordNode probed reflection for record/
        // rewardThreshold/rewardInterval on every ProgressNode and WARN'd when any
        // were missing. For the 392 per-body one-shots (Bop/Orbit, Dres/Flight, …)
        // none of those fields exist, producing 3,136 WARN lines in a single playtest
        // and short-circuiting the one-shot patch path. The pre-filter distinguishes
        // "not a record subclass" (return false silently, let the one-shot path run)
        // from "is a record subclass but missing stock fields" (keep the WARN).
        // ================================================================

        /// <summary>
        /// Synthetic stand-ins for KSPAchievements.RecordsAltitude/Depth/Speed/Distance.
        /// The production code only treats the exact stock full names as repeatable record
        /// types, but <see cref="KspStatePatcher.SuppressUnityCallsForTesting"/> enables a
        /// simple-name fallback so xUnit can still exercise the malformed-record WARN branch.
        /// None of these fakes carry the stock <c>record</c>/<c>rewardThreshold</c>/
        /// <c>rewardInterval</c> fields.
        /// </summary>
        private class RecordsAltitude : ProgressNode
        {
            public RecordsAltitude() : base("RecordsAltitude", startReached: false) { }
        }
        private class RecordsDepth : ProgressNode
        {
            public RecordsDepth() : base("RecordsDepth", startReached: false) { }
        }
        private class RecordsSpeed : ProgressNode
        {
            public RecordsSpeed() : base("RecordsSpeed", startReached: false) { }
        }
        private class RecordsDistance : ProgressNode
        {
            public RecordsDistance() : base("RecordsDistance", startReached: false) { }
        }

        /// <summary>
        /// Stand-in for one-shot per-body nodes (Orbit, Landing, Flyby, Flight, …).
        /// Any class name other than the four <c>Records*</c> names must drop out of
        /// the repeatable-record branch.
        /// </summary>
        private class OrbitNode : ProgressNode
        {
            public OrbitNode() : base("Orbit", startReached: false) { }
        }
        private class FlightNode : ProgressNode
        {
            public FlightNode() : base("Flight", startReached: false) { }
        }

        [Fact]
        public void IsRepeatableRecordType_NullNode_ReturnsFalse()
        {
            Assert.False(KspStatePatcher.IsRepeatableRecordType(null));
        }

        [Fact]
        public void IsRepeatableRecordType_RecordsSubclasses_ReturnTrue()
        {
            Assert.True(KspStatePatcher.IsRepeatableRecordType(new KSPAchievements.RecordsAltitude()));
            Assert.True(KspStatePatcher.IsRepeatableRecordType(new KSPAchievements.RecordsDepth()));
            Assert.True(KspStatePatcher.IsRepeatableRecordType(new KSPAchievements.RecordsSpeed()));
            Assert.True(KspStatePatcher.IsRepeatableRecordType(new KSPAchievements.RecordsDistance()));
        }

        [Fact]
        public void IsRepeatableRecordType_OneShotSubclasses_ReturnFalse()
        {
            Assert.False(KspStatePatcher.IsRepeatableRecordType(new OrbitNode()));
            Assert.False(KspStatePatcher.IsRepeatableRecordType(new FlightNode()));
        }

        [Fact]
        public void IsRepeatableRecordType_NameCollisionOutsideTestMode_ReturnsFalse()
        {
            KspStatePatcher.SuppressUnityCallsForTesting = false;

            Assert.False(KspStatePatcher.IsRepeatableRecordType(new RecordsAltitude()));
        }

        [Fact]
        public void PatchRepeatableRecordNode_NonRecordType_ReturnsFalseAndDoesNotWarn()
        {
            // The playtest log signature was 3,136 copies of:
            //   [Parsek][WARN][KspStatePatcher] PatchMilestones: repeatable node 'Bop/Orbit' is missing stock record fields (…) — skipping
            // After the fix the WARN must be gone AND the caller's one-shot path must
            // get a chance to run, which requires the function to return false (not true).
            var reachedField = typeof(ProgressNode).GetField("reached",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var completeField = typeof(ProgressNode).GetField("complete",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(reachedField);
            Assert.NotNull(completeField);

            var node = new OrbitNode();
            bool recognized = KspStatePatcher.PatchRepeatableRecordNode(
                node, effectiveCount: 0, qualifiedId: "Bop/Orbit",
                reachedField, completeField,
                authoritativeRepeatableRecordState: false,
                out bool changed);

            Assert.False(recognized); // falls through to one-shot branch in PatchProgressNodeTree
            Assert.False(changed);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("is missing stock record fields"));
        }

        [Fact]
        public void PatchRepeatableRecordNode_RecordsSubclassWithoutStockFields_StillWarns()
        {
            // The WARN must survive for genuinely-record types that drift away from
            // the stock API — that is a real structural change and should page someone.
            // Our synthetic RecordsAltitude has the matching class name but no stock
            // record/rewardThreshold/rewardInterval fields, so the reflection-miss
            // branch is reachable without a live KSP runtime.
            var reachedField = typeof(ProgressNode).GetField("reached",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var completeField = typeof(ProgressNode).GetField("complete",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(reachedField);
            Assert.NotNull(completeField);

            var node = new RecordsAltitude();
            bool recognized = KspStatePatcher.PatchRepeatableRecordNode(
                node, effectiveCount: 0, qualifiedId: "RecordsAltitude",
                reachedField, completeField,
                authoritativeRepeatableRecordState: false,
                out bool changed);

            // Genuine "recognized-but-malformed" case: true so the caller counts it as
            // handled, but the WARN must still fire as a structural-change alarm.
            Assert.True(recognized);
            Assert.False(changed);
            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]")
                && l.Contains("RecordsAltitude")
                && l.Contains("is missing stock record fields"));
        }
    }
}
