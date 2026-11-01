using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Contracts;

namespace Parsek
{
    // Contract parameter completion mirror and the stock contract re-evaluation.
    internal static partial class KspStatePatcher
    {
        /// <summary>What the live stock contract list shows for one credited parameter.</summary>
        internal enum StockContractParameterState
        {
            /// <summary>Stock has no Active contract with that guid.</summary>
            ContractNotActive,
            /// <summary>The contract is Active but the parameter id resolves to nothing.</summary>
            Unresolved,
            /// <summary>Incomplete or Failed: stock's SetComplete would still pay it.</summary>
            NotComplete,
            Complete
        }

        internal enum ContractParameterPatchDecision
        {
            CompleteSilently,
            AlreadyComplete,
            Unresolved,
            ContractNotActive,
            AfterClock,
            ClockNotReady
        }

        /// <summary>
        /// Pure: the clock credited parameter rows are held against. A walk with a real
        /// cutoff credited nothing after it, so the cutoff is its now; otherwise the
        /// committed-row clock (<see cref="ResolveContractAcceptClockUT"/>: the caller's clock
        /// or the live one, NaN while OnLoad runs). NaN when neither is a real clock.
        /// </summary>
        internal static double ResolveContractParameterClockUT(double? walkUtCutoff, double committedRowClockUT)
        {
            if (walkUtCutoff.HasValue && IsRealClock(walkUtCutoff.Value))
                return walkUtCutoff.Value;
            return IsRealClock(committedRowClockUT) ? committedRowClockUT : double.NaN;
        }

        /// <summary>
        /// Pure: what the contract patch does with one parameter the walk credited. Without a
        /// real clock nothing is completed (<see cref="ContractParameterPatchDecision.ClockNotReady"/>):
        /// unlike the accept filter, a silent completion persists in stock's save
        /// (<c>ContractParameter.Save</c> writes state and rewards) and has no undo, and a
        /// clockless walk (a boot load's full replay, a revert) credits committed-future rows
        /// too. The next patch with a real clock completes it. A row after the clock waits
        /// (the accept filter's tolerance). Otherwise a stock parameter that is not Complete is
        /// completed without payment, so stock cannot pay the reward the ledger already holds
        /// a second time (CONTRACT-PARAMETER-REWARDS-NOT-LEDGERED review).
        /// </summary>
        internal static ContractParameterPatchDecision DecideContractParameterPatch(
            StockContractParameterState stock, double creditedUT, double clockUT)
        {
            if (!IsRealClock(clockUT))
                return ContractParameterPatchDecision.ClockNotReady;
            if (!double.IsNaN(creditedUT)
                && creditedUT > clockUT + ContractAcceptClockToleranceSeconds)
                return ContractParameterPatchDecision.AfterClock;
            switch (stock)
            {
                case StockContractParameterState.ContractNotActive:
                    return ContractParameterPatchDecision.ContractNotActive;
                case StockContractParameterState.Unresolved:
                    return ContractParameterPatchDecision.Unresolved;
                case StockContractParameterState.Complete:
                    return ContractParameterPatchDecision.AlreadyComplete;
                default:
                    return ContractParameterPatchDecision.CompleteSilently;
            }
        }

        /// <summary>The outcome of <see cref="PlanContractParameterPatch"/>.</summary>
        internal sealed class ContractParameterPatchPlan
        {
            internal readonly List<CreditedContractParameter> ToComplete = new List<CreditedContractParameter>();
            internal int AlreadyComplete;
            internal int Unresolved;
            internal int ContractNotActive;
            internal int AfterClock;
            internal int ClockNotReady;
        }

