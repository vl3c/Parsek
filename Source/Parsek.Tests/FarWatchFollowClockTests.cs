using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The far-watch follow must read the ghost at the watch camera's clock. A mission
    /// loop-unit member carries no per-recording loop flag, so a per-recording loop read
    /// alone falls back to the raw game UT and the drone is written on the recording's orbit
    /// tail, about 1,000 km from the ghost on screen (WL-1 hold on refly-a-recorded, 2026-11-28).
    /// </summary>
    public class FarWatchFollowClockTests
    {
        private static string Read(params string[] parts)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            return File.ReadAllText(Path.Combine(root, Path.Combine(parts)));
        }

        private static string MethodBody(string src, string signature)
        {
            int start = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, "method not found: " + signature);
            int open = src.IndexOf('{', start);
            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0)
                    return src.Substring(open, i - open + 1);
            }
            throw new InvalidOperationException("unbalanced body: " + signature);
        }

        [Fact]
        public void TickFollow_ResolvesTheGhostThroughTheWatchClock()
        {
            string body = MethodBody(
                Read("Source", "Parsek", "Controllers", "FarWatchController.cs"), "private void TickFollow()");
            Assert.Contains("host.ResolveGhostPlaybackUTForFarWatch(index, rec, ut)", body);
            Assert.DoesNotContain("TryComputeLoopPlaybackUTForWatch", body);
        }

        [Fact]
        public void FarWatchResolver_UsesTheWatchResolverWithTheEngineGhostState()
        {
            string body = MethodBody(
                Read("Source", "Parsek", "Controllers", "ParsekFlight.cs"),
                "internal double ResolveGhostPlaybackUTForFarWatch(");
            Assert.Contains("engine.ghostStates.TryGetValue(index, out state)", body);
            Assert.Contains("watchMode.ResolveWatchPlaybackUT(rec, state, currentUT, index)", body);
        }

        [Fact]
        public void WatchOverlay_MeasuresTheDistanceFromThePlayerShip()
        {
            // During a far watch the active vessel is the drone on the ghost, so the overlay
            // read 0 m (owner, WL-1 hold 2026-11-28); it reads the player's ship instead.
            string body = MethodBody(
                Read("Source", "Parsek", "Controllers", "WatchModeController.cs"),
                "internal void DrawWatchModeOverlay()");
            Assert.Contains("FarWatchPlayerVessel.Resolve()", body);
            Assert.Contains("playerShip.GetWorldPos3D()", body);
            Assert.DoesNotContain("FlightGlobals.ActiveVessel", body);
        }

        [Fact]
        public void WatchResolver_TriesTheMissionSpanClockFirst()
        {
            string body = MethodBody(
                Read("Source", "Parsek", "Controllers", "WatchModeController.cs"),
                "internal double ResolveWatchPlaybackUT(");
            int unit = body.IndexOf("TryResolveUnitMemberPlaybackUTForWatch", StringComparison.Ordinal);
            int loop = body.IndexOf("TryComputeLoopPlaybackUTForWatch", StringComparison.Ordinal);
            Assert.True(unit >= 0 && loop > unit);
        }
    }
}
