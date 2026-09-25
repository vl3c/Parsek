using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Contracts;
using KSP.UI;
using KSP.UI.Screens;
using UnityEngine.UI;

namespace Parsek
{
    /// <summary>
    /// Applies the Mission Control stock-control annotations to the live screen. The
    /// Harmony patches in <c>Patches/MissionControlStockUiPatches.cs</c> and
    /// <c>Patches/ContractConfiguratorPatches.cs</c> call in here from the points where
    /// stock (or Contract Configurator) BUILDS a row or FILLS the detail panel, so every
    /// rebuild re-applies the annotation. Every decision goes through
    /// <see cref="MissionControlStockAnnotation.Decide"/>; nothing here is a second
    /// predicate. Parsek creates no GameObject on this screen.
    /// </summary>
    internal static class MissionControlStockUi
    {
        private const string Tag = "StockUiOverlay";

        // One stock RebuildContractList pass: the index and UT are fetched once and every
        // row AddItem builds is collected, so the pass logs one summary per rebuild.
        private static bool passOpen;
        private static string passTab;
        private static CommittedFutureIndex passIndex;
        private static double passNow;
        private static ContractSlotForecast passSlots;
        private static readonly List<StockUiDecoration> passDecorations = new List<StockUiDecoration>();

        // Keys already logged since Mission Control last opened (the CC CanAccept refusal
        // logs once per contract per open, not per selection).
        private static readonly HashSet<string> loggedThisOpen = new HashSet<string>(StringComparer.Ordinal);

        private static readonly Dictionary<Type, FieldInfo[]> containerFields = new Dictionary<Type, FieldInfo[]>();

        // True while btnAccept is greyed because PARSEK greyed it. Stock 1.12.5 writes
        // btnAccept.interactable only in RefreshUIControls (never on select, deselect or
        // UpdateInfoPanelContract), so Parsek's disable outlives the blocked selection
        // and must be undone by Parsek when an unblocked Offered contract is selected.
        private static bool acceptDisabledByParsek;

        /// <summary>The Mission Control tab stock is showing.</summary>
        internal static string TabForDisplayMode(MissionControl.DisplayMode mode)
        {
            switch (mode)
            {
                case MissionControl.DisplayMode.Available:
                    return StockUiDecorationQuery.MissionControlAvailableTab;
                case MissionControl.DisplayMode.Active:
                    return StockUiDecorationQuery.MissionControlActiveTab;
                default:
                    return StockUiDecorationQuery.MissionControlArchiveTab;
            }
        }

        /// <summary>The contract key the committed-future index uses for a contract.</summary>
        internal static string ContractKey(Contract contract)
        {
            return contract != null ? contract.ContractGuid.ToString() : null;
        }

        /// <summary>The decision for one live contract, over the current index, UT and
        /// contract-slot forecast (<see cref="ContractSlotReservation.ForecastNow()"/>).</summary>
        internal static StockUiDecoration DecideNow(Contract contract)
        {
            CommittedFutureIndex index = CommittedFutureIndexCache.Current;
            double now = CommittedFutureIndexCache.CurrentUT();
            return MissionControlStockAnnotation.Decide(
                index,
                now,
                ContractKey(contract),
                contract != null ? contract.ContractState : Contract.State.Offered,
                ReservationExplanation.DefaultDateFormatter,
                ContractSlotReservation.ForecastNow(index, now),
                contract != null && contract.AutoAccept);
        }

        // ---------------------------------------------------------------- screen lifecycle

        internal static void OnScreenOpened()
        {
            int cleared = loggedThisOpen.Count;
            loggedThisOpen.Clear();
            acceptDisabledByParsek = false;
            ParsekLog.Verbose(Tag, "MissionControl opened - stock-control annotations active (once-per-open log keys cleared="
                + cleared.ToString(CultureInfo.InvariantCulture) + ")");
        }

        internal static void OnScreenClosed()
        {
            if (passOpen)
            {
                ParsekLog.Verbose(Tag, "MissionControl closed with a rebuild pass still open - discarding it");
                ResetPass();
            }
            acceptDisabledByParsek = false;
            ParsekLog.Verbose(Tag, "MissionControl closed - Parsek owns no GameObject on it, nothing to strip");
        }

