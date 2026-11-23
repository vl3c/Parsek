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
            int sample = body.IndexOf("BackgroundRecorderInstance.OnBackgroundPhysicsFrame(v)", StringComparison.Ordinal);
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

        private static string PostfixBody()
        {
            string repo = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(
                Path.Combine(repo, "Source", "Parsek", "Patches", "PhysicsFramePatch.cs")));
            int sig = prepared.IndexOf("static void Postfix(", StringComparison.Ordinal);
            Assert.True(sig >= 0);
            return SourceScanText.BraceMatchedBlock(prepared, prepared.IndexOf('{', sig));
        }
    }
}
