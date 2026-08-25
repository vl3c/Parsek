using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// Pure presentation formatters for the M1 inline Interval cell's "Nx" cadence
    /// readout tooltip (LogisticsWindowUI). Split out of the window class so the
    /// single-line help-strip budget (TooltipEchoBudgetTests) can pin the exact
    /// worst-case text headlessly - the flat variant is the longest runtime-composed
    /// tooltip in the Logistics window.
    ///
    /// <para>M5 (D8): for a WINDOWED basis the tooltip leads with the windowed wording
    /// ("2x (every 2nd window)") - interval arithmetic is actively misleading on
    /// synodic spacing - and a small basis label follows the Nx readout. Flat rows
    /// spell the plain duration arithmetic instead.</para>
    /// </summary>
    internal static class LogisticsIntervalPresentation
    {
        /// <summary>
        /// The Interval cell's "Nx" hover tooltip. <paramref name="formattedTransit"/>
        /// arrives pre-formatted (the caller's <c>FormatDuration</c> output) so this
        /// builder stays Unity-free and byte-deterministic.
        /// </summary>
        internal static string BuildNxCellTooltip(
            bool windowedBasis, int multiplier, string basisLabel, string formattedTransit)
        {
            if (windowedBasis)
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "Dispatch cadence: {0} {1}. The route delivers on the launch windows its ghost flies; use -/+ to deliver every Nth window.",
                    RouteWindowBasisPresentation.FormatWindowedCadence(multiplier),
                    basisLabel ?? string.Empty);
            }

            // "N" binds the arithmetic to the "Nx" cell the player is hovering - it is
            // the multiplier readout's own symbol, not filler (a copy trim once
            // replaced it with the word "cadence", defining cadence as itself).
            return string.Format(CultureInfo.InvariantCulture,
                "Dispatch cadence = N x run duration (transit {0}). Type an interval (30m, 2h, 1d, or plain seconds) or use -/+; it snaps up to a whole run-multiple.",
                formattedTransit ?? "-");
        }
    }
}
