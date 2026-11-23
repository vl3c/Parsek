using System;
using System.IO;
using System.Text.RegularExpressions;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A background member of the live tree the player switches to stays a background recording
    /// until the post-switch watch promotes it, so the background recorder keeps sampling it
    /// while it is the active vessel (todo SWITCHED-TO-KERBAL-UNRECORDED-UNTIL-TRIGGER). Before,
    /// nobody did: the patch handed only non-active vessels over, and no active recorder existed.
    /// </summary>
    public class WatchedActiveVesselBackgroundSampleTests
    {
        [Theory]
        [InlineData(false, true, true)]    // switched to, waiting for the trigger
        [InlineData(false, false, false)]  // no watch on this vessel: today's behaviour
        [InlineData(true, true, false)]    // a recorder is attached: it owns the vessel
        [InlineData(true, false, false)]
        public void ActiveVessel_IsSampledInBackgroundOnlyWhileTheWatchWaits(
            bool activeRecorderAttached, bool watchArmedForVessel, bool expected)
        {
            Assert.Equal(expected, PhysicsFramePatch.ShouldSampleActiveVesselInBackground(
                activeRecorderAttached, watchArmedForVessel));
        }

        [Fact]
        public void Postfix_SamplesBeforeTheWatchCanPromote()
        {
            // The frame whose change promotes the vessel is still the background recorder's:
            // promotion seeds no events for it, so a watch that ran first would lose them.
            string body = PostfixBody();
            int sample = body.IndexOf(
                "BackgroundRecorderInstance.OnBackgroundPhysicsFrame(v, isActiveVessel: true)", StringComparison.Ordinal);
            int watch = body.IndexOf("flight.OnPostSwitchAutoRecordPhysicsFrame(v)", StringComparison.Ordinal);
            Assert.True(sample >= 0, "the watched active vessel is not handed to the background recorder");
            Assert.True(watch > sample, "the watch runs before the background sample");
        }

        [Fact]
        public void Postfix_GatesTheSampleOnTheWatchOfThatVessel()
        {
            Assert.Matches(
                @"BackgroundRecorderInstance != null\s*&& ShouldSampleActiveVesselInBackground\(\s*" +
                @"ActiveRecorder != null,\s*flight\.IsPostSwitchAutoRecordArmedForPid\(v\.persistentId\)\)",
                PostfixBody());
        }

        [Fact]
        public void Postfix_LeavesTheNonActiveVesselsOnTheirOwnPath()
        {
            string body = PostfixBody();
            Assert.Contains("bgVessel != null && bgVessel != v", body);
            Assert.Equal(2, Regex.Matches(body, @"\.OnBackgroundPhysicsFrame\(").Count);
        }

        // ---- sampling density: 0 m from itself is not proximity ----

        [Theory]
        [InlineData(false, 100.0, false, double.NaN, 100.0)]  // non-active: the focused vessel's distance
        [InlineData(false, 100.0, true, 5.0, 100.0)]
        [InlineData(true, 0.0, true, 1.5, 1.5)]               // the kerbal beside her ship
        [InlineData(true, 0.0, true, 1800.0, 1800.0)]
        [InlineData(true, 0.0, false, double.NaN, double.NaN)] // a lone visited vessel
        public void HighFidelityDistance_IsTheAnchorsForTheActiveVessel(
            bool isActive, double toFocused, bool hasAnchor, double toAnchor, double expected)
        {
            Assert.Equal(expected, BackgroundRecorder.ResolveHighFidelityDistance(
                isActive, toFocused, hasAnchor, toAnchor));
        }

        [Fact]
        public void LoneVisitedVessel_KeepsTheOrdinaryBackstop()
        {
            // 0 m from itself would force the 3 s backstop down to the 0.2 s minimum.
            double distance = BackgroundRecorder.ResolveHighFidelityDistance(true, 0.0, false, double.NaN);
            bool highFidelity = FlightRecorder.IsHighFidelitySamplingActive(100.0, double.NaN, distance);
            BackgroundRecorder.ResolveBackgroundEffectiveSampleIntervals(
                ProximitySamplingTier.None, highFidelity, ProximityRateSelector.GetSampleInterval(0.0),
                0.2f, 3.0f, new Recording(), out float min, out float max);
            Assert.False(highFidelity);
            Assert.Equal(0.2, (double)min, 3);
            Assert.Equal(3.0, (double)max, 3);
        }

        [Fact]
        public void PhysicsFrame_ReadsTheResolvedDistanceForHighFidelity()
        {
            string body = MethodBody(Path.Combine("Recording", "BackgroundRecorder.cs"),
                "public void OnBackgroundPhysicsFrame(");
            Assert.Matches(
                @"IsBackgroundHighFidelitySamplingActive\(\s*state, ut, " +
                @"ResolveBackgroundHighFidelityDistance\(state, bgVessel, distance, isActiveVessel\)\)", body);
        }

        private static string PostfixBody()
        {
            return MethodBody(Path.Combine("Patches", "PhysicsFramePatch.cs"), "static void Postfix(");
        }

        private static string MethodBody(string relPath, string signature)
        {
            string repo = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(
                Path.Combine(repo, "Source", "Parsek", relPath)));
            int sig = prepared.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(sig >= 0, "signature not found: " + signature);
            return SourceScanText.BraceMatchedBlock(prepared, prepared.IndexOf('{', sig));
        }
    }
}
