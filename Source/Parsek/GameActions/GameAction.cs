using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Discriminator for GameAction — one value per action schema from the design doc.
    /// Explicit int values for serialization stability.
    /// </summary>
    public enum GameActionType
    {
        ScienceEarning        = 0,
        ScienceSpending       = 1,
        FundsEarning          = 2,
        FundsSpending         = 3,
        MilestoneAchievement  = 4,
        ContractAccept        = 5,
        ContractComplete      = 6,
        ContractFail          = 7,
        ContractCancel        = 8,
        ReputationEarning     = 9,
        ReputationPenalty     = 10,
        KerbalAssignment      = 11,
        KerbalHire            = 12,
        KerbalRescue          = 13,
        KerbalStandIn         = 14,
        FacilityUpgrade       = 15,
        FacilityDestruction   = 16,
        FacilityRepair        = 17,
        StrategyActivate      = 18,
        StrategyDeactivate    = 19,
        FundsInitial          = 20,
        ScienceInitial        = 21,
        ReputationInitial     = 22,

        // ---- Reserved: legacy supply-route rows ----
        //
        // Supply routes are a real-time system whose events live in the route event log
        // (Logistics.RouteEventLog, persisted with the routes in the PARSEK node; todo
        // LOGISTICS-WITHOUT-MONEY phase 2). No ledger row of these types is ever written,
        // and an old save's rows are dropped at ledger load with one Info summary
        // (LedgerLoadMigration.IsLegacyRouteNode). The ids stay defined so an old row
        // never reads as type 0 and the numbers are never reused; the route event log's
        // Logistics.RouteEventType keeps the same names and numbers.

        /// <summary>Reserved (legacy supply-route row, dropped at ledger load).</summary>
        RouteDispatched       = 23,
        /// <summary>Reserved (legacy supply-route row, dropped at ledger load).</summary>
        RouteCargoDebited     = 24,
        /// <summary>Reserved (legacy supply-route row, dropped at ledger load).</summary>
        RouteCargoDelivered   = 25,
        /// <summary>Reserved (legacy supply-route row, dropped at ledger load).</summary>
        RoutePaused           = 26,
        /// <summary>Reserved (legacy supply-route row, dropped at ledger load).</summary>
        RouteEndpointLost     = 27,
        /// <summary>Reserved (legacy supply-route money row, dropped at ledger load).</summary>
        RouteRecoveryCredited = 28,
        /// <summary>Reserved (legacy supply-route row, dropped at ledger load).</summary>
        RouteCargoPickedUp    = 29,
        /// <summary>Reserved (legacy supply-route row, dropped at ledger load).</summary>
        RouteResumed          = 30,

        /// <summary>
        /// A kerbal's career-log entries were archived by a recovery (design: the P9a
        /// kerbal-XP facet). Carries <see cref="GameAction.KerbalName"/> and the encoded
        /// entry set in <see cref="GameAction.KerbalCareerEntries"/>. Explicitly numbered and
        /// append-only. Not resource-impacting: XP is derived from the career log, not paid
        /// out of any pool, so it never enters a funds/science/reputation reconciliation.
        /// </summary>
        KerbalExperience = 31,

        /// <summary>
        /// The SCIENCE INPUT leg of a stock <c>CurrencyExchanger</c> /
        /// <c>CurrencyConverter</c> strategy exchange (Patents Licensing,
        /// <c>researchIPsellout</c>): the strategy moved science OUT of the pool and
        /// credited another currency. Captured straight from the
        /// <c>ScienceChanged</c> event keyed <c>TransactionReasons.StrategyInput</c>
        /// (the mirror of the funds OUTPUT leg's
        /// <see cref="FundsEarningSource.Strategy"/> and the reputation INPUT leg's
        /// <see cref="ReputationPenaltySource.Strategy"/>), because no other channel
        /// captures it and the strategy's own <c>InitialCostScience</c> setup charge is
        /// separate (that one rides <see cref="StrategyActivate"/>).
        ///
        /// <para><see cref="GameAction.Cost"/> carries the POSITIVE magnitude KSP has
        /// already removed from the pool, so <see cref="ScienceModule"/> replays it as an
        /// UNCONDITIONAL debit and never re-derives or re-caps it. It is NOT a tech-node
        /// spending: it carries no <see cref="GameAction.NodeId"/>, and every
        /// tech-node-domain reader (<c>KspStatePatcher</c>'s unlock set,
        /// <c>LedgerGroundTruth</c>'s researched-node derivation,
        /// <c>SupersedeCommit</c>'s tech exclusion) stays
        /// <see cref="ScienceSpending"/>-only by construction.</para>
        ///
        /// <para>No strategy id is carried: the <c>ScienceChanged</c> event's key is the
        /// transaction reason and no strategy identity is available at that seam (the
        /// funds leg has the same limitation).</para>
        ///
        /// <para>TWO producers write this type, told apart by
        /// <see cref="GameAction.ConversionSource"/>:
        /// <see cref="StrategyConversionSource.Exchanger"/> for the reason-keyed
        /// door above, and <see cref="StrategyConversionSource.Converter"/> for the
        /// query-family door (<c>StrategyConversionCapture</c>), which observes
        /// <c>GameEvents.Modifiers.OnCurrencyModified</c> because that family leaves no
        /// StrategyInput event at all. The discriminator is load-bearing, not cosmetic:
        /// <c>LedgerOrchestrator.ComputePendingUncommittedStrategyScienceDebit</c>
        /// nets an OBSERVED population (stored StrategyInput events) against a
        /// COMMITTED one, and a converter row has no observed counterpart, so counting
        /// it would silently shrink a genuine pending exchanger adjustment.</para>
        /// </summary>
        StrategyScienceDebit = 32,

        /// <summary>
        /// The science OUTPUT leg of a stock <c>Strategies.Effects.CurrencyConverter</c>
        /// (Open-Source Tech Program and siblings): the strategy YIELDED science into
        /// the pool out of another currency. <see cref="GameAction.ScienceAwarded"/>
        /// carries the positive magnitude KSP has already added.
        ///
        /// <para>A SIBLING TYPE rather than a negative-<c>Cost</c>
        /// <see cref="StrategyScienceDebit"/>, and rather than a subject-less
        /// <see cref="ScienceEarning"/>. <see cref="ScienceEarning"/> genuinely cannot
        /// express it: <c>ScienceModule.ProcessEarning</c> computes
        /// <c>headroom = SubjectMaxValue - creditedTotal</c> and credits
        /// <c>min(awarded, headroom)</c>, so a row with no subject (max 0) is zeroed
        /// to nothing. A negative <c>Cost</c> would serialize and DISPLAY dishonestly
        /// (the field is named Cost, the row renders as "Strategy exchange --5 sci")
        /// and would flow as a negative into
        /// <c>ScienceModule.ComputeTotalSpendings</c>, silently RAISING spendable
        /// science through the reservation math. A distinct type keeps the sign in the
        /// type and the magnitude positive everywhere.</para>
        ///
        /// <para>Converter-only by construction, so it carries no
        /// <see cref="GameAction.ConversionSource"/> discriminator: the exchanger
        /// family's science leg is always a debit (its output currency is funds or
        /// reputation).</para>
        /// </summary>
        StrategyScienceCredit = 33,

        /// <summary>
        /// A kerbal came home: KSP recovered a REAL vessel that is the live continuation
        /// of a committed recording (the same launch by positive guid match, or the vessel
        /// Parsek spawned from it) with this kerbal aboard. Carries
        /// <see cref="GameAction.KerbalName"/> (the reservation owner's name, reverse-mapped
        /// from a seated stand-in) and <see cref="GameAction.RecordingId"/> (the owning
        /// committed recording); the row's <see cref="GameAction.UT"/> is the recovery
        /// instant.
        ///
        /// <para>Design 9.3: a STRANDED (Aboard) or Unknown hold stays open "until a rescue
        /// recording closes it". This row is that closure for the case the recording itself
        /// cannot express: with auto-merge on, an in-flight Recover commits the flight at the
        /// scene change BEFORE stock recovers the vessel, so the committed recording ends
        /// Landed with crew aboard, and committed recordings are never re-stamped.
        /// <see cref="KerbalsModule"/> ends this kerbal's open-ended holds from the owner's
        /// tree (flights ending at or before the recovery) at this UT, so the reservation
        /// becomes UT 0 -> recovery UT and a rewind to before it holds him again. Not
        /// resource-impacting; retired with the owner recording by a re-fly supersede.</para>
        /// </summary>
        KerbalRecovered = 34,

        /// <summary>
        /// The player recovered a REAL vessel that is the live continuation of a committed
        /// recording (the same launch by positive guid match, or the vessel Parsek spawned
        /// from it): from the Tracking Station, the KSC vessel marker, the flight Recover
        /// button or kRPC. Carries <see cref="GameAction.RecordingId"/> (the owning committed
        /// recording, one row per owning tree), <see cref="GameAction.RecoveredVesselName"/>
        /// and <see cref="GameAction.RecoveredVesselPid"/>; the row's
        /// <see cref="GameAction.UT"/> is the recovery instant.
        ///
        /// <para>Non-economic and written in every game mode. It exists because the other two
        /// recovery rows are conditional: <see cref="FundsEarning"/> with
        /// <see cref="FundsEarningSource.Recovery"/> needs stock's
        /// <c>FundsChanged(VesselRecovery)</c>, which a sandbox save never raises, and
        /// <see cref="KerbalRecovered"/> needs crew aboard. A committed recording is never
        /// re-stamped by a terminal event, so without this row an uncrewed sandbox recovery
        /// of a Parsek-spawned vessel left no trace and the next flight scene spawned it
        /// again. Read by <see cref="RecoveredRecordingEvidence"/> beside the other two.</para>
        ///
        /// <para>Player recoveries only: Parsek's own programmatic recoveries run under
        /// <c>SuppressionGuard.Crew()</c> and write none. Not resource-impacting, not a
        /// strict-block for a supersede, retired with its owner by a Re-Fly supersede and
        /// with the recovery funds row by a Re-Fly resurrection. Explicitly numbered and
        /// append-only; an older build reads the value as an unknown type (Warn) and the
        /// row's evidence is lost there, the accepted one-way downgrade of an additive
        /// enum member.</para>
        /// </summary>
        VesselRecovered = 35,

        /// <summary>Reserved (legacy supply-route row, dropped at ledger load).</summary>
        RouteHeld = 36,

        /// <summary>
        /// A resource scan unlocked a body (orbital survey, <see cref="GameAction.ScanBiomeName"/>
        /// null) or one biome of it (surface scanner) in stock's <c>ResourceMap</c>
        /// (owner ruling 2026-10-06, RESOURCE-SCAN-UNLOCKS-NOT-LEDGERED). Carries
        /// <see cref="GameAction.ScanBodyName"/> and <see cref="GameAction.ScanBiomeName"/>.
        /// Moves no pool; the state patcher re-applies every effective row at or before the
        /// live clock, add-only (stock has no re-lock, and a rewind's quicksave already
        /// predates a later scan). Retired with its owner by a Re-Fly supersede, never a
        /// strict-block. Explicitly numbered and append-only; an older build reads it as an
        /// unknown type (Warn) and loses only the re-apply there.
        /// </summary>
        ResourceScanUnlocked = 37,

        /// <summary>
        /// Science the Mobile Processing Lab credited to the pool with no science subject:
        /// a lab transmission (<c>ModuleScienceLab.OnTransmissionComplete</c>,
        /// <see cref="ScienceMethod.Transmitted"/>) or the lab's stored science paid out on
        /// recovery (<c>ModuleScienceLab.RecoverScienceLabs</c>, <see cref="ScienceMethod.Recovered"/>).
        /// Also the orbital survey's bonus (<c>ModuleOrbitalSurveyor.CompleteSurvey</c>,
        /// <see cref="ScienceMethod.Transmitted"/>, with the surveyed body in
        /// <see cref="GameAction.ScanBodyName"/>), the one other subject-less science stock pays.
        /// Stock adds it straight through <c>AddScience</c>, so no <c>OnScienceRecieved</c>
        /// and no <see cref="ScienceEarning"/> row exist for it (decompiled KSP 1.12.5).
        /// Carries the pool credit in <see cref="GameAction.ScienceAwarded"/> and is applied
        /// uncapped (there is no subject to cap against). Retired with its owner by a Re-Fly
        /// supersede. Explicitly numbered and append-only; an older build reads it as an
        /// unknown type (Warn) and loses only the credit there.
        /// </summary>
        ScienceLabEarning = 38,

        /// <summary>
        /// Something stock applied to one pool that the ledger holds no other row for: the
        /// drawdown guard's measured leak (committed-value rule plan section 2.4), or an
        /// older save's gap between its pool and the leak-free target, adopted once. Carries
        /// <see cref="GameAction.AdjustmentPool"/> and <see cref="GameAction.AdjustmentAmount"/>
        /// (reputation stored post-curve, applied as stored). Untagged, at the patch's now,
        /// written by <see cref="StockPoolReference"/>'s patch through
        /// <see cref="LedgerRowWriter.StockApplied"/> and counted at creation, so the reference
        /// never steps it. Exempt from stock's 0 clamp in the walk (the amount is what the
        /// live pool already shows). An earning for ordering and pruning, so a cold load's
        /// reconcile keeps it. Explicitly numbered and append-only; an older build reads it
        /// as an unknown type (Warn) and loses the adjustment there.
        /// </summary>
        StockPoolAdjustment = 39,

        /// <summary>
        /// One contract parameter's own completion reward (todo
        /// CONTRACT-PARAMETER-REWARDS-NOT-LEDGERED). Stock pays a contract in two parts: every
        /// <c>ContractParameter</c> that completes pays its <c>FundsCompletion</c> /
        /// <c>ScienceCompletion</c> / <c>ReputationCompletion</c> (reason <c>ContractReward</c>)
        /// the moment it completes, and the contract then pays its own reward, which
        /// <see cref="ContractComplete"/> carries. Carries <see cref="GameAction.ContractId"/>,
        /// <see cref="GameAction.ContractParameterId"/> (the parameter's index path in the
        /// contract), <see cref="GameAction.ContractTitle"/> and the three rewards in
        /// <see cref="GameAction.FundsReward"/> / <see cref="GameAction.RepReward"/> (nominal,
        /// pre-curve) / <see cref="GameAction.ScienceReward"/> (pool credit, no subject).
        /// Credited once per (contract, parameter); never moves a contract slot or its terminal
        /// state. Tagged, tombstoned and re-homed like a <see cref="ContractComplete"/> row.
        /// Explicitly numbered and append-only; an older build reads it as an unknown type
        /// (Warn) and loses the credit there.
        /// </summary>
        ContractParameterComplete = 40
    }

    /// <summary>
    /// Which stock strategy mechanism produced a strategy currency-conversion row.
    /// Default <see cref="Exchanger"/> so rows written before the query-family door
    /// existed - and every row whose producer does not set the field - keep the
    /// original meaning on load.
    /// </summary>
    public enum StrategyConversionSource
    {
        /// <summary>
        /// <c>Strategies.CurrencyExchanger</c> (Bail-Out Grant,
        /// <c>researchIPsellout</c>): a one-shot direct <c>Add*</c> under
        /// <c>TransactionReasons.StrategyInput</c>/<c>StrategyOutput</c>, captured from
        /// the resulting reason-keyed GameStateEvent.
        /// </summary>
        Exchanger = 0,

        /// <summary>
        /// <c>Strategies.Effects.CurrencyConverter</c> /
        /// <c>Strategies.Effects.CurrencyOperation</c>: an in-place mutation of a
        /// <c>CurrencyModifierQuery</c>, captured from
        /// <c>GameEvents.Modifiers.OnCurrencyModified</c>. Leaves NO StrategyInput /
        /// StrategyOutput event behind, so no stored event corroborates the row.
        /// </summary>
        Converter = 1
    }

    /// <summary>How science was collected — transmitted from orbit or recovered on the ground.</summary>
    public enum ScienceMethod
    {
        Transmitted = 0,
        Recovered   = 1
    }

    /// <summary>Where fund earnings came from.</summary>
    public enum FundsEarningSource
    {
        ContractComplete = 0,
        ContractAdvance  = 1,
        Recovery         = 2,
        Milestone        = 3,
        Other            = 4,
        /// <summary>
        /// Synthetic earning tag retained for ledger rows that still carry it.
        /// Tag-only — <see cref="FundsModule"/> treats it as a normal earning via its
        /// default branch. The on-load injector that once emitted this tag was removed
        /// with the schema generation 3 reset.
        /// </summary>
        LegacyMigration  = 5,
        /// <summary>
        /// Funds credited by a stock strategy currency exchange (Bail-Out Grant's
        /// <c>CurrencyExchanger</c> output, <c>TransactionReasons.StrategyOutput</c>).
        /// Captured directly from the <c>FundsChanged</c> event because the exchange
        /// is separate from the strategy's (zero) <c>InitialCost*</c> setup cost.
        /// </summary>
        Strategy         = 6
    }

    /// <summary>Where funds were spent.</summary>
    public enum FundsSpendingSource
    {
        VesselBuild      = 0,
        FacilityUpgrade  = 1,
        FacilityRepair   = 2,
        KerbalHire       = 3,
        ContractPenalty  = 4,
        /// <summary>
        /// The EXCHANGER family's funds INPUT leg (a <c>Strategies.CurrencyExchanger</c>
        /// spending funds under <c>TransactionReasons.StrategyInput</c>). Reason-keyed
        /// and captured directly from the <c>FundsChanged</c> event, so
        /// <c>KscActionExpectationClassifier</c> skips it rather than pairing it.
        /// </summary>
        Strategy         = 5,
        Other            = 6,
        /// <summary>
        /// The QUERY family's funds DEBIT leg, captured by the query-family door in
        /// <c>LedgerOrchestrator.BuildStrategyConversionAction</c>: a
        /// <c>Strategies.Effects.CurrencyConverter</c> with <c>input = Funds</c>
        /// (<c>AppreciationCampaignCfg</c> funds -> reputation,
        /// <c>OutsourcedResearchCfg</c> funds -> science) or a
        /// <c>Strategies.Effects.CurrencyOperation</c> scaling funds DOWN
        /// (<c>LeadershipInitiative</c>'s 1.00..0.25 multiplier on contract gains),
        /// diverting part of an ordinary transaction by mutating its
        /// <c>CurrencyModifierQuery</c> in place.
        ///
        /// <para><b>ONLY EVER WRITTEN UNDER A NOMINAL-CHANNEL REASON</b>
        /// (<c>ContractReward</c> / <c>ContractAdvance</c> / <c>Progression</c> - see
        /// <c>StrategyConversionCapture.IsNominalChannelFundsReason</c>), where the
        /// ordinary funds channel recorded the CONFIGURED GROSS amount and this row is
        /// the missing second half. Under the event-derived reasons the channel already
        /// reports the value net and no leg is emitted at all.</para>
        ///
        /// <para>Distinct from <see cref="Strategy"/> because the two are different
        /// mechanisms with different reconcile standings: that one has a reason-keyed
        /// <c>StrategyInput</c> event behind it, this one has NO event of its own - the
        /// <c>FundsChanged</c> that follows carries the ORIGINAL reason. So
        /// <c>KscActionExpectationClassifier</c> skips it with that reason stated at its
        /// own arm rather than inheriting the exchanger's, and
        /// <c>PostWalkActionReconciler</c> never sees it at all - it has no
        /// <c>FundsSpending</c> case, so every spending row falls to its
        /// <c>Reconcile = false</c> default by TYPE rather than by source.</para>
        /// </summary>
        StrategyConverter = 7,

        /// <summary>
        /// A part UPGRADE bought from the editor or R&amp;D part list tooltip
        /// (<see cref="GameStateEventType.PartUpgradePurchased"/>). <see cref="GameAction.DedupKey"/>
        /// carries the upgrade name. Kept apart from <see cref="Other"/> (part purchases, key =
        /// part name) because every reader of a part purchase row treats its key as an
        /// <c>AvailablePart</c> name. Stock charges it under the same
        /// <c>TransactionReasons.RnDPartPurchase</c> as a part purchase.
        /// </summary>
        PartUpgrade = 8
    }

    /// <summary>Where reputation earnings came from.</summary>
    public enum ReputationSource
    {
        ContractComplete = 0,
        Milestone        = 1,
        Other            = 2,
        /// <summary>
        /// The reputation OUTPUT leg of a stock <c>Strategies.Effects.CurrencyConverter</c>
        /// (Open-Source Tech Program, Appreciation Campaign), captured by the query-family
        /// door in <c>LedgerOrchestrator.BuildStrategyConversionAction</c>.
        ///
        /// <para>Its <c>NominalRep</c> is the query's PRE-curve effect delta -
        /// <c>qry.GetEffectDelta(Currency.Reputation)</c>, the very argument stock's
        /// <c>Reputation.OnCurrenciesModified</c> hands to <c>addReputation_granular</c> -
        /// so <c>ReputationModule.ProcessRepEarning</c>'s ordinary
        /// <c>ApplyReputationCurve</c> call reproduces the pool movement exactly. It is
        /// therefore a NOMINAL source like the other two, NOT a pre-curved one; the
        /// pre-curved arm on the penalty enum (<see cref="ReputationPenaltySource.Strategy"/>,
        /// the exchanger family's post-curve <c>ReputationChanged</c> capture) is a
        /// different mechanism and must not be confused with this.</para>
        /// </summary>
        Strategy         = 3
    }

    /// <summary>Where reputation penalties came from.</summary>
    public enum ReputationPenaltySource
    {
        ContractFail    = 0,
        ContractDecline = 1,
        KerbalDeath     = 2,
        /// <summary>
        /// The EXCHANGER family's reputation INPUT leg (Bail-Out Grant's
        /// <c>CurrencyExchanger</c>, <c>TransactionReasons.StrategyInput</c>), captured
        /// straight from the <c>ReputationChanged</c> event. Its
        /// <c>NominalPenalty</c> is ALREADY POST-CURVE - it is the magnitude KSP
        /// measured off its own pool - so <c>ReputationModule.ProcessRepPenalty</c>
        /// gives this source, and ONLY this source, a no-recurve shortcut.
        /// </summary>
        Strategy        = 3,
        Other           = 4,
        /// <summary>
        /// The QUERY family's reputation DEBIT leg, captured by the query-family door in
        /// <c>LedgerOrchestrator.BuildStrategyConversionAction</c>.
        ///
        /// <para><b>THE SOURCE COVERS BOTH QUERY-DIVERSION EFFECT KINDS, despite the
        /// name.</b> (1) <c>Strategies.Effects.CurrencyConverter</c> with
        /// <c>input = Reputation</c> - <c>FundraisingCampaign</c> reputation -> funds,
        /// <c>UnpaidResearchProgram</c> reputation -> science - diverting
        /// <c>GetInput(Reputation) * share</c> out of an ordinary reputation
        /// transaction. (2) <c>Strategies.Effects.CurrencyOperation</c> on Reputation -
        /// <c>LeadershipInitiative</c> (multiplier 1.00..0.25 by Factor under
        /// <c>ContractAdvance</c>/<c>ContractPenalty</c>/<c>ContractReward</c>), and
        /// <c>AgressiveNegotiations</c> - scaling the reputation DOWN, which is a
        /// negative effect delta on a positive input and lands here too. The credit
        /// sibling <see cref="ReputationSource.Strategy"/> covers the same two kinds in
        /// the other sign (a converter YIELD, and <c>LeadershipInitiative</c>'s
        /// 1.00..2.50 <c>Progression</c> operation). "Converter" in this member's name
        /// is the QUERY-FAMILY distinction from <see cref="Strategy"/> above, NOT a
        /// claim that only <c>CurrencyConverter</c> produces it.</para>
        ///
        /// <para>A DIFFERENT MECHANISM FROM <see cref="Strategy"/> ABOVE, and the
        /// distinction is the whole reason this is a separate member rather than a
        /// reuse. <c>NominalPenalty</c> here is the query's PRE-curve effect delta -
        /// the very argument stock's <c>Reputation.OnCurrenciesModified</c> hands to
        /// <c>addReputation_granular</c> - so it MUST run through
        /// <c>ApplyReputationCurve</c> like any nominal, at the reconstruction's own
        /// running rep. Taking <see cref="Strategy"/>'s no-recurve shortcut would apply
        /// a post-curve magnitude as if it were already effective and land the
        /// reconstruction on the wrong number. The sibling on the EARNING enum is
        /// <see cref="ReputationSource.Strategy"/>, which is nominal for the same
        /// reason.</para>
        ///
        /// <para>Magnitude is POSITIVE, like every other member here: the sign lives in
        /// the action TYPE, not in the field.</para>
        /// </summary>
        StrategyConverter = 5
    }

    // KerbalEndState enum is in KerbalEndState.cs (Aboard=0, Dead=1, Recovered=2, Unknown=3)

    /// <summary>
    /// Resource type identifier for strategy source/target fields.
    /// </summary>
    public enum StrategyResource
    {
        Funds      = 0,
        Science    = 1,
        Reputation = 2
    }

    /// <summary>
    /// Why the recalculation walk left a row not counted (<see cref="GameAction.Effective"/>
    /// false). Runtime only: stamped by the walk at the same sites that clear
    /// <see cref="GameAction.Effective"/>, reset with it, never serialized. The Timeline's
    /// row hover reads it to say why a greyed row paid or charged nothing.
    /// </summary>
    public enum GameActionNotCountedReason
    {
        None = 0,
        /// <summary>A contract completion after the contract was already credited.</summary>
        ContractAlreadyCompleted,
        /// <summary>A contract completion after the contract's deadline already failed it.</summary>
        ContractDeadlinePassed,
        /// <summary>A contract completion after an explicit fail or cancel resolved it.</summary>
        ContractAlreadyResolved,
        /// <summary>A contract fail or cancel after the contract had already ended.</summary>
        ContractOutcomeAfterEnd,
        /// <summary>A milestone already achieved earlier in the walk.</summary>
        MilestoneAlreadyAchieved,
        /// <summary>A facility repair of a building the walk has intact at that point (never
        /// destroyed, or already repaired).</summary>
        FacilityAlreadyIntact,
        /// <summary>A contract parameter reward the walk reaches after its contract's credited
        /// completion (design 8.3, 3.15 step 1).</summary>
        ContractParameterAfterCompletion,
        /// <summary>A world-record row every one of whose thresholds was already credited
        /// earlier in the walk (design 7.8, 3.15 step 4).</summary>
        RecordThresholdsAlreadyCredited
    }

    /// <summary>
    /// Union type for all game actions on the ledger timeline.
    /// Uses a single class with nullable/sentinel fields — the <see cref="Type"/> field
    /// discriminates which fields are populated. Simpler serialization than an inheritance hierarchy.
    /// </summary>
    public class GameAction
    {
        // ---- Common fields (all action types) ----

        /// <summary>
        /// Stable immutable identifier for this action (design doc section 5.6 +
        /// section 9). New actions auto-assign <c>"act_" + Guid.NewGuid("N")</c>
        /// at construction. Load-time migration re-hydrates a deterministic id
        /// for pre-feature actions via
        /// <see cref="ComputeLegacyActionId(double, GameActionType, string, int)"/>.
        /// Referenced by <see cref="LedgerTombstone.ActionId"/>.
        /// </summary>
        public string ActionId = "act_" + Guid.NewGuid().ToString("N");

        /// <summary>Universal time when the action occurred.</summary>
        public double UT;

        /// <summary>Discriminator — determines which fields are populated.</summary>
        public GameActionType Type;

        /// <summary>Recording that produced this action. Null for KSC spending actions and system-generated actions.</summary>
        public string RecordingId;

        /// <summary>Ordering within the same UT for spending actions. 0 for earnings.</summary>
        public int Sequence;

        /// <summary>
        /// Real-time capture order (<see cref="Parsek.CaptureSequence"/>): the source event's
        /// value for a row converted from a game-state event, else stamped when the ledger
        /// takes the row. 0 for a row from an older save (no capture order: nothing is ahead
        /// of it and it is ahead of nothing). Read by <see cref="ReservedFloorPlan"/>: a
        /// committed cost lies ahead of a loss only when it was captured before the loss.
        /// </summary>
        public long CaptureSequence;

        // ---- Derived fields (recalculated, NOT serialized) ----

        /// <summary>
        /// Whether this action's effects are active after recalculation.
        /// Set by first-tier modules (e.g., MilestonesModule sets false for duplicate milestones,
        /// ContractsModule sets false for duplicate completions). Defaults to true.
        /// NOT serialized — recomputed from scratch on every recalculation walk.
        /// </summary>
        public bool Effective = true;

        /// <summary>
        /// Why the walk left this row not counted; <see cref="GameActionNotCountedReason.None"/>
        /// on a counted row. Set by the same module sites that clear <see cref="Effective"/>,
        /// reset with it by <c>RecalculationEngine.ResetDerivedFields</c>. NOT serialized.
        /// </summary>
        public GameActionNotCountedReason NotCountedReason;

        /// <summary>
        /// True when <see cref="MilestonesModule"/> credited only part of a world-record row
        /// (some of its thresholds were credited earlier in the walk): the walk then pays
        /// <see cref="RecordCreditedFunds"/> / <see cref="RecordCreditedRep"/> /
        /// <see cref="RecordCreditedScience"/> instead of the captured totals. Derived, reset
        /// with <see cref="Effective"/>, NOT serialized.
        /// </summary>
        public bool RecordPartlyCredited;

        /// <summary>Funds a partly credited record row earns (derived; see <see cref="RecordPartlyCredited"/>).</summary>
        public float RecordCreditedFunds;

        /// <summary>Nominal reputation a partly credited record row earns (derived).</summary>
        public float RecordCreditedRep;

        /// <summary>Science a partly credited record row earns (derived).</summary>
        public float RecordCreditedScience;

        /// <summary>How many of a world-record row's thresholds the walk credited to it
        /// (derived; 0 on every other row).</summary>
        public int RecordThresholdsCredited;

        /// <summary>How many thresholds the world-record row stands for in the walk
        /// (derived; 0 on every other row).</summary>
        public int RecordThresholdsTotal;

        /// <summary>The funds a counted milestone row moves the pool by: the captured award, or
        /// the credited part of a partly credited world-record row.</summary>
        public float CreditedMilestoneFunds => RecordPartlyCredited ? RecordCreditedFunds : MilestoneFundsAwarded;

        /// <summary>The nominal reputation a counted milestone row earns (see <see cref="CreditedMilestoneFunds"/>).</summary>
        public float CreditedMilestoneRep => RecordPartlyCredited ? RecordCreditedRep : MilestoneRepAwarded;

        /// <summary>The science a counted milestone row earns (see <see cref="CreditedMilestoneFunds"/>).</summary>
        public float CreditedMilestoneScience => RecordPartlyCredited ? RecordCreditedScience : MilestoneScienceAwarded;

        // ---- Science fields ----

        /// <summary>Full KSP subject string, e.g. "crewReport@MunSrfLandedMidlands".</summary>
        public string SubjectId;

        /// <summary>Experiment type, e.g. "crewReport".</summary>
        public string ExperimentId;

        /// <summary>Celestial body name, e.g. "Mun".</summary>
        public string Body;

        /// <summary>KSP situation string, e.g. "SrfLanded".</summary>
        public string Situation;

        /// <summary>Biome name, e.g. "Midlands".</summary>
        public string Biome;

        /// <summary>
        /// Science points KSP added to the SUBJECT (immutable). Post-transmit-scalar and
        /// PRE-<see cref="ScienceGainMultiplier"/>: stock adds this to
        /// <c>subject.science</c>, so the per-subject cap walk and the committed-science
        /// cache run in these units. The pool received
        /// <see cref="GetScienceAwardedPoolCredit"/>. The walk treats it as the increment one
        /// submission added. Rows captured before SCIENCE-SUBJECT-RUNNING-TOTAL-OVER-CREDIT
        /// hold the subject's running total instead and over-credit repeated collections;
        /// by owner decision they are kept and walked unchanged.
        /// </summary>
        public float ScienceAwarded;

        /// <summary>How the science was collected.</summary>
        public ScienceMethod Method;

        /// <summary><see cref="GameActionType.StockPoolAdjustment"/>: the pool the row moves.</summary>
        public StockPool AdjustmentPool;

        /// <summary><see cref="GameActionType.StockPoolAdjustment"/>: the signed amount it moves
        /// the pool by (reputation post-curve).</summary>
        public double AdjustmentAmount;

        /// <summary>Transmission efficiency for the experiment (0.0 to 1.0).</summary>
        public float TransmitScalar;

        /// <summary>Total science this subject can yield (scienceCap).</summary>
        public float SubjectMaxValue;

        /// <summary>
        /// <c>Career.ScienceGainMultiplier</c> stamped when the subject was captured
        /// (stock <c>ResearchAndDevelopment.SubmitScienceData</c> multiplies the subject
        /// value by it before <c>AddScience</c>). Frozen at capture: a replay never reads
        /// the CURRENT difficulty, so changing the slider mid-career cannot rewrite past
        /// credits. Serialized sparsely (absent means 1).
        /// </summary>
        public float ScienceGainMultiplier = 1f;

        /// <summary>
        /// TRANSIENT, never serialized: true on a freshly converted <c>ScienceEarning</c>
        /// candidate whose <see cref="ActionId"/> is the per-callback capture identity
        /// (<see cref="PendingScienceSubject.captureActionId"/>). The ledger dedup then
        /// treats it as a re-file only of a row with the SAME <see cref="ActionId"/>, so a
        /// second award of one subject at the same instant is kept
        /// (SCIENCE-SAME-SUBJECT-SAME-INSTANT-ROW-DROPPED). False on every row read from
        /// disk and on any candidate built without a capture id, which keep the legacy
        /// subject + capture-moment match.
        /// </summary>
        internal bool HasScienceCaptureIdentity;

        /// <summary>
        /// TRANSIENT, never serialized: true on a candidate converted from one captured
        /// game-state event (<see cref="GameStateEventConverter.ConvertEvent"/>) whose event
        /// carries a capture number, so <see cref="CaptureSequence"/> names that capture.
        /// The ledger dedup then collapses it only onto a row of the SAME capture number
        /// (design 3.15 step 3): two captures with equal economic keys are two events and
        /// both stay. False on every row read from disk and on every row built without an
        /// event; a row it is compared against counts as a capture when its own number is
        /// positive. Either side without one keeps the legacy type + UT + key match.
        /// </summary>
        internal bool HasEventCaptureIdentity;

        // ---- Science spending fields ----

        /// <summary>Tech tree node ID, e.g. "survivability".</summary>
        public string NodeId;

        /// <summary>Cost in science points or funds (context-dependent on action type).</summary>
        public float Cost;

        /// <summary>
        /// Which stock strategy mechanism produced a
        /// <see cref="GameActionType.StrategyScienceDebit"/> row. Meaningless (and left
        /// at its <see cref="StrategyConversionSource.Exchanger"/> default) on every
        /// other action type. See the enum's doc for why the distinction is
        /// load-bearing.
        /// </summary>
        public StrategyConversionSource ConversionSource;

        // ---- Funds fields ----

        /// <summary>Funds earned (immutable).</summary>
        public float FundsAwarded;

        /// <summary>Source of fund earnings.</summary>
        public FundsEarningSource FundsSource;

        /// <summary>Funds spent (immutable).</summary>
        public float FundsSpent;

        /// <summary>Source of fund spending.</summary>
        public FundsSpendingSource FundsSpendingSource;

        /// <summary>
        /// Optional secondary dedup discriminator. Populated for action types whose
        /// natural key (<see cref="RecordingId"/>) collides at near-identical UTs —
        /// notably <see cref="FundsSpendingSource.Other"/> part purchases (part name)
        /// and <see cref="FundsEarningSource.Recovery"/> payouts (paired recovery-event
        /// fingerprint). Serialized for both <see cref="FundsEarning"/> and
        /// <see cref="FundsSpending"/> so save/load preserves the same dedup identity.
        /// See <see cref="LedgerOrchestrator.GetActionKey"/>.
        /// </summary>
        public string DedupKey;

        // ---- Reputation fields ----

        /// <summary>Nominal reputation earned before curve (immutable).</summary>
        public float NominalRep;

        /// <summary>Source of reputation earning.</summary>
        public ReputationSource RepSource;

        /// <summary>Nominal reputation penalty before curve (immutable).</summary>
        public float NominalPenalty;

        /// <summary>Source of reputation penalty.</summary>
        public ReputationPenaltySource RepPenaltySource;

        /// <summary>
        /// True when this row's reputation movement is ALREADY contained in the career's
        /// <see cref="GameActionType.ReputationInitial"/> seed value, so
        /// <c>ReputationModule.ProcessAction</c> must not apply it a second time.
        ///
        /// <para>
        /// NOT a ReputationPenalty field. It is meaningful on every type
        /// <see cref="ReputationSeedMembership.IsReputationAffectingRow"/> answers true
        /// for - milestones, contract outcomes, strategy setup costs and both reputation
        /// row types - because the seed is one absolute figure that contains whatever the
        /// live pool had taken by the time it was read, whatever produced it. Only the
        /// REPUTATION leg is suppressed: the funds and science modules never read it.
        /// </para>
        ///
        /// <para>
        /// Decided ONCE, by the producer, in PRODUCTION order rather than in game UT
        /// (<c>ReputationSeedMembership.IsInsideReputationSeed</c>): a rewind moves the
        /// game clock backwards, so "the row's UT precedes the seed's capture UT" orders
        /// nothing across a re-fly. What the producer knows instead is whether the live
        /// pool the seed was, or will be, read from had already taken this hit.
        /// </para>
        ///
        /// <para>
        /// Written only when true and absent means false, so a save from before the field
        /// existed reads as "apply", which is the old behaviour. Additive value on an
        /// existing node: it renames no key and changes no layout, so it is not a schema
        /// SHAPE change (.claude/CLAUDE.md, "Recording schema") - no generation bump.
        /// </para>
        /// </summary>
        public bool InsideReputationSeed;

        /// <summary>
        /// ConfigNode key for <see cref="InsideReputationSeed"/>. Written only when true;
        /// an absent key reads false, which is the pre-field shape and the apply-as-before
        /// behaviour.
        /// </summary>
        internal const string InsideReputationSeedKey = "insideRepSeed";

        /// <summary>
        /// True when the row was written by a <see cref="LedgerRowWriter.ParsekAuthored"/>
        /// writer: Parsek authored it without stock applying it to the live pool (a load
        /// migration's or broken-ledger recovery's rows, the reconciliation bundle's restored
        /// rows). The drawdown guard's stock reference (<see cref="StockPoolReference"/>)
        /// steps only rows stock applied, so such a row never reads as already in the pool.
        /// Stamped by <see cref="Ledger.AddAction(GameAction, LedgerRowWriter)"/> /
        /// <see cref="Ledger.AddActions(System.Collections.Generic.IEnumerable{GameAction}, LedgerRowWriter)"/>.
        /// Written only when true; absent means stock-applied. Additive key, no schema
        /// generation bump.
        /// </summary>
        public bool ParsekAuthored;

        /// <summary>ConfigNode key for <see cref="ParsekAuthored"/>, written only when true.</summary>
        internal const string ParsekAuthoredKey = "parsekAuthored";

        // ---- Milestone fields ----

        /// <summary>Milestone identifier, e.g. "FirstOrbitKerbin".</summary>
        public string MilestoneId;

        /// <summary>Funds awarded by the milestone (immutable, 0 in Science mode).</summary>
        public float MilestoneFundsAwarded;

        /// <summary>Reputation awarded by the milestone (immutable, 0 in Science mode).</summary>
        public float MilestoneRepAwarded;

        /// <summary>Science awarded by the milestone (immutable, 0 in pure-funds/rep milestones).
        /// Consumed by <see cref="ScienceModule.ProcessMilestoneScienceReward"/> so first-
        /// reached milestones credit the R&amp;D pool. Without this field, the sci= value
        /// recorded in the event detail was silently dropped at convert time.</summary>
        public float MilestoneScienceAwarded;

        /// <summary>
        /// World-record rows only (RecordsAltitude / Depth / Speed / Distance): how many stock
        /// reward thresholds this row stands for. The recorder folds every threshold one
        /// recording scope crosses into ONE row
        /// (<see cref="GameStateRecorder.TryCoalesceWorldRecordReward"/>), and the record-node
        /// patch rebuilds the node's band from the sum over rows, so the sum has to be the
        /// paid-threshold count, not the row count. 0 on a record node's completion row (stock
        /// completes the node after its last award, so that row pays no threshold).
        /// <para>
        /// 1 on every other row and on a row whose producer never wrote the field. A row
        /// written before the field existed cannot be repaired from its accumulated reward:
        /// the per-threshold reward depends on difficulty multipliers and strategies that may
        /// have changed since, and a science-mode row carries no funds at all.
        /// </para>
        /// <para>
        /// Additive: written as <see cref="MilestoneRecordThresholdsKey"/> only when not 1, so
        /// every other row keeps its bytes, and an older build reads a row as one hit as
        /// before. Not a schema shape change (.claude/CLAUDE.md, "Recording schema").
        /// </para>
        /// </summary>
        public int MilestoneRecordThresholds = 1;

        /// <summary>ConfigNode key for <see cref="MilestoneRecordThresholds"/>; absent reads 1.</summary>
        internal const string MilestoneRecordThresholdsKey = "milestoneRecordThresholds";

        /// <summary>
        /// World-record rows only: which stock thresholds this row crossed (stock's 1-based
        /// interval, <see cref="RecordThresholdCredit.Index"/>), what stock paid for each, and
        /// whether it was a re-payment of an interval the live node had already paid
        /// (<see cref="RecordThresholdCredit.Repay"/>), so the walk can credit each threshold once per career, earliest crossing first
        /// (design 7.8, section 3.15 step 4; <see cref="RecordThresholdBook"/>). Null on a row
        /// whose producer could not read the interval of every award, on a completion row, and
        /// on every row written before the field: such a row takes consecutive positions from
        /// <see cref="MilestoneRecordThresholds"/> instead. <see cref="MilestoneRecordThresholds"/>
        /// keeps counting awards, so an older build reads the row as before.
        /// <para>Additive: written as <see cref="MilestoneRecordBandKey"/> only when present
        /// (<see cref="RecordThresholdBand.Format"/>). Not a schema shape change
        /// (.claude/CLAUDE.md, "Recording schema").</para>
        /// </summary>
        internal List<RecordThresholdCredit> MilestoneRecordBand;

        /// <summary>ConfigNode key for <see cref="MilestoneRecordBand"/>; absent reads null.</summary>
        internal const string MilestoneRecordBandKey = "milestoneRecordBand";

        // ---- Contract fields ----

        /// <summary>KSP's unique contract instance ID.</summary>
        public string ContractId;

        /// <summary>Contract type, e.g. "ExploreBody".</summary>
        public string ContractType;

        /// <summary>Human-readable contract title.</summary>
        public string ContractTitle;

        /// <summary>
        /// The parameter a <see cref="GameActionType.ContractParameterComplete"/> row pays for:
        /// its index path inside the contract (<c>"0"</c>, <c>"1.0"</c>), built by
        /// <see cref="GameStateRecorder.BuildContractParameterIndexPath"/>. Null on every other
        /// type. Written under <see cref="ContractParameterIdKey"/> for that type only.
        /// </summary>
        public string ContractParameterId;

        /// <summary>ConfigNode key for <see cref="ContractParameterId"/>.</summary>
        internal const string ContractParameterIdKey = "contractParameterId";

        /// <summary>Advance payment received on accept.</summary>
        public float AdvanceFunds;

        /// <summary>
        /// ABSOLUTE expiration UT (stock <c>Contract.DateDeadline</c>). NaN when the
        /// contract carries no deadline (stock <c>DeadlineType.None</c>).
        ///
        /// <para>CONTRACT-DEADLINE-CAPTURED-AS-DURATION: until 2026-08-29 the capture
        /// stored stock's <c>Contract.TimeDeadline</c> - a DURATION in seconds - into
        /// this field, which every consumer reads as an absolute UT. It is a
        /// <c>double</c> rather than a <c>float</c> because a mid-career UT does not fit
        /// in a float's ~7 significant digits: the C2Career fixture's row stored
        /// 8228571.5 for a stock <c>TimeDeadline</c> of 8228571.72775267, already losing
        /// a quarter second at a value far below a long career's clock.</para>
        ///
        /// <para>On disk the absolute form is written under the key
        /// <c>deadlineAbsUT</c>. The legacy <c>deadlineUT</c> key means a DURATION and is
        /// migrated to absolute on load by <see cref="DeserializeContractAccept"/>.</para>
        /// </summary>
        public double DeadlineUT = double.NaN;

        /// <summary>Funds reward on completion.</summary>
        public float FundsReward;

        /// <summary>Reputation reward on completion (nominal, pre-curve).</summary>
        public float RepReward;

        /// <summary>Science reward on completion.</summary>
        public float ScienceReward;

        /// <summary>Funds penalty on fail/cancel.</summary>
        public float FundsPenalty;

        /// <summary>Reputation penalty on fail/cancel (nominal, pre-curve).</summary>
        public float RepPenalty;

        /// <summary>
        /// True on a <see cref="GameActionType.ContractCancel"/> row that is not a player cancel
        /// but stock ending an Active contract as <c>OfferExpired</c> because its requirements
        /// no longer hold. Stock charges nothing for that end, so the row carries zero
        /// penalties; the walk ends the contract and frees its slot like a cancel. Written
        /// under <see cref="ContractRequirementsLostKey"/> only when true.
        /// </summary>
        public bool ContractRequirementsLost;

        /// <summary>ConfigNode key for <see cref="ContractRequirementsLost"/>; absent reads false.</summary>
        internal const string ContractRequirementsLostKey = "requirementsLost";

        // ---- Kerbal fields ----

        /// <summary>Kerbal's full name.</summary>
        public string KerbalName;

        /// <summary>Kerbal's role/class (Pilot/Engineer/Scientist).</summary>
        public string KerbalRole;

        /// <summary>
        /// Encoded career-log entry set for a <see cref="GameActionType.KerbalExperience"/>
        /// row: pipe-separated <c>flight,type,target</c> triples (see
        /// <see cref="KerbalCareerLogEntry.FormatSet"/>). Null on every other action type.
        /// </summary>
        public string KerbalCareerEntries;

        // ---- Vessel recovery fields (VesselRecovered) ----

        /// <summary>
        /// Display name of the recovered vessel on a <see cref="GameActionType.VesselRecovered"/>
        /// row. Null on every other action type.
        /// </summary>
        public string RecoveredVesselName;

        /// <summary>
        /// Persistent id of the recovered live vessel on a
        /// <see cref="GameActionType.VesselRecovered"/> row (a Parsek spawn pid or the
        /// recorded launch's pid). Diagnostic only: identity was decided at write time.
        /// 0 on every other action type.
        /// </summary>
        public uint RecoveredVesselPid;

        // ---- Resource scan fields (ResourceScanUnlocked) ----

        /// <summary>
        /// Name of the scanned body on a <see cref="GameActionType.ResourceScanUnlocked"/> row
        /// (<c>CelestialBody.bodyName</c>; resolved to the stock planet id at patch time), and
        /// the surveyed body on a <see cref="GameActionType.ScienceLabEarning"/> row that holds
        /// an orbital survey's science bonus. Null on every other row.
        /// </summary>
        public string ScanBodyName;

        /// <summary>
        /// Biome unlocked by a surface scan on a <see cref="GameActionType.ResourceScanUnlocked"/>
        /// row; null for a whole-body orbital survey and on every other action type.
        /// </summary>
        public string ScanBiomeName;

        /// <summary>Mission start UT.</summary>
        public float StartUT;

        /// <summary>Mission end UT. NaN if stranded (open-ended).</summary>
        public float EndUT = float.NaN;

        /// <summary>Kerbal's end state for this assignment.</summary>
        public KerbalEndState KerbalEndStateField;

        /// <summary>XP earned during this recording.</summary>
        public float XpGained;

        /// <summary>Funds spent to hire this kerbal (career only).</summary>
        public float HireCost;

        /// <summary>Name of the kerbal this stand-in replaces.</summary>
        public string ReplacesKerbal;

        /// <summary>Stand-in kerbal's courage (randomized).</summary>
        public float Courage;

        /// <summary>Stand-in kerbal's stupidity (randomized).</summary>
        public float Stupidity;

        // ---- Facility fields ----

        /// <summary>Facility identifier, e.g. "LaunchPad".</summary>
        public string FacilityId;

        /// <summary>Target level after upgrade (2 or 3).</summary>
        public int ToLevel;

        /// <summary>Funds cost for facility upgrade or repair.</summary>
        public float FacilityCost;

        /// <summary>
        /// True on a <see cref="GameActionType.FacilityUpgrade"/> level-change row that LOWERS
        /// the facility: stock's paid <c>SpaceCenterBuilding.DowngradeFacility</c> (the KSC
        /// menu's "Rebuild lvl N"; FACILITY-DOWNGRADE-DEBIT-NOT-LEDGERED). The walk needs no
        /// flag (<c>ToLevel</c> is absolute and <c>FacilityCost</c> is the debit either way);
        /// the description, the stock-screen reservation index and the legacy upgrade-cost
        /// passes read it. Sparse key <c>facilityDowngrade</c>, absent = false.
        /// </summary>
        public bool FacilityDowngrade;

        // ---- Strategy fields ----

        /// <summary>Strategy identifier, e.g. "UnpaidResearch".</summary>
        public string StrategyId;

        /// <summary>Resource being diverted from.</summary>
        public StrategyResource SourceResource;

        /// <summary>Resource being diverted to.</summary>
        public StrategyResource TargetResource;

        /// <summary>Diversion percentage (0.01 to 0.25).</summary>
        public float Commitment;

        /// <summary>One-time cost in source resource on activation.</summary>
        public float SetupCost;

        /// <summary>One-time science cost on activation.</summary>
        public float SetupScienceCost;

        /// <summary>One-time reputation cost on activation.</summary>
        public float SetupReputationCost;

        // ---- Initial seed fields ----

        /// <summary>Career starting funds, extracted from save file.</summary>
        public float InitialFunds;

        /// <summary>
        /// True on a <see cref="GameActionType.FundsInitial"/> seed whose ZERO was read off a
        /// loaded <c>Funding</c> singleton with no funds history in the ledger (a
        /// StartingFunds = 0 career). It seals the zero: without it
        /// <c>Ledger.SeedInitialFunds</c>'s stale-zero repair would later overwrite the seed
        /// with a live pool that already contains the career's earnings. Sparse on disk
        /// (absent means false).
        /// </summary>
        public bool InitialFundsConfirmedZero;

        /// <summary>Existing science balance when Parsek is first installed mid-career.</summary>
        public float InitialScience;

        /// <summary>Existing reputation when Parsek is first installed mid-career.</summary>
        public float InitialReputation;

        /// <summary>
        /// On a <see cref="GameActionType.ReputationInitial"/> seed read off the LIVE pool:
        /// the recordings that were already recorded but not yet committed when it was
        /// read (the active and pending trees), so every reputation hit they had taken by
        /// then is inside the seed value although their rows are filed later.
        /// Paired with <see cref="SeedCaptureUT"/>; read by
        /// <c>ReputationSeedMembership.IsRecordedBeforeSeedCapture</c>. Null on every
        /// other seed (career baseline, refusal fallback, a seed with no uncommitted
        /// recording at capture). Sparse on disk: one <see cref="SeedPendingRecordingKey"/>
        /// value per id, absent means none.
        /// </summary>
        public List<string> SeedPendingRecordingIds;

        /// <summary>
        /// Game UT at which a live-pool <see cref="GameActionType.ReputationInitial"/> seed
        /// with <see cref="SeedPendingRecordingIds"/> was read. NaN when not recorded.
        /// </summary>
        public double SeedCaptureUT = double.NaN;

        /// <summary>Sparse ReputationInitial key for <see cref="SeedPendingRecordingIds"/>.</summary>
        internal const string SeedPendingRecordingKey = "seedPendingRec";

        /// <summary>Sparse ReputationInitial key for <see cref="SeedCaptureUT"/>.</summary>
        internal const string SeedCaptureUTKey = "seedCaptureUT";

        // ================================================================
        // Derived fields — set during recalculation walk, NOT serialized
        // ================================================================

        /// <summary>
        /// Science actually credited after applying subject cap headroom.
        /// Set by ScienceModule during recalculation walk. Always derived, never stored.
        /// </summary>
        public float EffectiveScience;

        /// <summary>
        /// Whether a spending action was affordable at the time it was processed in the walk.
        /// Set by resource modules during recalculation walk. Always derived, never stored.
        /// </summary>
        public bool Affordable;

        /// <summary>
        /// Running science pool at the moment <c>ScienceModule.ProcessSpending</c> REFUSED this
        /// spend as unaffordable. Non-null ONLY on a <see cref="GameActionType.ScienceSpending"/>
        /// row the walk actually processed and refused; null on an affordable spend and on every
        /// row the science walk never dispatched. Always derived, never stored.
        ///
        /// <para>This is a POSITIVE marker, and that is the point: <see cref="Affordable"/> alone
        /// cannot separate "the walk refused this purchase" from "the module never ran for this
        /// row", because <c>RecalculationEngine.ResetDerivedFields</c> seeds
        /// <see cref="Affordable"/> to <c>false</c> for every action. The unaffordable-re-lock
        /// guard (<c>KspStatePatcher.ShouldRefuseUnaffordableRelock</c>) must never fire on the
        /// second case — that would suppress a LEGITIMATE re-lock — so it keys on this field's
        /// presence rather than on <c>!Affordable</c>. It also carries the shortfall number the
        /// refusal WARN reports.</para>
        /// </summary>
        public double? UnaffordableRunningScience;

        /// <summary>
        /// Actual reputation change after applying the gain/loss curve against running rep.
        /// Set by ReputationModule during recalculation walk. Positive for gains, negative for losses.
        /// Always derived, never stored.
        /// </summary>
        public float EffectiveRep;

        /// <summary>Transformed funds reward after strategy application (derived, not serialized).</summary>
        public float TransformedFundsReward;
        /// <summary>Transformed science reward after strategy application (derived, not serialized).</summary>
        public float TransformedScienceReward;
        /// <summary>Transformed rep reward after strategy application (derived, not serialized).</summary>
        public float TransformedRepReward;

        // ================================================================
        // Serialization
        // ================================================================

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private static readonly NumberStyles NS = NumberStyles.Float;

        /// <summary>
        /// Computes a deterministic legacy <see cref="ActionId"/> for
        /// pre-Rewind-to-Staging actions that lack a persisted id
        /// (design doc section 5.6 + 9). The hash input is the concatenation
        /// <c>UT.ToString("R", InvariantCulture) + "|" + Type + "|" +
        /// (RecordingId ?? "") + "|" + Sequence</c>; the output is
        /// <c>"act_legacy_" + first 16 hex chars of SHA1(input)</c>. Idempotent:
        /// the same inputs always produce the same id, so repeated loads do not
        /// drift.
        /// </summary>
        internal static string ComputeLegacyActionId(double ut, GameActionType type, string recordingId, int sequence)
        {
            string input = ut.ToString("R", IC) + "|"
                + type.ToString() + "|"
                + (recordingId ?? "") + "|"
                + sequence.ToString(IC);
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
                var sb = new System.Text.StringBuilder("act_legacy_", 11 + 16);
                int take = System.Math.Min(8, hash.Length); // 8 bytes = 16 hex chars
                for (int i = 0; i < take; i++)
                    sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Serializes this action into a GAME_ACTION ConfigNode under the given parent.
        /// Only writes fields relevant to the action type — does not write nulls or defaults.
        /// </summary>
        public void SerializeInto(ConfigNode parent)
        {
            ConfigNode node = parent.AddNode("GAME_ACTION");
            node.AddValue("ut", UT.ToString("R", IC));
            node.AddValue("type", ((int)Type).ToString(IC));

            // Rewind-to-Staging (design section 5.6) — every action has an ActionId.
            // Auto-assigned at construction for new actions; deterministically rehydrated
            // on load for legacy actions (see DeserializeFrom + ComputeLegacyActionId).
            if (string.IsNullOrEmpty(ActionId))
                ActionId = "act_" + Guid.NewGuid().ToString("N");
            node.AddValue("actionId", ActionId);

            if (RecordingId != null)
                node.AddValue("recordingId", RecordingId);
            if (Sequence != 0)
                node.AddValue("seq", Sequence.ToString(IC));
            if (CaptureSequence != 0)
                node.AddValue(Parsek.CaptureSequence.Key, CaptureSequence.ToString(IC));

            // TYPE-AGNOSTIC ON PURPOSE. The flag applies to every reputation-affecting row
            // (ReputationSeedMembership.IsReputationAffectingRow), not to one source of one
            // type, so it is written and read in the common header rather than in seven
            // type-specific serializers. The KEY AND THE NODE ARE UNCHANGED: the
            // type-specific serializers write into this same GAME_ACTION node, so a row
            // saved by the 2026-09-10 build that wrote it from SerializeRepPenalty reads
            // back here byte-identically. Sparse on purpose: absent means false, which is
            // what a pre-field save carries and what every row outside the seed is.
            if (InsideReputationSeed)
                node.AddValue(InsideReputationSeedKey, InsideReputationSeed.ToString());
            if (ParsekAuthored)
                node.AddValue(ParsekAuthoredKey, ParsekAuthored.ToString());

            switch (Type)
            {
                case GameActionType.ScienceEarning:
                    SerializeScienceEarning(node);
                    break;
                case GameActionType.ScienceSpending:
                    SerializeScienceSpending(node);
                    break;
                case GameActionType.StrategyScienceDebit:
                    SerializeStrategyScienceDebit(node);
                    break;
                case GameActionType.StrategyScienceCredit:
                    SerializeStrategyScienceCredit(node);
                    break;
                case GameActionType.FundsEarning:
                    SerializeFundsEarning(node);
                    break;
                case GameActionType.FundsSpending:
                    SerializeFundsSpending(node);
                    break;
                case GameActionType.MilestoneAchievement:
                    SerializeMilestone(node);
                    break;
                case GameActionType.ContractAccept:
                    SerializeContractAccept(node);
                    break;
                case GameActionType.ContractComplete:
                    SerializeContractComplete(node);
                    break;
                case GameActionType.ContractParameterComplete:
                    SerializeContractParameterComplete(node);
                    break;
                case GameActionType.ContractFail:
                    SerializeContractFail(node);
                    break;
                case GameActionType.ContractCancel:
                    SerializeContractCancel(node);
                    break;
                case GameActionType.ReputationEarning:
                    SerializeRepEarning(node);
                    break;
                case GameActionType.ReputationPenalty:
                    SerializeRepPenalty(node);
                    break;
                case GameActionType.KerbalAssignment:
                    SerializeKerbalAssignment(node);
                    break;
                case GameActionType.KerbalHire:
                    SerializeKerbalHire(node);
                    break;
                case GameActionType.KerbalRescue:
                    SerializeKerbalRescue(node);
                    break;
                case GameActionType.KerbalStandIn:
                    SerializeKerbalStandIn(node);
                    break;
                case GameActionType.KerbalExperience:
                    SerializeKerbalExperience(node);
                    break;
                case GameActionType.KerbalRecovered:
                    SerializeKerbalRecovered(node);
                    break;
                case GameActionType.VesselRecovered:
                    SerializeVesselRecovered(node);
                    break;
                case GameActionType.ResourceScanUnlocked:
                    SerializeResourceScanUnlocked(node);
                    break;
                case GameActionType.ScienceLabEarning:
                    SerializeScienceLabEarning(node);
                    break;
                case GameActionType.StockPoolAdjustment:
                    SerializeStockPoolAdjustment(node);
                    break;
                case GameActionType.FacilityUpgrade:
                    SerializeFacilityUpgrade(node);
                    break;
                case GameActionType.FacilityDestruction:
                    SerializeFacilityDestruction(node);
                    break;
                case GameActionType.FacilityRepair:
                    SerializeFacilityRepair(node);
                    break;
                case GameActionType.StrategyActivate:
                    SerializeStrategyActivate(node);
                    break;
                case GameActionType.StrategyDeactivate:
                    SerializeStrategyDeactivate(node);
                    break;
                case GameActionType.FundsInitial:
                    SerializeFundsInitial(node);
                    break;
                case GameActionType.ScienceInitial:
                    SerializeScienceInitial(node);
                    break;
                case GameActionType.ReputationInitial:
                    SerializeReputationInitial(node);
                    break;
            }
        }

        /// <summary>
        /// Deserializes a GameAction from a GAME_ACTION ConfigNode.
        /// Unknown fields are silently ignored for forward compatibility.
        /// </summary>
        public static GameAction DeserializeFrom(ConfigNode node)
        {
            var a = new GameAction();

            string utStr = node.GetValue("ut");
            if (utStr != null)
                double.TryParse(utStr, NS, IC, out a.UT);

            string typeStr = node.GetValue("type");
            if (typeStr != null)
            {
                int typeInt;
                if (int.TryParse(typeStr, NumberStyles.Integer, IC, out typeInt))
                {
                    if (Enum.IsDefined(typeof(GameActionType), typeInt))
                        a.Type = (GameActionType)typeInt;
                    else
                        ParsekLog.Warn("GameAction", $"Unknown action type id '{typeInt}' while deserializing");
                }
            }

            a.RecordingId = node.GetValue("recordingId");

            string seqStr = node.GetValue("seq");
            if (seqStr != null)
                int.TryParse(seqStr, NumberStyles.Integer, IC, out a.Sequence);
            a.CaptureSequence = Parsek.CaptureSequence.Parse(node.GetValue(Parsek.CaptureSequence.Key));

            // Rewind-to-Staging (design section 5.6 + 9). Legacy actions without
            // `actionId` get a deterministic hash-based id so tombstones remain
            // stable across reloads. Counter bumped for the one-shot Info log.
            string actionIdStr = node.GetValue("actionId");
            if (!string.IsNullOrEmpty(actionIdStr))
            {
                a.ActionId = actionIdStr;
            }
            else
            {
                a.ActionId = ComputeLegacyActionId(a.UT, a.Type, a.RecordingId, a.Sequence);
                Ledger.BumpLegacyActionIdMigrationCounterForTesting();
            }

            // See SerializeInto: read in the common header, same key, same node.
            string insideSeedStr = node.GetValue(InsideReputationSeedKey);
            if (insideSeedStr != null)
                bool.TryParse(insideSeedStr, out a.InsideReputationSeed);
            string parsekAuthoredStr = node.GetValue(ParsekAuthoredKey);
            if (parsekAuthoredStr != null)
                bool.TryParse(parsekAuthoredStr, out a.ParsekAuthored);

            switch (a.Type)
            {
                case GameActionType.ScienceEarning:
                    DeserializeScienceEarning(node, a);
                    break;
                case GameActionType.ScienceSpending:
                    DeserializeScienceSpending(node, a);
                    break;
                case GameActionType.StrategyScienceDebit:
                    DeserializeStrategyScienceDebit(node, a);
                    break;
                case GameActionType.StrategyScienceCredit:
                    DeserializeStrategyScienceCredit(node, a);
                    break;
                case GameActionType.FundsEarning:
                    DeserializeFundsEarning(node, a);
                    break;
                case GameActionType.FundsSpending:
                    DeserializeFundsSpending(node, a);
                    break;
                case GameActionType.MilestoneAchievement:
                    DeserializeMilestone(node, a);
                    break;
                case GameActionType.ContractAccept:
                    DeserializeContractAccept(node, a);
                    break;
                case GameActionType.ContractComplete:
                    DeserializeContractComplete(node, a);
                    break;
                case GameActionType.ContractParameterComplete:
                    DeserializeContractParameterComplete(node, a);
                    break;
                case GameActionType.ContractFail:
                    DeserializeContractFail(node, a);
                    break;
                case GameActionType.ContractCancel:
                    DeserializeContractCancel(node, a);
                    break;
                case GameActionType.ReputationEarning:
                    DeserializeRepEarning(node, a);
                    break;
                case GameActionType.ReputationPenalty:
                    DeserializeRepPenalty(node, a);
                    break;
                case GameActionType.KerbalAssignment:
                    DeserializeKerbalAssignment(node, a);
                    break;
                case GameActionType.KerbalHire:
                    DeserializeKerbalHire(node, a);
                    break;
                case GameActionType.KerbalRescue:
                    DeserializeKerbalRescue(node, a);
                    break;
                case GameActionType.KerbalStandIn:
                    DeserializeKerbalStandIn(node, a);
                    break;
                case GameActionType.KerbalExperience:
                    DeserializeKerbalExperience(node, a);
                    break;
                case GameActionType.KerbalRecovered:
                    DeserializeKerbalRecovered(node, a);
                    break;
                case GameActionType.VesselRecovered:
                    DeserializeVesselRecovered(node, a);
                    break;
                case GameActionType.ResourceScanUnlocked:
                    DeserializeResourceScanUnlocked(node, a);
                    break;
                case GameActionType.ScienceLabEarning:
                    DeserializeScienceLabEarning(node, a);
                    break;
                case GameActionType.StockPoolAdjustment:
                    DeserializeStockPoolAdjustment(node, a);
                    break;
                case GameActionType.FacilityUpgrade:
                    DeserializeFacilityUpgrade(node, a);
                    break;
                case GameActionType.FacilityDestruction:
                    DeserializeFacilityDestruction(node, a);
                    break;
                case GameActionType.FacilityRepair:
                    DeserializeFacilityRepair(node, a);
                    break;
                case GameActionType.StrategyActivate:
                    DeserializeStrategyActivate(node, a);
                    break;
                case GameActionType.StrategyDeactivate:
                    DeserializeStrategyDeactivate(node, a);
                    break;
                case GameActionType.FundsInitial:
                    DeserializeFundsInitial(node, a);
                    break;
                case GameActionType.ScienceInitial:
                    DeserializeScienceInitial(node, a);
                    break;
                case GameActionType.ReputationInitial:
                    DeserializeReputationInitial(node, a);
                    break;
            }

            return a;
        }

        // ---- Per-type serialization helpers ----

        private void SerializeScienceEarning(ConfigNode n)
        {
            if (SubjectId != null) n.AddValue("subjectId", SubjectId);
            if (ExperimentId != null) n.AddValue("experimentId", ExperimentId);
            if (Body != null) n.AddValue("body", Body);
            if (Situation != null) n.AddValue("situation", Situation);
            if (Biome != null) n.AddValue("biome", Biome);
            n.AddValue("scienceAwarded", ScienceAwarded.ToString("R", IC));
            n.AddValue("method", ((int)Method).ToString(IC));
            n.AddValue("transmitScalar", TransmitScalar.ToString("R", IC));
            n.AddValue("subjectMaxValue", SubjectMaxValue.ToString("R", IC));
            // Sparse: an x1 career writes the same bytes it always did.
            float gainMultiplier = NormalizeScienceGainMultiplier(ScienceGainMultiplier);
            if (gainMultiplier != 1f)
                n.AddValue(ScienceGainMultiplierKey, gainMultiplier.ToString("R", IC));
            if (!float.IsNaN(EndUT))
            {
                n.AddValue("startUT", StartUT.ToString("R", IC));
                n.AddValue("endUT", EndUT.ToString("R", IC));
            }
        }

        private static void DeserializeScienceEarning(ConfigNode n, GameAction a)
        {
            a.SubjectId = n.GetValue("subjectId");
            a.ExperimentId = n.GetValue("experimentId");
            a.Body = n.GetValue("body");
            a.Situation = n.GetValue("situation");
            a.Biome = n.GetValue("biome");
            TryParseFloat(n, "scienceAwarded", out a.ScienceAwarded);
            TryParseEnum(n, "method", out a.Method);
            TryParseFloat(n, "transmitScalar", out a.TransmitScalar);
            TryParseFloat(n, "subjectMaxValue", out a.SubjectMaxValue);
            float gainMultiplier;
            a.ScienceGainMultiplier = TryParseFloat(n, ScienceGainMultiplierKey, out gainMultiplier)
                ? NormalizeScienceGainMultiplier(gainMultiplier)
                : 1f;
            TryParseFloat(n, "startUT", out a.StartUT);
            if (!TryParseFloat(n, "endUT", out a.EndUT))
                a.EndUT = float.NaN;
        }

        /// <summary>Sparse ScienceEarning key for <see cref="ScienceGainMultiplier"/>.</summary>
        internal const string ScienceGainMultiplierKey = "scienceGainMultiplier";

        /// <summary>
        /// Pure: maps a captured multiplier to the value the ledger applies. Stock's slider
        /// runs 0.1..10, so a non-positive, NaN or infinite value can only mean "not
        /// captured" (a default struct field, a legacy row) and reads as 1.
        /// </summary>
        internal static float NormalizeScienceGainMultiplier(float multiplier)
        {
            if (float.IsNaN(multiplier) || float.IsInfinity(multiplier) || multiplier <= 0f)
                return 1f;
            return multiplier;
        }

        /// <summary>
        /// The science this row put into the POOL: <see cref="ScienceAwarded"/> (subject
        /// units) times the multiplier frozen at capture.
        /// </summary>
        internal float GetScienceAwardedPoolCredit()
        {
            return ScienceAwarded * NormalizeScienceGainMultiplier(ScienceGainMultiplier);
        }

        private void SerializeScienceSpending(ConfigNode n)
        {
            if (NodeId != null) n.AddValue("nodeId", NodeId);
            n.AddValue("cost", Cost.ToString("R", IC));
        }

        private static void DeserializeScienceSpending(ConfigNode n, GameAction a)
        {
            a.NodeId = n.GetValue("nodeId");
            TryParseFloat(n, "cost", out a.Cost);
        }

        // StrategyScienceDebit reuses the Cost field (positive magnitude) and
        // deliberately carries NO nodeId - it is not a tech-node spending.
        // conversionSource tells the two producers apart (reason-keyed exchanger door
        // vs query-family converter door); absent on rows written before that door
        // existed, which TryParseEnum leaves at Exchanger = 0, the original meaning.
        private void SerializeStrategyScienceDebit(ConfigNode n)
        {
            n.AddValue("cost", Cost.ToString("R", IC));
            n.AddValue("conversionSource", ((int)ConversionSource).ToString(IC));
        }

        private static void DeserializeStrategyScienceDebit(ConfigNode n, GameAction a)
        {
            TryParseFloat(n, "cost", out a.Cost);
            TryParseEnum(n, "conversionSource", out a.ConversionSource);
        }

        // StrategyScienceCredit carries the positive magnitude in scienceAwarded (NOT
        // cost - it is a credit) and no subjectId / subjectMaxValue: it is a pool-only
        // movement with no science subject behind it, which is precisely why a
        // ScienceEarning row cannot represent it.
        private void SerializeStrategyScienceCredit(ConfigNode n)
        {
            n.AddValue("scienceAwarded", ScienceAwarded.ToString("R", IC));
        }

        private static void DeserializeStrategyScienceCredit(ConfigNode n, GameAction a)
        {
            TryParseFloat(n, "scienceAwarded", out a.ScienceAwarded);
        }

        private void SerializeFundsEarning(ConfigNode n)
        {
            n.AddValue("fundsAwarded", FundsAwarded.ToString("R", IC));
            n.AddValue("fundsSource", ((int)FundsSource).ToString(IC));
            if (!string.IsNullOrEmpty(DedupKey))
                n.AddValue("dedupKey", DedupKey);
        }

        private static void DeserializeFundsEarning(ConfigNode n, GameAction a)
        {
            TryParseFloat(n, "fundsAwarded", out a.FundsAwarded);
            TryParseEnum(n, "fundsSource", out a.FundsSource);
            a.DedupKey = n.GetValue("dedupKey");
        }

        private void SerializeFundsSpending(ConfigNode n)
        {
            n.AddValue("fundsSpent", FundsSpent.ToString("R", IC));
            n.AddValue("fundsSpendingSource", ((int)FundsSpendingSource).ToString(IC));
            // DedupKey disambiguates same-UT KSC spendings (e.g. multiple part purchases
            // with recordingId=null) — must round-trip so reload doesn't re-collapse them.
            if (!string.IsNullOrEmpty(DedupKey))
                n.AddValue("dedupKey", DedupKey);
        }

        private static void DeserializeFundsSpending(ConfigNode n, GameAction a)
        {
            TryParseFloat(n, "fundsSpent", out a.FundsSpent);
            TryParseEnum(n, "fundsSpendingSource", out a.FundsSpendingSource);
            a.DedupKey = n.GetValue("dedupKey");
        }

        private void SerializeMilestone(ConfigNode n)
        {
            if (MilestoneId != null) n.AddValue("milestoneId", MilestoneId);
            n.AddValue("milestoneFundsAwarded", MilestoneFundsAwarded.ToString("R", IC));
            n.AddValue("milestoneRepAwarded", MilestoneRepAwarded.ToString("R", IC));
            n.AddValue("milestoneSciAwarded", MilestoneScienceAwarded.ToString("R", IC));
            if (MilestoneRecordThresholds != 1)
                n.AddValue(MilestoneRecordThresholdsKey, MilestoneRecordThresholds.ToString(IC));
            string band = RecordThresholdBand.Format(MilestoneRecordBand);
            if (band != null)
                n.AddValue(MilestoneRecordBandKey, band);
        }

        private static void DeserializeMilestone(ConfigNode n, GameAction a)
        {
            a.MilestoneId = n.GetValue("milestoneId");
            TryParseFloat(n, "milestoneFundsAwarded", out a.MilestoneFundsAwarded);
            TryParseFloat(n, "milestoneRepAwarded", out a.MilestoneRepAwarded);
            // Backward compat: pre-fix saves have no milestoneSciAwarded key; default to 0.
            TryParseFloat(n, "milestoneSciAwarded", out a.MilestoneScienceAwarded);

            // An absent band key is a row that does not name its thresholds (every row an
            // older build wrote): the walk derives its positions from the count below.
            string bandRaw = n.GetValue(MilestoneRecordBandKey);
            if (RecordThresholdBand.Parse(bandRaw, out List<RecordThresholdCredit> band, out bool bandMalformed)
                == RecordThresholdBand.ParseState.Listed)
            {
                a.MilestoneRecordBand = band;
            }
            else if (bandMalformed)
            {
                ParsekLog.Warn("GameAction",
                    $"Milestone row '{a.MilestoneId ?? "(none)"}' actionId={a.ActionId ?? "(none)"} " +
                    $"has malformed {MilestoneRecordBandKey}='{bandRaw}' - its thresholds are derived from the count");
            }

            // An absent key is the one-threshold shape (every row an older build wrote).
            string thresholds = n.GetValue(MilestoneRecordThresholdsKey);
            if (thresholds == null)
                return;
            if (int.TryParse(thresholds, NumberStyles.Integer, IC, out int parsed) && parsed >= 0)
            {
                a.MilestoneRecordThresholds = parsed;
                return;
            }
            ParsekLog.Warn("GameAction",
                $"Milestone row '{a.MilestoneId ?? "(none)"}' actionId={a.ActionId ?? "(none)"} " +
                $"has malformed {MilestoneRecordThresholdsKey}='{thresholds}' - reading it as 1 threshold");
        }

        private void SerializeContractAccept(ConfigNode n)
        {
            if (ContractId != null) n.AddValue("contractId", ContractId);
            if (ContractType != null) n.AddValue("contractType", ContractType);
            if (ContractTitle != null) n.AddValue("contractTitle", ContractTitle);
            n.AddValue("advanceFunds", AdvanceFunds.ToString("R", IC));
            // CONTRACT-DEADLINE-CAPTURED-AS-DURATION: the key NAME is the migration
            // stamp. `deadlineAbsUT` always means an absolute UT; the legacy
            // `deadlineUT` key always means a duration. Writing only the new key means
            // a row this build produced can never be re-migrated, and a row an older
            // build produced can never be mistaken for an absolute one.
            if (!double.IsNaN(DeadlineUT))
                n.AddValue(ContractDeadlineAbsoluteKey, DeadlineUT.ToString("R", IC));
            if (FundsPenalty != 0f)
                n.AddValue("fundsPenalty", FundsPenalty.ToString("R", IC));
            if (RepPenalty != 0f)
                n.AddValue("repPenalty", RepPenalty.ToString("R", IC));
        }

        /// <summary>
        /// ConfigNode key carrying an ABSOLUTE contract deadline UT. Written by every
        /// build from 2026-08-29 on.
        /// </summary>
        internal const string ContractDeadlineAbsoluteKey = "deadlineAbsUT";

        /// <summary>
        /// LEGACY ConfigNode key. Its value is a DURATION in seconds (stock
        /// <c>Contract.TimeDeadline</c>) even though the name says UT - see
        /// CONTRACT-DEADLINE-CAPTURED-AS-DURATION. Read-only: never written again.
        /// </summary>
        internal const string ContractDeadlineLegacyDurationKey = "deadlineUT";

        /// <summary>
        /// Outcome of resolving a ContractAccept node's deadline. Reported so the
        /// per-load migration tally can be logged as one batch summary.
        /// </summary>
        internal enum ContractDeadlineResolution
        {
            /// <summary>Neither key present: the contract has no deadline (NaN).</summary>
            Absent,
            /// <summary>The absolute key was present and used verbatim.</summary>
            Absolute,
            /// <summary>The legacy duration key was present and converted to absolute.</summary>
            MigratedFromDuration,
            /// <summary>A key was present but unparseable; treated as no deadline.</summary>
            Unparseable
        }

        /// <summary>
        /// Pure: resolves a ContractAccept node's deadline to an ABSOLUTE UT.
        ///
        /// <para>The absolute key wins outright when present. Otherwise the legacy
        /// <c>deadlineUT</c> key is a DURATION and the absolute deadline is
        /// <paramref name="acceptUT"/> + duration - stock assigns
        /// <c>dateDeadline = dateAccepted + TimeDeadline</c> at accept for a Floating
        /// contract, and the ledger row's own <c>ut</c> IS <c>dateAccepted</c> (proved
        /// byte-for-byte against the C2Career fixture's stock CONTRACT node).</para>
        ///
        /// <para>Idempotent by construction: the migrated value is re-serialized under
        /// the absolute key, so a second load takes the <see
        /// cref="ContractDeadlineResolution.Absolute"/> branch. A non-positive or
        /// non-finite legacy value is NOT migrated - a zero/NaN duration means "no
        /// deadline" under the old capture's own convention, and adding it to the accept
        /// UT would manufacture a deadline that expires the instant it is accepted.</para>
        /// </summary>
        internal static ContractDeadlineResolution ResolveContractDeadlineUT(
            ConfigNode n, double acceptUT, out double deadlineUT)
        {
            deadlineUT = double.NaN;
            if (n == null)
                return ContractDeadlineResolution.Absent;

            string absStr = n.GetValue(ContractDeadlineAbsoluteKey);
            if (absStr != null)
            {
                double parsed;
                if (!double.TryParse(absStr, NS, IC, out parsed))
                    return ContractDeadlineResolution.Unparseable;
                deadlineUT = parsed;
                return ContractDeadlineResolution.Absolute;
            }

            string legacyStr = n.GetValue(ContractDeadlineLegacyDurationKey);
            if (legacyStr == null)
                return ContractDeadlineResolution.Absent;

            double duration;
            if (!double.TryParse(legacyStr, NS, IC, out duration))
                return ContractDeadlineResolution.Unparseable;

            if (double.IsNaN(duration) || double.IsInfinity(duration) || duration <= 0.0)
                return ContractDeadlineResolution.Absent;

            deadlineUT = acceptUT + duration;
            return ContractDeadlineResolution.MigratedFromDuration;
        }

        private static void DeserializeContractAccept(ConfigNode n, GameAction a)
        {
            a.ContractId = n.GetValue("contractId");
            a.ContractType = n.GetValue("contractType");
            a.ContractTitle = n.GetValue("contractTitle");
            TryParseFloat(n, "advanceFunds", out a.AdvanceFunds);

            // CONTRACT-DEADLINE-CAPTURED-AS-DURATION on-disk migration. `a.UT` is
            // already parsed by DeserializeFrom before this type-specific branch runs.
            var resolution = ResolveContractDeadlineUT(n, a.UT, out a.DeadlineUT);
            Ledger.NoteContractDeadlineResolution(resolution);

            TryParseFloat(n, "fundsPenalty", out a.FundsPenalty);
            TryParseFloat(n, "repPenalty", out a.RepPenalty);
        }

        private void SerializeContractComplete(ConfigNode n)
        {
            if (ContractId != null) n.AddValue("contractId", ContractId);
            // Optional, additive: rows written before outcome actions carried a title
            // lack the key and resolve their name by contract id at display time.
            if (ContractTitle != null) n.AddValue("contractTitle", ContractTitle);
            n.AddValue("fundsReward", FundsReward.ToString("R", IC));
            n.AddValue("repReward", RepReward.ToString("R", IC));
            n.AddValue("scienceReward", ScienceReward.ToString("R", IC));
        }

        private static void DeserializeContractComplete(ConfigNode n, GameAction a)
        {
            a.ContractId = n.GetValue("contractId");
            a.ContractTitle = n.GetValue("contractTitle");
            TryParseFloat(n, "fundsReward", out a.FundsReward);
            TryParseFloat(n, "repReward", out a.RepReward);
            TryParseFloat(n, "scienceReward", out a.ScienceReward);
        }

        private void SerializeContractParameterComplete(ConfigNode n)
        {
            SerializeContractComplete(n);
            if (ContractParameterId != null) n.AddValue(ContractParameterIdKey, ContractParameterId);
        }

        private static void DeserializeContractParameterComplete(ConfigNode n, GameAction a)
        {
            DeserializeContractComplete(n, a);
            a.ContractParameterId = n.GetValue(ContractParameterIdKey);
        }

        private void SerializeContractFail(ConfigNode n) => SerializeContractPenalty(n);
        private static void DeserializeContractFail(ConfigNode n, GameAction a) => DeserializeContractPenalty(n, a);

        private void SerializeContractCancel(ConfigNode n)
        {
            SerializeContractPenalty(n);
            if (ContractRequirementsLost)
                n.AddValue(ContractRequirementsLostKey, "True");
        }

        private static void DeserializeContractCancel(ConfigNode n, GameAction a)
        {
            DeserializeContractPenalty(n, a);
            bool requirementsLost;
            a.ContractRequirementsLost = bool.TryParse(n.GetValue(ContractRequirementsLostKey), out requirementsLost)
                && requirementsLost;
        }

        private void SerializeContractPenalty(ConfigNode n)
        {
            if (ContractId != null) n.AddValue("contractId", ContractId);
            if (ContractTitle != null) n.AddValue("contractTitle", ContractTitle);
            n.AddValue("fundsPenalty", FundsPenalty.ToString("R", IC));
            n.AddValue("repPenalty", RepPenalty.ToString("R", IC));
        }

        private static void DeserializeContractPenalty(ConfigNode n, GameAction a)
        {
            a.ContractId = n.GetValue("contractId");
            a.ContractTitle = n.GetValue("contractTitle");
            TryParseFloat(n, "fundsPenalty", out a.FundsPenalty);
            TryParseFloat(n, "repPenalty", out a.RepPenalty);
        }

        private void SerializeRepEarning(ConfigNode n)
        {
            n.AddValue("nominalRep", NominalRep.ToString("R", IC));
            n.AddValue("repSource", ((int)RepSource).ToString(IC));
        }

        private static void DeserializeRepEarning(ConfigNode n, GameAction a)
        {
            TryParseFloat(n, "nominalRep", out a.NominalRep);
            TryParseEnum(n, "repSource", out a.RepSource);
        }

        private void SerializeRepPenalty(ConfigNode n)
        {
            n.AddValue("nominalPenalty", NominalPenalty.ToString("R", IC));
            n.AddValue("repPenaltySource", ((int)RepPenaltySource).ToString(IC));
        }

        private static void DeserializeRepPenalty(ConfigNode n, GameAction a)
        {
            TryParseFloat(n, "nominalPenalty", out a.NominalPenalty);
            TryParseEnum(n, "repPenaltySource", out a.RepPenaltySource);
        }

        private void SerializeKerbalAssignment(ConfigNode n)
        {
            if (KerbalName != null) n.AddValue("kerbalName", KerbalName);
            if (KerbalRole != null) n.AddValue("kerbalRole", KerbalRole);
            n.AddValue("startUT", StartUT.ToString("R", IC));
            if (!float.IsNaN(EndUT))
                n.AddValue("endUT", EndUT.ToString("R", IC));
            n.AddValue("endState", ((int)KerbalEndStateField).ToString(IC));
            n.AddValue("xpGained", XpGained.ToString("R", IC));
        }

        private static void DeserializeKerbalAssignment(ConfigNode n, GameAction a)
        {
            a.KerbalName = n.GetValue("kerbalName");
            a.KerbalRole = n.GetValue("kerbalRole");
            TryParseFloat(n, "startUT", out a.StartUT);
            if (!TryParseFloat(n, "endUT", out a.EndUT))
                a.EndUT = float.NaN;
            TryParseEnum(n, "endState", out a.KerbalEndStateField);
            TryParseFloat(n, "xpGained", out a.XpGained);
        }

        private void SerializeKerbalHire(ConfigNode n)
        {
            if (KerbalName != null) n.AddValue("kerbalName", KerbalName);
            if (KerbalRole != null) n.AddValue("kerbalRole", KerbalRole);
            n.AddValue("hireCost", HireCost.ToString("R", IC));
        }

        private static void DeserializeKerbalHire(ConfigNode n, GameAction a)
        {
            a.KerbalName = n.GetValue("kerbalName");
            a.KerbalRole = n.GetValue("kerbalRole");
            TryParseFloat(n, "hireCost", out a.HireCost);
        }

        private void SerializeKerbalRescue(ConfigNode n)
        {
            if (KerbalName != null) n.AddValue("kerbalName", KerbalName);
            if (KerbalRole != null) n.AddValue("kerbalRole", KerbalRole);
            n.AddValue("endUT", EndUT.ToString("R", IC));
        }

        private static void DeserializeKerbalRescue(ConfigNode n, GameAction a)
        {
            a.KerbalName = n.GetValue("kerbalName");
            a.KerbalRole = n.GetValue("kerbalRole");
            TryParseFloat(n, "endUT", out a.EndUT);
        }

        private void SerializeKerbalStandIn(ConfigNode n)
        {
            if (KerbalName != null) n.AddValue("kerbalName", KerbalName);
            if (KerbalRole != null) n.AddValue("kerbalRole", KerbalRole);
            if (ReplacesKerbal != null) n.AddValue("replacesKerbal", ReplacesKerbal);
            n.AddValue("courage", Courage.ToString("R", IC));
            n.AddValue("stupidity", Stupidity.ToString("R", IC));
        }

        private static void DeserializeKerbalStandIn(ConfigNode n, GameAction a)
        {
            a.KerbalName = n.GetValue("kerbalName");
            a.KerbalRole = n.GetValue("kerbalRole");
            a.ReplacesKerbal = n.GetValue("replacesKerbal");
            TryParseFloat(n, "courage", out a.Courage);
            TryParseFloat(n, "stupidity", out a.Stupidity);
        }

        private void SerializeKerbalExperience(ConfigNode n)
        {
            if (KerbalName != null) n.AddValue("kerbalName", KerbalName);
            if (KerbalRole != null) n.AddValue("kerbalRole", KerbalRole);
            if (KerbalCareerEntries != null) n.AddValue("careerEntries", KerbalCareerEntries);
        }

        private static void DeserializeKerbalExperience(ConfigNode n, GameAction a)
        {
            a.KerbalName = n.GetValue("kerbalName");
            a.KerbalRole = n.GetValue("kerbalRole");
            a.KerbalCareerEntries = n.GetValue("careerEntries");
        }

        private void SerializeKerbalRecovered(ConfigNode n)
        {
            if (KerbalName != null) n.AddValue("kerbalName", KerbalName);
            if (KerbalRole != null) n.AddValue("kerbalRole", KerbalRole);
        }

        private static void DeserializeKerbalRecovered(ConfigNode n, GameAction a)
        {
            a.KerbalName = n.GetValue("kerbalName");
            a.KerbalRole = n.GetValue("kerbalRole");
        }

        private void SerializeVesselRecovered(ConfigNode n)
        {
            if (RecoveredVesselName != null) n.AddValue("vesselName", RecoveredVesselName);
            if (RecoveredVesselPid != 0u)
                n.AddValue("vesselPid", RecoveredVesselPid.ToString(IC));
        }

        private static void DeserializeVesselRecovered(ConfigNode n, GameAction a)
        {
            a.RecoveredVesselName = n.GetValue("vesselName");
            string pidStr = n.GetValue("vesselPid");
            uint pid;
            if (pidStr != null && uint.TryParse(pidStr, NumberStyles.Integer, IC, out pid))
                a.RecoveredVesselPid = pid;
        }

        private void SerializeResourceScanUnlocked(ConfigNode n)
        {
            if (ScanBodyName != null) n.AddValue("scanBody", ScanBodyName);
            if (!string.IsNullOrEmpty(ScanBiomeName)) n.AddValue("scanBiome", ScanBiomeName);
        }

        private static void DeserializeResourceScanUnlocked(ConfigNode n, GameAction a)
        {
            a.ScanBodyName = n.GetValue("scanBody");
            string biome = n.GetValue("scanBiome");
            a.ScanBiomeName = string.IsNullOrEmpty(biome) ? null : biome;
        }

        // ScienceLabEarning: the pool credit and how the lab paid it out; no subject. An
        // orbital survey's bonus also carries the surveyed body (absent on a lab row).
        private void SerializeScienceLabEarning(ConfigNode n)
        {
            n.AddValue("scienceAwarded", ScienceAwarded.ToString("R", IC));
            n.AddValue("method", ((int)Method).ToString(IC));
            if (!string.IsNullOrEmpty(ScanBodyName)) n.AddValue("scanBody", ScanBodyName);
        }

        private static void DeserializeScienceLabEarning(ConfigNode n, GameAction a)
        {
            TryParseFloat(n, "scienceAwarded", out a.ScienceAwarded);
            TryParseEnum(n, "method", out a.Method);
            string body = n.GetValue("scanBody");
            a.ScanBodyName = string.IsNullOrEmpty(body) ? null : body;
        }

        // StockPoolAdjustment: the pool and the signed amount.
        private void SerializeStockPoolAdjustment(ConfigNode n)
        {
            n.AddValue("adjustPool", ((int)AdjustmentPool).ToString(IC));
            n.AddValue("adjustAmount", AdjustmentAmount.ToString("R", IC));
        }

        private static void DeserializeStockPoolAdjustment(ConfigNode n, GameAction a)
        {
            TryParseEnum(n, "adjustPool", out a.AdjustmentPool);
            string amount = n.GetValue("adjustAmount");
            double parsed;
            if (amount != null && double.TryParse(amount, NS, IC, out parsed)
                && !double.IsNaN(parsed) && !double.IsInfinity(parsed))
                a.AdjustmentAmount = parsed;
        }

        private void SerializeFacilityUpgrade(ConfigNode n)
        {
            if (FacilityId != null) n.AddValue("facilityId", FacilityId);
            n.AddValue("toLevel", ToLevel.ToString(IC));
            n.AddValue("facilityCost", FacilityCost.ToString("R", IC));
            if (FacilityDowngrade)
                n.AddValue(FacilityDowngradeKey, FacilityDowngrade.ToString());
        }

        private static void DeserializeFacilityUpgrade(ConfigNode n, GameAction a)
        {
            a.FacilityId = n.GetValue("facilityId");
            TryParseInt(n, "toLevel", out a.ToLevel);
            TryParseFloat(n, "facilityCost", out a.FacilityCost);
            string downgradeStr = n.GetValue(FacilityDowngradeKey);
            if (downgradeStr != null)
                bool.TryParse(downgradeStr, out a.FacilityDowngrade);
        }

        /// <summary>Sparse ledger key of <see cref="FacilityDowngrade"/>.</summary>
        internal const string FacilityDowngradeKey = "facilityDowngrade";

        private void SerializeFacilityDestruction(ConfigNode n)
        {
            if (FacilityId != null) n.AddValue("facilityId", FacilityId);
        }

        private static void DeserializeFacilityDestruction(ConfigNode n, GameAction a)
        {
            a.FacilityId = n.GetValue("facilityId");
        }

        private void SerializeFacilityRepair(ConfigNode n)
        {
            if (FacilityId != null) n.AddValue("facilityId", FacilityId);
            n.AddValue("facilityCost", FacilityCost.ToString("R", IC));
        }

        private static void DeserializeFacilityRepair(ConfigNode n, GameAction a)
        {
            a.FacilityId = n.GetValue("facilityId");
            TryParseFloat(n, "facilityCost", out a.FacilityCost);
        }

        private void SerializeStrategyActivate(ConfigNode n)
        {
            if (StrategyId != null) n.AddValue("strategyId", StrategyId);
            n.AddValue("sourceResource", ((int)SourceResource).ToString(IC));
            n.AddValue("targetResource", ((int)TargetResource).ToString(IC));
            n.AddValue("commitment", Commitment.ToString("R", IC));
            n.AddValue("setupCost", SetupCost.ToString("R", IC));
            n.AddValue("setupSci", SetupScienceCost.ToString("R", IC));
            n.AddValue("setupRep", SetupReputationCost.ToString("R", IC));
        }

        private static void DeserializeStrategyActivate(ConfigNode n, GameAction a)
        {
            a.StrategyId = n.GetValue("strategyId");
            TryParseEnum(n, "sourceResource", out a.SourceResource);
            TryParseEnum(n, "targetResource", out a.TargetResource);
            TryParseFloat(n, "commitment", out a.Commitment);
            TryParseFloat(n, "setupCost", out a.SetupCost);
            TryParseFloat(n, "setupSci", out a.SetupScienceCost);
            TryParseFloat(n, "setupRep", out a.SetupReputationCost);
        }

        private void SerializeStrategyDeactivate(ConfigNode n)
        {
            if (StrategyId != null) n.AddValue("strategyId", StrategyId);
        }

        private static void DeserializeStrategyDeactivate(ConfigNode n, GameAction a)
        {
            a.StrategyId = n.GetValue("strategyId");
        }

        private void SerializeFundsInitial(ConfigNode n)
        {
            n.AddValue("initialFunds", InitialFunds.ToString("R", IC));
            if (InitialFundsConfirmedZero)
                n.AddValue(InitialFundsConfirmedZeroKey, InitialFundsConfirmedZero.ToString());
        }

        private static void DeserializeFundsInitial(ConfigNode n, GameAction a)
        {
            TryParseFloat(n, "initialFunds", out a.InitialFunds);
            string confirmedZero = n.GetValue(InitialFundsConfirmedZeroKey);
            if (confirmedZero != null)
                bool.TryParse(confirmedZero, out a.InitialFundsConfirmedZero);
        }

        /// <summary>Sparse FundsInitial key for <see cref="InitialFundsConfirmedZero"/>.</summary>
        internal const string InitialFundsConfirmedZeroKey = "initialFundsConfirmedZero";

        private void SerializeScienceInitial(ConfigNode n)
        {
            n.AddValue("initialScience", InitialScience.ToString("R", IC));
        }

        private static void DeserializeScienceInitial(ConfigNode n, GameAction a)
        {
            TryParseFloat(n, "initialScience", out a.InitialScience);
        }

        private void SerializeReputationInitial(ConfigNode n)
        {
            n.AddValue("initialReputation", InitialReputation.ToString("R", IC));
            if (SeedPendingRecordingIds == null || SeedPendingRecordingIds.Count == 0
                || double.IsNaN(SeedCaptureUT))
                return;
            n.AddValue(SeedCaptureUTKey, SeedCaptureUT.ToString("R", IC));
            for (int i = 0; i < SeedPendingRecordingIds.Count; i++)
            {
                if (!string.IsNullOrEmpty(SeedPendingRecordingIds[i]))
                    n.AddValue(SeedPendingRecordingKey, SeedPendingRecordingIds[i]);
            }
        }

        private static void DeserializeReputationInitial(ConfigNode n, GameAction a)
        {
            TryParseFloat(n, "initialReputation", out a.InitialReputation);
            string captureUT = n.GetValue(SeedCaptureUTKey);
            string[] pending = n.GetValues(SeedPendingRecordingKey);
            double ut;
            if (captureUT == null || pending == null || pending.Length == 0
                || !double.TryParse(captureUT, NS, IC, out ut))
                return;
            a.SeedCaptureUT = ut;
            a.SeedPendingRecordingIds = new List<string>(pending);
        }

        // ---- Parse helpers ----

        private static bool TryParseFloat(ConfigNode n, string key, out float result)
        {
            result = 0f;
            string val = n.GetValue(key);
            if (val == null) return false;
            return float.TryParse(val, NS, IC, out result);
        }

        private static bool TryParseInt(ConfigNode n, string key, out int result)
        {
            result = 0;
            string val = n.GetValue(key);
            if (val == null) return false;
            return int.TryParse(val, NumberStyles.Integer, IC, out result);
        }

        private static bool TryParseEnum<T>(ConfigNode n, string key, out T result) where T : struct
        {
            result = default(T);
            string val = n.GetValue(key);
            if (val == null) return false;
            int intVal;
            if (!int.TryParse(val, NumberStyles.Integer, IC, out intVal)) return false;
            if (!Enum.IsDefined(typeof(T), intVal))
            {
                // A value this build's enum does not define - almost always a save
                // written by a NEWER build (every enum here is extended by appending).
                // The field keeps default(T), which for a source enum is member 0, so the
                // row silently changes meaning: a rolled-back reader would read a
                // FundsSpendingSource.StrategyConverter debit as an untagged VesselBuild
                // one. Say so once per occurrence rather than letting the downgrade be
                // invisible - it is the only signal that distinguishes "this build is
                // older than the save" from a genuine data defect.
                ParsekLog.Warn("GameAction",
                    $"Enum value {intVal.ToString(IC)} is not defined on {typeof(T).Name} " +
                    $"(key '{key}') - keeping the default; this save was probably written " +
                    "by a newer build");
                return false;
            }
            result = (T)(object)intVal;
            return true;
        }
    }
}
