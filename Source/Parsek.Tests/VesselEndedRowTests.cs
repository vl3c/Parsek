using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// R3E-12 (design-competitive-multiplayer 7.7, addition A2): the
    /// <see cref="GameActionType.VesselEnded"/> ledger row type. A race fact that a committed
    /// flight's vessel ended outside any recording; it moves no pool and is grouped with
    /// <see cref="GameActionType.VesselRecovered"/> in every ledger switch, but it is NOT spawn
    /// evidence and draws no timeline row, so no single-player decision reads it.
    /// </summary>
    [Collection("Sequential")]
    public class VesselEndedRowTests : IDisposable
    {
        private const string VesselName = "Leftover Lander";

        private readonly List<string> logLines = new List<string>();

        public VesselEndedRowTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            EffectiveState.ResetRecoveredIndexForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            EffectiveState.ResetRecoveredIndexForTesting();
            EffectiveState.ResetCachesForTesting();
            Ledger.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static GameAction EndedRow(string recordingId, VesselEndCause cause = VesselEndCause.Destroyed,
            double ut = 400.0)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.VesselEnded,
                RecordingId = recordingId,
                EndedVesselName = VesselName,
                EndCause = cause,
            };
        }

        private static IEnumerable<GameActionType> RowTypes() =>
            Enum.GetValues(typeof(GameActionType)).Cast<GameActionType>()
                .Where(t => !LedgerLoadMigration.IsReservedRouteActionType(t));

        // ------------------------------------------------------------------
        // The enum member and the codec
        // ------------------------------------------------------------------

        [Fact]
        public void TypeId_IsTheNextAfterKerbalDismissal()
        {
            Assert.Equal(42, (int)GameActionType.VesselEnded);
            Assert.Equal((int)GameActionType.KerbalDismissal + 1, (int)GameActionType.VesselEnded);
            Assert.Equal(1, (int)VesselEndCause.Destroyed);
            Assert.Equal(2, (int)VesselEndCause.Deleted);
            Assert.False(LedgerLoadMigration.IsReservedRouteActionType(GameActionType.VesselEnded));
        }

        [Theory]
        [InlineData(VesselEndCause.Destroyed)]
        [InlineData(VesselEndCause.Deleted)]
        public void Codec_RoundTripsNameCauseAndOwner(VesselEndCause cause)
        {
            var row = EndedRow("rec-owner", cause);
            var parent = new ConfigNode("LEDGER");
            row.SerializeInto(parent);
            ConfigNode node = parent.GetNode("GAME_ACTION");

            Assert.Equal("42", node.GetValue("type"));
            Assert.Equal("rec-owner", node.GetValue("recordingId"));
            Assert.Equal(VesselName, node.GetValue("vesselName"));
            Assert.Equal(((int)cause).ToString(), node.GetValue("endCause"));
            Assert.Null(GameAction.ClassifyUnreadableActionNode(node, out _));

            var read = GameAction.DeserializeFrom(node);
            Assert.Equal(GameActionType.VesselEnded, read.Type);
            Assert.Equal("rec-owner", read.RecordingId);
            Assert.Equal(VesselName, read.EndedVesselName);
            Assert.Equal(cause, read.EndCause);
            Assert.Equal(row.ActionId, read.ActionId);
            Assert.Equal(row.UT, read.UT);
            // The recovery row's fields are not written by this type.
            Assert.Null(read.RecoveredVesselName);
            Assert.Equal(0u, read.RecoveredVesselPid);
        }

        [Fact]
        public void Codec_UndefinedCause_ReadsUnknownAndWarns()
        {
            var node = new ConfigNode("GAME_ACTION");
            node.AddValue("ut", "5");
            node.AddValue("type", "42");
            node.AddValue("vesselName", VesselName);
            node.AddValue("endCause", "9");

            var read = GameAction.DeserializeFrom(node);
            Assert.Equal(GameActionType.VesselEnded, read.Type);
            Assert.Equal(VesselEndCause.Unknown, read.EndCause);
            Assert.Contains(logLines, l => l.Contains("[GameAction]") && l.Contains("VesselEndCause"));
        }

        // ------------------------------------------------------------------
        // Every switch that names VesselRecovered: VesselEnded is in the same group
        // ------------------------------------------------------------------

        // catches: a ledger switch missing the new member and falling into a default that
        // blocks a supersede, keeps the row past a Re-Fly, collapses its dedup key, or claims
        // a pool move.
        [Fact]
        public void EveryLedgerSwitch_TreatsVesselEndedLikeVesselRecovered()
        {
            var ended = EndedRow("rec-a");
            var recovered = new GameAction
            {
                UT = 400.0, Type = GameActionType.VesselRecovered, RecordingId = "rec-a",
                RecoveredVesselName = VesselName,
            };
            var none = new List<GameAction>();

            Assert.False(LedgerLoadMigration.IsResourceImpactingAction(GameActionType.VesselEnded));
            Assert.False(LedgerOrchestrator.IsResourceImpactingAction(GameActionType.VesselEnded));
            Assert.Equal(KscActionExpectationClassifier.KscReconcileClass.NoResourceImpact,
                KscActionExpectationClassifier.ClassifyAction(ended).Class);
            Assert.True(TombstoneEligibility.IsSupersedeTombstoneEligible(ended));
            Assert.Equal(TombstoneEligibility.IsSupersedeTombstoneEligible(recovered),
                TombstoneEligibility.IsSupersedeTombstoneEligible(ended));
            Assert.False(SupersedeCommit.IsWorldStateChangingRecordingAction(ended, none));
            Assert.Equal("rec-a", LedgerOrchestrator.GetActionKey(ended));
            Assert.Equal(LedgerOrchestrator.GetActionKey(recovered), LedgerOrchestrator.GetActionKey(ended));
            Assert.False(SupersedeCommit.IsLossRow(ended));

            LedgerEventIdentityEntry entry;
            Assert.True(LedgerEventIdentityRegistry.TryGet(GameActionType.VesselEnded, null, out entry));
            Assert.Equal(LedgerIdentityRule.NoEconomicEffect, entry.Rule);

            // The pool helpers' defaults already say "no pool"; pin it.
            Assert.False(FundsModule.AffectsFunds(GameActionType.VesselEnded));
            Assert.False(ScienceModule.AffectsScience(GameActionType.VesselEnded));
            Assert.False(ReputationModule.AffectsReputation(GameActionType.VesselEnded));
            Assert.False(ReputationSeedMembership.IsReputationAffectingRow(GameActionType.VesselEnded));
            Assert.False(RecalculationEngine.IsEarningType(GameActionType.VesselEnded));
            Assert.False(RecalculationEngine.IsSpendingType(GameActionType.VesselEnded));
            Assert.False(CommittedFutureIndex.TryClassify(ended, out _, out _));

            Assert.Equal("Vessel lost: " + VesselName, GameActionDisplay.GetDescription(ended, null));
            Assert.Equal("Vessel lost: unknown",
                GameActionDisplay.GetDescription(new GameAction { Type = GameActionType.VesselEnded }, null));
        }

        // Deduplicated per owner recording: the same end delivered twice collapses, two
        // owning trees keep both.
        [Fact]
        public void DedupKey_IsPerOwnerRecording()
        {
            Assert.Equal(LedgerOrchestrator.GetActionKey(EndedRow("rec-a", VesselEndCause.Destroyed)),
                LedgerOrchestrator.GetActionKey(EndedRow("rec-a", VesselEndCause.Deleted)));
            Assert.NotEqual(LedgerOrchestrator.GetActionKey(EndedRow("rec-a")),
                LedgerOrchestrator.GetActionKey(EndedRow("rec-b")));
        }

        [Fact]
        public void TombstoneEligibility_NeedsAnOwnerRecording()
        {
            Assert.True(TombstoneEligibility.IsSupersedeTombstoneEligible(EndedRow("rec-a")));
            Assert.False(TombstoneEligibility.IsSupersedeTombstoneEligible(EndedRow(null)));
        }

        // ------------------------------------------------------------------
        // Every member through the switches whose default WARNs or misnames
        // ------------------------------------------------------------------

        // catches: a row type with no arm in the timeline type map, which WARNs on every
        // timeline build that meets it.
        [Fact]
        public void EveryRowType_MapsToATimelineEntryTypeWithoutWarn()
        {
            foreach (GameActionType t in RowTypes())
                TimelineEntryDisplay.MapGameActionType(t);

            Assert.DoesNotContain(logLines, l => l.Contains("Unknown GameActionType"));
        }

        // catches: a row type with no display arm, which falls to the bare enum name on the
        // timeline's text path (which delegates to GameActionDisplay for most types).
        [Fact]
        public void EveryRowType_HasADisplayText()
        {
            var bare = RowTypes()
                .Where(t => TimelineEntryDisplay.GetGameActionText(new GameAction { Type = t }, null, null) == t.ToString())
                .ToList();
            Assert.True(bare.Count == 0,
                "GameActionType(s) whose description is the bare enum name: " + string.Join(", ", bare));
        }

        // ------------------------------------------------------------------
        // Not spawn evidence, no timeline row; the effective ledger keeps it
        // ------------------------------------------------------------------

        [Fact]
        public void NotSpawnEvidence_NorARecoveryRow()
        {
            var ended = EndedRow("rec-a");
            Assert.False(TimelineBuilder.IsSpawnEvidenceOnlyActionType(GameActionType.VesselEnded));
            Assert.False(RecoveredRecordingEvidence.IsRecoveryRow(ended, "rec-a", double.NegativeInfinity));
            Assert.False(RecoveredRecordingEvidence.HasRecoveryRow("rec-a", new List<GameAction> { ended }));
        }

        [Fact]
        public void Timeline_DrawsNoRow_AndSaysSo()
        {
            var entries = TimelineBuilder.Build(
                new List<Recording>(),
                new List<GameAction> { EndedRow("rec-a") },
                new List<Milestone>(),
                null);

            Assert.Empty(entries.Where(e => e.Source == TimelineSource.GameAction));
            Assert.Contains(logLines, l => l.Contains("[Timeline]")
                && l.Contains("Filtered 1 vessel-ended action(s)"));
            Assert.DoesNotContain(logLines, l => l.Contains("Unknown GameActionType"));
        }

        [Fact]
        public void EffectiveLedger_KeepsTheRow_UntilItIsTombstoned()
        {
            var row = EndedRow("rec-a");
            Ledger.AddAction(row);
            var scenario = new ParsekScenario { LedgerTombstones = new List<LedgerTombstone>() };
            ParsekScenario.SetInstanceForTesting(scenario);

            Assert.Contains(EffectiveState.ComputeELS(), a => a.ActionId == row.ActionId);

            scenario.LedgerTombstones.Add(
                new LedgerTombstone { TombstoneId = "tomb-1", ActionId = row.ActionId, UT = 500.0 });
            scenario.BumpTombstoneStateVersion();

            Assert.DoesNotContain(EffectiveState.ComputeELS(), a => a.ActionId == row.ActionId);
        }

        // A supersede over a recording that carries the row is not strict-blocked.
        [Fact]
        public void StrictGate_DoesNotBlockARecordingThatCarriesTheRow()
        {
            var rec = new Recording { RecordingId = "rec-a", VesselName = "rec-a", TreeId = "tree-1" };
            Ledger.AddAction(EndedRow("rec-a"));

            string summary;
            Assert.False(SupersedeCommit.TryFindRecordingScopedWorldAction(rec, out summary));
            Assert.Null(summary);
        }
    }
}
