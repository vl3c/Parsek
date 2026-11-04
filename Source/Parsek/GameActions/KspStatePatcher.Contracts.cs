using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Contracts;

namespace Parsek
{
    // Contract list patch: entry point, stock-load deferral, twin withdrawal, the core pass and its clocks.
    internal static partial class KspStatePatcher
    {
        /// <summary>
        /// Patches KSP's contract system to match the ledger's derived state.
        /// Unregisters and clears all current contracts, then rebuilds from ConfigNode
        /// snapshots stored in GameStateStore for each contract that should be active
        /// according to the ContractsModule walk.
        ///
        /// Uses the Contract.Load path which sets state directly via enum parsing
        /// without calling SetState — no rewards, penalties, or GameEvents are fired.
        ///
        /// CRITICAL: Contract.Load mutates the input ConfigNode by removing the "type"
        /// value, so we always clone the snapshot before passing it to the load pipeline.
        /// </summary>
        internal static void PatchContracts(ContractsModule contracts, double acceptClockUT = double.NaN,
            double parameterClockUT = double.NaN)
        {
            ContractPatchTiming timing = DecideContractPatchTiming(
                ContractSystem.Instance != null, ContractSystem.loaded);
            if (timing == ContractPatchTiming.Skip)
            {
                ParsekLog.Verbose(Tag,
                    "PatchContracts: ContractSystem.Instance is null — skipping");
                return;
            }
            if (timing == ContractPatchTiming.DeferUntilStockLoaded)
            {
                DeferContractPatchUntilStockLoaded(contracts, acceptClockUT, parameterClockUT);
                return;
            }
            // A patch that runs against the loaded list supersedes one still waiting.
            if (DeferredContractPatchState.Cancel())
                ParsekLog.Verbose(Tag, "PatchContracts: pending deferred patch dropped (a run-now patch superseded it)");
            PatchContractsCore(contracts, acceptClockUT, parameterClockUT);
        }

        /// <summary>When a contract patch may run against stock's live contract list.</summary>
        internal enum ContractPatchTiming
        {
            Skip,
            DeferUntilStockLoaded,
            RunNow
        }

        /// <summary>
        /// CONTRACT-RESTORED-AT-SCENE-LOAD-PAYS-TWICE. A scene load reaches the patch from
        /// <c>ParsekScenario.OnLoad</c>, synchronously, while stock's
        /// <c>ContractSystem.OnLoadRoutine</c> is still one frame away: <c>OnAwake</c> set
        /// <c>ContractSystem.loaded = false</c>, and the routine will <c>contracts.Clear()</c>
        /// WITHOUT <c>Unregister()</c> before loading the save's own copies. A contract the
        /// patch restored and registered into that list is dropped while still subscribed
        /// to GameEvents, so it completes and pays alongside stock's copy (three payouts for
        /// one completion after KSC -> VAB -> launch, CS-3 run 2026-10-25_2257). Until stock
        /// has loaded, the patch therefore waits for its <c>onContractsLoaded</c>.
        /// </summary>
        internal static ContractPatchTiming DecideContractPatchTiming(
            bool contractSystemPresent, bool stockContractsLoaded)
        {
            if (!contractSystemPresent)
                return ContractPatchTiming.Skip;
            return stockContractsLoaded
                ? ContractPatchTiming.RunNow
                : ContractPatchTiming.DeferUntilStockLoaded;
        }

        /// <summary>
        /// The one deferred scene-load contract patch. Pure bookkeeping, so the routing is
        /// testable without a live ContractSystem: <see cref="Defer"/> keeps the latest
        /// request, <see cref="TryRun"/> runs it at most once on stock's onContractsLoaded,
        /// and Parsek's own Fire from inside the core finds nothing pending (the pending
        /// flag clears before the core runs; <c>running</c> guards a re-entrant call).
        /// </summary>
        internal sealed class DeferredContractPatch
        {
            private bool pending;
            private bool running;
            private ContractsModule module;
            private double clockUT = double.NaN;
            private double parameterClockUT = double.NaN;

            internal bool HasPending => pending;

            /// <summary>Keeps the latest request; true when one was already waiting.</summary>
            internal bool Defer(ContractsModule contracts, double acceptClockUT)
            {
                return Defer(contracts, acceptClockUT, double.NaN);
            }

