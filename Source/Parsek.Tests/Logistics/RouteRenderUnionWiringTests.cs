using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Phase 3 (render union via append) source-text parity gate. The three host
    /// push seams (<c>ParsekFlight</c> / <c>ParsekKSC</c> /
    /// <c>ParsekTrackingStation</c> <c>DriveMissionLoopUnits</c>) MUST each:
    /// <list type="number">
    ///   <item>build the route-mission list via the shared
    ///   <c>RouteGhostDriverSelector.SelectGhostDrivingBackingMissions</c>; and</item>
    ///   <item>append it to a NEW unioned list seeded from
    ///   <c>MissionStore.Missions</c>; and</item>
    ///   <item>pass that <c>unioned</c> list (not the bare
    ///   <c>MissionStore.Missions</c>) to BOTH
    ///   <c>MissionLoopUnitBuilder.BuildSignature</c> and
    ///   <c>MissionLoopUnitBuilder.Build</c>.</item>
    /// </list>
    /// The runtime selector behavior is covered by
    /// <see cref="RouteGhostDriverSelectorTests"/>; this gate catches a future edit
    /// that drops the union wiring from any one seam (which would silently stop
    /// rendering route ghosts in that scene) — a pure runtime regression the
    /// per-scene MonoBehaviour drive cannot be unit-driven to catch (it depends on
    /// Planetarium / the live Unity lifecycle). Same source-text gate pattern as
    /// <c>RouteStoreScenarioIntegrationTests.Scenario_OnSaveAndOnLoad_InvokeRouteStoreCodec</c>.
    /// </summary>
    public class RouteRenderUnionWiringTests
    {
        private static string ResolveSourceFile(string fileName)
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot, "Source", "Parsek", fileName);

            // Fallback path if the test runs from an unusual working dir.
            if (!File.Exists(path))
                path = Path.Combine(projectRoot, "Parsek", fileName);

            Assert.True(File.Exists(path), $"{fileName} not found at {path}");
            return File.ReadAllText(path);
        }

        // The single block of source we care about in each seam: the BRACE-MATCHED body of the
        // private DriveMissionLoopUnits method, taken from a SANITIZED copy of the file
        // (comments blanked, string-literal contents masked, length preserved). Brace-matched
        // rather than a fixed-length slice: a character budget either stops short of a long body
        // (so the negative DoesNotMatch arms never see its tail) or overruns into the following
        // method. Sanitized so neither a comment nor a log literal naming a call satisfies a pin.
        private static string ExtractDriveMissionLoopUnitsBody(string source)
        {
            const string Decl =
                "private void DriveMissionLoopUnits(IReadOnlyList<Recording> committed)";

            string sanitized = SourceScanText.StripCommentsAndMaskLiterals(source);
            int decl = sanitized.IndexOf(Decl, StringComparison.Ordinal);
            Assert.True(decl >= 0,
                "DriveMissionLoopUnits(IReadOnlyList<Recording> committed) declaration not found");
            Assert.True(sanitized.IndexOf(Decl, decl + 1, StringComparison.Ordinal) < 0,
                "DriveMissionLoopUnits(IReadOnlyList<Recording> committed) declaration is not unique");

            int open = sanitized.IndexOf('{', decl + Decl.Length);
            Assert.True(open >= 0, "no body brace after the DriveMissionLoopUnits declaration");

            int depth = 0;
            for (int i = open; i < sanitized.Length; i++)
            {
                if (sanitized[i] == '{') depth++;
                else if (sanitized[i] == '}')
                {
                    depth--;
                    if (depth == 0) return sanitized.Substring(open, i - open + 1);
                }
            }

            Assert.True(false, "unbalanced braces walking the DriveMissionLoopUnits body");
            return null;
        }

        [Theory]
        [InlineData("ParsekFlight.cs")]
        [InlineData("ParsekKSC.cs")]
        [InlineData("ParsekTrackingStation.cs")]
        public void Seam_BuildsRouteMissionsViaSharedSelector(string fileName)
        {
            string body = ExtractDriveMissionLoopUnitsBody(ResolveSourceFile(fileName));

            Assert.Contains(
                "RouteGhostDriverSelector.SelectGhostDrivingBackingMissions(",
                body);
            // Fed the committed route store surface, not an arbitrary list.
            Assert.Contains("RouteStore.CommittedRoutes", body);
        }

        [Theory]
        [InlineData("ParsekFlight.cs")]
        [InlineData("ParsekKSC.cs")]
        [InlineData("ParsekTrackingStation.cs")]
        public void Seam_UnionsRouteMissionsOntoMissionStoreList(string fileName)
        {
            string body = ExtractDriveMissionLoopUnitsBody(ResolveSourceFile(fileName));

            // New unioned list seeded from MissionStore.Missions, then the route
            // missions appended (IReadOnlyList cannot be appended in place).
            Assert.Contains("new List<Mission>(MissionStore.Missions)", body);
            Assert.Contains("unioned.AddRange(routeMissions)", body);
        }

        [Theory]
        [InlineData("ParsekFlight.cs")]
        [InlineData("ParsekKSC.cs")]
        [InlineData("ParsekTrackingStation.cs")]
        public void Seam_PassesUnionedListToBuildSignatureAndBuild(string fileName)
        {
            string body = ExtractDriveMissionLoopUnitsBody(ResolveSourceFile(fileName));

            // BuildSignature is called with the unioned list as its first argument.
            Assert.Matches(
                new Regex(@"BuildSignature\(\s*unioned\s*,", RegexOptions.Singleline),
                body);
            // Build is called with the unioned list as its first argument.
            Assert.Matches(
                new Regex(@"MissionLoopUnitBuilder\.Build\(\s*unioned\s*,", RegexOptions.Singleline),
                body);
            // And the bare MissionStore.Missions is NOT passed straight into either
            // builder (it must go through the unioned list first), so the route side
            // always participates in the cache-rebuild signature. Whitespace-tolerant.
            Assert.DoesNotMatch(
                new Regex(@"BuildSignature\(\s*MissionStore\.Missions", RegexOptions.Singleline),
                body);
            Assert.DoesNotMatch(
                new Regex(@"\bBuild\(\s*MissionStore\.Missions", RegexOptions.Singleline),
                body);
        }
    }
}
