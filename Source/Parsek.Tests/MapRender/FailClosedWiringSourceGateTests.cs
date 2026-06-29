using System;
using System.IO;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-7 source gates (migration plan §9) locking the ADDITIVE, geometry-neutral, flag-safe contract
    /// of the fail-closed wiring:
    ///
    /// <list type="bullet">
    ///   <item>The fail-closed decision is a CLASSIFICATION concern (PhaseFactory), NOT a spine concern, so
    ///     the three swapped-spine files never reference <c>FailClosedClassifier</c> (mirroring the existing
    ///     <c>SwappedSpine_DoesNotConsumeDeorbitClock_SourceGate</c> discipline — the spine stays clean).</item>
    ///   <item>The live fail-closed trace emit in <c>PhaseFactory</c> is GATED on
    ///     <c>MapRenderTrace.IsEnabled</c> so flag-OFF / tracing-OFF normal play pays nothing and never
    ///     touches the live Unity body-info resolver (keeping the headless factory tests pure).</item>
    /// </list>
    /// </summary>
    public class FailClosedWiringSourceGateTests
    {
        private static string ReadMapRenderSource(string fileName)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(root, "Source", "Parsek", "MapRender", fileName);
            if (!File.Exists(path))
                path = Path.Combine(root, "Parsek", "MapRender", fileName);
            Assert.True(File.Exists(path), $"Source file not found at {path}");
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

        [Theory]
        [InlineData("ShadowRenderDriver.cs")]
        [InlineData("ChainSampler.cs")]
        [InlineData("GhostRenderDirector.cs")]
        public void SwappedSpine_DoesNotReferenceFailClosedClassifier_SourceGate(string spineFile)
        {
            // Fail-closed is a CLASSIFICATION decision (PhaseFactory), not a per-frame spine concern. A
            // future edit that pulled it into the spine would couple the spine to the classifier (the
            // architecture this design separates); these per-file assertions catch that. All three are
            // currently clean. (Doc prose mentioning the term is stripped before the check.)
            string normalized = CollapseWhitespace(StripLineComments(ReadMapRenderSource(spineFile)));
            Assert.DoesNotContain("FailClosedClassifier", normalized);
            Assert.DoesNotContain("EmitFailClosedToFaithful", normalized);
        }

        [Fact]
        public void PhaseFactory_FailClosedTraceEmit_IsGatedOnTracing()
        {
            // The live fail-closed wiring must be behind MapRenderTrace.IsEnabled so normal play pays
            // nothing AND the headless factory tests never reach the live FlightGlobalsBodyInfo resolver
            // (a Unity ECall). The helper early-returns on !MapRenderTrace.IsEnabled before touching the
            // body-info resolver or the emit.
            string src = CollapseWhitespace(StripLineComments(ReadMapRenderSource("PhaseFactory.cs")));
            Assert.Contains("EmitFailClosedDecisionTraceIfEnabled(", src); // the additive call site exists
            Assert.Contains("if (!MapRenderTrace.IsEnabled || traj == null) return;", src);
        }

        [Fact]
        public void PhaseFactory_BuildPhaseChain_ReturnsAssemblerGeometryWindow_Unchanged()
        {
            // GEOMETRY-NEUTRAL: the fail-closed decision must NOT alter the chain's window /
            // faithful-fallback geometry — the PhaseChain is still constructed from the assembler's
            // geometry fields (geometry.WindowStartUT / geometry.WindowEndUT / geometry.IsFaithfulFallback),
            // exactly as before Phase 7. A regression that fed the fail-closed decision into the geometry
            // would change this constructor call.
            string src = CollapseWhitespace(StripLineComments(ReadMapRenderSource("PhaseFactory.cs")));
            Assert.Contains(
                "return new PhaseChain( traj.RecordingId, committedIndex, instanceKey, phases, "
                + "geometry.WindowStartUT, geometry.WindowEndUT, geometry.IsFaithfulFallback);",
                src);
        }
    }
}
