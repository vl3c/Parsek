using System.Collections.Generic;
using Parsek.TestCommands;
using Xunit;
using R = Parsek.TestCommands.PartParityDepthMaskRepair;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the sweep's real-side depth-mask repair
    /// (<see cref="PartParityDepthMaskRepair"/>): which modules it looks at, how a reference is
    /// classified, when a module needs the repair, the module's own prefab-time body rule, the
    /// outcome after the re-run, the case flag and the one summary line.
    /// </summary>
    public class PartParityDepthMaskRepairTests
    {
        [Theory]
        [InlineData("ModuleRestockDepthMask", true)]
        [InlineData("ModuleDepthMask", true)]
        [InlineData("moduledepthmask", true)]
        [InlineData("ModuleRestockLinkedMesh", false)]
        [InlineData("ModuleEngines", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsDepthMaskModuleName_IsTheGhostBuildersModuleRule(string name, bool expected)
        {
            Assert.Equal(expected, R.IsDepthMaskModuleName(name));
        }

        [Theory]
        // managed null wins over everything else
        [InlineData(true, true, true, true, (int)R.RefState.Null)]
        // a wrapper whose native object is gone
        [InlineData(false, true, true, true, (int)R.RefState.Destroyed)]
        // an asset with no hierarchy (the shader)
        [InlineData(false, false, false, false, (int)R.RefState.Alive)]
        [InlineData(false, false, true, true, (int)R.RefState.InPart)]
        [InlineData(false, false, true, false, (int)R.RefState.OutsidePart)]
        public void Classify_ReadsTheFourStates(bool managedNull, bool unityNull, bool hierarchy, bool underPart,
            int expected)
        {
            Assert.Equal((R.RefState)expected, R.Classify(managedNull, unityNull, hierarchy, underPart));
        }

        [Fact]
        public void NeedsRepair_FalseOnlyWhenEveryReferenceIsLiveInThePart()
        {
            var inPart = new List<R.RefState> { R.RefState.InPart, R.RefState.InPart };
            Assert.False(R.NeedsRepair(R.RefState.InPart, inPart, R.RefState.Alive));

            Assert.True(R.NeedsRepair(R.RefState.Destroyed, inPart, R.RefState.Alive));
            Assert.True(R.NeedsRepair(R.RefState.Null, inPart, R.RefState.Alive));
            // a reference into another object (the prefab) is not this part's
            Assert.True(R.NeedsRepair(R.RefState.OutsidePart, inPart, R.RefState.Alive));
            Assert.True(R.NeedsRepair(R.RefState.InPart,
                new List<R.RefState> { R.RefState.InPart, R.RefState.Destroyed }, R.RefState.Alive));
            Assert.True(R.NeedsRepair(R.RefState.InPart, new List<R.RefState>(), R.RefState.Alive));
            Assert.True(R.NeedsRepair(R.RefState.InPart, null, R.RefState.Alive));
            Assert.True(R.NeedsRepair(R.RefState.InPart, inPart, R.RefState.Null));
            Assert.True(R.NeedsRepair(R.RefState.InPart, inPart, R.RefState.Destroyed));
        }

        [Theory]
        // ReStock's OnLoad: empty bodyTransform -> part.partTransform
        [InlineData("", false, true)]
        [InlineData(null, false, true)]
        // named and found -> the named transform
        [InlineData("body", true, false)]
        // named but not found -> falls back to part.partTransform
        [InlineData("body", false, true)]
        public void UsePartTransformForBody_MirrorsThePrefabTimeRule(string name, bool found, bool expected)
        {
            Assert.Equal(expected, R.UsePartTransformForBody(name, found));
        }

        [Theory]
        [InlineData(0, true, 0, (int)R.Outcome.Unresolved)]
        [InlineData(2, false, 0, (int)R.Outcome.Unresolved)]
        [InlineData(2, true, 2, (int)R.Outcome.Repaired)]
        [InlineData(2, true, 1, (int)R.Outcome.NotDepthShaded)]
        [InlineData(1, true, 0, (int)R.Outcome.NotDepthShaded)]
        public void AfterRerun_RepairedOnlyWhenEveryMaskCarriesTheDepthShader(int resolved, bool shader, int shaded,
            int expected)
        {
            Assert.Equal((R.Outcome)expected, R.AfterRerun(resolved, shader, shaded));
        }

        [Fact]
        public void CaseFlag_NoneForIntactModules_RepairedCount_UnrepairedWins()
        {
            Assert.Null(R.CaseFlag(null));
            Assert.Null(R.CaseFlag(new List<R.ModuleResult>()));
            Assert.Null(R.CaseFlag(new List<R.ModuleResult> { new R.ModuleResult { Outcome = R.Outcome.Intact } }));
            Assert.Equal("depth-mask-repaired:2", R.CaseFlag(new List<R.ModuleResult>
            {
                new R.ModuleResult { Outcome = R.Outcome.Repaired },
                new R.ModuleResult { Outcome = R.Outcome.Intact },
                new R.ModuleResult { Outcome = R.Outcome.Repaired },
            }));
            Assert.Equal("depth-mask-unrepaired:1", R.CaseFlag(new List<R.ModuleResult>
            {
                new R.ModuleResult { Outcome = R.Outcome.Repaired },
                new R.ModuleResult { Outcome = R.Outcome.Threw },
            }));
            Assert.Equal("depth-mask-unrepaired:1", R.CaseFlag(new List<R.ModuleResult>
            {
                new R.ModuleResult { Outcome = R.Outcome.Unsupported },
            }));
        }

        [Fact]
        public void SummaryLine_NamesTheCaseCountsAndEveryModulesStates()
        {
            var results = new List<R.ModuleResult>
            {
                new R.ModuleResult
                {
                    Index = 7, Module = "ModuleRestockDepthMask", Mask = "fanintake_mask",
                    Body = R.RefState.Destroyed, Masks = { R.RefState.Destroyed }, Shader = R.RefState.Alive,
                    PrefabBody = R.RefState.InPart, PrefabMasks = new List<R.RefState> { R.RefState.InPart },
                    PrefabShader = R.RefState.Alive,
                    Outcome = R.Outcome.Repaired, MasksResolved = 1, MasksDepthShaded = 1,
                },
                new R.ModuleResult
                {
                    Index = 9, Module = "ModuleDepthMask", Outcome = R.Outcome.Unsupported, Detail = "members-missing",
                },
            };
            string line = R.SummaryLine("circularintake--default--d5470c37", "CircularIntake", results);
            Assert.StartsWith("case circularintake--default--d5470c37: depth-mask repair part=CircularIntake modules=2 " +
                              "repaired=1 intact=0 other=1", line);
            Assert.Contains("| m7=ModuleRestockDepthMask mask=fanintake_mask outcome=repaired " +
                            "live: body=destroyed masks=[destroyed] shader=alive " +
                            "prefab: body=in-part masks=[in-part] shader=alive resolved=1 depthShaded=1", line);
            Assert.Contains("| m9=ModuleDepthMask mask=<none> outcome=unsupported live: body=null masks=[] " +
                            "shader=null prefab: body=<none> masks=<none> shader=<none> detail=members-missing", line);
        }

        [Fact]
        public void Tokens_AreStable()
        {
            Assert.Equal("null", R.Token(R.RefState.Null));
            Assert.Equal("destroyed", R.Token(R.RefState.Destroyed));
            Assert.Equal("in-part", R.Token(R.RefState.InPart));
            Assert.Equal("outside-part", R.Token(R.RefState.OutsidePart));
            Assert.Equal("alive", R.Token(R.RefState.Alive));
            Assert.Equal("intact", R.Token(R.Outcome.Intact));
            Assert.Equal("repaired", R.Token(R.Outcome.Repaired));
            Assert.Equal("not-depth-shaded", R.Token(R.Outcome.NotDepthShaded));
            Assert.Equal("unresolved", R.Token(R.Outcome.Unresolved));
            Assert.Equal("unsupported", R.Token(R.Outcome.Unsupported));
            Assert.Equal("threw", R.Token(R.Outcome.Threw));
        }

        /// <summary>The repair's member names are the ReStock module's own: the decompiled
        /// <c>Restock.ModuleRestockDepthMask</c> (ReStock 1.4.x) declares exactly these.</summary>
        [Fact]
        public void MemberNames_AreTheReStockModulesOwn()
        {
            Assert.Equal("maskTransform", R.FieldMaskName);
            Assert.Equal("bodyTransform", R.FieldBodyName);
            Assert.Equal("shaderName", R.FieldShaderName);
            Assert.Equal("maskTransformObjects", R.FieldMaskObjects);
            Assert.Equal("bodyTransformObject", R.FieldBodyObject);
            Assert.Equal("depthShader", R.FieldDepthShader);
            Assert.Equal("UpdateAllMaterials", R.MethodUpdate);
        }
    }
}
