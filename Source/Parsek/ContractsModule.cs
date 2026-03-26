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
    internal class ContractsModule : IResourceModule
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
        public void ProcessAction(GameAction action)
        {
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
            activeContracts.Remove(id);

            ParsekLog.Verbose(Tag,
                $"Complete: slot freed for contractId='{id}', " +
                $"activeSlots={activeContracts.Count}/{maxSlots}");
        }

        // TODO: deadline failure generation — deferred to future task

        private void ProcessFail(GameAction action)
        {
            string id = action.ContractId ?? "";

            // Penalties apply unconditionally
            activeContracts.Remove(id);

            ParsekLog.Info(Tag,
                $"Fail: contractId='{id}' fundsPenalty={action.FundsPenalty} " +
                $"repPenalty={action.RepPenalty} " +
                $"activeSlots={activeContracts.Count}/{maxSlots}");
        }

        private void ProcessCancel(GameAction action)
        {
            string id = action.ContractId ?? "";

            // Penalties apply unconditionally
            activeContracts.Remove(id);

            ParsekLog.Info(Tag,
                $"Cancel: contractId='{id}' fundsPenalty={action.FundsPenalty} " +
                $"repPenalty={action.RepPenalty} " +
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
        /// Sets the max slot count. Used for testing and facility-level changes.
        /// </summary>
        internal void SetMaxSlots(int max)
        {
            maxSlots = max;
            ParsekLog.Verbose(Tag, $"SetMaxSlots: maxSlots={maxSlots}");
        }
    }
}
