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

        private static bool scopeActive;
        private static string scopeFacility;
        private static bool scopeFundsCharged;
        private static double scopeFundsAtOpen = double.NaN;
        private static int scopeObservedDebits;
        private static double scopeObservedDelta;
        private static bool scopeCostConsumed;
        private static readonly List<GameStateEvent> scopePendingForward = new List<GameStateEvent>();

        /// <summary>True while a <c>SpaceCenterBuilding.UpgradeFacility</c> call is on the stack.</summary>
        internal static bool IsScopeActive => scopeActive;

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
        /// Opens the upgrade scope. A scope left open by an interrupted call is closed first,
        /// so no queued row is ever lost.
        /// </summary>
        internal static void BeginScope(string facility, bool fundsCharged, double fundsAtOpen)
        {
            if (scopeActive)
            {
                ParsekLog.Warn(Tag,
                    $"FacilityUpgrade scope: '{scopeFacility ?? "(null)"}' was still open when " +
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
            scopePendingForward.Clear();

            ParsekLog.Verbose(Tag,
                $"FacilityUpgrade scope open: facility='{facility ?? "(null)"}' " +
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
                $"FacilityUpgrade scope: StructureConstruction funds change observed " +
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
            if (!scopeActive)
            {
                costSource = SourceNoScope;
                ParsekLog.Verbose(Tag,
                    $"FacilityUpgrade cost: '{facilityId ?? "(null)"}' changed level outside an " +
                    "UpgradeFacility call (no stock debit to read) - cost 0");
                return 0f;
            }
            if (!string.Equals(facilityId, scopeFacility, StringComparison.Ordinal))
            {
                costSource = SourceFacilityMismatch;
                ParsekLog.Warn(Tag,
                    $"FacilityUpgrade cost: '{facilityId ?? "(null)"}' changed level inside the " +
                    $"UpgradeFacility scope of '{scopeFacility ?? "(null)"}' - cost 0");
                return 0f;
            }
            if (scopeCostConsumed)
            {
                costSource = SourceAlreadyConsumed;
                ParsekLog.Warn(Tag,
                    $"FacilityUpgrade cost: a second level change of '{facilityId}' inside one " +
                    "UpgradeFacility call - the debit was already read, cost 0");
                return 0f;
            }

            float cost = ResolveUpgradeCost(
                scopeFundsCharged, scopeObservedDebits, scopeObservedDelta, out costSource);
            scopeCostConsumed = true;

            if (scopeFundsCharged && costSource != SourceObservedDebit)
            {
                ParsekLog.Warn(Tag,
                    $"FacilityUpgrade cost: '{facilityId}' was charged funds but the debit could not " +
                    $"be read (costSource={costSource} firings={scopeObservedDebits.ToString(IC)} " +
                    $"delta={scopeObservedDelta.ToString("R", IC)}) - cost 0");
            }
            else
            {
                ParsekLog.Verbose(Tag,
                    $"FacilityUpgrade cost: '{facilityId}' cost={cost.ToString("R", IC)} " +
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

            scopeActive = false;
            scopeFacility = null;
            scopeFundsCharged = false;
            scopeFundsAtOpen = double.NaN;
            scopeObservedDebits = 0;
            scopeObservedDelta = 0.0;
            scopeCostConsumed = false;
            scopePendingForward.Clear();

            if (!consumed && debits > 0)
            {
                ParsekLog.Warn(Tag,
                    $"FacilityUpgrade scope: stock moved funds by {delta.ToString("R", IC)} " +
                    $"(StructureConstruction) for '{facility ?? "(null)"}' but no FacilityUpgraded " +
                    "event read it - the ledger carries no row for this spend");
            }

            ParsekLog.Verbose(Tag,
                $"FacilityUpgrade scope close: facility='{facility ?? "(null)"}' " +
                $"reason={reason ?? "(none)"} fundsCharged={fundsCharged.ToString(IC)} " +
                $"firings={debits.ToString(IC)} delta={delta.ToString("R", IC)} " +
                $"costRead={consumed.ToString(IC)} rowsToWrite={pending.Count.ToString(IC)}");

            if (pending.Count > 0)
                LedgerOrchestrator.OnKscSpendingBatch(pending, "facility-upgrade-reset");
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
            scopePendingForward.Clear();
        }
    }
}