        /// <summary>True the first time <paramref name="key"/> is seen since Mission Control opened.</summary>
        internal static bool FirstLogThisOpen(string kind, string key)
        {
            return loggedThisOpen.Add((kind ?? "") + "|" + (key ?? ""));
        }

        // ---------------------------------------------------------------- stock row rebuild

        internal static void BeginRebuildPass(MissionControl.DisplayMode mode)
        {
            if (passOpen)
            {
                ParsekLog.Verbose(Tag, "MissionControl rebuild pass began while a previous pass was open - discarding "
                    + passDecorations.Count.ToString(CultureInfo.InvariantCulture) + " collected row(s)");
            }
            passOpen = true;
            passTab = TabForDisplayMode(mode);
            passIndex = CommittedFutureIndexCache.Current;
            passNow = CommittedFutureIndexCache.CurrentUT();
            passSlots = ContractSlotReservation.ForecastNow(passIndex, passNow);
            passDecorations.Clear();
            if (passSlots != null && passSlots.BlocksNewAcceptNow)
                ParsekLog.Verbose(Tag, "MissionControl rebuild pass: committed timeline needs every free slot - "
                    + passSlots.Describe());
        }

        internal static void EndRebuildPass()
        {
            if (!passOpen) return;
            StockUiDecorationQuery.LogPass(StockUiScreen.MissionControl, new[] { passTab }, passDecorations);
            ResetPass();
        }

        private static void ResetPass()
        {
            passOpen = false;
            passTab = null;
            passIndex = null;
            passSlots = null;
            passDecorations.Clear();
        }

        /// <summary>
        /// The <c>MissionControl.AddItem</c> label for <paramref name="contract"/>: the
        /// incoming label unchanged for an unmarked row, else the full stock title (a
        /// non-empty label replaces stock's whole title) plus the Parsek status.
        /// </summary>
        internal static string LabelForAddItem(Contract contract, string label)
        {
            if (contract == null)
                return label;

            StockUiDecoration d;
            if (passOpen)
            {
                d = MissionControlStockAnnotation.Decide(passIndex, passNow, ContractKey(contract),
                    contract.ContractState, ReservationExplanation.DefaultDateFormatter, passSlots, contract.AutoAccept);
                passDecorations.Add(d);
            }
            else
            {
                d = DecideNow(contract);
                ParsekLog.VerboseRateLimited(Tag, "mc-additem-outside-rebuild",
                    "MissionControl.AddItem ran outside RebuildContractList - row decided alone, no pass summary");
            }

            if (!d.Marked)
                return label;
            return MissionControlStockAnnotation.ComposeRowLabel(label, contract.Title, d);
        }

        // ---------------------------------------------------------------- detail panel

        /// <summary>
        /// What the detail panel must write to <c>btnAccept</c> for the contract being
        /// shown: false for a committed accept or a slot the committed timeline needs (and
        /// Parsek now owns the greyed state);
        /// for an unblocked Offered contract, stock's own Accept rule
        /// (<paramref name="stockAllows"/>) but ONLY when Parsek greyed the button earlier;
        /// otherwise null (leave it). Undoing only Parsek's own write, rather than
        /// re-deriving Accept on every selection, keeps stock's slot rule and any other
        /// mod's Accept gate authoritative for contracts Parsek does not block. A
        /// non-Offered contract hides Accept, so the restore waits for the next Offered one.
        /// </summary>
        internal static bool? ResolveAcceptWrite(StockUiDecoration decision, Contract.State state, Func<bool> stockAllows)
        {
            if (MissionControlStockAnnotation.BlocksAccept(decision))
            {
                acceptDisabledByParsek = true;
                return false;
            }
            if (!acceptDisabledByParsek || state != Contract.State.Offered)
                return null;
            acceptDisabledByParsek = false;
            return stockAllows == null || stockAllows();
        }

