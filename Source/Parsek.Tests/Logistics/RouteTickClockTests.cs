using System;
using System.Collections.Generic;
using System.IO;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// The route tick pacing (todo ROUTE-TICK-BASELINE-SET-BEFORE-GO-BACK-CLOCK-MOVE). The go-back
    /// rewind loads the Space Center at the pre-rewind clock (persistent.sfs) and moves the UT back
    /// from <c>ParsekScenario.ApplyRewindResourceAdjustment</c> one frame later, after the new
    /// scenario's first Update has taken the pre-rewind UT as its tick baseline. A baseline left
    /// ahead of the clock held every route tick until the clock passed it again: H58's collected
    /// logs read OnLoad at ut=1601.9, `UT adjustment: 1601.9 -> 1585.5`, then no route tick across
    /// the 2.4 s of game time that followed.
    /// </summary>
    [Collection("Sequential")]
    public class RouteTickClockTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteTickClockTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private const double Interval = 1.0;

        // catches: the pacing contract the backward branch must not disturb. The first Update
        // only seeds, then one tick per interval of game time.
        [Fact]
        public void FirstUpdateSeeds_ThenOneTickPerInterval()
        {
            double last = -1.0;
            Assert.Equal(RouteTickClockStep.Seeded, RouteTickClock.Advance(ref last, 1000.0, Interval));
            Assert.Equal(1000.0, last);
            Assert.Equal(RouteTickClockStep.Waiting, RouteTickClock.Advance(ref last, 1000.5, Interval));
            Assert.Equal(1000.0, last);
            Assert.Equal(RouteTickClockStep.Tick, RouteTickClock.Advance(ref last, 1001.0, Interval));
            Assert.Equal(1001.0, last);
            Assert.Equal(RouteTickClockStep.Tick, RouteTickClock.Advance(ref last, 5000.0, Interval));
            Assert.Equal(5000.0, last);
            Assert.DoesNotContain(logLines, l => l.Contains("Tick clock: UT moved back"));
        }

        // catches: the defect. The H58 sequence: seeded at the pre-rewind clock, the rewind moves
        // the UT back 16.4 s, and the next interval of game time must tick, not wait for 1602.9.
        [Fact]
        public void ClockMovedBack_ReseedsTheBaseline_TheNextIntervalTicks()
        {
            double last = -1.0;
            RouteTickClock.Advance(ref last, 1601.9, Interval);   // the Space Center's first Update

            Assert.Equal(RouteTickClockStep.ReseededClockMovedBack,
                RouteTickClock.Advance(ref last, 1585.5, Interval)); // after UT adjustment: 1601.9 -> 1585.5
            Assert.Equal(1585.5, last);
            Assert.Equal(RouteTickClockStep.Waiting, RouteTickClock.Advance(ref last, 1586.0, Interval));
            Assert.Equal(RouteTickClockStep.Tick, RouteTickClock.Advance(ref last, 1586.5, Interval));
            Assert.Equal(RouteTickClockStep.Tick, RouteTickClock.Advance(ref last, 1587.5, Interval));

            Assert.Contains(logLines, l => l.Contains("[Route]")
                && l.Contains("Tick clock: UT moved back from 1601.9 to 1585.5"));
        }

        // catches: Update keeping its own pacing arithmetic beside the helper (the fix would then
        // never run in game). OnLoad / Update are not xUnit-drivable.
        [Fact]
        public void ScenarioUpdate_PacesRouteTicksThroughTheClock()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string source = File.ReadAllText(Path.Combine(projectRoot, "Source", "Parsek", "ParsekScenario.cs"))
                .Replace("\r\n", "\n");
            int start = source.IndexOf("        private void Update()\n", StringComparison.Ordinal);
            Assert.True(start >= 0, "ParsekScenario.Update is missing");
            int end = source.IndexOf("\n        }\n", start, StringComparison.Ordinal);
            string body = source.Substring(start, end - start);

            int advanceIdx = body.IndexOf(
                "RouteTickClock.Advance(ref lastRouteTickUT, currentUT, RouteOrchestrator.TickIntervalSec)",
                StringComparison.Ordinal);
            int tickIdx = body.IndexOf("RouteOrchestrator.Tick(currentUT);", StringComparison.Ordinal);
            Assert.True(advanceIdx >= 0, "Update must pace route ticks through RouteTickClock.Advance");
            Assert.True(tickIdx > advanceIdx, "the clock decides before the tick runs");
            Assert.Contains("!= RouteTickClockStep.Tick)", body);
            Assert.DoesNotContain("currentUT - lastRouteTickUT", body);
            Assert.DoesNotContain("lastRouteTickUT = currentUT", body);
        }
    }
}
