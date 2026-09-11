using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="SpawnWarningUI"/> pure static methods: chain status text and
    /// the in-world ghost label. The warning-decision and warning-text cells went with
    /// ShouldShowWarning / FormatWarningText, which had no call site (GUI census D2).
    /// </summary>
    [Collection("Sequential")]
    public class SpawnWarningUITests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SpawnWarningUITests()
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
        //  FormatChainStatus
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void FormatChainStatus_ActiveChain_ShowsSpawnUT()
        {
            var chain = new GhostChain
            {
                OriginalVesselPid = 100,
                SpawnUT = 18500.0,
                TipRecordingId = "rec-1",
                IsTerminated = false,
                SpawnBlocked = false
            };

            string status = SpawnWarningUI.FormatChainStatus(chain, "Station Alpha");

            Assert.Contains("Ghosted", status);
            Assert.Contains("spawns at UT=18500", status);
            Assert.DoesNotContain("terminated", status);
            Assert.DoesNotContain("blocked", status);
            Assert.Contains(logLines, l => l.Contains("[SpawnWarning]") && l.Contains("FormatChainStatus"));
        }

        [Fact]
        public void FormatChainStatus_TerminatedChain_ShowsTerminated()
        {
            var chain = new GhostChain
            {
                OriginalVesselPid = 100,
                SpawnUT = 18500.0,
                TipRecordingId = "rec-1",
                IsTerminated = true,
                SpawnBlocked = false
            };

            string status = SpawnWarningUI.FormatChainStatus(chain, "Station Alpha");

            Assert.Contains("terminated", status);
            Assert.DoesNotContain("spawns at UT", status);
            Assert.Contains(logLines, l => l.Contains("[SpawnWarning]") && l.Contains("FormatChainStatus"));
        }

        [Fact]
        public void FormatChainStatus_BlockedChain_ShowsBlocked()
        {
            var chain = new GhostChain
            {
                OriginalVesselPid = 100,
                SpawnUT = 18500.0,
                TipRecordingId = "rec-1",
                IsTerminated = false,
                SpawnBlocked = true
            };

            string status = SpawnWarningUI.FormatChainStatus(chain, "Station Alpha");

            Assert.Contains("blocked", status.ToLowerInvariant());
            Assert.Contains("clearance", status);
            Assert.Contains(logLines, l => l.Contains("[SpawnWarning]") && l.Contains("FormatChainStatus"));
        }

        [Fact]
        public void FormatChainStatus_NullChain_ReturnsNull()
        {
            string status = SpawnWarningUI.FormatChainStatus(null, "Station Alpha");
            Assert.Null(status);
        }

        [Fact]
        public void FormatChainStatus_BlockedTakesPriorityOverTerminated()
        {
            // If both blocked and terminated are set, blocked takes priority
            // (blocked is an active runtime state that the user needs to act on)
            var chain = new GhostChain
            {
                OriginalVesselPid = 100,
                SpawnUT = 18500.0,
                TipRecordingId = "rec-1",
                IsTerminated = true,
                SpawnBlocked = true
            };

            string status = SpawnWarningUI.FormatChainStatus(chain, "Station");

            Assert.Contains("blocked", status.ToLowerInvariant());
        }
    }
}
