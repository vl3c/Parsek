using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using KSP.UI.Screens;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Strategies (S1, docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md section
    /// 10 step 5): the activation and player-path deactivation predicates, their texts, the
    /// Administration hooks' targets and pairing, and the ledger-to-stock state-patch diff.
    /// </summary>
    [Collection("Sequential")]
    public class StrategyReservationTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private static readonly Func<double, string> Date = ut => "D" + ut.ToString("F0", IC);
        private const string Committed = "rec-committed";
        private const string Uncommitted = "rec-live";

        private readonly List<string> logLines = new List<string>();
        private readonly List<string> dialogBodies = new List<string>();

        public StrategyReservationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateStore.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            GameStateRecorder.IsReplayingActions = false;
            EffectiveState.ResetCachesForTesting();
            CommittedFutureIndexCache.ResetForTesting();
            StrategyReservationGate.ResetForTesting();
            StrategyStatePatcher.ResetForTesting();
            RecalculationEngine.ClearModules();
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings();
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 100.0;
            StrategyReservationGate.ActiveStrategyIdsProviderForTesting = () => new List<string>();
            StrategyReservationGate.SlotLimitProviderForTesting = () => 1;
            StrategyReservationGate.StrategyTitleProviderForTesting = id => "Title of " + id;
            CommittedActionDialog.TestHookForTesting = (action, reason, detail) => dialogBodies.Add(reason);
        }

        public void Dispose()
        {
            CommittedActionDialog.TestHookForTesting = null;
            StrategyReservationGate.ResetForTesting();
            CommittedFutureIndexCache.ResetForTesting();
            GameStateRecorder.IsReplayingActions = false;
            RecalculationEngine.ClearModules();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
        }

        // ================================================================
        // Builders
        // ================================================================

        private static GameAction On(double ut, string id, string recordingId = null, float commitment = 0.25f)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.StrategyActivate,
                StrategyId = id,
                RecordingId = recordingId,
                Commitment = commitment,
                SetupCost = 1000f
            };
        }

        private static GameAction Off(double ut, string id, string recordingId = null)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.StrategyDeactivate,
                StrategyId = id,
                RecordingId = recordingId
            };
        }

        private static GameAction AdminUpgrade(double ut, int toLevel, string facilityId = "SpaceCenter/Administration")
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.FacilityUpgrade,
                FacilityId = facilityId,
                ToLevel = toLevel,
                FacilityCost = 1f
            };
        }

        private static CommittedFutureIndex Index(params GameAction[] rows)
        {
            return CommittedFutureIndex.Build(rows, id => id == Committed, id => null, null);
        }

        private static StrategyActivationDecision Activate(
            CommittedFutureIndex index, string id, IEnumerable<string> activeNow, int limit, double now = 100.0)
        {
            return StrategyReservationPredicates.EvaluateActivation(index, id, now, activeNow, limit);
        }

        // ================================================================
        // Activation: the strategy's own committed activation
        // ================================================================

        [Fact]
        public void Activation_NoCommittedRows_Allowed()
        {
            var d = Activate(Index(), "A", new string[0], 1);
            Assert.False(d.Blocked);
        }

        [Fact]
        public void Activation_CommittedLaterActivationOfTheSameStrategy_Refused()
        {
            var d = Activate(Index(On(300.0, "A")), "A", new string[0], 5);

            Assert.Equal(StrategyActivationBlockKind.FutureActivation, d.Kind);
            Assert.Equal(300.0, d.Entry.UT);
            var text = StrategyReservationPredicates.ExplainActivation(d, null, Date);
            Assert.Equal("Activated on D300", text.Title);
            Assert.Equal(
                "Activated on D300 on your committed timeline. " + ReservationExplanation.TimelineRule +
                " It becomes active on that date.",
                text.Body);
        }

        [Fact]
        public void Activation_CommittedActivationAtOrBeforeNow_IsHistory_Allowed()
        {
            Assert.False(Activate(Index(On(100.0, "A")), "A", new string[0], 5).Blocked);
            Assert.False(Activate(Index(On(50.0, "A")), "A", new string[0], 5).Blocked);
        }

        [Fact]
        public void Activation_RowOfAnUncommittedRecording_DoesNotReserve()
        {
            var d = Activate(Index(On(300.0, "A", Uncommitted), On(300.0, "B", Uncommitted)), "A", new string[0], 1);
            Assert.False(d.Blocked);
        }

        [Fact]
        public void Activation_FlightTaggedCommittedRow_NamesTheFlight()
        {
            var index = CommittedFutureIndex.Build(new[] { On(300.0, "A", Committed) },
                id => id == Committed, id => "Mun Lander 3", null);
            var text = StrategyReservationPredicates.ExplainActivation(Activate(index, "A", new string[0], 5), null, Date);
            Assert.StartsWith("Activated on D300 by the committed flight 'Mun Lander 3'.", text.Body);
        }

        // ================================================================
        // Activation: the slot peak
        // ================================================================

        [Fact]
        public void Activation_TakesTheSlotACommittedActivationNeeds_Refused()
        {
            var d = Activate(Index(On(200.0, "B")), "A", new string[0], 1);

            Assert.Equal(StrategyActivationBlockKind.SlotNeeded, d.Kind);
            Assert.Equal("B", d.Entry.Key);
            Assert.Equal(2, d.CountAtOverflow);
            Assert.Equal(1, d.LimitAtOverflow);
            var text = StrategyReservationPredicates.ExplainActivation(d, id => "Title of " + id, Date);
            Assert.Equal("Slot needed on D200", text.Title);
            Assert.Equal(
                "A committed activation of 'Title of B' on D200 needs this slot. " +
                ReservationExplanation.TimelineRule +
                " A slot frees when one of your active strategies ends.",
                text.Body);
        }

        [Fact]
        public void Activation_SlotStillFreeAtTheCommittedActivation_Allowed()
        {
            Assert.False(Activate(Index(On(200.0, "B")), "A", new string[0], 2).Blocked);
        }

        [Fact]
        public void Activation_ActiveNowStrategyWithNoCommittedEnd_CountsThroughout()
        {
            // C is active now and nothing committed ends it: A + C + B = 3 > 2 at UT 200.
            var d = Activate(Index(On(200.0, "B")), "A", new[] { "C" }, 2);
            Assert.Equal(StrategyActivationBlockKind.SlotNeeded, d.Kind);
        }

        [Fact]
        public void Activation_CommittedDeactivationBeforeTheCommittedActivation_FreesTheSlot()
        {
            var d = Activate(Index(Off(150.0, "C"), On(200.0, "B")), "A", new[] { "C" }, 2);
            Assert.False(d.Blocked);
        }

        [Fact]
        public void Activation_CommittedDeactivationAfterTheCommittedActivation_DoesNotFreeIt()
        {
            var d = Activate(Index(On(200.0, "B"), Off(250.0, "C")), "A", new[] { "C" }, 2);
            Assert.Equal(StrategyActivationBlockKind.SlotNeeded, d.Kind);
        }

        [Fact]
        public void Activation_SameUtDeactivationAndActivation_DeactivationFirst()
        {
            var d = Activate(Index(On(200.0, "B"), Off(200.0, "C")), "A", new[] { "C" }, 2);
            Assert.False(d.Blocked);
        }

        [Fact]
        public void Activation_CommittedAdministrationUpgradeBeforeTheActivation_RaisesTheLimit()
        {
            // Level 1 = 1 slot; a committed upgrade to level 2 (3 slots) lands first.
            Assert.False(Activate(Index(AdminUpgrade(150.0, 2), On(200.0, "B")), "A", new string[0], 1).Blocked);
            Assert.False(Activate(Index(AdminUpgrade(150.0, 2, "Administration"), On(200.0, "B")), "A", new string[0], 1).Blocked);
            // An upgrade after the activation does not help it.
            Assert.True(Activate(Index(On(200.0, "B"), AdminUpgrade(250.0, 2)), "A", new string[0], 1).Blocked);
        }

        [Fact]
        public void Activation_OverflowOnlyNow_IsStocksOwnCheck_NotReported()
        {
            // Full now, nothing committed ahead: stock's own slot check refuses with its reason.
            Assert.False(Activate(Index(), "A", new[] { "C" }, 1).Blocked);
            Assert.False(Activate(Index(Off(300.0, "C")), "A", new[] { "C" }, 1).Blocked);
        }

        [Fact]
        public void Activation_TheEarliestOverflow_IsNamed()
        {
            var d = Activate(Index(On(200.0, "B"), On(260.0, "D")), "A", new string[0], 2);
            Assert.Equal("D", d.Entry.Key);
            var d2 = Activate(Index(On(200.0, "B"), On(260.0, "D")), "A", new string[0], 1);
            Assert.Equal("B", d2.Entry.Key);
        }

        // ================================================================
        // Deactivation (player path)
        // ================================================================

        [Fact]
        public void Deactivation_CommittedLaterDeactivation_Refused()
        {
            var index = Index(On(10.0, "A"), Off(300.0, "A"));
            Assert.True(StrategyReservationPredicates.IsDeactivationBlocked(index, "A", 100.0));
            var text = StrategyReservationPredicates.ExplainDeactivation(index, "A", 100.0, Date);
            Assert.Equal("Deactivated on D300", text.Title);
            Assert.Equal(
                "Deactivated on D300 on your committed timeline. " + ReservationExplanation.TimelineRule +
                " It stays active until that date.",
                text.Body);
        }

        [Fact]
        public void Deactivation_CommittedLaterReActivation_Refused()
        {
            var index = Index(On(10.0, "A"), On(300.0, "A"));
            Assert.True(StrategyReservationPredicates.IsDeactivationBlocked(index, "A", 100.0));
            var text = StrategyReservationPredicates.ExplainDeactivation(index, "A", 100.0, Date);
            Assert.Equal(
                "Activated again on D300 on your committed timeline. " + ReservationExplanation.TimelineRule +
                " It can be deactivated after that date.",
                text.Body);
        }

        [Fact]
        public void Deactivation_NoLaterRowForThisStrategy_Allowed()
        {
            var index = Index(On(10.0, "A"), Off(50.0, "A"), On(60.0, "A"), Off(300.0, "B"), On(300.0, "C"));
            Assert.False(StrategyReservationPredicates.IsDeactivationBlocked(index, "A", 100.0));
        }

        [Fact]
        public void Deactivation_LiftsOnceTheClockPassesTheLastCommittedRow()
        {
            var index = Index(On(10.0, "A"), Off(300.0, "A"));
            Assert.True(StrategyReservationPredicates.IsDeactivationBlocked(index, "A", 299.0));
            Assert.False(StrategyReservationPredicates.IsDeactivationBlocked(index, "A", 300.0));
        }

        [Fact]
        public void Deactivation_TheEarliestLaterRowIsNamed()
        {
            var index = Index(On(10.0, "A"), Off(300.0, "A"), On(400.0, "A"));
            var next = StrategyReservationPredicates.FirstFutureRow(index, "A", 100.0);
            Assert.Equal(CommittedFutureKind.StrategyDeactivate, next.Kind);
            Assert.Equal(300.0, next.UT);
        }

        // ================================================================
        // The live gate: replay bypass, the dialog, and pairing
        // ================================================================

        [Fact]
        public void Gate_ReplayInProgress_RefusesNothing()
        {
            Ledger.AddAction(On(300.0, "A"));
            Ledger.AddAction(On(10.0, "B"));
            Ledger.AddAction(Off(300.0, "B"));
            GameStateRecorder.IsReplayingActions = true;

            ReservationText text;
            Assert.False(StrategyReservationGate.TryRefuseActivation("A", out text));
            Assert.False(StrategyReservationGate.TryRefuseDeactivation("B", out text));
            Assert.True(AdministrationButtonBackstopPatch.ShouldAllow("accept", false, "A", "A"));
            Assert.True(AdministrationButtonBackstopPatch.ShouldAllow("cancel", true, "B", "B"));
            Assert.Empty(dialogBodies);
            Assert.Contains(logLines, l => l.Contains("[StrategyReservation]") && l.Contains("replay in progress"));
        }

        [Fact]
        public void Gate_ActivationClickBackstop_ShowsTheSameTextAsTheStockReason()
        {
            Ledger.AddAction(On(300.0, "A"));

            ReservationText reason;
            Assert.True(StrategyReservationGate.TryRefuseActivation("A", out reason));
            Assert.False(AdministrationButtonBackstopPatch.ShouldAllow("accept", false, "A", "Unpaid Research"));

            Assert.Single(dialogBodies);
            Assert.Equal(reason.Body, dialogBodies[0]);
            Assert.Contains(logLines, l => l.Contains("[CommittedAction]")
                && l.Contains("Cannot activate \"Unpaid Research\""));
            Assert.Contains(logLines, l => l.Contains("[StrategyReservation]")
                && l.Contains("activation refused strategy=A") && l.Contains("kind=FutureActivation"));
        }

        [Fact]
        public void Gate_DeactivationClickBackstop_ShowsTheSameTextAsTheDescriptionReason()
        {
            Ledger.AddAction(On(10.0, "B"));
            Ledger.AddAction(Off(300.0, "B"));

            ReservationText reason;
            Assert.True(StrategyReservationGate.TryRefuseDeactivation("B", out reason));
            Assert.False(AdministrationButtonBackstopPatch.ShouldAllow("cancel", true, "B", "Aggressive Negotiations"));

            Assert.Single(dialogBodies);
            Assert.Equal(reason.Body, dialogBodies[0]);
            Assert.Contains(logLines, l => l.Contains("[CommittedAction]")
                && l.Contains("Cannot deactivate \"Aggressive Negotiations\""));
        }

        [Fact]
        public void Gate_BackstopOnlyActsOnTheMatchingButtonState()
        {
            Ledger.AddAction(On(300.0, "A"));
            Ledger.AddAction(On(10.0, "B"));
            Ledger.AddAction(Off(300.0, "B"));

            // Accept on an active strategy and Cancel on an inactive one are not our clicks.
            Assert.True(AdministrationButtonBackstopPatch.ShouldAllow("accept", true, "B", "B"));
            Assert.True(AdministrationButtonBackstopPatch.ShouldAllow("cancel", false, "A", "A"));
            Assert.True(AdministrationButtonBackstopPatch.ShouldAllow("somethingElse", false, "A", "A"));
            Assert.Empty(dialogBodies);
        }

        [Fact]
        public void Pairing_AdministrationReasonSet_EqualsTheClickBlockSet()
        {
            // A, D, I: committed later activations. B: active, committed later deactivation.
            // G: active, nothing committed ahead. C, E: free, but two slots are full and
            // the committed activation of I needs one. H: shares I's two tags, so with H
            // active stock's conflict rule would refuse I's committed activation.
            Ledger.AddAction(On(300.0, "A"));
            Ledger.AddAction(On(10.0, "B"));
            Ledger.AddAction(Off(350.0, "B"));
            Ledger.AddAction(On(400.0, "D"));
            Ledger.AddAction(On(20.0, "G"));
            Ledger.AddAction(On(200.0, "I"));
            var activeNow = new HashSet<string> { "B", "G" };
            StrategyReservationGate.ActiveStrategyIdsProviderForTesting = () => activeNow;
            StrategyReservationGate.SlotLimitProviderForTesting = () => 2;
            StrategyReservationGate.StockStrategiesProviderForTesting = () => new List<StrategyTagInfo>
            {
                new StrategyTagInfo("H", new[] { "Basic", "A" }),
                new StrategyTagInfo("A", new[] { "tA" }),
                new StrategyTagInfo("B", new[] { "tB" }),
                new StrategyTagInfo("C", new[] { "tC" }),
                new StrategyTagInfo("D", new[] { "tD" }),
                new StrategyTagInfo("E", new[] { "tE" }),
                new StrategyTagInfo("G", new[] { "tG" }),
                new StrategyTagInfo("I", new[] { "Basic", "A" })
            };

            var marks = new List<string>();
            var blocks = new List<string>();
            foreach (string id in new[] { "A", "B", "C", "D", "E", "G", "H", "I" })
            {
                bool isActive = activeNow.Contains(id);
                ReservationText text;
                bool marked = isActive
                    ? StrategyReservationGate.TryRefuseDeactivation(id, out text)
                    : StrategyReservationGate.TryRefuseActivation(id, out text);
                bool blocked = !AdministrationButtonBackstopPatch.ShouldAllow(
                    isActive ? "cancel" : "accept", isActive, id, id);
                if (marked) marks.Add(id);
                if (blocked) blocks.Add(id);
            }

            Assert.Equal(new[] { "A", "B", "C", "D", "E", "H", "I" }, marks);
            Assert.Equal(marks, blocks);
            Assert.Equal(7, dialogBodies.Count);
            Assert.Contains(logLines, l => l.Contains("activation refused strategy=H")
                && l.Contains("kind=ConflictsWithCommitted") && l.Contains("committedKey=I"));
        }

        // ================================================================
        // Stock's conflict rule (StrategySystem.HasConflictingActiveStrategies)
        // ================================================================

        private const string Appreciation = "AppreciationCampaignCfg";
        private const string Fundraising = "FundraisingCampaignCfg";
        private const string OpenSource = "OpenSourceTechProgramCfg";
        private const string Outsourced = "OutsourcedResearchCfg";
        private const string Aggressive = "AgressiveNegotiations";
        private const string Leadership = "LeadershipInitiative";

        /// <summary>Stock's 11 strategies in GameData/Squad/Strategies/Strategies.cfg order,
        /// with their groupTag values (KSP 1.12.5).</summary>
        private static List<StrategyTagInfo> StockList()
        {
            return new List<StrategyTagInfo>
            {
                new StrategyTagInfo(Appreciation, new[] { "Basic", "A" }),
                new StrategyTagInfo(Fundraising, new[] { "Basic", "A" }),
                new StrategyTagInfo(OpenSource, new[] { "Basic", "B" }),
                new StrategyTagInfo("UnpaidResearchProgramCfg", new[] { "Basic", "B" }),
                new StrategyTagInfo(Outsourced, new[] { "Basic", "C" }),
                new StrategyTagInfo("PatentsLicensingCfg", new[] { "Basic", "C" }),
                new StrategyTagInfo(Aggressive, new[] { "Misc", "D" }),
                new StrategyTagInfo("RecoveryTransponders", new[] { "Misc", "Recovery" }),
                new StrategyTagInfo("BailoutGrant", new[] { "Emergency", "A" }),
                new StrategyTagInfo("researchIPsellout", new[] { "Emergency", "A" }),
                new StrategyTagInfo(Leadership, new[] { "Misc", "D" })
            };
        }

        private static bool StockConflict(string candidate, params string[] active)
        {
            var list = StockList();
            var tags = list.First(x => x.Id == candidate).Tags;
            return StrategyReservationPredicates.StockHasConflictingActiveStrategies(
                list, new HashSet<string>(active), tags);
        }

        [Fact]
        public void StockConflict_MirrorsTheStockIl_HandTracedCases()
        {
            Assert.False(StockConflict(Fundraising));
            // One active shares a tag -> threshold 2; the first strategy carries both tags.
            Assert.True(StockConflict(Fundraising, Appreciation));
            Assert.False(StockConflict(OpenSource, Appreciation));
            // Two active share a tag -> threshold 1; any tag in the first two strategies.
            Assert.True(StockConflict(Outsourced, Appreciation, OpenSource));
        }

        /// <summary>
        /// Stock indexes its FULL strategy list with the active list's positions, so it
        /// inspects the first activeCount strategies in list order, active or not. Pinned
        /// here because the predicate must answer what stock answers, quirk included.
        /// </summary>
        [Fact]
        public void StockConflict_KeepsStocksIndexQuirk()
        {
            // Only Open Source is active, yet Fundraising is refused: the window reads
            // Appreciation (list position 0, inactive), which carries both of its tags.
            Assert.True(StockConflict(Fundraising, OpenSource));
            // Aggressive Negotiations and Leadership Initiative share both tags, but the
            // window reads Appreciation instead of Aggressive, so stock allows the pair.
            Assert.False(StockConflict(Leadership, Aggressive));
        }

        [Fact]
        public void Activation_WouldMakeStockRefuseACommittedActivation_Refused()
        {
            var d = StrategyReservationPredicates.EvaluateActivation(
                Index(On(200.0, Fundraising)), Appreciation, 100.0, new string[0], 5,
                stockStrategies: StockList());

            Assert.Equal(StrategyActivationBlockKind.ConflictsWithCommitted, d.Kind);
            Assert.Equal(Fundraising, d.Entry.Key);
            Assert.Null(d.ConflictEnd);
            var text = StrategyReservationPredicates.ExplainActivation(d, id => "Fundraising Campaign", Date);
            Assert.Equal("Conflicts with 'Fundraising Campaign'", text.Title);
            Assert.Equal(
                "Conflicts with 'Fundraising Campaign', which is activated on D200 on your committed timeline. " +
                ReservationExplanation.TimelineRule,
                text.Body);
        }

        [Fact]
        public void Activation_Conflict_NamesTheCommittedEndWhenThereIsOne()
        {
            var d = StrategyReservationPredicates.EvaluateActivation(
                Index(On(200.0, Fundraising), Off(300.0, Fundraising)), Appreciation, 100.0, new string[0], 5,
                stockStrategies: StockList());
            Assert.Equal(300.0, d.ConflictEnd.UT);
            var text = StrategyReservationPredicates.ExplainActivation(d, id => "Fundraising Campaign", Date);
            Assert.EndsWith(" The conflict ends when 'Fundraising Campaign' is deactivated on D300.", text.Body);
        }

        [Fact]
        public void Activation_Conflict_OnlyWhileTheCandidateIsStillActive()
        {
            // A committed deactivation of the candidate before the committed activation:
            // no overlap, no conflict.
            var d = StrategyReservationPredicates.EvaluateActivation(
                Index(Off(150.0, Appreciation), On(200.0, Fundraising)), Appreciation, 100.0, new string[0], 5,
                stockStrategies: StockList());
            Assert.False(d.Blocked);
        }

        [Fact]
        public void Activation_NoStockConflict_Allowed()
        {
            var d = StrategyReservationPredicates.EvaluateActivation(
                Index(On(200.0, Fundraising)), Aggressive, 100.0, new string[0], 5,
                stockStrategies: StockList());
            Assert.False(d.Blocked);
        }

        [Fact]
        public void Activation_ConflictTheTimelineAlreadyHasWithoutTheCandidate_IsNotBlamedOnIt()
        {
            // Appreciation is active now and already makes Fundraising's committed activation
            // conflict; activating Patents Licensing changes nothing about that.
            var d = StrategyReservationPredicates.EvaluateActivation(
                Index(On(200.0, Fundraising)), "PatentsLicensingCfg", 100.0, new[] { Appreciation }, 5,
                stockStrategies: StockList());
            Assert.NotEqual(StrategyActivationBlockKind.ConflictsWithCommitted, d.Kind);
        }

        [Fact]
        public void Activation_NoStockList_SkipsTheConflictBranch()
        {
            var d = StrategyReservationPredicates.EvaluateActivation(
                Index(On(200.0, Fundraising)), Appreciation, 100.0, new string[0], 5);
            Assert.False(d.Blocked);
        }

        [Fact]
        public void Plan_LedgerConflictingPair_ActivatesBoth_WarnsOnce_DoesNotThrash()
        {
            var known = new HashSet<string>(StockList().Select(x => x.Id));
            var stock = new HashSet<string>();
            var ledger = LedgerSet((Appreciation, 50.0), (Fundraising, 60.0));

            var first = StrategyStatePatcher.ComputePlan(ledger, id => true, stock, known, 100.0,
                stockStrategies: StockList());
            Assert.Equal(new[] { Appreciation, Fundraising }, first.ToActivate);
            Assert.Equal(new[] { Appreciation, Fundraising }, first.Conflicting);
            Assert.True(StrategyStatePatcher.ReportConflicts(first));

            foreach (var id in first.ToActivate) stock.Add(id);
            var second = StrategyStatePatcher.ComputePlan(ledger, id => true, stock, known, 100.0,
                stockStrategies: StockList());
            Assert.False(second.HasChanges);
            Assert.Equal(first.Conflicting, second.Conflicting);
            Assert.False(StrategyStatePatcher.ReportConflicts(second));

            Assert.Single(logLines.Where(l => l.Contains("[WARN][KspStatePatcher]")
                && l.Contains("conflict rule would not allow")));
            Assert.Contains("conflicting=2", StrategyStatePatcher.DescribePlan(second));
        }

        // ================================================================
        // State patch: the ledger -> stock diff
        // ================================================================

        private static Dictionary<string, double> LedgerSet(params (string id, double ut)[] rows)
        {
            var d = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var r in rows) d[r.id] = r.ut;
            return d;
        }

        [Fact]
        public void Plan_LedgerActiveStockInactive_ActivatesWithoutACharge()
        {
            var plan = StrategyStatePatcher.ComputePlan(
                LedgerSet(("A", 50.0)), id => true, new HashSet<string>(), new HashSet<string> { "A", "B" }, 100.0);

            Assert.Equal(new[] { "A" }, plan.ToActivate);
            Assert.Empty(plan.ToDeactivate);
            Assert.True(plan.HasChanges);
        }

        [Fact]
        public void Plan_StockActiveLedgerInactive_ManagedIsDeactivated_UnmanagedLeftAlone()
        {
            var plan = StrategyStatePatcher.ComputePlan(
                LedgerSet(), id => id == "A", new HashSet<string> { "A", "Legacy" },
                new HashSet<string> { "A", "Legacy" }, 100.0);

            Assert.Equal(new[] { "A" }, plan.ToDeactivate);
            Assert.Equal(new[] { "Legacy" }, plan.Unmanaged);
            Assert.Empty(plan.ToActivate);
        }

        [Fact]
        public void Plan_AlreadyMatching_IsANoOp_AndApplyingAPlanIsIdempotent()
        {
            var known = new HashSet<string> { "A", "B", "C" };
            var stock = new HashSet<string> { "B" };
            var ledger = LedgerSet(("A", 50.0), ("C", 60.0));
            var first = StrategyStatePatcher.ComputePlan(ledger, id => true, stock, known, 100.0);
            Assert.Equal(new[] { "A", "C" }, first.ToActivate);
            Assert.Equal(new[] { "B" }, first.ToDeactivate);

            foreach (var id in first.ToDeactivate) stock.Remove(id);
            foreach (var id in first.ToActivate) stock.Add(id);
            var second = StrategyStatePatcher.ComputePlan(ledger, id => true, stock, known, 100.0);

            Assert.False(second.HasChanges);
            Assert.Equal(2, second.Matching);
        }

        [Fact]
        public void Plan_ActivationDatedAfterNow_Waits()
        {
            // A walk that ran past now (no current-UT cutoff) must not switch it on early.
            var plan = StrategyStatePatcher.ComputePlan(
                LedgerSet(("A", 300.0)), id => true, new HashSet<string>(), new HashSet<string> { "A" }, 100.0);
            Assert.Empty(plan.ToActivate);
            Assert.Equal(new[] { "A" }, plan.FutureDated);
        }

        [Fact]
        public void Plan_CommittedDeactivationStillAhead_LeavesItOn()
        {
            var plan = StrategyStatePatcher.ComputePlan(
                LedgerSet(), id => true, new HashSet<string> { "A" }, new HashSet<string> { "A" }, 100.0,
                hasFutureDeactivation: id => id == "A");
            Assert.Empty(plan.ToDeactivate);
            Assert.Equal(new[] { "A" }, plan.FutureDeactivation);
        }

        [Fact]
        public void Plan_LedgerActiveStrategyStockDoesNotList_IsReportedMissing()
        {
            var plan = StrategyStatePatcher.ComputePlan(
                LedgerSet(("Gone", 50.0)), id => true, new HashSet<string>(), new HashSet<string> { "A" }, 100.0);
            Assert.Empty(plan.ToActivate);
            Assert.Equal(new[] { "Gone" }, plan.MissingInStock);
            Assert.Contains("missingInStock=1", StrategyStatePatcher.DescribePlan(plan));
        }

        /// <summary>
        /// The walk feeds the plan: a committed activation before now is switched on, a
        /// committed deactivation before now switches it off.
        /// </summary>
        [Fact]
        public void Plan_FromACutoffWalk_MatchesTheCommittedTimelineAtNow()
        {
            var strategies = new StrategiesModule();
            RecalculationEngine.RegisterModule(strategies, RecalculationEngine.ModuleTier.Strategy);
            var rows = new List<GameAction> { On(10.0, "A"), On(20.0, "B"), Off(60.0, "B"), On(300.0, "C") };

            RecalculationEngine.Recalculate(rows, 100.0);
            var plan = PlanFromModule(strategies, new HashSet<string> { "B" }, 100.0);

            Assert.Equal(new[] { "A" }, plan.ToActivate);
            Assert.Equal(new[] { "B" }, plan.ToDeactivate);
            Assert.Empty(plan.FutureDated); // C is not walked at a cutoff of 100
        }

        /// <summary>
        /// Stock auto-expiry cannot loop with the state patch. KSPCF's StrategyDuration fix
        /// expires a strategy through <c>Strategy.Deactivate()</c>, which
        /// <c>StrategyDeactivatePatch</c> captures as a StrategyDeactivate row; the walk then
        /// has it inactive and the plan leaves stock's expired strategy off. Even when that
        /// row is missing (a flight-tagged expiry whose recording was discarded) the plan
        /// re-activates once, and the re-expiry's captured row settles it.
        /// </summary>
        [Fact]
        public void Plan_StockAutoExpiry_IsNotUndone_AndCannotLoop()
        {
            var strategies = new StrategiesModule();
            RecalculationEngine.RegisterModule(strategies, RecalculationEngine.ModuleTier.Strategy);
            var rows = new List<GameAction> { On(10.0, "A") };
            var stock = new HashSet<string>(); // KSPCF expired A at UT 110

            // The captured expiry row is in the ledger: nothing to do.
            rows.Add(Off(110.0, "A"));
            RecalculationEngine.Recalculate(rows, 120.0);
            Assert.False(PlanFromModule(strategies, stock, 120.0).HasChanges);

            // The expiry row was lost: one re-activation, then the re-expiry's row settles it.
            rows.RemoveAt(rows.Count - 1);
            RecalculationEngine.Recalculate(rows, 120.0);
            var once = PlanFromModule(strategies, stock, 120.0);
            Assert.Equal(new[] { "A" }, once.ToActivate);
            rows.Add(Off(120.0, "A"));
            RecalculationEngine.Recalculate(rows, 121.0);
            Assert.False(PlanFromModule(strategies, stock, 121.0).HasChanges);
        }

        private static StrategyStatePatchPlan PlanFromModule(StrategiesModule strategies, HashSet<string> stock, double now)
        {
            var ledger = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var id in strategies.GetActiveStrategyIds())
            {
                StrategiesModule.StrategyState state;
                Assert.True(strategies.TryGetActiveStrategy(id, out state));
                ledger[id] = state.ActivateUT;
            }
            return StrategyStatePatcher.ComputePlan(ledger, strategies.IsManagedStrategy, stock,
                new HashSet<string> { "A", "B", "C" }, now);
        }

        // ================================================================
        // State patch: stock loads its strategy list a frame late
        // ================================================================

        [Fact]
        public void StockList_EmptyInstanceIsNotLoaded_NullIsNoSystem()
        {
            Assert.Equal(StockStrategyListState.NoSystem, StrategyStatePatcher.ClassifyStockList(false, 0));
            Assert.Equal(StockStrategyListState.NotLoaded, StrategyStatePatcher.ClassifyStockList(true, 0));
            Assert.Equal(StockStrategyListState.Loaded, StrategyStatePatcher.ClassifyStockList(true, 11));
        }

        [Fact]
        public void StockList_NotLoaded_DefersOnlyInCareer()
        {
            Assert.True(StrategyStatePatcher.ShouldDeferUntilStockLoad(StockStrategyListState.NotLoaded, true));
            // Stock's module can load after Parsek's in the same pass: Instance is null then.
            Assert.True(StrategyStatePatcher.ShouldDeferUntilStockLoad(StockStrategyListState.NoSystem, true));
            Assert.False(StrategyStatePatcher.ShouldDeferUntilStockLoad(StockStrategyListState.NotLoaded, false));
            Assert.False(StrategyStatePatcher.ShouldDeferUntilStockLoad(StockStrategyListState.NoSystem, false));
            Assert.False(StrategyStatePatcher.ShouldDeferUntilStockLoad(StockStrategyListState.Loaded, true));
        }

        [Fact]
        public void DeferredPatch_TriggerDecision()
        {
            object requested = new object();
            object other = new object();
            Assert.Equal(DeferredStrategyPatchDecision.NoPending,
                StrategyStatePatcher.DecideDeferredPatch(false, null, requested, "s", "s", 11));
            Assert.Equal(DeferredStrategyPatchDecision.Run,
                StrategyStatePatcher.DecideDeferredPatch(true, requested, requested, "s", "s", 11));
            // Requested before stock's module existed: the first instance of this save runs it.
            Assert.Equal(DeferredStrategyPatchDecision.Run,
                StrategyStatePatcher.DecideDeferredPatch(true, null, other, "s", "s", 11));
            // A scene change replaced the instance, or another save loaded.
            Assert.Equal(DeferredStrategyPatchDecision.Stale,
                StrategyStatePatcher.DecideDeferredPatch(true, requested, other, "s", "s", 11));
            Assert.Equal(DeferredStrategyPatchDecision.Stale,
                StrategyStatePatcher.DecideDeferredPatch(true, null, other, "s", "t", 11));
            Assert.Equal(DeferredStrategyPatchDecision.StillEmpty,
                StrategyStatePatcher.DecideDeferredPatch(true, requested, requested, "s", "s", 0));
        }

        [Fact]
        public void DeferredPatch_StockLoadWithNothingPending_IsANoOp()
        {
            StrategyStatePatcher.OnStockStrategiesLoaded(null);
            Assert.False(StrategyStatePatcher.HasPendingPatch);
            Assert.DoesNotContain(logLines, l => l.Contains("PatchStrategies"));
        }

        [Fact]
        public void DeferredPatch_ANewRecalculationDropsTheWaitingRequest()
        {
            StrategyStatePatcher.SetPendingPatchForTesting(new LedgerStrategySnapshot(), null, "save");
            Assert.True(StrategyStatePatcher.HasPendingPatch);

            LedgerOrchestrator.RecalculateAndPatch();

            // The headless recalc finds no StrategySystem and no Career game, so it does
            // not re-request one.
            Assert.False(StrategyStatePatcher.HasPendingPatch);
            Assert.Contains(logLines, l => l.Contains("[KspStatePatcher]")
                && l.Contains("pending patch dropped (new-recalc)"));
        }

        [Fact]
        public void DeferredPatch_CancelIsLoggedOnlyWhenSomethingWasWaiting()
        {
            StrategyStatePatcher.CancelPendingPatch("idle");
            Assert.DoesNotContain(logLines, l => l.Contains("pending patch dropped"));
            StrategyStatePatcher.SetPendingPatchForTesting(new LedgerStrategySnapshot(), null, "save");
            StrategyStatePatcher.CancelPendingPatch("scene");
            Assert.False(StrategyStatePatcher.HasPendingPatch);
            Assert.Contains(logLines, l => l.Contains("pending patch dropped (scene)"));
        }

        [Fact]
        public void DeferredPatch_SnapshotCopiesTheWalkState()
        {
            var strategies = new StrategiesModule();
            RecalculationEngine.RegisterModule(strategies, RecalculationEngine.ModuleTier.Strategy);
            RecalculationEngine.Recalculate(new List<GameAction>
            {
                On(10.0, "A", commitment: 0.5f), On(20.0, "B"), Off(30.0, "B")
            }, 100.0);

            var snapshot = LedgerStrategySnapshot.From(strategies);
            RecalculationEngine.Recalculate(new List<GameAction>(), 100.0);

            Assert.Equal(new[] { "A" }, snapshot.ActivateUT.Keys.ToArray());
            Assert.Equal(10.0, snapshot.ActivateUT["A"]);
            Assert.Equal(0.5f, snapshot.Factor["A"]);
            Assert.True(snapshot.Managed.SetEquals(new[] { "A", "B" }));
            Assert.Empty(strategies.GetActiveStrategyIds());
        }

        [Fact]
        public void Target_StockLoadStrategies_IsThePrivateListFiller()
        {
            var m = StrategySystemLoadStrategiesPatch.ResolveTargetMethodForTesting();
            Assert.NotNull(m);
            Assert.Equal(typeof(Strategies.StrategySystem), m.DeclaringType);
            Assert.True(m.IsPrivate);
            Assert.Equal(typeof(List<ConfigNode>), m.GetParameters().Single().ParameterType);
        }

        [Fact]
        public void ActivationNode_IsInvariantUnderACommaCulture()
        {
            var prior = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var node = StrategyStatePatcher.BuildActivationNode("A", 1234.5, 0.25f);
                Assert.Equal("A", node.GetValue("name"));
                Assert.Equal("1234.5", node.GetValue("date"));
                Assert.Equal("0.25", node.GetValue("factor"));
                Assert.Empty(node.GetNodes("EFFECT"));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prior;
            }
        }

        [Fact]
        public void PatchStrategies_NullModule_SkipsAndLogs()
        {
            StrategyStatePatcher.PatchStrategies(null);
            Assert.Contains(logLines, l => l.Contains("[KspStatePatcher]") && l.Contains("PatchStrategies: null StrategiesModule"));
        }

        // ================================================================
        // Patch targets and the player-path-only design
        // ================================================================

        [Fact]
        public void Target_CanBeActivated_IsTheOutStringOverload()
        {
            var m = StrategyCanBeActivatedPatch.ResolveTargetMethodForTesting();
            Assert.NotNull(m);
            Assert.Equal(typeof(Strategies.Strategy), m.DeclaringType);
            var p = m.GetParameters();
            Assert.Single(p);
            Assert.Equal("reason", p[0].Name);
            Assert.True(p[0].IsOut);
        }

        [Fact]
        public void Target_Administration_SetSelectedStrategy_AndItsDescriptionWriter()
        {
            var m = AdministrationSetSelectedStrategyPatch.ResolveTargetMethodForTesting();
            Assert.NotNull(m);
            Assert.Equal(typeof(Administration), m.DeclaringType);
            Assert.Equal("wrapper", m.GetParameters().Single().Name);
            Assert.NotNull(AdministrationSetSelectedStrategyPatch.ResolveDescriptionMethodForTesting());
            Assert.NotNull(typeof(Administration).GetField("btnAcceptCancel"));
            Assert.NotNull(typeof(Administration).GetProperty("SelectedWrapper"));
        }

        [Fact]
        public void Target_Administration_BtnInputAccept_TakesState()
        {
            var m = AdministrationButtonBackstopPatch.ResolveTargetMethodForTesting();
            Assert.NotNull(m);
            Assert.Equal(typeof(Administration), m.DeclaringType);
            Assert.Equal("state", m.GetParameters().Single().Name);
        }

        [Fact]
        public void Target_StatePatch_UsesLoadAndThePrivateActiveFlag()
        {
            Assert.NotNull(typeof(Strategies.Strategy).GetMethod("Load", new[] { typeof(ConfigNode) }));
            Assert.NotNull(typeof(Strategies.Strategy).GetMethod("Unregister", Type.EmptyTypes));
            var field = typeof(Strategies.Strategy).GetField("isActive", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            Assert.Equal(typeof(bool), field.FieldType);
        }

        /// <summary>
        /// Stock auto-expiry is NOT blocked. With KSPCommunityFixes' StrategyDuration fix
        /// (installed in the dev and harness instances; without it expiry is dead code),
        /// <c>Strategy.Update()</c> expires through <c>Deactivate()</c>, gated on
        /// <c>CanBeDeactivated</c>. No Parsek patch may target <c>CanBeDeactivated</c>, and
        /// the only patch on <c>Deactivate()</c> is the capture postfix: the refusal lives on
        /// the Administration player path only.
        /// </summary>
        [Fact]
        public void AutoExpiry_NoParsekPatchCanRefuseAStockDeactivation()
        {
            var targets = new List<(Type patchType, MethodBase target)>();
            foreach (var t in typeof(ParsekHarmony).Assembly.GetTypes())
            {
                var attrs = t.GetCustomAttributes(typeof(HarmonyPatch), false).Cast<HarmonyPatch>().ToList();
                if (attrs.Count == 0) continue;
                var declaring = attrs.Select(a => a.info.declaringType).FirstOrDefault(x => x != null);
                var name = attrs.Select(a => a.info.methodName).FirstOrDefault(x => x != null);
                if (declaring != null && name != null)
                {
                    foreach (var m in declaring.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(m => m.Name == name))
                        targets.Add((t, m));
                }
                var resolve = t.GetMethod("ResolveTargetMethodForTesting", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (resolve != null && resolve.GetParameters().Length == 0)
                {
                    var m = resolve.Invoke(null, null) as MethodBase;
                    if (m != null) targets.Add((t, m));
                }
            }

            Assert.DoesNotContain(targets, x => x.target.DeclaringType == typeof(Strategies.Strategy)
                && x.target.Name == "CanBeDeactivated");
            var onDeactivate = targets.Where(x => x.target.DeclaringType == typeof(Strategies.Strategy)
                && x.target.Name == "Deactivate").ToList();
            Assert.Single(onDeactivate);
            Assert.Equal(typeof(StrategyDeactivatePatch), onDeactivate[0].patchType);
            Assert.Null(typeof(StrategyDeactivatePatch).GetMethod("Prefix",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));
            // The activation block sits on CanBeActivated, which Update() never calls.
            Assert.Contains(targets, x => x.patchType == typeof(StrategyCanBeActivatedPatch)
                && x.target.Name == "CanBeActivated");
        }

        /// <summary>
        /// The S1 hole the verification cell pins in the walk (a second activation of the
        /// same strategy overwrites and charges the setup cost again) is closed at the stock
        /// control: once the committed activation is ahead, the player's activation is
        /// refused, so the walk never sees the duplicate row.
        /// </summary>
        [Fact]
        public void S1_DuplicateActivation_IsRefusedAtTheStockControl()
        {
            Ledger.AddAction(On(300.0, "UnpaidResearch"));
            ReservationText text;
            Assert.True(StrategyReservationGate.TryRefuseActivation("UnpaidResearch", out text));
            Assert.Contains("Activated on", text.Body);
        }
    }
}
