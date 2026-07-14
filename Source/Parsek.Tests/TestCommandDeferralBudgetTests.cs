using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the deferral-budget model
    /// (<see cref="DeferralBudget"/>). Guards that a never-satisfiable command
    /// converts to TIMEOUT once past its per-verb budget and the pump advances,
    /// rather than wedging the run forever. Also pins the per-verb budgets
    /// (default 60s, LoadGame 300s, StartRecording scene-wait, RunTests scenario
    /// budget with fallback).
    /// </summary>
    public class TestCommandDeferralBudgetTests
    {
        [Fact]
        public void Default_Is60()
        {
            Assert.Equal(60.0, DeferralBudget.BudgetSeconds("MissionMark"));
            Assert.Equal(60.0, DeferralBudget.BudgetSeconds("SetSetting"));
        }

        [Fact]
        public void LoadGame_Is300()
        {
            Assert.Equal(300.0, DeferralBudget.BudgetSeconds("LoadGame"));
        }

        [Fact]
        public void StartRecording_UsesSceneWaitBudget_GreaterThanDefault()
        {
            double budget = DeferralBudget.BudgetSeconds("StartRecording");
            Assert.Equal(DeferralBudget.StartRecordingSceneWaitSeconds, budget);
            Assert.True(budget > DeferralBudget.DefaultSeconds);
        }

        [Fact]
        public void RunTests_UsesScenarioBudget_WhenProvided()
        {
            Assert.Equal(1200.0, DeferralBudget.BudgetSeconds("RunTests", 1200.0));
        }

        [Fact]
        public void RunTests_FallsBack_WhenNoScenarioBudget()
        {
            Assert.Equal(DeferralBudget.RunTestsFallbackSeconds, DeferralBudget.BudgetSeconds("RunTests"));
        }

        [Fact]
        public void C1Verbs_UseTheirBudgets()
        {
            // M-C1 batch-1 budgets (design M-A5 driver integration table).
            Assert.Equal(300.0, DeferralBudget.BudgetSeconds("InvokeRewind"));
            Assert.Equal(120.0, DeferralBudget.BudgetSeconds("AnswerMergeDialog"));
            Assert.Equal(120.0, DeferralBudget.BudgetSeconds("TimeJump"));
            // KscAction covers only the career-ready / SPACECENTER wait: the default 60s.
            Assert.Equal(DeferralBudget.DefaultSeconds, DeferralBudget.BudgetSeconds("KscAction"));
        }

        [Fact]
        public void ShouldTimeout_False_WithinBudget()
        {
            // First deferred at t=100, now t=140, budget 60 -> not yet.
            Assert.False(DeferralBudget.ShouldTimeout(100.0, 140.0, 60.0));
        }

        [Fact]
        public void ShouldTimeout_True_AtBudgetBoundary()
        {
            Assert.True(DeferralBudget.ShouldTimeout(100.0, 160.0, 60.0));
        }

        [Fact]
        public void ShouldTimeout_True_PastBudget()
        {
            Assert.True(DeferralBudget.ShouldTimeout(100.0, 500.0, 60.0));
        }

        [Fact]
        public void ShouldTimeout_Converts_OverLoadGameBudget()
        {
            // A LoadGame stuck deferring past 300s converts and advances.
            double budget = DeferralBudget.BudgetSeconds("LoadGame");
            Assert.False(DeferralBudget.ShouldTimeout(0.0, 299.0, budget));
            Assert.True(DeferralBudget.ShouldTimeout(0.0, 301.0, budget));
        }
    }
}
