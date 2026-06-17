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
        public void BuildPeriodBasisLabel_VesselOrbital_StationWindow()
        {
            // M4a test 13: a VesselOrbital (station rendezvous) period cell reads
            // "(station window)" - NOT "(Kerbin window)", since BodyName holds the ORBITED body
            // and the period is the station's orbit, not a celestial event of that body.
            Assert.Equal("(station window)",
                BuildPeriodBasisLabel(ConstraintKind.VesselOrbital, "Kerbin"));
            // The label is fixed text, so it renders even without a body name.
            Assert.Equal("(station window)",
                BuildPeriodBasisLabel(ConstraintKind.VesselOrbital, null));
        }

        [Fact]
        public void BuildPeriodCellDisplay_VesselOrbital_CombinesPeriodAndStationWindow()
        {
            // The combined cell reads "~30m (station window)" for a 1800 s station orbit.
            Assert.Equal("~30m (station window)",
                BuildPeriodCellDisplay(1800.0, ConstraintKind.VesselOrbital, "Kerbin"));
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

        [Fact]
        public void BuildReaimPeriodCellDisplay_ShowsSynodicAndTargetTransfer()
        {
            // Re-aim period cell: the synodic cadence + a "(<target> transfer)" basis. The fixture pins
            // the Earth calendar (365 d/y), so 2.1 years = 2.1 * 31536000 s reads "~2.1y".
            double synodic = 2.1 * 365.0 * 86400.0;
            Assert.Equal("~2.1y (Duna transfer)",
                BuildReaimPeriodCellDisplay(synodic, "Duna"));
            // No target -> just the period (no empty parens).
            Assert.Equal("~2.1y", BuildReaimPeriodCellDisplay(synodic, null));
        }

        [Fact]
        public void BuildTMinus_Reaim_ShowsCountdown_NotNotAligned()
        {
            // Re-aim is cross-parent (ShouldPhaseLock==false), which would normally read "not aligned",
            // but a re-aim unit IS aligned to a synodic window: it must count down to the next relaunch.
            double now = 1000.0;
            double next = now + (2 * 3600 + 14 * 60 + 9);
            Assert.Equal("T- 2h 14m", BuildTMinusCellText(
                looping: true, solved: true, shouldPhaseLock: false, unitBuilt: true,
                p: double.NaN, nextRelaunchUT: next, nowUT: now, isReaim: true));
        }

        [Fact]
        public void BuildTMinus_Reaim_NoWindow_FallsBackToNotAligned()
        {
            // Defensive: a re-aim unit with no resolvable relaunch UT still reads "not aligned".
            Assert.Equal("not aligned", BuildTMinusCellText(
                looping: true, solved: true, shouldPhaseLock: false, unitBuilt: true,
                p: double.NaN, nextRelaunchUT: double.NaN, nowUT: 1000.0, isReaim: true));
        }

        // ===================== BuildTMinusCellText (the four states) =====================

        [Fact]
        public void BuildTMinus_NotLooping_Blank()
        {
            // Fails if a non-looping mission shows anything in the T- cell.
            Assert.Equal("", BuildTMinusCellText(
                looping: false, solved: true, shouldPhaseLock: true, unitBuilt: true,
                p: 21549.0, nextRelaunchUT: 5000.0, nowUT: 1000.0));
        }

        [Fact]
        public void BuildTMinus_NotSolved_Blank()
        {
            // Fails if a looping mission with no computed solution (default) shows stale text.
            Assert.Equal("", BuildTMinusCellText(
                looping: true, solved: false, shouldPhaseLock: false, unitBuilt: false,
                p: double.NaN, nextRelaunchUT: double.NaN, nowUT: 1000.0));
        }

        [Fact]
        public void BuildTMinus_Unsupported_NotAligned()
        {
            // Fails if an unsupported config (the no-lock sentinel: ShouldPhaseLock==false) does
            // NOT read "not aligned" (cross-parent / rendezvous - the body-only solver can't
            // schedule it yet).
            Assert.Equal("not aligned", BuildTMinusCellText(
                looping: true, solved: true, shouldPhaseLock: false, unitBuilt: true,
                p: double.NaN, nextRelaunchUT: double.NaN, nowUT: 1000.0));
        }

        [Fact]
        public void BuildTMinus_NoUnitBuilt_NotAligned()
        {
            // Fails if a phase-lockable mission with NO engine unit (every loop member was trimmed
            // off, so the engine relaunches nothing) does not read "not aligned". The engine builds
            // no unit -> there is no schedule to count down to, so the cell must not show a stale
            // "T- ..." countdown derived from the periodicity solution alone.
            Assert.Equal("not aligned", BuildTMinusCellText(
                looping: true, solved: true, shouldPhaseLock: true, unitBuilt: false,
                p: 138984.0, nextRelaunchUT: double.NaN, nowUT: 1000.0));
        }

        [Fact]
        public void BuildTMinus_Unconstrained_Continuous()
        {
            // Fails if an unconstrained config (P == MinCycleDuration) does not read "continuous"
            // (nothing to line up -> loop freely).
            Assert.Equal("continuous", BuildTMinusCellText(
                looping: true, solved: true, shouldPhaseLock: true, unitBuilt: true,
                p: LoopTiming.MinCycleDuration, nextRelaunchUT: 1000.0, nowUT: 1000.0));
        }

        [Fact]
        public void BuildTMinus_Supported_ShowsCountdownToRelaunch()
        {
            // Fails if a supported + constrained config does not read "T- <countdown>" to the
            // engine's next RELAUNCH. now=1000, nextRelaunch=1000+2h14m9s -> "T- 2h 14m".
            double now = 1000.0;
            double next = now + (2 * 3600 + 14 * 60 + 9);
            Assert.Equal("T- 2h 14m", BuildTMinusCellText(
                looping: true, solved: true, shouldPhaseLock: true, unitBuilt: true,
                p: 138984.0, nextRelaunchUT: next, nowUT: now));
        }

        [Fact]
        public void BuildTMinus_RelaunchAtOrBehindNow_ZeroCountdown()
        {
            // Fails if a relaunch at/behind now produces a negative countdown instead of "T- 0s"
            // (the loop is launching now / parked exactly on the relaunch).
            Assert.Equal("T- 0s", BuildTMinusCellText(
                looping: true, solved: true, shouldPhaseLock: true, unitBuilt: true,
                p: 138984.0, nextRelaunchUT: 900.0, nowUT: 1000.0));
            Assert.Equal("T- 0s", BuildTMinusCellText(
                looping: true, solved: true, shouldPhaseLock: true, unitBuilt: true,
                p: 138984.0, nextRelaunchUT: 1000.0, nowUT: 1000.0));
        }

        // ===================== ComputeNextRelaunchUT (the no-drift relaunch schedule) =====================

        [Fact]
        public void ComputeNextRelaunchUT_OverlapShorterThanSpan_UsesOverlapCadence()
        {
            // Fails if a mission whose overlap cadence is shorter than its span does NOT relaunch on
            // the overlap cadence. The engine overlaps the whole mission with itself here, relaunching
            // every OverlapCadenceSeconds. anchor=1000, overlapCadence=600, span=900 -> the relaunches
            // are 1000, 1600, 2200, ...; at now=1700 the next is 2200.
            var unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 1000.0, spanEndUT: 1900.0,   // span = 900
                cadenceSeconds: 900.0, phaseAnchorUT: 1000.0,
                overlapCadenceSeconds: 600.0);
            Assert.Equal(2200.0, ComputeNextRelaunchUT(unit, 1700.0), 6);
        }

        [Fact]
        public void ComputeNextRelaunchUT_OverlapAtOrAboveSpan_UsesSpanCadence()
        {
            // Fails if a mission with no self-overlap (overlap cadence >= span) does NOT relaunch on
            // the span-clock cadence (the single span instance). anchor=1000, cadence=900,
            // overlapCadence=900 (== span, so no overlap) -> relaunches 1000, 1900, 2800; at now=2000
            // the next is 2800.
            var unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 1000.0, spanEndUT: 1900.0,   // span = 900
                cadenceSeconds: 900.0, phaseAnchorUT: 1000.0,
                overlapCadenceSeconds: 900.0);
            Assert.Equal(2800.0, ComputeNextRelaunchUT(unit, 2000.0), 6);
        }

        [Fact]
        public void ComputeNextRelaunchUT_CadenceIsMultipleOfP_TargetsRelaunchNotNextPWindow()
        {
            // The REGRESSION this guards: the old countdown targeted solution.NextWindowUT = the next
            // P-lattice window from now (anchor + the SMALLEST k*P >= now), but the engine relaunches
            // only every m*P (here m=2) and SKIPS the in-between P-window. The countdown must point at
            // the engine's relaunch (anchor + n*2P), not the skipped P-window.
            //
            // P=1000, relaunch cadence = 2*P = 2000 (overlap, span=5000 so it overlaps). anchor=10000.
            // Engine relaunches: 10000, 12000, 14000, ... at now=10500 the next RELAUNCH is 12000.
            // The next P-WINDOW from now would be 11000 (10000 + 1*P) - which the engine SKIPS.
            double p = 1000.0;
            var unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 10000.0, spanEndUT: 15000.0,   // span = 5000
                cadenceSeconds: 6000.0, phaseAnchorUT: 10000.0,
                overlapCadenceSeconds: 2.0 * p);            // 2000 < span -> overlaps
            double next = ComputeNextRelaunchUT(unit, 10500.0);
            Assert.Equal(12000.0, next, 6);                 // the engine relaunch
            Assert.NotEqual(10000.0 + p, next);             // NOT the skipped 11000 P-window
        }

        [Fact]
        public void ComputeNextRelaunchUT_ParkedBeforeForwardAnchor_ReportsAnchor()
        {
            // Fails if a forward-snapped anchor (loop parked, now < anchor) reports a negative cycle
            // instead of the anchor itself. anchor=5000, now=2000 -> next relaunch = 5000 (n clamps
            // to 0; the loop simply waits for the window).
            var unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 5000.0, spanEndUT: 5900.0,
                cadenceSeconds: 900.0, phaseAnchorUT: 5000.0,
                overlapCadenceSeconds: 600.0);
            Assert.Equal(5000.0, ComputeNextRelaunchUT(unit, 2000.0), 6);
        }

        [Fact]
        public void ComputeNextRelaunchUT_AnchorExactlyOnWindowBoundary_DoesNotSkip()
        {
            // Fails if a now landing exactly on a relaunch boundary skips to the NEXT relaunch
            // instead of reporting the current one (the eps in the ceil keeps an exact hit at "now").
            // anchor=1000, interval=600 -> relaunch at 1600; at now=1600 the next relaunch is 1600.
            var unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 1000.0, spanEndUT: 1900.0,
                cadenceSeconds: 900.0, phaseAnchorUT: 1000.0,
                overlapCadenceSeconds: 600.0);
            Assert.Equal(1600.0, ComputeNextRelaunchUT(unit, 1600.0), 6);
        }

        [Fact]
        public void ComputeNextRelaunchUT_DegenerateCadence_FallsBackToAnchor()
        {
            // Fails if a degenerate (<= 0) interval throws / divides by zero instead of naming the
            // anchor as the only launch. overlapCadence=0 (< span) selects 0 -> guarded to the anchor.
            var unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 1000.0, spanEndUT: 1900.0,
                cadenceSeconds: 0.0, phaseAnchorUT: 1000.0,
                overlapCadenceSeconds: 0.0);
            Assert.Equal(1000.0, ComputeNextRelaunchUT(unit, 5000.0), 6);
        }

        [Fact]
        public void ComputeNextRelaunchUT_LaunchAlignmentEngaged_ReportsLNMinusDelta()
        {
            // Per-loop launch alignment (borrow-at-launch): the navigable launch time the user warps to is
            // L_N - delta_N (delta_N EARLIER than nominal), so the next-relaunch cell reads the advanced UT.
            // span [0,1000], cadence 2000 (no self-overlap), phaseAnchor 300, T_sid 700 => the nominal window
            // at/after now is L = anchor + n*2000; delta for that window is (300 + n*2000) mod 700.
            const double anchor = 300, cadence = 2000, tSid = 700;
            var unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 0.0, spanEndUT: 1000.0,
                cadenceSeconds: cadence, phaseAnchorUT: anchor,
                overlapCadenceSeconds: cadence, // == cadence (no overlap) -> uses the span-clock cadence
                memberWindows: null, relaunchSchedule: null, reaimPlan: null, reaimSchedule: null,
                loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalAlignPeriodSeconds: double.NaN, arrivalAmberReason: null,
                launchBodyRotationPeriodSeconds: tSid, launchHoldEngaged: true, recordedSoiExitUT: 600.0);

            // now = 100 (before the first window L_1 = 2300; n clamps so the nominal next window is L_1).
            // Wait: anchor 300, now 100 < anchor -> n=0, nominal next = anchor = 300 = L_0; delta_0 = 300 mod
            // 700 = 300, so the advanced launch is 300 - 300 = 0. 0 < now(100) so fall forward to L_1: delta_1
            // = (300+2000) mod 700 = 200, advanced = 2300 - 200 = 2100.
            double next = ComputeNextRelaunchUT(unit, 100.0);
            Assert.Equal(2100.0, next, 6);

            // The not-engaged unit (same params) reports the plain nominal launch (no subtraction).
            var plainUnit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 0.0, spanEndUT: 1000.0,
                cadenceSeconds: cadence, phaseAnchorUT: anchor,
                overlapCadenceSeconds: cadence);
            Assert.Equal(300.0, ComputeNextRelaunchUT(plainUnit, 100.0), 6); // L_0 = anchor, no advance
        }

        // ===================== ShouldEnableWarpToWindow =====================

        [Fact]
        public void ShouldEnableWarpToWindow_LoopingBuiltFutureRelaunch_Enabled()
        {
            // Fails if a looping mission with a built unit and a future relaunch is not warp-able.
            Assert.True(ShouldEnableWarpToWindow(
                looping: true, unitBuilt: true, nextRelaunchUT: 1_250_000.0, nowUT: 10_000.0));
        }

        [Theory]
        [InlineData(false, true, 1_250_000.0, 10_000.0)]   // not looping
        [InlineData(true, false, 1_250_000.0, 10_000.0)]   // no engine unit built
        [InlineData(true, true, 10_000.0, 1_250_000.0)]    // relaunch in the past -> nothing to warp to
        [InlineData(true, true, 10_000.5, 10_000.0)]       // relaunch <= now + 1s -> no-op, disabled
        public void ShouldEnableWarpToWindow_Disabled_Cases(
            bool looping, bool unitBuilt, double nextRelaunchUT, double nowUT)
        {
            Assert.False(ShouldEnableWarpToWindow(looping, unitBuilt, nextRelaunchUT, nowUT));
        }

        [Fact]
        public void ShouldEnableWarpToWindow_NaNOrInfiniteRelaunch_Disabled()
        {
            // Fails if a NaN / infinite relaunch UT (no faithful window / unsupported config) is
            // treated as a valid warp target.
            Assert.False(ShouldEnableWarpToWindow(true, true, double.NaN, 10_000.0));
            Assert.False(ShouldEnableWarpToWindow(true, true, double.PositiveInfinity, 10_000.0));
        }

        // ===================== Zero-drift: scheduled ComputeNextRelaunchUT + period cell =====================

        [Fact]
        public void ComputeNextRelaunchUT_ScheduledUnit_TargetsTheNextScheduledLaunch()
        {
            // Fails if a scheduled (zero-drift) unit's next relaunch is computed from the uniform
            // anchor + n*cadence instead of the non-uniform schedule. Synthetic 100/31 schedule:
            // launches at UT 900, 1300, 1800. The next relaunch strictly after now must be the next
            // SCHEDULED launch, not phaseAnchor + n*cadence.
            var schedule = new MissionRelaunchSchedule(
                0.0, 100.0, new double[] { 31.0 }, new double[] { 2.0 }, floorUT: 0.0,
                lookaheadMultiples: 100);
            double cad = Math.Max(50.0, schedule.MinIntervalSeconds);
            var unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 }, spanStartUT: 0.0, spanEndUT: 50.0,
                cadenceSeconds: cad, phaseAnchorUT: schedule.FirstLaunchUT,
                overlapCadenceSeconds: cad, memberWindows: null, relaunchSchedule: schedule);

            Assert.Equal(900.0, ComputeNextRelaunchUT(unit, 0.0), 6);     // parked -> first launch
            Assert.Equal(1300.0, ComputeNextRelaunchUT(unit, 900.0), 6);  // strictly after L_0
            Assert.Equal(1800.0, ComputeNextRelaunchUT(unit, 1500.0), 6); // next after a between-time
        }

        [Fact]
        public void TransitedBodyRotationMode_SettingsLabelAndCycle()
        {
            // Guards the Settings UI A/B control: the three labels and the Drop -> Loose -> Tight -> Drop
            // cycle order.
            Assert.Equal("Off (frequent)",
                SettingsWindowUI.TransitedBodyRotationModeLabel(TransitedBodyRotationMode.Drop));
            Assert.Equal("Loose (~monthly)",
                SettingsWindowUI.TransitedBodyRotationModeLabel(TransitedBodyRotationMode.Loose));
            Assert.Equal("Precise (rare)",
                SettingsWindowUI.TransitedBodyRotationModeLabel(TransitedBodyRotationMode.Tight));

            Assert.Equal(TransitedBodyRotationMode.Loose,
                SettingsWindowUI.CycleTransitedBodyRotationMode(TransitedBodyRotationMode.Drop));
            Assert.Equal(TransitedBodyRotationMode.Tight,
                SettingsWindowUI.CycleTransitedBodyRotationMode(TransitedBodyRotationMode.Loose));
            Assert.Equal(TransitedBodyRotationMode.Drop,
                SettingsWindowUI.CycleTransitedBodyRotationMode(TransitedBodyRotationMode.Tight));
        }

        [Fact]
        public void BuildScheduledPeriodCellDisplay_ShowsVariesWithBasis()
        {
            // Fails if the scheduled (non-uniform) period cell does not mark the cadence as varying.
            // Earth time: 86400 s/day. Equal min==max collapses to a single value, so an orbital (Mun)
            // basis -> "(Mun window, varies)".
            Assert.Equal("~2d (Mun window, varies)",
                BuildScheduledPeriodCellDisplay(
                    2.0 * 86400.0, 2.0 * 86400.0, ConstraintKind.Orbital, "Mun"));
            // A rotation basis.
            Assert.Equal("~6h (Kerbin rot, varies)",
                BuildScheduledPeriodCellDisplay(
                    6.0 * 3600.0, 6.0 * 3600.0, ConstraintKind.Rotation, "Kerbin"));
            // No body -> just "~P (varies)".
            Assert.Equal("~2d (varies)",
                BuildScheduledPeriodCellDisplay(
                    2.0 * 86400.0, 2.0 * 86400.0, ConstraintKind.Orbital, null));
        }

        [Fact]
        public void BuildScheduledPeriodCellDisplay_ShowsRangeWhenMinMaxDiffer()
        {
            // Fails if a genuinely varying min!=max cadence is not shown as a "min-max" range. The
            // high end drops its leading "~" so only one tilde shows; the "varies" basis suffix stays.
            Assert.Equal("~13d-30d (Mun window, varies)",
                BuildScheduledPeriodCellDisplay(
                    13.0 * 86400.0, 30.0 * 86400.0, ConstraintKind.Orbital, "Mun"));
        }

        [Fact]
        public void FormatScheduledIntervalRange_CollapsesWithinFivePercent()
        {
            // Two ends within ~5% collapse to a single value (no dash) - the tight cadence after the
            // loiter knob engages. 13d and 13.5d are within 5%, so the range collapses to "~13.5d"
            // (FormatPeriodCompact rounds the LOW end's 13.0d display to "~13d", but the collapse uses
            // the low end string).
            Assert.Equal("~13d",
                FormatScheduledIntervalRange(13.0 * 86400.0, 13.5 * 86400.0));
            // Exactly equal -> single value.
            Assert.Equal("~13d", FormatScheduledIntervalRange(13.0 * 86400.0, 13.0 * 86400.0));
            // Clearly different (>5%) -> a dash range, single tilde.
            Assert.Equal("~13d-30d",
                FormatScheduledIntervalRange(13.0 * 86400.0, 30.0 * 86400.0));
            // A NaN end falls back to the other end.
            Assert.Equal("~13d", FormatScheduledIntervalRange(13.0 * 86400.0, double.NaN));
            Assert.Equal("~30d", FormatScheduledIntervalRange(double.NaN, 30.0 * 86400.0));
        }

    }
}
