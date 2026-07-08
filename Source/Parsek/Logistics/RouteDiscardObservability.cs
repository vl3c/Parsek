using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.Logistics
{
    /// <summary>
    /// Rec-3 (the non-rewind discard leak) — discard-time OBSERVABILITY reporter.
    /// Plan <c>docs/dev/plans/fix-logistics-rewind-determinism.md</c> Phase 4;
    /// report <c>docs/dev/research/logistics-time-rewind-compat-report.md</c> risk #6.
    ///
    /// <para><b>The leak this makes visible (it does NOT fix it).</b> A recurring
    /// supply route fires LIVE on a UT-pinned loop grid and physically mutates the
    /// world through three writers with NO reverse method
    /// (<c>LiveDeliveryWriters</c>, <c>LiveOriginDebitWriters</c>,
    /// <c>LiveInventoryPickupWriter</c>). Their only rollback is the
    /// Rewind-to-Separation full-world quicksave. When a route physically fires
    /// INSIDE a segment the player then discards WITHOUT a rewind, that mutation
    /// persists in the surviving timeline with no rollback path. Today this is
    /// economically CONSISTENT (the free-standing route funds rows survive the
    /// recording-scoped discard purge too, so funds and cargo both persist), so it is
    /// a discard-INTENT residual, not a free/lost-resources desync — but it is
    /// invisible in the log.</para>
    ///
    /// <para><b>What this does.</b> At each non-rewind discard core it computes the
    /// discarded segment's UT window and emits ONE <c>Warn</c> summarizing the
    /// physical route mutations that fired inside it and were left un-reversed, so the
    /// residual is greppable (<c>[Route] ... Rec-3 residual</c>). It changes NO
    /// behavior: nothing is reversed, retired, or gated — funds and cargo both persist
    /// exactly as before. The full reverse-on-discard fix is deferred (see the plan
    /// Phase 4 follow-up and <see cref="RouteRevertSafety"/>).</para>
    ///
    /// <para><b>Rewind/RP-backed discards are skipped.</b> A discard whose session has
    /// a reachable full-world quicksave (a Re-Fly / RewindPoint-backed session) is
    /// classified revertable by <see cref="RouteRevertSafety.PhysicalMutationRevertable"/>
    /// and reports nothing — the quicksave restore is its rollback path.</para>
    ///
    /// <para><b>ERS/ELS grep-gate:</b> every method takes the action list + the
    /// discarded recordings BY PARAMETER (the allowlisted discard core passes the
    /// ledger action list in), so this file reads neither the static ledger nor the
    /// committed-recordings store and needs no allowlist entry.</para>
    /// </summary>
    internal static class RouteDiscardObservability
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// Computes the inclusive UT window <c>[<paramref name="minUT"/>,
        /// <paramref name="maxUT"/>]</c> spanned by the discarded recordings, from each
        /// recording's <see cref="Recording.StartUT"/> / <see cref="Recording.EndUT"/>.
        /// Recordings with no real bounds (both ends at the literal <c>0.0</c> fallback,
        /// or a NaN end) are skipped so a boundsless placeholder cannot drag the window
        /// down to zero and over-report. Returns false when no recording contributes a
        /// usable span (then the caller reports nothing).
        /// </summary>
        internal static bool TryComputeDiscardedWindow(
            IEnumerable<Recording> recordings, out double minUT, out double maxUT)
        {
            minUT = double.PositiveInfinity;
            maxUT = double.NegativeInfinity;
            bool any = false;

            if (recordings != null)
            {
                foreach (Recording rec in recordings)
                {
                    if (rec == null)
                        continue;

                    double s = rec.StartUT;
                    double e = rec.EndUT;

                    // Skip the boundsless-placeholder signature (no trajectory and no
                    // explicit bounds => both fall back to 0.0), any non-finite bound, and
                    // an INVERTED span (e < s). Inversion happens for a start-only recording
                    // (ExplicitStartUT set, ExplicitEndUT NaN => StartUT > 0 but EndUT falls
                    // back to 0.0); accepting it would push maxUT below minUT and make the
                    // whole window select nothing (silent under-report).
                    if (double.IsNaN(s) || double.IsNaN(e) || double.IsInfinity(s) || double.IsInfinity(e))
                        continue;
                    if (s == 0.0 && e == 0.0)
                        continue;
                    if (e < s)
                        continue;

                    if (s < minUT) minUT = s;
                    if (e > maxUT) maxUT = e;
                    any = true;
                }
            }

            if (!any)
            {
                minUT = 0.0;
                maxUT = 0.0;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Pure decision + summary: counts the physical route mutations
        /// (<see cref="RouteLedgerRetire.IsPhysicalRouteMutation"/>) among the
        /// free-standing route rows inside <c>[<paramref name="minUT"/>,
        /// <paramref name="maxUT"/>]</c> that a non-rewind discard leaves un-reversed,
        /// and builds a human-readable <paramref name="summary"/>. Returns 0 (and an
        /// empty summary) when the discard is rewind/RP-backed (the quicksave is its
        /// rollback) or when no physical route row fired in the window. No side effects.
        /// </summary>
        internal static int SummarizePhysicalLeak(
            IReadOnlyList<GameAction> actions,
            double minUT,
            double maxUT,
            bool rewindOrRpBacked,
            out string summary)
        {
            summary = string.Empty;

            // At discard time the segment is definitionally a non-rewind in-flight
            // segment (inFlightNonRewindSegment: true), so the classifier reduces to
            // "revertable IFF rewind/RP-backed". A revertable discard leaks nothing.
            if (RouteRevertSafety.PhysicalMutationRevertable(
                    rewindOrRpBacked, inFlightNonRewindSegment: true))
                return 0;

            List<GameAction> inWindow =
                RouteLedgerRetire.SelectFreeStandingRouteActionsInWindow(actions, minUT, maxUT);
            if (inWindow.Count == 0)
                return 0;

            var sb = new StringBuilder();
            int leaked = 0;
            for (int i = 0; i < inWindow.Count; i++)
            {
                GameAction a = inWindow[i];
                if (!RouteLedgerRetire.IsPhysicalRouteMutation(a))
                    continue;

                leaked++;
                if (sb.Length > 0)
                    sb.Append("; ");
                sb.Append("route '").Append(a.RouteId ?? "<?>").Append("'");
                if (!string.IsNullOrEmpty(a.RouteCycleId))
                    sb.Append("/cycle ").Append(a.RouteCycleId);
                sb.Append(' ').Append(a.Type).Append(' ').Append(DescribeManifest(a));
            }

            if (leaked == 0)
                return 0;

            summary = leaked + " route physical mutation(s) fired inside the discarded window ["
                + minUT.ToString("F1", IC) + ".." + maxUT.ToString("F1", IC)
                + "] and were not undone by this non-rewind discard: " + sb
                + " (route rows carry no RecordingId, so this UT window may also include a"
                + " concurrent committed route; no reverse writer, funds+cargo both kept,"
                + " Rec-3 reverse-on-discard deferred)";
            return leaked;
        }

        /// <summary>
        /// The discard-core entry point: computes the discarded window from
        /// <paramref name="discardedRecordings"/>, summarizes the physical route leak,
        /// and emits ONE <c>Warn</c> when there is one. Returns the leaked-row count
        /// (0 = nothing to report). Changes no state.
        /// </summary>
        internal static int ReportDiscardLeakForRecordings(
            IEnumerable<Recording> discardedRecordings,
            IReadOnlyList<GameAction> ledgerActions,
            bool rewindOrRpBacked,
            string context)
        {
            if (!TryComputeDiscardedWindow(discardedRecordings, out double minUT, out double maxUT))
                return 0;

            int leaked = SummarizePhysicalLeak(
                ledgerActions, minUT, maxUT, rewindOrRpBacked, out string summary);
            if (leaked > 0)
                ParsekLog.Warn("Route",
                    "[Rec-3 residual] " + summary + " (context=" + (context ?? "<none>") + ")");
            return leaked;
        }

        /// <summary>
        /// Compact one-line manifest description for the leak summary: resource
        /// name=amount pairs and/or an inventory item count.
        /// </summary>
        private static string DescribeManifest(GameAction a)
        {
            var sb = new StringBuilder();
            if (a.RouteResourceManifest != null && a.RouteResourceManifest.Count > 0)
            {
                bool first = true;
                foreach (KeyValuePair<string, double> kv in a.RouteResourceManifest)
                {
                    if (!first) sb.Append(", ");
                    sb.Append(kv.Key).Append('=').Append(kv.Value.ToString("F1", IC));
                    first = false;
                }
            }
            if (a.RouteInventoryManifest != null && a.RouteInventoryManifest.Count > 0)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(a.RouteInventoryManifest.Count).Append(" inventory item(s)");
            }
            if (sb.Length == 0)
                sb.Append("(no manifest)");
            return sb.ToString();
        }
    }
}
