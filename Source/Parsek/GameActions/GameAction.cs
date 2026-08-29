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

        // ---- Route actions (logistics supply routes; design doc §6 / §10) ----

        /// <summary>
        /// Scheduler decided the cycle is good to go after destination + origin + funds
        /// checks (design doc §6.1 step 5). Carries <c>RouteId</c>, <c>RouteCycleId</c>,
        /// and the scheduled dispatch UT. Also carries the sparse
        /// <see cref="GameAction.RouteSendOnce"/> marker when the cycle was armed by
        /// the player's Send Once one-shot instead of the auto-dispatch loop. A durable
        /// player resume is recorded explicitly as <see cref="RouteResumed"/>; a
        /// RouteDispatched row alone only proves a cycle fired.
        /// </summary>
        RouteDispatched       = 23,

        /// <summary>
        /// Physical/funds debit applied to origin (design doc §6.1 step 5 / §6.3): non-KSC
        /// resource or inventory removal, or KSC funds charge in Career. Separated from
        /// <see cref="RouteDispatched"/> so a future module can sequence the actual debit
        /// at a different tier slot from the dispatch decision (e.g. so the funds module
        /// can reuse its existing ContractFail-style penalty path for the KSC charge while
        /// the route module owns the dispatch counter).
        /// </summary>
        RouteCargoDebited     = 24,

        /// <summary>
        /// Delivery boundary reached (design doc §6.3). Carries the actual per-resource
        /// delivery manifest. For partial-fill (§10.5) the requested manifest is also
        /// carried so the player can see requested-vs-actual instead of silent loss.
        /// </summary>
        RouteCargoDelivered   = 25,

        /// <summary>
        /// Player Pause action, OR auto-pause when status transitions to
        /// MissingSourceRecording / SourceChanged (design doc §6.6, §10.6). The reason is
        /// captured in <see cref="GameAction.RouteEndpointReason"/>: <c>player-pause</c>,
        /// the armed pause-after-cycle <c>delivered-then-paused</c> /
        /// <c>delivered-partial-then-paused</c>, or the LIVE source-revalidation
        /// auto-flips <c>AutoPause:MissingSourceRecording</c> /
        /// <c>AutoPause:SourceChanged</c> (OnLoad revalidation passes stay silent -
        /// caller-gated via <c>RouteStore.RevalidateSources(reason, liveEmitUT)</c>).
        /// An EndpointLost transition emits <see cref="RouteEndpointLost"/> instead.
        /// §10.6 needs this row in the timeline so revert past a dispatch can
        /// correctly suspend future cycles.
        /// </summary>
        RoutePaused           = 26,

        /// <summary>
        /// Endpoint resolution failed (design doc §10.1, §10.2). Distinct from
        /// <see cref="RoutePaused"/> because the recovery contract differs — endpoint
        /// loss may auto-recover through surface-proximity fallback while a player pause
        /// can only be cleared by explicit unpause. Reason text is in
        /// <see cref="GameAction.RouteEndpointReason"/>.
        /// </summary>
        RouteEndpointLost     = 27,

        /// <summary>
        /// Deferred per-cycle recovery credit for a Career, KSC-origin Supply Route
        /// (logistics-recovery-credit plan, design doc section 6.1). Emitted ONE
        /// dispatch interval after a dispatched cycle (at the next dock crossing),
        /// keyed on the PRIOR dispatched cycle it pays back via
        /// <see cref="GameAction.RouteCycleId"/>. The credit amount (the source
        /// tree's per-run recovery, summed by
        /// <c>RouteRunCostCalculator.SumRecoveredCredits</c>) is stored as a
        /// positive magnitude in <see cref="GameAction.RouteKscFundsCost"/>; the
        /// action TYPE carries the credit direction. Processed by
        /// <see cref="FundsModule"/> as a fund EARNING so a rewind / re-fly /
        /// tombstone reverses it through the same recalc + patch path that reverses
        /// a recovery <see cref="GameActionType.FundsEarning"/>.
        /// </summary>
        RouteRecoveryCredited = 28,

        /// <summary>
        /// Per-window pickup debit (logistics M3, plan Phase 4 / OQ4 / D6): cargo
        /// flowed FROM a stop's endpoint ONTO the transport across a connection
        /// window (the reverse of <see cref="RouteCargoDelivered"/>), and the
        /// witnessed amount was PHYSICALLY removed from the endpoint vessel at the
        /// dock crossing. The mirror of <see cref="RouteCargoDebited"/> but for a
        /// per-window pickup ENDPOINT, not the dispatch-time origin. Carries the
        /// ACTUAL debited manifest (<see cref="GameAction.RouteResourceManifest"/>),
        /// the requested-on-shortfall manifest
        /// (<see cref="GameAction.RouteRequestedResourceManifest"/>) when the source
        /// came up short / unresolved, and the endpoint pid
        /// (<see cref="GameAction.RouteOriginVesselPid"/>). Emits ZERO funds
        /// (loaded-en-route cargo debits its physical source, never funds, design
        /// D6); no resource module consumes it (the physical removal happened LIVE
        /// at emit and is reverted by the rewind quicksave, exactly like
        /// <see cref="RouteCargoDebited"/>'s physical half), so it is NOT a
        /// resource-impacting action and supersede does NOT strict-block on it.
        /// Sequence is assigned AFTER <see cref="RouteDispatched"/> (Seq0) so the
        /// ledger walker sees dispatch first at the shared UT.
        /// </summary>
        RouteCargoPickedUp = 29,

        /// <summary>
        /// Player Activate action turning a <see cref="RoutePaused"/> route back into
        /// an auto-dispatching one (route-timeline events; design doc §6.6). The
        /// explicit durable-resume marker: earlier revisions inferred resumption from
        /// the next <see cref="RouteDispatched"/> row, which leaves the timeline blind
        /// to a resume whose first cycle blocks on eligibility (or never fires). The
        /// reason is captured in <see cref="GameAction.RouteEndpointReason"/>
        /// (<c>player-activate</c>; <c>AutoResume:SourcesRestored</c> when a LIVE
        /// source-revalidation pass restores an Active-family status from
        /// MissingSourceRecording - a restored Paused emits nothing, it never
        /// resumed; or <c>AutoResume:CatchUp</c> when a live pass repairs an
        /// unmatched RoutePaused row left by a flip that had to run silently in
        /// load context). A Send Once arm is NOT a
        /// resume — it stamps <see cref="GameAction.RouteSendOnce"/> on its dispatched
        /// row instead. Free-standing like every route row (no <c>RecordingId</c>);
        /// retired at rewind by <c>RouteLedgerRetire</c> alongside types 23-29.
        /// </summary>
        RouteResumed = 30,

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
        StrategyScienceCredit = 33
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
        StrategyConverter = 7
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

        // ---- Derived fields (recalculated, NOT serialized) ----

        /// <summary>
        /// Whether this action's effects are active after recalculation.
        /// Set by first-tier modules (e.g., MilestonesModule sets false for duplicate milestones,
        /// ContractsModule sets false for duplicate completions). Defaults to true.
        /// NOT serialized — recomputed from scratch on every recalculation walk.
        /// </summary>
        public bool Effective = true;

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

        /// <summary>Science points KSP actually credited (immutable). Post-transmit-scalar.</summary>
        public float ScienceAwarded;

        /// <summary>How the science was collected.</summary>
        public ScienceMethod Method;

        /// <summary>Transmission efficiency for the experiment (0.0 to 1.0).</summary>
        public float TransmitScalar;

        /// <summary>Total science this subject can yield (scienceCap).</summary>
        public float SubjectMaxValue;

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

        // ---- Contract fields ----

        /// <summary>KSP's unique contract instance ID.</summary>
        public string ContractId;

        /// <summary>Contract type, e.g. "ExploreBody".</summary>
        public string ContractType;

        /// <summary>Human-readable contract title.</summary>
        public string ContractTitle;

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

        // ---- Route fields ----

        /// <summary>
        /// Stable identifier of the logistics route this action belongs to
        /// (design doc §6, §10). Null on non-route actions. Skeleton-only:
        /// route entities themselves are not yet defined in the codebase, so
        /// this is treated as opaque string identity.
        /// </summary>
        public string RouteId;

        /// <summary>
        /// Per-dispatch cycle identifier — groups one
        /// <see cref="GameActionType.RouteDispatched"/> row with its matching
        /// <see cref="GameActionType.RouteCargoDebited"/> and
        /// <see cref="GameActionType.RouteCargoDelivered"/> rows. Used by future
        /// dispatch/delivery walkers to correlate within-cycle effects. Null on
        /// non-cycle-scoped route actions (e.g. <see cref="GameActionType.RoutePaused"/>
        /// at the route level, not a specific cycle).
        /// </summary>
        public string RouteCycleId;

        /// <summary>
        /// 0-based stop index inside the route's stop list (design doc §6.3). Sentinel
        /// value -1 means "not applicable / route-level event". v1 routes have a single
        /// stop (§11), so the only non-sentinel value in v1 will be 0. Persisted as a
        /// non-negative integer so future multi-stop routes can populate it without a
        /// schema change.
        /// </summary>
        public int RouteStopIndex = -1;

        /// <summary>
        /// Per-resource signed delivered/debited amount keyed by stock resource name
        /// (design doc §6.3, §6.5). For <see cref="GameActionType.RouteCargoDelivered"/>
        /// the value is positive amount actually delivered to the destination
        /// (post-clamp by <c>maxAmount</c>). For <see cref="GameActionType.RouteCargoDebited"/>
        /// the value is positive amount removed from the origin. Both directions
        /// intentionally store positive magnitudes — the action type carries the sign.
        /// <para>
        /// <c>maxAmount</c> is deliberately NOT carried — delivery is amount-only
        /// (design doc §11, §6.5). Tank capacity is a destination property and is
        /// re-read each tick by the scheduler; carrying it on the ledger row would
        /// be a stale snapshot.
        /// </para>
        /// <para>Null or empty when the action carries no resource manifest (e.g. the
        /// KSC-funds-only debit case, where <see cref="RouteKscFundsCost"/> is set instead).</para>
        /// </summary>
        /// <remarks>
        /// No <c>RouteInventoryManifest</c> field exists yet. The dispatch/delivery
        /// scheduler that will emit route actions is not built, so the route-action
        /// inventory field shape waits until that scheduler clarifies its needs. The
        /// <see cref="InventoryPayloadItem"/> type used elsewhere by the route-proof
        /// recording metadata is already available when that field is added.
        /// </remarks>
        public Dictionary<string, double> RouteResourceManifest;

        /// <summary>
        /// Requested per-resource amounts, populated only when the actual fell
        /// short of the request for at least one resource. Same keying as
        /// <see cref="RouteResourceManifest"/>. On
        /// <see cref="GameActionType.RouteCargoDelivered"/> rows it is the
        /// partial-fill request (design doc section 10.5); on
        /// <see cref="GameActionType.RouteCargoDebited"/> rows with a physical
        /// origin debit (M1) it is the requested removal when the origin came
        /// up short or unresolved at apply time (design D3 clamp-and-warn).
        /// Null when the actual met the request in full - saves a few bytes on
        /// the common case. The pair (requested, actual) is what UI / future
        /// dispatch tuning reads to expose "delivered X / Y" badges.
        /// </summary>
        public Dictionary<string, double> RouteRequestedResourceManifest;

        /// <summary>
        /// M3 picked-up stored-part inventory payloads (Phase 5, design D7),
        /// keyed by exact <see cref="InventoryPayloadItem.IdentityHash"/>. On a
        /// <see cref="GameActionType.RouteCargoPickedUp"/> row this is the ACTUAL
        /// inventory removed from the per-window pickup endpoint (identity intact,
        /// the removed quantity), the inventory analogue of
        /// <see cref="RouteResourceManifest"/>. The transport credit is
        /// bookkeeping (the transport never materializes), so this row is the only
        /// record the inventory came aboard. Sparse: null/empty writes nothing, so
        /// a resource-only pickup row round-trips byte-identically. No
        /// <c>RouteCargoDelivered</c> row carries inventory (delivery inventory is
        /// applied physically, not recorded on the ledger row), so this is the
        /// FIRST ledger-row inventory manifest.
        /// <para>INTERNAL (not public) because <see cref="InventoryPayloadItem"/>
        /// is an internal route-proof type; the public ledger surface stays
        /// resource-keyed. Tests reach it via InternalsVisibleTo.</para>
        /// </summary>
        internal List<InventoryPayloadItem> RouteInventoryManifest;

        /// <summary>
        /// Requested inventory payloads, populated only when the actual fell short
        /// of the witnessed pickup for at least one identity (the source no longer
        /// held a witnessed item at debit time). Same keying as
        /// <see cref="RouteInventoryManifest"/>; the inventory analogue of
        /// <see cref="RouteRequestedResourceManifest"/>. Null when the actual met
        /// the request in full. INTERNAL for the same reason as
        /// <see cref="RouteInventoryManifest"/>.
        /// </summary>
        internal List<InventoryPayloadItem> RouteRequestedInventoryManifest;

        /// <summary>
        /// Persistent id of the live origin vessel a physical origin debit
        /// removed cargo from (M1). Stored sparsely on
        /// <see cref="GameActionType.RouteCargoDebited"/> rows for attribution
        /// diagnostics and the future M3 escrow; 0 on KSC-origin rows, legacy
        /// non-loop rows, and rows whose origin was unresolved at apply time.
        /// </summary>
        public uint RouteOriginVesselPid;

        /// <summary>
        /// KSC funds charge in funds-units (design doc §6.1 step 5: the Career-mode
        /// KSC-origin dispatch cost). Zero when the dispatch had no KSC funds component
        /// (Science / Sandbox modes or non-KSC origins). Stored on
        /// <see cref="GameActionType.RouteCargoDebited"/> rows.
        /// </summary>
        public float RouteKscFundsCost;

        /// <summary>
        /// True on a <see cref="GameActionType.RouteDispatched"/> row whose cycle was
        /// armed by the player's Send Once one-shot (route-timeline events): the route
        /// dispatches this single cycle and auto-pauses after delivery, so the
        /// dispatched row plus the following <see cref="GameActionType.RoutePaused"/>
        /// row bracket the individual run in the timeline. Stamped from the persisted
        /// <c>Route.SendOnceArmed</c> flag at emit time. Sparse in the codec (omitted
        /// when false) so ordinary auto-cycle rows stay byte-identical. Consumed by
        /// <see cref="RouteModule"/> to accept a send-once dispatch on a paused route
        /// without the dispatch-on-paused warning.
        /// </summary>
        public bool RouteSendOnce;

        /// <summary>
        /// Short human/machine-readable reason for a
        /// <see cref="GameActionType.RoutePaused"/>,
        /// <see cref="GameActionType.RouteResumed"/> or
        /// <see cref="GameActionType.RouteEndpointLost"/> row (design doc §6.6, §10.1,
        /// §10.2, §10.15, §10.16). Typical values: <c>"PlayerPause"</c>,
        /// <c>"AutoPause:EndpointLost"</c>, <c>"AutoPause:MissingSourceRecording"</c>,
        /// <c>"AutoPause:SourceChanged"</c>, <c>"EndpointLost:OrbitalNoFallback"</c>.
        /// Free-form by design — the route module logs it but does not branch on it,
        /// so adding new reasons is a non-breaking change.
        /// </summary>
        public string RouteEndpointReason;

        // ---- Initial seed fields ----

        /// <summary>Career starting funds, extracted from save file.</summary>
        public float InitialFunds;

        /// <summary>Existing science balance when Parsek is first installed mid-career.</summary>
        public float InitialScience;

        /// <summary>Existing reputation when Parsek is first installed mid-career.</summary>
        public float InitialReputation;

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

        // M3 Phase 5 (D7): per-row inventory manifest node names. The picked-up
        // inventory + requested-on-shortfall inventory each serialize as a sparse
        // child node holding one ITEM subnode per stored-part identity; omitted
        // when empty so a resource-only RouteCargoPickedUp row stays
        // byte-identical (D9). Distinct names from the route-side
        // INVENTORY_*_MANIFEST nodes (RouteCodec) - this is the ledger row codec.
        private const string RouteInventoryManifestNode = "ROUTE_INVENTORY_MANIFEST";
        private const string RouteRequestedInventoryManifestNode = "ROUTE_REQUESTED_INVENTORY_MANIFEST";
        private const string RouteInventoryItemNode = "ITEM";
        private const string RouteInventoryStoredResourcesNode = "STORED_RESOURCES";
        private const string RouteInventoryStoredPartNode = "STOREDPART";

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
                case GameActionType.RouteDispatched:
                    SerializeRouteDispatched(node);
                    break;
                case GameActionType.RouteCargoDebited:
                    SerializeRouteCargoDebited(node);
                    break;
                case GameActionType.RouteCargoDelivered:
                    SerializeRouteCargoDelivered(node);
                    break;
                case GameActionType.RoutePaused:
                    SerializeRoutePaused(node);
                    break;
                case GameActionType.RouteEndpointLost:
                    SerializeRouteEndpointLost(node);
                    break;
                case GameActionType.RouteRecoveryCredited:
                    SerializeRouteRecoveryCredited(node);
                    break;
                case GameActionType.RouteCargoPickedUp:
                    SerializeRouteCargoPickedUp(node);
                    break;
                case GameActionType.RouteResumed:
                    SerializeRouteResumed(node);
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
                case GameActionType.RouteDispatched:
                    DeserializeRouteDispatched(node, a);
                    break;
                case GameActionType.RouteCargoDebited:
                    DeserializeRouteCargoDebited(node, a);
                    break;
                case GameActionType.RouteCargoDelivered:
                    DeserializeRouteCargoDelivered(node, a);
                    break;
                case GameActionType.RoutePaused:
                    DeserializeRoutePaused(node, a);
                    break;
                case GameActionType.RouteEndpointLost:
                    DeserializeRouteEndpointLost(node, a);
                    break;
                case GameActionType.RouteRecoveryCredited:
                    DeserializeRouteRecoveryCredited(node, a);
                    break;
                case GameActionType.RouteCargoPickedUp:
                    DeserializeRouteCargoPickedUp(node, a);
                    break;
                case GameActionType.RouteResumed:
                    DeserializeRouteResumed(node, a);
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
            TryParseFloat(n, "startUT", out a.StartUT);
            if (!TryParseFloat(n, "endUT", out a.EndUT))
                a.EndUT = float.NaN;
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
        }

        private static void DeserializeMilestone(ConfigNode n, GameAction a)
        {
            a.MilestoneId = n.GetValue("milestoneId");
            TryParseFloat(n, "milestoneFundsAwarded", out a.MilestoneFundsAwarded);
            TryParseFloat(n, "milestoneRepAwarded", out a.MilestoneRepAwarded);
            // Backward compat: pre-fix saves have no milestoneSciAwarded key; default to 0.
            TryParseFloat(n, "milestoneSciAwarded", out a.MilestoneScienceAwarded);
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
            n.AddValue("fundsReward", FundsReward.ToString("R", IC));
            n.AddValue("repReward", RepReward.ToString("R", IC));
            n.AddValue("scienceReward", ScienceReward.ToString("R", IC));
        }

        private static void DeserializeContractComplete(ConfigNode n, GameAction a)
        {
            a.ContractId = n.GetValue("contractId");
            TryParseFloat(n, "fundsReward", out a.FundsReward);
            TryParseFloat(n, "repReward", out a.RepReward);
            TryParseFloat(n, "scienceReward", out a.ScienceReward);
        }

        private void SerializeContractFail(ConfigNode n) => SerializeContractPenalty(n);
        private static void DeserializeContractFail(ConfigNode n, GameAction a) => DeserializeContractPenalty(n, a);

        private void SerializeContractCancel(ConfigNode n) => SerializeContractPenalty(n);
        private static void DeserializeContractCancel(ConfigNode n, GameAction a) => DeserializeContractPenalty(n, a);

        private void SerializeContractPenalty(ConfigNode n)
        {
            if (ContractId != null) n.AddValue("contractId", ContractId);
            n.AddValue("fundsPenalty", FundsPenalty.ToString("R", IC));
            n.AddValue("repPenalty", RepPenalty.ToString("R", IC));
        }

        private static void DeserializeContractPenalty(ConfigNode n, GameAction a)
        {
            a.ContractId = n.GetValue("contractId");
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

        private void SerializeFacilityUpgrade(ConfigNode n)
        {
            if (FacilityId != null) n.AddValue("facilityId", FacilityId);
            n.AddValue("toLevel", ToLevel.ToString(IC));
            n.AddValue("facilityCost", FacilityCost.ToString("R", IC));
        }

        private static void DeserializeFacilityUpgrade(ConfigNode n, GameAction a)
        {
            a.FacilityId = n.GetValue("facilityId");
            TryParseInt(n, "toLevel", out a.ToLevel);
            TryParseFloat(n, "facilityCost", out a.FacilityCost);
        }

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
        }

        private static void DeserializeFundsInitial(ConfigNode n, GameAction a)
        {
            TryParseFloat(n, "initialFunds", out a.InitialFunds);
        }

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
        }

        private static void DeserializeReputationInitial(ConfigNode n, GameAction a)
        {
            TryParseFloat(n, "initialReputation", out a.InitialReputation);
        }

        // ---- Route action serialization helpers ----
        //
        // Manifest encoding: each non-zero/non-empty manifest serializes as one
        //   resource = <name>|<amount-R-invariant>
        // line per entry. Skipping zero/null fields keeps the on-disk shape small
        // and lets future readers detect "no manifest" via the absence of the key.

        private void SerializeRouteDispatched(ConfigNode n)
        {
            WriteRouteCommon(n);
            // Sparse Send Once provenance (route-timeline events): only a one-shot
            // cycle writes the key, so auto-cycle rows stay byte-identical.
            if (RouteSendOnce)
                n.AddValue("routeSendOnce", RouteSendOnce.ToString());
        }

        private static void DeserializeRouteDispatched(ConfigNode n, GameAction a)
        {
            ReadRouteCommon(n, a);
            string sendOnceStr = n.GetValue("routeSendOnce");
            if (sendOnceStr != null)
                bool.TryParse(sendOnceStr, out a.RouteSendOnce);
        }

        private void SerializeRouteCargoDebited(ConfigNode n)
        {
            WriteRouteCommon(n);
            WriteResourceManifest(n, "resource", RouteResourceManifest);
            // M1 physical-origin-debit additions, both sparse: the requested
            // manifest only exists when the origin came up short/unresolved
            // at apply time, and the origin pid only on physical debits.
            // KSC rows and legacy non-loop rows carry neither, so their
            // on-disk shape is byte-identical to the pre-M1 codec.
            WriteResourceManifest(n, "requestedResource", RouteRequestedResourceManifest);
            // M3 Phase 5 (D7 carve-out lift): the origin INVENTORY debit's actual
            // + requested-on-shortfall payloads, sparse. Empty/null writes nothing
            // so a resource-only origin debit row stays byte-identical to the M1
            // codec.
            WriteInventoryManifest(n, RouteInventoryManifestNode, RouteInventoryManifest);
            WriteInventoryManifest(n, RouteRequestedInventoryManifestNode, RouteRequestedInventoryManifest);
            if (RouteOriginVesselPid != 0u)
                n.AddValue("routeOriginVesselPid", RouteOriginVesselPid.ToString(IC));
            if (RouteKscFundsCost != 0f)
                n.AddValue("routeKscFundsCost", RouteKscFundsCost.ToString("R", IC));
        }

        private static void DeserializeRouteCargoDebited(ConfigNode n, GameAction a)
        {
            ReadRouteCommon(n, a);
            a.RouteResourceManifest = ReadResourceManifest(n, "resource");
            // Additive M1 keys: absent on pre-M1 rows, which read back with
            // the defaults (null requested manifest, 0 origin pid).
            a.RouteRequestedResourceManifest = ReadResourceManifest(n, "requestedResource");
            a.RouteInventoryManifest = ReadInventoryManifest(n, RouteInventoryManifestNode);
            a.RouteRequestedInventoryManifest = ReadInventoryManifest(n, RouteRequestedInventoryManifestNode);
            string pidStr = n.GetValue("routeOriginVesselPid");
            if (pidStr != null && uint.TryParse(pidStr, NumberStyles.Integer, IC, out uint originPid))
                a.RouteOriginVesselPid = originPid;
            TryParseFloat(n, "routeKscFundsCost", out a.RouteKscFundsCost);
        }

        private void SerializeRouteCargoDelivered(ConfigNode n)
        {
            WriteRouteCommon(n);
            WriteResourceManifest(n, "resource", RouteResourceManifest);
            WriteResourceManifest(n, "requestedResource", RouteRequestedResourceManifest);
        }

        private static void DeserializeRouteCargoDelivered(ConfigNode n, GameAction a)
        {
            ReadRouteCommon(n, a);
            a.RouteResourceManifest = ReadResourceManifest(n, "resource");
            a.RouteRequestedResourceManifest = ReadResourceManifest(n, "requestedResource");
        }

        private void SerializeRoutePaused(ConfigNode n)
        {
            WriteRouteCommon(n);
            if (!string.IsNullOrEmpty(RouteEndpointReason))
                n.AddValue("routeEndpointReason", RouteEndpointReason);
        }

        private static void DeserializeRoutePaused(ConfigNode n, GameAction a)
        {
            ReadRouteCommon(n, a);
            a.RouteEndpointReason = n.GetValue("routeEndpointReason");
        }

        // RouteResumed (route-timeline events): the durable player-resume marker.
        // Same wire shape as RoutePaused — route identity plus the free-form reason.
        private void SerializeRouteResumed(ConfigNode n)
        {
            WriteRouteCommon(n);
            if (!string.IsNullOrEmpty(RouteEndpointReason))
                n.AddValue("routeEndpointReason", RouteEndpointReason);
        }

        private static void DeserializeRouteResumed(ConfigNode n, GameAction a)
        {
            ReadRouteCommon(n, a);
            a.RouteEndpointReason = n.GetValue("routeEndpointReason");
        }

        private void SerializeRouteEndpointLost(ConfigNode n)
        {
            WriteRouteCommon(n);
            if (!string.IsNullOrEmpty(RouteEndpointReason))
                n.AddValue("routeEndpointReason", RouteEndpointReason);
        }

        private static void DeserializeRouteEndpointLost(ConfigNode n, GameAction a)
        {
            ReadRouteCommon(n, a);
            a.RouteEndpointReason = n.GetValue("routeEndpointReason");
        }

        // RouteRecoveryCredited reuses the route-common identity codec plus the
        // existing routeKscFundsCost float field for the credit amount (a positive
        // magnitude; the action type carries the credit direction, same convention
        // as RouteCargoDebited / RouteCargoDelivered). No new serialized field.
        private void SerializeRouteRecoveryCredited(ConfigNode n)
        {
            WriteRouteCommon(n);
            if (RouteKscFundsCost != 0f)
                n.AddValue("routeKscFundsCost", RouteKscFundsCost.ToString("R", IC));
        }

        private static void DeserializeRouteRecoveryCredited(ConfigNode n, GameAction a)
        {
            ReadRouteCommon(n, a);
            TryParseFloat(n, "routeKscFundsCost", out a.RouteKscFundsCost);
        }

        // RouteCargoPickedUp (logistics M3, plan Phase 4 / OQ4 / D6): the
        // pickup-direction mirror of RouteCargoDebited's physical-debit shape.
        // Carries the actual debited manifest (resource), the requested-on-shortfall
        // manifest (requestedResource), and the endpoint pid (routeOriginVesselPid),
        // all sparse. NO funds field is written: a pickup emits ZERO funds
        // (loaded-en-route cargo debits its physical source, never funds, D6), so
        // unlike RouteCargoDebited there is no routeKscFundsCost line.
        private void SerializeRouteCargoPickedUp(ConfigNode n)
        {
            WriteRouteCommon(n);
            WriteResourceManifest(n, "resource", RouteResourceManifest);
            WriteResourceManifest(n, "requestedResource", RouteRequestedResourceManifest);
            // M3 Phase 5 (D7): the picked-up stored-part inventory + the
            // requested-on-shortfall inventory, sparse. Empty/null writes nothing
            // so a resource-only pickup row stays byte-identical.
            WriteInventoryManifest(n, RouteInventoryManifestNode, RouteInventoryManifest);
            WriteInventoryManifest(n, RouteRequestedInventoryManifestNode, RouteRequestedInventoryManifest);
            if (RouteOriginVesselPid != 0u)
                n.AddValue("routeOriginVesselPid", RouteOriginVesselPid.ToString(IC));
        }

        private static void DeserializeRouteCargoPickedUp(ConfigNode n, GameAction a)
        {
            ReadRouteCommon(n, a);
            a.RouteResourceManifest = ReadResourceManifest(n, "resource");
            a.RouteRequestedResourceManifest = ReadResourceManifest(n, "requestedResource");
            a.RouteInventoryManifest = ReadInventoryManifest(n, RouteInventoryManifestNode);
            a.RouteRequestedInventoryManifest = ReadInventoryManifest(n, RouteRequestedInventoryManifestNode);
            string pidStr = n.GetValue("routeOriginVesselPid");
            if (pidStr != null && uint.TryParse(pidStr, NumberStyles.Integer, IC, out uint endpointPid))
                a.RouteOriginVesselPid = endpointPid;
        }

        /// <summary>
        /// Writes route-common identity fields (RouteId, RouteCycleId, RouteStopIndex).
        /// Sentinel <c>RouteStopIndex == -1</c> is skipped so a not-applicable stop index
        /// does not pollute the on-disk shape.
        /// </summary>
        private void WriteRouteCommon(ConfigNode n)
        {
            if (!string.IsNullOrEmpty(RouteId))
                n.AddValue("routeId", RouteId);
            if (!string.IsNullOrEmpty(RouteCycleId))
                n.AddValue("routeCycleId", RouteCycleId);
            if (RouteStopIndex >= 0)
                n.AddValue("routeStopIndex", RouteStopIndex.ToString(IC));
        }

        private static void ReadRouteCommon(ConfigNode n, GameAction a)
        {
            a.RouteId = n.GetValue("routeId");
            a.RouteCycleId = n.GetValue("routeCycleId");
            string stopStr = n.GetValue("routeStopIndex");
            if (stopStr != null && int.TryParse(stopStr, NumberStyles.Integer, IC, out int idx))
                a.RouteStopIndex = idx;
            else
                a.RouteStopIndex = -1;
        }

        /// <summary>
        /// Writes a manifest as one <c><paramref name="key"/> = name|amount</c> line per
        /// non-zero entry. Empty / null manifests write nothing.
        /// </summary>
        private static void WriteResourceManifest(ConfigNode n, string key, Dictionary<string, double> manifest)
        {
            if (manifest == null || manifest.Count == 0)
                return;

            // Format: "name|amount" per entry. Stock KSP resource names contain no '|'.
            // If a future modded resource name does, the read side splits on the first
            // '|' and treats anything after as the amount — a malformed entry would log
            // a parse warn and be skipped. Do not change the separator without a
            // schema-version bump and read-side fallback.
            foreach (var kv in manifest)
            {
                if (string.IsNullOrEmpty(kv.Key))
                    continue;
                n.AddValue(key, kv.Key + "|" + kv.Value.ToString("R", IC));
            }
        }

        /// <summary>
        /// Reads all values for <paramref name="key"/> back into a manifest dict. Returns
        /// null when no values are present so callers can distinguish "absent" from "empty".
        /// </summary>
        private static Dictionary<string, double> ReadResourceManifest(ConfigNode n, string key)
        {
            string[] raws = n.GetValues(key);
            if (raws == null || raws.Length == 0)
                return null;

            var dict = new Dictionary<string, double>(raws.Length, StringComparer.Ordinal);
            for (int i = 0; i < raws.Length; i++)
            {
                string raw = raws[i];
                if (string.IsNullOrEmpty(raw))
                    continue;
                int sep = raw.IndexOf('|');
                if (sep <= 0 || sep == raw.Length - 1)
                {
                    ParsekLog.Warn("GameAction",
                        $"Route manifest entry malformed under key '{key}': '{raw}' (expected 'name|amount')");
                    continue;
                }
                string name = raw.Substring(0, sep);
                string amountStr = raw.Substring(sep + 1);
                if (!double.TryParse(amountStr, NS, IC, out double amount))
                {
                    ParsekLog.Warn("GameAction",
                        $"Route manifest amount unparseable under key '{key}' for resource '{name}': '{amountStr}'");
                    continue;
                }
                dict[name] = amount;
            }
            return dict.Count == 0 ? null : dict;
        }

        /// <summary>
        /// M3 Phase 5 (D7): serializes a stored-part inventory manifest as one
        /// <paramref name="nodeName"/> child holding one <c>ITEM</c> subnode per
        /// payload (identityHash / partName / variantName / quantity / slotsTaken
        /// + the verbatim STOREDPART snapshot, identity preserved exactly). Mirror
        /// of the route-side <c>RouteCodec.SerializeInventoryItems</c> shape, kept
        /// self-contained here (the ledger row codec). Empty / null manifests
        /// write NOTHING (sparse, D9).
        /// </summary>
        private static void WriteInventoryManifest(
            ConfigNode n, string nodeName, List<InventoryPayloadItem> manifest)
        {
            if (manifest == null || manifest.Count == 0)
                return;

            ConfigNode parent = n.AddNode(nodeName);
            for (int i = 0; i < manifest.Count; i++)
            {
                InventoryPayloadItem item = manifest[i];
                if (item == null)
                    continue;

                ConfigNode itemNode = parent.AddNode(RouteInventoryItemNode);
                if (!string.IsNullOrEmpty(item.IdentityHash))
                    itemNode.AddValue("identityHash", item.IdentityHash);
                if (!string.IsNullOrEmpty(item.PartName))
                    itemNode.AddValue("partName", item.PartName);
                if (!string.IsNullOrEmpty(item.VariantName))
                    itemNode.AddValue("variantName", item.VariantName);
                if (item.Quantity != 0)
                    itemNode.AddValue("quantity", item.Quantity.ToString(IC));
                if (item.SlotsTaken != 0)
                    itemNode.AddValue("slotsTaken", item.SlotsTaken.ToString(IC));

                if (item.StoredResources != null && item.StoredResources.Count > 0)
                {
                    ConfigNode res = itemNode.AddNode(RouteInventoryStoredResourcesNode);
                    foreach (var kvp in item.StoredResources)
                    {
                        if (string.IsNullOrEmpty(kvp.Key))
                            continue;
                        ConfigNode r = res.AddNode("RESOURCE");
                        r.AddValue("name", kvp.Key);
                        r.AddValue("amount", kvp.Value.amount.ToString("R", IC));
                        r.AddValue("maxAmount", kvp.Value.maxAmount.ToString("R", IC));
                    }
                }

                if (item.StoredPartSnapshot != null)
                {
                    ConfigNode copy = item.StoredPartSnapshot.CreateCopy();
                    copy.name = RouteInventoryStoredPartNode;
                    itemNode.AddNode(copy);
                }
            }
        }

        /// <summary>
        /// Reads a stored-part inventory manifest written by
        /// <see cref="WriteInventoryManifest"/>. Returns null when the node is
        /// absent OR carries no items so callers distinguish "absent" from
        /// "empty" (sparse, D9).
        /// </summary>
        private static List<InventoryPayloadItem> ReadInventoryManifest(ConfigNode n, string nodeName)
        {
            ConfigNode parent = n.GetNode(nodeName);
            if (parent == null)
                return null;

            ConfigNode[] itemNodes = parent.GetNodes(RouteInventoryItemNode);
            if (itemNodes.Length == 0)
                return null;

            var items = new List<InventoryPayloadItem>(itemNodes.Length);
            for (int i = 0; i < itemNodes.Length; i++)
            {
                ConfigNode itemNode = itemNodes[i];
                var item = new InventoryPayloadItem
                {
                    IdentityHash = itemNode.GetValue("identityHash"),
                    PartName = itemNode.GetValue("partName"),
                    VariantName = itemNode.GetValue("variantName"),
                };
                if (TryParseInt(itemNode, "quantity", out int qty))
                    item.Quantity = qty;
                if (TryParseInt(itemNode, "slotsTaken", out int slots))
                    item.SlotsTaken = slots;

                ConfigNode res = itemNode.GetNode(RouteInventoryStoredResourcesNode);
                if (res != null)
                {
                    ConfigNode[] resNodes = res.GetNodes("RESOURCE");
                    if (resNodes.Length > 0)
                    {
                        item.StoredResources = new Dictionary<string, ResourceAmount>(
                            resNodes.Length, StringComparer.Ordinal);
                        for (int r = 0; r < resNodes.Length; r++)
                        {
                            string name = resNodes[r].GetValue("name");
                            if (string.IsNullOrEmpty(name))
                                continue;
                            double amount = 0.0, maxAmount = 0.0;
                            double.TryParse(resNodes[r].GetValue("amount"), NS, IC, out amount);
                            double.TryParse(resNodes[r].GetValue("maxAmount"), NS, IC, out maxAmount);
                            item.StoredResources[name] = new ResourceAmount { amount = amount, maxAmount = maxAmount };
                        }
                    }
                }

                ConfigNode snapshot = itemNode.GetNode(RouteInventoryStoredPartNode);
                if (snapshot != null)
                {
                    ConfigNode copy = snapshot.CreateCopy();
                    copy.name = RouteInventoryStoredPartNode;
                    item.StoredPartSnapshot = copy;
                }

                items.Add(item);
            }

            return items.Count == 0 ? null : items;
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
