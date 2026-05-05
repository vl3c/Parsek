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
                SelectedRootPartPersistentId = 3087746488u,
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
            Assert.Equal(3087746488u, restored.SelectedRootPartPersistentId);
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
            Assert.Equal(0u, restored.SelectedRootPartPersistentId);
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
            Assert.Equal(0u, restored.SelectedRootPartPersistentId);
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

        // ============================================================
        // Issue #734: InPlaceContinuation field round-trip
        // ============================================================

        [Fact]
        public void ReFlySessionMarker_InPlaceContinuationTrue_RoundTrips()
        {
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess",
                TreeId = "tree",
                ActiveReFlyRecordingId = "rec_fork",
                OriginChildRecordingId = "rec_origin",
                SupersedeTargetId = "rec_origin",
                RewindPointId = "rp",
                InvokedUT = 100.0,
                InPlaceContinuation = true,
            };

            var parent = new ConfigNode();
            marker.SaveInto(parent);

            // The flag must be serialized so a quickload mid-session
            // restores the right shape.
            var savedNode = parent.GetNode("REFLY_SESSION_MARKER");
            Assert.Equal("true", savedNode.GetValue("inPlaceContinuation"));

            var restored = ReFlySessionMarker.LoadFrom(savedNode);
            Assert.True(restored.InPlaceContinuation);
        }

        [Fact]
        public void ReFlySessionMarker_InPlaceContinuationFalse_OmitsValue_LoadsAsFalse()
        {
            // Defaults to false; when false the codec omits the value to
            // keep marker payloads minimal.
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess",
                TreeId = "tree",
                ActiveReFlyRecordingId = "rec_placeholder",
                OriginChildRecordingId = "rec_origin",
                RewindPointId = "rp",
                InvokedUT = 50.0,
                InPlaceContinuation = false,
            };

            var parent = new ConfigNode();
            marker.SaveInto(parent);

            var savedNode = parent.GetNode("REFLY_SESSION_MARKER");
            Assert.False(savedNode.HasValue("inPlaceContinuation"));

            var restored = ReFlySessionMarker.LoadFrom(savedNode);
            Assert.False(restored.InPlaceContinuation);
        }

        [Fact]
        public void ReFlySessionMarker_LegacyMarkerWithoutFlag_LoadsAsFalse()
        {
            // Pre-#734 markers do not carry the inPlaceContinuation value;
            // load must default to false (the legacy id-equality shape is
            // what the gate helper recognises for those).
            var node = new ConfigNode("REFLY_SESSION_MARKER");
            node.AddValue("sessionId", "sess");
            node.AddValue("treeId", "tree");
            node.AddValue("activeReFlyRecordingId", "rec_origin"); // legacy shape: active == origin
            node.AddValue("originChildRecordingId", "rec_origin");
            node.AddValue("rewindPointId", "rp");
            node.AddValue("invokedUT", "0");

            var restored = ReFlySessionMarker.LoadFrom(node);

            Assert.False(restored.InPlaceContinuation);
        }

        [Fact]
        public void ReFlySessionMarker_InPlaceContinuationFlag_CaseInsensitiveLoad()
        {
            // Defensive: KSP's ConfigNode serialization is famously
            // inconsistent about case. Confirm that 'True' loads the same
            // as 'true' (we write lowercase but legacy / external tools may
            // write uppercase).
            var node = new ConfigNode("REFLY_SESSION_MARKER");
            node.AddValue("sessionId", "sess");
            node.AddValue("activeReFlyRecordingId", "rec_fork");
            node.AddValue("originChildRecordingId", "rec_origin");
            node.AddValue("rewindPointId", "rp");
            node.AddValue("invokedUT", "0");
            node.AddValue("inPlaceContinuation", "True");

            var restored = ReFlySessionMarker.LoadFrom(node);

            Assert.True(restored.InPlaceContinuation);
        }

        // ============================================================
        // Issue #734: IsInPlaceContinuation centralized gate helper
        // ============================================================

        [Fact]
        public void IsInPlaceContinuation_NullMarker_False()
        {
            Assert.False(ReFlySessionMarker.IsInPlaceContinuation(null));
        }

        [Fact]
        public void IsInPlaceContinuation_EmptyIds_False()
        {
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess",
                ActiveReFlyRecordingId = null,
                OriginChildRecordingId = null,
                InPlaceContinuation = true, // even with the flag, missing ids is invalid
            };
            Assert.False(ReFlySessionMarker.IsInPlaceContinuation(marker));
        }

        [Fact]
        public void IsInPlaceContinuation_ForkShape_True()
        {
            // Post-#734 fork shape: distinct ids + flag set.
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec_fork",
                OriginChildRecordingId = "rec_origin",
                InPlaceContinuation = true,
            };
            Assert.True(ReFlySessionMarker.IsInPlaceContinuation(marker));
        }

        [Fact]
        public void IsInPlaceContinuation_LegacyShape_False()
        {
            // Pre-fork legacy shape (ids equal but no InPlaceContinuation
            // flag) is no longer recognised. The flag is the only signal
            // -- AtomicMarkerWrite is the sole writer and always pairs
            // the in-place case with the flag set, so any marker without
            // the flag is by construction a placeholder pattern.
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec_origin",
                OriginChildRecordingId = "rec_origin",
                InPlaceContinuation = false,
            };
            Assert.False(ReFlySessionMarker.IsInPlaceContinuation(marker));
        }

        [Fact]
        public void IsInPlaceContinuation_PlaceholderPattern_False()
        {
            // Placeholder Re-Fly: distinct ids, no flag. Player flies a
            // fresh strip-spawned vessel.
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec_placeholder",
                OriginChildRecordingId = "rec_origin",
                InPlaceContinuation = false,
            };
            Assert.False(ReFlySessionMarker.IsInPlaceContinuation(marker));
        }
    }
}
