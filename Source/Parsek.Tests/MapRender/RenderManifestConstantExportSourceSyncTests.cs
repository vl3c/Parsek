using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests.MapRender
{
    /// <summary>
    /// M-A7 Layer 2 source-sync pin. The manifest's <c>CONSTANTS</c> block is TRANSPORT; the Python
    /// verifier's ratified table is the AUTHORITY, and it is keyed by these exact C# code names. So a
    /// renamed / retired / re-valued constant must red HERE, at build time, instead of silently
    /// desynchronizing the verifier on the next nightly.
    ///
    /// <para>Three legs, mirroring the <c>SeamFieldsDrawIrrelevantSourceGateTests</c> /
    /// <c>IsolatedBatchDispatchWiringTests</c> source-text discipline:</para>
    /// <list type="number">
    ///   <item>the exported NAME set is exactly the pinned set (order included);</item>
    ///   <item>each exported VALUE equals the live constant, and the declaring source file really
    ///     declares an <c>internal const</c> of that name;</item>
    ///   <item>an anti-vacuity floor, so a future refactor cannot pass this cell by exporting nothing.</item>
    /// </list>
    /// </summary>
    public class RenderManifestConstantExportSourceSyncTests
    {
        // (exported name, declaring source file relative to Source/Parsek/, declared member name)
        private static readonly string[][] PinnedConstants =
        {
            new[] { "PhaseSeamClassifier.DefaultTangentToleranceRadians",
                    "MapRender/PhaseSeamClassifier.cs", "DefaultTangentToleranceRadians" },
            new[] { "CrossMemberSeamStitcher.TangentToleranceRadians",
                    "MapRender/CrossMemberSeamStitcher.cs", "TangentToleranceRadians" },
            new[] { "SeamEndpointOracle.DefaultRatioTolerance",
                    "MapRender/SeamEndpointOracle.cs", "DefaultRatioTolerance" },
            new[] { "GhostTrajectoryPolylineRenderer.BridgeMergeSampleCount",
                    "Display/GhostTrajectoryPolylineRenderer.cs", "BridgeMergeSampleCount" },
            new[] { "GhostTrajectoryPolylineRenderer.BridgeMaxAngleRadians",
                    "Display/GhostTrajectoryPolylineRenderer.cs", "BridgeMaxAngleRadians" },
            new[] { "GhostTrajectoryPolylineRenderer.BridgeMinAngleRadians",
                    "Display/GhostTrajectoryPolylineRenderer.cs", "BridgeMinAngleRadians" },
            new[] { "GhostTrajectoryPolylineRenderer.BridgeChordMinAngleRadians",
                    "Display/GhostTrajectoryPolylineRenderer.cs", "BridgeChordMinAngleRadians" },
            new[] { "GhostTrajectoryPolylineRenderer.BridgeMaxSeamGapSeconds",
                    "Display/GhostTrajectoryPolylineRenderer.cs", "BridgeMaxSeamGapSeconds" },
            new[] { "GhostTrajectoryPolylineRenderer.BridgeSeamSharedBoundaryToleranceSeconds",
                    "Display/GhostTrajectoryPolylineRenderer.cs", "BridgeSeamSharedBoundaryToleranceSeconds" },
            new[] { "GhostTrajectoryPolylineRenderer.AnchorMaxResidualKm",
                    "Display/GhostTrajectoryPolylineRenderer.cs", "AnchorMaxResidualKm" },
            new[] { "GhostTrajectoryPolylineRenderer.AnchorMaxRelResidual",
                    "Display/GhostTrajectoryPolylineRenderer.cs", "AnchorMaxRelResidual" },
            new[] { "ShadowRenderDriver.SeedFreshnessFrames",
                    "MapRender/ShadowRenderDriver.cs", "SeedFreshnessFrames" },
            new[] { "GhostOrbitLinePatch.PolylineReleaseGraceSeconds",
                    "Patches/GhostOrbitLinePatch.cs", "PolylineReleaseGraceSeconds" },
            new[] { "GhostTrajectoryPolylineRenderer.TangentSeamConicSampleDtSeconds",
                    "Display/GhostTrajectoryPolylineRenderer.cs", "TangentSeamConicSampleDtSeconds" },
            new[] { "DescentTrigger.DefaultSeamEpsSeconds",
                    "Reaim/DescentTrigger.cs", "DefaultSeamEpsSeconds" },
            new[] { "ReaimLoiterCompressor.DefaultKeepRevs",
                    "Reaim/ReaimLoiterCompressor.cs", "DefaultKeepRevs" },
            new[] { "ReaimLoiterCompressor.DefaultAStepRelThreshold",
                    "Reaim/ReaimLoiterCompressor.cs", "DefaultAStepRelThreshold" },
            new[] { "ReaimLoiterCompressor.DefaultContiguityEpsilonSeconds",
                    "Reaim/ReaimLoiterCompressor.cs", "DefaultContiguityEpsilonSeconds" },
            new[] { "ReaimLoiterCompressor.DefaultSameOrbitRelThreshold",
                    "Reaim/ReaimLoiterCompressor.cs", "DefaultSameOrbitRelThreshold" },
            new[] { "DestinationArrivalSolver.MaxJointHoldWholePeriods",
                    "Reaim/DestinationArrivalSolver.cs", "MaxJointHoldWholePeriods" },
        };

        // The ratified values, restated here so a one-line C# retune reds instead of silently
        // re-tuning every armed gate downstream (the oracle-independence rule: the subject may not
        // define its own gate). Keep in step with harness/lib/rendercompose.py RATIFIED_TOLERANCES.
        private static readonly Dictionary<string, double> RatifiedValues = new Dictionary<string, double>
        {
            { "PhaseSeamClassifier.DefaultTangentToleranceRadians", 0.1 },
            { "CrossMemberSeamStitcher.TangentToleranceRadians", 0.1 },
            { "SeamEndpointOracle.DefaultRatioTolerance", 1.005 },
            { "GhostTrajectoryPolylineRenderer.BridgeMergeSampleCount", 60.0 },
            { "GhostTrajectoryPolylineRenderer.BridgeMaxAngleRadians", 0.7853981633974483 },
            { "GhostTrajectoryPolylineRenderer.BridgeMinAngleRadians", 0.08726646259971647 },
            { "GhostTrajectoryPolylineRenderer.BridgeChordMinAngleRadians", 0.008726646259971648 },
            { "GhostTrajectoryPolylineRenderer.BridgeMaxSeamGapSeconds", 120.0 },
            { "GhostTrajectoryPolylineRenderer.BridgeSeamSharedBoundaryToleranceSeconds", 1.0 },
            { "GhostTrajectoryPolylineRenderer.AnchorMaxResidualKm", 50.0 },
            { "GhostTrajectoryPolylineRenderer.AnchorMaxRelResidual", 0.05 },
            { "ShadowRenderDriver.SeedFreshnessFrames", 2.0 },
            { "GhostOrbitLinePatch.PolylineReleaseGraceSeconds", 1.5 },
            { "GhostTrajectoryPolylineRenderer.TangentSeamConicSampleDtSeconds", 1.0 },
            { "DescentTrigger.DefaultSeamEpsSeconds", 1.0 },
            { "ReaimLoiterCompressor.DefaultKeepRevs", 1.0 },
            { "ReaimLoiterCompressor.DefaultAStepRelThreshold", 0.05 },
            { "ReaimLoiterCompressor.DefaultContiguityEpsilonSeconds", 1.0 },
            { "ReaimLoiterCompressor.DefaultSameOrbitRelThreshold", 0.001 },
            { "DestinationArrivalSolver.MaxJointHoldWholePeriods", 64.0 },
        };

        [Fact]
        public void ExportedConstantNames_MatchThePinnedSetExactly()
        {
            IReadOnlyList<RenderCompositionManifest.ExportedConstant> exported =
                RenderCompositionManifest.Constants;

            Assert.Equal(PinnedConstants.Length, exported.Count);
            for (int i = 0; i < PinnedConstants.Length; i++)
            {
                Assert.True(
                    string.Equals(PinnedConstants[i][0], exported[i].Name, StringComparison.Ordinal),
                    string.Format(CultureInfo.InvariantCulture,
                        "Exported constant #{0} is '{1}' but the pin expects '{2}'. The Python "
                        + "verifier's RATIFIED_TOLERANCES table is keyed by these names - update BOTH "
                        + "sides deliberately, with a citation.",
                        i, exported[i].Name, PinnedConstants[i][0]));
            }
        }

        [Fact]
        public void ExportedConstantValues_MatchTheRatifiedTable()
        {
            foreach (RenderCompositionManifest.ExportedConstant c in RenderCompositionManifest.Constants)
            {
                Assert.True(RatifiedValues.TryGetValue(c.Name, out double ratified),
                    "No ratified value pinned for exported constant '" + c.Name + "'.");
                Assert.True(Math.Abs(c.Value - ratified) < 1e-12,
                    string.Format(CultureInfo.InvariantCulture,
                        "Exported constant '{0}' is {1} but the ratified table says {2}. A tolerance "
                        + "retune must be a DELIBERATE, cited change on both sides (C# + "
                        + "harness/lib/rendercompose.py), never a silent re-tuning of every armed gate.",
                        c.Name, c.Value, ratified));
            }
        }

        [Fact]
        public void EachExportedConstant_IsDeclaredInternalConstInItsOwningSourceFile()
        {
            for (int i = 0; i < PinnedConstants.Length; i++)
            {
                string relPath = PinnedConstants[i][1];
                string member = PinnedConstants[i][2];
                string src = ReadParsekSource(relPath);
                var re = new Regex(
                    @"internal\s+const\s+[A-Za-z0-9_\.]+\s+" + Regex.Escape(member) + @"\s*=",
                    RegexOptions.CultureInvariant);
                Assert.True(re.IsMatch(src),
                    "Expected 'internal const ... " + member + " =' in " + relPath
                    + " (the render-composition manifest exports it by name; a rename or a "
                    + "visibility drop breaks the Python verifier's ratified table).");
            }
        }

        [Fact]
        public void ExportedConstantSet_HasAnAntiVacuityFloor()
        {
            Assert.True(RenderCompositionManifest.Constants.Count >= 14,
                "The CONSTANTS block must carry at least the 14 catalog numbers the RC rules cite; "
                + "found " + RenderCompositionManifest.Constants.Count + ".");
        }

        [Fact]
        public void ConstantsBlock_IsSerializedIntoTheManifestHeader()
        {
            var m = new RenderCompositionManifest();
            ConfigNode root = m.BuildFileNode(
                new RenderCompositionManifest.ManifestHeader(
                    1.0, "verb", "FLIGHT", "s", true, false, false))
                .GetNode(RenderCompositionManifest.RootNodeName);
            ConfigNode consts = root.GetNode("CONSTANTS");
            Assert.NotNull(consts);
            Assert.Equal(RenderCompositionManifest.Constants.Count, consts.values.Count);
            foreach (RenderCompositionManifest.ExportedConstant c in RenderCompositionManifest.Constants)
                Assert.Equal(RenderCompositionManifest.D(c.Value), consts.GetValue(c.Name));
        }

        internal static string ReadParsekSource(string relPath)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(root, "Source", "Parsek",
                relPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "Source file not found at " + path);
            return File.ReadAllText(path);
        }
    }
}
