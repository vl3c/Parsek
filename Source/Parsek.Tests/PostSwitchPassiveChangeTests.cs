using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Todo POST-SWITCH-PASSIVE-MOTION-TRIGGERS (design 5.4 rule 5): after a switch to an outsider
    /// the watcher counted a passive tumble (or the co-rotating world frame of a low orbit) past
    /// 3 degrees, and an SOI crossing while coasting, as the player changing the vessel, and
    /// started a recording for it. An outsider's attitude now counts only from the first rotation
    /// the player commands, and its orbit baseline moves with an SOI change. A tracked background
    /// member keeps promoting on any change.
    /// </summary>
    public class PostSwitchPassiveChangeTests
    {
        private const ParsekFlightPostSwitchAutoRecord.PostSwitchAttitudeBaselineStep Measure =
            ParsekFlightPostSwitchAutoRecord.PostSwitchAttitudeBaselineStep.Measure;
        private const ParsekFlightPostSwitchAutoRecord.PostSwitchAttitudeBaselineStep FollowPassive =
            ParsekFlightPostSwitchAutoRecord.PostSwitchAttitudeBaselineStep.FollowPassive;
        private const ParsekFlightPostSwitchAutoRecord.PostSwitchAttitudeBaselineStep LatchCommand =
            ParsekFlightPostSwitchAutoRecord.PostSwitchAttitudeBaselineStep.LatchCommand;

        // catches: a passively tumbling outsider measured against a fixed baseline (the bug):
        // with no rotation command its baseline follows it.
        [Fact]
        public void Outsider_NoCommand_FollowsPassiveMotion()
        {
            Assert.Equal(FollowPassive, ParsekFlightPostSwitchAutoRecord.DecidePostSwitchAttitudeBaselineStep(
                trackedInActiveTree: false, rotationCommandSeen: false, rotationCommandNow: false));
        }

        // catches: real player input no longer triggering: the first command latches, and from
        // then on the attitude is measured.
        [Fact]
        public void Outsider_FirstCommand_Latches_ThenMeasures()
        {
            Assert.Equal(LatchCommand, ParsekFlightPostSwitchAutoRecord.DecidePostSwitchAttitudeBaselineStep(
                false, false, true));
            Assert.Equal(Measure, ParsekFlightPostSwitchAutoRecord.DecidePostSwitchAttitudeBaselineStep(
                false, true, false));
        }

        // catches: the fix reaching tracked background members (GS-11's tumbling booster is
        // promoted on its attitude; it is already part of the recording).
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TrackedMember_AlwaysMeasures(bool commandNow)
        {
            Assert.Equal(Measure, ParsekFlightPostSwitchAutoRecord.DecidePostSwitchAttitudeBaselineStep(
                true, false, commandNow));
        }

        // sasAtBaseline, modeAtBaseline, sasNow, modeNow, input pyr, ctrl pyr, expected
        [Theory]
        // nothing commanded, SAS off, a tumble shows no control input
        [InlineData(false, 0, false, 0, 0f, 0f, 0f, 0f, 0f, 0f, false)]
        // SAS on and holding: its own corrections on the control state are not a command
        [InlineData(true, 0, true, 0, 0f, 0f, 0f, 0.4f, -0.2f, 0.1f, false)]
        // the player's keys
        [InlineData(false, 0, false, 0, 1f, 0f, 0f, 1f, 0f, 0f, true)]
        [InlineData(true, 0, true, 0, 0f, 0f, -1f, 0f, 0f, -1f, true)]
        // SAS switched on (it damps the tumble) or off
        [InlineData(false, 0, true, 0, 0f, 0f, 0f, 0f, 0f, 0f, true)]
        [InlineData(true, 0, false, 0, 0f, 0f, 0f, 0f, 0f, 0f, true)]
        // another SAS mode selected (prograde, ...)
        [InlineData(true, 0, true, 1, 0f, 0f, 0f, 0f, 0f, 0f, true)]
        // a mode index change with SAS off is no command
        [InlineData(false, 0, false, 3, 0f, 0f, 0f, 0f, 0f, 0f, false)]
        // SAS off and an autopilot (kRPC, a mod) rotating through the control state
        [InlineData(false, 0, false, 0, 0f, 0f, 0f, 0f, 0.3f, 0f, true)]
        // stick noise below the epsilon
        [InlineData(false, 0, false, 0, 0.005f, 0f, 0f, 0f, 0f, 0.005f, false)]
        public void IsPlayerRotationCommand_Matrix(
            bool sasAtBaseline, int modeAtBaseline, bool sasNow, int modeNow,
            float inPitch, float inYaw, float inRoll,
            float ctrlPitch, float ctrlYaw, float ctrlRoll,
            bool expected)
        {
            Assert.Equal(expected, ParsekFlightPostSwitchAutoRecord.IsPlayerRotationCommand(
                sasAtBaseline, modeAtBaseline, sasNow, modeNow,
                inPitch, inYaw, inRoll, ctrlPitch, ctrlYaw, ctrlRoll));
        }

        private static ParsekFlight.PostSwitchOrbitSnapshot Orbit(string body, double sma)
            => new ParsekFlight.PostSwitchOrbitSnapshot
            {
                IsValid = true, BodyName = body, SemiMajorAxis = sma, Eccentricity = 0.1,
                Inclination = 1, LongitudeOfAscendingNode = 2, ArgumentOfPeriapsis = 3,
            };

        // catches: an SOI crossing while coasting read as the player's orbit change.
        [Fact]
        public void SoiChange_IsPassiveTransition_SameBodyIsNot()
        {
            Assert.True(ParsekFlightPostSwitchAutoRecord.IsPassiveSoiTransition(
                Orbit("Kerbin", 700000), Orbit("Mun", 300000)));
            Assert.False(ParsekFlightPostSwitchAutoRecord.IsPassiveSoiTransition(
                Orbit("Kerbin", 700000), Orbit("Kerbin", 800000)));
            Assert.False(ParsekFlightPostSwitchAutoRecord.IsPassiveSoiTransition(
                default(ParsekFlight.PostSwitchOrbitSnapshot), Orbit("Mun", 300000)));
            // after the baseline moves, a burn on the new body is still an orbit change
            Assert.True(ParsekFlightPostSwitchAutoRecord.HasMeaningfulOrbitChange(
                Orbit("Mun", 300000), Orbit("Mun", 300100)));
        }

        // catches: the passive rules not wired, or wired after the measurement they guard, or
        // reaching tracked members.
        [Fact]
        public void TheWatcherAppliesThePassiveRulesBeforeMeasuring()
        {
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string src = File.ReadAllText(Path.Combine(root, "Source", "Parsek", "Controllers",
                "ParsekFlightPostSwitchAutoRecord.cs"));

            string att = Body(src, "private static bool EvaluatePostSwitchAttitudeChange(",
                "private static bool FollowPassiveOutsiderAttitude(");
            int follow = att.IndexOf("FollowPassiveOutsiderAttitude(", StringComparison.Ordinal);
            int measure = att.IndexOf("HasMeaningfulAttitudeChange(", StringComparison.Ordinal);
            Assert.True(follow > 0 && measure > follow);

            string fol = Body(src, "private static bool FollowPassiveOutsiderAttitude(",
                "internal enum PostSwitchAttitudeBaselineStep");
            Assert.Contains("DecidePostSwitchAttitudeBaselineStep(", fol);
            Assert.Contains("state.TrackedInActiveTree", fol);

            string orb = Body(src, "private static bool EvaluatePostSwitchOrbitChange(",
                "private void PrepareActiveTreeForFreshPostSwitchRecording(");
            int tracked = orb.IndexOf("if (!state.TrackedInActiveTree)", StringComparison.Ordinal);
            int soi = orb.IndexOf("IsPassiveSoiTransition(", StringComparison.Ordinal);
            int rebase = orb.IndexOf("state.BaselineOrbit = current;", StringComparison.Ordinal);
            int change = orb.IndexOf("HasMeaningfulOrbitChange(", StringComparison.Ordinal);
            Assert.True(tracked > 0 && soi > tracked && rebase > soi && change > rebase);

            string baseline = Body(src, "private bool CapturePostSwitchAutoRecordBaseline(",
                "private static HashSet<string> CapturePostSwitchPartStateTokens(");
            Assert.Contains("state.RotationCommandSeen = false;", baseline);
            Assert.Contains("state.BaselineSasEnabled = ReadSasEnabled(v);", baseline);
        }

        private static string Body(string src, string startMarker, string endMarker)
        {
            int start = src.IndexOf(startMarker, StringComparison.Ordinal);
            Assert.True(start >= 0, startMarker);
            int end = src.IndexOf(endMarker, start, StringComparison.Ordinal);
            Assert.True(end > start, endMarker);
            return src.Substring(start, end - start);
        }
    }
}
