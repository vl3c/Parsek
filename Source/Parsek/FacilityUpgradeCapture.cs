using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Captures what a KSC facility upgrade cost, so the FacilityUpgrade ledger row carries
    /// stock's funds debit instead of 0.
    ///
    /// <para>Stock mechanics (decompiled KSP 1.12.5): <c>SpaceCenterBuilding.UpgradeFacility(bool
    /// deduceFunds)</c> is the funded upgrade entry point (the KSC context menu passes
    /// <c>Funding.Instance != null</c>, so Science and Sandbox games pass false). At max level
    /// it returns with a message. With <c>deduceFunds</c> it checks affordability through
    /// <c>CurrencyModifierQuery.RunQuery</c> (returning with no debit when the player cannot
    /// afford it) and calls
    /// <c>Funding.Instance.AddFunds(-|Facility.GetUpgradeCost()|, TransactionReasons.StructureConstruction)</c>,
    /// where <c>GetUpgradeCost()</c> is <c>upgradeLevels[level + 1].levelCost</c> times
    /// <c>Career.FundsLossMultiplier</c>. Only THEN does it run <c>ResetStructures()</c> and
    /// <c>Facility.SetLevel(level + 1)</c>, which fires <c>OnKSCFacilityUpgrading</c>
    /// synchronously (the event the recorder turns into the FacilityUpgraded event), and
    /// finally saves the game.</para>
    ///
    /// <para>The cost recorded is the OBSERVED <c>FundsChanged(StructureConstruction)</c> delta
    /// inside the call, not a recomputed <c>GetUpgradeCost()</c>: a stock strategy
    /// (Aggressive Negotiations, a <c>CurrencyOperation</c> on StructureConstruction) moves the
    /// pool by a discounted amount inside the same <c>AddFunds</c>, and
    /// <c>StrategyConversionCapture</c> deliberately records no funds leg for this reason
    /// because the channel here is event-derived (net). The observed delta is also exactly
    /// what <c>ReconcileKscAction</c> pairs the row against. The FundsChanged event itself is
    /// never converted into a ledger row, so the row's <see cref="GameAction.FacilityCost"/> is
    /// the only place the ledger carries this spend - no double count.</para>
    ///
    /// <para>Buildings <c>ResetStructures</c> repairs for free inside the call are queued here
    /// and written in one batch with the upgrade row, so no recalc runs between the debit and
    /// the row that explains it.</para>
    ///
    /// <para>The same scope, opened with <c>downgrade: true</c>, serves the private
    /// <c>SpaceCenterBuilding.DowngradeFacility(bool deduceFunds)</c>
    /// (FACILITY-DOWNGRADE-DEBIT-NOT-LEDGERED). Stock reaches it from the KSC facility menu's
    /// "Rebuild lvl N" button, shown when the menu is opened with Left Ctrl held on an
    /// out-of-service facility above level 1, outside Mission mode
    /// (<c>KSCFacilityContextMenu.showDowngradeControls = Input.GetKey(KeyCode.LeftControl)</c>,
    /// no cheat or debug gate). Its order is the upgrade's: an affordability check, then
    /// <c>AddFunds(-|GetDowngradeCost()|, StructureConstruction)</c> (0.667 times
    /// <c>upgradeLevels[level - 1].levelCost</c> times <c>Career.FundsLossMultiplier</c>), then
    /// <c>ResetStructures()</c> and <c>SetLevel(level - 1)</c>, then a save. The recorder reads
    /// the debit for the FacilityDowngraded event of that facility through
    /// <see cref="TryConsumeCostForDowngrade"/>, and that event becomes a
    /// <see cref="GameActionType.FacilityUpgrade"/> level-change row marked
    /// <see cref="GameAction.FacilityDowngrade"/>. A level drop with no downgrade scope open
    /// stays informational.</para>
    /// </summary>
    internal static class FacilityUpgradeCapture
    {
        private const string Tag = "GameStateRecorder";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // Cost-source tokens written on the recorder's FacilityUpgraded log line.
        internal const string SourceObservedDebit = "observed-debit";
        internal const string SourceNoFundsCharged = "no-funds-charged";
        internal const string SourceNoDebitObserved = "no-debit-observed";
        internal const string SourceUnmeasuredDebit = "unmeasured-debit";
        internal const string SourceNonDebit = "non-debit";
        internal const string SourceNoScope = "no-upgrade-scope";
        internal const string SourceFacilityMismatch = "facility-mismatch";
        internal const string SourceAlreadyConsumed = "already-consumed";
        internal const string SourceDirectionMismatch = "direction-mismatch";

        private static bool scopeActive;
        private static string scopeFacility;
        private static bool scopeFundsCharged;
        private static double scopeFundsAtOpen = double.NaN;
        private static int scopeObservedDebits;
        private static double scopeObservedDelta;
        private static bool scopeCostConsumed;
        private static bool scopeDowngrade;
        private static readonly List<GameStateEvent> scopePendingForward = new List<GameStateEvent>();

        /// <summary>
        /// True while a <c>SpaceCenterBuilding.UpgradeFacility</c> or <c>DowngradeFacility</c>
        /// call is on the stack.
        /// </summary>
        internal static bool IsScopeActive => scopeActive;

        /// <summary>
        /// True while a <c>DowngradeFacility</c> call on <paramref name="facilityId"/> is on the
        /// stack: the one case in which a FacilityDowngraded event is the player's paid
        /// downgrade and becomes a ledger row.
        /// </summary>
        internal static bool IsDowngradeScopeOpenFor(string facilityId)
        {
            return scopeActive && scopeDowngrade
                && string.Equals(facilityId, scopeFacility, StringComparison.Ordinal);
        }

        private static string ScopeLabel(bool downgrade)
        {
            return downgrade ? "FacilityDowngrade" : "FacilityUpgrade";
        }

        private static string StockCallName(bool downgrade)
        {
            return downgrade ? "DowngradeFacility" : "UpgradeFacility";
        }

        // ================================================================
        // Pure decisions
        // ================================================================

        /// <summary>
        /// Pure: the funds movement of one <c>FundsChanged(StructureConstruction)</c> firing.
        /// The recorder's own baseline is the "before" value, so the captured cost equals the
        /// FundsChanged event the recorder writes. When that baseline is still unseeded (NaN)
        /// on the scope's first firing, the pool read when the scope opened stands in (nothing
        /// moves funds between the prefix and stock's AddFunds); a later NaN firing cannot be
        /// measured and yields NaN.
        /// </summary>
        internal static double ResolveObservedFundsDelta(
            double oldFunds, double newFunds, double fundsAtOpen, bool firstObservation)
        {
            double before = !double.IsNaN(oldFunds)
                ? oldFunds
                : (firstObservation ? fundsAtOpen : double.NaN);
            return newFunds - before;
        }

        /// <summary>
        /// Pure: the FacilityCost of one upgrade from what the scope observed. 0 when the call
        /// deducts no funds (Science / Sandbox, <c>deduceFunds=false</c>), when no debit was
        /// observed, when the debit could not be measured, or when the observed movement is not
        /// a debit (a credit would read as a refund).
        /// </summary>
        internal static float ResolveUpgradeCost(
            bool fundsCharged, int observedDebits, double observedFundsDelta, out string costSource)
        {
            if (!fundsCharged)
            {
                costSource = SourceNoFundsCharged;
                return 0f;
            }
            if (observedDebits <= 0)
            {
                costSource = SourceNoDebitObserved;
                return 0f;
            }
            if (double.IsNaN(observedFundsDelta) || double.IsInfinity(observedFundsDelta))
            {
                costSource = SourceUnmeasuredDebit;
                return 0f;
            }
            if (observedFundsDelta >= 0.0)
            {
                costSource = SourceNonDebit;
                return 0f;
            }
            costSource = SourceObservedDebit;
            return (float)(-observedFundsDelta);
        }

        /// <summary>Pure: the FacilityUpgraded event detail read by <c>ConvertFacilityUpgraded</c>.</summary>
        internal static string BuildUpgradeDetail(float cost)
        {
            return "cost=" + cost.ToString("R", IC);
        }

        // ================================================================
        // Scope (driven by the UpgradeFacility Harmony patch)
        // ================================================================

        /// <summary>
        /// The prefix's decision after the committed-upgrade block ran: a refused upgrade skips
        /// stock's method (no debit, no level change) and opens no scope; an allowed one opens
        /// the scope and lets the original run. Returns the value the Harmony prefix returns.
        /// </summary>
        internal static bool AllowAndOpenScope(
            bool blocked, string facility, bool fundsCharged, double fundsAtOpen)
        {
            if (blocked)
            {
                ParsekLog.Verbose(Tag,
                    $"FacilityUpgrade scope not opened: facility='{facility ?? "(null)"}' - " +
                    "the committed-upgrade block refused the upgrade (no debit, no level change)");
                return false;
            }
            BeginScope(facility, fundsCharged, fundsAtOpen);
            return true;
        }

        /// <summary>
        /// Opens the upgrade scope, or with <paramref name="downgrade"/> the downgrade scope.
        /// A scope left open by an interrupted call is closed first, so no queued row is ever
        /// lost.
        /// </summary>
        internal static void BeginScope(string facility, bool fundsCharged, double fundsAtOpen, bool downgrade = false)
        {
            if (scopeActive)
            {
                ParsekLog.Warn(Tag,
                    $"{ScopeLabel(scopeDowngrade)} scope: '{scopeFacility ?? "(null)"}' was still open when " +
                    $"'{facility ?? "(null)"}' began - closing it first");
                EndScope("reentered");
            }

            scopeActive = true;
            scopeFacility = facility;
            scopeFundsCharged = fundsCharged;
            scopeFundsAtOpen = fundsCharged ? fundsAtOpen : double.NaN;
            scopeObservedDebits = 0;
            scopeObservedDelta = 0.0;
            scopeCostConsumed = false;
            scopeDowngrade = downgrade;
            scopePendingForward.Clear();

            ParsekLog.Verbose(Tag,
                $"{ScopeLabel(downgrade)} scope open: facility='{facility ?? "(null)"}' " +
                $"fundsCharged={fundsCharged.ToString(IC)} " +
                $"fundsAtOpen={scopeFundsAtOpen.ToString("R", IC)}");
        }

        /// <summary>
        /// Called by the recorder's OnFundsChanged handler for every funds change. Inside an
        /// open scope whose cost has not been read yet, a StructureConstruction change is
        /// accumulated as stock's upgrade debit; every other reason, and everything outside a
        /// scope, is ignored.
        /// </summary>
        internal static void ObserveFundsChange(TransactionReasons reason, double oldFunds, double newFunds)
        {
            if (!scopeActive || scopeCostConsumed || reason != TransactionReasons.StructureConstruction)
                return;

            double delta = ResolveObservedFundsDelta(
                oldFunds, newFunds, scopeFundsAtOpen, scopeObservedDebits == 0);
            scopeObservedDebits++;
            scopeObservedDelta += delta;

            ParsekLog.Verbose(Tag,
                $"{ScopeLabel(scopeDowngrade)} scope: StructureConstruction funds change observed " +
                $"facility='{scopeFacility ?? "(null)"}' delta={delta.ToString("R", IC)} " +
                $"total={scopeObservedDelta.ToString("R", IC)} " +
                $"firings={scopeObservedDebits.ToString(IC)}");
        }

        /// <summary>
        /// Reads the upgrade's cost for the FacilityUpgraded event of
        /// <paramref name="facilityId"/>. Only the first read inside the scope, for the
        /// facility the scope opened on, gets the observed debit; every other read gets 0
        /// with the reason in <paramref name="costSource"/>.
        /// </summary>
        internal static float ConsumeCostForUpgrade(string facilityId, out string costSource)
        {
            return ConsumeCost(facilityId, false, out costSource);
        }

        /// <summary>
        /// Reads the downgrade's cost for the FacilityDowngraded event of
        /// <paramref name="facilityId"/>. False (cost 0, nothing consumed) unless a
        /// <c>DowngradeFacility</c> scope on that facility is open
        /// (<see cref="IsDowngradeScopeOpenFor"/>): a level drop no such call made is not the
        /// player's paid downgrade, and the caller leaves its event informational. Inside the
        /// scope the first read gets the observed debit, as for an upgrade.
        /// </summary>
        internal static bool TryConsumeCostForDowngrade(string facilityId, out float cost, out string costSource)
        {
            if (!IsDowngradeScopeOpenFor(facilityId))
            {
                cost = 0f;
                costSource = SourceNoScope;
                ParsekLog.Verbose(Tag,
                    $"FacilityDowngrade cost: '{facilityId ?? "(null)"}' dropped a level outside a " +
                    "DowngradeFacility call on it (no stock debit to read) - informational, no ledger row");
                return false;
            }
            cost = ConsumeCost(facilityId, true, out costSource);
            return true;
        }

        private static float ConsumeCost(string facilityId, bool downgrade, out string costSource)
        {
            string label = ScopeLabel(downgrade);
            string call = StockCallName(downgrade);
            if (!scopeActive)
            {
                costSource = SourceNoScope;
                ParsekLog.Verbose(Tag,
                    $"{label} cost: '{facilityId ?? "(null)"}' changed level outside an " +
                    $"{call} call (no stock debit to read) - cost 0");
                return 0f;
            }
            if (!string.Equals(facilityId, scopeFacility, StringComparison.Ordinal))
            {
                costSource = SourceFacilityMismatch;
                ParsekLog.Warn(Tag,
                    $"{label} cost: '{facilityId ?? "(null)"}' changed level inside the " +
                    $"{StockCallName(scopeDowngrade)} scope of '{scopeFacility ?? "(null)"}' - cost 0");
                return 0f;
            }
            if (scopeDowngrade != downgrade)
            {
                costSource = SourceDirectionMismatch;
                ParsekLog.Warn(Tag,
                    $"{label} cost: '{facilityId}' changed level the other way inside its own " +
                    $"{StockCallName(scopeDowngrade)} scope - cost 0");
                return 0f;
            }
            if (scopeCostConsumed)
            {
                costSource = SourceAlreadyConsumed;
                ParsekLog.Warn(Tag,
                    $"{label} cost: a second level change of '{facilityId}' inside one " +
                    $"{call} call - the debit was already read, cost 0");
                return 0f;
            }

            float cost = ResolveUpgradeCost(
                scopeFundsCharged, scopeObservedDebits, scopeObservedDelta, out costSource);
            scopeCostConsumed = true;

            if (scopeFundsCharged && costSource != SourceObservedDebit)
            {
                ParsekLog.Warn(Tag,
                    $"{label} cost: '{facilityId}' was charged funds but the debit could not " +
                    $"be read (costSource={costSource} firings={scopeObservedDebits.ToString(IC)} " +
                    $"delta={scopeObservedDelta.ToString("R", IC)}) - cost 0");
            }
            else
            {
                ParsekLog.Verbose(Tag,
                    $"{label} cost: '{facilityId}' cost={cost.ToString("R", IC)} " +
                    $"costSource={costSource}");
            }
            return cost;
        }

        /// <summary>
        /// Queues a direct-ledger building-repair row (a free repair by <c>ResetStructures</c>)
        /// for the batch the upgrade row is written in. Returns false (the caller forwards it
        /// immediately) when no scope is open.
        /// </summary>
        internal static bool TryDeferForward(GameStateEvent evt)
        {
            if (!scopeActive)
                return false;
            scopePendingForward.Add(evt);
            return true;
        }

        /// <summary>Hands over (and clears) the rows queued so far in the open scope.</summary>
        internal static List<GameStateEvent> TakePendingForward()
        {
            var pending = new List<GameStateEvent>(scopePendingForward);
            scopePendingForward.Clear();
            return pending;
        }

        /// <summary>
        /// Closes the scope. Rows still queued (the upgrade event never came, or was not the
        /// recorder's to forward) are written in one batch, and a debit that no FacilityUpgraded
        /// event read is reported: the ledger then has no row for that spend.
        /// </summary>
        internal static void EndScope(string reason)
        {
            if (!scopeActive)
                return;

            var pending = new List<GameStateEvent>(scopePendingForward);
            string facility = scopeFacility;
            bool fundsCharged = scopeFundsCharged;
            int debits = scopeObservedDebits;
            double delta = scopeObservedDelta;
            bool consumed = scopeCostConsumed;
            bool downgrade = scopeDowngrade;

            scopeActive = false;
            scopeFacility = null;
            scopeFundsCharged = false;
            scopeFundsAtOpen = double.NaN;
            scopeObservedDebits = 0;
            scopeObservedDelta = 0.0;
            scopeCostConsumed = false;
            scopeDowngrade = false;
            scopePendingForward.Clear();

            if (!consumed && debits > 0)
            {
                ParsekLog.Warn(Tag,
                    $"{ScopeLabel(downgrade)} scope: stock moved funds by {delta.ToString("R", IC)} " +
                    $"(StructureConstruction) for '{facility ?? "(null)"}' but no " +
                    $"{(downgrade ? "FacilityDowngraded" : "FacilityUpgraded")} " +
                    "event read it - the ledger carries no row for this spend");
            }

            ParsekLog.Verbose(Tag,
                $"{ScopeLabel(downgrade)} scope close: facility='{facility ?? "(null)"}' " +
                $"reason={reason ?? "(none)"} fundsCharged={fundsCharged.ToString(IC)} " +
                $"firings={debits.ToString(IC)} delta={delta.ToString("R", IC)} " +
                $"costRead={consumed.ToString(IC)} rowsToWrite={pending.Count.ToString(IC)}");

            if (pending.Count > 0)
                LedgerOrchestrator.OnKscSpendingBatch(
                    pending, downgrade ? "facility-downgrade-reset" : "facility-upgrade-reset");
        }

        internal static void ResetForTesting()
        {
            scopeActive = false;
            scopeFacility = null;
            scopeFundsCharged = false;
            scopeFundsAtOpen = double.NaN;
            scopeObservedDebits = 0;
            scopeObservedDelta = 0.0;
            scopeCostConsumed = false;
            scopeDowngrade = false;
            scopePendingForward.Clear();
        }
    }
}
