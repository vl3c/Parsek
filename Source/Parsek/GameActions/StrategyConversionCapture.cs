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
        /// <para><b>Funds / reputation - capture ONLY when the transaction put
        /// nothing of that currency in.</b> A nonzero input means the ordinary
        /// event-driven channel is already watching that transaction and reports the
        /// value NET of the modifier, so capturing here would double-count. That is
        /// exactly the <c>CurrencyOperation</c> reward-multiplier case (a contract's
        /// funds reward scaled by an active strategy): input != 0, so no row. A
        /// zero-input nonzero delta is a genuine cross-currency YIELD with no
        /// transaction of its own - measured 60.526316 funds of drift in one session,
        /// low by exactly the yields, because they arrive under the ORIGINAL reason
        /// AND below the recorder's 100-funds threshold, a double miss.</para>
        ///
        /// <para><b>The reputation leg's PRE-curve magnitude is exactly what the ledger
        /// wants</b>, which is why the zero-input rule is load-bearing for it too.
        /// Stock's <c>Reputation.OnCurrenciesModified</c> passes
        /// <c>GetEffectDelta(Currency.Reputation)</c> straight to
        /// <c>addReputation_granular</c>, so this leg IS the curve's input argument;
        /// <c>LedgerOrchestrator.BuildStrategyConversionAction</c> writes it as a NOMINAL
        /// <c>ReputationEarning</c> and <c>ReputationModule.ApplyReputationCurve</c> - a
        /// line-by-line mirror of that routine - re-derives the pool movement at the
        /// reconstruction's OWN running rep. The zero-input scoping is what keeps that
        /// from double-counting against <c>TransformedRepReward</c> /
        /// <c>MilestoneRepAwarded</c> / the reason-keyed exchanger door.</para>
        /// </summary>
        internal static List<StrategyConversionLeg> EvaluateLegs(StrategyConversionQuery q)
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

            if (System.Math.Abs(q.InputFunds) < MinCaptureMagnitude &&
                System.Math.Abs(q.DeltaFunds) >= MinCaptureMagnitude)
            {
                legs.Add(new StrategyConversionLeg
                {
                    Currency = StrategyConversionCurrency.Funds,
                    Delta = q.DeltaFunds
                });
            }

            if (System.Math.Abs(q.InputReputation) < MinCaptureMagnitude &&
                System.Math.Abs(q.DeltaReputation) >= MinCaptureMagnitude)
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
