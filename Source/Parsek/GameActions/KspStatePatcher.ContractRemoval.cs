using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Contracts;

namespace Parsek
{
    // Contract partition, removal keys and the silent retire / archive of finished contracts.
    internal static partial class KspStatePatcher
    {
        // ================================================================
        // Testable pure helpers for #404 filtered contract partitioning
        // ================================================================

        /// <summary>
        /// Represents the current-state classification of a KSP contract for PatchContracts
        /// filtering. Active contracts are matched against ledger Active ids; terminal
        /// contracts are matched against ledger terminal ids; offered/declined/unknown
        /// non-terminal state is preserved untouched.
        /// </summary>
        internal struct ContractFilterEntry
        {
            public Guid Id;
            public bool IsActive;
            public bool IsTerminal;
            public Contract.State State;
        }

        internal struct ContractRemovalKey
        {
            public Guid Id;
            public Contract.State State;
        }

        /// <summary>
        /// Pure helper: partitions the current KSP contract list into {stale Active
        /// contracts plus explicitly tombstoned terminal contracts to remove} and
        /// {surviving ledger-backed contracts to keep in place}.
        /// Non-terminal non-Active entries are implicitly preserved by being absent from the
        /// remove list. Extracted from <see cref="PatchContracts"/> so the filtering logic can
        /// be unit-tested without real KSP Contract instances.
        ///
        /// The rules:
        /// <list type="number">
        ///   <item>An Active contract whose ID is NOT in the ledger's active set is "stale" and goes to <paramref name="toRemove"/>.</item>
        ///   <item>An Active contract whose ID IS in the ledger's active set is "surviving" and goes to <paramref name="surviving"/>.</item>
        ///   <item>A terminal contract whose ID IS in the ledger's terminal set is "surviving".</item>
        ///   <item>A terminal contract whose ID is NOT in the ledger's terminal set is removed only when the caller supplies it in the removable tombstoned set.</item>
        ///   <item>A non-terminal non-Active contract is left out of both lists — callers MUST NOT touch it.</item>
        /// </list>
        /// </summary>
        internal static void PartitionContractsForPatch(
            IReadOnlyList<ContractFilterEntry> currentEntries,
            HashSet<Guid> activeIdSet,
            out List<Guid> toRemove,
            out HashSet<Guid> surviving)
        {
            PartitionContractsForPatch(
                currentEntries,
                activeIdSet,
                new HashSet<Guid>(),
                null,
                null,
                out toRemove,
                out surviving,
                out _);
        }

        internal static void PartitionContractsForPatch(
            IReadOnlyList<ContractFilterEntry> currentEntries,
            HashSet<Guid> activeIdSet,
            HashSet<Guid> terminalIdSet,
            out List<Guid> toRemove,
            out HashSet<Guid> survivingActive,
            out HashSet<Guid> survivingTerminal)
        {
            PartitionContractsForPatch(currentEntries, activeIdSet, terminalIdSet,
                null, null,
                out toRemove, out survivingActive, out survivingTerminal);
        }

        internal static void PartitionContractsForPatch(
            IReadOnlyList<ContractFilterEntry> currentEntries,
            HashSet<Guid> activeIdSet,
            HashSet<Guid> terminalIdSet,
            HashSet<Guid> removableTerminalIdSet,
            out List<Guid> toRemove,
            out HashSet<Guid> survivingActive,
            out HashSet<Guid> survivingTerminal)
        {
            PartitionContractsForPatch(currentEntries, activeIdSet, terminalIdSet,
                null, removableTerminalIdSet,
                out toRemove, out survivingActive, out survivingTerminal);
        }

