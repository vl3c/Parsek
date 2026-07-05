using System;
using System.IO;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 5b (migration plan section 7, 5b) source gates for the polyline Driver walk re-scope:
    ///
    /// <para><b>Deleted and locked:</b> the walk's DIRECT deorbit-clock consumption. The Driver's I1
    /// deorbit-tail sweep now consumes the clock exclusively through the Phase-6
    /// <c>CrossMemberSeamStitcher</c> absorb APIs (<c>TryResolveTransferDeorbitTailHead</c> +
    /// <c>ResolveDeorbitTailLegHead</c>) - the stitcher owns that clock - so
    /// <c>GhostTrajectoryPolylineRenderer.cs</c> must never again name the raw
    /// <c>DescentTrigger.ResolveTransferLegHeadUT</c> / span-clock
    /// <c>TryResolveTransferDeorbitHeadForMember</c> helpers. Likewise the
    /// <c>GhostRenderReconciler.NoteIntent</c> write was removed from <c>ShadowRenderDriver.cs</c>
    /// (its store lost its last production reader at the Phase-8 unwiring).</para>
    ///
    /// <para><b>Retained and locked (the 5b fence):</b> the walk itself is the single draw host and the
    /// only renderer for the populations the spine does not enumerate (proto-less pid-0 recordings,
    /// StockConic Driver-direct bridge legs, the boundary-overlap secondary, the forward
    /// legs/arcs/bridges), the sole feeder of the KEPT ownership source
    /// (<c>drewNonOrbitalLegRecordings</c>, 8e S3b), and - since 5b - the host of the Tier-C
    /// rigid-seam-tangent evaluation at the owned descent draw. Deleting any of those regresses a real
    /// population. Mirrors <see cref="GhostOrbitLineCascadeDeleteGateTests"/>.</para>
    /// </summary>
    public class PolylineDriverWalkDeleteGateTests
    {
        private const string RendererPath = "Display/GhostTrajectoryPolylineRenderer.cs";
        private const string DriverPath = "MapRender/ShadowRenderDriver.cs";

        [Fact]
        public void DriverWalk_DirectDeorbitClockReads_StayDeleted()
        {
            // The walk must consume the deorbit clock through the stitcher absorb only. A direct
            // DescentTrigger / span-clock read re-couples the draw host to the clock the stitcher owns
            // (the Phase-3<->Phase-6 coupling the source gates forbid).
            string src = StripComments(ReadParsekSource(RendererPath));
            Assert.False(src.Contains("ResolveTransferLegHeadUT"),
                "Phase-5b gate: GhostTrajectoryPolylineRenderer.cs must not name "
                + "DescentTrigger.ResolveTransferLegHeadUT directly - route through "
                + "CrossMemberSeamStitcher.ResolveDeorbitTailLegHead (the stitcher owns the clock).");
            Assert.False(src.Contains("TryResolveTransferDeorbitHeadForMember"),
                "Phase-5b gate: GhostTrajectoryPolylineRenderer.cs must not name "
                + "GhostPlaybackLogic.TryResolveTransferDeorbitHeadForMember directly - route through "
                + "CrossMemberSeamStitcher.TryResolveTransferDeorbitTailHead (the stitcher owns the clock).");
        }

        [Fact]
        public void DriverWalk_ConsumesDeorbitClock_ThroughTheStitcherAbsorb()
        {
            // Positive pin: the I1 deorbit-tail sweep is RETAINED (the spine renders the promoted
            // DescentPhase only from the trigger onward; the Loiter-phase sweep has no spine equivalent)
            // and consumes the clock through the stitcher.
            string src = StripComments(ReadParsekSource(RendererPath));
            Assert.Contains("CrossMemberSeamStitcher.TryResolveTransferDeorbitTailHead(", src);
            Assert.Contains("CrossMemberSeamStitcher.ResolveDeorbitTailLegHead(", src);
        }

        [Fact]
        public void DriverWalk_RetainedFallbackMechanisms_StayWired()
        {
            // The 5b fence: the walk is the SOLE feeder of the kept ownership source (8e S3b) and the
            // dispatch host of the owned TracedPath draw; the Tier-C tangent-seam raise is wired at the
            // owned descent draw. Deleting any of these regresses a real population / the 5b wiring.
            string src = StripComments(ReadParsekSource(RendererPath));
            Assert.Contains("drewNonOrbitalLegRecordings.Add(rec.RecordingId);", src);
            Assert.Contains("TracedPathTreatment.TryDrawOwnedLeg(", src);
            Assert.Contains("EvaluateDescentSeamTangents(", src);
            Assert.Contains("ShouldEvaluateTangentSeamAtDraw(", src);
        }

        [Fact]
        public void ShadowRenderDriver_NoteIntentWrite_StaysDeleted()
        {
            // The reconciler store lost its last production reader at the Phase-8 unwiring; the
            // NoteIntent write was removed at 5b. The reconciler TYPE + its pure predicates stay
            // (exercised by GhostRenderReconcilerTests), but the driver must not repopulate the store.
            string src = StripComments(ReadParsekSource(DriverPath));
            Assert.False(src.Contains("NoteIntent"),
                "Phase-5b gate: ShadowRenderDriver.cs must not write GhostRenderReconciler.NoteIntent - "
                + "the store has no production reader (Phase-8 unwiring; the RenderParityOracle is the "
                + "sole acceptance axis).");
        }

        // ---- helpers (mirror GhostOrbitLineCascadeDeleteGateTests) ----

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

        // Strip line comments so the in-code fence notes (which name the deleted symbols on purpose,
        // as pointers) do not trip the forbidden-token scan.
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
