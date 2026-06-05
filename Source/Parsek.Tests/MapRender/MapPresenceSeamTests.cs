using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 8d.0 / 8d.1 source-gate for the map-presence adapter seam. The per-frame map-presence
    /// (proto-vessel) tick routes through <c>IGhostMapScene.DriveMapPresence</c> instead of the host
    /// calling the policy directly. Phase 8d.1 relocated the former
    /// <c>ParsekPlaybackPolicy.CheckPendingMapVessels</c> body wholesale into
    /// <c>GhostMapPresence.UpdateFlightMapGhostLifecycle</c> (a faithful move, no behavior change, no
    /// gate); the FLIGHT scene's <c>MapViewScene.DriveMapPresence</c> override now calls that directly,
    /// threading the policy's exact per-frame loop units via <c>CurrentLoopUnitsForPresence</c>.
    ///
    /// <para>These gates lock the new structure so a later refactor cannot silently (a) re-route the
    /// host back to a direct policy call, (b) duplicate the presence dicts back into the policy, or
    /// (c) sneak a director-drive gate into the relocated presence body (presence is behavior-identical
    /// across the cutover, so it must never branch on <c>IsDirectorDriveActive</c> /
    /// <c>IsDirectorTracedPathActive</c>). The relocated body is Unity/proto-coupled (needs a live KSP),
    /// so it is validated in-game; this is the Unity-free equivalent, mirroring the
    /// <c>ChainSaveLoadTests.ChainStateNotPersistedInScenario</c> source-gate pattern.</para>
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
        public void MapViewScene_DriveMapPresence_DelegatesToGhostMapPresenceLifecycle()
        {
            string source = ReadSource("Source", "Parsek", "MapRender", "MapViewScene.cs");

            // The FLIGHT override exists and now drives the relocated GhostMapPresence lifecycle
            // directly (Phase 8d.1), threading the policy's exact per-frame loop units.
            Assert.Contains("public override void DriveMapPresence(", source);
            Assert.Contains("GhostMapPresence.UpdateFlightMapGhostLifecycle(", source);
            Assert.Contains("CurrentLoopUnitsForPresence", source);
            // The presence driver (policy, the loop-units source) is still injected.
            Assert.Contains("SetPresenceDriver(", source);

            // ...and the override no longer routes back through the deleted policy method.
            string codeOnly = StripLineComments(source);
            Assert.DoesNotContain("CheckPendingMapVessels(", codeOnly);
        }

        [Fact]
        public void GhostMapPresence_Declares_UpdateFlightMapGhostLifecycle()
        {
            string source = ReadSource("Source", "Parsek", "GhostMapPresence.cs");

            // The relocated per-frame flight presence body lives here as an internal static method.
            Assert.Contains("internal static void UpdateFlightMapGhostLifecycle(", source);
        }

        [Fact]
        public void ParsekPlaybackPolicy_NoLongerOwns_MapPresenceBodyOrDicts()
        {
            string source = ReadSource("Source", "Parsek", "ParsekPlaybackPolicy.cs");
            string codeOnly = StripLineComments(source);

            // The per-frame body is gone (wholesale move, not a copy). The doc/seam comments still
            // legitimately name the method, so strip line comments before the negative.
            Assert.DoesNotContain("void CheckPendingMapVessels(", codeOnly);

            // The 6 presence dicts moved wholesale to GhostMapPresence (proving move, not
            // duplication). Match the field declaration shapes that were unique to those dicts.
            Assert.DoesNotContain("Dictionary<int, PendingMapVessel>", codeOnly);
            Assert.DoesNotContain("Dictionary<int, IPlaybackTrajectory> stateVectorOrbitTrajectories", codeOnly);
            Assert.DoesNotContain("Dictionary<int, int> stateVectorCachedIndices", codeOnly);
            Assert.DoesNotContain("Dictionary<string, int> chainMapOwner", codeOnly);
            Assert.DoesNotContain("Dictionary<int, (string body, double sma, double ecc)> lastMapOrbitByIndex", codeOnly);
            Assert.DoesNotContain("Dictionary<int, string> soiGapStateVectorExpectedBodies", codeOnly);

            // The loop-units pass-through the scene needs is exposed.
            Assert.Contains("CurrentLoopUnitsForPresence", source);
        }

        [Fact]
        public void UpdateFlightMapGhostLifecycle_DoesNotGateOnDirectorDrive()
        {
            string source = ReadSource("Source", "Parsek", "GhostMapPresence.cs");
            // Bound the body region between this method's signature and the next method declaration
            // (PruneTerminalMapRetentionLogKeys is the next member after the relocated body). This
            // avoids brace counting, which the body's string-literal {0}/{idx} placeholders would
            // otherwise unbalance.
            string body = ExtractMethodRegion(
                source,
                "internal static void UpdateFlightMapGhostLifecycle(",
                "private static void PruneTerminalMapRetentionLogKeys(");

            // Presence is behavior-identical across the map-render cutover; the relocated body must
            // never branch on the director-drive / traced-path gate (no mapRenderDirectorDrive branch).
            Assert.DoesNotContain("IsDirectorDriveActive", body);
            Assert.DoesNotContain("IsDirectorTracedPathActive", body);
        }

        // Returns the source slice from <paramref name="startNeedle"/> up to (not including) the next
        // occurrence of <paramref name="endNeedle"/>. Used to bound a method body by the following
        // method's signature, sidestepping brace counting that string-literal braces would break.
        private static string ExtractMethodRegion(string source, string startNeedle, string endNeedle)
        {
            int start = source.IndexOf(startNeedle, StringComparison.Ordinal);
            Assert.True(start >= 0, $"Method signature not found: {startNeedle}");
            int end = source.IndexOf(endNeedle, start, StringComparison.Ordinal);
            Assert.True(end > start, $"Region end signature not found after start: {endNeedle}");
            return source.Substring(start, end - start);
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
