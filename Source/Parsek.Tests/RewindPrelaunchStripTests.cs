using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for bug #129: pad vessel from future persists after rewind.
    /// ShouldStripFuturePrelaunch is the pure decision method extracted from
    /// StripFuturePrelaunchVessels for testability (ProtoVessel can't be
    /// constructed outside KSP). RewindQuicksaveVesselPids property management
    /// tested via RecordingStore static state.
    /// </summary>
    [Collection("Sequential")]
    public class RewindPrelaunchStripTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RewindPrelaunchStripTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        [Fact]
        public void ShouldStripFuturePrelaunch_UnknownPrelaunch_ReturnsTrue()
        {
            // A PRELAUNCH vessel with PID not in quicksave should be stripped
            var quicksavePids = new HashSet<uint> { 100, 200 };

            bool result = ParsekScenario.ShouldStripFuturePrelaunch(
                Vessel.Situations.PRELAUNCH, 999, quicksavePids);

            Assert.True(result);
        }

        [Fact]
        public void ShouldStripFuturePrelaunch_WhitelistedPrelaunch_ReturnsFalse()
        {
            // A PRELAUNCH vessel with PID in quicksave should be kept
            var quicksavePids = new HashSet<uint> { 100, 200 };

            bool result = ParsekScenario.ShouldStripFuturePrelaunch(
                Vessel.Situations.PRELAUNCH, 100, quicksavePids);

            Assert.False(result);
        }

        [Fact]
        public void ShouldStripFuturePrelaunch_NonPrelaunch_ReturnsFalse()
        {
            // A LANDED vessel should never be stripped regardless of PID
            var quicksavePids = new HashSet<uint> { 100 };

            Assert.False(ParsekScenario.ShouldStripFuturePrelaunch(
                Vessel.Situations.LANDED, 999, quicksavePids));
            Assert.False(ParsekScenario.ShouldStripFuturePrelaunch(
                Vessel.Situations.ORBITING, 999, quicksavePids));
            Assert.False(ParsekScenario.ShouldStripFuturePrelaunch(
                Vessel.Situations.FLYING, 999, quicksavePids));
            Assert.False(ParsekScenario.ShouldStripFuturePrelaunch(
                Vessel.Situations.SPLASHED, 999, quicksavePids));
        }

        [Fact]
        public void ShouldStripFuturePrelaunch_NullQuicksavePids_ReturnsFalse()
        {
            // Null whitelist means we can't determine — safe to keep
            bool result = ParsekScenario.ShouldStripFuturePrelaunch(
                Vessel.Situations.PRELAUNCH, 999, null);

            Assert.False(result);
        }

        [Fact]
        public void ShouldStripFuturePrelaunch_EmptyWhitelist_StripsPrelaunch()
        {
            // Empty whitelist means no known-good PRELAUNCH vessels
            var emptyPids = new HashSet<uint>();

            bool result = ParsekScenario.ShouldStripFuturePrelaunch(
                Vessel.Situations.PRELAUNCH, 100, emptyPids);

            Assert.True(result);
        }

        [Fact]
        public void StripFuturePrelaunchVessels_NullInputs_ReturnsZero()
        {
            // Null protoVessels
            Assert.Equal(0, ParsekScenario.StripFuturePrelaunchVessels(
                null, new HashSet<uint> { 1 }));

            // Null quicksavePids
            Assert.Equal(0, ParsekScenario.StripFuturePrelaunchVessels(
                new List<ProtoVessel>(), null));

            // Both null
            Assert.Equal(0, ParsekScenario.StripFuturePrelaunchVessels(null, null));
        }

        [Fact]
        public void RewindQuicksaveVesselPids_ClearedInResetForTesting()
        {
            RecordingStore.RewindQuicksaveVesselPids = new HashSet<uint> { 1, 2, 3 };
            Assert.NotNull(RecordingStore.RewindQuicksaveVesselPids);

            RecordingStore.ResetForTesting();

            Assert.Null(RecordingStore.RewindQuicksaveVesselPids);
        }

        [Fact]
        public void RewindQuicksaveVesselPids_SetAndRetrieve()
        {
            // Verify the property round-trips correctly
            var pids = new HashSet<uint> { 42, 77, 999 };
            RecordingStore.RewindQuicksaveVesselPids = pids;

            Assert.Equal(pids, RecordingStore.RewindQuicksaveVesselPids);
            Assert.Contains(42u, RecordingStore.RewindQuicksaveVesselPids);
            Assert.Contains(77u, RecordingStore.RewindQuicksaveVesselPids);
            Assert.Contains(999u, RecordingStore.RewindQuicksaveVesselPids);
            Assert.DoesNotContain(100u, RecordingStore.RewindQuicksaveVesselPids);
        }

        [Fact]
        public void RewindQuicksaveVesselPids_NullAfterSetToNull()
        {
            RecordingStore.RewindQuicksaveVesselPids = new HashSet<uint> { 1 };
            RecordingStore.RewindQuicksaveVesselPids = null;

            Assert.Null(RecordingStore.RewindQuicksaveVesselPids);
        }
    }
}
