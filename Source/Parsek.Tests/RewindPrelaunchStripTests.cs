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

        /// <summary>
        /// Coverage cell C-rewind-refly-020-01. The pure predicate
        /// ShouldStripFuturePrelaunch is covered five times over, but the LOOP that
        /// applies it has no cell. It walks backwards precisely so that consecutive
        /// strips do not skip an entry; a forward walk with RemoveAt leaves a future
        /// vessel alive in flightState after the rewind (and returns a short count).
        /// Two CONSECUTIVE strippable entries are what make that visible, so the
        /// whitelist keeps only the last vessel.
        /// </summary>
        [Fact]
        public void StripFuturePrelaunchVessels_MixedList_RemovesUnknownAndCounts()
        {
            var pv1 = MakeProtoVessel("FutureFlag", 1, Vessel.Situations.LANDED);
            var pv2 = MakeProtoVessel("FuturePad", 2, Vessel.Situations.PRELAUNCH);
            var pv3 = MakeProtoVessel("FutureDebris", 3, Vessel.Situations.ORBITING);
            var pv4 = MakeProtoVessel("InQuicksave", 4, Vessel.Situations.ORBITING);
            var protoVessels = new List<ProtoVessel> { pv1, pv2, pv3, pv4 };

            int stripped = ParsekScenario.StripFuturePrelaunchVessels(
                protoVessels, new HashSet<uint> { 4 });

            Assert.Equal(3, stripped);
            Assert.Single(protoVessels);
            Assert.Same(pv4, protoVessels[0]);

            // One log line per stripped vessel, naming the pid that was removed.
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]") && l.Contains("Stripping future vessel 'FutureFlag'") && l.Contains("pid=1"));
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]") && l.Contains("Stripping future vessel 'FuturePad'") && l.Contains("pid=2"));
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]") && l.Contains("Stripping future vessel 'FutureDebris'") && l.Contains("pid=3"));
            Assert.DoesNotContain(logLines, l => l.Contains("InQuicksave"));
        }

        [Fact]
        public void StripFuturePrelaunchVessels_NullArguments_ReturnZeroAndLeaveListAlone()
        {
            var pv = MakeProtoVessel("Future", 7, Vessel.Situations.PRELAUNCH);
            var protoVessels = new List<ProtoVessel> { pv };

            Assert.Equal(0, ParsekScenario.StripFuturePrelaunchVessels(protoVessels, null));
            Assert.Single(protoVessels);
            Assert.Equal(0, ParsekScenario.StripFuturePrelaunchVessels(null, new HashSet<uint> { 7 }));
        }

        // ProtoVessel cannot be constructed outside KSP; the uninitialized-object
        // precedent is SpawnAuditFollowupTests.cs.
        private static ProtoVessel MakeProtoVessel(
            string name, uint persistentId, Vessel.Situations situation)
        {
            var pv = (ProtoVessel)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(ProtoVessel));
            pv.vesselName = name;
            pv.persistentId = persistentId;
            pv.situation = situation;
            return pv;
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
