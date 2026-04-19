using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using KSP.UI.Screens;
using Parsek.InGameTests;
using Strategies;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class StrategyLifecycleProbeSupportTests : IDisposable
    {
        private readonly Administration priorAdministration = Administration.Instance;

        public void Dispose()
        {
            Administration.Instance = priorAdministration;
        }

        [Fact]
        public void GetGlobalReadinessBlockReason_NullStrategySystem_ReturnsSystemReason()
        {
            Administration.Instance = CreateAdministration();

            string reason = StrategyLifecycleProbeSupport.GetGlobalReadinessBlockReason(
                system: null,
                strategies: new List<Strategy>());

            Assert.Equal(StrategyLifecycleProbeSupport.StrategySystemNotReadyReason, reason);
        }

        [Fact]
        public void GetGlobalReadinessBlockReason_NullStrategyList_ReturnsListReason()
        {
            Administration.Instance = CreateAdministration();

            string reason = StrategyLifecycleProbeSupport.GetGlobalReadinessBlockReason(
                CreateStrategySystem(),
                strategies: null);

            Assert.Equal(StrategyLifecycleProbeSupport.StrategyListNotReadyReason, reason);
        }

        [Fact]
        public void GetGlobalReadinessBlockReason_MissingAdministration_ReturnsAdministrationReason()
        {
            Administration.Instance = null;

            string reason = StrategyLifecycleProbeSupport.GetGlobalReadinessBlockReason(
                CreateStrategySystem(),
                new List<Strategy>());

            Assert.Equal(StrategyLifecycleProbeSupport.AdministrationNotReadyReason, reason);
        }

        [Fact]
        public void GetGlobalReadinessBlockReason_WhenReady_ReturnsNull()
        {
            Administration.Instance = CreateAdministration();

            string reason = StrategyLifecycleProbeSupport.GetGlobalReadinessBlockReason(
                CreateStrategySystem(),
                new List<Strategy>());

            Assert.Null(reason);
        }

        [Fact]
        public void BuildProbeDiagnostic_IncludesCountsAndStack()
        {
            string diagnostic = StrategyLifecycleProbeSupport.BuildProbeDiagnostic(
                strategyCount: 11,
                nullEntries: 0,
                activeEntries: 0,
                configlessEntries: 0,
                namelessEntries: 0,
                blockedEntries: 0,
                probeThrows: 11,
                firstThrowIndex: 7,
                firstThrowSummary: "NullReferenceException: boom",
                firstThrowDetail: "System.NullReferenceException: boom\n at Strategy.CanBeActivated()");

            Assert.Contains("count=11", diagnostic);
            Assert.Contains("probeThrows=11", diagnostic);
            Assert.Contains("firstProbeIndex=7", diagnostic);
            Assert.Contains("firstProbe='NullReferenceException: boom'", diagnostic);
            Assert.Contains("stack=System.NullReferenceException: boom", diagnostic);
        }

        [Fact]
        public void BuildPollExceptionSummary_FormatsFirstFailure()
        {
            string summary = StrategyLifecycleProbeSupport.BuildPollExceptionSummary(
                strategyCount: 11,
                probeThrows: 11,
                firstThrowIndex: 0,
                firstThrowSummary: "NullReferenceException: boom");

            Assert.Contains("11/11", summary);
            Assert.Contains("firstIndex=0", summary);
            Assert.Contains("firstException='NullReferenceException: boom'", summary);
        }

        [Fact]
        public void FormatExceptionSummary_UsesTypeAndMessage()
        {
            var ex = new InvalidOperationException("nope");

            string summary = StrategyLifecycleProbeSupport.FormatExceptionSummary(ex);

            Assert.Equal("InvalidOperationException: nope", summary);
        }

        private static Administration CreateAdministration()
        {
            return (Administration)FormatterServices.GetUninitializedObject(typeof(Administration));
        }

        private static StrategySystem CreateStrategySystem()
        {
            return (StrategySystem)FormatterServices.GetUninitializedObject(typeof(StrategySystem));
        }
    }
}
