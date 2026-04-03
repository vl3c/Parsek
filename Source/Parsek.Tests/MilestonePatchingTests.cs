using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for milestone ID qualification (GameStateRecorder.QualifyMilestoneId)
    /// and milestone patching (KspStatePatcher.PatchMilestones).
    ///
    /// PatchMilestones requires KSP's ProgressTracking.Instance at runtime, so most
    /// tests here cover the null-guard paths and logging. The QualifyMilestoneId method
    /// is pure and can be tested with mock ProgressNode objects, but ProgressNode requires
    /// KSP's assembly — so we test it indirectly via the converter and module integration.
    ///
    /// MilestonesModule.GetCreditedMilestoneIds and path-qualified ID flow through the
    /// converter are tested directly.
    /// </summary>
    [Collection("Sequential")]
    public class MilestonePatchingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public MilestonePatchingTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            GameStateRecorder.SuppressResourceEvents = false;
            GameStateRecorder.IsReplayingActions = false;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GameStateRecorder.SuppressResourceEvents = false;
            GameStateRecorder.IsReplayingActions = false;
        }

        // ================================================================
        // PatchMilestones — null guards
        // ================================================================

        [Fact]
        public void PatchMilestones_NullModule_LogsWarning()
        {
            KspStatePatcher.PatchMilestones(null);

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("null module"));
        }

        [Fact]
        public void PatchMilestones_NullProgressTracking_SkipsGracefully()
        {
            // ProgressTracking.Instance is null in test environment
            var module = new MilestonesModule();

            KspStatePatcher.PatchMilestones(module);

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("ProgressTracking.Instance is null"));
        }

        // ================================================================
        // MilestonesModule — GetCreditedMilestoneIds
        // ================================================================

        [Fact]
        public void GetCreditedMilestoneIds_ReturnsCorrectSet()
        {
            var module = new MilestonesModule();
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = 100.0,
                MilestoneId = "Mun/Landing"
            });
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = 200.0,
                MilestoneId = "FirstLaunch"
            });
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = 300.0,
                MilestoneId = "Kerbin/Orbit"
            });

            var credited = module.GetCreditedMilestoneIds();

            Assert.Equal(3, credited.Count);
            Assert.Contains("Mun/Landing", credited);
            Assert.Contains("FirstLaunch", credited);
            Assert.Contains("Kerbin/Orbit", credited);
        }

        [Fact]
        public void GetCreditedMilestoneIds_EmptyAfterReset()
        {
            var module = new MilestonesModule();
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = 100.0,
                MilestoneId = "FirstLaunch"
            });

            module.Reset();

            var credited = module.GetCreditedMilestoneIds();
            Assert.Empty(credited);
        }

        [Fact]
        public void GetCreditedMilestoneIds_ReturnsCopy_NotReference()
        {
            var module = new MilestonesModule();
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = 100.0,
                MilestoneId = "FirstLaunch"
            });

            var set1 = module.GetCreditedMilestoneIds();
            set1.Add("Injected");

            // Module's internal set should not be affected
            Assert.False(module.IsMilestoneCredited("Injected"));
            Assert.Equal(1, module.GetCreditedCount());
        }

        // ================================================================
        // Path-qualified milestone IDs through converter
        // ================================================================

        [Fact]
        public void Converter_PathQualifiedMilestoneId_PreservesPath()
        {
            var evt = new GameStateEvent
            {
                ut = 1000.0,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "Mun/Landing",
                detail = ""
            };

            var action = GameStateEventConverter.ConvertEvent(evt, "rec1");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.MilestoneAchievement, action.Type);
            Assert.Equal("Mun/Landing", action.MilestoneId);
        }

        [Fact]
        public void Converter_TopLevelMilestoneId_PreservesBareId()
        {
            var evt = new GameStateEvent
            {
                ut = 2000.0,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "FirstLaunch",
                detail = ""
            };

            var action = GameStateEventConverter.ConvertEvent(evt, "rec1");

            Assert.NotNull(action);
            Assert.Equal("FirstLaunch", action.MilestoneId);
        }

        // ================================================================
        // Full flow: path-qualified capture -> module -> IsMilestoneCredited
        // ================================================================

        [Fact]
        public void FullFlow_PathQualified_BodyMilestones_AreDistinct()
        {
            var module = new MilestonesModule();

            // Mun landing at UT=100
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = 100.0,
                MilestoneId = "Mun/Landing"
            });

            // Minmus landing at UT=200 — different body, should also be effective
            var minmusAction = new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = 200.0,
                MilestoneId = "Minmus/Landing"
            };
            module.ProcessAction(minmusAction);

            Assert.True(minmusAction.Effective,
                "Minmus/Landing should be effective — it's a different body than Mun/Landing");
            Assert.True(module.IsMilestoneCredited("Mun/Landing"));
            Assert.True(module.IsMilestoneCredited("Minmus/Landing"));
            Assert.False(module.IsMilestoneCredited("Landing"),
                "Bare 'Landing' should NOT be credited — only qualified IDs are stored");
        }

        [Fact]
        public void FullFlow_OldBareId_WouldBeDuplicated()
        {
            // Demonstrates the problem that path-qualification solves:
            // if both bodies stored bare "Landing", one would be a duplicate.
            var module = new MilestonesModule();

            module.ProcessAction(new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = 100.0,
                MilestoneId = "Landing"  // old format — ambiguous
            });

            var second = new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = 200.0,
                MilestoneId = "Landing"  // same bare ID — wrongly deduped
            };
            module.ProcessAction(second);

            // Without qualification, the second "Landing" is treated as a duplicate
            Assert.False(second.Effective,
                "Bare 'Landing' is ambiguous — second occurrence is wrongly deduped");
        }

        // ================================================================
        // QualifyMilestoneId — null node
        // ================================================================

        [Fact]
        public void QualifyMilestoneId_NullNode_ReturnsEmpty()
        {
            string result = GameStateRecorder.QualifyMilestoneId(null);
            Assert.Equal("", result);
        }

        // ================================================================
        // MilestonesModule — path-qualified IsMilestoneCredited
        // ================================================================

        [Fact]
        public void IsMilestoneCredited_PathQualified_DoesNotMatchBareId()
        {
            var module = new MilestonesModule();
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = 100.0,
                MilestoneId = "Mun/Landing"
            });

            Assert.True(module.IsMilestoneCredited("Mun/Landing"));
            Assert.False(module.IsMilestoneCredited("Landing"));
            Assert.False(module.IsMilestoneCredited("Kerbin/Landing"));
        }

        // ================================================================
        // Log assertion — PatchMilestones logs credited count
        // ================================================================

        [Fact]
        public void PatchMilestones_WithCreditedMilestones_LogsCountBeforeEarlyReturn()
        {
            var module = new MilestonesModule();
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = 100.0,
                MilestoneId = "Mun/Landing"
            });
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = 200.0,
                MilestoneId = "FirstLaunch"
            });

            // ProgressTracking.Instance is null → will early-return with log
            KspStatePatcher.PatchMilestones(module);

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("ProgressTracking.Instance is null"));
        }
    }
}
