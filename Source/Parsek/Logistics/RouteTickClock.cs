using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>What <see cref="RouteTickClock.Advance"/> decided for one Update.</summary>
    internal enum RouteTickClockStep
    {
        /// <summary>First Update of this scenario instance: the baseline was set, no tick.</summary>
        Seeded,
        /// <summary>Less than one tick interval of game time since the last tick.</summary>
        Waiting,
        /// <summary>A tick interval passed: run <c>RouteOrchestrator.Tick</c>.</summary>
        Tick,
        /// <summary>The clock is behind the baseline: the baseline was reset to it, no tick.</summary>
        ReseededClockMovedBack,
    }

    /// <summary>
    /// The UT-delta accumulator that paces <c>RouteOrchestrator.Tick</c> from
    /// <c>ParsekScenario.Update</c> (one tick per <c>RouteOrchestrator.TickIntervalSec</c> of game
    /// time, so time warp is respected). The baseline is per scenario instance and must follow the
    /// clock backwards: the go-back rewind sets the UT from a coroutine one frame after the new
    /// Space Center scenario's first Update has already taken the pre-rewind UT as its baseline,
    /// and a baseline left ahead of the clock holds every route tick until the clock passes it.
    /// </summary>
    internal static class RouteTickClock
    {
        private const string Tag = "Route";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// Advances the baseline <paramref name="lastTickUT"/> (negative = not seeded) for an
        /// Update at <paramref name="currentUT"/> and says whether to tick. Logs a clock move
        /// backwards (an event, at most once per move).
        /// </summary>
        internal static RouteTickClockStep Advance(ref double lastTickUT, double currentUT, double intervalSec)
        {
            if (lastTickUT < 0.0)
            {
                lastTickUT = currentUT;
                return RouteTickClockStep.Seeded;
            }
            if (currentUT < lastTickUT)
            {
                double from = lastTickUT;
                lastTickUT = currentUT;
                ParsekLog.Info(Tag,
                    "Tick clock: UT moved back from " + from.ToString("R", IC) + " to " +
                    currentUT.ToString("R", IC) + "; baseline reset, next route tick " +
                    intervalSec.ToString("R", IC) + " s of game time from here");
                return RouteTickClockStep.ReseededClockMovedBack;
            }
            if (currentUT - lastTickUT < intervalSec)
                return RouteTickClockStep.Waiting;
            lastTickUT = currentUT;
            return RouteTickClockStep.Tick;
        }
    }
}
