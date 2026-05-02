using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Guards against ReFlySessionMarker save/load drift (design doc section 5.7).
    /// </summary>
    public class ReFlySessionMarkerRoundTripTests
    {
        [Fact]
        public void ReFlySessionMarker_AllFields_RoundTrips()
        {
            var marker = new ReFlySessionMarker
            {
                SessionId = "rf_c3d4",
                TreeId = "tree_xyz",
                ActiveReFlyRecordingId = "rec_prov",
                OriginChildRecordingId = "rec_child0",
                SupersedeTargetId = "rec_tip0",
                RewindPointId = "rp_a1b2",
                InvokedUT = 1742810.25,
                InvokedRealTime = "2026-04-17T23:15:00Z"
            };

            var parent = new ConfigNode("PARSEK");
            marker.SaveInto(parent);
            var node = parent.GetNode("REFLY_SESSION_MARKER");
            Assert.NotNull(node);

            var restored = ReFlySessionMarker.LoadFrom(node);
            Assert.Equal("rf_c3d4", restored.SessionId);
            Assert.Equal("tree_xyz", restored.TreeId);
            Assert.Equal("rec_prov", restored.ActiveReFlyRecordingId);
            Assert.Equal("rec_child0", restored.OriginChildRecordingId);
            Assert.Equal("rec_tip0", restored.SupersedeTargetId);
            Assert.Equal("rp_a1b2", restored.RewindPointId);
            Assert.Equal(1742810.25, restored.InvokedUT);
            Assert.Equal("2026-04-17T23:15:00Z", restored.InvokedRealTime);
        }

        [Fact]
        public void ReFlySessionMarker_MinimalFields_RoundTrips()
        {
            // A marker with only the mandatory fields (no wall-clock) must still
            // round-trip. The loader returns null for missing optional values.
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess",
                TreeId = "tree",
                ActiveReFlyRecordingId = "rec_active",
                OriginChildRecordingId = "rec_origin",
                SupersedeTargetId = "rec_origin",
                RewindPointId = "rp",
                InvokedUT = 0.0
            };

            var parent = new ConfigNode("PARSEK");
            marker.SaveInto(parent);
            var restored = ReFlySessionMarker.LoadFrom(parent.GetNode("REFLY_SESSION_MARKER"));

            Assert.Equal("sess", restored.SessionId);
            Assert.Equal("tree", restored.TreeId);
            Assert.Equal("rec_active", restored.ActiveReFlyRecordingId);
            Assert.Equal("rec_origin", restored.OriginChildRecordingId);
            Assert.Equal("rec_origin", restored.SupersedeTargetId);
            Assert.Equal("rp", restored.RewindPointId);
            Assert.Equal(0.0, restored.InvokedUT);
            Assert.Null(restored.InvokedRealTime);
        }

        [Fact]
        public void ReFlySessionMarker_LegacyWithoutSupersedeTarget_LoadsNull()
        {
            var node = new ConfigNode("REFLY_SESSION_MARKER");
            node.AddValue("sessionId", "sess");
            node.AddValue("treeId", "tree");
            node.AddValue("activeReFlyRecordingId", "rec_active");
            node.AddValue("originChildRecordingId", "rec_origin");
            node.AddValue("rewindPointId", "rp");
            node.AddValue("invokedUT", "0");

            var restored = ReFlySessionMarker.LoadFrom(node);

            Assert.Null(restored.SupersedeTargetId);
        }

        [Fact]
        public void ReFlySessionMarker_PreSessionBranchPointIds_RoundTrip_PopulatedList()
        {
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess",
                TreeId = "tree",
                ActiveReFlyRecordingId = "rec_active",
                OriginChildRecordingId = "rec_origin",
                RewindPointId = "rp",
                InvokedUT = 0.0,
                PreSessionBranchPointIds = new List<string>
                {
                    "bp_pre_1", "bp_pre_2", "bp_pre_3",
                },
            };

            var parent = new ConfigNode("PARSEK");
            marker.SaveInto(parent);
            var restored = ReFlySessionMarker.LoadFrom(parent.GetNode("REFLY_SESSION_MARKER"));

            Assert.NotNull(restored.PreSessionBranchPointIds);
            Assert.Equal(3, restored.PreSessionBranchPointIds.Count);
            Assert.Contains("bp_pre_1", restored.PreSessionBranchPointIds);
            Assert.Contains("bp_pre_2", restored.PreSessionBranchPointIds);
            Assert.Contains("bp_pre_3", restored.PreSessionBranchPointIds);
        }

        [Fact]
        public void ReFlySessionMarker_PreSessionBranchPointIds_RoundTrip_EmptyListSurvivesAsEmpty()
        {
            // Distinguish "field present, empty" (post-fix marker on a
            // tree that had no BPs at invocation -> baseline IS known,
            // every current BP is session-authored) from "field absent"
            // (legacy marker -> gate skipped). The presence sentinel
            // makes that distinction round-trippable.
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess",
                TreeId = "tree",
                ActiveReFlyRecordingId = "rec_active",
                OriginChildRecordingId = "rec_origin",
                RewindPointId = "rp",
                InvokedUT = 0.0,
                PreSessionBranchPointIds = new List<string>(),
            };

            var parent = new ConfigNode("PARSEK");
            marker.SaveInto(parent);
            var restored = ReFlySessionMarker.LoadFrom(parent.GetNode("REFLY_SESSION_MARKER"));

            Assert.NotNull(restored.PreSessionBranchPointIds);
            Assert.Empty(restored.PreSessionBranchPointIds);
        }

        [Fact]
        public void ReFlySessionMarker_LegacyWithoutPreSessionBranchPointIds_LoadsAsNull()
        {
            // Markers persisted before the field shipped: load returns
            // null so the structural-mutation gate conservatively skips.
            var node = new ConfigNode("REFLY_SESSION_MARKER");
            node.AddValue("sessionId", "sess");
            node.AddValue("treeId", "tree");
            node.AddValue("activeReFlyRecordingId", "rec_active");
            node.AddValue("originChildRecordingId", "rec_origin");
            node.AddValue("rewindPointId", "rp");
            node.AddValue("invokedUT", "0");

            var restored = ReFlySessionMarker.LoadFrom(node);

            Assert.Null(restored.PreSessionBranchPointIds);
        }
    }
}
