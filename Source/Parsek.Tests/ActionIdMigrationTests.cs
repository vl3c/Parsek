using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Guards the GameAction.ActionId contract (design doc section 5.6 + 9):
    /// new actions auto-assign a fresh <c>act_*</c> id at construction, and
    /// serializing writes it out while deserializing reads it back unchanged.
    /// </summary>
    [Collection("Sequential")]
    public class ActionIdMigrationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public ActionIdMigrationTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            Ledger.ResetForTesting();
        }

        [Fact]
        public void NewGameAction_HasAutoAssignedActionId_WithActPrefix()
        {
            var a = new GameAction { UT = 100.0, Type = GameActionType.FundsEarning };
            Assert.False(string.IsNullOrEmpty(a.ActionId));
            Assert.StartsWith("act_", a.ActionId);
        }

        [Fact]
        public void GameAction_ActionId_RoundTripsWhenExplicit()
        {
            var a = new GameAction
            {
                UT = 200.0,
                Type = GameActionType.FundsEarning,
                FundsAwarded = 1000f,
                FundsSource = FundsEarningSource.ContractComplete,
                ActionId = "act_explicit"
            };

            var parent = new ConfigNode("LEDGER");
            a.SerializeInto(parent);
            var node = parent.GetNode("GAME_ACTION");
            Assert.Equal("act_explicit", node.GetValue("actionId"));

            var restored = GameAction.DeserializeFrom(node);
            Assert.Equal("act_explicit", restored.ActionId);
        }
    }
}