        internal static void PartitionContractsForPatch(
            IReadOnlyList<ContractFilterEntry> currentEntries,
            HashSet<Guid> activeIdSet,
            HashSet<Guid> terminalIdSet,
            IReadOnlyDictionary<Guid, ContractTerminalOutcome> terminalOutcomeById,
            HashSet<Guid> removableTerminalIdSet,
            out List<Guid> toRemove,
            out HashSet<Guid> survivingActive,
            out HashSet<Guid> survivingTerminal)
        {
            toRemove = new List<Guid>();
            survivingActive = new HashSet<Guid>();
            survivingTerminal = new HashSet<Guid>();
            if (currentEntries == null) return;

            for (int i = 0; i < currentEntries.Count; i++)
            {
                var entry = currentEntries[i];
                if (entry.IsActive)
                {
                    if (activeIdSet != null && activeIdSet.Contains(entry.Id))
                        survivingActive.Add(entry.Id);
                    else
                        toRemove.Add(entry.Id);
                    continue;
                }

                if (entry.IsTerminal)
                {
                    if (IsSurvivingTerminalEntry(
                            entry, terminalIdSet, terminalOutcomeById))
                    {
                        survivingTerminal.Add(entry.Id);
                    }
                    else if (removableTerminalIdSet != null &&
                             removableTerminalIdSet.Contains(entry.Id))
                    {
                        toRemove.Add(entry.Id);
                    }
                    continue;
                }

                // Offered/declined/unknown non-terminal stock state is preserved.
            }
        }

        internal static void PartitionContractsForPatch(
            IReadOnlyList<ContractFilterEntry> currentEntries,
            IReadOnlyList<ContractFilterEntry> finishedEntries,
            HashSet<Guid> activeIdSet,
            HashSet<Guid> terminalIdSet,
            HashSet<Guid> removableFinishedTerminalIdSet,
            out List<Guid> toRemoveCurrent,
            out List<Guid> toRemoveFinished,
            out HashSet<Guid> survivingActive,
            out HashSet<Guid> survivingTerminal)
        {
            PartitionContractsForPatch(currentEntries, finishedEntries,
                activeIdSet, terminalIdSet, null, removableFinishedTerminalIdSet,
                out toRemoveCurrent, out toRemoveFinished,
                out survivingActive, out survivingTerminal);
        }

        internal static void PartitionContractsForPatch(
            IReadOnlyList<ContractFilterEntry> currentEntries,
            IReadOnlyList<ContractFilterEntry> finishedEntries,
            HashSet<Guid> activeIdSet,
            HashSet<Guid> terminalIdSet,
            IReadOnlyDictionary<Guid, ContractTerminalOutcome> terminalOutcomeById,
            HashSet<Guid> removableFinishedTerminalIdSet,
            out List<Guid> toRemoveCurrent,
            out List<Guid> toRemoveFinished,
            out HashSet<Guid> survivingActive,
            out HashSet<Guid> survivingTerminal)
        {
            PartitionContractsForPatch(currentEntries, activeIdSet, terminalIdSet,
                terminalOutcomeById, removableFinishedTerminalIdSet,
                out toRemoveCurrent, out survivingActive, out survivingTerminal);

            PartitionFinishedContractsForPatch(finishedEntries, terminalIdSet,
                terminalOutcomeById, removableFinishedTerminalIdSet,
                out toRemoveFinished, out var survivingFinishedTerminal);

            foreach (var id in survivingFinishedTerminal)
                survivingTerminal.Add(id);
        }

        internal static void PartitionFinishedContractsForPatch(
            IReadOnlyList<ContractFilterEntry> finishedEntries,
            HashSet<Guid> terminalIdSet,
            IReadOnlyDictionary<Guid, ContractTerminalOutcome> terminalOutcomeById,
            HashSet<Guid> removableFinishedTerminalIdSet,
            out List<Guid> toRemoveFinished,
            out HashSet<Guid> survivingTerminal)
        {
            toRemoveFinished = new List<Guid>();
            survivingTerminal = new HashSet<Guid>();
            if (finishedEntries == null) return;

            for (int i = 0; i < finishedEntries.Count; i++)
            {
                var entry = finishedEntries[i];
                if (!entry.IsTerminal)
                    continue;

                if (IsSurvivingTerminalEntry(
                        entry, terminalIdSet, terminalOutcomeById))
                {
                    survivingTerminal.Add(entry.Id);
                    continue;
                }

                if (removableFinishedTerminalIdSet != null &&
                    removableFinishedTerminalIdSet.Contains(entry.Id))
                {
                    toRemoveFinished.Add(entry.Id);
                }
            }
        }

