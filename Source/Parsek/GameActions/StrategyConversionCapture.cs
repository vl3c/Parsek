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
        /// <para><b>Reputation is evaluated and returned but deliberately not turned
        /// into a ledger row by the live door</b> - see
        /// <c>LedgerOrchestrator.OnStrategyCurrencyConversion</c>, which logs it and
        /// explains why (the query delta is PRE-curve while
        /// <c>Reputation.AddReputation</c> applies KSP's granular curve on top, so the
        /// magnitude here is not the magnitude the pool moved by, and this seam
        /// exposes no post-curve value to read). Keeping the leg in the pure result
        /// keeps the observation testable and the asymmetry explicit rather than
        /// hidden behind a missing branch.</para>
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
