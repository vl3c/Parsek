using System;
using Xunit;
using static Parsek.MissionsWindowUI;

namespace Parsek.Tests
{
    // Unit tests for the PURE display helpers the Missions tab's periodicity column uses
    // (Phase 3 UI of docs/dev/design-mission-periodicity.md): the compact countdown formatter,
    // the "~" period formatter, the basis label, the combined period-cell display, and the
    // four-state T- cell text. The IMGUI layout itself is playtest-verified; these guard the
    // exact strings the player sees. Each test states what makes it fail.
    //
    // The day-length helpers (FormatCountdownCompact / FormatPeriodCompact) respect the player's
    // Kerbin/Earth calendar via ParsekTimeFormat; the tests pin Earth time (86400 s/day, 365 d/y)
    // so the day/year boundaries are deterministic and match the design's example strings.
    [Collection("Sequential")]
    public class MissionsWindowPeriodicityDisplayTests : IDisposable
    {
        public MissionsWindowPeriodicityDisplayTests()
        {
            ParsekTimeFormat.KerbinTimeOverrideForTesting = false; // Earth time: 86400 s/day
        }

        public void Dispose()
        {
            ParsekTimeFormat.ResetForTesting();
        }

        // ===================== FormatCountdownCompact =====================

        [Fact]
        public void FormatCountdownCompact_SubMinute_ShowsSeconds()
        {
            // Fails if a sub-minute duration is not shown as bare seconds (e.g. "5s").
            Assert.Equal("5s", FormatCountdownCompact(5.0));
            Assert.Equal("59s", FormatCountdownCompact(59.0));
        }

        [Fact]
        public void FormatCountdownCompact_Minutes_ShowsMinutesAndSeconds()
        {
            // Fails if the minutes range does not read "Nm Ss" (the design's "12m 30s" example).
            Assert.Equal("12m 30s", FormatCountdownCompact(12 * 60 + 30));
            Assert.Equal("1m 0s", FormatCountdownCompact(60.0));
        }

        [Fact]
        public void FormatCountdownCompact_Hours_ShowsHoursAndMinutes()
        {
            // Fails if the hours range does not read "Nh Mm" (the design's "2h 14m" example).
            Assert.Equal("2h 14m", FormatCountdownCompact(2 * 3600 + 14 * 60 + 9));
            Assert.Equal("1h 0m", FormatCountdownCompact(3600.0));
        }

        [Fact]
        public void FormatCountdownCompact_Days_ShowsDaysAndHours()
        {
            // Fails if the days range does not read "Nd Mh" (the design's "3d 5h" example), at
            // Earth day length (86400 s).
            Assert.Equal("3d 5h", FormatCountdownCompact(3 * 86400 + 5 * 3600 + 30 * 60));
            // Exactly N days with no remainder hours drops the hours term.
            Assert.Equal("2d", FormatCountdownCompact(2 * 86400));
        }

        [Fact]
        public void FormatCountdownCompact_Years_ShowsYearsAndDays()
        {
            // Fails if a multi-year duration does not read "Ny Md" at Earth year length (365 d).
            Assert.Equal("1y 42d", FormatCountdownCompact(365L * 86400 + 42L * 86400));
            Assert.Equal("2y", FormatCountdownCompact(2L * 365 * 86400));
        }

        [Fact]
        public void FormatCountdownCompact_ZeroAndNegativeAndNaN_ClampToZeroSeconds()
        {
            // Fails if zero / negative / NaN / infinity are not clamped to "0s" (the countdown
            // should never show a negative time or throw - a window at/behind now reads "0s").
            Assert.Equal("0s", FormatCountdownCompact(0.0));
            Assert.Equal("0s", FormatCountdownCompact(-1234.0));
            Assert.Equal("0s", FormatCountdownCompact(double.NaN));
            Assert.Equal("0s", FormatCountdownCompact(double.PositiveInfinity));
        }

        [Fact]
        public void FormatCountdownCompact_KerbinTime_UsesSixHourDays()
        {
            // Fails if the day boundary ignores the Kerbin calendar: 21600 s is exactly one Kerbin
            // day, so it must read "1d", not "6h".
            ParsekTimeFormat.KerbinTimeOverrideForTesting = true; // 21600 s/day
            Assert.Equal("1d", FormatCountdownCompact(21600.0));
        }

        // ===================== FormatPeriodCompact =====================

        [Fact]
        public void FormatPeriodCompact_PicksLargestUnit_OneDecimalDroppingTrailingZero()
        {
            // Fails if the period is not shown as a single "~" approximate unit. ~6h for a Kerbin
            // rotation-ish period; the ".0" must be dropped (so "~6h", not "~6.0h").
            Assert.Equal("~6h", FormatPeriodCompact(6 * 3600));
            Assert.Equal("~2.5h", FormatPeriodCompact(2 * 3600 + 30 * 60));
        }

        [Fact]
        public void FormatPeriodCompact_Days_WithDecimal()
        {
            // Fails if a ~1.6-day Mun-window period is not "~1.6d" at Earth day length.
            Assert.Equal("~1.6d", FormatPeriodCompact(1.6 * 86400));
            Assert.Equal("~2d", FormatPeriodCompact(2.0 * 86400));
        }

        [Fact]
        public void FormatPeriodCompact_MinutesAndSeconds()
        {
            Assert.Equal("~36m", FormatPeriodCompact(36 * 60));
            Assert.Equal("~9s", FormatPeriodCompact(9.0));
        }

