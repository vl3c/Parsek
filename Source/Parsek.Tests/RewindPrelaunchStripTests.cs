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
        public void ShouldStripFuturePrelaunch_NonPrelaunch_AlsoStripped()
        {
            // #164: ALL vessel types not in whitelist are stripped (not just PRELAUNCH)
            var quicksavePids = new HashSet<uint> { 100 };

            Assert.True(ParsekScenario.ShouldStripFuturePrelaunch(
                Vessel.Situations.LANDED, 999, quicksavePids));
            Assert.True(ParsekScenario.ShouldStripFuturePrelaunch(
                Vessel.Situations.ORBITING, 999, quicksavePids));
            Assert.True(ParsekScenario.ShouldStripFuturePrelaunch(
                Vessel.Situations.FLYING, 999, quicksavePids));
            Assert.True(ParsekScenario.ShouldStripFuturePrelaunch(
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
            RewindContext.SetQuicksaveVesselPids(new HashSet<uint> { 1, 2, 3 });
            Assert.NotNull(RewindContext.RewindQuicksaveVesselPids);

            RecordingStore.ResetForTesting();

            Assert.Null(RewindContext.RewindQuicksaveVesselPids);
        }

        [Fact]
        public void RewindQuicksaveVesselPids_SetAndRetrieve()
        {
            // Verify the property round-trips correctly
            var pids = new HashSet<uint> { 42, 77, 999 };
            RewindContext.SetQuicksaveVesselPids(pids);

            Assert.Equal(pids, RewindContext.RewindQuicksaveVesselPids);
            Assert.Contains(42u, RewindContext.RewindQuicksaveVesselPids);
            Assert.Contains(77u, RewindContext.RewindQuicksaveVesselPids);
            Assert.Contains(999u, RewindContext.RewindQuicksaveVesselPids);
            Assert.DoesNotContain(100u, RewindContext.RewindQuicksaveVesselPids);
        }

        [Fact]
        public void RewindQuicksaveVesselPids_NullAfterSetToNull()
        {
            RewindContext.SetQuicksaveVesselPids(new HashSet<uint> { 1 });
            RewindContext.SetQuicksaveVesselPids(null);

            Assert.Null(RewindContext.RewindQuicksaveVesselPids);
        }
    }
}