            /// <summary>
            /// As above, with the parameter-patch clock (passed through as captured: a
            /// silent parameter completion has no undo, so it never runs on a re-read clock).
            /// </summary>
            internal bool Defer(ContractsModule contracts, double acceptClockUT, double parameterPatchClockUT)
            {
                bool already = pending;
                pending = true;
                module = contracts;
                clockUT = acceptClockUT;
                parameterClockUT = parameterPatchClockUT;
                return already;
            }

            /// <summary>Drops a waiting request; true when one was dropped.</summary>
            internal bool Cancel()
            {
                bool had = pending;
                pending = false;
                module = null;
                clockUT = double.NaN;
                parameterClockUT = double.NaN;
                return had;
            }

            /// <summary>
            /// Runs the waiting request once through <paramref name="core"/>, with the clock
            /// <paramref name="resolveClock"/> turns the captured one into. False when
            /// nothing was pending or a run is already in progress.
            /// </summary>
            internal bool TryRun(Func<double, double> resolveClock, Action<ContractsModule, double> core)
            {
                return TryRun(resolveClock, (m, c, p) => core?.Invoke(m, c));
            }

            /// <summary>As above, also handing the core the captured parameter-patch clock.</summary>
            internal bool TryRun(Func<double, double> resolveClock,
                Action<ContractsModule, double, double> core)
            {
                if (!pending || running)
                    return false;
                ContractsModule m = module;
                double captured = clockUT;
                double parameterCaptured = parameterClockUT;
                pending = false;
                module = null;
                clockUT = double.NaN;
                parameterClockUT = double.NaN;
                double clock = resolveClock != null ? resolveClock(captured) : captured;
                running = true;
                try
                {
                    core?.Invoke(m, clock, parameterCaptured);
                }
                finally
                {
                    running = false;
                }
                return true;
            }
        }

        /// <summary>
        /// Pure: the clock a deferred patch holds later accepts against. A real captured
        /// clock stands. A NaN captured inside OnLoad (the live clock was still the previous
        /// game's) is re-read: stock fires onContractsLoaded a frame after the scenario
        /// OnLoad, when the save's clock is set, and a plain-walk load (a revert) with
        /// committed future accepts would otherwise restore a later-accepted contract as a
        /// visible Active one. Still NaN if the load is somehow still in progress.
        /// </summary>
        internal static double ResolveDeferredContractClockUT(
            double capturedClockUT, bool onLoadInProgress, Func<double> liveClockUT)
        {
            if (!double.IsNaN(capturedClockUT))
                return capturedClockUT;
            if (onLoadInProgress || liveClockUT == null)
                return double.NaN;
            return liveClockUT();
        }

        private static void DeferContractPatchUntilStockLoaded(
            ContractsModule contracts, double acceptClockUT, double parameterClockUT)
        {
            bool alreadyDeferred = DeferredContractPatchState.Defer(contracts, acceptClockUT, parameterClockUT);
            if (stockContractsLoadedHandler == null)
            {
                stockContractsLoadedHandler = () => OnStockContractsLoaded();
                GameEvents.Contract.onContractsLoaded.Add(stockContractsLoadedHandler);
            }
            ParsekLog.Info(Tag,
                "PatchContracts: deferred - stock ContractSystem has not loaded the save's " +
                "contracts yet (loaded=False); the patch runs on its onContractsLoaded " +
                $"(alreadyDeferred={alreadyDeferred.ToString(IC)})");
        }

        private static void OnStockContractsLoaded()
        {
            if (!DeferredContractPatchState.HasPending)
                return;
            if (ContractSystem.Instance == null)
            {
                DeferredContractPatchState.Cancel();
                ParsekLog.Verbose(Tag,
                    "PatchContracts: deferred patch dropped - ContractSystem.Instance is null at onContractsLoaded");
                return;
            }
            // Stock fires onContractsLoaded before it sets loaded = true, so the core runs
            // directly rather than re-entering the timing gate.
            DeferredContractPatchState.TryRun(
                captured => ResolveDeferredContractClockUT(
                    captured, ParsekScenario.IsOnLoadInProgress,
                    ParsekScenario.GetCurrentTimelineUTForLedgerRecalc),
                (module, clock, parameterClock) =>
                {
                    ParsekLog.Info(Tag,
                        "PatchContracts: running the deferred scene-load patch on stock's onContractsLoaded " +
                        $"(kspCurrent={(ContractSystem.Instance.Contracts != null ? ContractSystem.Instance.Contracts.Count : 0).ToString(IC)}, " +
                        $"clockUT={clock.ToString("R", IC)})");
                    using (SuppressionGuard.ResourcesAndReplay())
                        PatchContractsCore(module, clock, parameterClock);
                });
        }

