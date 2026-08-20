using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>Which pool a captured strategy-conversion leg moves.</summary>
    internal enum StrategyConversionCurrency
    {
        Funds = 0,
        Science = 1,
        Reputation = 2
    }

    /// <summary>
    /// One capture-worthy currency movement pulled out of a stock
    /// <c>CurrencyModifierQuery</c>. <see cref="Delta"/> keeps the query's SIGN
    /// (negative = the strategy took from that pool, positive = it yielded into it).
    /// </summary>
    internal struct StrategyConversionLeg
    {
        public StrategyConversionCurrency Currency;
        public double Delta;
    }

    /// <summary>
    /// Unity-free snapshot of the three currency rows of a stock
    /// <c>CurrencyModifierQuery</c>: what the transaction ITSELF put in
    /// (<c>GetInput</c>) and what the subscribed strategy effects ADDED on top
    /// (<c>GetEffectDelta</c>), plus the transaction's reason.
    /// </summary>
    internal struct StrategyConversionQuery
    {
        public double InputFunds;
        public double DeltaFunds;
        public double InputScience;
        public double DeltaScience;
        public double InputReputation;
        public double DeltaReputation;
        public string Reason;
    }

    /// <summary>
    /// What the query-family door does with the recalc that normally follows a ledger
    /// write. The ROWS are always written synchronously; only the recalc+patch moves.
    /// </summary>
    internal enum StrategyConversionRecalcDispatch
    {
        /// <summary>Nothing was written, so there is nothing to recalculate.</summary>
        None = 0,
        /// <summary>Run the recalc on the NEXT frame, after KSP has applied the query.</summary>
        Deferred = 1,
        /// <summary>No frame host available - run it inline rather than lose it.</summary>
        Immediate = 2
    }

    /// <summary>
    /// Pure decision core for the QUERY-FAMILY strategy door
    /// (STRATEGY-SCIENCE-CONVERSION-LEAK / STRATEGY-FUNDS-YIELD-DRIFT).
    ///
    /// <para>KSP has TWO stock strategy conversion mechanisms and they leave
    /// completely different traces:</para>
    /// <list type="bullet">
    /// <item><b>The exchanger family</b> (<c>Strategies.CurrencyExchanger</c> -
    /// Bail-Out Grant, <c>researchIPsellout</c>) does a one-shot direct
    /// <c>AddScience</c>/<c>AddFunds</c>/<c>AddReputation</c> under the dedicated
    /// reasons <c>TransactionReasons.StrategyInput</c> / <c>StrategyOutput</c> at
    /// activation. Those reason-keyed events ARE observable, and the reason-keyed
    /// doors in <c>GameStateRecorder.On{Funds,Science,Reputation}Changed</c> +
    /// <c>GameStateEventConverter.ConvertStrategyExchange*</c> capture them. Nothing
    /// in this file touches that family.</item>
    /// <item><b>The query family</b> (<c>Strategies.Effects.CurrencyConverter</c> -
    /// Patents Licensing and 7 siblings - plus
    /// <c>Strategies.Effects.CurrencyOperation</c>) subscribes
    /// <c>GameEvents.Modifiers.OnCurrencyModifierQuery</c> and mutates the query's
    /// deltas IN PLACE. <c>ResearchAndDevelopment.AddScience</c> /
    /// <c>Funding.AddFunds</c> then fire <c>OnScienceChanged</c>/<c>OnFundsChanged</c>
    /// with values ALREADY NET, under the ORIGINAL reason. No StrategyInput /
    /// StrategyOutput event ever exists, so the reason-keyed doors are blind to it -
    /// which is what this file's door exists to close.</item>
    /// </list>
    ///
    /// <para>Read <c>OnCurrencyModified</c>, NOT <c>OnCurrencyModifierQuery</c>:
    /// <c>CurrencyModifierQuery.RunQuery</c> (33 stock DISPLAY sites - contract
    /// tooltips, the strategy preview, the R&amp;D panel) fires the query event and
    /// mutates nothing. A <c>[CurrencyConverter ...]</c> log line is therefore NOT
    /// proof a balance moved; only <c>OnCurrencyModified</c> rides an actual
    /// <c>Add*</c>.</para>
    /// </summary>
    internal static class StrategyConversionCapture
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        internal const string Tag = "StrategyConversion";

        /// <summary>
        /// Smallest movement worth a ledger row. Matches
        /// <c>GameStateRecorder</c>'s science threshold rather than its 100-funds one:
        /// this door exists BECAUSE the funds threshold drops the yields (measured
        /// 60.526 funds of drift across one session, every individual yield under
        /// 100), so re-applying that threshold here would re-open the very leak.
        /// </summary>
        internal const double MinCaptureMagnitude = 0.001;

        /// <summary>
        /// True when the door must stand down, mirroring every other recorder door:
        /// <c>SuppressResourceEvents</c> covers timeline replay and the in-game
        /// tests' <c>SuppressionGuard.Resources()</c> restores, and
        /// <c>IsReplayingActions</c> covers <c>KspStatePatcher</c>'s own pool writes
        /// during a ledger walk. Patching writes carry
        /// <c>TransactionReasons.None</c>, which no strategy effect subscribes to, so
        /// the second gate is belt-and-braces - but it is the SAME belt the funds /
        /// science / reputation doors wear, and a future stock or modded effect that
        /// does match <c>None</c> would otherwise let a recalc feed itself.
        /// </summary>
        internal static bool ShouldStandDown(
            bool suppressResourceEvents,
            bool isReplayingActions,
            out string reason)
        {
            if (suppressResourceEvents)
            {
                reason = "resource events suppressed";
                return true;
            }
            if (isReplayingActions)
            {
                reason = "ledger replay in progress";
                return true;
            }
            reason = null;
            return false;
        }

        /// <summary>
        /// Applies the SCOPING RULE that keeps this door from double-counting the
        /// movements the ordinary event-driven channels already see.
        ///
        /// <para><b>THE DECOMPOSITION THAT MAKES THE SCIENCE ARM SAFE.</b> A stock
        /// <c>Add*</c> moves the pool by <c>GetInput + GetEffectDelta</c>. Parsek's
        /// earning channels record the INPUT half and nothing else - a
        /// <c>ScienceEarning</c> carries the SUBJECT's value, a
        /// <c>ContractComplete</c> carries <c>contract.ScienceCompletion</c>, a
        /// <c>MilestoneAchievement</c> carries the progress node's configured award -
        /// and <c>StrategiesModule.TransformContractReward</c> is a documented identity
        /// no-op, so NO existing channel records the effect-delta half. Capturing
        /// exactly the effect-delta therefore lands the ledger on the pool movement by
        /// construction, whichever way stock happens to fold the modifier in. That is
        /// the argument for capturing science unconditionally, and it is the argument
        /// to re-check before adding any channel that starts recording a post-modifier
        /// amount.</para>
        ///
        /// <para><b>Science - capture any nonzero delta, input or not.</b> Parsek's
        /// science EARNING channel is archive-derived (it converts
        /// <c>PendingScienceSubjects</c>, i.e. the SUBJECT's value), so it never sees
        /// a pool-only movement at all. A converter TAKE (Patents Licensing) and a
        /// converter YIELD (Open-Source Tech Program) are both invisible to it, and so
        /// is a <c>CurrencyOperation</c> science multiplier. Every one of them must be
        /// captured or the reconstruction drifts by exactly that amount (measured:
        /// science recon high by 0.72000 against a 0.7200818 GUARDED UPLIFT
        /// gap).</para>
        ///
        /// <para><b>Funds - capture a zero-input delta always, and a NONZERO-input delta
        /// only under a NOMINAL-channel reason.</b> A zero-input nonzero delta is a
        /// genuine cross-currency YIELD with no transaction of its own - measured
        /// 60.526316 funds of drift in one session, low by exactly the yields, because
        /// they arrive under the ORIGINAL reason AND below the recorder's 100-funds
        /// threshold, a double miss. A nonzero-input delta is the modifier riding an
        /// ordinary transaction, and whether capturing it double-counts depends
        /// ENTIRELY on what Parsek's funds channel recorded for that transaction, which
        /// is a property of the REASON - hence
        /// <see cref="IsNominalChannelFundsReason"/> and the paragraph below.</para>
        ///
        /// <para><b>THE PREMISE IS REASON-QUALIFIED, SO THE GATE IS TOO.</b> "The
        /// ordinary channel reports NET" is TRUE only where Parsek's funds channel is
        /// EVENT-DERIVED - i.e. derived from the observed pool movement:
        /// <c>VesselRollout</c>, <c>RnDPartPurchase</c>, <c>StructureRepair</c>,
        /// <c>StructureConstruction</c> and <c>StrategyOutput</c>. There the zero-input
        /// rule stands unchanged and a row really would double-count. It is FALSE on the
        /// NOMINAL-channel reasons <c>ContractReward</c>, <c>ContractAdvance</c> and
        /// <c>Progression</c>, where the channel records a CONFIGURED GROSS amount
        /// instead - <c>contract.FundsCompletion</c> and <c>contract.FundsAdvance</c>
        /// (the first via <c>TransformedFundsReward</c>, which
        /// <c>RecalculationEngine</c> assigns straight from <c>FundsReward</c> because
        /// <c>StrategiesModule.TransformContractReward</c> is a documented identity
        /// no-op) and the <c>ProgressNode.AwardProgress</c> arguments that
        /// <c>ProgressRewardPatch</c> captures. Nothing on those three reads an observed
        /// delta, so the effect-delta half had no capture channel at all and the
        /// reconstruction ran HIGH by every diverted fraction.
        /// See <see cref="IsNominalChannelFundsReason"/> for the gate itself and
        /// STRATEGY-FUNDS-DEBIT-CONVERTERS-UNCAPTURED in docs/dev/todo-and-known-bugs.md
        /// for the measurement that closed it.</para>
        ///
        /// <para><b>Reputation - capture any nonzero delta, input or not, exactly like
        /// science, and for a MECHANISM reason rather than by analogy.</b> Decompiled
        /// <c>Reputation.AddReputation(r, reason)</c> moves the pool TWICE:
        /// <c>rep += addReputation_granular(r)</c> first, then - from
        /// <c>Reputation.OnCurrenciesModified</c>, after the query has run -
        /// <c>rep += addReputation_granular(GetEffectDelta(Currency.Reputation))</c>
        /// against the already-moved pool. The two halves are SEPARATE curve
        /// applications, and every Parsek reputation channel records the FIRST one only:
        /// <c>ContractComplete</c> carries the contract's configured
        /// <c>ReputationCompletion</c> (<c>StrategiesModule.TransformContractReward</c>
        /// is a documented identity no-op), <c>MilestoneAchievement</c> carries the
        /// progress node's configured award, and <c>GameStateEventConverter</c> converts
        /// a <c>ReputationChanged</c> event ONLY under
        /// <c>TransactionReasons.StrategyInput</c>. Nothing anywhere is derived from the
        /// observed pool delta, so "a nonzero input means the ordinary channel already
        /// reports it net" - true for funds - is FALSE for reputation, and the
        /// zero-input rule was excluding a whole family of real movements rather than
        /// preventing a double count.</para>
        ///
        /// <para><b>What the old rule excluded, and what it cost.</b> Every stock
        /// reputation-INPUT converter (<c>FundraisingCampaign</c> reputation -> funds,
        /// <c>UnpaidResearchProgram</c> reputation -> science) diverts
        /// <c>GetInput(Reputation) * share</c>, so <c>GetInput != 0</c> by construction
        /// and the leg never reached the row-shape mapper. MEASURED live on run
        /// <c>2026-08-20_2052_L3-strategy-currency-conversion</c>, model-free (the same
        /// 20-point award at the same reputation with Fundraising Campaign active, once
        /// under the excluded <c>VesselRecovery</c> reason and once under the masked
        /// <c>ContractReward</c>): the pool moved <c>19.999963760375977</c> against
        /// <c>18.999906539916992</c>, a diversion of <c>1.0000572204589844</c>
        /// reputation - 100x the reputation guard's 0.01 epsilon - that no ledger row
        /// carried. See STRATEGY-REP-DEBIT-CONVERTERS-UNCAPTURED.</para>
        ///
        /// <para><b>The leg's PRE-curve magnitude is exactly what the ledger wants</b>,
        /// in BOTH directions. The delta IS the argument stock hands to
        /// <c>addReputation_granular</c>, so
        /// <c>LedgerOrchestrator.BuildStrategyConversionAction</c> writes it NOMINAL -
        /// a <c>ReputationEarning</c> for a credit, a <c>ReputationPenalty</c> sourced
        /// <see cref="ReputationPenaltySource.StrategyConverter"/> for a debit - and
        /// <c>ReputationModule.ApplyReputationCurve</c>, a line-by-line mirror of that
        /// routine, re-derives the pool movement at the reconstruction's OWN running
        /// rep. Two rows for one transaction is not a workaround: it is the shape stock
        /// itself applies.</para>
        ///
        /// <para><b>The one thing that would break this</b> is a future channel that
        /// starts recording a POST-modifier reputation amount - i.e. one derived from an
        /// observed pool delta rather than from a configured nominal. That would make
        /// the input half double-counted, and it is the invariant to re-check before
        /// adding any such channel.</para>
        ///
        /// <para><b>THE OTHER DIRECTION, for completeness.</b> The same double count
        /// arrives from the effect side rather than the channel side if a
        /// <c>Strategies.Effects</c> effect ever lists
        /// <c>TransactionReasons.StrategyInput</c> in its <c>AffectReasons</c>: it would
        /// then divert on the EXCHANGER's own query, and that movement is already
        /// carried by <c>ConvertStrategyExchangeReputation</c>'s POST-curve observed row
        /// read off the <c>ReputationChanged</c>/<c>StrategyInput</c> event - so the
        /// second <c>addReputation_granular</c> call would be counted twice, once there
        /// and once as the <c>StrategyConverter</c> row this door writes. Unreachable in
        /// stock: NO stock effect lists <c>StrategyInput</c>. So the invariant is BOTH
        /// halves - no post-modifier reputation channel AND no
        /// <c>StrategyInput</c>-targeting effect - and either one appearing is what
        /// makes the unconditional reputation capture unsafe.</para>
        /// </summary>
        internal static List<StrategyConversionLeg> EvaluateLegs(StrategyConversionQuery q)
        {
            return EvaluateLegs(q, IsNominalChannelFundsReason(q.Reason));
        }

        /// <summary>
        /// The three transaction reasons on which Parsek's FUNDS channel records a
        /// CONFIGURED GROSS nominal rather than an observed pool movement, and therefore
        /// the only three on which a nonzero-input funds effect delta is safe - and
        /// necessary - to capture.
        ///
        /// <list type="bullet">
        /// <item><c>ContractReward</c> - <c>ContractComplete.TransformedFundsReward</c>,
        /// which <c>RecalculationEngine</c> assigns straight from the contract's
        /// configured <c>FundsCompletion</c>.</item>
        /// <item><c>ContractAdvance</c> - the <c>FundsEarning</c> built from
        /// <c>contract.FundsAdvance</c> at accept time.</item>
        /// <item><c>Progression</c> - <c>MilestoneAchievement.MilestoneFundsAwarded</c>,
        /// captured by <c>ProgressRewardPatch</c> from the <c>AwardProgress</c>
        /// ARGUMENTS, i.e. before <c>Funding.AddFunds</c> runs the query at all.</item>
        /// </list>
        ///
        /// <para><b>THE MATCH IS EXACT AND THE DEFAULT IS SUPPRESSION.</b> The string
        /// comes from <c>CurrencyModifierQuery.reason.ToString()</c>, so a single stock
        /// <c>TransactionReasons</c> member renders as exactly one of these names. An
        /// unknown reason, a null, or a combined flags spelling matches nothing and
        /// keeps the historical zero-input rule - which is the CONSERVATIVE side: it
        /// preserves today's behaviour rather than guessing that some unseen channel
        /// records gross.</para>
        ///
        /// <para><b>WHY THE EVENT-DERIVED REASONS MUST STAY OUT.</b> Under
        /// <c>VesselRollout</c>, <c>RnDPartPurchase</c>, <c>StructureRepair</c>,
        /// <c>StructureConstruction</c> and <c>StrategyOutput</c> the channel records
        /// the OBSERVED <c>FundsChanged</c> delta, which is already NET of the modifier.
        /// A row there would be counted twice - once inside the observed amount and once
        /// as this door's leg - and that is exactly what stock's
        /// <c>AgressiveNegotiations</c> launch/purchase discount would produce. Pinned by
        /// <c>StrategyConversionCaptureTests.NonZeroInputFunds_UnderEventDerivedReason_IsNotCaptured</c>.</para>
        /// </summary>
        internal static bool IsNominalChannelFundsReason(string reason)
        {
            if (string.IsNullOrEmpty(reason))
                return false;
            return string.Equals(reason, "ContractReward", System.StringComparison.Ordinal)
                || string.Equals(reason, "ContractAdvance", System.StringComparison.Ordinal)
                || string.Equals(reason, "Progression", System.StringComparison.Ordinal);
        }

        /// <summary>
        /// Testable core of <see cref="EvaluateLegs(StrategyConversionQuery)"/> with the
        /// reason decision hoisted out, so a test can drive both sides of the funds gate
        /// without depending on the reason spelling.
        /// </summary>
        internal static List<StrategyConversionLeg> EvaluateLegs(
            StrategyConversionQuery q, bool fundsChannelRecordsGross)
        {
            var legs = new List<StrategyConversionLeg>();

            if (System.Math.Abs(q.DeltaScience) >= MinCaptureMagnitude)
            {
                legs.Add(new StrategyConversionLeg
                {
                    Currency = StrategyConversionCurrency.Science,
                    Delta = q.DeltaScience
                });
            }

            bool fundsInputIsZero = System.Math.Abs(q.InputFunds) < MinCaptureMagnitude;
            if ((fundsInputIsZero || fundsChannelRecordsGross) &&
                System.Math.Abs(q.DeltaFunds) >= MinCaptureMagnitude)
            {
                legs.Add(new StrategyConversionLeg
                {
                    Currency = StrategyConversionCurrency.Funds,
                    Delta = q.DeltaFunds
                });
            }

            if (System.Math.Abs(q.DeltaReputation) >= MinCaptureMagnitude)
            {
                legs.Add(new StrategyConversionLeg
                {
                    Currency = StrategyConversionCurrency.Reputation,
                    Delta = q.DeltaReputation
                });
            }

            return legs;
        }

        /// <summary>
        /// Pure: how the door dispatches its recalc after writing rows.
        ///
        /// <para>WHY IT IS DEFERRED AT ALL. <c>OnCurrencyModified</c> fires from INSIDE
        /// the <c>CurrencyModifierQuery</c> handling, before KSP has applied every leg of
        /// the transaction to its pools. A recalc run there patches against a live pool
        /// that is briefly missing the output leg, so the reconstruction reads as running
        /// ABOVE live and <c>KspStatePatcher.PatchFunds</c> uplift-clamps it down once
        /// (measured: one <c>GUARDED UPLIFT clamped ... running=500168.13 live=500000</c>
        /// on run 2026-08-18_2019, self-healing on the next recalc). Moving the recalc to
        /// the next frame lets the query finish applying first.</para>
        ///
        /// <para>CAPTURE LOSS RISK IS ZERO BY CONSTRUCTION: the caller writes its ledger
        /// rows synchronously and this decision only governs the recalc. A deferred recalc
        /// that never runs (scene torn down, host destroyed) costs nothing but freshness -
        /// the rows are already in the ledger and the next natural recalc picks them up.
        /// <see cref="StrategyConversionRecalcDispatch.Immediate"/> is the no-frame-host
        /// fallback (headless tests, and any live path where the defer host is gone):
        /// running the old synchronous shape beats silently dropping the recalc.</para>
        /// </summary>
        internal static StrategyConversionRecalcDispatch DecideRecalcDispatch(
            int rowsWritten,
            bool hasFrameDeferHost)
        {
            if (rowsWritten <= 0)
                return StrategyConversionRecalcDispatch.None;
            return hasFrameDeferHost
                ? StrategyConversionRecalcDispatch.Deferred
                : StrategyConversionRecalcDispatch.Immediate;
        }

        /// <summary>Grep-stable one-line summary of an evaluated query.</summary>
        internal static string FormatQuery(StrategyConversionQuery q, int legCount)
        {
            return string.Format(IC,
                "reason={0} inF={1} dF={2} inS={3} dS={4} inR={5} dR={6} legs={7}",
                q.Reason ?? "(none)",
                q.InputFunds.ToString("R", IC),
                q.DeltaFunds.ToString("R", IC),
                q.InputScience.ToString("R", IC),
                q.DeltaScience.ToString("R", IC),
                q.InputReputation.ToString("R", IC),
                q.DeltaReputation.ToString("R", IC),
                legCount.ToString(IC));
        }
    }
}
