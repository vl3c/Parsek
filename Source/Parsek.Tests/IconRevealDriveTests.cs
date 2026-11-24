using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Parsek.MapRender;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A ghost's proto icon is shown only where the icon drive put it (todo
    /// MAP-ICON-SHOWN-BEFORE-DRIVEN-2026-11-24). The drive runs per physics step, the line Postfix per
    /// render frame: a hidden icon revealed on a frame with no physics step (a frame rate above the
    /// physics rate, or a paused game) would otherwise be drawn where stock propagated the raw epoch at
    /// the live clock. The live sequence (the real Prefix, stock and the real Postfix inside one frame)
    /// is the in-game <c>IconRevealDriveInGameTest</c>.
    /// </summary>
    [Collection("Sequential")]
    public class IconRevealDriveTests : IDisposable
    {
        private const uint Pid = 70_011u;
        private readonly List<string> logLines = new List<string>();

        public IconRevealDriveTests()
        {
            GhostMapPresence.ResetForTesting();
            ShadowRenderDriver.Reset();
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ShadowRenderDriver.Reset();
            GhostMapPresence.ResetForTesting();
        }

        // The half of the drive Prefix the reveal depends on: its own visible / suppressed decision,
        // written to the suppressed set. (The placement itself needs a live OrbitDriver.)
        private static void DriveAt(double liveUT, double windowStartUT, double windowEndUT)
        {
            GhostOrbitIconDrivePatch.IconDriveDecision d =
                GhostOrbitIconDrivePatch.ResolveIconDriveDecision(
                    liveUT, windowStartUT, windowEndUT, shift: 1_249_866.7, onArc: true,
                    loiterCircleActive: false, loiterCircleEffUT: double.NaN);
            if (d.Suppressed)
                GhostMapPresence.ghostsWithSuppressedIcon.Add(Pid);
            else
                GhostMapPresence.ghostsWithSuppressedIcon.Remove(Pid);
        }

        [Fact]
        public void HiddenIcon_IsDrivenOnTheRevealFrame_AndShown()
        {
            // TP-6 2026-11-24_0531: the polyline owned the leg, the jump ended the hide at frame 11068,
            // and that frame had no physics step.
            GhostMapPresence.ghostsWithSuppressedIcon.Add(Pid);
            int drives = 0;

            bool placed = GhostOrbitLinePatch.DriveHiddenIconForReveal(
                Pid, "director-stockconic-visible",
                () => { drives++; DriveAt(1_268_401.0, 1_266_000.0, 1_270_000.0); },
                frame: 11068, currentUT: 1_268_401.0);

            Assert.True(placed);
            Assert.Equal(1, drives);
            Assert.False(GhostMapPresence.IsIconSuppressed(Pid));
            Assert.Contains(logLines, l => l.Contains("[GhostOrbitIcon]")
                && l.Contains("Icon reveal drove the icon")
                && l.Contains("pid=70011") && l.Contains("frame=11068")
                && l.Contains("branch=director-stockconic-visible")
                && l.Contains("liveUT=1268401.0"));
        }

        [Fact]
        public void DriveThatRefuses_KeepsTheIconHidden_AndTheMarkerDraws()
        {
            GhostMapPresence.ghostsWithSuppressedIcon.Add(Pid);

            bool placed = GhostOrbitLinePatch.DriveHiddenIconForReveal(
                Pid, "visible-body-frame", () => DriveAt(2000.0, 100.0, 1000.0),
                frame: 12, currentUT: 2000.0);

            Assert.False(placed);
            // No gap and no double: the proto icon stays off and the marker decision, which reads the
            // same set, draws.
            Assert.True(GhostMapPresence.ResolveMarkerDrawDecision(
                directorTracedPathActive: false, polylineOwning: false,
                iconSuppressedLegacy: GhostMapPresence.IsIconSuppressed(Pid)));
            Assert.Contains(logLines, l => l.Contains("[GhostOrbitIcon]")
                && l.Contains("Icon reveal refused") && l.Contains("pid=70011")
                && l.Contains("branch=visible-body-frame") && l.Contains("hasDriver=True"));
        }

        [Fact]
        public void NoDriver_KeepsTheIconHidden()
        {
            GhostMapPresence.ghostsWithSuppressedIcon.Add(Pid);

            Assert.False(GhostOrbitLinePatch.DriveHiddenIconForReveal(
                Pid, "director-stockconic-visible", drive: null, frame: 1, currentUT: 1.0));

            Assert.True(GhostMapPresence.IsIconSuppressed(Pid));
            Assert.Contains(logLines, l => l.Contains("Icon reveal refused") && l.Contains("hasDriver=False"));
        }

        [Fact]
        public void IconTheDriveAlreadyPlaced_IsShownWithoutDrivingOrLogging()
        {
            Assert.True(GhostOrbitLinePatch.EnsurePlacedForReveal(
                null, Pid, "director-stockconic-visible", 1.0));
            Assert.Empty(logLines);
        }

        [Fact]
        public void AHoldThatLastsManyFrames_LogsOnce()
        {
            GhostMapPresence.ghostsWithSuppressedIcon.Add(Pid);
            for (int frame = 1; frame <= 50; frame++)
                GhostOrbitLinePatch.DriveHiddenIconForReveal(
                    Pid, "director-stockconic-visible", null, frame, 1.0);

            Assert.Single(logLines.FindAll(l => l.Contains("Icon reveal refused")));
        }

        [Fact]
        public void PausedGame_TheRevealStillPlacesTheIcon()
        {
            // Paused: the clock stands still and no physics step ever comes. The first render frame
            // places the icon, and from then on it is simply shown.
            GhostMapPresence.ghostsWithSuppressedIcon.Add(Pid);
            int drives = 0;
            for (int frame = 100; frame < 110; frame++)
            {
                if (!GhostMapPresence.IsIconSuppressed(Pid))
                {
                    Assert.True(GhostOrbitLinePatch.EnsurePlacedForReveal(
                        null, Pid, "director-stockconic-visible", 500.0));
                    continue;
                }
                Assert.True(GhostOrbitLinePatch.DriveHiddenIconForReveal(
                    Pid, "director-stockconic-visible",
                    () => { drives++; DriveAt(500.0, 100.0, 1000.0); }, frame, 500.0));
            }
            Assert.Equal(1, drives);
        }

        // ---- the Postfix wiring ----

        /// <summary>
        /// The two branches that show the icon of a drive-placed ghost ask the reveal BEFORE they set
        /// drawIcons and let the drive, not the branch, take the pid out of the suppressed set.
        /// </summary>
        [Theory]
        [InlineData("if (appliedBoundsCoverHead", "\"director-stockconic-visible\",\n                    line.active", false)]
        [InlineData("\"stale-segment-awaiting-reseed\"", "\"visible-body-frame\",\n                    line.active", true)]
        public void ShowBranches_AskTheRevealBeforeShowing(string from, string to, bool mayRemoveWithoutBounds)
        {
            string src = StripComments(ReadParsekSource("Patches/GhostOrbitLinePatch.cs"));
            int start = src.IndexOf(from, StringComparison.Ordinal);
            Assert.True(start >= 0, from);
            int end = src.IndexOf(to, start, StringComparison.Ordinal);
            Assert.True(end > start, to);
            string branch = src.Substring(start, end - start);

            int reveal = branch.IndexOf("EnsurePlacedForReveal(", StringComparison.Ordinal);
            int show = branch.LastIndexOf("? OrbitRendererBase.DrawIcons.OBJ", StringComparison.Ordinal);
            Assert.True(reveal >= 0, "the branch no longer asks EnsurePlacedForReveal");
            Assert.True(show > reveal, "the branch sets drawIcons before (or without) the reveal");
            Assert.Contains("iconPlaced", branch.Substring(reveal, show - reveal));

            int removes = CountOf(branch, "ghostsWithSuppressedIcon.Remove(");
            Assert.Equal(mayRemoveWithoutBounds ? 1 : 0, removes);
            if (mayRemoveWithoutBounds)
                Assert.Contains("if (haveAppliedBounds)", branch);
        }

        // ---- the probe's lift-frame check ----

        [Fact]
        public void UndrivenLift_FarFromItsSeed_IsReported()
        {
            // The TP-6 distance: the raw-epoch Mun hyperbola at the live clock.
            Assert.True(MapRenderTrace.IsUndrivenLiftMisplaced(
                suppressionLifted: true, drivenThisFrame: false, hasReference: true,
                justReset: false, misplacedMeters: 448_726_384.0, expectedMotionMeters: 8.0));
        }

        [Theory]
        // A lift the drive placed on this frame is the normal one.
        [InlineData(true, true, true, false, 448_726_384.0, 8.0)]
        // No Director seed: stock places this ghost, there is nothing to compare with.
        [InlineData(true, false, false, false, 448_726_384.0, 8.0)]
        // Not a lift: the jump and off-orbit checks own the frame.
        [InlineData(false, false, true, false, 448_726_384.0, 8.0)]
        // First sample after a scene change or a pid rebuild.
        [InlineData(true, false, true, true, 448_726_384.0, 8.0)]
        // An undriven lift that sits where the seed puts it (a ghost with no loop shift).
        [InlineData(true, false, true, false, 900_000.0, 8.0)]
        // Within the motion one warp step can make.
        [InlineData(true, false, true, false, 30_000_000.0, 10_000_000.0)]
        [InlineData(true, false, true, false, double.NaN, 8.0)]
        [InlineData(true, false, true, false, double.PositiveInfinity, 8.0)]
        public void UndrivenLift_LegitimateOrUnmeasurable_IsNotReported(
            bool lifted, bool driven, bool hasReference, bool justReset, double misplaced, double expected)
        {
            Assert.False(MapRenderTrace.IsUndrivenLiftMisplaced(
                lifted, driven, hasReference, justReset, misplaced, expected));
        }

        [Fact]
        public void UndrivenLift_UnknownExpectedMotion_FallsToTheFloor()
        {
            Assert.True(MapRenderTrace.IsUndrivenLiftMisplaced(
                true, false, true, false, MapRenderTrace.IconJumpFloorMeters + 1.0, double.NaN));
            Assert.False(MapRenderTrace.IsUndrivenLiftMisplaced(
                true, false, true, false, MapRenderTrace.IconJumpFloorMeters, double.NaN));
        }

        [Fact]
        public void Probe_ComparesALiftTheDriveDidNotPlaceThisFrame()
        {
            string src = StripComments(ReadParsekSource("MapRender/MapRenderProbe.cs"));
            int lift = src.IndexOf("bool suppressionLifted =", StringComparison.Ordinal);
            int check = src.IndexOf("CheckUndrivenLift(", lift, StringComparison.Ordinal);
            Assert.True(lift >= 0 && check > lift);
            string gate = src.Substring(lift, check - lift);
            Assert.Contains("if (suppressionLifted", gate);
            // Freshness 0: a record of an earlier frame is not a placement on the lift frame.
            Assert.Contains("!GhostMapPresence.TryGetFreshIconDrivePropagateUT(", gate);
            Assert.Contains("pid, frame, 0, out _", gate);
        }

        [Fact]
        public void DriveRecord_OfTheFrameBefore_IsNotADriveOnTheLiftFrame()
        {
            GhostMapPresence.RecordIconDrivePropagateUT(Pid, 1.0, frame: 11067);
            Assert.False(GhostMapPresence.TryGetFreshIconDrivePropagateUT(Pid, 11068, 0, out _));
            GhostMapPresence.RecordIconDrivePropagateUT(Pid, 1.0, frame: 11068);
            Assert.True(GhostMapPresence.TryGetFreshIconDrivePropagateUT(Pid, 11068, 0, out _));
        }

        [Fact]
        public void StockConicSeedSeam_IsReadBackAndClearedByReset()
        {
            ShadowRenderDriver.SetStockConicSeedForTesting(
                Pid, 50, new OrbitSegment { semiMajorAxis = -512034.0, bodyName = "Mun" }, "Mun");

            Assert.True(ShadowRenderDriver.TryGetFreshStockConicSeed(
                Pid, 51, out OrbitSegment seg, out string body));
            Assert.Equal(-512034.0, seg.semiMajorAxis);
            Assert.Equal("Mun", body);
            Assert.False(ShadowRenderDriver.TryGetFreshStockConicSeed(Pid, 53, out _, out _));

            ShadowRenderDriver.Reset();
            Assert.False(ShadowRenderDriver.TryGetFreshStockConicSeed(Pid, 50, out _, out _));
        }

        // ---- helpers (mirror GhostOrbitLineCascadeDeleteGateTests) ----

        private static int CountOf(string text, string token)
        {
            int count = 0;
            for (int i = text.IndexOf(token, StringComparison.Ordinal); i >= 0;
                i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal))
                count++;
            return count;
        }

        private static string ReadParsekSource(string relPath)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(
                root, "Source", "Parsek", relPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "Source file not found at " + path);
            return File.ReadAllText(path).Replace("\r\n", "\n");
        }

        private static string StripComments(string source)
        {
            var sb = new StringBuilder(source.Length);
            foreach (string line in source.Split('\n'))
            {
                int idx = line.IndexOf("//", StringComparison.Ordinal);
                sb.Append(idx >= 0 ? line.Substring(0, idx) : line).Append('\n');
            }
            return sb.ToString();
        }
    }
}
