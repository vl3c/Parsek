using System;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live-scene coverage for the discard-preserves-irreversible-economy fix
    /// (<see cref="LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard"/>).
    ///
    /// <para>xUnit proves the pure logic + a ContractsModule walk, but cannot run the REAL
    /// <see cref="LedgerOrchestrator.RecalculateAndPatch"/> against the live KSP
    /// <c>ContractSystem</c>/<c>Funding</c>. These tests do: they seed a synthetic
    /// "contract accepted at KSC (direct ledger action -> active) + completed during a
    /// recorded flight (tagged event)" scenario, run the REAL discard + REAL recalc, and
    /// assert via the post-walk <see cref="LedgerOrchestrator.Contracts"/> module that the
    /// genuine discard re-homes the completion (contract credited, NOT re-listed active)
    /// while the abandon path (quickload/revert) does not.</para>
    ///
    /// <para>Self-contained and fully reversible: snapshots the ledger action / event
    /// counts and the live funds/science/rep, seeds, runs, asserts, then truncates the
    /// appended actions/events, restores the pools, and re-runs the recalc in a finally.
    /// <c>RestoreBatchFlightBaselineAfterExecution=true</c> is the persisted-state backstop.
    /// Career + FLIGHT only; skips while a live/pending tree would defer patching.</para>
    /// </summary>
    public class DiscardEconomyPreservationInGameTest
    {
        private const string Tag = "DiscardEconomyInGame";

        [InGameTest(Category = "Contracts", Scene = GameScenes.FLIGHT,
            Description = "Genuine recording discard re-homes a contract completed live: "
                + "the contract stays credited and is NOT re-listed active through the real recalc.",
            RestoreBatchFlightBaselineAfterExecution = true)]
        public void GenuineDiscard_RehomesContractCompletion_NotReListedActive()
        {
            if (!PreconditionsMet(out string skip))
            {
                InGameAssert.Skip(skip);
                return;
            }

            string contractId = "parsek-ingame-rehome-" + Guid.NewGuid().ToString("N");
            string recId = "parsek-ingame-rehome-rec-" + Guid.NewGuid().ToString("N");

            RunSeededDiscardScenario(
                preserveIrreversibleLiveGameplay: true,
                contractId: contractId,
                recId: recId,
                afterRecalc: () =>
                {
                    var contracts = LedgerOrchestrator.Contracts;
                    InGameAssert.IsNotNull(contracts,
                        "ContractsModule is null after RecalculateAndPatch");

                    InGameAssert.IsTrue(contracts.IsContractCredited(contractId),
                        "Genuine discard must re-home the completion so the contract is "
                        + "credited (resolved) in the live walk");

                    bool stillActive = false;
                    foreach (var id in contracts.GetActiveContractIds())
                        if (id == contractId) { stillActive = true; break; }
                    InGameAssert.IsFalse(stillActive,
                        "Completed contract must NOT be re-listed active after discard");

                    InGameAssert.IsTrue(LedgerHasDirectComplete(contractId),
                        "Re-homed direct (recordingId-cleared) ContractComplete must be in the ledger");
                });

            ParsekLog.Info(Tag,
                "GenuineDiscard_RehomesContractCompletion_NotReListedActive: passed (contract "
                + "credited + not re-listed + re-homed through the real recalc).");
        }

        [InGameTest(Category = "Contracts", Scene = GameScenes.FLIGHT,
            Description = "Abandon discard path (quickload/revert) does NOT re-home — KSP is "
                + "being rolled back, so preserving would diverge the ledger the other way.",
            RestoreBatchFlightBaselineAfterExecution = true)]
        public void AbandonDiscard_DoesNotRehomeContractCompletion()
        {
            if (!PreconditionsMet(out string skip))
            {
                InGameAssert.Skip(skip);
                return;
            }

            string contractId = "parsek-ingame-abandon-" + Guid.NewGuid().ToString("N");
            string recId = "parsek-ingame-abandon-rec-" + Guid.NewGuid().ToString("N");

            RunSeededDiscardScenario(
                preserveIrreversibleLiveGameplay: false,
                contractId: contractId,
                recId: recId,
                afterRecalc: () =>
                {
                    InGameAssert.IsFalse(LedgerHasDirectComplete(contractId),
                        "Abandon path must NOT re-home the completion into the ledger "
                        + "(KSP is being reverted)");
                });

            ParsekLog.Info(Tag,
                "AbandonDiscard_DoesNotRehomeContractCompletion: passed (no re-home on the "
                + "revert/quickload path).");
        }

        // ----------------------------------------------------------------

        private static bool PreconditionsMet(out string skipReason)
        {
            skipReason = null;
            if (HighLogic.CurrentGame == null
                || HighLogic.CurrentGame.Mode != Game.Modes.CAREER)
            {
                skipReason = "Discard-economy in-game test is career-only";
                return false;
            }
            if (Funding.Instance == null
                || ResearchAndDevelopment.Instance == null
                || Reputation.Instance == null)
            {
                skipReason = "Funding/R&D/Reputation singletons not all initialized";
                return false;
            }
            if (RecordingStore.HasPendingTree
                || GameStateRecorder.HasActiveUncommittedTree()
                || GameStateRecorder.HasLiveRecorder())
            {
                skipReason = "A live/pending tree would defer patching — stop recording and "
                    + "commit/discard any pending tree first";
                return false;
            }
            return true;
        }

        private static bool LedgerHasDirectComplete(string contractId)
        {
            foreach (var a in Ledger.Actions)
            {
                if (a == null) continue;
                if (a.Type == GameActionType.ContractComplete
                    && a.ContractId == contractId
                    && string.IsNullOrEmpty(a.RecordingId))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Seeds a KSC-accepted (direct ledger action, active) contract whose completion is
        /// tagged to a pending recording, runs the real discard + recalc, invokes the
        /// caller's assertions, then fully restores the ledger / event store / live pools.
        /// </summary>
        private static void RunSeededDiscardScenario(
            bool preserveIrreversibleLiveGameplay,
            string contractId,
            string recId,
            Action afterRecalc)
        {
            int beforeActions = Ledger.Actions.Count;
            int beforeEvents = GameStateStore.EventCount;
            double fundsBefore = Funding.Instance.Funds;
            float scienceBefore = ResearchAndDevelopment.Instance.Science;
            float repBefore = Reputation.Instance.reputation;

            try
            {
                LedgerOrchestrator.Initialize();

                // Accept at KSC: a direct (untagged) ledger action -> contract starts ACTIVE.
                Ledger.AddAction(new GameAction
                {
                    Type = GameActionType.ContractAccept,
                    ContractId = contractId,
                    UT = 100.0,
                    DeadlineUT = float.NaN,
                });

                // Completed during the recorded flight: tagged to the pending recording id.
                var complete = new GameStateEvent
                {
                    ut = 200.0,
                    eventType = GameStateEventType.ContractCompleted,
                    key = contractId,
                    detail = "",
                    recordingId = recId,
                };
                GameStateStore.AddEvent(ref complete);

                var tree = new RecordingTree
                {
                    Id = "parsek-ingame-rehome-tree-" + recId,
                    TreeName = "parsek-ingame-rehome",
                    RootRecordingId = recId,
                    ActiveRecordingId = recId,
                };
                tree.Recordings[recId] = new Recording { RecordingId = recId, TreeId = tree.Id };
                RecordingStore.StashPendingTree(tree);

                // REAL discard (genuine vs abandon) + REAL recalc against live KSP.
                RecordingStore.DiscardPendingTree(preserveIrreversibleLiveGameplay);
                LedgerOrchestrator.RecalculateAndPatch();

                afterRecalc();
            }
            finally
            {
                // Restore: drop the appended (seeded + re-homed) actions/events, re-patch
                // KSP from the restored ledger, then force the live pools back exactly.
                if (RecordingStore.HasPendingTree)
                    RecordingStore.DiscardPendingTree(preserveIrreversibleLiveGameplay: false);
                Ledger.TruncateActionsForTesting(beforeActions);
                GameStateStore.TruncateEventsForTesting(beforeEvents);
                try { LedgerOrchestrator.RecalculateAndPatch(); }
                catch (Exception ex)
                {
                    ParsekLog.Warn(Tag, $"restore recalc threw: {ex.Message}");
                }
                using (SuppressionGuard.Resources())
                {
                    if (Funding.Instance != null)
                    {
                        double d = fundsBefore - Funding.Instance.Funds;
                        if (Math.Abs(d) > 0.01)
                            Funding.Instance.AddFunds(d, TransactionReasons.None);
                    }
                    if (ResearchAndDevelopment.Instance != null)
                    {
                        float d = scienceBefore - ResearchAndDevelopment.Instance.Science;
                        if (Mathf.Abs(d) > 0.01f)
                            ResearchAndDevelopment.Instance.AddScience(d, TransactionReasons.None);
                    }
                    if (Reputation.Instance != null
                        && Mathf.Abs(repBefore - Reputation.Instance.reputation) > 0.01f)
                    {
                        Reputation.Instance.SetReputation(repBefore, TransactionReasons.None);
                    }
                }
            }
        }
    }
}
