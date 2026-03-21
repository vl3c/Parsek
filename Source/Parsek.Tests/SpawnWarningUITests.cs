using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="SpawnWarningUI"/> pure static methods:
    /// warning decision logic, warning text formatting, and chain status text.
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
        //  ShouldShowWarning
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void ShouldShowWarning_WithinRadius_True()
        {
            Assert.True(SpawnWarningUI.ShouldShowWarning(150f, 200f));
            Assert.Contains(logLines, l => l.Contains("[SpawnWarning]") && l.Contains("ShouldShowWarning"));
        }

        [Fact]
        public void ShouldShowWarning_BeyondRadius_False()
        {
            Assert.False(SpawnWarningUI.ShouldShowWarning(250f, 200f));
            Assert.Contains(logLines, l => l.Contains("[SpawnWarning]") && l.Contains("ShouldShowWarning"));
        }

        [Fact]
        public void ShouldShowWarning_ExactRadius_True()
        {
            // At exactly the warning radius, warning should still show
            Assert.True(SpawnWarningUI.ShouldShowWarning(200f, 200f));
        }

        [Fact]
        public void ShouldShowWarning_ZeroDistance_True()
        {
            Assert.True(SpawnWarningUI.ShouldShowWarning(0f, 200f));
        }

        [Fact]
        public void ShouldShowWarning_ZeroRadius_ZeroDistance_True()
        {
            Assert.True(SpawnWarningUI.ShouldShowWarning(0f, 0f));
        }

        // ────────────────────────────────────────────────────────────
        //  FormatWarningText
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void FormatWarningText_NotBlocked_ContainsDistance()
        {
            string text = SpawnWarningUI.FormatWarningText("MyShip", 150f, 18000.0, false);

            Assert.Contains("MyShip", text);
            Assert.Contains("150", text);
            Assert.Contains("spawning", text);
            Assert.DoesNotContain("BLOCKED", text);
            Assert.Contains(logLines, l => l.Contains("[SpawnWarning]") && l.Contains("FormatWarningText"));
        }

        [Fact]
        public void FormatWarningText_Blocked_ContainsBlockedMessage()
        {
            string text = SpawnWarningUI.FormatWarningText("Station", 5f, 18000.0, true);

            Assert.Contains("BLOCKED", text);
            Assert.Contains("Station", text);
            Assert.Contains("move vessel to clear", text);
            Assert.DoesNotContain("spawning", text);
            Assert.Contains(logLines, l => l.Contains("[SpawnWarning]") && l.Contains("FormatWarningText"));
        }

        [Fact]
        public void FormatWarningText_NullName_UsesUnknown()
        {
            string text = SpawnWarningUI.FormatWarningText(null, 100f, 18000.0, false);

            Assert.Contains("(unknown)", text);
            Assert.Contains("spawning", text);
        }

        [Fact]
        public void FormatWarningText_EmptyName_UsesUnknown()
        {
            string text = SpawnWarningUI.FormatWarningText("", 100f, 18000.0, false);

            Assert.Contains("(unknown)", text);
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
