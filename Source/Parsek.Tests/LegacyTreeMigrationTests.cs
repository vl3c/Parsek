using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Covers the <see cref="LedgerOrchestrator.IsResourceImpactingAction"/> classifier.
    ///
    /// <para>The pre-reset Phase A legacy tree-resource residual migration this file
    /// once covered was removed with the schema generation 3 clean-slate reset; the
    /// only surviving member it tested is the resource-impacting classifier, which is
    /// still live (it is part of the general ledger machinery).</para>
    /// </summary>
    [Collection("Sequential")]
    public class LegacyTreeMigrationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public LegacyTreeMigrationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = false;
        }

        // ================================================================
        // IsResourceImpactingAction classifier: exhaustive Theory over every
        // GameActionType value. Forces a code review when a new enum value is
        // added — the default-false branch in the classifier would otherwise
        // silently exclude new action types.
        // ================================================================

        [Theory]
        [InlineData(GameActionType.ScienceEarning,       true)]
        [InlineData(GameActionType.ScienceSpending,      true)]
        [InlineData(GameActionType.FundsEarning,         true)]
        [InlineData(GameActionType.FundsSpending,        true)]
        [InlineData(GameActionType.MilestoneAchievement, true)]
        [InlineData(GameActionType.ContractAccept,       true)]
        [InlineData(GameActionType.ContractComplete,     true)]
        [InlineData(GameActionType.ContractFail,         true)]
        [InlineData(GameActionType.ContractCancel,       true)]
        [InlineData(GameActionType.ReputationEarning,    true)]
        [InlineData(GameActionType.ReputationPenalty,    true)]
        [InlineData(GameActionType.KerbalHire,           true)]
        [InlineData(GameActionType.FacilityUpgrade,      true)]
        [InlineData(GameActionType.FacilityRepair,       true)]
        [InlineData(GameActionType.StrategyActivate,     true)]
        [InlineData(GameActionType.KerbalAssignment,     false)]
        [InlineData(GameActionType.KerbalRescue,         false)]
        [InlineData(GameActionType.KerbalStandIn,        false)]
        [InlineData(GameActionType.FacilityDestruction,  false)]
        [InlineData(GameActionType.StrategyDeactivate,   false)]
        [InlineData(GameActionType.FundsInitial,         false)]
        [InlineData(GameActionType.ScienceInitial,       false)]
        [InlineData(GameActionType.ReputationInitial,    false)]
        public void IsResourceImpactingAction_Theory(GameActionType type, bool expected)
        {
            Assert.Equal(expected, LedgerOrchestrator.IsResourceImpactingAction(type));
        }

        /// <summary>
        /// Pins the enum surface: if a new <see cref="GameActionType"/> value is added,
        /// the InlineData in <c>IsResourceImpactingAction_Theory</c> must be extended
        /// to match. This check fails loudly when it isn't — preventing a silent
        /// default-false exclusion.
        /// </summary>
        [Fact]
        public void IsResourceImpactingAction_Theory_CoversEveryEnumValue()
        {
            var attrs = typeof(LegacyTreeMigrationTests)
                .GetMethod(nameof(IsResourceImpactingAction_Theory))
                .GetCustomAttributes(typeof(InlineDataAttribute), inherit: false)
                .Cast<InlineDataAttribute>();

            var covered = new HashSet<GameActionType>();
            foreach (var a in attrs)
            {
                covered.Add((GameActionType)a.GetData(null).First()[0]);
            }

            var all = Enum.GetValues(typeof(GameActionType)).Cast<GameActionType>().ToList();
            var missing = all.Where(t => !covered.Contains(t)).ToList();

            Assert.True(missing.Count == 0,
                $"IsResourceImpactingAction_Theory InlineData is missing entries for: " +
                $"[{string.Join(", ", missing)}]. Add them with the correct expected value " +
                "and update LedgerOrchestrator.IsResourceImpactingAction's switch.");
        }
    }
}