        internal static void ResetDeferredContractPatch()
        {
            DeferredContractPatchState.Cancel();
        }

        /// <summary>
        /// Contract twins stay out of the offer pool (design 3.15 step 8): withdraws, the way
        /// stock withdraws a surplus offer (<c>Contract.Withdraw</c>), every Offered contract
        /// that twins a committed future accept (<see cref="ContractTwinRule"/>: stock's own
        /// uniqueness id or a once-per-career goal key, a different guid). Returns how many it
        /// withdrew.
        /// A withdrawal has no undo, so it judges only against a real clock (the patch's
        /// parameter clock: the walk's cutoff, else the committed-row clock) and withdraws
        /// nothing without one, never against a fallback UT.
        /// </summary>
        internal static int WithdrawOfferedContractTwins(List<Contract> current, double clockUT)
        {
            if (current == null || current.Count == 0)
                return 0;
            if (!IsRealClock(clockUT))
            {
                ParsekLog.Verbose(ContractTwinRule.Tag,
                    $"PatchContracts: no real clock (clockUT={clockUT.ToString("R", IC)}) - no twin offer withdrawn");
                return 0;
            }
            double nowUT = clockUT;
            CommittedFutureIndex twinIndex = CommittedFutureIndexCache.Current;
            var offered = new List<ContractTwinRule.OfferedContract>(current.Count);
            for (int i = 0; i < current.Count; i++)
            {
                Contract c = current[i];
                if (c == null)
                    continue;
                long stockId = 0;
                try { stockId = c.ContractID; }
                catch (Exception ex)
                {
                    ParsekLog.VerboseRateLimited(Tag, "twin-id-" + c.ContractGuid,
                        $"PatchContracts: reading ContractID of '{c.Title}' threw ({ex.Message}) - judged on the goal key only");
                }
                bool isOffered = c.ContractState == Contract.State.Offered;
                offered.Add(new ContractTwinRule.OfferedContract
                {
                    Index = i,
                    Guid = c.ContractGuid.ToString(),
                    StockContractId = stockId,
                    GoalKey = isOffered
                        ? ContractGoalKeyReader.ReadIfJudged(c, twinIndex, nowUT, "PatchContracts")
                        : null,
                    Title = c.Title,
                    Offered = isOffered
                });
            }
            var twins = ContractTwinRule.SelectOfferedTwins(offered, twinIndex, nowUT);
            int withdrawn = 0;
            for (int i = 0; i < twins.Count; i++)
            {
                ContractTwinRule.OfferedContract t = twins[i].Key;
                Contract live = current[t.Index];
                try
                {
                    live.Withdraw();
                    withdrawn++;
                    ParsekLog.Info(ContractTwinRule.Tag,
                        $"PatchContracts: withdrew offer '{t.Title}' (guid={t.Guid} stockId={t.StockContractId.ToString(IC)}" +
                        $"{(t.GoalKey != null ? " goal=" + t.GoalKey : "")}): " +
                        $"it twins {ContractTwinRule.DescribeHolder(twins[i].Value)} at ut={nowUT.ToString("R", IC)} " +
                        ContractTwinRule.DescribeMatch(ContractTwinRule.MatchOf(t, twins[i].Value), twins[i].Value));
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(ContractTwinRule.Tag,
                        $"PatchContracts: withdrawing twin offer '{t.Title}' (guid={t.Guid}) threw: {ex.Message}");
                }
            }
            if (twins.Count > 0)
                ParsekLog.Info(ContractTwinRule.Tag,
                    $"PatchContracts: {withdrawn.ToString(IC)} of {twins.Count.ToString(IC)} twin offer(s) withdrawn " +
                    $"({offered.Count.ToString(IC)} contract(s) checked)");
            return withdrawn;
        }

