using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests.SaveRoundTrip
{
    /// <summary>
    /// Field-level save/load round-trip of a whole <see cref="RecordingTree"/> through
    /// <see cref="RecordingTree.Save"/> / <see cref="RecordingTree.Load"/>: the tree metadata,
    /// every <see cref="BranchPoint"/> and every recording's RECORDING node (with the same
    /// <see cref="Recording"/> contract as <see cref="RecordingSaveRoundTripTests"/>, plus the
    /// load-side endpoint backfill the tree wrapper runs). Runs under a comma-decimal culture.
    /// </summary>
    [Collection("Sequential")]
    [SaveRoundTripCovers(typeof(RecordingTree))]
    public class RecordingTreeSaveRoundTripTests : IDisposable
    {
        public RecordingTreeSaveRoundTripTests()
        {
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
        }

        private static FieldRoundTrip NewRoundTrip()
        {
            FieldRoundTrip rt = null;
            FieldSpec tree = new FieldSpec(typeof(RecordingTree))
                .NotPersisted("in-memory undo slot of the #266 vessel-switch stash, never serialized",
                    "VesselSwitchPreTransitionActiveRecordingId", "VesselSwitchPreTransitionVesselPid")
                .Derived("rebuilt from Recordings by RebuildBackgroundMap at the end of Load",
                    "BackgroundMap", "RecordedVesselPids")
                // Load keys recordings by their own RecordingId, so the dictionary key must be it.
                .Fill("Recordings", () =>
                {
                    var d = new Dictionary<string, Recording>();
                    for (int i = 0; i < 2; i++)
                    {
                        var rec = (Recording)rt.Build(typeof(Recording), "RecordingTree.Recordings[" + i + "]");
                        d[rec.RecordingId] = rec;
                    }
                    return d;
                });
            var specs = new List<FieldSpec>(RecordingMetadataSpecs.All()) { tree };
            rt = new FieldRoundTrip(specs);
            return rt;
        }

        private static RecordingTree SaveAndLoad(RecordingTree written, out ConfigNode node)
        {
            node = new ConfigNode("RECORDING_TREE");
            written.Save(node);
            return RecordingTree.Load(node);
        }

        [Fact]
        public void RecordingTree_EveryFieldSurvivesSaveAndLoad()
        {
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                FieldRoundTrip rt = NewRoundTrip();
                var written = rt.Build<RecordingTree>();
                RecordingTree read = SaveAndLoad(written, out _);
                foreach (Recording rec in written.Recordings.Values)
                    RecordingMetadataSpecs.NormalizeAnchorThroughTrajectoryCodec(rec);

                var problems = rt.Diff(written, read, "RecordingTree");
                Assert.True(problems.Count == 0, FieldRoundTrip.Report("RecordingTree", problems));
            });
        }

        [Fact]
        public void BranchPoint_EveryFieldSurvivesSaveAndLoad()
        {
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                var rt = new FieldRoundTrip(new FieldSpec[0]);
                var written = rt.Build<BranchPoint>();
                var node = new ConfigNode("BRANCH_POINT");
                RecordingTree.SaveBranchPointInto(node, written);
                BranchPoint read = RecordingTree.LoadBranchPointFrom(node);

                var problems = rt.Diff(written, read, "BranchPoint");
                Assert.True(problems.Count == 0, FieldRoundTrip.Report("BranchPoint", problems));
            });
        }

        [Fact]
        public void Harness_DetectsATreeFieldTheCodecDrops()
        {
            // Proves the gate can fail: a tree whose saved BRANCH_POINT lost a key the codec
            // writes must report that field.
            FieldRoundTrip rt = NewRoundTrip();
            var written = rt.Build<RecordingTree>();
            var node = new ConfigNode("RECORDING_TREE");
            written.Save(node);
            ConfigNode bp = node.GetNodes("BRANCH_POINT").First();
            Assert.True(bp.HasValue("mergeCause"));
            bp.RemoveValue("mergeCause");
            RecordingTree read = RecordingTree.Load(node);

            var problems = rt.Diff(written, read, "RecordingTree");
            Assert.Contains(problems, p => p.StartsWith("RecordingTree.BranchPoints[0].MergeCause:", StringComparison.Ordinal));
        }
    }
}
