using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests of the six units <c>LedgerOrchestrator.RecalculateAndPatchCore</c> was
    /// split into (wave 2 W7b). The walk end to end, through every entry point, is pinned by
    /// <see cref="RecalculateAndPatchCoreCharacterizationTests"/>.
    /// </summary>
    [Collection("Sequential")]
    public class RecalculateCoreExtractedUnitsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public RecalculateCoreExtractedUnitsTests()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "parsek-w7-recalc-units-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ClockOverrideForTesting = () => 1000.0;
            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            logLines.Clear();
        }

        public void Dispose()
        {
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            try { Directory.Delete(tempDir, true); } catch (Exception) { }
        }

        private void HoldNewerLedger()
        {
            var root = new ConfigNode("LEDGER");
            root.AddValue("version", "99");
            root.AddValue("recordingSchemaGeneration",
                RecordingStore.CurrentRecordingSchemaGeneration.ToString(CultureInfo.InvariantCulture));
            string path = Path.Combine(tempDir, "ledger.pgld");
            root.Save(path);
            Ledger.LoadFromFile(path);
            logLines.Clear();
        }

        private static List<GameAction> Rows()
        {
            return new List<GameAction>
            {
                new GameAction
                {
                    UT = 100.0,
                    Type = GameActionType.FundsEarning,
                    FundsAwarded = 500f,
                    FundsSource = FundsEarningSource.Other,
                },
                new GameAction
                {
                    UT = 300.0,
                    Type = GameActionType.ReputationEarning,
                    NominalRep = 4f,
                    RepSource = ReputationSource.Other,
                },
            };
        }

        // ---- ResolveRecalcPatchDeferralReason ------------------------------------

        [Fact]
        public void Deferral_NoTree_IsNull_Silently()
        {
            Assert.Null(LedgerOrchestrator.ResolveRecalcPatchDeferralReason(false));
            Assert.Empty(logLines);
        }

        [Fact]
        public void Deferral_LiveTree_NamesIt_UnlessBypassed()
        {
            GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = () => true;
            Assert.Equal("active uncommitted flight tree",
                LedgerOrchestrator.ResolveRecalcPatchDeferralReason(false));
            Assert.Null(LedgerOrchestrator.ResolveRecalcPatchDeferralReason(true));

            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;
            Assert.Equal("live recorder active",
                LedgerOrchestrator.ResolveRecalcPatchDeferralReason(false));
            Assert.Empty(logLines);
        }

        [Fact]
        public void Deferral_HeldLedger_WinsOverBypass_AndLogsOnceInTheWindow()
        {
            HoldNewerLedger();
            Assert.Equal(LedgerOrchestrator.HeldLedgerPatchDeferralReason,
                LedgerOrchestrator.ResolveRecalcPatchDeferralReason(true));
            Assert.Equal(LedgerOrchestrator.HeldLedgerPatchDeferralReason,
                LedgerOrchestrator.ResolveRecalcPatchDeferralReason(false));
            Assert.Equal(
                "[Parsek][INFO][LedgerOrchestrator] RecalculateAndPatch: KSP state patch skipped "
                + "(ledger held from a newer Parsek version); funds, science, reputation, tech, "
                + "contracts, facilities and strategies keep the save's values",
                Assert.Single(logLines));
        }

        // ---- LogRecalculateAndPatchComplete --------------------------------------

        [Fact]
        public void Complete_LogsOnlyWhenTheWalkShapeChanges()
        {
            var actions = Rows();
            LedgerOrchestrator.LogRecalculateAndPatchComplete(actions, null, null, false);
            LedgerOrchestrator.LogRecalculateAndPatchComplete(actions, null, null, false);
            Assert.Equal("[Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch complete: 2 actions walked",
                Assert.Single(logLines));

            LedgerOrchestrator.LogRecalculateAndPatchComplete(actions, 150.0, null, false);
            LedgerOrchestrator.LogRecalculateAndPatchComplete(actions, 150.0, "x", false);
            LedgerOrchestrator.LogRecalculateAndPatchComplete(actions, 150.0, "x", true);
            actions.RemoveAt(0);
            LedgerOrchestrator.LogRecalculateAndPatchComplete(actions, 150.0, "x", true);
            Assert.Equal(5, logLines.Count);
            Assert.EndsWith("RecalculateAndPatch complete: 1 actions walked", logLines[4]);
        }

        // ---- ApplyDeferredPatchMilestoneReach ------------------------------------

        [Fact]
        public void Deferred_LogsTheReason_ThenTheMilestoneReach()
        {
            LedgerOrchestrator.ApplyDeferredPatchMilestoneReach(Rows(), 150.0, "golden reason");
            Assert.Equal(
                "[Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: deferred KSP state patch (golden reason)",
                logLines[0]);
            Assert.Contains(logLines, l => l.Contains("[CommittedMilestones]") && l.Contains("reason=deferred-patch"));
        }

        // ---- PrepareRecalculationWalk --------------------------------------------

        [Fact]
        public void Prepare_SeedsAndProgressSeed_DeferWithoutLivePools()
        {
            LedgerOrchestrator.Initialize();
            logLines.Clear();
            LedgerOrchestrator.PrepareRecalculationWalk();
            Assert.Contains(logLines, l => l.Contains("SeedInitialFunds: deferring - Funding.Instance null"));
            Assert.Contains(logLines, l => l.Contains("PreLedgerProgressSeed: deferring capture"));
            Assert.Empty(Ledger.Actions);
        }

        // ---- WalkLedgerAndReconcile ----------------------------------------------

        [Fact]
        public void Walk_CutoffWalk_RebuildsCrewReservations_FullWalkDoesNot()
        {
            LedgerOrchestrator.Initialize();
            logLines.Clear();
            LedgerOrchestrator.WalkLedgerAndReconcile(Rows(), null, null);
            Assert.Contains(logLines, l => l.Contains("Recalculate complete: actionsTotal=2") && l.Contains("cutoffUT=null"));
            Assert.Contains(logLines, l => l.Contains("UpdateSlotLimits:"));
            Assert.DoesNotContain(logLines, l => l.Contains("Recomputed after cutoff walk"));

            logLines.Clear();
            LedgerOrchestrator.WalkLedgerAndReconcile(Rows(), 150.0, null);
            Assert.Contains(logLines, l => l.Contains("Recalculate complete: actionsTotal=2") && l.Contains("cutoffUT=150"));
            Assert.Contains(logLines, l => l.Contains("provisional=True"));
            Assert.Contains(logLines, l => l.Contains("Recomputed after cutoff walk") && l.Contains("(cutoffUT=150)"));
        }

        // ---- PatchKspFromRecalculatedWalk ----------------------------------------

        [Fact]
        public void Patch_ReFlyDiscardAndTombstone_AreAuthoritativeReductions()
        {
            LedgerOrchestrator.Initialize();
            var actions = Rows();
            LedgerOrchestrator.WalkLedgerAndReconcile(actions, null, null);

            logLines.Clear();
            LedgerOrchestrator.PatchKspFromRecalculatedWalk(actions, null, false, null, false, false,
                tombstonePath: false, deadlineClockUT: null, reFlyDiscard: false);
            Assert.Contains(logLines, l => l.Contains("drawdown-guard authoritativeReduction=False")
                && l.Contains("tombstone=False") && l.Contains("reFlyDiscard=False)"));
            Assert.Contains(logLines, l => l.Contains("PatchAll complete"));

            logLines.Clear();
            LedgerOrchestrator.PatchKspFromRecalculatedWalk(actions, null, false, null, false, false,
                tombstonePath: false, deadlineClockUT: null, reFlyDiscard: true);
            Assert.Contains(logLines, l => l.Contains("drawdown-guard authoritativeReduction=True")
                && l.Contains("reFlyDiscard=True)"));

            logLines.Clear();
            LedgerOrchestrator.PatchKspFromRecalculatedWalk(actions, null, false, null, false, false,
                tombstonePath: true, deadlineClockUT: null, reFlyDiscard: false);
            Assert.Contains(logLines, l => l.Contains("drawdown-guard authoritativeReduction=True")
                && l.Contains("tombstone=True"));
            Assert.Null(KspStatePatcher.ActiveClockCrossing);
        }
    }
}