        /// <summary>
        /// The <c>UpdateInfoPanelContract</c> postfix body: appends the explanation to the
        /// stock detail text and greys out Accept and Decline for a committed accept, or
        /// Cancel for a committed resolution. For an unblocked contract stock's Decline and
        /// Cancel state is kept (stock rewrites both here) and Accept is restored to stock's
        /// rule only if Parsek greyed it for an earlier selection (<see cref="ResolveAcceptWrite"/>).
        /// Under Contract Configurator its select handler overwrites Accept right after this
        /// with the same rule.
        /// </summary>
        internal static void ApplyDetailPanel(MissionControl mc, Contract contract, string source)
        {
            if (mc == null || contract == null)
            {
                // Stock and CC both call UpdateInfoPanelContract(null) to clear the panel.
                ParsekLog.VerboseRateLimited(Tag, "mc-detail-null",
                    "MissionControl detail panel: no contract (" + (source ?? "?") + ") - nothing to annotate");
                return;
            }

            StockUiDecoration d = DecideNow(contract);
            SetDetailText(mc, d);

            // An Active contract the committed timeline resolves later: Cancel greyed out.
            // Stock sets btnCancel.interactable = CanBeCancelled() in this same method on
            // every Active selection (KSP 1.12.5; RefreshUIControls never touches it), so an
            // unblocked Active contract needs nothing restored here.
            if (MissionControlStockAnnotation.BlocksCancel(d))
            {
                SetInteractable(mc.btnCancel, false);
                ParsekLog.Verbose(Tag, "MissionControl detail panel: contract=" + d.Id
                    + " tab=" + d.Tab + " blocked - Cancel disabled, why appended (" + (source ?? "?") + ") why=\"" + d.Why + "\"");
                return;
            }

            bool? acceptWrite = ResolveAcceptWrite(d, contract.ContractState, () => StockAcceptAllowed(contract));
            if (!d.Blocked)
            {
                if (acceptWrite.HasValue)
                {
                    SetInteractable(mc.btnAccept, acceptWrite.Value);
                    ParsekLog.Verbose(Tag, "MissionControl detail panel: contract=" + d.Id
                        + " not blocked - Accept restored to stock's rule accept=" + (acceptWrite.Value ? "true" : "false")
                        + " after Parsek greyed it for an earlier selection (" + (source ?? "?") + ")");
                }
                else
                {
                    ParsekLog.Verbose(Tag, "MissionControl detail panel: contract=" + d.Id
                        + " tab=" + d.Tab + " not blocked - stock Accept/Decline state kept (" + (source ?? "?") + ")");
                }
                return;
            }

            SetInteractable(mc.btnAccept, false);
            if (MissionControlStockAnnotation.BlocksAcceptForSlot(d))
            {
                // Declining an offer the committed timeline does not accept is fine: stock's
                // Decline state (written by stock just before this postfix) is kept.
                ParsekLog.Verbose(Tag, "MissionControl detail panel: contract=" + d.Id
                    + " slot needed by a committed accept - Accept disabled, why appended (" + (source ?? "?")
                    + ") why=\"" + d.Why + "\"");
                return;
            }
            SetInteractable(mc.btnDecline, false);
            ParsekLog.Verbose(Tag, "MissionControl detail panel: contract=" + d.Id
                + " blocked - Accept and Decline disabled, why appended (" + (source ?? "?") + ") why=\"" + d.Why + "\"");
        }