        internal static HashSet<ContractRemovalKey> BuildCurrentContractRemovalKeys(
            IReadOnlyList<ContractFilterEntry> currentEntries,
            HashSet<Guid> activeIdSet,
            HashSet<Guid> terminalIdSet,
            IReadOnlyDictionary<Guid, ContractTerminalOutcome> terminalOutcomeById,
            HashSet<Guid> removableTerminalIdSet)
        {
            var keys = new HashSet<ContractRemovalKey>();
            if (currentEntries == null)
                return keys;

            for (int i = 0; i < currentEntries.Count; i++)
            {
                var entry = currentEntries[i];
                if (entry.IsActive)
                {
                    if (activeIdSet == null || !activeIdSet.Contains(entry.Id))
                        keys.Add(ToRemovalKey(entry));
                    continue;
                }

                if (entry.IsTerminal &&
                    !IsSurvivingTerminalEntry(entry, terminalIdSet, terminalOutcomeById) &&
                    removableTerminalIdSet != null &&
                    removableTerminalIdSet.Contains(entry.Id))
                {
                    keys.Add(ToRemovalKey(entry));
                }
            }

            return keys;
        }

        /// <summary>
        /// Pure: the live Offered entries whose contract the walk holds terminal and not
        /// Active (todo STALE-OFFER-RE-ACCEPTABLE-AFTER-FLIGHT-PASSES-COMMITTED-RESOLUTION).
        /// Stock never re-offers a finished guid, so such an offer is the quicksave's copy from
        /// before a committed accept that a rewind restored; the committed timeline already
        /// accepted and ended it. Declined and every other state are left alone.
        /// </summary>
        internal static HashSet<ContractRemovalKey> BuildEndedLifecycleOfferedRemovalKeys(
            IReadOnlyList<ContractFilterEntry> currentEntries,
            HashSet<Guid> activeIdSet,
            HashSet<Guid> terminalIdSet)
        {
            var keys = new HashSet<ContractRemovalKey>();
            if (currentEntries == null || terminalIdSet == null || terminalIdSet.Count == 0)
                return keys;

            for (int i = 0; i < currentEntries.Count; i++)
            {
                var entry = currentEntries[i];
                if (entry.State != Contract.State.Offered)
                    continue;
                if (!terminalIdSet.Contains(entry.Id))
                    continue;
                if (activeIdSet != null && activeIdSet.Contains(entry.Id))
                    continue;
                keys.Add(ToRemovalKey(entry));
            }

            return keys;
        }

        internal static HashSet<ContractRemovalKey> BuildFinishedContractRemovalKeys(
            IReadOnlyList<ContractFilterEntry> finishedEntries,
            HashSet<Guid> terminalIdSet,
            IReadOnlyDictionary<Guid, ContractTerminalOutcome> terminalOutcomeById,
            HashSet<Guid> removableFinishedTerminalIdSet)
        {
            var keys = new HashSet<ContractRemovalKey>();
            if (finishedEntries == null)
                return keys;

            for (int i = 0; i < finishedEntries.Count; i++)
            {
                var entry = finishedEntries[i];
                if (entry.IsTerminal &&
                    !IsSurvivingTerminalEntry(entry, terminalIdSet, terminalOutcomeById) &&
                    removableFinishedTerminalIdSet != null &&
                    removableFinishedTerminalIdSet.Contains(entry.Id))
                {
                    keys.Add(ToRemovalKey(entry));
                }
            }

            return keys;
        }

        private static ContractRemovalKey ToRemovalKey(ContractFilterEntry entry)
        {
            return new ContractRemovalKey
            {
                Id = entry.Id,
                State = entry.State
            };
        }

        /// <summary>One Active contract the patch moves into stock's finished list.</summary>
        internal struct RetiredContractMove
        {
            public Guid Id;
            public ContractTerminalOutcome Outcome;
            public Contract.State FinishedState;
            /// <summary>The walk's end UT for the outcome, NaN when unknown.</summary>
            public double FinishedUT;
        }

        /// <summary>
        /// Pure: the stock state a contract ended with <paramref name="outcome"/> holds in
        /// stock's finished list. Stock's <c>ContractSystem.UpdateContracts</c> (decompiled
        /// KSP 1.12.5) files only Completed, DeadlineExpired, Failed and Cancelled there; an
        /// OfferExpired contract (requirements lost) is dropped, so it gets no state here.
        /// </summary>
        internal static bool TryMapOutcomeToFinishedState(
            ContractTerminalOutcome outcome, out Contract.State state)
        {
            switch (outcome)
            {
                case ContractTerminalOutcome.Completed:
                    state = Contract.State.Completed;
                    return true;
                case ContractTerminalOutcome.Failed:
                    state = Contract.State.Failed;
                    return true;
                case ContractTerminalOutcome.DeadlineExpired:
                    state = Contract.State.DeadlineExpired;
                    return true;
                case ContractTerminalOutcome.Cancelled:
                    state = Contract.State.Cancelled;
                    return true;
                default:
                    state = Contract.State.Active;
                    return false;
            }
        }