        private static void PatchContractsCore(ContractsModule contracts, double acceptClockUT,
            double parameterClockUT)
        {
            if (contracts == null)
            {
                ParsekLog.Verbose(Tag,
                    "PatchContracts: null ContractsModule — skipping");
                return;
            }

            // Get the set of contract IDs that should be active after the ledger walk,
            // less any the committed timeline accepts only after the clock (a walk past
            // the clock; NaN filters nothing, see ResolveContractAcceptClockUT).
            List<string> acceptedLater;
            var activeIds = SelectContractsAcceptedByNow(
                contracts.GetActiveContractIds(),
                contracts.GetActiveContractAcceptUT,
                acceptClockUT,
                out acceptedLater);
            if (acceptedLater.Count > 0)
            {
                ParsekLog.Info(Tag,
                    $"PatchContracts: kept {acceptedLater.Count.ToString(IC)} contract(s) out of stock's active list " +
                    $"until the clock reaches their committed accept: [{string.Join(", ", acceptedLater.ToArray())}]");
            }
            var activeIdSet = new HashSet<Guid>();
            foreach (var idStr in activeIds)
            {
                if (Guid.TryParse(idStr, out var gid))
                    activeIdSet.Add(gid);
            }
            var terminalIds = contracts.GetTerminalContractIds();
            var terminalIdSet = new HashSet<Guid>();
            foreach (var idStr in terminalIds)
            {
                if (Guid.TryParse(idStr, out var gid))
                    terminalIdSet.Add(gid);
            }
            var terminalOutcomeById = BuildTerminalContractOutcomeMap(contracts);

            // If no active contracts in ledger, just log the current KSP state
            int currentCount = ContractSystem.Instance.Contracts != null
                ? ContractSystem.Instance.Contracts.Count
                : 0;
            int finishedCount = ContractSystem.Instance.ContractsFinished != null
                ? ContractSystem.Instance.ContractsFinished.Count
                : 0;

            ParsekLog.Verbose(Tag,
                $"PatchContracts: ledger has {activeIds.Count.ToString(IC)} active contracts, " +
                $"{terminalIds.Count.ToString(IC)} terminal contracts, " +
                $"KSP has {currentCount.ToString(IC)} current contracts, " +
                $"{finishedCount.ToString(IC)} finished contracts");

            // #404/#756: Filtered remove. Active current contracts whose id is NOT
            // in the ledger's active set are stale and can be removed. Offered and
            // Declined entries still stay untouched. Terminal stock rows in either
            // current contracts or ContractsFinished are removable only for explicit
            // tombstoned Parsek contract action ids when their terminal ELS row no
            // longer survives.
            // Filtering is delegated to PartitionContractsForPatch for testability.
            var currentContracts = ContractSystem.Instance.Contracts;
            var finishedContracts = ContractSystem.Instance.ContractsFinished;
            var currentEntries = BuildContractFilterEntries(currentContracts);
            var finishedEntries = BuildContractFilterEntries(finishedContracts);
            var tombstonedContractGuids =
                LedgerOrchestrator.BuildTombstonedContractGuidsForPatch();
            // Per-contract "rebuild it as of THIS UT" cutoffs. Resolved here rather than
            // taken as a parameter because the tombstoned-guid set above is resolved the
            // same way, at the same point, from the same source.
            var reinstateCutoffs =
                LedgerOrchestrator.BuildContractReinstateCutoffsForPatch();

            PartitionContractsForPatch(currentEntries, finishedEntries,
                activeIdSet, terminalIdSet, terminalOutcomeById, tombstonedContractGuids,
                out var toRemoveCurrent, out var toRemoveFinished,
                out var survivingActiveIds,
                out var survivingTerminalIds);
            var toRemoveCurrentKeys = BuildCurrentContractRemovalKeys(
                currentEntries,
                activeIdSet,
                terminalIdSet,
                terminalOutcomeById,
                tombstonedContractGuids);
            var toRemoveFinishedKeys = BuildFinishedContractRemovalKeys(
                finishedEntries,
                terminalIdSet,
                terminalOutcomeById,
                tombstonedContractGuids);

            // An Active contract the walk has already ended (the clock crossed its committed
            // outcome row) leaves the active list below; it goes into stock's finished list in
            // the outcome's state rather than vanishing from the Archive.
            var terminalUtById = BuildTerminalContractUtMap(contracts);
            var retiredMoves = PlanRetiredActiveContractMoves(
                currentEntries, toRemoveCurrentKeys, terminalIdSet, terminalOutcomeById,
                terminalUtById, finishedEntries, toRemoveFinishedKeys, parameterClockUT,
                out int archiveAlreadyFinished, out int archiveNoFinishedState,
                out int archiveOutcomeLater, out int archiveNoClock);
            if (archiveNoClock > 0)
            {
                ParsekLog.Info(Tag,
                    $"PatchContracts: {archiveNoClock.ToString(IC)} ended contract(s) leave the active list " +
                    $"without being filed into stock's finished list - no real clock " +
                    $"(clockUT={parameterClockUT.ToString("R", IC)}) to prove the outcome already happened");
            }

            int removedStale = 0;
            int removedFinishedTombstoned = 0;
            int unregisterFailures = 0;
            var removedCurrentContracts = new List<Contract>();
            removedStale = RemoveContractsByKey(
                currentContracts,
                toRemoveCurrentKeys,
                unregisterBeforeRemove: true,
                ref unregisterFailures,
                removedCurrentContracts);
            removedFinishedTombstoned = RemoveContractsByKey(
                finishedContracts,
                toRemoveFinishedKeys,
                unregisterBeforeRemove: false,
                ref unregisterFailures);

            int archived = 0;
            int archiveFailed = 0;
            ArchiveRetiredActiveContracts(removedCurrentContracts, retiredMoves, finishedContracts,
                ref archived, ref archiveFailed);

            // An Offered copy of a contract whose lifecycle the walk already ended. A rewind to
            // before the accept puts the contract back on offer; when no recalculation ran
            // between the committed accept and the committed outcome (a flight passing both,
            // the flight scene has no clock cursor), the rebuild below never replaced the
            // Offered copy, and it would stay acceptable for a second advance.
            var staleOfferedKeys = BuildEndedLifecycleOfferedRemovalKeys(
                currentEntries, activeIdSet, terminalIdSet);
            int removedStaleOffered = 0;
            if (staleOfferedKeys.Count > 0)
            {
                removedStaleOffered = RemoveContractsByKey(
                    currentContracts,
                    staleOfferedKeys,
                    unregisterBeforeRemove: true,
                    ref unregisterFailures);
                var staleOfferedIds = new List<string>();
                foreach (var key in staleOfferedKeys)
                    staleOfferedIds.Add(key.Id.ToString());
                staleOfferedIds.Sort(StringComparer.Ordinal);
                ParsekLog.Info(Tag,
                    $"PatchContracts: removed {removedStaleOffered.ToString(IC)} Offered copy(ies) of contract(s) " +
                    $"whose committed lifecycle already ended: " +
                    ComposeBoundedIdentitySample(staleOfferedIds, IdentitySampleCap));
            }

            ParsekLog.Verbose(Tag,
                $"PatchContracts: removed {removedStale.ToString(IC)} stale Active/terminal contract(s), " +
                $"{removedFinishedTombstoned.ToString(IC)} tombstoned finished terminal contract(s), " +
                $"{survivingActiveIds.Count.ToString(IC)} Active contract(s) preserved in place, " +
                $"{survivingTerminalIds.Count.ToString(IC)} terminal contract(s) preserved in place " +
                $"(unregisterFailures={unregisterFailures.ToString(IC)})");

            // These are the TARGETED removal keys (pure set): current keys first, then
            // finished keys. The count can exceed the actually-removed count if a targeted
            // live contract was already absent from the KSP list.
            var removedIds = DescribeRemovedContractIdentities(
                toRemoveCurrentKeys, toRemoveFinishedKeys);

            // 2. Rebuild ONLY contracts that are in the ledger but not already present.
            int restored = 0;
            int skippedExisting = 0;
            int noSnapshot = 0;
            int noType = 0;
            int typeNotFound = 0;
            int loadFailed = 0;
            int registered = 0;
            var restoredIds = new List<string>();

            foreach (string contractId in activeIds)
            {
                // Skip contracts that survived the filtered remove — no need to unregister
                // and recreate them, preserving parameter subscriptions that are already hot.
                if (Guid.TryParse(contractId, out var cg) && survivingActiveIds.Contains(cg))
                {
                    skippedExisting++;
                    continue;
                }

                // Prefer the rewind-point snapshot at or before this contract's reinstate
                // cutoff, so a contract that was half-finished when the timeline rewound
                // comes back half-finished instead of reset to accept-time zero progress.
                double? cutoffUt = null;
                if (reinstateCutoffs != null && Guid.TryParse(contractId, out var cutoffGuid))
                {
                    double cutoffValue;
                    if (reinstateCutoffs.TryGetValue(cutoffGuid, out cutoffValue))
                        cutoffUt = cutoffValue;
                }

                ConfigNode snapshot = GameStateStore.GetContractSnapshotForPatch(contractId, cutoffUt);
                if (snapshot == null)
                {
                    noSnapshot++;
                    ParsekLog.Verbose(Tag,
                        $"PatchContracts: no snapshot for contractId='{contractId}' — skipping");
                    continue;
                }

                // Clone the snapshot — Contract.Load mutates the input by removing "type"
                ConfigNode cloned = snapshot.CreateCopy();

                // Read and remove the type value (same pattern as ContractSystem.LoadContract)
                string typeName = cloned.GetValue("type");
                if (string.IsNullOrEmpty(typeName))
                {
                    noType++;
                    ParsekLog.Warn(Tag,
                        $"PatchContracts: snapshot for contractId='{contractId}' has no 'type' value — skipping");
                    continue;
                }

                cloned.RemoveValues("type");

                // Look up the contract type in KSP's type registry
                Type contractType = ContractSystem.GetContractType(typeName);
                if (contractType == null)
                {
                    typeNotFound++;
                    ParsekLog.Warn(Tag,
                        $"PatchContracts: type '{typeName}' not found in ContractSystem.ContractTypes " +
                        $"for contractId='{contractId}' — skipping (mod removed?)");
                    continue;
                }

                // Create instance and load state from ConfigNode
                try
                {
                    Contract contract = (Contract)Activator.CreateInstance(contractType);
                    contract = Contract.Load(contract, cloned);

                    if (contract == null)
                    {
                        loadFailed++;
                        ParsekLog.Warn(Tag,
                            $"PatchContracts: Contract.Load returned null for contractId='{contractId}' " +
                            $"type='{typeName}' — skipping");
                        continue;
                    }

                    // Only add Active-state contracts. GetActiveContractIds only yields actives,
                    // so any other state here is either a mis-snapshotted entry or a contract
                    // that mutated between snapshot and now — either way, we do NOT mutate the
                    // Finished bucket (see #404: append-only game history).
                    if (contract.ContractState == Contract.State.Active)
                    {
                        // A load back in time puts an accepted contract back on offer: the
                        // live list then holds the quicksave's Offered instance with the same
                        // guid. Replace it, or stock lists the contract twice (ContractsApp
                        // throws on the duplicate key) and the Offered copy can be accepted
                        // again, paying the advance a second time.
                        int replacedOffered = RemoveOfferedCopiesOfContract(
                            currentContracts, contract.ContractGuid, ref unregisterFailures);
                        if (replacedOffered > 0)
                        {
                            ParsekLog.Info(Tag,
                                $"PatchContracts: replaced {replacedOffered.ToString(IC)} Offered copy(ies) of " +
                                $"contractId='{contractId}' with the ledger's Active contract");
                        }
                        currentContracts.Add(contract);

                        // Re-register so parameters subscribe to GameEvents
                        try
                        {
                            contract.Register();
                            registered++;
                        }
                        catch (Exception ex)
                        {
                            ParsekLog.Warn(Tag,
                                $"PatchContracts: failed to register contract '{contractId}' " +
                                $"type='{typeName}': {ex.Message}");
                        }

                        restored++;
                        restoredIds.Add(contractId);
                    }
                    else
                    {
                        ParsekLog.Warn(Tag,
                            $"PatchContracts: snapshot for contractId='{contractId}' loaded in " +
                            $"state={contract.ContractState} (expected Active) — skipping, not mutating " +
                            $"Finished bucket");
                    }
                }
                catch (Exception ex)
                {
                    loadFailed++;
                    ParsekLog.Warn(Tag,
                        $"PatchContracts: failed to create/load contract contractId='{contractId}' " +
                        $"type='{typeName}': {ex.Message}");
                }
            }

            // 2b. A parameter reward the ledger credited at or before the clock must not be
            // payable again: mark that stock parameter Complete the way stock leaves it after
            // paying, without paying.
            PatchContractParameters(contracts, currentContracts, parameterClockUT);

            // 3. Fire contracts loaded event so UI refreshes
            try
            {
                GameEvents.Contract.onContractsLoaded.Fire();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"PatchContracts: onContractsLoaded.Fire() threw: {ex.Message}");
            }