        /// <summary>
        /// Pure: <see cref="DecideContractParameterPatch"/> over every credited parameter,
        /// with stock's state supplied by <paramref name="stockState"/> (contract id,
        /// parameter id). A pair is planned once even if listed twice.
        /// </summary>
        internal static ContractParameterPatchPlan PlanContractParameterPatch(
            IReadOnlyList<CreditedContractParameter> credited,
            Func<string, string, StockContractParameterState> stockState,
            double clockUT)
        {
            var plan = new ContractParameterPatchPlan();
            if (credited == null || stockState == null)
                return plan;
            var planned = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < credited.Count; i++)
            {
                CreditedContractParameter c = credited[i];
                if (!planned.Add(ContractsModule.ContractParameterCreditKey(c.ContractId, c.ParameterId)))
                    continue;
                switch (DecideContractParameterPatch(stockState(c.ContractId, c.ParameterId), c.UT, clockUT))
                {
                    case ContractParameterPatchDecision.CompleteSilently: plan.ToComplete.Add(c); break;
                    case ContractParameterPatchDecision.AlreadyComplete: plan.AlreadyComplete++; break;
                    case ContractParameterPatchDecision.Unresolved: plan.Unresolved++; break;
                    case ContractParameterPatchDecision.ContractNotActive: plan.ContractNotActive++; break;
                    case ContractParameterPatchDecision.AfterClock: plan.AfterClock++; break;
                    case ContractParameterPatchDecision.ClockNotReady: plan.ClockNotReady++; break;
                }
            }
            return plan;
        }

