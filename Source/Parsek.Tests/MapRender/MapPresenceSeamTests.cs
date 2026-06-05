using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 8d.0 source-gate for the map-presence adapter seam. The per-frame map-presence
    /// (proto-vessel) tick must route through <c>IGhostMapScene.DriveMapPresence</c> instead of the
    /// host calling <c>ParsekPlaybackPolicy.CheckPendingMapVessels</c> directly, and the FLIGHT scene's
    /// <c>MapViewScene.DriveMapPresence</c> override must delegate VERBATIM to that same method.
    ///
    /// <para>The seam is byte-identical today (a pure indirection); these gates lock the wiring so a
    /// later refactor cannot silently re-route the host back to the direct call or change the FLIGHT
    /// override's delegation target. The scene-iterating drive itself is KSP-coupled (needs a live
    /// <c>ParsekPlaybackPolicy</c> + Unity), so it is validated in-game; this is the Unity-free
    /// equivalent, mirroring the <c>ChainSaveLoadTests.ChainStateNotPersistedInScenario</c>
    /// source-gate pattern.</para>
    /// </summary>
    public class MapPresenceSeamTests
    {
        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", ".."));

        private static string ReadSource(params string[] relativeParts)
        {
            string path = Path.Combine(ProjectRoot, Path.Combine(relativeParts));
            if (!File.Exists(path))
            {
                // Fallback layout (mirrors ChainSaveLoadTests): some checkouts root at Parsek/.
                var trimmed = new string[relativeParts.Length - 1];
                Array.Copy(relativeParts, 1, trimmed, 0, trimmed.Length);
                path = Path.Combine(ProjectRoot, Path.Combine(trimmed));
            }
            Assert.True(File.Exists(path), $"Source file not found at {path}");
            return File.ReadAllText(path);
        }

        [Fact]
        public void ParsekFlight_DrivesMapPresence_ThroughScene_NotPolicyDirectly()
        {
            string source = ReadSource("Source", "Parsek", "ParsekFlight.cs");

            // The per-frame presence tick routes through the scene adapter seam...
            Assert.Contains("mapViewScene.DriveMapPresence(", source);
            // ...and the flight scene is injected with the presence driver during init.
            Assert.Contains("mapViewScene.SetPresenceDriver(", source);

            // ...and the host no longer CALLS CheckPendingMapVessels directly. Strip line comments
            // first so the doc references in the seam comments (which legitimately name the method)
            // don't trip the gate; the negative is about an actual call statement, not prose.
            string codeOnly = StripLineComments(source);
            Assert.DoesNotContain("policy.CheckPendingMapVessels(", codeOnly);
        }

        // Removes the trailing // line-comment from each line (string literals in this codebase do
        // not contain "//", so a plain split is sufficient for this source-gate).
        private static string StripLineComments(string source)
        {
            var sb = new System.Text.StringBuilder(source.Length);
            foreach (string line in source.Split('\n'))
            {
                int idx = line.IndexOf("//", StringComparison.Ordinal);
                sb.Append(idx >= 0 ? line.Substring(0, idx) : line);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        [Fact]
        public void MapViewScene_DriveMapPresence_DelegatesToCheckPendingMapVessels()
        {
            string source = ReadSource("Source", "Parsek", "MapRender", "MapViewScene.cs");

            // The FLIGHT override exists and delegates VERBATIM to CheckPendingMapVessels.
            Assert.Contains("public override void DriveMapPresence(", source);
            Assert.Contains("CheckPendingMapVessels(currentUT)", source);
            // The presence driver is injected (the established SetFrameInputs-style wiring).
            Assert.Contains("SetPresenceDriver(", source);
        }

        [Fact]
        public void IGhostMapScene_Declares_DriveMapPresence()
        {
            string source = ReadSource("Source", "Parsek", "MapRender", "IGhostMapScene.cs");
            Assert.Contains("void DriveMapPresence(double currentUT)", source);
        }

        [Fact]
        public void TrackingStationScene_DriveMapPresence_DelegatesToLifecycle()
        {
            string source = ReadSource("Source", "Parsek", "MapRender", "TrackingStationScene.cs");

            // The TS override exists for symmetry and delegates to the TS lifecycle pass.
            Assert.Contains("public override void DriveMapPresence(", source);
            Assert.Contains("UpdateTrackingStationGhostLifecycle(", source);
        }
    }
}