        /// <summary>
        /// Pure (todo CONTRACT-CROSSED-OUTCOME-MISSING-FROM-STOCK-ARCHIVE): which Active
        /// contracts leaving stock's active list go into its finished list, and in what
        /// state. A rewind to before a contract's committed outcome puts it back Active;
        /// when the clock crosses the outcome row, the walk holds it terminal and the patch
        /// takes it off the active list. Stock would have filed it as finished, so the patch
        /// does too, in the walk's outcome state. Rules, per Active entry targeted by
        /// <paramref name="toRemoveCurrentKeys"/>:
        /// <list type="number">
        ///   <item>Not terminal in the walk (dropped from the ledger, accepted later): no move.</item>
        ///   <item>Terminal with an outcome stock does not archive (requirements lost): no move, counted in <paramref name="noFinishedState"/>.</item>
        ///   <item>A finished entry with the same guid survives this patch: no move, counted in <paramref name="alreadyFinished"/> (never a second copy).</item>
        ///   <item>No real <paramref name="clockUT"/> (NaN on the full-replay paths: an OnLoad walk with no cutoff, the deferred scene-load patch, the cold-load full-replay fallback, the commit walk during OnLoad): no move, counted in <paramref name="noClock"/>. Fail closed: such a walk can hold terminal a contract whose committed outcome is still in the future, and a filed copy would sit in the finished list while the next cutoff walk restores it Active.</item>
        ///   <item>The outcome's end UT is after the real <paramref name="clockUT"/> (a walk past the clock, the Re-Fly invoke's whole-ledger walk): no move, counted in <paramref name="outcomeLater"/>; the Archive never shows an outcome before it happens.</item>
        ///   <item>Otherwise: move, at the walk's outcome state and end UT.</item>
        /// </list>
        /// A move matches the walk, so the next patch keeps the finished entry in place
        /// (a surviving terminal entry) and never restores it Active; a later tombstone of
        /// the outcome row removes it the way it removes any stock-finished contract.
        /// </summary>
        internal static Dictionary<Guid, RetiredContractMove> PlanRetiredActiveContractMoves(
            IReadOnlyList<ContractFilterEntry> currentEntries,
            HashSet<ContractRemovalKey> toRemoveCurrentKeys,
            HashSet<Guid> terminalIdSet,
            IReadOnlyDictionary<Guid, ContractTerminalOutcome> terminalOutcomeById,
            IReadOnlyDictionary<Guid, double> terminalUtById,
            IReadOnlyList<ContractFilterEntry> finishedEntries,
            HashSet<ContractRemovalKey> toRemoveFinishedKeys,
            double clockUT,
            out int alreadyFinished,
            out int noFinishedState,
            out int outcomeLater,
            out int noClock)
        {
            var moves = new Dictionary<Guid, RetiredContractMove>();
            alreadyFinished = 0;
            noFinishedState = 0;
            outcomeLater = 0;
            noClock = 0;
            bool clockReady = IsRealClock(clockUT);
            if (currentEntries == null || toRemoveCurrentKeys == null || toRemoveCurrentKeys.Count == 0
                || terminalIdSet == null || terminalOutcomeById == null)
                return moves;

            var finishedSurviving = new HashSet<Guid>();
            if (finishedEntries != null)
            {
                for (int i = 0; i < finishedEntries.Count; i++)
                {
                    var f = finishedEntries[i];
                    if (toRemoveFinishedKeys != null && toRemoveFinishedKeys.Contains(ToRemovalKey(f)))
                        continue;
                    finishedSurviving.Add(f.Id);
                }
            }

            var judged = new HashSet<Guid>();
            for (int i = 0; i < currentEntries.Count; i++)
            {
                var entry = currentEntries[i];
                if (!entry.IsActive || !toRemoveCurrentKeys.Contains(ToRemovalKey(entry)))
                    continue;
                if (!judged.Add(entry.Id))
                    continue;
                ContractTerminalOutcome outcome;
                if (!terminalIdSet.Contains(entry.Id)
                    || !terminalOutcomeById.TryGetValue(entry.Id, out outcome))
                    continue;
                Contract.State finishedState;
                if (!TryMapOutcomeToFinishedState(outcome, out finishedState))
                {
                    noFinishedState++;
                    continue;
                }
                if (finishedSurviving.Contains(entry.Id))
                {
                    alreadyFinished++;
                    continue;
                }
                if (!clockReady)
                {
                    noClock++;
                    continue;
                }
                double ut;
                if (terminalUtById == null || !terminalUtById.TryGetValue(entry.Id, out ut))
                    ut = double.NaN;
                if (!double.IsNaN(ut) && ut > clockUT + ContractAcceptClockToleranceSeconds)
                {
                    outcomeLater++;
                    continue;
                }
                moves[entry.Id] = new RetiredContractMove
                {
                    Id = entry.Id,
                    Outcome = outcome,
                    FinishedState = finishedState,
                    FinishedUT = ut
                };
            }

            return moves;
        }