            int kspTotalAfter = currentContracts != null ? currentContracts.Count : 0;
            int kspFinishedAfter = finishedContracts != null ? finishedContracts.Count : 0;
            string removedSample =
                ComposeBoundedIdentitySample(removedIds, IdentitySampleCap);
            string restoredSample =
                ComposeBoundedIdentitySample(restoredIds, IdentitySampleCap);
            ParsekLog.Info(Tag,
                $"PatchContracts: removedStale={removedStale.ToString(IC)}, " +
                $"removedFinishedTombstoned={removedFinishedTombstoned.ToString(IC)}, " +
                $"restored={restored.ToString(IC)}, " +
                $"registered={registered.ToString(IC)}, " +
                $"skippedExisting={skippedExisting.ToString(IC)}, " +
                $"noSnapshot={noSnapshot.ToString(IC)}, " +
                $"noType={noType.ToString(IC)}, " +
                $"typeNotFound={typeNotFound.ToString(IC)}, " +
                $"loadFailed={loadFailed.ToString(IC)}, " +
                $"archivedToFinished={archived.ToString(IC)}, " +
                $"archiveAlreadyFinished={archiveAlreadyFinished.ToString(IC)}, " +
                $"archiveNoFinishedState={archiveNoFinishedState.ToString(IC)}, " +
                $"archiveOutcomeLater={archiveOutcomeLater.ToString(IC)}, " +
                $"archiveNoClock={archiveNoClock.ToString(IC)}, " +
                $"archiveFailed={archiveFailed.ToString(IC)}, " +
                $"ledgerActive={activeIds.Count.ToString(IC)}, " +
                $"ledgerTerminal={terminalIds.Count.ToString(IC)}, " +
                $"kspTotal={kspTotalAfter.ToString(IC)}, " +
                $"kspFinished={kspFinishedAfter.ToString(IC)} " +
                $"(Offered and unrelated finished history preserved; tombstoned terminal filtered)" +
                (removedSample.Length > 0
                    ? $", removedIds=[{removedSample}]"
                    : string.Empty) +
                (restoredSample.Length > 0
                    ? $", restoredIds=[{restoredSample}]"
                    : string.Empty));

