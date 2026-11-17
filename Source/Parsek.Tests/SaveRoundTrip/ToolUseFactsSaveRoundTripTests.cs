using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests.SaveRoundTrip
{
    /// <summary>
    /// Field-level save/load round-trip of the tool-use fact: <see cref="ToolUseFacts"/> writes
    /// one TOOL_USE child per <see cref="ToolUse"/> into the RECORDING node and reads them back.
    /// Every <see cref="ToolKind"/> member, plus a kind number no member defines (written by a
    /// newer build, which the reader must keep), carries every <see cref="ToolUse"/> field back.
    /// The whole-recording round-trip in <see cref="RecordingSaveRoundTripTests"/> also fills
    /// <see cref="Recording.ToolUses"/>; this class owns the codec on its own.
    /// </summary>
    [Collection("Sequential")]
    [SaveRoundTripCovers(typeof(ToolUseFacts))]
    public class ToolUseFactsSaveRoundTripTests : IDisposable
    {
        private const int NewerBuildKind = 99;

        public ToolUseFactsSaveRoundTripTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        private static List<ToolUse> BuildEveryKind(FieldRoundTrip rt)
        {
            var uses = new List<ToolUse>();
            foreach (ToolKind kind in Enum.GetValues(typeof(ToolKind)))
            {
                var use = rt.Build<ToolUse>();
                use.Kind = kind;
                uses.Add(use);
            }
            var newer = rt.Build<ToolUse>();
            newer.Kind = (ToolKind)NewerBuildKind;
            uses.Add(newer);
            return uses;
        }

        private static Recording SaveAndLoad(Recording written, out ConfigNode node)
        {
            node = new ConfigNode("RECORDING");
            ToolUseFacts.SaveInto(node, written);
            var read = new Recording { RecordingId = written.RecordingId };
            ToolUseFacts.LoadFrom(node, read);
            return read;
        }

        [Fact]
        public void ToolUse_EveryFieldOfEveryKindSurvivesSaveAndLoad()
        {
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                var rt = new FieldRoundTrip(new FieldSpec[0], seed: 4100);
                var written = new Recording { RecordingId = "rec-tooluse", ToolUses = BuildEveryKind(rt) };

                Recording read = SaveAndLoad(written, out ConfigNode node);

                Assert.Equal(written.ToolUses.Count, node.GetNodes(ToolUseFacts.NodeName).Length);
                var problems = rt.Diff(written.ToolUses, read.ToolUses, "Recording.ToolUses");
                Assert.True(problems.Count == 0, FieldRoundTrip.Report("ToolUseFacts", problems));
            });
        }

        [Fact]
        public void ToolUse_FillerReachesEveryField()
        {
            // A ToolUse left at its defaults would round-trip vacuously; prove each filled use
            // carries a non-default value in every field the codec writes.
            var rt = new FieldRoundTrip(new FieldSpec[0], seed: 4200);
            foreach (ToolUse use in BuildEveryKind(rt))
            {
                Assert.NotEqual(0, (int)use.Kind);
                Assert.False(string.IsNullOrEmpty(use.Detail));
                Assert.NotEqual(0.0, use.FirstUT);
            }
            Assert.Equal(3, FieldRoundTrip.GetFields(typeof(ToolUse)).Count());
        }

        [Fact]
        public void ToolUse_SurvivesTheWholeRecordingCodec()
        {
            // The RECORDING node codec must reach ToolUseFacts on both sides.
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                var rt = new FieldRoundTrip(new FieldSpec[0], seed: 4300);
                var written = new Recording { RecordingId = "rec-tooluse-codec", ToolUses = BuildEveryKind(rt) };

                var node = new ConfigNode("RECORDING");
                RecordingTreeRecordCodec.SaveRecordingInto(node, written);
                var read = new Recording();
                RecordingTreeRecordCodec.LoadRecordingFrom(node, read);

                var problems = rt.Diff(written.ToolUses, read.ToolUses, "Recording.ToolUses");
                Assert.True(problems.Count == 0, FieldRoundTrip.Report("RecordingTreeRecordCodec ToolUses", problems));
            });
        }

        [Fact]
        public void Harness_DetectsAToolUseFieldTheCodecDrops()
        {
            var rt = new FieldRoundTrip(new FieldSpec[0], seed: 4400);
            var written = new Recording { RecordingId = "rec-tooluse-drop", ToolUses = BuildEveryKind(rt) };
            var node = new ConfigNode("RECORDING");
            ToolUseFacts.SaveInto(node, written);
            ConfigNode first = node.GetNodes(ToolUseFacts.NodeName)[0];
            Assert.True(first.HasValue("detail"));
            first.RemoveValue("detail");
            var read = new Recording();
            ToolUseFacts.LoadFrom(node, read);

            var problems = rt.Diff(written.ToolUses, read.ToolUses, "Recording.ToolUses");
            Assert.Contains(problems, p => p.StartsWith("Recording.ToolUses[0].Detail:", StringComparison.Ordinal));
        }
    }
}