        /// <summary>
        /// Puts an Active stock contract (already unregistered) in the finished state stock
        /// leaves it in, without stock's transition: <c>Contract.SetState</c> (decompiled KSP
        /// 1.12.5) pays the completion or the failure penalty, posts the message and fires
        /// <c>OnStateChange</c>, <c>onCompleted</c> / <c>onFailed</c> / <c>onCancelled</c> and
        /// <c>onFinished</c>; the ledger already applied the outcome, so none of that runs.
        /// The private <c>state</c> field has no setter and is written by reflection, as
        /// <c>Contract.Load</c> writes it; the protected <c>dateFinished</c> takes the walk's
        /// end UT when one is known (stock writes the deadline for an expiry, the clock
        /// otherwise). False when the reflection is unavailable; the caller then leaves
        /// the contract out of the finished list rather than file it Active there.
        /// </summary>
        internal static bool TryRetireContractSilently(Contract contract, Contract.State state, double finishedUT)
        {
            if (contract == null)
                return false;
            if (contractStateField == null)
            {
                contractStateField = typeof(Contract).GetField(
                    "state", BindingFlags.Instance | BindingFlags.NonPublic);
                if (contractStateField == null || contractStateField.FieldType != typeof(Contract.State))
                {
                    contractStateField = null;
                    EmitContractStateReflectionWarnOnce(
                        "typeof(Contract).GetField(\"state\") is missing or not a Contract.State");
                    return false;
                }
            }
            if (contractDateFinishedField == null)
            {
                contractDateFinishedField = typeof(Contract).GetField(
                    "dateFinished", BindingFlags.Instance | BindingFlags.NonPublic);
                if (contractDateFinishedField != null && contractDateFinishedField.FieldType != typeof(double))
                    contractDateFinishedField = null;
            }
            try
            {
                contractStateField.SetValue(contract, state);
                if (contractDateFinishedField != null && !double.IsNaN(finishedUT) && !double.IsInfinity(finishedUT))
                    contractDateFinishedField.SetValue(contract, finishedUT);
                return true;
            }
            catch (Exception ex)
            {
                EmitContractStateReflectionWarnOnce(
                    $"writing Contract.state threw {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static void EmitContractStateReflectionWarnOnce(string detail)
        {
            if (contractStateReflectionWarnEmitted)
                return;
            contractStateReflectionWarnEmitted = true;
            ParsekLog.Warn(Tag,
                "PatchContracts: could not file a contract the ledger ended into stock's finished list " +
                $"(one-shot per session) - {detail}. It is missing from the Mission Control Archive.");
        }

        /// <summary>
        /// Files each removed Active contract the plan names into stock's finished list,
        /// silently (<see cref="TryRetireContractSilently"/>), one Info line per move.
        /// </summary>
        internal static void ArchiveRetiredActiveContracts(
            List<Contract> removedCurrent,
            Dictionary<Guid, RetiredContractMove> moves,
            List<Contract> finishedContracts,
            ref int archived,
            ref int archiveFailed)
        {
            if (removedCurrent == null || moves == null || moves.Count == 0)
                return;
            var done = new HashSet<Guid>();
            for (int i = 0; i < removedCurrent.Count; i++)
            {
                Contract c = removedCurrent[i];
                RetiredContractMove move;
                if (c == null || !moves.TryGetValue(c.ContractGuid, out move) || !done.Add(move.Id))
                    continue;
                if (finishedContracts == null || !TryRetireContractSilently(c, move.FinishedState, move.FinishedUT))
                {
                    archiveFailed++;
                    ParsekLog.Warn(Tag,
                        $"PatchContracts: could not file ended contract {move.Id} '{c.Title ?? ""}' " +
                        $"into stock's finished list (outcome={move.Outcome}) - it is off the active list only");
                    continue;
                }
                finishedContracts.Add(c);
                archived++;
                ParsekLog.Info(Tag,
                    $"PatchContracts: filed ended contract {move.Id} '{c.Title ?? ""}' into stock's finished list " +
                    $"as {move.FinishedState} (outcome={move.Outcome}, " +
                    $"finishedUT={move.FinishedUT.ToString("R", IC)}) silently: no GameEvents, no reward or penalty");
            }
        }

        /// <summary>
        /// Pure helper: composes a bounded, comma-joined sample of changed-identity
        /// descriptions for a default-level (Info) apply line. Returns string.Empty for a
        /// null/empty list so the caller appends NOTHING (steady-state Info lines stay
        /// byte-identical). Under the cap, the full list joins as-is; over the cap, the
        /// first <paramref name="cap"/> entries join followed by a " (+N more)" overflow
        /// marker. The full list is emitted separately at Verbose by each caller.
        /// </summary>
        internal static string ComposeBoundedIdentitySample(
            IReadOnlyList<string> changedDescriptions, int cap)
        {
            if (changedDescriptions == null || changedDescriptions.Count == 0)
                return string.Empty;

            int count = changedDescriptions.Count;
            if (count <= cap)
                return string.Join(", ", changedDescriptions);

            var head = new List<string>(cap);
            for (int i = 0; i < cap; i++)
                head.Add(changedDescriptions[i]);

            return string.Join(", ", head)
                + " (+" + (count - cap).ToString(IC) + " more)";
        }

        /// <summary>
        /// Pure helper: derives the contract identities targeted for removal by the
        /// per-state removal-key sets — current-set keys first, then finished-set keys.
        /// Headless-testable because both inputs come from pure key builders. The result
        /// reflects the TARGETED removal keys, which can exceed the count actually removed
        /// when a targeted contract was already absent from the live KSP list.
        /// </summary>
        internal static List<string> DescribeRemovedContractIdentities(
            HashSet<ContractRemovalKey> currentKeys,
            HashSet<ContractRemovalKey> finishedKeys)
        {
            var ids = new List<string>();
            if (currentKeys != null)
            {
                foreach (var key in currentKeys)
                    ids.Add(key.Id.ToString());
            }

            if (finishedKeys != null)
            {
                foreach (var key in finishedKeys)
                    ids.Add(key.Id.ToString());
            }

            return ids;
        }

        private static bool IsSurvivingTerminalEntry(
            ContractFilterEntry entry,
            HashSet<Guid> terminalIdSet,
            IReadOnlyDictionary<Guid, ContractTerminalOutcome> terminalOutcomeById)
        {
            if (terminalIdSet == null || !terminalIdSet.Contains(entry.Id))
                return false;

            if (terminalOutcomeById == null)
                return true;

            ContractTerminalOutcome terminalOutcome;
            return terminalOutcomeById.TryGetValue(entry.Id, out terminalOutcome)
                && IsTerminalContractStateCompatible(entry.State, terminalOutcome);
        }

        private static bool IsTerminalContractStateCompatible(
            Contract.State state,
            ContractTerminalOutcome terminalOutcome)
        {
            switch (terminalOutcome)
            {
                case ContractTerminalOutcome.Completed:
                    return state == Contract.State.Completed;
                case ContractTerminalOutcome.Failed:
                    return state == Contract.State.Failed;
                case ContractTerminalOutcome.DeadlineExpired:
                    return state == Contract.State.DeadlineExpired;
                case ContractTerminalOutcome.Cancelled:
                    return state == Contract.State.Cancelled;
                case ContractTerminalOutcome.RequirementsLost:
                    return state == Contract.State.OfferExpired;
                default:
                    return false;
            }
        }

        private static List<ContractFilterEntry> BuildContractFilterEntries(
            IReadOnlyList<Contract> contracts)
        {
            var entries = new List<ContractFilterEntry>();
            if (contracts == null)
                return entries;

            for (int i = 0; i < contracts.Count; i++)
            {
                var c = contracts[i];
                if (c == null) continue;
                entries.Add(new ContractFilterEntry
                {
                    Id = c.ContractGuid,
                    IsActive = c.ContractState == Contract.State.Active,
                    IsTerminal = IsTerminalContractState(c.ContractState),
                    State = c.ContractState
                });
            }

            return entries;
        }

        private static Dictionary<Guid, ContractTerminalOutcome> BuildTerminalContractOutcomeMap(
            ContractsModule contracts)
        {
            var result = new Dictionary<Guid, ContractTerminalOutcome>();
            if (contracts == null)
                return result;

            var source = contracts.GetTerminalContractOutcomes();
            if (source == null)
                return result;

            foreach (var kvp in source)
            {
                Guid contractGuid;
                if (Guid.TryParse(kvp.Key, out contractGuid))
                    result[contractGuid] = kvp.Value;
            }

            return result;
        }

        private static Dictionary<Guid, double> BuildTerminalContractUtMap(ContractsModule contracts)
        {
            var result = new Dictionary<Guid, double>();
            var source = contracts != null ? contracts.GetTerminalContractUTs() : null;
            if (source == null)
                return result;

            foreach (var kvp in source)
            {
                Guid contractGuid;
                if (Guid.TryParse(kvp.Key, out contractGuid))
                    result[contractGuid] = kvp.Value;
            }

            return result;
        }

        private static int RemoveContractsByKey(
            List<Contract> contracts,
            HashSet<ContractRemovalKey> toRemove,
            bool unregisterBeforeRemove,
            ref int unregisterFailures,
            List<Contract> removedOut = null)
        {
            if (contracts == null || toRemove == null || toRemove.Count == 0)
                return 0;

            int removed = 0;
            for (int i = contracts.Count - 1; i >= 0; i--)
            {
                var c = contracts[i];
                if (c == null) continue;
                var key = new ContractRemovalKey
                {
                    Id = c.ContractGuid,
                    State = c.ContractState
                };
                if (!toRemove.Contains(key)) continue;

                if (unregisterBeforeRemove)
                {
                    try
                    {
                        c.Unregister();
                    }
                    catch (Exception ex)
                    {
                        unregisterFailures++;
                        ParsekLog.Warn(Tag,
                            $"PatchContracts: failed to unregister stale contract '{c.ContractGuid}': {ex.Message}");
                    }
                }

                contracts.RemoveAt(i);
                removed++;
                if (removedOut != null)
                    removedOut.Add(c);
            }

            return removed;
        }

        /// <summary>
        /// Pure: true for a live entry that is the Offered instance of the contract being
        /// restored Active (same guid, still Offered).
        /// </summary>
        internal static bool IsOfferedCopyOfRestoredContract(ContractFilterEntry entry, Guid restoredId)
        {
            return entry.Id == restoredId && entry.State == Contract.State.Offered;
        }

        private static int RemoveOfferedCopiesOfContract(
            List<Contract> contracts, Guid restoredId, ref int unregisterFailures)
        {
            if (contracts == null)
                return 0;
            var keys = new HashSet<ContractRemovalKey>();
            var entries = BuildContractFilterEntries(contracts);
            for (int i = 0; i < entries.Count; i++)
            {
                if (IsOfferedCopyOfRestoredContract(entries[i], restoredId))
                    keys.Add(ToRemovalKey(entries[i]));
            }
            return RemoveContractsByKey(contracts, keys, unregisterBeforeRemove: true, ref unregisterFailures);
        }

        private static bool IsTerminalContractState(Contract.State state)
        {
            switch (state)
            {
                case Contract.State.Completed:
                case Contract.State.Failed:
                case Contract.State.Cancelled:
                case Contract.State.DeadlineExpired:
                    return true;
                default:
                    return false;
            }
        }
    }
}
