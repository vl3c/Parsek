using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The hover-carrier decision itself: WHEN a greyed-out control gets a carrier at all.
    ///
    /// <para>See <see cref="DisabledHoverEcho"/> for why the reason travels on a
    /// zero-size <c>GUI.Label</c> rather than on the button's own tooltip. The two
    /// predicates below are the whole Unity-free surface of that mechanism; the rest of
    /// the type is three lines of IMGUI that cannot run headlessly, and is covered live
    /// by the in-game <c>DisabledButtonHoverPublishesTooltip</c> cell.</para>
    /// </summary>
    public class DisabledHoverEchoCarrierTests
    {
        // catches: a carrier emitted for an ENABLED control, which would double-publish
        // over the tooltip the control itself already sets and pin the strip to the
        // disabled wording while the button is live.
        [Fact]
        public void AnEnabledControlNeverCarries()
        {
            Assert.False(DisabledHoverEcho.ShouldCarry(true, "Stop recording before rewinding"));
        }

        // catches: a disabled control with nothing to say blanking the strip - the
        // carrier would publish an empty tooltip and suppress whatever the pointer was
        // otherwise over.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void ADisabledControlWithNoReasonStaysSilent(string reason)
        {
            Assert.False(DisabledHoverEcho.ShouldCarry(false, reason));
        }

        [Fact]
        public void ADisabledControlWithAReasonCarriesIt()
        {
            Assert.True(DisabledHoverEcho.ShouldCarry(false, "Recording is not in the future"));
        }

        // catches: the hover test drifting from the one IMGUI itself does.
        [Fact]
        public void PointerInsideMatchesTheRect()
        {
            var rect = new Rect(10f, 20f, 100f, 30f);
            Assert.True(DisabledHoverEcho.PointerInside(rect, new Vector2(50f, 30f)));
            Assert.False(DisabledHoverEcho.PointerInside(rect, new Vector2(5f, 30f)));
            Assert.False(DisabledHoverEcho.PointerInside(rect, new Vector2(50f, 60f)));
        }

        // catches: a rect that has not been laid out yet (width or height still 0)
        // swallowing the pointer. GUILayoutUtility hands back a degenerate rect before
        // the first Repaint of a freshly opened window, and Rect.Contains on a zero-width
        // rect is false anyway - but the guard is what the house pattern in
        // SpawnControlUI.DrawSpawnControlBottomBar checks, so it is pinned here.
        [Theory]
        [InlineData(0f, 30f)]
        [InlineData(100f, 0f)]
        public void ANotYetLaidOutRectNeverSwallowsThePointer(float w, float h)
        {
            Assert.False(DisabledHoverEcho.PointerInside(new Rect(0f, 0f, w, h), Vector2.zero));
        }
    }

    /// <summary>
    /// The per-site "why is this greyed out?" wordings introduced with the hover
    /// explainer, and their fit against the strip that renders each one.
    ///
    /// <para><b>Why the budget is re-asserted here.</b> <c>TooltipEchoBudgetTests</c>
    /// pins tooltips it can SEE - <c>new GUIContent(label, "literal")</c> in a
    /// strip-hosting file. Every wording below is produced by a function instead, so that
    /// source scan cannot reach it; this class is the runtime-builder gate for the new
    /// family, in the same shape as that file's
    /// <c>RuntimeComposedLogisticsTooltips_FitTheSingleLineStrip</c>.</para>
    ///
    /// <para><b>The voice.</b> Existing why-disabled strings ("No rewind save available",
    /// "Stop recording before rewinding", "Recording is not in the future") are a bare
    /// capitalized clause with NO trailing period, stating either the blocking fact or
    /// the remedy, and never using the word "disabled" at the player. New wordings match
    /// that, and <see cref="EveryNewDisabledReason"/> holds every one of them to it
    /// mechanically so a copy edit cannot drift.</para>
    /// </summary>
    public class DisabledReasonWordingTests
    {
        /// <summary>Window strip budgets, mirroring TooltipEchoBudgetTests.StripWindows.</summary>
        private const int MainWindowBudget = 62;      // 250 px, 2 lines
        private const int SettingsBudget = 71;        // 280 px, 2 lines
        private const int SpawnControlBudget = 102;   // 750 px, 1 line
        private const int TimelineBudget = 112;       // 820 px, 1 line
        private const int RecordingsBudget = 189;     // 1355 px, 1 line (hosts Missions)
        private const int LogisticsBudget = 218;      // 1556 px, 1 line

        /// <summary>
        /// Every wording this change introduces, paired with the budget of the strip that
        /// actually renders it. A new reason added without a row here is invisible to the
        /// gate, so keep them in step.
        /// </summary>
        public static IEnumerable<object[]> EveryNewDisabledReason()
        {
            yield return Row(ParsekUI.SpawnControlLauncherDisabledReason(0),
                MainWindowBudget, "ParsekUI spawn-control launcher");

            yield return Row(SettingsWindowUI.WipeRecordingsDisabledReason(0),
                SettingsBudget, "Settings wipe recordings");
            yield return Row(SettingsWindowUI.WipeGameActionsDisabledReason(0),
                SettingsBudget, "Settings wipe game actions");

            yield return Row(SpawnControlPresentation.WarpButtonDisabledReason(true, false, true),
                SpawnControlBudget, "Spawn row warp - too far");
            yield return Row(SpawnControlPresentation.WarpButtonDisabledReason(false, true, true),
                SpawnControlBudget, "Spawn row warp - too fast");
            yield return Row(SpawnControlPresentation.WarpButtonDisabledReason(false, false, false),
                SpawnControlBudget, "Spawn row warp - pass elapsed");

            yield return Row(TimelineWindowUI.WarpToTimeDisabledReason(false, null, false, false),
                TimelineBudget, "Timeline warp - no plan reason");
            yield return Row(TimelineWindowUI.WarpToTimeDisabledReason(true, null, true, false),
                TimelineBudget, "Timeline warp - already running");
            yield return Row(TimelineWindowUI.WarpToTimeDisabledReason(true, null, false, true),
                TimelineBudget, "Timeline warp - deferred to KSC");

            yield return Row(RecordingsTableUI.LoopToggleDisabledReason(null),
                RecordingsBudget, "Loop toggle - aggregate");
            yield return Row(RecordingsTableUI.LoopPeriodDisabledReason(false, false),
                RecordingsBudget, "Loop period - loop off");
            yield return Row(RecordingsTableUI.LoopPeriodDisabledReason(true, true),
                RecordingsBudget, "Loop period - auto unit");

            yield return Row(MissionsWindowUI.MissionDeleteDisabledReason(),
                RecordingsBudget, "Mission delete");
            yield return Row(MissionsWindowUI.MissionWatchDisabledReason(false, false),
                RecordingsBudget, "Mission watch - not in flight");
            yield return Row(MissionsWindowUI.MissionWatchDisabledReason(true, false),
                RecordingsBudget, "Mission watch - nothing flying");
            yield return Row(MissionsWindowUI.MissionWarpToDisabledReason(false, true, true, true),
                RecordingsBudget, "Mission warp - wrong scene");
            yield return Row(MissionsWindowUI.MissionWarpToDisabledReason(true, false, true, true),
                RecordingsBudget, "Mission warp - not looping");
            yield return Row(MissionsWindowUI.MissionWarpToDisabledReason(true, true, false, true),
                RecordingsBudget, "Mission warp - no schedule");
            yield return Row(MissionsWindowUI.MissionWarpToDisabledReason(true, true, true, false),
                RecordingsBudget, "Mission warp - launch behind");

            yield return Row(LogisticsWindowUI.LinkButtonDisabledReason(null),
                LogisticsBudget, "Logistics link - no partner");
            yield return Row(LogisticsWindowUI.MissionLogButtonDisabledReason(null),
                LogisticsBudget, "Logistics mission log - no source");
        }

        private static object[] Row(string reason, int budget, string label)
        {
            return new object[] { reason, budget, label };
        }

        // catches: a new why-disabled wording that clips in the strip that renders it,
        // spends a strip line on a hard newline, or drifts out of the house voice
        // (sentence case, no trailing period, never the word "disabled").
        [Theory]
        [MemberData(nameof(EveryNewDisabledReason))]
        public void NewDisabledReasons_FitTheirStripAndKeepTheVoice(
            string reason, int budget, string label)
        {
            Assert.False(string.IsNullOrEmpty(reason),
                label + ": a disabled reason must actually say something - an empty string "
                + "makes the carrier stay silent and the button explains nothing.");

            Assert.DoesNotContain("\n", reason);
            Assert.DoesNotContain("\r", reason);

            Assert.True(reason.Length <= budget, string.Format(
                "{0}: reason is {1} chars but its strip holds about {2} - the tail would "
                + "marquee-scroll in instead of reading at a glance. Reason: \"{3}\"",
                label, reason.Length, budget, reason));

            Assert.True(char.IsUpper(reason[0]), string.Format(
                "{0}: house voice is sentence case - \"{1}\"", label, reason));

            Assert.False(reason.EndsWith(".", StringComparison.Ordinal), string.Format(
                "{0}: why-disabled wordings are bare clauses and take no trailing period "
                + "(matches \"No rewind save available\" / \"Stop recording before "
                + "rewinding\") - \"{1}\"", label, reason));

            Assert.DoesNotContain("disabled", reason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("greyed", reason, StringComparison.OrdinalIgnoreCase);
        }

        // catches: a reason function that keeps talking once the control is live again.
        // An enabled control must yield the empty string so ShouldCarry stays false and
        // the control's own tooltip is what reaches the strip.
        [Fact]
        public void EveryReasonFunctionGoesSilentWhenTheControlIsLive()
        {
            Assert.Equal(string.Empty, ParsekUI.SpawnControlLauncherDisabledReason(1));
            Assert.Equal(string.Empty, SettingsWindowUI.WipeRecordingsDisabledReason(1));
            Assert.Equal(string.Empty, SettingsWindowUI.WipeGameActionsDisabledReason(1));
            Assert.Equal(string.Empty,
                SpawnControlPresentation.WarpButtonDisabledReason(false, false, true));
            Assert.Equal(string.Empty,
                TimelineWindowUI.WarpToTimeDisabledReason(true, null, false, false));
            Assert.Equal(string.Empty, RecordingsTableUI.LoopPeriodDisabledReason(true, false));
            Assert.Equal(string.Empty, MissionsWindowUI.MissionWatchDisabledReason(true, true));
            Assert.Equal(string.Empty,
                MissionsWindowUI.MissionWarpToDisabledReason(true, true, true, true));
            Assert.Equal(string.Empty, LogisticsWindowUI.LinkButtonDisabledReason("route-7"));
            Assert.Equal(string.Empty, LogisticsWindowUI.MissionLogButtonDisabledReason("tree-3"));
        }
    }

    /// <summary>
    /// Per-function branch coverage for the new reason derivations - that each one picks
    /// the cause it is meant to report, not merely that it says something.
    /// </summary>
    public class DisabledReasonDerivationTests
    {
        // catches: an aggregate Loop toggle claiming a route name it does not have, or a
        // per-recording toggle dropping the name it does.
        [Fact]
        public void LoopToggleNamesTheRouteOnlyWhenItKnowsIt()
        {
            Assert.Equal("Looped by route: Minmus Ore Run",
                RecordingsTableUI.LoopToggleDisabledReason("Minmus Ore Run"));

            string aggregate = RecordingsTableUI.LoopToggleDisabledReason(null);
            Assert.DoesNotContain("Looped by route:", aggregate);
            Assert.Equal(aggregate, RecordingsTableUI.LoopToggleDisabledReason(""));
        }

        // catches: the loop-period cell reporting the Auto cause while Loop is off, which
        // would send the player to Settings when the fix is the Loop box on their own row.
        [Fact]
        public void LoopPeriodReportsLoopOffAheadOfAuto()
        {
            string loopOff = RecordingsTableUI.LoopPeriodDisabledReason(false, true);
            Assert.Contains("Loop", loopOff);
            Assert.DoesNotContain("Settings", loopOff);

            Assert.Contains("Settings", RecordingsTableUI.LoopPeriodDisabledReason(true, true));
        }

        // catches: the two proximity gates being reported out of order, or the clock gate
        // masking them. Distance comes first because closing it is what makes the speed
        // gate reachable; the elapsed-pass gate is last because it is the only one the
        // player can no longer act on.
        [Fact]
        public void WarpButtonReportsDistanceThenSpeedThenTheClock()
        {
            Assert.Contains("far",
                SpawnControlPresentation.WarpButtonDisabledReason(true, true, true));
            Assert.Contains("fast",
                SpawnControlPresentation.WarpButtonDisabledReason(false, true, true));
            Assert.Contains("already happened",
                SpawnControlPresentation.WarpButtonDisabledReason(false, false, false));
        }

        // catches: the Timeline warp button echoing its ENABLED tooltip while greyed by
        // one of the two already-warping gates - the bug that made this site worth
        // touching at all.
        [Fact]
        public void TimelineWarpPrefersThePlannerReasonThenTheWarpState()
        {
            Assert.Equal("Pick a date after the current one",
                TimelineWindowUI.WarpToTimeDisabledReason(
                    false, "Pick a date after the current one", false, false));

            string running = TimelineWindowUI.WarpToTimeDisabledReason(true, null, true, false);
            Assert.Contains("already running", running);

            string deferred = TimelineWindowUI.WarpToTimeDisabledReason(true, null, false, true);
            Assert.Contains("Space Center", deferred);
        }

        // catches: the four mission-warp gates collapsing into one wording, which would
        // send the player to fix the wrong thing.
        [Fact]
        public void MissionWarpReportsEachGateSeparately()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal)
            {
                MissionsWindowUI.MissionWarpToDisabledReason(false, true, true, true),
                MissionsWindowUI.MissionWarpToDisabledReason(true, false, true, true),
                MissionsWindowUI.MissionWarpToDisabledReason(true, true, false, true),
                MissionsWindowUI.MissionWarpToDisabledReason(true, true, true, false)
            };
            Assert.Equal(4, seen.Count);
        }

        // catches: the reason predicate drifting out of step with the ENABLE predicate,
        // which greys a button while leaving the strip silent - the exact failure this
        // whole change exists to remove, reintroduced one site at a time.
        //
        // It bit for real: the carrier first re-derived the clock leg as
        // `NextRelaunchUT > now`, but ShouldEnableWarpToWindow needs `now + 1.0` AND
        // finiteness. Through the last second before every scheduled relaunch - which
        // recurs forever on a looping mission - and for a NaN/Inf relaunch UT, the button
        // greyed with nothing to say. The call site now feeds the gate itself, and this
        // cell holds the two together over the boundary and the non-finite values.
        [Theory]
        // (warpScene, looping, unitBuilt, nextRelaunchUT, nowUT)
        [InlineData(true, true, true, 100.0, 0.0)]        // comfortably ahead: enabled
        [InlineData(true, true, true, 1.5, 0.0)]          // ahead by more than the 1.0 lead
        [InlineData(true, true, true, 1.0, 0.0)]          // exactly AT the lead: disabled
        [InlineData(true, true, true, 0.5, 0.0)]          // inside the final second: disabled
        [InlineData(true, true, true, 0.0, 0.0)]          // now
        [InlineData(true, true, true, -5.0, 0.0)]         // behind
        [InlineData(true, true, true, double.NaN, 0.0)]   // unsolved periodicity
        [InlineData(true, true, true, double.PositiveInfinity, 0.0)]
        [InlineData(true, true, true, double.NegativeInfinity, 0.0)]
        [InlineData(false, true, true, 100.0, 0.0)]       // wrong scene
        [InlineData(true, false, true, 100.0, 0.0)]       // not looping
        [InlineData(true, true, false, 100.0, 0.0)]       // no schedule
        public void MissionWarpReasonIsNonEmptyExactlyWhenTheButtonIsGreyed(
            bool warpScene, bool looping, bool unitBuilt, double nextRelaunchUT, double nowUT)
        {
            // Mirrors the call site: the clock leg is answered BY the enable gate, with the
            // upstream legs pinned true so only its clock + finiteness checks speak.
            bool relaunchAhead = MissionsWindowUI.ShouldEnableWarpToWindow(
                true, true, nextRelaunchUT, nowUT);
            bool actionable = warpScene && MissionsWindowUI.ShouldEnableWarpToWindow(
                looping, unitBuilt, nextRelaunchUT, nowUT);

            string reason = MissionsWindowUI.MissionWarpToDisabledReason(
                warpScene, looping, unitBuilt, relaunchAhead);

            Assert.Equal(!actionable, DisabledHoverEcho.ShouldCarry(actionable, reason));
        }

        // catches: the not-in-flight placeholder and the nothing-flying case sharing one
        // wording - they are fixed by completely different actions.
        [Fact]
        public void MissionWatchSeparatesWrongSceneFromNothingFlying()
        {
            Assert.NotEqual(
                MissionsWindowUI.MissionWatchDisabledReason(false, false),
                MissionsWindowUI.MissionWatchDisabledReason(true, false));
        }
    }

    /// <summary>
    /// The spawn-row warp reason is not just a free function - it is stamped onto every
    /// row by the pure presentation builder, so the row the window draws carries it.
    /// </summary>
    public class SpawnRowDisabledReasonWiringTests
    {
        private static NearbySpawnCandidate Candidate(
            double distance, double relativeSpeed, double endUT)
        {
            return new NearbySpawnCandidate
            {
                vesselName = "Probe",
                recordingIndex = 0,
                distance = distance,
                relativeSpeed = relativeSpeed,
                endUT = endUT,
                willDepart = false
            };
        }

        // catches: the row builder greying the warp button without stamping the matching
        // reason, which is what leaves a player hovering a dead control.
        [Fact]
        public void ADisabledRowAlwaysCarriesAReason()
        {
            var row = SpawnControlPresentation.BuildRowPresentation(
                Candidate(distance: 9000.0, relativeSpeed: 1.0, endUT: 500.0),
                currentUT: 100.0, proximityRadius: 2000.0, maxRelativeSpeed: 50.0);

            Assert.False(row.WarpButtonEnabled);
            Assert.False(string.IsNullOrEmpty(row.WarpButtonDisabledReason));
            Assert.True(DisabledHoverEcho.ShouldCarry(
                row.WarpButtonEnabled, row.WarpButtonDisabledReason));
        }

        // catches: a live row still carrying stale disabled wording.
        [Fact]
        public void AnEnabledRowCarriesNothing()
        {
            var row = SpawnControlPresentation.BuildRowPresentation(
                Candidate(distance: 500.0, relativeSpeed: 1.0, endUT: 500.0),
                currentUT: 100.0, proximityRadius: 2000.0, maxRelativeSpeed: 50.0);

            Assert.True(row.WarpButtonEnabled);
            Assert.Equal(string.Empty, row.WarpButtonDisabledReason);
            Assert.False(DisabledHoverEcho.ShouldCarry(
                row.WarpButtonEnabled, row.WarpButtonDisabledReason));
        }

        // catches: a candidate inside the proximity gates whose pass has already elapsed
        // being reported as a range problem.
        [Fact]
        public void AnElapsedPassIsReportedAsTheClockNotTheRange()
        {
            var row = SpawnControlPresentation.BuildRowPresentation(
                Candidate(distance: 500.0, relativeSpeed: 1.0, endUT: 50.0),
                currentUT: 100.0, proximityRadius: 2000.0, maxRelativeSpeed: 50.0);

            Assert.False(row.WarpButtonEnabled);
            Assert.Contains("already happened", row.WarpButtonDisabledReason);
        }
    }
}