        [Fact]
        public void FormatPeriodCompact_NonPositiveOrNaN_ZeroSeconds()
        {
            // Fails if a degenerate period throws or shows garbage instead of "~0s".
            Assert.Equal("~0s", FormatPeriodCompact(0.0));
            Assert.Equal("~0s", FormatPeriodCompact(-100.0));
            Assert.Equal("~0s", FormatPeriodCompact(double.NaN));
        }

        // ===================== BuildPeriodBasisLabel / BuildPeriodCellDisplay =====================

        [Fact]
        public void BuildPeriodBasisLabel_RotationVsOrbital()
        {
            // Fails if the basis label does not distinguish a rotation lock from an intercept
            // window (the "why" the player reads next to the period).
            Assert.Equal("(Kerbin rot)", BuildPeriodBasisLabel(ConstraintKind.Rotation, "Kerbin"));
            Assert.Equal("(Mun window)", BuildPeriodBasisLabel(ConstraintKind.Orbital, "Mun"));
        }

        [Fact]
        public void BuildPeriodBasisLabel_EmptyBody_EmptyLabel()
        {
            // Fails if a missing dominant body still emits a "( rot)" with no name.
            Assert.Equal("", BuildPeriodBasisLabel(ConstraintKind.Rotation, null));
            Assert.Equal("", BuildPeriodBasisLabel(ConstraintKind.Orbital, ""));
        }

        [Fact]
        public void BuildPeriodCellDisplay_CombinesPeriodAndBasis()
        {
            // Fails if the period cell does not read "~P (basis)" (the design's "~6h (Kerbin rot)"
            // / "~1.6d (Mun window)" examples).
            Assert.Equal("~6h (Kerbin rot)",
                BuildPeriodCellDisplay(6 * 3600, ConstraintKind.Rotation, "Kerbin"));
            Assert.Equal("~1.6d (Mun window)",
                BuildPeriodCellDisplay(1.6 * 86400, ConstraintKind.Orbital, "Mun"));
        }

        [Fact]
        public void BuildPeriodCellDisplay_NoBasis_DropsSuffix()
        {
            // Fails if a missing basis leaves a trailing space / empty parens.
            Assert.Equal("~6h", BuildPeriodCellDisplay(6 * 3600, ConstraintKind.Rotation, null));
        }

        // ===================== BuildTMinusCellText (the four states) =====================

        [Fact]
        public void BuildTMinus_NotLooping_Blank()
        {
            // Fails if a non-looping mission shows anything in the T- cell.
            Assert.Equal("", BuildTMinusCellText(
                looping: false, solved: true, shouldPhaseLock: true,
                p: 21549.0, nextWindowUT: 5000.0, nowUT: 1000.0));
        }

        [Fact]
        public void BuildTMinus_NotSolved_Blank()
        {
            // Fails if a looping mission with no computed solution (default) shows stale text.
            Assert.Equal("", BuildTMinusCellText(
                looping: true, solved: false, shouldPhaseLock: false,
                p: double.NaN, nextWindowUT: double.NaN, nowUT: 1000.0));
        }

        [Fact]
        public void BuildTMinus_Unsupported_NotAligned()
        {
            // Fails if an unsupported config (the no-lock sentinel: ShouldPhaseLock==false) does
            // NOT read "not aligned" (cross-parent / rendezvous - the body-only solver can't
            // schedule it yet).
            Assert.Equal("not aligned", BuildTMinusCellText(
                looping: true, solved: true, shouldPhaseLock: false,
                p: double.NaN, nextWindowUT: double.NaN, nowUT: 1000.0));
        }

        [Fact]
        public void BuildTMinus_Unconstrained_Continuous()
        {
            // Fails if an unconstrained config (P == MinCycleDuration) does not read "continuous"
            // (nothing to line up -> loop freely).
            Assert.Equal("continuous", BuildTMinusCellText(
                looping: true, solved: true, shouldPhaseLock: true,
                p: LoopTiming.MinCycleDuration, nextWindowUT: 1000.0, nowUT: 1000.0));
        }

        [Fact]
        public void BuildTMinus_Supported_ShowsCountdown()
        {
            // Fails if a supported + constrained config does not read "T- <countdown>" to the next
            // window. now=1000, nextWindow=1000+2h14m9s -> "T- 2h 14m".
            double now = 1000.0;
            double next = now + (2 * 3600 + 14 * 60 + 9);
            Assert.Equal("T- 2h 14m", BuildTMinusCellText(
                looping: true, solved: true, shouldPhaseLock: true,
                p: 138984.0, nextWindowUT: next, nowUT: now));
        }

        [Fact]
        public void BuildTMinus_WindowAtOrBehindNow_ZeroCountdown()
        {
            // Fails if a window at/behind now produces a negative countdown instead of "T- 0s"
            // (the loop is launching now / parked exactly on the window).
            Assert.Equal("T- 0s", BuildTMinusCellText(
                looping: true, solved: true, shouldPhaseLock: true,
                p: 138984.0, nextWindowUT: 900.0, nowUT: 1000.0));
            Assert.Equal("T- 0s", BuildTMinusCellText(
                looping: true, solved: true, shouldPhaseLock: true,
                p: 138984.0, nextWindowUT: 1000.0, nowUT: 1000.0));
        }
    }
}
