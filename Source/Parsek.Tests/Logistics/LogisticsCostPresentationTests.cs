using System;
using System.Globalization;
using System.Threading;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the pure run-cost presentation helpers in
    /// <see cref="LogisticsCostPresentation"/> (the detail line, its tooltip, the
    /// creation-summary block, and the candidate suffix). Every helper is
    /// Unity-free and locale-stable, so they are exercised directly. Locale
    /// stability is checked by flipping the thread culture to a comma-decimal
    /// culture (de-DE) and asserting the output stays InvariantCulture-formatted
    /// (comma thousands grouping, no decimal comma). ASCII-only and no-em-dash are
    /// asserted explicitly because the plan forbids the funds glyph and em dashes.
    /// </summary>
    [Collection("Sequential")]
    public class LogisticsCostPresentationTests : IDisposable
    {
        private readonly CultureInfo originalCulture;

        public LogisticsCostPresentationTests()
        {
            originalCulture = Thread.CurrentThread.CurrentCulture;
        }

        public void Dispose()
        {
            Thread.CurrentThread.CurrentCulture = originalCulture;
        }

        private static RouteRunCostCalculator.RouteRunCost Cost(
            double launch, double recovered, int recoveries)
        {
            double net = launch - recovered;
            return new RouteRunCostCalculator.RouteRunCost
            {
                Applicable = true,
                CostKnown = launch > 0.0,
                LaunchCost = launch,
                RecoveredCredits = recovered,
                NetCost = net > 0.0 ? net : 0.0,
                RecoveryEventCount = recoveries
            };
        }

        private static void AssertAsciiNoEmDash(string s)
        {
            Assert.DoesNotContain("\u2014", s); // em dash
            Assert.DoesNotContain("\u2013", s); // en dash
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                // Allow newline (block separator) and printable ASCII only.
                Assert.True(c == '\n' || (c >= 0x20 && c <= 0x7E),
                    $"Non-ASCII char U+{((int)c):X4} at index {i} in: {s}");
            }
        }

        // ------------------------------------------------------------------
        // FormatFunds
        // ------------------------------------------------------------------

        // catches: dropping the thousands grouping or the "funds" unit word.
        [Fact]
        public void FormatFunds_GroupsThousands_AppendsUnit()
        {
            Assert.Equal("5,200 funds", LogisticsCostPresentation.FormatFunds(5200.0));
            Assert.Equal("12,500 funds", LogisticsCostPresentation.FormatFunds(12500.0));
            Assert.Equal("0 funds", LogisticsCostPresentation.FormatFunds(0.0));
        }

        // catches: thread-culture leaking a decimal comma ("12.500,00") instead of
        // the InvariantCulture comma-grouped integer ("12,500").
        [Fact]
        public void FormatFunds_StaysInvariant_UnderCommaLocale()
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("12,500 funds", LogisticsCostPresentation.FormatFunds(12500.0));
        }

        // ------------------------------------------------------------------
        // FormatDetailLine
        // ------------------------------------------------------------------

        // catches: the with-recovery shape drifting from the plan's exact wording.
        [Fact]
        public void FormatDetailLine_WithRecovery_ShowsLaunchMinusRecovered()
        {
            string s = LogisticsCostPresentation.FormatDetailLine(Cost(12500.0, 7300.0, 1));
            Assert.Equal("Cost/run: 5,200 funds  (launch 12,500 - recovered 7,300)", s);
            AssertAsciiNoEmDash(s);
        }

        // catches: the no-recovery shape losing its caption (or wrongly showing a
        // "recovered 0" breakdown that looks like a recovered run).
        [Fact]
        public void FormatDetailLine_NoRecovery_ShowsNotRecoveredCaption()
        {
            string s = LogisticsCostPresentation.FormatDetailLine(Cost(12500.0, 0.0, 0));
            Assert.Equal("Cost/run: 12,500 funds  (transport not recovered in the recording)", s);
            AssertAsciiNoEmDash(s);
        }

        // catches: a comma-locale leaking a decimal comma into the detail line.
        [Fact]
        public void FormatDetailLine_StaysInvariant_UnderCommaLocale()
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            string s = LogisticsCostPresentation.FormatDetailLine(Cost(12500.0, 7300.0, 1));
            Assert.Equal("Cost/run: 5,200 funds  (launch 12,500 - recovered 7,300)", s);
        }

        // ------------------------------------------------------------------
        // FormatDetailTooltip
        // ------------------------------------------------------------------

        // catches: the tooltip losing the net definition or the D1 gross-charge
        // caveat (the player must know the per-cycle charge is still gross).
        [Fact]
        public void FormatDetailTooltip_ExplainsNetAndD1Caveat()
        {
            string s = LogisticsCostPresentation.FormatDetailTooltip(Cost(12500.0, 7300.0, 1));
            Assert.Contains("launch cost - recovered credits", s);
            Assert.Contains("per-cycle charge is currently the gross launch cost", s);
            // With recovery the recover-to-reduce hint must NOT appear (already done).
            Assert.DoesNotContain(LogisticsCostPresentation.RecoverToReduceHint, s);
            AssertAsciiNoEmDash(s);
        }

        // catches: the no-recovery tooltip omitting the recover-to-reduce hint.
        [Fact]
        public void FormatDetailTooltip_NoRecovery_AddsRecoverHint()
        {
            string s = LogisticsCostPresentation.FormatDetailTooltip(Cost(12500.0, 0.0, 0));
            Assert.Contains(LogisticsCostPresentation.RecoverToReduceHint, s);
            AssertAsciiNoEmDash(s);
        }

        // ------------------------------------------------------------------
        // FormatCreationSummaryBlock
        // ------------------------------------------------------------------

        // catches: the creation block losing a line or drifting from the plan shape.
        [Fact]
        public void FormatCreationSummaryBlock_ThreeLines_NetLaunchRecovered()
        {
            string s = LogisticsCostPresentation.FormatCreationSummaryBlock(Cost(12500.0, 7300.0, 1));
            Assert.Equal(
                "Cost per run: 5,200 funds\n  Launch: 12,500 funds\n  Recovered: 7,300 funds\n",
                s);
            AssertAsciiNoEmDash(s);
        }

        // catches: a comma-locale corrupting the block numbers.
        [Fact]
        public void FormatCreationSummaryBlock_StaysInvariant_UnderCommaLocale()
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            string s = LogisticsCostPresentation.FormatCreationSummaryBlock(Cost(12500.0, 7300.0, 1));
            Assert.Contains("Cost per run: 5,200 funds", s);
            Assert.Contains("Launch: 12,500 funds", s);
            Assert.Contains("Recovered: 7,300 funds", s);
        }

        // ------------------------------------------------------------------
        // FormatCandidateSuffix
        // ------------------------------------------------------------------

        // catches: the compact candidate suffix drifting (it rides the narrow
        // "Would deliver" cell, so the wording must stay terse).
        [Fact]
        public void FormatCandidateSuffix_CompactNet()
        {
            string s = LogisticsCostPresentation.FormatCandidateSuffix(Cost(12500.0, 7300.0, 1));
            Assert.Equal("  (cost/run 5,200 funds)", s);
            AssertAsciiNoEmDash(s);
        }
    }
}
