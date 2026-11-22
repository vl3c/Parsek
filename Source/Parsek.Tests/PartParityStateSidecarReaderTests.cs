using System.Collections.Generic;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A state case's sidecar (design-part-ghost-parity.md Appendix D.2), written by
    /// <see cref="PartParityJson.WriteCase"/> and read back by
    /// <see cref="PartParityJsonReader.ReadCaseSidecar"/> with what a later step's manifest and
    /// state control judge need: the state proof, every view's start and stowedRef blocks, the
    /// start-route structure and the stowed pairing id. A stowed sidecar reads them as null.
    /// </summary>
    public class PartParityStateSidecarReaderTests
    {
        private static PartParityViewMeasures M(int realPx, int ghostPx, int realOnlyBlob)
            => new PartParityViewMeasures
            {
                Width = 256, Height = 256, RealPx = realPx, GhostPx = ghostPx, AndPx = ghostPx, OrPx = realPx,
                RealOnlyPx = realPx - ghostPx, RealOnlyTolPx = realPx - ghostPx, RealOnlyBlobPx = realOnlyBlob,
                RealOnlyBlobPct = 1.5, GhostOnlyBlobPct = 0.0, Iou = 0.75,
            };

        private static PartParityCaseSidecar StateSample()
        {
            var s = new PartParityCaseSidecar
            {
                CaseId = "longantenna--default--deployed--0a1b2c3d", Part = "longAntenna", Source = "Squad",
                State = "deployed", Context = "alone",
                Setup = new PartParitySetup { Status = "ok" },
                Structure = new PartParityStructureDiff { RealRenderers = 4, GhostRenderers = 4, Pairs = 4 },
                StructureStart = new PartParityStructureDiff { RealRenderers = 4, GhostRenderers = 3, Pairs = 3 },
                StowedCaseId = "longantenna--default--11223344",
                StateProof = new PartParityStateProof
                {
                    Family = "deployable", Module = "ModuleDeployableAntenna", ModuleIndex = 2, State = "deployed",
                    Action = "Extend", EventVisible = true, ActionResult = "invoked", Reached = true, WaitS = 3.5,
                    EndpointField = "deployState", EndpointValue = "EXTENDED",
                    Events = new List<PartParityStateEvent>
                    {
                        new PartParityStateEvent { Type = "DeployableExtended", Ut = 1902.25, Pid = 77, Midx = 0 },
                    },
                    GhostEvent = "applied", GhostStart = "not-reached", StartFirstFrameAtTarget = false,
                },
            };
            s.StructureStart.MissingOnGhost.Add(new PartParityRendererRef { Key = "model/dish", Mesh = "dish", Rendered = true });
            s.Views.Add(new PartParityViewRecord
            {
                View = "side-a", Measures = M(1000, 990, 4),
                Start = new PartParityViewRecord { Measures = M(1000, 600, 200) },
                StowedRef = M(1000, 500, 300),
            });
            return s;
        }

        [Fact]
        public void AStateSidecar_ReadsBackItsProofRoutesAndPairing()
        {
            PartParityCaseSidecar back = PartParityJsonReader.ReadCaseSidecar(PartParityJson.WriteCase(StateSample()));
            Assert.NotNull(back.StateProof);
            Assert.Equal("deployable", back.StateProof.Family);
            Assert.Equal(2, back.StateProof.ModuleIndex);
            Assert.Equal("invoked", back.StateProof.ActionResult);
            Assert.True(back.StateProof.Reached);
            Assert.Equal(3.5, back.StateProof.WaitS);
            Assert.Equal("deployState", back.StateProof.EndpointField);
            Assert.Equal("EXTENDED", back.StateProof.EndpointValue);
            Assert.Single(back.StateProof.Events);
            Assert.Equal("DeployableExtended", back.StateProof.Events[0].Type);
            Assert.Equal(77u, back.StateProof.Events[0].Pid);
            Assert.Equal("applied", back.StateProof.GhostEvent);
            Assert.Equal("not-reached", back.StateProof.GhostStart);
            Assert.Equal("longantenna--default--11223344", back.StowedCaseId);
            Assert.Equal(3, back.StructureStart.Pairs);
            Assert.Single(back.StructureStart.MissingOnGhost);

            PartParityViewRecord v = Assert.Single(back.Views);
            Assert.Equal("side-a", v.View);
            Assert.Equal(990, v.Measures.GhostPx);
            Assert.NotNull(v.Start);
            Assert.Null(v.Start.View);
            Assert.Equal(600, v.Start.Measures.GhostPx);
            Assert.Equal(200, v.Start.Measures.RealOnlyBlobPx);
            Assert.NotNull(v.StowedRef);
            Assert.Equal(500, v.StowedRef.GhostPx);
            Assert.Equal(300, v.StowedRef.RealOnlyBlobPx);
            Assert.Equal(1.5, v.StowedRef.RealOnlyBlobPct);
        }

        [Fact]
        public void AStateControlReadBack_IsJudgedOnTheProofAndTheShapes()
        {
            // The state control's re-judge from disk (a later step): reached, one event, the
            // event route and the stowed reference failing on shape.
            PartParityCaseSidecar s = StateSample();
            s.Views[0].Measures = M(1000, 500, 300);
            PartParityCaseSidecar back = PartParityJsonReader.ReadCaseSidecar(PartParityJson.WriteCase(s));
            string outcome = PartParitySweepPlan.JudgeStateControl(back.StateProof.Reached == true,
                back.StateProof.Events.Count, back.Views, back.Setup.Status, out _);
            Assert.Equal(PartParitySweepPlan.JudgeStateControl(true, 1, s.Views, "ok", out _), outcome);
        }

        [Theory]
        [InlineData(@"C:\KSP\automation\stock-minimal\KSP_x64_Data\..\", "stock-minimal")]
        [InlineData(@"C:\KSP\automation\modded-compat\KSP_x64_Data/../", "modded-compat")]
        [InlineData(@"C:\KSP\Kerbal Space Program\", "Kerbal Space Program")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void TheManifestProfile_IsTheInstallDirectoryNotDotDot(string root, string expected)
            => Assert.Equal(expected, PartParityManifestBuilder.ProfileNameOf(root));

        [Fact]
        public void AStowedSidecar_ReadsNoStateBlocks()
        {
            PartParityCaseSidecar s = StateSample();
            s.StateProof = null;
            s.StructureStart = null;
            s.StowedCaseId = null;
            s.Views[0].Start = null;
            s.Views[0].StowedRef = null;
            PartParityCaseSidecar back = PartParityJsonReader.ReadCaseSidecar(PartParityJson.WriteCase(s));
            Assert.Null(back.StateProof);
            Assert.Null(back.StructureStart);
            Assert.Null(back.StowedCaseId);
            Assert.Null(back.Views[0].Start);
            Assert.Null(back.Views[0].StowedRef);
        }
    }
}
