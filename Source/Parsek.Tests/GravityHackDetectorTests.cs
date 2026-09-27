using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// KSP-SETTINGS-FOLLOWUPS-RECORDING-2026-09-27 item 4 (owner ruling): one Warn when a
    /// recording starts, or is running when the stock Hack Gravity cheat changes, while a
    /// body's gravity is hacked. No behavior change.
    /// </summary>
    [Collection("Sequential")]
    public class GravityHackDetectorTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GravityHackDetectorTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GravityHackDetector.ResetForTesting();
        }

        public void Dispose()
        {
            GravityHackDetector.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Theory]
        [InlineData(1.0, false)]
        [InlineData(1.0000001, false)]
        [InlineData(0.5, true)]
        [InlineData(2.0, true)]
        [InlineData(double.NaN, false)]
        public void IsHackedFactor_IsAnyFactorAwayFromOne(double factor, bool expected)
        {
            Assert.Equal(expected, GravityHackDetector.IsHackedFactor(factor));
        }

        [Theory]
        [InlineData(true, false, 0.5, true)]
        [InlineData(false, false, 0.5, false)]
        [InlineData(true, true, 0.5, false)]
        [InlineData(true, false, 1.0, false)]
        public void ShouldWarn_LiveNotYetWarnedAndHacked(bool live, bool warned, double factor, bool expected)
        {
            Assert.Equal(expected, GravityHackDetector.ShouldWarn(live, warned, factor));
        }

        [Fact]
        public void ReadLiveHackedFactor_Headless_ReadsNotHacked()
        {
            Assert.True(double.IsNaN(GravityHackDetector.ReadLiveHackedFactor()));
            GravityHackDetector.LiveFactorOverrideForTesting = () => 0.25;
            Assert.Equal(0.25, GravityHackDetector.ReadLiveHackedFactor());
        }

        [Fact]
        public void NoteGravityHack_WarnsOncePerRecording()
        {
            var recorder = new FlightRecorder { IsRecording = true };

            Assert.False(recorder.NoteGravityHack("record-start", double.NaN));
            Assert.True(recorder.NoteGravityHack("cheat-toggled", 0.5));
            Assert.False(recorder.NoteGravityHack("cheat-toggled", 3.0));
            Assert.True(recorder.GravityHackWarnedForTesting);

            var warns = logLines.Where(l => l.Contains("[Recorder]")
                && l.Contains("Gravity hack active while recording")).ToList();
            Assert.Single(warns);
            Assert.Contains("trigger=cheat-toggled factor=0.5", warns[0]);
            Assert.Contains("[WARN]", warns[0].ToUpperInvariant());
        }

        [Fact]
        public void NoteGravityHack_NotRecording_StaysSilent()
        {
            var recorder = new FlightRecorder();
            Assert.False(recorder.NoteGravityHack("cheat-toggled", 0.5));
            Assert.DoesNotContain(logLines, l => l.Contains("Gravity hack active"));
        }
    }
}