            if (removedIds.Count > 0)
                ParsekLog.Verbose(Tag,
                    $"PatchContracts: all {removedIds.Count.ToString(IC)} targeted removed contract id(s): " +
                    string.Join(", ", removedIds));

            if (restoredIds.Count > 0)
                ParsekLog.Verbose(Tag,
                    $"PatchContracts: all {restoredIds.Count.ToString(IC)} restored contract id(s): " +
                    string.Join(", ", restoredIds));

            // Design 3.15 step 8: an offer that twins a committed future accept leaves the pool.
            WithdrawOfferedContractTwins(currentContracts, parameterClockUT);

            // LedgerTrace Tier-B (per-contract change lines, reusing the #1098
            // removed/restored sets) + Tier-C (presence read-back reconcile). The
            // read-back builds a set of live-present contract guids from the current
            // contracts list, so it is guarded by IsEnabled — the extra work only runs
            // when tracing. Intended: a restored id must be present, a removed id absent.
            if (LedgerTrace.IsEnabled)
            {
                // Coverage boundary (intentional, not a bug): livePresentGuids is built
                // from the ACTIVE contract bucket (currentContracts) only. removedIds can
                // include finished-bucket removals, whose absence from this active set
                // would read as "absent" — which matches the intended-removed expectation,
                // so this never produces a false positive. Reconciling the finished bucket
                // is out of v1 scope; this read-back reconciles the active bucket only.
                var livePresentGuids = new HashSet<string>(StringComparer.Ordinal);
                if (currentContracts != null)
                {
                    foreach (var c in currentContracts)
                    {
                        if (c != null)
                            livePresentGuids.Add(c.ContractGuid.ToString());
                    }
                }

                foreach (string contractId in restoredIds)
                {
                    LedgerTrace.EmitOnChange("contract", contractId, "restored");
                    bool present = livePresentGuids.Contains(contractId);
                    if (LedgerTrace.IsContractPresenceMismatch(true, present))
                        LedgerTrace.EmitAnomaly("contract", contractId, "ledger-vs-truth",
                            "intendedPresent=true actualPresent=" + LedgerTrace.Bool(present));
                }
                foreach (string contractId in removedIds)
                {
                    LedgerTrace.EmitOnChange("contract", contractId, "removed");
                    bool present = livePresentGuids.Contains(contractId);
                    if (LedgerTrace.IsContractPresenceMismatch(false, present))
                        LedgerTrace.EmitAnomaly("contract", contractId, "ledger-vs-truth",
                            "intendedPresent=false actualPresent=" + LedgerTrace.Bool(present));
                }
            }
        }

