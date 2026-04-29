using System.Collections.Generic;

namespace Parsek
{
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
        /// Mission Control slot limit. Determined by building level.
        /// Default 2 for level 1 (KSP stock).
        /// </summary>
        private int maxSlots = 2;

        // ================================================================
        // IResourceModule
        // ================================================================

        /// <inheritdoc/>
        public void Reset()
        {
            int prevActive = activeContracts.Count;
            int prevCredited = creditedContracts.Count;

            activeContracts.Clear();
            creditedContracts.Clear();

            ParsekLog.Verbose(Tag,
                $"Reset: cleared {prevActive} active contracts, {prevCredited} credited contracts");
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Scans for ContractAccept actions with deadlines that expire before the
        /// walk's effective "now" without a resolution (Complete/Fail/Cancel). Injects
        /// synthetic ContractFail actions at the deadline UT so the recalculation walk
        /// applies failure penalties (funds + reputation).
        ///
        /// The "now" threshold is <paramref name="walkNowUT"/> when supplied — e.g. the
        /// rewind cutoff UT, so deadlines that expired between the last pre-cutoff
        /// action and the cutoff itself still produce a synthetic fail. When no cutoff
        /// is in play, "now" falls back to the last surviving action's UT (the original
        /// heuristic — correct for non-rewind walks where the end of the ledger is
        /// effectively the current time).
        /// </remarks>
        public bool PrePass(List<GameAction> actions, double? walkNowUT = null)
        {
            if (actions == null || actions.Count == 0)
                return false;

            // Track accepted contracts with deadlines: contractId -> accept action
            var tracked = new Dictionary<string, GameAction>();

            // Walk all actions to find unresolved contracts with deadlines
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                switch (action.Type)
                {
                    case GameActionType.ContractAccept:
                        if (!float.IsNaN(action.DeadlineUT) && action.ContractId != null)
                            tracked[action.ContractId] = action;
                        break;

                    case GameActionType.ContractComplete:
                    case GameActionType.ContractFail:
                    case GameActionType.ContractCancel:
                        if (action.ContractId != null)
                            tracked.Remove(action.ContractId);
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
                if (accept.DeadlineUT <= nowUT)
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
                        $"at deadlineUT={accept.DeadlineUT} fundsPenalty={accept.FundsPenalty} " +
                        $"repPenalty={accept.RepPenalty} (nowUT={nowUT} source={nowSource})");
                }
            }

            if (injected > 0)
            {
                ParsekLog.Info(Tag,
                    $"PrePass: injected {injected} synthetic ContractFail action(s) for expired deadlines " +
                    $"(nowUT={nowUT} source={nowSource})");
            }
            else
            {
                ParsekLog.Verbose(Tag,
                    $"PrePass: no expired deadline contracts found " +
                    $"(tracked={tracked.Count}, nowUT={nowUT}, source={nowSource})");
            }

            return injected > 0;
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
            else
            {
                // First completion — credit awarded
                action.Effective = true;
                creditedContracts.Add(id);

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
                if (!float.IsNaN(deadline) && deadline <= currentUT)
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

            ParsekLog.Info(Tag,
                $"Fail: contractId='{id}' fundsPenalty={action.FundsPenalty} " +
                $"repPenalty={action.RepPenalty} " +
                $"wasActive={wasActive} " +
                $"activeSlots={activeContracts.Count}/{maxSlots}");
        }

        private void ProcessCancel(GameAction action)
        {
            string id = action.ContractId ?? "";

            // Penalties apply unconditionally
            bool wasActive = activeContracts.Remove(id);

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
