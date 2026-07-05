using System;
using System.IO;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-10 A4 source gate (the "compare-vs-gate" decide for <see cref="Parsek.MapRender.RenderSegment"/>
    /// seam / generated fields). <c>GeometryParityComparator.CompareSegment</c> DELIBERATELY does NOT
    /// byte-compare <c>RenderSegment.LeadingSeam</c> / <c>TrailingSeam</c> / <c>IsGenerated</c>: the factory
    /// builds null seams (-> <c>SeamKind.None</c>) while the assembler stamps Rigid/FlexibleSoi, so they
    /// diverge by construction (seam re-derivation is a spine-side / Phase-5b concern), and they are
    /// draw-irrelevant TODAY. This gate makes the "draw-irrelevant" claim a CHECKED invariant rather than a
    /// silent assumption, so the comparator omission is guarded.
    ///
    /// <para><b>What it asserts.</b> The two LIVE PIXEL-DRAW paths read ZERO <c>RenderSegment</c> seam /
    /// <c>IsGenerated</c> fields off a spine-emitted segment:
    /// <list type="bullet">
    ///   <item><c>Patches/GhostOrbitLinePatch.cs</c> — the StockConic proto-orbit-line draw.</item>
    ///   <item><c>Display/GhostTrajectoryPolylineRenderer.cs</c> — the TracedPath owned-polyline draw.</item>
    /// </list>
    /// Both draw from <see cref="Recording"/> / <see cref="Orbit"/> / the <c>GhostRenderIntent</c>, which
    /// carries treatment / payload / frame / drive-UT but NOT seams or <c>IsGenerated</c>
    /// (<c>GhostRenderIntent.cs</c>). So the comparator cannot let a DRAW-affecting divergence pass by
    /// skipping those fields.</para>
    ///
    /// <para><b>Why this is the right gate BEFORE Phase 5b.</b> Phase 5b deletes the legacy draw and makes
    /// the descent orbit↔landing G1 seam load-bearing at the draw layer (the
    /// <see cref="Parsek.MapRender.CrossMemberSeamStitcher"/> stamps it). The instant a draw path starts
    /// reading a seam / <c>IsGenerated</c> field off a spine segment, this gate fails and forces the
    /// comparator (and the factory's seam stamping) to be widened at exactly that point — instead of the
    /// omission silently masking a regression.</para>
    ///
    /// <para>Mirrors the <c>FailClosedWiringSourceGateTests</c> source-text approach (strip line comments
    /// + collapse whitespace, then a substring assertion). A substring gate (NOT a repo-wide forbidden
    /// token) is correct here because these fields ARE legitimately read elsewhere: the assembler stamps
    /// them, the <see cref="Parsek.MapRender.CrossMemberSeamStitcher"/> re-stamps the descent seam, the
    /// tracer summarizes them, and the in-game <c>DescentReStitchInGameTest</c> asserts on a sampled seam.
    /// Only the live DRAW files must stay clean.</para>
    /// </summary>
    public class SeamFieldsDrawIrrelevantSourceGateTests
    {
        // The exact member-access tokens a draw path would use to READ a seam / generated field off a
        // RenderSegment / GhostSample.Segment (e.g. `seg.LeadingSeam`, `sample.Segment.IsGenerated`). The
        // field name preceded by a '.' catches the read regardless of the local variable name; the
        // declaring enum/struct names are not flagged (the gate is about READING the field, not the type).
        private static readonly string[] ForbiddenSeamReads =
        {
            ".LeadingSeam",
            ".TrailingSeam",
            ".IsGenerated",
        };

        // The live pixel-draw files, relative to Source/Parsek/.
        [Theory]
        [InlineData("Patches/GhostOrbitLinePatch.cs")]
        [InlineData("Display/GhostTrajectoryPolylineRenderer.cs")]
        public void LiveDrawPath_ReadsNoRenderSegmentSeamOrGeneratedField(string relPath)
        {
            string src = CollapseWhitespace(StripLineComments(ReadParsekSource(relPath)));

            foreach (string token in ForbiddenSeamReads)
            {
                Assert.False(
                    src.Contains(token),
                    string.Format(
                        "Phase-10 A4 gate: live draw path '{0}' now reads a RenderSegment seam / generated " +
                        "field ('{1}'). If Phase 5b (or any other change) has made these fields load-bearing " +
                        "at the DRAW layer, the GeometryParityComparator MUST be widened to byte-compare " +
                        "LeadingSeam/TrailingSeam/IsGenerated (and the factory must stamp seams to match) — " +
                        "do not just delete this gate. See GeometryParityComparator's class header.",
                        relPath, token));
            }
        }

        // SANITY: the comparator's class header documents the deliberate omission + names this gate, so the
        // decision is discoverable from the code (not only from this test). A wording drift that dropped the
        // pointer would make the omission silent again.
        [Fact]
        public void Comparator_DocumentsSeamOmission_AndNamesThisGate()
        {
            string src = ReadParsekSource("MapRender/GeometryParityComparator.cs");
            Assert.Contains("SeamFieldsDrawIrrelevantSourceGateTests", src);
            Assert.Contains("Phase-10 A4", src);
        }

        // ---- helpers (mirrors FailClosedWiringSourceGateTests) ----

        private static string ReadParsekSource(string relPath)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(root, "Source", "Parsek", relPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                path = Path.Combine(root, "Parsek", relPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "Source file not found at " + path);
            return File.ReadAllText(path);
        }

        private static string StripLineComments(string source)
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

        private static string CollapseWhitespace(string source)
        {
            var sb = new StringBuilder(source.Length);
            bool inWs = false;
            foreach (char c in source)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!inWs) { sb.Append(' '); inWs = true; }
                }
                else { sb.Append(c); inWs = false; }
            }
            return sb.ToString();
        }
    }
}
