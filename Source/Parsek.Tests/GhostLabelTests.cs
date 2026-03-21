using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="SpawnWarningUI.ComputeGhostLabelText"/> and
    /// <see cref="ParsekFlight.ComputeGhostLabelText"/> — ghost label text
    /// computation for floating labels on ghost vessels.
    /// </summary>
    [Collection("Sequential")]
    public class GhostLabelTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostLabelTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ────────────────────────────────────────────────────────────
        //  ComputeGhostLabelText (SpawnWarningUI static method)
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void ComputeGhostLabelText_NormalChain_ContainsVesselNameAndUT()
        {
            string label = SpawnWarningUI.ComputeGhostLabelText("Station Alpha", 18500.0, false, false);

            Assert.Contains("Station Alpha", label);
            Assert.Contains("spawns at UT=18500", label);
            Assert.Contains("\n", label);
            Assert.DoesNotContain("terminated", label);
            Assert.DoesNotContain("blocked", label);
            Assert.Contains(logLines, l => l.Contains("[SpawnWarning]") && l.Contains("ComputeGhostLabelText"));
        }

        [Fact]
        public void ComputeGhostLabelText_Terminated_ContainsTerminated()
        {
            string label = SpawnWarningUI.ComputeGhostLabelText("Station Alpha", 18500.0, true, false);

            Assert.Contains("Station Alpha", label);
            Assert.Contains("terminated", label);
            Assert.DoesNotContain("spawns at UT", label);
            Assert.DoesNotContain("blocked", label);
            Assert.Contains(logLines, l => l.Contains("[SpawnWarning]") && l.Contains("ComputeGhostLabelText"));
        }

        [Fact]
        public void ComputeGhostLabelText_Blocked_ContainsBlocked()
        {
            string label = SpawnWarningUI.ComputeGhostLabelText("Station Alpha", 18500.0, false, true);

            Assert.Contains("Station Alpha", label);
            Assert.Contains("blocked", label);
            Assert.DoesNotContain("spawns at UT", label);
            Assert.DoesNotContain("terminated", label);
            Assert.Contains(logLines, l => l.Contains("[SpawnWarning]") && l.Contains("ComputeGhostLabelText"));
        }

        [Fact]
        public void ComputeGhostLabelText_NullName_Handles()
        {
            string label = SpawnWarningUI.ComputeGhostLabelText(null, 18500.0, false, false);

            Assert.Contains("(unknown)", label);
            Assert.Contains("spawns at UT=18500", label);
        }

        [Fact]
        public void ComputeGhostLabelText_EmptyName_Handles()
        {
            string label = SpawnWarningUI.ComputeGhostLabelText("", 18500.0, false, false);

            Assert.Contains("(unknown)", label);
            Assert.Contains("spawns at UT=18500", label);
        }

        [Fact]
        public void ComputeGhostLabelText_BlockedTakesPriorityOverTerminated()
        {
            // If both blocked and terminated, blocked wins (active user action needed)
            string label = SpawnWarningUI.ComputeGhostLabelText("Station", 18500.0, true, true);

            Assert.Contains("blocked", label);
            Assert.DoesNotContain("terminated", label);
        }

        [Fact]
        public void ComputeGhostLabelText_LargeUT_FormatsCorrectly()
        {
            // Verify large UT values don't produce scientific notation
            string label = SpawnWarningUI.ComputeGhostLabelText("Ship", 1234567.0, false, false);

            Assert.Contains("UT=1234567", label);
        }

        // ────────────────────────────────────────────────────────────
        //  GetChainStatusForRecording (ParsekFlight static method)
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void GetChainStatusForRecording_NoChains_ReturnsNull()
        {
            string status = ParsekFlight.GetChainStatusForRecording(null, null);
            Assert.Null(status);
        }

        [Fact]
        public void GetChainStatusForRecording_EmptyChains_ReturnsNull()
        {
            var chains = new Dictionary<uint, GhostChain>();
            var rec = new Recording { VesselPersistentId = 100 };

            string status = ParsekFlight.GetChainStatusForRecording(chains, rec);
            Assert.Null(status);
        }

        [Fact]
        public void GetChainStatusForRecording_MatchingChain_ReturnsStatus()
        {
            var chain = new GhostChain
            {
                OriginalVesselPid = 100,
                SpawnUT = 18500.0,
                TipRecordingId = "rec-1",
                IsTerminated = false,
                SpawnBlocked = false
            };
            var chains = new Dictionary<uint, GhostChain> { { 100, chain } };
            var rec = new Recording { VesselPersistentId = 100 };

            string status = ParsekFlight.GetChainStatusForRecording(chains, rec);

            Assert.NotNull(status);
            Assert.Contains("Ghosted", status);
            Assert.Contains("spawns at UT=18500", status);
        }

        [Fact]
        public void GetChainStatusForRecording_NoMatchingChain_ReturnsNull()
        {
            var chain = new GhostChain
            {
                OriginalVesselPid = 200,
                SpawnUT = 18500.0,
                TipRecordingId = "rec-1",
                IsTerminated = false,
                SpawnBlocked = false
            };
            var chains = new Dictionary<uint, GhostChain> { { 200, chain } };
            var rec = new Recording { VesselPersistentId = 100 };

            string status = ParsekFlight.GetChainStatusForRecording(chains, rec);
            Assert.Null(status);
        }

        [Fact]
        public void GetChainStatusForRecording_TipRecording_ReturnsChainTipStatus()
        {
            var chain = new GhostChain
            {
                OriginalVesselPid = 200,
                SpawnUT = 18500.0,
                TipRecordingId = "rec-tip",
                IsTerminated = false,
                SpawnBlocked = false
            };
            var chains = new Dictionary<uint, GhostChain> { { 200, chain } };
            var rec = new Recording
            {
                RecordingId = "rec-tip",
                VesselPersistentId = 999 // different PID — this is the recording that spawns the chain tip
            };

            string status = ParsekFlight.GetChainStatusForRecording(chains, rec);

            Assert.NotNull(status);
            Assert.Contains("Chain tip", status);
        }
    }
}
