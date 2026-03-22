using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class CompoundPartDataTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CompoundPartDataTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void NoPARTDATA_NonCompoundPart_ReturnsFalse_NoLog()
        {
            var partNode = new ConfigNode("PART");

            bool result = GhostVisualBuilder.TryParseCompoundPartData(
                partNode, null, isCompoundPart: false, 12345, "fuelTank", out _);

            Assert.False(result);
            Assert.Empty(logLines);
        }

        [Fact]
        public void NoPARTDATA_CompoundPart_ReturnsFalse_LogsWarning()
        {
            var partNode = new ConfigNode("PART");

            bool result = GhostVisualBuilder.TryParseCompoundPartData(
                partNode, null, isCompoundPart: true, 12345, "fuelLine", out _);

            Assert.False(result);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostVisual]") &&
                l.Contains("CompoundPart fixup WARNING") &&
                l.Contains("fuelLine") &&
                l.Contains("12345") &&
                l.Contains("no PARTDATA node"));
        }

        [Fact]
        public void PARTDATA_MissingPos_ReturnsFalse_LogsSkip()
        {
            var partNode = new ConfigNode("PART");
            partNode.AddNode("PARTDATA");

            bool result = GhostVisualBuilder.TryParseCompoundPartData(
                partNode, null, isCompoundPart: true, 99999, "strutConnector", out _);

            Assert.False(result);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostVisual]") &&
                l.Contains("CompoundPart fixup skipped") &&
                l.Contains("strutConnector") &&
                l.Contains("no parseable 'pos'"));
        }

        [Fact]
        public void PARTDATA_UnparseablePos_ReturnsFalse_LogsSkip()
        {
            var partNode = new ConfigNode("PART");
            var partData = partNode.AddNode("PARTDATA");
            partData.AddValue("pos", "not,a,vector,really");

            bool result = GhostVisualBuilder.TryParseCompoundPartData(
                partNode, null, isCompoundPart: true, 55555, "fuelLine", out _);

            Assert.False(result);
            Assert.Contains(logLines, l =>
                l.Contains("CompoundPart fixup skipped") &&
                l.Contains("no parseable 'pos'"));
        }

        [Fact]
        public void PARTDATA_ValidPos_MissingRot_ReturnsTrue_DefaultsToIdentity_Logs()
        {
            var partNode = new ConfigNode("PART");
            var partData = partNode.AddNode("PARTDATA");
            partData.AddValue("pos", "-0.224, -0.054, 0.081");

            CompoundPartData data;
            bool result = GhostVisualBuilder.TryParseCompoundPartData(
                partNode, null, isCompoundPart: true, 12345, "fuelLine", out data);

            Assert.True(result);
            Assert.Equal(Quaternion.identity, data.targetRot);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostVisual]") &&
                l.Contains("no parseable 'rot'") &&
                l.Contains("defaulting to identity"));
        }

        [Fact]
        public void PARTDATA_ValidPosAndRot_ReturnsTrue_ParsesCorrectly()
        {
            var partNode = new ConfigNode("PART");
            var partData = partNode.AddNode("PARTDATA");
            partData.AddValue("pos", "-0.223904356,-0.0542270504,0.0808728784");
            partData.AddValue("rot", "0,0.499999911,6.29212309E-08,0.866025448");

            CompoundPartData data;
            bool result = GhostVisualBuilder.TryParseCompoundPartData(
                partNode, null, isCompoundPart: true, 1306106500, "fuelLine", out data);

            Assert.True(result);
            Assert.InRange(data.targetPos.x, -0.224f, -0.223f);
            Assert.InRange(data.targetPos.y, -0.055f, -0.054f);
            Assert.InRange(data.targetPos.z, 0.080f, 0.082f);
            Assert.InRange(data.targetRot.y, 0.499f, 0.501f);
            Assert.InRange(data.targetRot.w, 0.865f, 0.867f);
            // No warning/skip logs — only a rot-missing log would appear, and rot is present
            Assert.DoesNotContain(logLines, l => l.Contains("skipped") || l.Contains("WARNING"));
        }

        [Fact]
        public void PARTDATA_DefaultTransformNames_WhenNoPartConfig()
        {
            var partNode = new ConfigNode("PART");
            var partData = partNode.AddNode("PARTDATA");
            partData.AddValue("pos", "1,2,3");
            partData.AddValue("rot", "0,0,0,1");

            CompoundPartData data;
            GhostVisualBuilder.TryParseCompoundPartData(
                partNode, null, isCompoundPart: false, 100, "fuelLine", out data);

            Assert.Equal("obj_line", data.lineObjName);
            Assert.Equal("obj_targetAnchor", data.targetAnchorName);
            Assert.Equal("obj_targetCap", data.targetCapName);
        }

        [Fact]
        public void PARTDATA_ReadsTransformNames_FromCModuleLinkedMeshConfig()
        {
            var partNode = new ConfigNode("PART");
            var partData = partNode.AddNode("PARTDATA");
            partData.AddValue("pos", "1,2,3");
            partData.AddValue("rot", "0,0,0,1");

            var partConfig = new ConfigNode("PART");
            var module = partConfig.AddNode("MODULE");
            module.AddValue("name", "CModuleLinkedMesh");
            module.AddValue("lineObjName", "obj_strut");
            module.AddValue("targetAnchorName", "obj_custom_anchor");
            module.AddValue("targetCapName", "obj_custom_cap");

            CompoundPartData data;
            bool result = GhostVisualBuilder.TryParseCompoundPartData(
                partNode, partConfig, isCompoundPart: true, 200, "strutConnector", out data);

            Assert.True(result);
            Assert.Equal("obj_strut", data.lineObjName);
            Assert.Equal("obj_custom_anchor", data.targetAnchorName);
            Assert.Equal("obj_custom_cap", data.targetCapName);
        }

        [Fact]
        public void PARTDATA_PartialLinkedMeshConfig_KeepsDefaults()
        {
            var partNode = new ConfigNode("PART");
            var partData = partNode.AddNode("PARTDATA");
            partData.AddValue("pos", "1,2,3");
            partData.AddValue("rot", "0,0,0,1");

            // Config with CModuleLinkedMesh that only specifies lineObjName
            var partConfig = new ConfigNode("PART");
            var module = partConfig.AddNode("MODULE");
            module.AddValue("name", "CModuleLinkedMesh");
            module.AddValue("lineObjName", "obj_strut");

            CompoundPartData data;
            GhostVisualBuilder.TryParseCompoundPartData(
                partNode, partConfig, isCompoundPart: true, 300, "strutConnector", out data);

            Assert.Equal("obj_strut", data.lineObjName);
            Assert.Equal("obj_targetAnchor", data.targetAnchorName);
            Assert.Equal("obj_targetCap", data.targetCapName);
        }
    }
}
