using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    // Source gates for the FLIGHT LoopUnitSet single-source invariant (the follow-up to the
    // 2026-06-20 13:34 "owner=33 lacks HasDescentTrigger in the engine's LoopUnitSet while the map
    // resolver's has it" suspicion). The investigation found the suspected stale/divergent cache is
    // structurally impossible in FLIGHT: ParsekFlight.DriveMissionLoopUnits is the ONLY build site,
    // it pushes the SAME cachedLoopUnits reference into the engine every frame, and every flight map
    // surface (polyline Driver, orbit-line patch, ParsekUI markers) reads that same object back
    // (CurrentCachedLoopUnits / Engine.CurrentLoopUnits). What the log actually showed was a
    // CONSUMPTION gap: the engine had no descent-trigger code at all until ea8935a1d landed later
    // that day. These gates keep the invariant from silently regressing: no second builder in the
    // engine, no copy on SetLoopUnits, no divergent field for the map readers. The behavioral half
    // (SetLoopUnits stores the reference; engine and map agree on HasDescentTrigger) is
    // DescentTriggerTests.EngineAndMapResolver_ConsumeSameLoopUnitSet_AgreeOnHasDescentTrigger.
    // Pattern: MapPresenceSeamTests (xUnit runs from Source/Parsek.Tests/bin/Debug/net472/ ->
    // 5 ".." segments to the repo root).
    public class LoopUnitSetCoherenceTests
    {
        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", ".."));

        private static string ReadSource(params string[] relativeParts)
        {
            string path = Path.Combine(ProjectRoot, Path.Combine(relativeParts));
            if (!File.Exists(path))
            {
                // Fallback layout (mirrors MapPresenceSeamTests): some checkouts root at Parsek/.
                var trimmed = new string[relativeParts.Length - 1];
                Array.Copy(relativeParts, 1, trimmed, 0, trimmed.Length);
                path = Path.Combine(ProjectRoot, Path.Combine(trimmed));
            }
            Assert.True(File.Exists(path), $"Source file not found at {path}");
            return File.ReadAllText(path);
        }

        [Fact]
        public void ParsekFlight_PushesItsCachedSetIntoTheEngine_AndExposesTheSameField()
        {
            string source = ReadSource("Source", "Parsek", "ParsekFlight.cs");

            // One build site: the signature-gated DriveMissionLoopUnits pushes the cached set into the
            // engine every frame...
            Assert.Contains("engine.SetLoopUnits(cachedLoopUnits);", source);
            // ...and the flight map readers (polyline Driver) get the SAME field back.
            Assert.Contains("internal GhostPlaybackLogic.LoopUnitSet CurrentCachedLoopUnits => cachedLoopUnits;", source);
        }

        [Fact]
        public void Engine_StoresTheReference_AndNeverBuildsItsOwnSet()
        {
            string source = ReadSource("Source", "Parsek", "GhostPlaybackEngine.cs");

            // SetLoopUnits stores the caller's reference (no copy, no re-derivation)...
            Assert.Contains("currentLoopUnits = units ?? GhostPlaybackLogic.LoopUnitSet.Empty;", source);
            Assert.Contains("internal GhostPlaybackLogic.LoopUnitSet CurrentLoopUnits => currentLoopUnits;", source);
            // ...and the engine can never manufacture a divergent set of its own. Comments are stripped
            // first so prose naming the builder (e.g. documenting this very invariant) can't trip the gate.
            Assert.DoesNotContain("MissionLoopUnitBuilder", StripLineComments(source));
        }

        // Removes the trailing // line-comment from each line (string literals in this codebase do not
        // contain "//", so a plain split is sufficient — same shape as MapPresenceSeamTests).
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
        public void FlightMapSurfaces_ReadTheSameSetTheEngineConsumes()
        {
            // The polyline Driver's FLIGHT branch reads the ParsekFlight cached field (the object
            // SetLoopUnits handed the engine the same frame)...
            string renderer = ReadSource("Source", "Parsek", "Display", "GhostTrajectoryPolylineRenderer.cs");
            Assert.Contains("loopUnits = flCtl.CurrentCachedLoopUnits;", renderer);

            // ...and the orbit-line patch reads the engine passthrough of that same object.
            string linePatch = ReadSource("Source", "Parsek", "Patches", "GhostOrbitLinePatch.cs");
            Assert.Contains("ParsekFlight.Instance?.Engine?.CurrentLoopUnits", linePatch);
        }

        [Fact]
        public void DescentHandoffHide_SharedDecision_WiredOnBothSides()
        {
            // Both the map/TS resolver and the FLIGHT engine consume the ONE pure handoff decision, so
            // the icon handoff and the 3D-ghost handoff can never disagree.
            string spanClock = ReadSource("Source", "Parsek", "GhostPlaybackLogic.SpanClock.cs");
            Assert.Contains("internal static bool ShouldHideNonDescentMemberForDescentHandoff(", spanClock);
            Assert.Contains("bool hideForDescentHandoff = ShouldHideNonDescentMemberForDescentHandoff(", spanClock);

            string engine = ReadSource("Source", "Parsek", "GhostPlaybackEngine.cs");
            Assert.Contains("GhostPlaybackLogic.ShouldHideNonDescentMemberForDescentHandoff(", engine);
        }

        [Fact]
        public void EngineHandoffHide_OrderedBetweenDescentBlockAndLoiterGapClamp_AndForcesTheHide()
        {
            // The engine wiring the pure-decision tests cannot cover: the handoff block must (a) run
            // AFTER the descent-member override resolves (it consumes the raw-clock decision + descentMember
            // gate), (b) run BEFORE the loiter-gap clamp (the handoff hide wins over the loiter wrap by
            // flipping decision off Render, which the clamp gates on - the resolver's order), and (c)
            // actually FORCE the hide. A reorder or a dropped assignment keeps every pure test green, so
            // pin the ordering by source position inside UpdateUnitMemberPlayback.
            string engine = ReadSource("Source", "Parsek", "GhostPlaybackEngine.cs");

            int descentOverride = engine.IndexOf(
                "GhostPlaybackLogic.ResolveDescentMemberEngineRender(", StringComparison.Ordinal);
            int handoffDecision = engine.IndexOf(
                "GhostPlaybackLogic.ShouldHideNonDescentMemberForDescentHandoff(", StringComparison.Ordinal);
            int forcedHide = engine.IndexOf(
                "hiddenForDescentHandoff = true;", StringComparison.Ordinal);
            int loiterClamp = engine.IndexOf(
                "GhostPlaybackLogic.ClampTransferMemberHeadToLoiterGap(", StringComparison.Ordinal);

            Assert.True(descentOverride >= 0, "descent-member override missing");
            Assert.True(handoffDecision >= 0, "engine handoff decision call missing");
            Assert.True(forcedHide >= 0, "forced handoff hide assignment missing");
            Assert.True(loiterClamp >= 0, "loiter-gap clamp call missing");

            Assert.True(descentOverride < handoffDecision,
                "the handoff hide must run after the descent-member override");
            Assert.True(handoffDecision < forcedHide,
                "the forced hide belongs to the handoff block");
            Assert.True(forcedHide < loiterClamp,
                "the handoff hide must run before the loiter-gap clamp (hide wins over the wrap)");
        }
    }
}