        /// <summary>
        /// Pure: the root-first indices of a parameter id written by
        /// <see cref="GameStateRecorder.BuildContractParameterIndexPath"/> ("1.0" -> [1, 0]),
        /// or null for a malformed id (including the "id:" fallback form).
        /// </summary>
        internal static List<int> ParseContractParameterIndexPath(string parameterId)
        {
            if (string.IsNullOrEmpty(parameterId))
                return null;
            string[] parts = parameterId.Split('.');
            var indices = new List<int>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                int index;
                if (!int.TryParse(parts[i], NumberStyles.None, IC, out index))
                    return null;
                indices.Add(index);
            }
            return indices;
        }

        private static ContractParameter ResolveStockContractParameter(Contract contract, string parameterId)
        {
            if (contract == null || string.IsNullOrEmpty(parameterId))
                return null;
            const string IdPrefix = "id:";
            if (parameterId.StartsWith(IdPrefix, StringComparison.Ordinal))
            {
                string stockId = parameterId.Substring(IdPrefix.Length);
                foreach (ContractParameter p in contract.AllParameters)
                {
                    if (p != null && string.Equals(p.ID, stockId, StringComparison.Ordinal))
                        return p;
                }
                return null;
            }
            List<int> indices = ParseContractParameterIndexPath(parameterId);
            if (indices == null)
                return null;
            IContractParameterHost host = contract;
            ContractParameter current = null;
            for (int i = 0; i < indices.Count; i++)
            {
                if (host == null || indices[i] >= host.ParameterCount)
                    return null;
                current = host.GetParameter(indices[i]);
                host = current;
            }
            return current;
        }

        /// <summary>
        /// Puts a stock parameter in the state stock leaves it in after
        /// <c>ContractParameter.SetComplete</c> paid it (decompiled KSP 1.12.5): the protected
        /// <c>state</c> field Complete, the three completion rewards zeroed (AwardCompletion
        /// zeroes each one it paid), and the parameter disabled when
        /// <c>DisableOnStateChange</c> (SetComplete's own <c>Disable()</c>, which only
        /// unregisters its event handlers). Nothing is paid and no GameEvents fire: no
        /// AwardCompletion, no OnStateChange, no onParameterChange, no parent
        /// ParameterStateUpdate. The <c>state</c> field has no setter (the <c>State</c>
        /// property is read-only), so it is written by reflection.
        /// </summary>
        internal static bool TryCompleteStockParameterSilently(ContractParameter parameter)
        {
            if (parameter == null)
                return false;
            if (contractParameterStateField == null)
            {
                contractParameterStateField = typeof(ContractParameter).GetField(
                    "state", BindingFlags.Instance | BindingFlags.NonPublic);
                if (contractParameterStateField == null
                    || contractParameterStateField.FieldType != typeof(ParameterState))
                {
                    contractParameterStateField = null;
                    EmitContractParameterStateReflectionWarnOnce(
                        "typeof(ContractParameter).GetField(\"state\") is missing or not a ParameterState");
                    return false;
                }
            }
            try
            {
                contractParameterStateField.SetValue(parameter, ParameterState.Complete);
                parameter.FundsCompletion = 0.0;
                parameter.ScienceCompletion = 0f;
                parameter.ReputationCompletion = 0f;
                if (parameter.DisableOnStateChange)
                    parameter.Disable();
                return true;
            }
            catch (Exception ex)
            {
                EmitContractParameterStateReflectionWarnOnce(
                    $"writing ContractParameter.state threw {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        internal static void EmitContractParameterStateReflectionWarnOnce(string detail)
        {
            if (contractParameterStateReflectionWarnEmitted)
                return;
            contractParameterStateReflectionWarnEmitted = true;
            ParsekLog.Warn(Tag,
                "PatchContracts: could not mark a ledger-credited contract parameter Complete " +
                $"(one-shot per session) - {detail}. Stock may pay that parameter's reward again.");
        }

        /// <summary>Active contracts by guid text.</summary>
        private static Dictionary<string, Contract> IndexActiveContracts(List<Contract> currentContracts)
        {
            var activeById = new Dictionary<string, Contract>(StringComparer.Ordinal);
            if (currentContracts != null)
            {
                for (int i = 0; i < currentContracts.Count; i++)
                {
                    Contract c = currentContracts[i];
                    if (c != null && c.ContractState == Contract.State.Active)
                        activeById[c.ContractGuid.ToString()] = c;
                }
            }
            return activeById;
        }

        /// <summary>
        /// Plans <paramref name="credited"/> against the live Active contracts and completes
        /// every planned parameter silently (<see cref="TryCompleteStockParameterSilently"/>).
        /// Shared by the full contract patch and the add-only mirror
        /// (<see cref="MirrorCommittedContractParameters"/>).
        /// </summary>
        private static ContractParameterPatchPlan CompleteCreditedParametersSilently(
            IReadOnlyList<CreditedContractParameter> credited, Dictionary<string, Contract> activeById,
            double clockUT, out int completed, out int writeFailed, List<string> completedIds)
        {
            ContractParameterPatchPlan plan = PlanContractParameterPatch(credited, (contractId, paramId) =>
            {
                Contract c;
                if (contractId == null || !activeById.TryGetValue(contractId, out c))
                    return StockContractParameterState.ContractNotActive;
                ContractParameter p = ResolveStockContractParameter(c, paramId);
                if (p == null)
                    return StockContractParameterState.Unresolved;
                return p.State == ParameterState.Complete
                    ? StockContractParameterState.Complete
                    : StockContractParameterState.NotComplete;
            }, clockUT);

            completed = 0;
            writeFailed = 0;
            for (int i = 0; i < plan.ToComplete.Count; i++)
            {
                CreditedContractParameter c = plan.ToComplete[i];
                ContractParameter p = ResolveStockContractParameter(activeById[c.ContractId], c.ParameterId);
                if (TryCompleteStockParameterSilently(p))
                {
                    completed++;
                    completedIds?.Add(c.ContractId + "/" + c.ParameterId);
                }
                else
                {
                    writeFailed++;
                }
            }
            return plan;
        }

        /// <summary>
        /// The add-only live mirror for contract parameters (design 3.15 step 5, prevention):
        /// while the KSP patch is deferred, completes without reward every parameter of a live
        /// Active contract that the committed timeline completed at or before
        /// <paramref name="clockUT"/>, so stock cannot pay it again. Unlike the full patch it
        /// hands no contract back to stock's own completion path (that waits for the next
        /// patch that is not deferred): a contract stays Active until then. Returns the count
        /// completed.
        /// </summary>
        internal static int MirrorCommittedContractParameters(
            IReadOnlyList<CreditedContractParameter> credited, double clockUT)
        {
            if (credited == null || credited.Count == 0)
                return 0;
            if (SuppressUnityCallsForTesting)
            {
                VerboseStablePatchState("mirror-skip|parameters|test", "suppressed",
                    "MirrorCommittedContractParameters: Unity calls suppressed for testing - skipping");
                return 0;
            }
            List<Contract> current = ReadLiveContractsOrNull();
            if (current == null)
            {
                VerboseStablePatchState("mirror-skip|parameters|system", "no-contract-system",
                    "MirrorCommittedContractParameters: ContractSystem not loaded in this scene - skipping " +
                    "(the next pass with it loaded completes them)");
                return 0;
            }
            var completedIds = new List<string>();
            ContractParameterPatchPlan plan = CompleteCreditedParametersSilently(
                credited, IndexActiveContracts(current), clockUT, out int completed, out int writeFailed, completedIds);
            string line =
                $"MirrorCommittedContractParameters: completedSilently={completed.ToString(IC)}, " +
                $"alreadyComplete={plan.AlreadyComplete.ToString(IC)}, unresolved={plan.Unresolved.ToString(IC)}, " +
                $"contractNotActive={plan.ContractNotActive.ToString(IC)}, afterClock={plan.AfterClock.ToString(IC)}, " +
                $"clockNotReady={plan.ClockNotReady.ToString(IC)}, writeFailed={writeFailed.ToString(IC)}, " +
                $"clock={clockUT.ToString("R", IC)} " + ComposeBoundedIdentitySample(completedIds, IdentitySampleCap);
            if (completed > 0 || writeFailed > 0)
                ParsekLog.Info(Tag, line);
            else
                VerboseStablePatchState("mirror-noop|parameters",
                    plan.AlreadyComplete.ToString(IC) + "|" + plan.ContractNotActive.ToString(IC) + "|"
                    + plan.Unresolved.ToString(IC), line);
            return completed;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static List<Contract> ReadLiveContractsOrNull()
        {
            return ContractSystem.Instance != null ? ContractSystem.Instance.Contracts : null;
        }

        private static void PatchContractParameters(
            ContractsModule contracts, List<Contract> currentContracts, double clockUT)
        {
            IReadOnlyList<CreditedContractParameter> credited = contracts.GetCreditedParameters();
            if (credited == null || credited.Count == 0)
                return;

            var activeById = IndexActiveContracts(currentContracts);
            var completedIds = new List<string>();
            ContractParameterPatchPlan plan = CompleteCreditedParametersSilently(
                credited, activeById, clockUT, out int completed, out int writeFailed, completedIds);

            // Stock completes a contract (and a group parameter) only from the parent's
            // ParameterStateUpdate that SetComplete runs after paying; the silent write skips
            // it. Every Active contract with a credited parameter that stock would now complete
            // is handed back to stock's own path, one frame later and outside the patch.
            var terminal = new HashSet<string>(contracts.GetTerminalContractIds() ?? new string[0], StringComparer.Ordinal);
            var reEvaluate = new List<string>();
            int ledgerTerminal = 0;
            int completesLater = 0;
            if (IsRealClock(clockUT))
            {
                // A contract the committed timeline completes after the clock keeps waiting
                // for that row: handing it to stock now would move its reward off the flight
                // that earns it (a parameter that went Incomplete again, then re-completed).
                HashSet<string> completedLater = CollectContractsCompletedAfter(
                    EffectiveState.ComputeELS(), clockUT);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < credited.Count; i++)
                {
                    string id = credited[i].ContractId;
                    Contract c;
                    if (id == null || !seen.Add(id) || !activeById.TryGetValue(id, out c))
                        continue;
                    if (terminal.Contains(id))
                    {
                        ledgerTerminal++;
                        continue;
                    }
                    if (completedLater.Contains(id))
                    {
                        completesLater++;
                        continue;
                    }
                    ContractCompletionShape shape = ReadContractCompletionShape(c);
                    if (NeedsStockContractReEvaluation(true, false, shape.TopLevelCount, shape.TopLevelComplete,
                            shape.SatisfiedOpenGroups.Count))
                        reEvaluate.Add(id);
                }
            }

            string line =
                $"PatchContracts: credited contract parameters completedSilently={completed.ToString(IC)}, " +
                $"alreadyComplete={plan.AlreadyComplete.ToString(IC)}, unresolved={plan.Unresolved.ToString(IC)}, " +
                $"contractNotActive={plan.ContractNotActive.ToString(IC)}, afterClock={plan.AfterClock.ToString(IC)}, " +
                $"clockNotReady={plan.ClockNotReady.ToString(IC)}, writeFailed={writeFailed.ToString(IC)}, " +
                $"reEvaluateNextFrame={reEvaluate.Count.ToString(IC)}, ledgerTerminal={ledgerTerminal.ToString(IC)}, " +
                $"completesLaterInLedger={completesLater.ToString(IC)} " +
                ComposeBoundedIdentitySample(completedIds, IdentitySampleCap);
            if (completed > 0 || plan.Unresolved > 0 || writeFailed > 0 || reEvaluate.Count > 0)
                ParsekLog.Info(Tag, line);
            else
                ParsekLog.Verbose(Tag, line);

            if (reEvaluate.Count == 0)
                return;
            Action<Action> host = ContractReEvaluationDeferForTesting
                ?? (WarpToTimeConsumer.Instance != null ? (Action<Action>)WarpToTimeConsumer.RunNextFrame : null);
            if (host == null)
            {
                ParsekLog.Info(Tag,
                    $"PatchContracts: no frame host to re-evaluate {reEvaluate.Count.ToString(IC)} contract(s) " +
                    "with every required parameter Complete - the next patch retries");
                return;
            }
            host(() => RunContractReEvaluation(reEvaluate, terminal));
        }

        /// <summary>What stock's own completion checks read from one live contract.</summary>
        internal struct ContractCompletionShape
        {
            internal int TopLevelCount;
            internal int TopLevelComplete;
            /// <summary>Not-Complete parameters with children, every child Complete, deepest first.</summary>
            internal List<ContractParameter> SatisfiedOpenGroups;
        }

        private static ContractCompletionShape ReadContractCompletionShape(Contract contract)
        {
            var shape = new ContractCompletionShape { SatisfiedOpenGroups = new List<ContractParameter>() };
            if (contract == null)
                return shape;
            shape.TopLevelCount = contract.ParameterCount;
            for (int i = 0; i < contract.ParameterCount; i++)
            {
                ContractParameter p = contract.GetParameter(i);
                if (p != null && p.State == ParameterState.Complete)
                    shape.TopLevelComplete++;
            }
            var depthOf = new Dictionary<ContractParameter, int>();
            foreach (ContractParameter p in contract.AllParameters)
            {
                if (p == null || p.State == ParameterState.Complete || p.ParameterCount == 0)
                    continue;
                if (!p.AllChildParametersComplete())
                    continue;
                int depth = 0;
                for (IContractParameterHost h = p.Parent; h is ContractParameter && depth < 32; h = h.Parent)
                    depth++;
                depthOf[p] = depth;
                shape.SatisfiedOpenGroups.Add(p);
            }
            shape.SatisfiedOpenGroups.Sort((a, b) => depthOf[b].CompareTo(depthOf[a]));
            return shape;
        }

        /// <summary>
        /// Pure: whether stock must re-evaluate a contract after a silent parameter
        /// completion. Stock's <c>Contract.OnParameterStateChange</c> (decompiled KSP 1.12.5)
        /// completes the contract when a parameter completes and every top-level parameter is
        /// Complete; a group parameter completes from its own <c>OnParameterStateChange</c>
        /// override when its children change. Neither runs for a silent write. A contract the
        /// ledger already ended is never re-evaluated: the patch removed it from stock's
        /// Active list, and re-evaluating it would pay its reward a second time.
        /// </summary>
        /// <summary>
        /// Pure: the ids of contracts a ContractComplete row in <paramref name="actions"/>
        /// completes strictly after <paramref name="clockUT"/>.
        /// </summary>
        internal static HashSet<string> CollectContractsCompletedAfter(
            IEnumerable<GameAction> actions, double clockUT)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (actions == null)
                return ids;
            foreach (var a in actions)
            {
                if (a == null || a.Type != GameActionType.ContractComplete || string.IsNullOrEmpty(a.ContractId))
                    continue;
                if (a.UT > clockUT)
                    ids.Add(a.ContractId);
            }
            return ids;
        }

        internal static bool NeedsStockContractReEvaluation(
            bool contractActiveInStock, bool ledgerTerminal,
            int topLevelCount, int topLevelComplete, int satisfiedOpenGroups)
        {
            if (!contractActiveInStock || ledgerTerminal)
                return false;
            return (topLevelCount > 0 && topLevelComplete >= topLevelCount) || satisfiedOpenGroups > 0;
        }

        /// <summary>
        /// Hands each listed contract back to stock's own completion path: a satisfied group
        /// parameter's <c>ParameterStateUpdate</c> (deepest first, so a completed group can
        /// satisfy its parent), then the contract's <c>ParameterStateUpdate</c> - the calls
        /// <c>ContractParameter.SetComplete</c> makes after paying. Runs in the recorder's
        /// clock-cause scope, so what stock pays here (a group's reward, the contract's
        /// reward, its ContractCompleted) is filed untagged, not to the live flight. Skips a
        /// contract no longer Active or ended in the ledger, and everything while a ledger
        /// replay runs (the next patch retries: the decision reads stock's state).
        /// </summary>
        private static void RunContractReEvaluation(List<string> contractIds, HashSet<string> ledgerTerminal)
        {
            if (ContractSystem.Instance == null || ContractSystem.Instance.Contracts == null)
            {
                ParsekLog.Verbose(Tag, "PatchContracts: contract re-evaluation dropped - no ContractSystem");
                return;
            }
            if (GameStateRecorder.IsReplayingActions)
            {
                ParsekLog.Info(Tag,
                    "PatchContracts: contract re-evaluation skipped - a ledger replay is running; the next patch retries");
                return;
            }
            if (ParsekScenario.IsOnLoadInProgress || !ContractSystem.loaded)
            {
                ParsekLog.Info(Tag,
                    "PatchContracts: contract re-evaluation skipped - a load is in progress " +
                    $"(onLoad={ParsekScenario.IsOnLoadInProgress.ToString(IC)}, stockLoaded={ContractSystem.loaded.ToString(IC)}); " +
                    "the next patch retries");
                return;
            }

            const string Scope = "contract re-evaluation after a silent parameter completion";
            int reEvaluated = 0;
            int completedByStock = 0;
            int skipped = 0;
            GameStateRecorder.EnterClockCauseScope(Scope);
            try
            {
                for (int i = 0; i < contractIds.Count; i++)
                {
                    string id = contractIds[i];
                    Contract contract = null;
                    var live = ContractSystem.Instance.Contracts;
                    for (int k = 0; k < live.Count; k++)
                    {
                        if (live[k] != null && live[k].ContractState == Contract.State.Active
                            && live[k].ContractGuid.ToString() == id)
                        {
                            contract = live[k];
                            break;
                        }
                    }
                    ContractCompletionShape shape = ReadContractCompletionShape(contract);
                    if (!NeedsStockContractReEvaluation(contract != null,
                            ledgerTerminal != null && ledgerTerminal.Contains(id),
                            shape.TopLevelCount, shape.TopLevelComplete, shape.SatisfiedOpenGroups.Count))
                    {
                        skipped++;
                        continue;
                    }

                    reEvaluated++;
                    int groupsCompleted = 0;
                    var tried = new HashSet<ContractParameter>();
                    for (int pass = 0; pass < 8 && contract.ContractState == Contract.State.Active; pass++)
                    {
                        bool progressed = false;
                        List<ContractParameter> groups = ReadContractCompletionShape(contract).SatisfiedOpenGroups;
                        for (int g = 0; g < groups.Count; g++)
                        {
                            ContractParameter group = groups[g];
                            if (!tried.Add(group))
                                continue;
                            group.ParameterStateUpdate(group.GetParameter(group.ParameterCount - 1));
                            if (group.State == ParameterState.Complete)
                            {
                                groupsCompleted++;
                                progressed = true;
                            }
                        }
                        if (!progressed)
                            break;
                    }

                    if (contract.ContractState == Contract.State.Active)
                    {
                        shape = ReadContractCompletionShape(contract);
                        if (shape.TopLevelCount > 0 && shape.TopLevelComplete >= shape.TopLevelCount)
                            contract.ParameterStateUpdate(contract.GetParameter(shape.TopLevelCount - 1));
                    }

                    bool completedNow = contract.ContractState != Contract.State.Active;
                    if (completedNow)
                        completedByStock++;
                    ParsekLog.Info(Tag,
                        $"PatchContracts: re-evaluated contract {id} '{contract.Title ?? ""}' through stock: " +
                        $"groupsCompleted={groupsCompleted.ToString(IC)} state={contract.ContractState}");
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"PatchContracts: contract re-evaluation threw {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                GameStateRecorder.ExitClockCauseScope(Scope);
            }

            ParsekLog.Info(Tag,
                $"PatchContracts: contract re-evaluation reEvaluated={reEvaluated.ToString(IC)}, " +
                $"completedByStock={completedByStock.ToString(IC)}, skipped={skipped.ToString(IC)}");
        }
    }
}