        /// <summary>
        /// The <c>RefreshUIControls</c> postfix body: stock rewrites <c>btnAccept</c> from
        /// the slot count for every contract, so the block is re-asserted for the
        /// selected contract. Returns true when a button had to be re-disabled.
        /// </summary>
        internal static bool ReapplyButtonsForSelection(MissionControl mc, string source)
        {
            Contract contract = mc != null && mc.selectedMission != null ? mc.selectedMission.contract : null;
            // Stock has just written btnAccept from its own slot rule, so any earlier
            // Parsek disable is gone unless it is re-applied below.
            acceptDisabledByParsek = false;
            if (contract == null)
                return false;

            StockUiDecoration d = DecideNow(contract);
            if (!d.Blocked)
                return false;

            if (MissionControlStockAnnotation.BlocksCancel(d))
            {
                // Stock RefreshUIControls does not write btnCancel; re-asserting it here
                // keeps the block correct if a mod's refresh does.
                bool cancelChanged = IsInteractable(mc.btnCancel);
                SetInteractable(mc.btnCancel, false);
                if (cancelChanged)
                    ParsekLog.VerboseRateLimited(Tag, "mc-reapply-cancel-" + d.Id,
                        "MissionControl " + (source ?? "?") + " re-enabled Cancel on committed-resolution contract="
                        + d.Id + " - Cancel disabled again");
                return cancelChanged;
            }

            acceptDisabledByParsek = true;
            if (MissionControlStockAnnotation.BlocksAcceptForSlot(d))
            {
                bool acceptChanged = IsInteractable(mc.btnAccept);
                SetInteractable(mc.btnAccept, false);
                if (acceptChanged)
                    ParsekLog.VerboseRateLimited(Tag, "mc-reapply-slot",
                        "MissionControl " + (source ?? "?") + " re-enabled Accept on contract=" + d.Id
                        + " while the committed timeline needs every free slot - Accept disabled again");
                return acceptChanged;
            }
            bool changed = IsInteractable(mc.btnAccept) || IsInteractable(mc.btnDecline);
            SetInteractable(mc.btnAccept, false);
            SetInteractable(mc.btnDecline, false);
            if (changed)
                ParsekLog.VerboseRateLimited(Tag, "mc-reapply-" + d.Id,
                    "MissionControl " + (source ?? "?") + " re-enabled a button on committed-accept contract="
                    + d.Id + " - Accept and Decline disabled again");
            return changed;
        }

        // ---------------------------------------------------------------- timeline change

        /// <summary>
        /// Re-applies every annotation on an open Mission Control after the committed
        /// timeline changed: relabels the listed rows in place (stock and CC rows alike)
        /// and re-evaluates the selected contract's detail panel, restoring stock's own
        /// button state when a block lifted.
        /// </summary>
        internal static void RefreshOpenScreen(MissionControl mc, string reason)
        {
            if (mc == null)
            {
                ParsekLog.Verbose(Tag, "MissionControl refresh (" + (reason ?? "?") + "): no MissionControl instance - skipped");
                return;
            }

            RelabelRows(mc, "refresh:" + (reason ?? "?"));

            Contract selected = mc.selectedMission != null ? mc.selectedMission.contract : null;
            if (selected == null)
                return;

            StockUiDecoration d = DecideNow(selected);
            SetDetailText(mc, d);
            if (MissionControlStockAnnotation.BlocksCancel(d))
            {
                SetInteractable(mc.btnCancel, false);
                ParsekLog.Verbose(Tag, "MissionControl refresh: selected contract=" + d.Id + " blocked - Cancel disabled");
                return;
            }
            if (MissionControlStockAnnotation.BlocksAcceptForSlot(d))
            {
                // Only Accept: Decline goes back to stock's own rule, in case a committed
                // accept of this contract (which greyed both) is what just went away.
                bool slotDecline = selected.CanBeDeclined();
                acceptDisabledByParsek = true;
                SetInteractable(mc.btnAccept, false);
                SetInteractable(mc.btnDecline, slotDecline);
                ParsekLog.Verbose(Tag, "MissionControl refresh: selected contract=" + d.Id
                    + " slot needed by a committed accept - Accept disabled, stock Decline state decline="
                    + (slotDecline ? "true" : "false"));
                return;
            }
            if (d.Blocked)
            {
                acceptDisabledByParsek = true;
                SetInteractable(mc.btnAccept, false);
                SetInteractable(mc.btnDecline, false);
                ParsekLog.Verbose(Tag, "MissionControl refresh: selected contract=" + d.Id + " still blocked");
                return;
            }

            if (selected.ContractState == Contract.State.Active)
            {
                // Stock's own Cancel rule (UpdateInfoPanelContract's Active branch).
                bool cancel = selected.CanBeCancelled();
                SetInteractable(mc.btnCancel, cancel);
                ParsekLog.Verbose(Tag, "MissionControl refresh: selected contract=" + d.Id
                    + " not blocked - stock Cancel state restored cancel=" + (cancel ? "true" : "false"));
                return;
            }

            if (selected.ContractState == Contract.State.Offered)
            {
                bool accept = StockAcceptAllowed(selected);
                bool decline = selected.CanBeDeclined();
                acceptDisabledByParsek = false;
                SetInteractable(mc.btnAccept, accept);
                SetInteractable(mc.btnDecline, decline);
                ParsekLog.Verbose(Tag, "MissionControl refresh: selected contract=" + d.Id
                    + " no longer blocked - stock state restored accept=" + (accept ? "true" : "false")
                    + " decline=" + (decline ? "true" : "false"));
            }
        }

