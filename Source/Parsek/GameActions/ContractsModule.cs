using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    internal enum ContractTerminalOutcome
    {
        Completed = 0,
        Failed = 1,
        DeadlineExpired = 2,
        Cancelled = 3
    }

    /// <summary>
    /// First-tier resource module: tracks contract state machine transitions,
    /// slot reservation (all accepted contracts reserved from UT=0), and
    /// once-ever completion semantics.
    ///
    /// Pure computation — no KSP state access.
    ///
    /// Design doc: section 8 (Contracts Module).
    /// </summary>
    internal class ContractsModule : IResourceModule, IProjectionCloneableModule
    {
        private const string Tag = "Contracts";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// Active (accepted, unresolved) contracts. Key = contractId, Value = accept action.
        /// A contract is active from accept until resolution (complete/fail/cancel).
        /// All active contracts are reserved from UT=0 (slot consumed immediately).
        /// </summary>
        private readonly Dictionary<string, GameAction> activeContracts
            = new Dictionary<string, GameAction>();

        /// <summary>
        /// Contract IDs that have been effectively completed (once-ever).
        /// A second completion of the same contract sets Effective=false on the action.
        /// </summary>
        private readonly HashSet<string> creditedContracts = new HashSet<string>();

        /// <summary>
        /// Contract IDs that resolved to a terminal state during this walk.
        /// Used by the KSP patcher to distinguish terminal contracts that should
        /// remain in stock history from terminal contracts whose ledger row was
        /// removed by a tombstone.
        /// </summary>
        private readonly HashSet<string> terminalContracts = new HashSet<string>();

        /// <summary>
        /// Terminal action type for each contract id that resolved during this walk.
        /// This lets KSP patching distinguish a surviving retry failure from a
        /// tombstoned old-branch completion with the same stock contract guid.
        /// </summary>
        private readonly Dictionary<string, GameActionType> terminalContractActions
            = new Dictionary<string, GameActionType>();

        private readonly Dictionary<string, ContractTerminalOutcome> terminalContractOutcomes
            = new Dictionary<string, ContractTerminalOutcome>();

        /// <summary>
        /// Contract IDs whose accepted deadline expired during the current walk.
        /// Late completions for these contracts are ineffective and do not earn rewards.
        /// </summary>
        private readonly HashSet<string> deadlineExpiredContracts = new HashSet<string>();

        /// <summary>
        /// Contract IDs that reached an explicit terminal state (fail/cancel) during
        /// the current walk. Later stale completions for the same id are ineffective.
        /// </summary>
        private readonly HashSet<string> explicitlyResolvedContracts = new HashSet<string>();

        /// <summary>
        /// Earliest explicit fail/cancel UT seen by PrePass for the currently
        /// accepted lifecycle epoch. This protects replay from same-UT sort
        /// ordering where ContractComplete is processed before fail/cancel.
        /// </summary>
        private readonly Dictionary<string, double> prepassExplicitResolutionUT
            = new Dictionary<string, double>();

        /// <summary>
        /// Mission Control slot limit. Determined by building level.
        /// Default 2 for level 1 (KSP stock).
        /// </summary>
        private int maxSlots = 2;

        /// <summary>
        /// Returns true only when an action UT is strictly before the accepted
        /// contract deadline. The deadline boundary is non-inclusive: a
        /// ContractComplete with UT == DeadlineUT is late and must not earn
        /// completion rewards.
        /// </summary>
        internal static bool IsBeforeContractDeadline(double actionUT, float deadlineUT)
        {
            return !float.IsNaN(deadlineUT) && actionUT < deadlineUT;
        }

        /// <summary>
        /// Returns true once the current UT reaches or passes a real deadline.
        /// NaN deadlines are open-ended and never expire.
        /// </summary>
        internal static bool HasContractDeadlineElapsed(double currentUT, float deadlineUT)
        {
            return !float.IsNaN(deadlineUT) && !IsBeforeContractDeadline(currentUT, deadlineUT);
        }

        // ================================================================
        // IResourceModule
        // ================================================================

        /// <inheritdoc/>
        public void Reset()
        {
            int prevActive = activeContracts.Count;
            int prevCredited = creditedContracts.Count;
            int prevTerminal = terminalContracts.Count;
            int prevTerminalActions = terminalContractActions.Count;
            int prevTerminalOutcomes = terminalContractOutcomes.Count;
            int prevExpired = deadlineExpiredContracts.Count;
            int prevResolved = explicitlyResolvedContracts.Count;
            int prevPrepassResolved = prepassExplicitResolutionUT.Count;

            activeContracts.Clear();
            creditedContracts.Clear();
            terminalContracts.Clear();
            terminalContractActions.Clear();
            terminalContractOutcomes.Clear();
            deadlineExpiredContracts.Clear();
            explicitlyResolvedContracts.Clear();
            prepassExplicitResolutionUT.Clear();

            ParsekLog.Verbose(Tag,
                $"Reset: cleared {prevActive} active contracts, {prevCredited} credited contracts, " +
                $"{prevTerminal} terminal contracts, {prevTerminalActions} terminal actions, " +
                $"{prevTerminalOutcomes} terminal outcomes, " +
                $"{prevExpired} deadline-expired contracts, {prevResolved} explicitly resolved contracts, " +
                $"{prevPrepassResolved} prepass explicit resolutions");
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Scans for ContractAccept actions with deadlines that expire before the
        /// walk's effective "now" without an on-time completion or explicit fail/cancel
        /// resolution. Injects synthetic ContractFail actions at the deadline UT so
        /// the recalculation walk applies failure penalties (funds + reputation).
        ///
        /// The "now" threshold is <paramref name="walkNowUT"/> when supplied — e.g. the
        /// rewind cutoff UT, so deadlines that expired between the last pre-cutoff
        /// action and the cutoff itself still produce a synthetic fail. When no cutoff
        /// is in play, "now" falls back to the last surviving action's UT (the original
        /// heuristic — correct for non-rewind walks where the end of the ledger is
        /// effectively the current time).
        /// </remarks>
        public void PrePass(List<GameAction> actions, double? walkNowUT = null)
        {
            if (actions == null || actions.Count == 0)
                return;

            // Track accepted contracts with deadlines: contractId -> accept action
            var tracked = new Dictionary<string, GameAction>();
            var activeAcceptUT = new Dictionary<string, double>();
            prepassExplicitResolutionUT.Clear();

            // Walk all actions to find unresolved contracts with deadlines
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                string id = action.ContractId;
                switch (action.Type)
                {
                    case GameActionType.ContractAccept:
                        if (id != null)
                        {
                            activeAcceptUT[id] = action.UT;
                            prepassExplicitResolutionUT.Remove(id);
                            if (!float.IsNaN(action.DeadlineUT))
                                tracked[id] = action;
                        }
                        break;

                    case GameActionType.ContractComplete:
                        if (id != null)
                        {
                            GameAction accept;
                            if (tracked.TryGetValue(id, out accept)
                                && IsBeforeContractDeadline(action.UT, accept.DeadlineUT))
                            {
                                tracked.Remove(id);
                            }
                        }
                        break;

                    case GameActionType.ContractFail:
                    case GameActionType.ContractCancel:
                        // Explicit resolution already applies its own penalty; after
                        // deadline it must not receive an additional synthetic fail.
                        if (id != null)
                        {
                            double acceptUT;
                            if (activeAcceptUT.TryGetValue(id, out acceptUT)
                                && action.UT >= acceptUT)
                            {
                                double existing;
                                if (!prepassExplicitResolutionUT.TryGetValue(id, out existing)
                                    || action.UT < existing)
                                {
                                    prepassExplicitResolutionUT[id] = action.UT;
                                }
                            }
                            tracked.Remove(id);
                        }
                        break;
                }
            }

            // Effective "now" for deadline comparisons:
            //   - Rewind path: walkNowUT is the cutoff — actions after the cutoff are
            //     filtered out by the engine before this runs, but we still need to
            //     synthesize fails for deadlines that fell between the last pre-cutoff
            //     action and the cutoff itself.
            //   - Non-rewind path: walkNowUT is null; use the last surviving action's
            //     UT (the original behavior).
            double lastActionUT = actions[actions.Count - 1].UT;
            double nowUT = walkNowUT ?? lastActionUT;
            string nowSource = walkNowUT.HasValue ? "cutoff" : "lastActionUT";
            int injected = 0;

            foreach (var kvp in tracked)
            {
                var accept = kvp.Value;
                if (HasContractDeadlineElapsed(nowUT, accept.DeadlineUT))
                {
                    var syntheticFail = new GameAction
                    {
                        Type = GameActionType.ContractFail,
                        UT = accept.DeadlineUT,
                        ContractId = accept.ContractId,
                        FundsPenalty = accept.FundsPenalty,
                        RepPenalty = accept.RepPenalty,
                        RecordingId = accept.RecordingId
                    };
                    actions.Add(syntheticFail);
                    injected++;

                    ParsekLog.Info(Tag,
                        $"PrePass: injected synthetic ContractFail for contractId='{accept.ContractId}' " +
                        $"at deadlineUT={accept.DeadlineUT.ToString("R", IC)} fundsPenalty={accept.FundsPenalty.ToString("R", IC)} " +
                        $"repPenalty={accept.RepPenalty.ToString("R", IC)} (nowUT={nowUT.ToString("R", IC)} source={nowSource})");
                }
            }

            if (injected > 0)
            {
                ParsekLog.Info(Tag,
                    $"PrePass: injected {injected.ToString(IC)} synthetic ContractFail action(s) for expired deadlines " +
                    $"(nowUT={nowUT.ToString("R", IC)} source={nowSource})");
            }
            else
            {
                ParsekLog.Verbose(Tag,
                    $"PrePass: no expired deadline contracts found " +
                    $"(tracked={tracked.Count}, nowUT={nowUT}, source={nowSource})");
            }
        }

        /// <inheritdoc/>
        public void ProcessAction(GameAction action)
        {
            CheckDeadlines(action.UT);

            switch (action.Type)
            {
                case GameActionType.ContractAccept:
                    ProcessAccept(action);
                    break;
                case GameActionType.ContractComplete:
                    ProcessComplete(action);
                    break;
                case GameActionType.ContractFail:
                    ProcessFail(action);
                    break;
                case GameActionType.ContractCancel:
                    ProcessCancel(action);
                    break;
                // All other action types: ignore silently
            }
        }

        // ================================================================
        // Contract state transitions
        // ================================================================

        private void ProcessAccept(GameAction action)
        {
            string id = action.ContractId ?? "";

            if (activeContracts.ContainsKey(id))
            {
                ParsekLog.Warn(Tag,
                    $"Accept: contractId='{id}' already active, overwriting previous accept");
            }

            activeContracts[id] = action;
            terminalContracts.Remove(id);
            terminalContractActions.Remove(id);
            terminalContractOutcomes.Remove(id);
            deadlineExpiredContracts.Remove(id);
            explicitlyResolvedContracts.Remove(id);

            // Advance funds flow to Funds module via action.AdvanceFunds —
            // the Funds module reads that field from the action directly.

            ParsekLog.Info(Tag,
                $"Accept: contractId='{id}' type='{action.ContractType}' " +
                $"title='{action.ContractTitle}' advance={action.AdvanceFunds} " +
                $"activeSlots={activeContracts.Count}/{maxSlots}");
        }

        private void ProcessComplete(GameAction action)
        {
            string id = action.ContractId ?? "";

            if (creditedContracts.Contains(id))
            {
                // Duplicate completion — rewards zeroed
                action.Effective = false;

                ParsekLog.Info(Tag,
                    $"Complete: contractId='{id}' effective=false (already credited), " +
                    $"rewards zeroed");
            }
            else if (deadlineExpiredContracts.Contains(id))
            {
                // Deadline failure already resolved this contract; rewards zeroed.
                action.Effective = false;

                ParsekLog.Info(Tag,
                    $"Complete: contractId='{id}' effective=false (deadline expired), " +
                    $"rewards zeroed");
            }
            else if (explicitlyResolvedContracts.Contains(id))
            {
                // Explicit fail/cancel already resolved this contract; rewards zeroed.
                action.Effective = false;

                ParsekLog.Info(Tag,
                    $"Complete: contractId='{id}' effective=false (explicitly resolved), " +
                    $"rewards zeroed");
            }
            else if (HasPrepassExplicitResolutionAtOrBefore(id, action.UT))
            {
                // SortActions processes ContractComplete before ContractFail/Cancel
                // at the same UT. PrePass sees the whole walk and makes that
                // same-tick explicit terminal state authoritative.
                action.Effective = false;

                ParsekLog.Info(Tag,
                    $"Complete: contractId='{id}' effective=false " +
                    $"(explicitly resolved at same/prior UT), rewards zeroed");
            }
            else
            {
                // First completion — credit awarded
                action.Effective = true;
                creditedContracts.Add(id);
                terminalContracts.Add(id);
                terminalContractActions[id] = GameActionType.ContractComplete;
                terminalContractOutcomes[id] = ContractTerminalOutcome.Completed;

                ParsekLog.Info(Tag,
                    $"Complete: contractId='{id}' effective=true, " +
                    $"fundsReward={action.FundsReward} repReward={action.RepReward} " +
                    $"scienceReward={action.ScienceReward}");
            }

            // Slot freed regardless of effective status
            bool wasActive = activeContracts.Remove(id);

            ParsekLog.Verbose(Tag,
                $"Complete: slot freed for contractId='{id}', " +
                $"wasActive={wasActive}, " +
                $"activeSlots={activeContracts.Count}/{maxSlots}");
        }

        /// <summary>
        /// Checks all active contracts for deadline expiration at the given UT.
        /// Any contract whose DeadlineUT &lt;= currentUT (and is not NaN) is removed
        /// from activeContracts (slot freed). Returns the list of expired contract IDs.
        /// </summary>
        internal List<string> CheckDeadlines(double currentUT)
        {
            List<string> expired = null;

            foreach (var kvp in activeContracts)
            {
                float deadline = kvp.Value.DeadlineUT;
                if (HasContractDeadlineElapsed(currentUT, deadline))
                {
                    if (expired == null)
                        expired = new List<string>();
                    expired.Add(kvp.Key);
                }
            }

            if (expired != null)
            {
                for (int i = 0; i < expired.Count; i++)
                {
                    string id = expired[i];
                    activeContracts.Remove(id);
                    terminalContracts.Add(id);
                    terminalContractActions[id] = GameActionType.ContractFail;
                    deadlineExpiredContracts.Add(id);
                    terminalContractOutcomes[id] = ContractTerminalOutcome.DeadlineExpired;

                    ParsekLog.Info(Tag,
                        $"DeadlineExpired: contractId='{id}' deadline passed at currentUT={currentUT}, " +
                        $"slot freed, activeSlots={activeContracts.Count}/{maxSlots}");
                }
            }

            return expired ?? new List<string>();
        }

        private void ProcessFail(GameAction action)
        {
            string id = action.ContractId ?? "";

            // Penalties apply unconditionally
            bool wasActive = activeContracts.Remove(id);
            terminalContracts.Add(id);
            terminalContractActions[id] = GameActionType.ContractFail;
            terminalContractOutcomes[id] = deadlineExpiredContracts.Contains(id)
                ? ContractTerminalOutcome.DeadlineExpired
                : ContractTerminalOutcome.Failed;
            explicitlyResolvedContracts.Add(id);

            ParsekLog.Info(Tag,
                $"Fail: contractId='{id}' fundsPenalty={action.FundsPenalty} " +
                $"repPenalty={action.RepPenalty} " +
                $"wasActive={wasActive} " +
                $"activeSlots={activeContracts.Count}/{maxSlots}");
        }

        private bool HasPrepassExplicitResolutionAtOrBefore(string contractId, double completeUT)
        {
            if (string.IsNullOrEmpty(contractId))
                return false;

            double resolutionUT;
            return prepassExplicitResolutionUT.TryGetValue(contractId, out resolutionUT)
                && resolutionUT <= completeUT;
        }

        private void ProcessCancel(GameAction action)
        {
            string id = action.ContractId ?? "";

            // Penalties apply unconditionally
            bool wasActive = activeContracts.Remove(id);
            terminalContracts.Add(id);
            terminalContractActions[id] = GameActionType.ContractCancel;
            terminalContractOutcomes[id] = ContractTerminalOutcome.Cancelled;
            explicitlyResolvedContracts.Add(id);

            ParsekLog.Info(Tag,
                $"Cancel: contractId='{id}' fundsPenalty={action.FundsPenalty} " +
                $"repPenalty={action.RepPenalty} " +
                $"wasActive={wasActive} " +
                $"activeSlots={activeContracts.Count}/{maxSlots}");
        }

        // ================================================================
        // Queries
        // ================================================================

        /// <summary>
        /// Returns the number of currently active (reserved, unresolved) contracts.
        /// At UT=0 reservation: all accepted and unresolved contracts count.
        /// </summary>
        internal int GetActiveContractCount()
        {
            return activeContracts.Count;
        }

        /// <summary>
        /// Returns available Mission Control slots (maxSlots - active contracts).
        /// Can be negative if over-subscribed (e.g., facility downgrade).
        /// </summary>
        internal int GetAvailableSlots()
        {
            return maxSlots - activeContracts.Count;
        }

        /// <summary>
        /// Returns true if the given contract has been effectively completed
        /// (once-ever semantics — first completion on the timeline).
        /// </summary>
        internal bool IsContractCredited(string contractId)
        {
            return creditedContracts.Contains(contractId ?? "");
        }

        /// <summary>
        /// Returns the set of contract IDs currently active (accepted but not yet
        /// resolved). Used by KspStatePatcher to determine which contracts to restore.
        /// </summary>
        internal IReadOnlyCollection<string> GetActiveContractIds()
        {
            return activeContracts.Keys;
        }

        /// <summary>
        /// Returns the set of contract IDs that resolved to terminal state during
        /// the current walk. Used by KSP state patching to remove only terminal
        /// stock contracts that no longer have terminal ledger support.
        /// </summary>
        internal IReadOnlyCollection<string> GetTerminalContractIds()
        {
            return terminalContracts;
        }

        /// <summary>
        /// Returns terminal action types keyed by contract id for contracts resolved
        /// during the current walk. Used by the KSP patcher to avoid preserving a
        /// tombstoned terminal row just because a retry produced a different
        /// surviving terminal outcome for the same contract id.
        /// </summary>
        internal IReadOnlyDictionary<string, GameActionType> GetTerminalContractActionTypes()
        {
            return terminalContractActions;
        }

        /// <summary>
        /// Returns terminal outcomes keyed by contract id for contracts resolved
        /// during the current walk. Unlike the action type map, this distinguishes
        /// explicit failures from deadline-expired failures, including preserving
        /// a prior deadline-expired classification when a later fail action for
        /// the same contract is processed in the same walk.
        /// </summary>
        internal IReadOnlyDictionary<string, ContractTerminalOutcome> GetTerminalContractOutcomes()
        {
            return terminalContractOutcomes;
        }

        /// <summary>
        /// Sets the max slot count. Used for testing and facility-level changes.
        /// </summary>
        internal void SetMaxSlots(int max)
        {
            maxSlots = max;
            ParsekLog.Verbose(Tag, $"SetMaxSlots: maxSlots={maxSlots}");
        }

        public IResourceModule CreateProjectionClone()
        {
            var clone = new ContractsModule();
            clone.maxSlots = maxSlots;
            return clone;
        }

        public void PostWalk() { }
    }
}