        /// <summary>Slack between an accept row's UT and the clock read that follows it.</summary>
        internal const double ContractAcceptClockToleranceSeconds = 1.0;

        private static bool IsRealClock(double ut)
        {
            return !double.IsNaN(ut) && !double.IsInfinity(ut) && ut > 0.0
                && !KerbalsModule.IsNoTimeFilterCutoff(ut);
        }

        /// <summary>
        /// The clock <see cref="PatchContracts"/> holds later accepts against, or NaN for no
        /// filter. A walk with a real cutoff already left out every accept after it, so it
        /// needs none. A walk past the clock judges against the clock its caller named (the
        /// Re-Fly invoke's session UT); without one it reads the live clock, except while
        /// ParsekScenario.OnLoad runs: stock loads the scenario modules before it sets the
        /// clock from the save, so the live clock can still be the previous game's, and a
        /// stale clock would remove a contract that really is Active. A later patch at the
        /// save's clock (the OnLoad cutoff walk, or the next recalculation) holds it then.
        /// </summary>
        internal static double ResolveContractAcceptClockUT(
            double? walkUtCutoff, double? callerClockUT, bool onLoadInProgress, Func<double> liveClockUT)
        {
            if (walkUtCutoff.HasValue && !double.IsNaN(walkUtCutoff.Value)
                && !double.IsInfinity(walkUtCutoff.Value)
                && !KerbalsModule.IsNoTimeFilterCutoff(walkUtCutoff.Value))
                return double.NaN;
            if (callerClockUT.HasValue && !double.IsNaN(callerClockUT.Value)
                && !double.IsInfinity(callerClockUT.Value)
                && !KerbalsModule.IsNoTimeFilterCutoff(callerClockUT.Value))
                return callerClockUT.Value;
            if (onLoadInProgress || liveClockUT == null)
                return double.NaN;
            return liveClockUT();
        }

