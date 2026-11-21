using System;
using System.Collections.Generic;
using System.Text;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// The route event log's per-route index against the pre-index full scan
    /// (todo ROUTE-TICK-EVENT-LOG-SCANS): the same seeded timeline, run once reading the index
    /// and once with <see cref="RouteEventLog.IndexDisabledForTesting"/> (every reader handed the
    /// whole log, as before the index), writes the same route event log, leaves the same world
    /// and route counters, and prints the same log lines. The log clock is frozen so the
    /// rate-limited lines do not depend on how fast each run went.
    /// </summary>
    public partial class RouteTimelineFuzzerTests
    {
        private string CaptureRun(bool indexDisabled, Func<string> run)
        {
            ResetStores();
            RouteEventLog.ResetForTesting();
            RouteOrchestrator.ResetWindowBasisTransitionCountsForTesting();
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.ClockOverrideForTesting = () => 0.0;
            logLines.Clear();
            RouteEventLog.IndexDisabledForTesting = indexDisabled;
            try
            {
                string world = run();
                var parsek = new ConfigNode("PARSEK");
                RouteEventLog.SaveInto(parsek);
                var sb = new StringBuilder();
                sb.Append(world).Append('\n');
                foreach (Route r in RouteStore.CommittedRoutes)
                {
                    sb.Append(r.Id).Append(" completed=").Append(r.CompletedCycles.ToString(IC))
                        .Append(" skipped=").Append(r.SkippedCycles.ToString(IC))
                        .Append(" cursor=").Append(r.LastObservedLoopCycleIndex.ToString(IC))
                        .Append(" status=").Append(r.Status).Append('\n');
                }
                sb.Append(parsek.ToString()).Append('\n');
                sb.Append("--- log ---\n");
                foreach (string line in logLines)
                    sb.Append(line).Append('\n');
                return sb.ToString();
            }
            finally
            {
                RouteEventLog.IndexDisabledForTesting = false;
                ParsekLog.ClockOverrideForTesting = null;
            }
        }

        private static void AssertSameRun(string fullScan, string indexed, string what)
        {
            if (string.Equals(fullScan, indexed, StringComparison.Ordinal))
                return;
            string[] a = fullScan.Split('\n');
            string[] b = indexed.Split('\n');
            int i = 0;
            while (i < a.Length && i < b.Length && string.Equals(a[i], b[i], StringComparison.Ordinal))
                i++;
            Assert.True(false,
                $"{what}: the indexed run differs from the full-scan run at line {i.ToString(IC)}:\n" +
                $"  full scan: {(i < a.Length ? a[i] : "(end)")}\n" +
                $"  indexed:   {(i < b.Length ? b[i] : "(end)")}");
        }

        // catches: the per-route index changing any decision of the route tick over warps, saves,
        // Re-Fly starts and Discard Re-fly loads (a replay guard, the supply deficit or the
        // open-hold-episode walk answering from other rows than the full scan): a different
        // event log, world, counter or log line than the same timeline read the pre-index way.
        [Theory]
        [InlineData(1, false)]
        [InlineData(4, false)]
        [InlineData(7, false)]
        [InlineData(2, true)]
        [InlineData(5, true)]
        [InlineData(11, true)]
        public void RandomTimeline_IndexDecidesExactlyAsTheFullScan(int seed, bool withVesselOrigin)
        {
            Func<string> run = () =>
            {
                RunRandomTimeline(seed, withVesselOrigin);
                return "tank=" + tank.ToString("R", IC)
                    + (withVesselOrigin ? " depot=" + originTank.amount.ToString("R", IC) : "");
            };
            string fullScan = CaptureRun(indexDisabled: true, run);
            string indexed = CaptureRun(indexDisabled: false, run);
            Assert.Contains("RouteCargoDelivered", fullScan);
            AssertSameRun(fullScan, indexed, "seed " + seed.ToString(IC));
        }

        // catches: the same for two chained routes sharing a depot (the second route's
        // origin debit, a blocked draw on an empty depot and its hold row).
        [Theory]
        [InlineData(3)]
        [InlineData(6)]
        [InlineData(9)]
        public void RandomTimeline_ChainedRoutes_IndexDecidesExactlyAsTheFullScan(int seed)
        {
            Func<string> run = () => RunChainedTimeline(seed);
            string fullScan = CaptureRun(indexDisabled: true, run);
            string indexed = CaptureRun(indexDisabled: false, run);
            Assert.Contains("RouteCargoDelivered", fullScan);
            AssertSameRun(fullScan, indexed, "chained seed " + seed.ToString(IC));
        }
    }
}