        /// <summary>
        /// Stock's Accept rule (the slot count, <c>MissionControl.RefreshUIControls</c>),
        /// and CC's per-prestige limit when Contract Configurator is loaded (its
        /// <c>OnSelectContract</c> rule).
        /// </summary>
        private static bool StockAcceptAllowed(Contract contract)
        {
            bool allowed = true;
            try
            {
                int limit = GameVariables.Instance.GetActiveContractsLimit(
                    ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.MissionControl));
                allowed = ContractSystem.Instance.GetActiveContractCount() < limit;
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag, "MissionControl refresh: slot limit unavailable (" + ex.GetType().Name + ") - Accept left enabled");
            }

            bool ccAllows;
            if (allowed && Patches.ContractConfiguratorCompat.TryInvokeCanAccept(contract, out ccAllows))
                allowed = ccAllows;
            return allowed;
        }

        /// <summary>
        /// Relabels every contract row the list currently holds, in place: the Parsek
        /// status is added to a marked row and stripped from an unmarked one. Logs one
        /// decoration pass. Returns the number of rows whose label changed.
        /// </summary>
        internal static int RelabelRows(MissionControl mc, string source)
        {
            UIList list = mc != null ? mc.scrollListContracts : null;
            if (list == null)
            {
                ParsekLog.Verbose(Tag, "MissionControl relabel (" + (source ?? "?") + "): no contract list - skipped");
                return 0;
            }

            CommittedFutureIndex index = CommittedFutureIndexCache.Current;
            double now = CommittedFutureIndexCache.CurrentUT();
            ContractSlotForecast slots = ContractSlotReservation.ForecastNow(index, now);
            var decorations = new List<StockUiDecoration>();
            int relabeled = 0, nonContractRows = 0;
            int count = list.Count;
            for (int i = 0; i < count; i++)
            {
                UIListItem item = list.GetUilistItemAt(i);
                MCListItem row = item != null ? item.GetComponent<MCListItem>() : null;
                Contract contract = item != null ? ExtractContractFromData(item.Data) : null;
                if (row == null || row.title == null || contract == null)
                {
                    nonContractRows++;
                    continue;
                }

                StockUiDecoration d = MissionControlStockAnnotation.Decide(index, now, ContractKey(contract),
                    contract.ContractState, ReservationExplanation.DefaultDateFormatter, slots, contract.AutoAccept);
                decorations.Add(d);
                string current = row.title.text;
                string next = d.Marked
                    ? MissionControlStockAnnotation.ComposeRowLabel(current, contract.Title, d)
                    : MissionControlStockAnnotation.StripRowStatus(current);
                if (!string.Equals(current, next, StringComparison.Ordinal))
                {
                    row.title.text = next;
                    relabeled++;
                }
            }

            StockUiDecorationQuery.LogPass(StockUiScreen.MissionControl,
                new[] { TabForDisplayMode(mc.displayMode) }, decorations);
            ParsekLog.Verbose(Tag, "MissionControl relabel (" + (source ?? "?") + "): rows="
                + count.ToString(CultureInfo.InvariantCulture)
                + " contractRows=" + decorations.Count.ToString(CultureInfo.InvariantCulture)
                + " otherRows=" + nonContractRows.ToString(CultureInfo.InvariantCulture)
                + " relabeled=" + relabeled.ToString(CultureInfo.InvariantCulture));
            return relabeled;
        }

        /// <summary>
        /// Relabels one row (Contract Configurator's per-row title seam). Returns true
        /// when the row is marked.
        /// </summary>
        internal static bool RelabelRow(MCListItem row, object rowData, string source)
        {
            Contract contract = ExtractContractFromData(rowData);
            if (row == null || row.title == null || contract == null)
                return false;

            StockUiDecoration d = DecideNow(contract);
            string current = row.title.text;
            string next = d.Marked
                ? MissionControlStockAnnotation.ComposeRowLabel(current, contract.Title, d)
                : MissionControlStockAnnotation.StripRowStatus(current);
            if (!string.Equals(current, next, StringComparison.Ordinal))
                row.title.text = next;
            if (d.Marked)
                ParsekLog.Verbose(Tag, StockUiDecorationQuery.FormatItemLine(d) + " source=" + (source ?? "?"));
            return d.Marked;
        }

        // ---------------------------------------------------------------- row payloads

        /// <summary>
        /// The contract behind a Mission Control row payload (<c>UIListItem.Data</c>):
        /// stock's <c>MissionControl.MissionSelection</c>, a bare <c>Contract</c>, or any
        /// other object with a public <c>Contract</c>-typed <c>contract</c> field or a
        /// <c>MissionSelection</c>-typed <c>missionSelection</c> field (Contract
        /// Configurator's <c>MissionControlUI.ContractContainer</c>, whose <c>contract</c>
        /// is null on a contract-TYPE row). Null for anything else (CC group rows).
        /// </summary>
        internal static Contract ExtractContractFromData(object data)
        {
            if (data == null)
                return null;
            var selection = data as MissionControl.MissionSelection;
            if (selection != null)
                return selection.contract;
            var contract = data as Contract;
            if (contract != null)
                return contract;

            FieldInfo[] fields = ContainerFields(data.GetType());
            for (int i = 0; i < fields.Length; i++)
            {
                object value = fields[i].GetValue(data);
                var asContract = value as Contract;
                if (asContract != null)
                    return asContract;
                var asSelection = value as MissionControl.MissionSelection;
                if (asSelection != null && asSelection.contract != null)
                    return asSelection.contract;
            }
            return null;
        }

        /// <summary>The contract behind a Mission Control list row.</summary>
        internal static Contract ExtractRowContract(MCListItem row)
        {
            return row != null && row.container != null ? ExtractContractFromData(row.container.Data) : null;
        }

        private static FieldInfo[] ContainerFields(Type type)
        {
            FieldInfo[] fields;
            if (containerFields.TryGetValue(type, out fields))
                return fields;

            var found = new List<FieldInfo>();
            FieldInfo contractField = type.GetField("contract", BindingFlags.Instance | BindingFlags.Public);
            if (contractField != null && typeof(Contract).IsAssignableFrom(contractField.FieldType))
                found.Add(contractField);
            FieldInfo selectionField = type.GetField("missionSelection", BindingFlags.Instance | BindingFlags.Public);
            if (selectionField != null && typeof(MissionControl.MissionSelection).IsAssignableFrom(selectionField.FieldType))
                found.Add(selectionField);
            fields = found.ToArray();
            containerFields[type] = fields;
            ParsekLog.Verbose(Tag, "MissionControl row payload type " + type.FullName + ": contract fields="
                + fields.Length.ToString(CultureInfo.InvariantCulture));
            return fields;
        }

        // Writes the detail text only when it changes: KSPCF's ShowContractFinishDates also
        // postfixes UpdateInfoPanelContract (Archive tab only) and an unchanged write would
        // just force a TextMeshPro relayout.
        private static void SetDetailText(MissionControl mc, StockUiDecoration d)
        {
            if (mc.contractText == null) return;
            string current = mc.contractText.text;
            string next = MissionControlStockAnnotation.ComposeDetailText(current, d);
            if (!string.Equals(current, next, StringComparison.Ordinal))
                mc.contractText.text = next;
        }

        // ---------------------------------------------------------------- buttons

        private static bool IsInteractable(Button button)
        {
            return button != null && button.interactable;
        }

        private static void SetInteractable(Button button, bool value)
        {
            if (button != null)
                button.interactable = value;
        }

        internal static bool AcceptDisabledByParsekForTesting => acceptDisabledByParsek;

        internal static void ResetForTesting()
        {
            ResetPass();
            acceptDisabledByParsek = false;
            loggedThisOpen.Clear();
            containerFields.Clear();
            ContractSlotReservation.ResetForTesting();
        }
    }
}
