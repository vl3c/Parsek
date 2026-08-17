using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The H2 guard on the P9a kerbal-experience handler
    /// (<c>GameStateRecorder.OnVesselRecoveryProcessingForExperience</c>) reads
    /// <c>SuppressCrewEvents</c> so Parsek's OWN programmatic recoveries do not credit
    /// career XP for a recovery the player never performed. That guard is only worth
    /// anything if the recoveries it is meant to catch actually set the flag — and when the
    /// facet shipped, none of the three did, so the guard guarded nothing.
    ///
    /// <para>
    /// Stock <c>VesselRecovery</c> archives every crew member's flight log and fires
    /// <c>onVesselRecoveryProcessing</c> unconditionally on every path, so there is no way to
    /// tell a Parsek housekeeping recovery from a player one at the handler. The
    /// discrimination has to happen at the call site, which is what these cells hold in
    /// place: one behavioural cell on the pure guard, and a source gate over the three
    /// production <c>ShipConstruction.RecoverVesselFromFlight</c> call sites.
    /// </para>
    /// </summary>
    public class ProgrammaticRecoveryCrewSuppressionGateTests
    {
        // ================================================================
        // Behaviour: a suppressed recovery emits no ExperienceGained event.
        // ================================================================

        [Fact]
        public void SuppressedRecovery_SkipsTheExperienceCapture()
        {
            // The handler's only exit before any GameStateEvent is emitted. With
            // SuppressCrewEvents set, a recovery produces no ExperienceGained row.
            Assert.True(GameStateRecorder.ShouldSkipExperienceCapture(
                isReplayingActions: false,
                suppressCrewEvents: true,
                isGhostMapVessel: false,
                isRewinding: false));
        }

        [Fact]
        public void UnsuppressedPlayerRecovery_StillCaptures()
        {
            // The counterweight: the guard must not swallow a real player recovery.
            Assert.False(GameStateRecorder.ShouldSkipExperienceCapture(
                isReplayingActions: false,
                suppressCrewEvents: false,
                isGhostMapVessel: false,
                isRewinding: false));
        }

        [Fact]
        public void EveryOtherGuardStillSkipsIndependently()
        {
            Assert.True(GameStateRecorder.ShouldSkipExperienceCapture(true, false, false, false));
            Assert.True(GameStateRecorder.ShouldSkipExperienceCapture(false, false, true, false));
            Assert.True(GameStateRecorder.ShouldSkipExperienceCapture(false, false, false, true));
        }

        // ================================================================
        // Source gate: the three production call sites stay wrapped.
        // ================================================================

        [Theory]
        [InlineData("ParsekFlight.cs", 2)]
        [InlineData("VesselSpawner.cs", 1)]
        public void EveryProductionRecoverVesselFromFlightCallIsCrewSuppressed(
            string relPath, int expectedCallSites)
        {
            string src = ReadParsekSource(relPath);
            var lines = src.Split('\n');

            int found = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int idx = line.IndexOf("ShipConstruction.RecoverVesselFromFlight", StringComparison.Ordinal);
                if (idx < 0) continue;
                // Skip the token when it only appears inside a comment.
                int commentIdx = line.IndexOf("//", StringComparison.Ordinal);
                if (commentIdx >= 0 && commentIdx < idx) continue;

                found++;

                // Walk back over the preceding lines looking for the guard that opens
                // this call's block. A short window is enough: the guard is written
                // immediately above the call, and stopping at the enclosing statement
                // keeps an unrelated guard further up from counting.
                bool suppressed = false;
                for (int back = i - 1; back >= 0 && back >= i - 6; back--)
                {
                    if (lines[back].Contains("SuppressionGuard.Crew()"))
                    {
                        suppressed = true;
                        break;
                    }
                }

                Assert.True(suppressed,
                    $"{relPath} line {i + 1}: ShipConstruction.RecoverVesselFromFlight must be "
                    + "wrapped in `using (SuppressionGuard.Crew())`. Stock VesselRecovery fires "
                    + "onVesselRecoveryProcessing unconditionally, so an unwrapped Parsek "
                    + "housekeeping recovery credits the crew career XP for a recovery the "
                    + "player never performed.");
            }

            Assert.True(found == expectedCallSites,
                $"{relPath}: expected {expectedCallSites} production "
                + $"ShipConstruction.RecoverVesselFromFlight call site(s), found {found}. "
                + "A new call site needs its own crew suppression and this count updated.");
        }

        private static string ReadParsekSource(string relPath)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(
                root, "Source", "Parsek", relPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                path = Path.Combine(root, "Parsek", relPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "Source file not found at " + path);
            return File.ReadAllText(path);
        }
    }
}
