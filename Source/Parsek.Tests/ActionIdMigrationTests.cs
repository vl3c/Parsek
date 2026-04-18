using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Guards against drift in the GameAction.ActionId migration (design doc
    /// section 5.6 + 9):
    ///
    ///  - new actions auto-assign a fresh <c>act_*</c> id at construction,
    ///  - serializing writes it out, deserializing reads it back unchanged,
    ///  - legacy GAME_ACTION nodes without <c>actionId</c> get a deterministic
    ///    hash-based id from (UT, Type, RecordingId, Sequence); identical
    ///    inputs produce identical ids (idempotent),
    ///  - the one-shot <c>[Ledger] Assigned deterministic ActionIds</c> Info
    ///    line fires after load when the counter is non-zero.
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

        private static ConfigNode LegacyActionNode(double ut, GameActionType type, string recordingId, int seq)
        {
            var ic = CultureInfo.InvariantCulture;
            var node = new ConfigNode("GAME_ACTION");
            node.AddValue("ut", ut.ToString("R", ic));
            node.AddValue("type", ((int)type).ToString(ic));
            if (recordingId != null)
                node.AddValue("recordingId", recordingId);
            if (seq != 0)
                node.AddValue("seq", seq.ToString(ic));
            // Deliberately no `actionId` — simulates a pre-feature save.
            return node;
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
            Assert.Equal(0, Ledger.LegacyActionIdMigrationCount);
        }

        [Fact]
        public void LegacyGameAction_WithoutActionId_GetsDeterministicLegacyId()
        {
            var node = LegacyActionNode(300.0, GameActionType.ReputationPenalty, "rec_1", 2);

            var first = GameAction.DeserializeFrom(node);
            Assert.StartsWith("act_legacy_", first.ActionId);
            Assert.Equal(11 + 16, first.ActionId.Length);
            Assert.Equal(1, Ledger.LegacyActionIdMigrationCount);

            // Same inputs -> same id (idempotent)
            var second = GameAction.DeserializeFrom(node);
            Assert.Equal(first.ActionId, second.ActionId);
            Assert.Equal(2, Ledger.LegacyActionIdMigrationCount);
        }

        [Fact]
        public void LegacyActionId_DifferentInputs_ProduceDifferentIds()
        {
            string a = GameAction.ComputeLegacyActionId(100.0, GameActionType.FundsEarning, "rec_1", 0);
            string b = GameAction.ComputeLegacyActionId(100.0, GameActionType.FundsEarning, "rec_1", 1);
            string c = GameAction.ComputeLegacyActionId(100.0, GameActionType.FundsEarning, "rec_2", 0);
            string d = GameAction.ComputeLegacyActionId(101.0, GameActionType.FundsEarning, "rec_1", 0);
            string e = GameAction.ComputeLegacyActionId(100.0, GameActionType.FundsSpending, "rec_1", 0);

            Assert.NotEqual(a, b);
            Assert.NotEqual(a, c);
            Assert.NotEqual(a, d);
            Assert.NotEqual(a, e);
        }

        [Fact]
        public void LegacyActionIdMigration_EmitsOneShotInfoLog_OnceOnly()
        {
            GameAction.DeserializeFrom(
                LegacyActionNode(100.0, GameActionType.ReputationPenalty, null, 0));
            GameAction.DeserializeFrom(
                LegacyActionNode(200.0, GameActionType.ReputationPenalty, null, 0));
            Assert.Equal(2, Ledger.LegacyActionIdMigrationCount);

            logLines.Clear();
            Ledger.EmitLegacyActionIdMigrationLogOnce();
            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]") && l.Contains("Assigned deterministic ActionIds") &&
                l.Contains("2 legacy actions"));

            logLines.Clear();
            Ledger.EmitLegacyActionIdMigrationLogOnce();
            Assert.DoesNotContain(logLines, l =>
                l.Contains("Assigned deterministic ActionIds"));
        }
    }
}
