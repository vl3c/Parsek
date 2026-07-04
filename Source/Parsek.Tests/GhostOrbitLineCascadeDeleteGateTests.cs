using System;
using System.IO;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 5a (map/TS render overhaul, migration plan section 7) - the grep gate locking the
    /// deletion of the legacy line-visibility decision cascade in
    /// <c>Patches/GhostOrbitLinePatch.cs</c>. The typed PhaseChain spine
    /// (<c>ShadowRenderDriver.RunFrame</c> -> <c>GhostRenderDirector</c>) decides visibility for
    /// every Director-driven ghost; the line Postfix now APPLIES those decisions (the TracedPath
    /// suppress + the StockConic show sourced from <c>IsDirectorDriveActive</c>) and keeps only the
    /// explicitly RETAINED fallbacks (the below-atmosphere / no-bounds icon floor, the polyline
    /// actual-draw ownership hide, the parking-conic loiter hold, and the legacy bounds clamp for
    /// the populations the Director does not drive: skipped re-aim windows and terminal-orbit /
    /// endpoint-tail protos).
    ///
    /// <para><b>Deleted with the cascade (must stay deleted):</b> the FIX-#26 orbit-line grace
    /// machinery - <c>ShouldDeferOrbitLineHide</c>, <c>OrbitLineGraceFrames</c>, the
    /// <c>OffReasonStaleSegment</c> / <c>OffReasonPolylineOwns</c> transient-reason consts, both
    /// grace-defer branches, and the per-pid grace map in <c>GhostMapPresence</c>
    /// (<c>StampOrbitLineGrace</c> / <c>GetOrbitLineGraceUntilFrame</c> /
    /// <c>ghostOrbitLineGraceUntilFrame</c>). It existed to debounce chatter between the legacy
    /// cascade's own transient off reasons; the spine's decisions are freshness-bridged
    /// (<c>SeedFreshnessFrames</c>) and the Director TracedPath suppress was never graced. The
    /// former <c>GhostOrbitLineGraceTests</c> pinned exactly this machinery and was removed with it
    /// (no converted assertions: every test in that file exercised a deleted symbol).</para>
    ///
    /// <para>Source-text gate (comments stripped, so the tombstone notes naming the deleted symbols
    /// do not trip it), mirroring <c>SeamFieldsDrawIrrelevantSourceGateTests</c>. The positive pins
    /// assert the RETAINED mechanisms stay wired - the Phase-4c/8f re-scope: the
    /// <c>ghostsWithSuppressedIcon</c> icon-floor writes, the Director signal reads, and the
    /// polyline-owning stamp the ParsekUI labeled marker shares.</para>
    /// </summary>
    public class GhostOrbitLineCascadeDeleteGateTests
    {
        // Distinctive identifiers of the deleted cascade machinery. Any of these reappearing in the
        // two files below (outside comments) means the cascade is growing back - fail the build.
        private static readonly string[] ForbiddenCascadeSymbols =
        {
            "ShouldDeferOrbitLineHide",
            "OrbitLineGraceFrames",
            "OffReasonStaleSegment",
            "OffReasonPolylineOwns",
            "StampOrbitLineGrace",
            "GetOrbitLineGraceUntilFrame",
            "ghostOrbitLineGraceUntilFrame",
        };

        [Theory]
        [InlineData("Patches/GhostOrbitLinePatch.cs")]
        [InlineData("GhostMapPresence.cs")]
        public void DeletedCascadeSymbols_StayDeleted(string relPath)
        {
            string src = StripComments(ReadParsekSource(relPath));
            foreach (string token in ForbiddenCascadeSymbols)
            {
                Assert.False(
                    src.Contains(token),
                    string.Format(
                        "Phase-5a gate: '{0}' resurrects deleted line-visibility-cascade symbol "
                        + "'{1}'. The spine (ShadowRenderDriver -> GhostRenderDirector) decides line "
                        + "visibility now; do not re-grow the legacy grace/cascade machinery. See "
                        + "docs/dev/plans/map-ts-render-overhaul-migration.md section 7 (5a).",
                        relPath, token));
            }
        }

        [Fact]
        public void SurvivingMechanisms_StayWired()
        {
            // The RETAINED mechanisms the 5a re-scope pinned (deleting any of these regresses a
            // real population - below-atmosphere / off-arc / no-bounds ghosts to a blank icon, or
            // the marker to a stale mesh position):
            string src = StripComments(ReadParsekSource("Patches/GhostOrbitLinePatch.cs"));

            // Spine decision reads: the TracedPath suppress (the single intent-sourced selector since
            // the Phase-5b side-channel delete) + the StockConic show + the no-bounds tracking guard.
            Assert.Contains("IsTracedPathOwnedThisFrame(", src);
            Assert.Contains("IsDirectorDriveActive(", src);
            Assert.Contains("IsDirectorTracking(", src);

            // Icon-floor writes (KEPT permanent fallback, the 8f reassessment): both directions.
            Assert.Contains("ghostsWithSuppressedIcon.Add(", src);
            Assert.Contains("ghostsWithSuppressedIcon.Remove(", src);

            // Polyline-owning stamp: produced only by the polyline-owns branch; read by the
            // post-polyline-release grace here AND by ParsekUI.DrawMapMarkers (trajPos preference).
            Assert.Contains("StampPolylineOwning(", src);
            Assert.Contains("IsPolylineRecentlyOwningGhostPhase(", src);

            // StockConic icon-drive seed apply (the one-source icon+line drive).
            Assert.Contains("SeedAndDriveLive(", src);
        }

        // ---- helpers (mirror SeamFieldsDrawIrrelevantSourceGateTests) ----

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

        // Strip line comments so the in-code tombstone notes (which name the deleted symbols on
        // purpose, as pointers) do not trip the forbidden-token scan.
        private static string StripComments(string source)
        {
            var sb = new StringBuilder(source.Length);
            foreach (string line in source.Split('\n'))
            {
                int idx = line.IndexOf("//", StringComparison.Ordinal);
                sb.Append(idx >= 0 ? line.Substring(0, idx) : line);
                sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