        /// <summary>
        /// Pure: the ledger-active contracts stock may hold Active at <paramref name="nowUT"/>.
        /// A walk that ran past now (the Re-Fly invoke walks the whole ledger so the session
        /// shows the committed career, design section 6.8 step 5) leaves a contract active
        /// whose accept the committed timeline makes later, at the Space Center after the
        /// rewind point. Restored Active, a re-fly could complete or fail it before it was
        /// accepted, and the walk would then credit the completion with no accept before it
        /// and make the contract Active again at its accept (a time-travel paradox, todo
        /// REFLY-SESSION-SHOWS-CONTRACT-ACCEPTED-LATER). Those go to
        /// <paramref name="acceptedLater"/>; stock keeps whatever the loaded save holds for
        /// them (the Offered copy) until the clock reaches the accept. A clock that is not
        /// ready (not positive, not finite, or the walk-everything sentinel) filters nothing.
        /// </summary>
        internal static List<string> SelectContractsAcceptedByNow(
            IEnumerable<string> activeIds,
            Func<string, double> acceptUT,
            double nowUT,
            out List<string> acceptedLater)
        {
            var kept = new List<string>();
            acceptedLater = new List<string>();
            if (activeIds == null) return kept;
            bool clockReady = !double.IsNaN(nowUT) && !double.IsInfinity(nowUT)
                && nowUT > 0.0 && !KerbalsModule.IsNoTimeFilterCutoff(nowUT);
            foreach (string id in activeIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                double ut = clockReady && acceptUT != null ? acceptUT(id) : double.NaN;
                if (!double.IsNaN(ut) && ut > nowUT + ContractAcceptClockToleranceSeconds)
                    acceptedLater.Add(id);
                else
                    kept.Add(id);
            }
            kept.Sort(StringComparer.Ordinal);
            acceptedLater.Sort(StringComparer.Ordinal);
            return kept;
        }
    }
}
