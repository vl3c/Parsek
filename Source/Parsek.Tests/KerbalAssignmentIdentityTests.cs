using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// TOMBSTONED-DEATH-RESURRECTS-ON-RELOAD-AFTER-A-RP-SPLIT, rulings of 2026-09-23:
    /// a KerbalAssignment row's identity is (RecordingId, KerbalName). Load-time
    /// re-derivation keeps the replaced row's ActionId (ruling a1), commit-time dedup
    /// matches on that identity without a UT window, and the endUT screen logs a death
    /// whose float EndUT cannot be placed on either side of the rewind cutoff (ruling 4).
    /// </summary>
    [Collection("Sequential")]
    public class KerbalAssignmentIdentityTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public KerbalAssignmentIdentityTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        private static GameAction Row(string rec, string kerbal, double ut, KerbalEndState state,
            float endUT = 53f, string id = null)
        {
            var a = new GameAction
            {
                Type = GameActionType.KerbalAssignment,
                RecordingId = rec,
                KerbalName = kerbal,
                KerbalRole = "Pilot",
                UT = ut,
                StartUT = (float)ut,
                EndUT = endUT,
                KerbalEndStateField = state,
            };
            if (id != null) a.ActionId = id;
            return a;
        }

        // ---------------------------------------------------------------- a1

        [Fact]
        public void Inherit_SameRecordingSameKerbal_TakesTheReplacedRowsId()
        {
            var existing = new List<GameAction> { Row("rec_tip", "Bill Kerman", 8.0, KerbalEndState.Dead, id: "act_old") };
            var desired = new List<GameAction> { Row("rec_tip", "Bill Kerman", 34.0, KerbalEndState.Dead) };

            int fresh;
            int inherited = LedgerOrchestrator.InheritKerbalAssignmentActionIds(existing, desired, out fresh);

            Assert.Equal(1, inherited);
            Assert.Equal(0, fresh);
            Assert.Equal("act_old", desired[0].ActionId);
            // Content follows the derivation; only the identity is kept.
            Assert.Equal(34.0, desired[0].UT);
        }

        [Fact]
        public void Inherit_SeveralRowsPerKerbal_PairsInOrder_NoIdIsShared()
        {
            var existing = new List<GameAction>
            {
                Row("rec", "Bill Kerman", 8.0, KerbalEndState.Aboard, id: "act_b1"),
                Row("rec", "Bob Kerman", 8.0, KerbalEndState.Aboard, id: "act_o1"),
                Row("rec", "Bill Kerman", 9.0, KerbalEndState.Aboard, id: "act_b2"),
            };
            var desired = new List<GameAction>
            {
                Row("rec", "Bill Kerman", 10.0, KerbalEndState.Dead),
                Row("rec", "Bill Kerman", 10.0, KerbalEndState.Dead),
                Row("rec", "Bill Kerman", 10.0, KerbalEndState.Dead),
                Row("rec", "Bob Kerman", 10.0, KerbalEndState.Dead),
            };

            int fresh;
            int inherited = LedgerOrchestrator.InheritKerbalAssignmentActionIds(existing, desired, out fresh);

            Assert.Equal(3, inherited);
            Assert.Equal(1, fresh);
            Assert.Equal("act_b1", desired[0].ActionId);
            Assert.Equal("act_b2", desired[1].ActionId);
            Assert.StartsWith("act_", desired[2].ActionId);
            Assert.NotEqual("act_b1", desired[2].ActionId);
            Assert.NotEqual("act_b2", desired[2].ActionId);
            Assert.Equal("act_o1", desired[3].ActionId);
            Assert.Equal(desired.Count, desired.Select(a => a.ActionId).Distinct().Count());
        }

        [Fact]
        public void Inherit_Deterministic_SameInputsSameIds()
        {
            Func<List<GameAction>> existing = () => new List<GameAction>
            {
                Row("rec", "Bill Kerman", 8.0, KerbalEndState.Dead, id: "act_x"),
                Row("rec", "Bill Kerman", 8.0, KerbalEndState.Dead, id: "act_y"),
            };
            var first = new List<GameAction> { Row("rec", "Bill Kerman", 1, KerbalEndState.Dead), Row("rec", "Bill Kerman", 1, KerbalEndState.Dead) };
            var second = new List<GameAction> { Row("rec", "Bill Kerman", 1, KerbalEndState.Dead), Row("rec", "Bill Kerman", 1, KerbalEndState.Dead) };
            int fresh;
            LedgerOrchestrator.InheritKerbalAssignmentActionIds(existing(), first, out fresh);
            LedgerOrchestrator.InheritKerbalAssignmentActionIds(existing(), second, out fresh);
            Assert.Equal(first.Select(a => a.ActionId), second.Select(a => a.ActionId));
            Assert.Equal(new[] { "act_x", "act_y" }, first.Select(a => a.ActionId));
        }

        [Fact]
        public void Inherit_DifferentKerbalOrRecording_KeepsFreshId()
        {
            var existing = new List<GameAction>
            {
                Row("rec_a", "Bill Kerman", 8.0, KerbalEndState.Dead, id: "act_other_rec"),
                Row("rec_b", "Jeb Kerman", 8.0, KerbalEndState.Dead, id: "act_other_kerbal"),
            };
            var desired = new List<GameAction> { Row("rec_b", "Bill Kerman", 8.0, KerbalEndState.Dead) };
            string freshId = desired[0].ActionId;

            int fresh;
            Assert.Equal(0, LedgerOrchestrator.InheritKerbalAssignmentActionIds(existing, desired, out fresh));
            Assert.Equal(1, fresh);
            Assert.Equal(freshId, desired[0].ActionId);
        }

        [Fact]
        public void Inherit_NullOrEmptyLists_AreNoOps()
        {
            int fresh;
            Assert.Equal(0, LedgerOrchestrator.InheritKerbalAssignmentActionIds(null, null, out fresh));
            Assert.Equal(0, fresh);
            var desired = new List<GameAction> { Row("rec", "Bill Kerman", 8.0, KerbalEndState.Dead) };
            Assert.Equal(0, LedgerOrchestrator.InheritKerbalAssignmentActionIds(null, desired, out fresh));
            Assert.Equal(1, fresh);
            Assert.Equal(0, LedgerOrchestrator.InheritKerbalAssignmentActionIds(
                new List<GameAction> { Row("rec", "Bill Kerman", 8.0, KerbalEndState.Dead, id: "act_z") },
                new List<GameAction>(), out fresh));
        }

        // ---------------------------------------------------------------- commit dedup

        [Fact]
        public void Dedup_KerbalAssignment_SameIdentityAtDifferentUt_IsADuplicate()
        {
            // The retagged TIP row carries the origin's UT (8); a re-commit of TIP derives
            // UT 34. Same (recording, kerbal) = same row.
            Ledger.AddAction(Row("rec_tip", "Bill Kerman", 8.0, KerbalEndState.Dead, id: "act_retired"));
            var result = LedgerOrchestrator.DeduplicateAgainstLedger(
                new List<GameAction> { Row("rec_tip", "Bill Kerman", 34.0, KerbalEndState.Dead) });
            Assert.Empty(result);
        }

        [Fact]
        public void Dedup_KerbalAssignment_OtherRecordingSameKerbal_IsNotADuplicate()
        {
            Ledger.AddAction(Row("rec_a", "Bill Kerman", 131.88, KerbalEndState.Dead));
            var result = LedgerOrchestrator.DeduplicateAgainstLedger(
                new List<GameAction> { Row("rec_b", "Bill Kerman", 131.90, KerbalEndState.Dead) });
            Assert.Single(result);
        }

        [Fact]
        public void Dedup_NonKerbalType_StillUsesTheUtWindow()
        {
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.MilestoneAchievement, MilestoneId = "FirstLaunch", UT = 10.0,
            });
            var farApart = LedgerOrchestrator.DeduplicateAgainstLedger(new List<GameAction>
            {
                new GameAction { Type = GameActionType.MilestoneAchievement, MilestoneId = "FirstLaunch", UT = 50.0 },
            });
            Assert.Single(farApart);
        }

        // ---------------------------------------------------------------- ruling 4

        [Fact]
        public void FloatStepAt_MatchesSinglePrecisionSpacing()
        {
            Assert.Equal(2.0, TombstoneAttributionHelper.FloatStepAt(2e7));
            Assert.True(TombstoneAttributionHelper.FloatStepAt(34.0) < 1e-5);
            Assert.True(double.IsNaN(TombstoneAttributionHelper.FloatStepAt(double.NaN)));
        }

        [Fact]
        public void NearCutoffDeath_Detected_OnlyInsideOneFloatStep()
        {
            double step;
            var near = Row("rec", "Bill Kerman", 1e7, KerbalEndState.Dead, endUT: (float)(2e7 + 1.0));
            Assert.True(TombstoneAttributionHelper.IsDeathEndUTWithinFloatStepOfCutoff(near, 2e7, out step));
            Assert.Equal(2.0, step);

            var far = Row("rec", "Bill Kerman", 1e7, KerbalEndState.Dead, endUT: (float)(2e7 + 8.0));
            Assert.False(TombstoneAttributionHelper.IsDeathEndUTWithinFloatStepOfCutoff(far, 2e7, out step));

            var alive = Row("rec", "Bill Kerman", 1e7, KerbalEndState.Recovered, endUT: (float)2e7);
            Assert.False(TombstoneAttributionHelper.IsDeathEndUTWithinFloatStepOfCutoff(alive, 2e7, out step));
            Assert.False(TombstoneAttributionHelper.IsDeathEndUTWithinFloatStepOfCutoff(near, double.NaN, out step));
        }

        private static ParsekScenario ScenarioWithMarker(double rewindUT)
        {
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_ulp", TreeId = "tree_ulp", ActiveReFlyRecordingId = "rec_fork",
                OriginChildRecordingId = "rec_tip", SupersedeTargetId = "rec_tip",
                RewindPointUT = rewindUT, InvokedUT = rewindUT,
            };
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = marker,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            return scenario;
        }

        [Fact]
        public void CommitTombstones_DeathWithinOneFloatStepOfCutoff_LogsWarn()
        {
            const double cutoff = 2e7;
            var scenario = ScenarioWithMarker(cutoff);
            Ledger.AddAction(Row("rec_tip", "Bill Kerman", 1.9e7, KerbalEndState.Dead,
                endUT: (float)(cutoff + 1.0), id: "act_near"));

            SupersedeCommit.CommitTombstones(scenario.ActiveReFlySessionMarker,
                new List<string> { "rec_tip" }, "rec_fork", cutoff, "2026-09-23T00:00:00Z", scenario);

            Assert.Contains(logLines, l => l.Contains("[WARN]")
                && l.Contains("death endUT within one float step of the cutoff")
                && l.Contains("action=act_near") && l.Contains("floatStep=2"));
        }

        [Fact]
        public void CommitTombstones_OrdinaryDeath_LogsNoFloatStepWarn()
        {
            var scenario = ScenarioWithMarker(34.0);
            Ledger.AddAction(Row("rec_tip", "Bill Kerman", 8.0, KerbalEndState.Dead,
                endUT: 53f, id: "act_far"));

            SupersedeCommit.CommitTombstones(scenario.ActiveReFlySessionMarker,
                new List<string> { "rec_tip" }, "rec_fork", 34.0, "2026-09-23T00:00:00Z", scenario);

            Assert.DoesNotContain(logLines, l => l.Contains("within one float step"));
            Assert.Contains(scenario.LedgerTombstones, t => t.ActionId == "act_far");
        }
    }
}
