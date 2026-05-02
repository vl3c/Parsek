using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the Re-Fly tree anchor lock helpers in
    /// <see cref="ParsekFlight"/>. The lock captures a display-only
    /// alignment once per recording per active Re-Fly session, stores it in
    /// the body's rotating frame, and reprojects it while the hidden
    /// recorded trajectory remains the source of truth. The live vessel is
    /// only an initialization reference.
    ///
    /// <para>
    /// These tests exercise the parts that don't require a live KSP
    /// runtime: the recording-id → tree-id resolution path that decides
    /// which ghost positions get translated, the activation-gate decision
    /// rule, the snapshot lifecycle, the codec round-trip, and the pure
    /// body-fixed alignment math. The live KSP capture path is covered by
    /// in-game validation.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class ReFlyTreeAnchorLockTests : IDisposable
    {
        public ReFlyTreeAnchorLockTests()
        {
            ParsekLog.SuppressLogging = true;
            RecordingStore.Clear();
        }

        public void Dispose()
        {
            RecordingStore.Clear();
            ParsekLog.SuppressLogging = false;
        }

        [Fact]
        public void ResolveTreeIdForRecording_NullId_ReturnsNull()
        {
            Assert.Null(ParsekFlight.ResolveTreeIdForRecording(null));
            Assert.Null(ParsekFlight.ResolveTreeIdForRecording(""));
        }

        [Fact]
        public void ResolveTreeIdForRecording_NoTrees_ReturnsNull()
        {
            Assert.Null(ParsekFlight.ResolveTreeIdForRecording("any-rec-id"));
        }

        [Fact]
        public void ResolveTreeIdForRecording_RecordingInCommittedTree_ReturnsTreeId()
        {
            var tree = MakeTreeWithRecording(treeId: "tree-1", recordingId: "rec-A");
            RecordingStore.CommittedTrees.Add(tree);

            string treeId = ParsekFlight.ResolveTreeIdForRecording("rec-A");

            Assert.Equal("tree-1", treeId);
        }

        [Fact]
        public void ResolveTreeIdForRecording_UnknownRecording_ReturnsNull()
        {
            var tree = MakeTreeWithRecording(treeId: "tree-1", recordingId: "rec-A");
            RecordingStore.CommittedTrees.Add(tree);

            Assert.Null(ParsekFlight.ResolveTreeIdForRecording("rec-B"));
        }

        [Fact]
        public void ResolveTreeIdForRecording_RecordingInPendingTree_ReturnsPendingTreeId()
        {
            var pending = MakeTreeWithRecording(treeId: "pending-tree", recordingId: "rec-pending");
            RecordingStore.StashPendingTree(pending);

            string treeId = ParsekFlight.ResolveTreeIdForRecording("rec-pending");

            Assert.Equal("pending-tree", treeId);
        }

        [Fact]
        public void ResolveTreeIdForRecording_MultipleTrees_FindsCorrectTree()
        {
            var tree1 = MakeTreeWithRecording(treeId: "tree-1", recordingId: "rec-A");
            var tree2 = MakeTreeWithRecording(treeId: "tree-2", recordingId: "rec-B");
            RecordingStore.CommittedTrees.Add(tree1);
            RecordingStore.CommittedTrees.Add(tree2);

            Assert.Equal("tree-1", ParsekFlight.ResolveTreeIdForRecording("rec-A"));
            Assert.Equal("tree-2", ParsekFlight.ResolveTreeIdForRecording("rec-B"));
        }

        [Fact]
        public void TrySampleRecordedAbsoluteWorld_NullRec_ReturnsFalse()
        {
            Vector3d worldPos;
            Assert.False(ParsekFlight.TrySampleRecordedAbsoluteWorld((Recording)null, 0.0, out worldPos));
            Assert.False(ParsekFlight.TrySampleRecordedAbsoluteWorld(
                (List<TrackSection>)null, 0.0, out worldPos));
        }

        [Fact]
        public void TrySampleRecordedAbsoluteWorld_EmptySections_ReturnsFalse()
        {
            var rec = new Recording { RecordingId = "rec-empty" };
            Vector3d worldPos;
            Assert.False(ParsekFlight.TrySampleRecordedAbsoluteWorld(rec, 0.0, out worldPos));
        }

        [Fact]
        public void TrySampleRecordedAbsoluteWorld_RelativeSectionWithoutShadow_ReturnsFalse()
        {
            var rec = new Recording { RecordingId = "rec-rel-no-shadow" };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 0.0,
                endUT = 10.0,
                anchorVesselId = 12345u,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 5.0, latitude = 1, longitude = 2, altitude = 3 },
                },
                // absoluteFrames = null → no shadow data; helper must return false
                absoluteFrames = null,
            });

            Vector3d worldPos;
            Assert.False(ParsekFlight.TrySampleRecordedAbsoluteWorld(rec, 5.0, out worldPos));
        }

        [Fact]
        public void TrySampleRecordedAbsoluteWorld_OrbitalCheckpointSection_ReturnsFalse()
        {
            var rec = new Recording { RecordingId = "rec-checkpoint" };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = 0.0,
                endUT = 10.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 5.0 },
                },
            });

            Vector3d worldPos;
            Assert.False(ParsekFlight.TrySampleRecordedAbsoluteWorld(rec, 5.0, out worldPos));
        }

        // ============================================================
        // Pre-Re-Fly anchor snapshot lifecycle (#688 follow-up)
        // ============================================================

        [Fact]
        public void ClearPreReFlyAnchorTrajectory_NoSnapshot_NoOp()
        {
            var rec = new Recording { RecordingId = "rec-empty" };

            rec.ClearPreReFlyAnchorTrajectory();

            Assert.Null(rec.PreReFlyAnchorSessionId);
            Assert.Null(rec.PreReFlyAnchorPoints);
            Assert.Null(rec.PreReFlyAnchorOrbitSegments);
            Assert.Null(rec.PreReFlyAnchorTrackSections);
            Assert.False(rec.HasPreReFlyAnchorTrajectory("any"));
        }

        [Fact]
        public void ClearPreReFlyAnchorTrajectory_WithSnapshot_ClearsAllFields()
        {
            var rec = new Recording { RecordingId = "rec-with-snap" };
            rec.Points.Add(new TrajectoryPoint { ut = 1.0 });
            rec.OrbitSegments.Add(new OrbitSegment { startUT = 1.0, endUT = 2.0 });
            rec.TrackSections.Add(new TrackSection { referenceFrame = ReferenceFrame.Absolute, startUT = 0, endUT = 5 });
            rec.CapturePreReFlyAnchorTrajectory("sess-1");

            Assert.True(rec.HasPreReFlyAnchorTrajectory("sess-1"));

            rec.ClearPreReFlyAnchorTrajectory();

            Assert.Null(rec.PreReFlyAnchorSessionId);
            Assert.Null(rec.PreReFlyAnchorPoints);
            Assert.Null(rec.PreReFlyAnchorOrbitSegments);
            Assert.Null(rec.PreReFlyAnchorTrackSections);
            Assert.False(rec.HasPreReFlyAnchorTrajectory("sess-1"));
        }

        [Fact]
        public void ClearPreReFlyAnchorSnapshotsForSession_NullOrEmpty_ReturnsZero()
        {
            Assert.Equal(0, SupersedeCommit.ClearPreReFlyAnchorSnapshotsForSession(null));
            Assert.Equal(0, SupersedeCommit.ClearPreReFlyAnchorSnapshotsForSession(""));
        }

        [Fact]
        public void ClearPreReFlyAnchorSnapshotsForSession_OnlyMatchingSession_Cleared()
        {
            var recA = new Recording { RecordingId = "rec-A" };
            recA.Points.Add(new TrajectoryPoint { ut = 1.0 });
            recA.CapturePreReFlyAnchorTrajectory("sess-target");

            var recB = new Recording { RecordingId = "rec-B" };
            recB.Points.Add(new TrajectoryPoint { ut = 2.0 });
            recB.CapturePreReFlyAnchorTrajectory("sess-other");

            RecordingStore.AddCommittedInternal(recA);
            RecordingStore.AddCommittedInternal(recB);

            // Capture log output so we can assert the verbose summary fires.
            var logLines = new System.Collections.Generic.List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                int cleared = SupersedeCommit.ClearPreReFlyAnchorSnapshotsForSession("sess-target");
                Assert.Equal(1, cleared);
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
                ParsekLog.SuppressLogging = true;
            }

            Assert.False(recA.HasPreReFlyAnchorTrajectory("sess-target"));
            Assert.True(recB.HasPreReFlyAnchorTrajectory("sess-other"));
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Cleared 1 pre-Re-Fly anchor snapshot")
                && l.Contains("sess-target"));
        }

        [Fact]
        public void RecordingTreeRecordCodec_RoundTrip_PreservesPreReFlyAnchorSnapshot()
        {
            var rec = new Recording
            {
                RecordingId = "rec-roundtrip",
                VesselName = "Round Trip",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100.0, latitude = 1.5, longitude = -2.5, altitude = 1234.0,
                rotation = new Quaternion(0, 0, 0, 1), bodyName = "Kerbin",
                velocity = new Vector3(10, 20, 30),
            });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 100.0, endUT = 200.0, bodyName = "Kerbin",
            });
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0, endUT = 200.0,
                frames = new List<TrajectoryPoint>(rec.Points),
            });

            rec.CapturePreReFlyAnchorTrajectory("sess-roundtrip");
            Assert.True(rec.HasPreReFlyAnchorTrajectory("sess-roundtrip"));

            var node = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingInto(node, rec);

            var loaded = new Recording
            {
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            };
            RecordingTreeRecordCodec.LoadRecordingFrom(node, loaded);

            Assert.Equal("sess-roundtrip", loaded.PreReFlyAnchorSessionId);
            Assert.True(loaded.HasPreReFlyAnchorTrajectory("sess-roundtrip"));
            Assert.NotNull(loaded.PreReFlyAnchorPoints);
            Assert.Single(loaded.PreReFlyAnchorPoints);
            Assert.Equal(100.0, loaded.PreReFlyAnchorPoints[0].ut);
            Assert.Equal(1.5, loaded.PreReFlyAnchorPoints[0].latitude);
        }

        [Fact]
        public void Recording_DeepClone_PreservesPreReFlyAnchorSnapshot()
        {
            // #688 follow-up: repair / splice paths that DeepClone a
            // recording must preserve the captured pre-Re-Fly anchor
            // snapshot — otherwise display alignment falls back to the
            // trimmed live recording mid-session and ghosts in the active
            // Re-Fly tree misposition.
            var rec = new Recording
            {
                RecordingId = "rec-clone-source",
                VesselName = "Source",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 1.0 });
            rec.OrbitSegments.Add(new OrbitSegment { startUT = 1.0, endUT = 2.0 });
            rec.TrackSections.Add(MakeAbsoluteSectionWithFrame(0, 5));
            rec.CapturePreReFlyAnchorTrajectory("sess-clone");

            var clone = Recording.DeepClone(rec);

            Assert.NotNull(clone);
            Assert.Equal("sess-clone", clone.PreReFlyAnchorSessionId);
            Assert.True(clone.HasPreReFlyAnchorTrajectory("sess-clone"));
            Assert.NotNull(clone.PreReFlyAnchorPoints);
            Assert.Single(clone.PreReFlyAnchorPoints);
            Assert.NotNull(clone.PreReFlyAnchorOrbitSegments);
            Assert.Single(clone.PreReFlyAnchorOrbitSegments);
            Assert.NotNull(clone.PreReFlyAnchorTrackSections);
            Assert.Single(clone.PreReFlyAnchorTrackSections);

            // Independence: mutating the source's snapshot list must not
            // affect the clone (deep copy contract).
            rec.PreReFlyAnchorPoints.Add(new TrajectoryPoint { ut = 99.0 });
            Assert.Single(clone.PreReFlyAnchorPoints);
        }

        [Fact]
        public void Recording_DeepClone_NoSnapshot_LeavesCloneFieldsNull()
        {
            var rec = new Recording
            {
                RecordingId = "rec-clone-no-snap",
                VesselName = "No Snapshot",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            };

            var clone = Recording.DeepClone(rec);

            Assert.Null(clone.PreReFlyAnchorSessionId);
            Assert.Null(clone.PreReFlyAnchorPoints);
            Assert.Null(clone.PreReFlyAnchorOrbitSegments);
            Assert.Null(clone.PreReFlyAnchorTrackSections);
            Assert.False(clone.HasPreReFlyAnchorTrajectory("any-sess"));
        }

        [Fact]
        public void RecordingTreeRecordCodec_RoundTrip_NoSnapshot_OmitsAnchorNode()
        {
            var rec = new Recording
            {
                RecordingId = "rec-nosnap",
                VesselName = "No Snapshot",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            };
            // Snapshot intentionally not captured.

            var node = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingInto(node, rec);

            Assert.Null(node.GetNode("PRE_REFLY_ANCHOR"));

            var loaded = new Recording
            {
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            };
            RecordingTreeRecordCodec.LoadRecordingFrom(node, loaded);
            Assert.False(loaded.HasPreReFlyAnchorTrajectory("any-sess"));
        }

        // ============================================================
        // RefreshReFlyAnchorActivationGate (#688 spawn-frame fix)
        // ============================================================

        [Fact]
        public void ShouldRaiseGate_GhostAlreadyActive_AlwaysFalse()
        {
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess",
                TreeId = "tree-1",
                ActiveReFlyRecordingId = "rec-active",
            };
            // Even with offset miss in the active tree, an active ghost
            // must not get the gate raised.
            Assert.False(ParsekFlight.ShouldRaiseExternalActivationGate(
                ghostActive: true,
                recordingId: "rec-in-tree",
                marker: marker,
                hasResolvableAnchorData: true,
                offsetApplied: false));
        }

        [Fact]
        public void ShouldRaiseGate_NoMarker_AlwaysFalse()
        {
            // No active Re-Fly session: gate must stay clear regardless of
            // ghost active state.
            Assert.False(ParsekFlight.ShouldRaiseExternalActivationGate(
                ghostActive: false,
                recordingId: "rec-foo",
                marker: null,
                hasResolvableAnchorData: false,
                offsetApplied: false));
        }

        [Fact]
        public void ShouldRaiseGate_OutOfTreeRecording_StaysClear()
        {
            var tree = MakeTreeWithRecording(treeId: "tree-active", recordingId: "rec-in-tree");
            RecordingStore.CommittedTrees.Add(tree);
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess",
                TreeId = "tree-active",
                ActiveReFlyRecordingId = "rec-in-tree",
            };

            // recordingId not in any tree → out-of-tree → gate must stay clear.
            Assert.False(ParsekFlight.ShouldRaiseExternalActivationGate(
                ghostActive: false,
                recordingId: "rec-foreign",
                marker: marker,
                hasResolvableAnchorData: true,
                offsetApplied: false));
        }

        [Fact]
        public void ShouldRaiseGate_NoResolvableAnchorData_StaysClear()
        {
            // Safety net: non-in-place Re-Fly creates an empty provisional
            // with no captured snapshot and no track sections. Without this
            // safety net the gate would hold the ghost permanently invisible.
            var tree = MakeTreeWithRecording(treeId: "tree-1", recordingId: "rec-A");
            RecordingStore.CommittedTrees.Add(tree);
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess",
                TreeId = "tree-1",
                ActiveReFlyRecordingId = "rec-A",
            };

            Assert.False(ParsekFlight.ShouldRaiseExternalActivationGate(
                ghostActive: false,
                recordingId: "rec-A",
                marker: marker,
                hasResolvableAnchorData: false,
                offsetApplied: false));
        }

        [Fact]
        public void ShouldRaiseGate_InTreeAnchorDataPresentOffsetMiss_RaisesGate()
        {
            var tree = MakeTreeWithRecording(treeId: "tree-1", recordingId: "rec-A");
            RecordingStore.CommittedTrees.Add(tree);
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess",
                TreeId = "tree-1",
                ActiveReFlyRecordingId = "rec-A",
            };

            // Inactive ghost, in active tree, anchor data exists, offset
            // miss → gate must raise so the activation pipeline waits.
            Assert.True(ParsekFlight.ShouldRaiseExternalActivationGate(
                ghostActive: false,
                recordingId: "rec-A",
                marker: marker,
                hasResolvableAnchorData: true,
                offsetApplied: false));
        }

        [Fact]
        public void ShouldRaiseGate_OffsetResolved_StaysClear()
        {
            var tree = MakeTreeWithRecording(treeId: "tree-1", recordingId: "rec-A");
            RecordingStore.CommittedTrees.Add(tree);
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess",
                TreeId = "tree-1",
                ActiveReFlyRecordingId = "rec-A",
            };

            Assert.False(ParsekFlight.ShouldRaiseExternalActivationGate(
                ghostActive: false,
                recordingId: "rec-A",
                marker: marker,
                hasResolvableAnchorData: true,
                offsetApplied: true));
        }

        [Fact]
        public void IsRecordingInReFlyTreeMarker_NullRecordingOrMarker_ReturnsFalse()
        {
            Assert.False(ParsekFlight.IsRecordingInReFlyTreeMarker(null, null));
            Assert.False(ParsekFlight.IsRecordingInReFlyTreeMarker(
                "rec-A", new ReFlySessionMarker { TreeId = null }));
            Assert.False(ParsekFlight.IsRecordingInReFlyTreeMarker(
                "rec-A", new ReFlySessionMarker { TreeId = "tree-1" }));
            // Recording does not exist → returns false.
        }

        [Fact]
        public void IsRecordingInReFlyTreeMarker_RecordingTreeMatches_ReturnsTrue()
        {
            var tree = MakeTreeWithRecording(treeId: "tree-1", recordingId: "rec-A");
            RecordingStore.CommittedTrees.Add(tree);

            Assert.True(ParsekFlight.IsRecordingInReFlyTreeMarker(
                "rec-A", new ReFlySessionMarker { TreeId = "tree-1" }));
        }

        // ============================================================
        // Frozen Re-Fly display alignment
        // ============================================================

        [Fact]
        public void ReFlyDisplayAlignment_CaptureAndProjectSameRotation_RecoversInitialDelta()
        {
            Quaternion bodyRotation = TrajectoryMath.PureAngleAxis(35f, Vector3.up);
            Vector3d recorded = new Vector3d(1000.0, 2000.0, -3000.0);
            Vector3d initialDelta = new Vector3d(12.5, -3.0, 44.0);
            Vector3d live = recorded + initialDelta;

            bool captured = ReFlyDisplayAlignment.TryCapture(
                "sess-1",
                "tree-1",
                "rec-ghost",
                "Kerbin",
                bodyRotation,
                live,
                recorded,
                123.45,
                "root-part",
                3087746488u,
                8.18,
                out ReFlyDisplayAlignment alignment);

            Assert.True(captured);
            Assert.True(alignment.TryProject(bodyRotation, out Vector3d projected));
            AssertVectorClose(initialDelta, projected, 0.0001);
            Assert.Equal("sess-1", alignment.SessionId);
            Assert.Equal("rec-ghost", alignment.RecordingId);
            Assert.Equal("Kerbin", alignment.BodyName);
            Assert.Equal("root-part", alignment.LiveAnchorSource);
            Assert.Equal(3087746488u, alignment.LiveAnchorPartPid);
        }

        [Fact]
        public void ReFlyDisplayAlignment_ProjectAfterBodyRotation_DoesNotKeepStaleWorldVector()
        {
            Quaternion captureRotation = Quaternion.identity;
            Quaternion laterRotation = TrajectoryMath.PureAngleAxis(90f, Vector3.up);
            Vector3d recorded = Vector3d.zero;
            Vector3d live = new Vector3d(10.0, 0.0, 0.0);

            Assert.True(ReFlyDisplayAlignment.TryCapture(
                "sess-1",
                "tree-1",
                "rec-ghost",
                "Kerbin",
                captureRotation,
                live,
                recorded,
                10.0,
                "root-part",
                1u,
                0.0,
                out ReFlyDisplayAlignment alignment));

            Assert.True(alignment.TryProject(laterRotation, out Vector3d projected));
            Vector3 expected = TrajectoryMath.PureRotateVector(laterRotation, new Vector3(10f, 0f, 0f));
            AssertVectorClose(new Vector3d(expected.x, expected.y, expected.z), projected, 0.0001);
            Assert.True(Vector3d.Distance(live, projected) > 1.0);
        }

        [Fact]
        public void ReFlyDisplayAlignment_CaptureRejectsNonFiniteInputs()
        {
            bool captured = ReFlyDisplayAlignment.TryCapture(
                "sess-1",
                "tree-1",
                "rec-ghost",
                "Kerbin",
                Quaternion.identity,
                new Vector3d(double.NaN, 0.0, 0.0),
                Vector3d.zero,
                0.0,
                "root-part",
                1u,
                0.0,
                out ReFlyDisplayAlignment alignment);

            Assert.False(captured);
            Assert.Null(alignment.SessionId);
            Assert.Null(alignment.RecordingId);
        }

        [Fact]
        public void ReFlyDisplayAlignmentCache_SessionChangeClearsFrozenOffsets()
        {
            var cache = new ReFlyDisplayAlignmentCache();
            var alignment = new ReFlyDisplayAlignment
            {
                SessionId = "sess-1",
                TreeId = "tree-1",
                RecordingId = "rec-ghost",
                BodyName = "Kerbin",
                BodyFixedOffset = new Vector3d(1.0, 2.0, 3.0),
            };

            cache.ClearIfSessionChanged("sess-1");
            cache.Store(alignment);

            Assert.True(cache.TryGet("sess-1", "rec-ghost", out ReFlyDisplayAlignment found));
            AssertVectorClose(new Vector3d(1.0, 2.0, 3.0), found.BodyFixedOffset, 0.0001);

            cache.ClearIfSessionChanged("sess-1");
            Assert.Equal(1, cache.Count);

            cache.ClearIfSessionChanged("sess-2");
            Assert.Equal(0, cache.Count);
            Assert.False(cache.TryGet("sess-1", "rec-ghost", out _));
        }

        [Fact]
        public void ReFlyDisplayAlignmentCache_StoreNewSessionClearsPriorOffsets()
        {
            var cache = new ReFlyDisplayAlignmentCache();
            cache.Store(new ReFlyDisplayAlignment
            {
                SessionId = "sess-1",
                RecordingId = "rec-a",
                BodyName = "Kerbin",
                BodyFixedOffset = new Vector3d(1.0, 0.0, 0.0),
            });

            cache.Store(new ReFlyDisplayAlignment
            {
                SessionId = "sess-2",
                RecordingId = "rec-b",
                BodyName = "Kerbin",
                BodyFixedOffset = new Vector3d(2.0, 0.0, 0.0),
            });

            Assert.Equal(1, cache.Count);
            Assert.False(cache.TryGet("sess-1", "rec-a", out _));
            Assert.True(cache.TryGet("sess-2", "rec-b", out ReFlyDisplayAlignment found));
            AssertVectorClose(new Vector3d(2.0, 0.0, 0.0), found.BodyFixedOffset, 0.0001);
        }

        [Fact]
        public void ReFlyDisplayAlignmentCache_ScopeChangeClearsFrozenOffsets()
        {
            var cache = new ReFlyDisplayAlignmentCache();
            cache.ClearIfScopeChanged("sess-1", "scope-a");
            cache.Store(new ReFlyDisplayAlignment
            {
                SessionId = "sess-1",
                RecordingId = "rec-a",
                BodyName = "Kerbin",
                BodyFixedOffset = new Vector3d(1.0, 0.0, 0.0),
            });

            Assert.True(cache.TryGet("sess-1", "rec-a", out _));

            cache.ClearIfScopeChanged("sess-1", "scope-b");

            Assert.Equal(0, cache.Count);
            Assert.False(cache.TryGet("sess-1", "rec-a", out _));
        }

        [Fact]
        public void ReFlyDisplayAlignmentCache_RemoveDeletesOnlyMatchingSessionRecording()
        {
            var cache = new ReFlyDisplayAlignmentCache();
            cache.ClearIfScopeChanged("sess-1", "scope-a");
            cache.Store(new ReFlyDisplayAlignment
            {
                SessionId = "sess-1",
                RecordingId = "rec-a",
                BodyName = "Kerbin",
                BodyFixedOffset = new Vector3d(1.0, 0.0, 0.0),
            });
            cache.Store(new ReFlyDisplayAlignment
            {
                SessionId = "sess-1",
                RecordingId = "rec-b",
                BodyName = "Kerbin",
                BodyFixedOffset = new Vector3d(2.0, 0.0, 0.0),
            });

            Assert.False(cache.Remove("sess-other", "rec-a"));
            Assert.True(cache.Remove("sess-1", "rec-a"));
            Assert.False(cache.TryGet("sess-1", "rec-a", out _));
            Assert.True(cache.TryGet("sess-1", "rec-b", out _));
        }

        [Fact]
        public void BuildReFlyDisplayAlignmentScopeKey_ChangesWhenRetryMarkerFieldsChange()
        {
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess-1",
                TreeId = "tree-1",
                ActiveReFlyRecordingId = "active",
                OriginChildRecordingId = "origin",
                SupersedeTargetId = "target",
                RewindPointId = "rp-a",
                SelectedRootPartPersistentId = 123u,
                InvokedUT = 10.0,
            };

            string first = ParsekFlight.BuildReFlyDisplayAlignmentScopeKey(marker);
            marker.RewindPointId = "rp-b";
            string second = ParsekFlight.BuildReFlyDisplayAlignmentScopeKey(marker);
            marker.RewindPointId = "rp-a";
            marker.InvokedUT = 11.0;
            string third = ParsekFlight.BuildReFlyDisplayAlignmentScopeKey(marker);

            Assert.NotNull(first);
            Assert.NotEqual(first, second);
            Assert.NotEqual(first, third);
        }

        [Fact]
        public void IsSuspiciousReFlyDisplayAlignmentOffset_OnlyFlagsFiniteLargeOffsets()
        {
            Assert.False(ParsekFlight.IsSuspiciousReFlyDisplayAlignmentOffset(double.NaN));
            Assert.False(ParsekFlight.IsSuspiciousReFlyDisplayAlignmentOffset(double.PositiveInfinity));
            Assert.False(ParsekFlight.IsSuspiciousReFlyDisplayAlignmentOffset(
                ParsekFlight.ReFlyDisplayAlignmentSuspiciousOffsetMeters));
            Assert.True(ParsekFlight.IsSuspiciousReFlyDisplayAlignmentOffset(
                ParsekFlight.ReFlyDisplayAlignmentSuspiciousOffsetMeters + 0.01));
        }

        [Fact]
        public void ReFlyDisplayAlignmentBodyMatches_RequiresSameNonEmptyBodyName()
        {
            Assert.True(ParsekFlight.ReFlyDisplayAlignmentBodyMatches("Kerbin", "Kerbin"));
            Assert.False(ParsekFlight.ReFlyDisplayAlignmentBodyMatches("Kerbin", "Mun"));
            Assert.False(ParsekFlight.ReFlyDisplayAlignmentBodyMatches(null, "Kerbin"));
            Assert.False(ParsekFlight.ReFlyDisplayAlignmentBodyMatches("Kerbin", ""));
        }

        // ============================================================
        // HasResolvableReFlyAnchorData (#688 safety-net branch)
        // ============================================================

        [Fact]
        public void HasResolvableAnchorData_NullOrEmptyMarker_ReturnsFalse()
        {
            Assert.False(ParsekFlight.HasResolvableReFlyAnchorData(null, currentUT: 0.0));
            Assert.False(ParsekFlight.HasResolvableReFlyAnchorData(
                new ReFlySessionMarker { ActiveReFlyRecordingId = null }, currentUT: 0.0));
            Assert.False(ParsekFlight.HasResolvableReFlyAnchorData(
                new ReFlySessionMarker { ActiveReFlyRecordingId = "" }, currentUT: 0.0));
        }

        [Fact]
        public void HasResolvableAnchorData_RecordingMissing_ReturnsFalse()
        {
            // Empty store → committed-tree walk finds no recording.
            Assert.False(ParsekFlight.HasResolvableReFlyAnchorData(
                new ReFlySessionMarker
                {
                    SessionId = "sess",
                    TreeId = "tree-1",
                    ActiveReFlyRecordingId = "rec-missing",
                },
                currentUT: 0.0));
        }

        [Fact]
        public void HasResolvableAnchorData_NonInPlaceEmptyProvisional_ReturnsFalse()
        {
            // Non-in-place Re-Fly captures no snapshot and the fresh
            // provisional has no track sections — safety-net target.
            var tree = new RecordingTree
            {
                Id = "tree-1",
                TreeName = "tree-1",
                RootRecordingId = "rec-empty",
            };
            tree.Recordings["rec-empty"] = new Recording
            {
                RecordingId = "rec-empty",
                TreeId = "tree-1",
                VesselName = "Empty Provisional",
            };
            RecordingStore.CommittedTrees.Add(tree);

            Assert.False(ParsekFlight.HasResolvableReFlyAnchorData(
                new ReFlySessionMarker
                {
                    SessionId = "sess",
                    TreeId = "tree-1",
                    ActiveReFlyRecordingId = "rec-empty",
                },
                currentUT: 0.0));
        }

        [Fact]
        public void HasResolvableAnchorData_SnapshotMatchesSession_ReturnsTrue()
        {
            // In-place Re-Fly with captured snapshot. The snapshot must
            // contain at least one sampleable track section (the predicate
            // mirrors TrySampleRecordedAbsoluteWorld's contract — points
            // alone do not satisfy it).
            var tree = new RecordingTree
            {
                Id = "tree-1",
                TreeName = "tree-1",
                RootRecordingId = "rec-with-snap",
            };
            var rec = new Recording
            {
                RecordingId = "rec-with-snap",
                TreeId = "tree-1",
                VesselName = "Origin",
            };
            rec.Points.Add(new TrajectoryPoint { ut = 1.0 });
            rec.TrackSections.Add(MakeAbsoluteSectionWithFrame(0, 5));
            rec.CapturePreReFlyAnchorTrajectory("sess");
            tree.Recordings["rec-with-snap"] = rec;
            RecordingStore.CommittedTrees.Add(tree);

            Assert.True(ParsekFlight.HasResolvableReFlyAnchorData(
                new ReFlySessionMarker
                {
                    SessionId = "sess",
                    TreeId = "tree-1",
                    ActiveReFlyRecordingId = "rec-with-snap",
                },
                currentUT: 2.5));
        }

        [Fact]
        public void HasResolvableAnchorData_SnapshotPresentButUnsampleable_ReturnsFalse()
        {
            // Regression coverage for the #688 follow-up:
            // HasResolvableReFlyAnchorData previously returned true purely
            // on snapshot presence (`HasPreReFlyAnchorTrajectory`), but the
            // sampler rejects empty / shadowless sections. A snapshot whose
            // sections never sample cleanly would have raised the gate
            // forever, leaving the ghost permanently invisible. The
            // predicate must now reject this case.
            var tree = new RecordingTree
            {
                Id = "tree-1",
                TreeName = "tree-1",
                RootRecordingId = "rec-empty-snap",
            };
            var rec = new Recording
            {
                RecordingId = "rec-empty-snap",
                TreeId = "tree-1",
                VesselName = "Empty Snapshot",
            };
            rec.Points.Add(new TrajectoryPoint { ut = 1.0 });
            // Only a checkpoint section — sampler rejects all checkpoint
            // sections, so the snapshot is effectively unsampleable.
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = 0, endUT = 5,
            });
            rec.CapturePreReFlyAnchorTrajectory("sess");
            tree.Recordings["rec-empty-snap"] = rec;
            RecordingStore.CommittedTrees.Add(tree);

            Assert.False(ParsekFlight.HasResolvableReFlyAnchorData(
                new ReFlySessionMarker
                {
                    SessionId = "sess",
                    TreeId = "tree-1",
                    ActiveReFlyRecordingId = "rec-empty-snap",
                },
                currentUT: 2.5));
        }

        [Fact]
        public void HasResolvableAnchorData_SnapshotPresentButUnsampleable_DoesNotFallThroughToLive()
        {
            // Regression coverage for the second-pass review: even when
            // the live recording's TrackSections are sampleable, the
            // sampler does NOT fall through from an unsampleable snapshot
            // — it commits to the snapshot. The predicate must match.
            // Otherwise the gate would say "data is resolvable" while the
            // sampler permanently fails, raising the gate forever.
            var tree = new RecordingTree
            {
                Id = "tree-1",
                TreeName = "tree-1",
                RootRecordingId = "rec-snap-vs-live",
            };
            var rec = new Recording
            {
                RecordingId = "rec-snap-vs-live",
                TreeId = "tree-1",
                VesselName = "Unsampleable Snapshot, Sampleable Live",
            };
            // Live TrackSections are perfectly sampleable.
            rec.TrackSections.Add(MakeAbsoluteSectionWithFrame(0, 5));
            // Capture the snapshot from the current state, then mutate
            // the snapshot's track-section list to be unsampleable
            // (only an OrbitalCheckpoint section).
            rec.CapturePreReFlyAnchorTrajectory("sess");
            rec.PreReFlyAnchorTrackSections.Clear();
            rec.PreReFlyAnchorTrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = 0, endUT = 5,
            });
            tree.Recordings["rec-snap-vs-live"] = rec;
            RecordingStore.CommittedTrees.Add(tree);

            // Predicate must NOT report sampleable on the strength of the
            // live sections, because the sampler will be reading the
            // snapshot.
            Assert.False(ParsekFlight.HasResolvableReFlyAnchorData(
                new ReFlySessionMarker
                {
                    SessionId = "sess",
                    TreeId = "tree-1",
                    ActiveReFlyRecordingId = "rec-snap-vs-live",
                },
                currentUT: 2.5));
        }

        [Fact]
        public void HasResolvableAnchorData_SnapshotSessionMismatch_UsesLiveSections()
        {
            // Snapshot present but for a different session id — the
            // sampler treats this as "no snapshot" and falls back to
            // live, so the predicate must too.
            var tree = new RecordingTree
            {
                Id = "tree-1",
                TreeName = "tree-1",
                RootRecordingId = "rec-mismatch",
            };
            var rec = new Recording
            {
                RecordingId = "rec-mismatch",
                TreeId = "tree-1",
                VesselName = "Mismatch",
            };
            rec.Points.Add(new TrajectoryPoint { ut = 1.0 });
            rec.CapturePreReFlyAnchorTrajectory("sess-old");
            tree.Recordings["rec-mismatch"] = rec;
            RecordingStore.CommittedTrees.Add(tree);

            // No live track sections + sessionId mismatch ⇒ false.
            Assert.False(ParsekFlight.HasResolvableReFlyAnchorData(
                new ReFlySessionMarker
                {
                    SessionId = "sess-current",
                    TreeId = "tree-1",
                    ActiveReFlyRecordingId = "rec-mismatch",
                },
                currentUT: 2.5));

            // Add a sampleable live Absolute section ⇒ true.
            rec.TrackSections.Add(MakeAbsoluteSectionWithFrame(0, 5));
            Assert.True(ParsekFlight.HasResolvableReFlyAnchorData(
                new ReFlySessionMarker
                {
                    SessionId = "sess-current",
                    TreeId = "tree-1",
                    ActiveReFlyRecordingId = "rec-mismatch",
                },
                currentUT: 2.5));
        }

        [Fact]
        public void HasResolvableAnchorData_AbsoluteSectionWithoutFrames_ReturnsFalse()
        {
            // Regression coverage for the #688 follow-up: an Absolute
            // section with a null/empty frames list is structurally
            // present but the sampler rejects it. The predicate must
            // match the sampler's contract — otherwise the gate raises
            // and stays raised forever, leaving the ghost permanently
            // invisible.
            var tree = new RecordingTree
            {
                Id = "tree-1",
                TreeName = "tree-1",
                RootRecordingId = "rec-empty-abs",
            };
            var rec = new Recording
            {
                RecordingId = "rec-empty-abs",
                TreeId = "tree-1",
                VesselName = "Empty Absolute",
            };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 0, endUT = 5,
                frames = null, // structurally present, but no data
            });
            tree.Recordings["rec-empty-abs"] = rec;
            RecordingStore.CommittedTrees.Add(tree);

            Assert.False(ParsekFlight.HasResolvableReFlyAnchorData(
                new ReFlySessionMarker
                {
                    SessionId = "sess",
                    TreeId = "tree-1",
                    ActiveReFlyRecordingId = "rec-empty-abs",
                },
                currentUT: 2.5));

            // Also exercise the empty (non-null) frames list shape.
            rec.TrackSections[0] = new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 0, endUT = 5,
                frames = new List<TrajectoryPoint>(),
            };
            Assert.False(ParsekFlight.HasResolvableReFlyAnchorData(
                new ReFlySessionMarker
                {
                    SessionId = "sess",
                    TreeId = "tree-1",
                    ActiveReFlyRecordingId = "rec-empty-abs",
                },
                currentUT: 2.5));
        }

        [Fact]
        public void HasResolvableAnchorData_RelativeSectionWithoutShadow_ReturnsFalse()
        {
            // Regression coverage for the #688 follow-up: a Relative
            // section without an absoluteFrames shadow (legacy v6 format
            // or upgraded recording where the shadow was never written)
            // looks plausible to a section-presence-only check but is
            // structurally unsampleable for the Re-Fly display alignment model.
            var tree = new RecordingTree
            {
                Id = "tree-1",
                TreeName = "tree-1",
                RootRecordingId = "rec-rel-no-shadow",
            };
            var rec = new Recording
            {
                RecordingId = "rec-rel-no-shadow",
                TreeId = "tree-1",
                VesselName = "Relative without shadow",
            };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 0, endUT = 5,
                anchorVesselId = 12345u,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 1.0 },
                },
                absoluteFrames = null, // legacy / upgraded recording
            });
            tree.Recordings["rec-rel-no-shadow"] = rec;
            RecordingStore.CommittedTrees.Add(tree);

            Assert.False(ParsekFlight.HasResolvableReFlyAnchorData(
                new ReFlySessionMarker
                {
                    SessionId = "sess",
                    TreeId = "tree-1",
                    ActiveReFlyRecordingId = "rec-rel-no-shadow",
                },
                currentUT: 2.5));

            // Adding a v7 absolute-shadow frame restores sampleability.
            rec.TrackSections[0] = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 0, endUT = 5,
                anchorVesselId = 12345u,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 1.0 },
                },
                absoluteFrames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 1.0, latitude = 0.0, longitude = 0.0, altitude = 100.0, bodyName = "Kerbin" },
                },
            };
            Assert.True(ParsekFlight.HasResolvableReFlyAnchorData(
                new ReFlySessionMarker
                {
                    SessionId = "sess",
                    TreeId = "tree-1",
                    ActiveReFlyRecordingId = "rec-rel-no-shadow",
                },
                currentUT: 2.5));
        }

        [Fact]
        public void HasSampleableTrackSectionAtUT_OnlySectionAtUTMatters()
        {
            // The predicate must mirror TrySampleRecordedAbsoluteWorld's
            // section-at-UT selection: a list with sampleable sections
            // elsewhere does NOT satisfy the predicate when the section
            // selected for the current UT is unsampleable.
            var sections = new List<TrackSection>
            {
                // [0, 5] sampleable Absolute
                new TrackSection
                {
                    referenceFrame = ReferenceFrame.Absolute,
                    startUT = 0, endUT = 5,
                    frames = new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { ut = 0.0, bodyName = "Kerbin" },
                    },
                },
                // [5, 10] OrbitalCheckpoint (never sampleable)
                new TrackSection
                {
                    referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                    startUT = 5, endUT = 10,
                },
                // [10, 15] sampleable Relative with shadow
                new TrackSection
                {
                    referenceFrame = ReferenceFrame.Relative,
                    startUT = 10, endUT = 15,
                    frames = new List<TrajectoryPoint> { new TrajectoryPoint() },
                    absoluteFrames = new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { ut = 12.0, bodyName = "Kerbin" },
                    },
                },
            };

            // UT inside the early Absolute section ⇒ true.
            Assert.True(ParsekFlight.HasSampleableTrackSectionAtUT(sections, ut: 2.5));
            // UT inside the checkpoint section ⇒ FALSE even though
            // sampleable sections exist before AND after.
            Assert.False(ParsekFlight.HasSampleableTrackSectionAtUT(sections, ut: 7.5));
            // UT inside the Relative-with-shadow section ⇒ true.
            Assert.True(ParsekFlight.HasSampleableTrackSectionAtUT(sections, ut: 12.5));

            // Out-of-range UT clamps to nearest endpoint section.
            // Before the first section ⇒ uses sections[0] (sampleable Absolute) ⇒ true.
            Assert.True(ParsekFlight.HasSampleableTrackSectionAtUT(sections, ut: -10.0));
            // After the last section ⇒ uses sections[Count-1] (sampleable Relative) ⇒ true.
            Assert.True(ParsekFlight.HasSampleableTrackSectionAtUT(sections, ut: 1000.0));

            // Null / empty list short-circuits regardless of UT.
            Assert.False(ParsekFlight.HasSampleableTrackSectionAtUT(null, ut: 2.5));
            Assert.False(ParsekFlight.HasSampleableTrackSectionAtUT(new List<TrackSection>(), ut: 2.5));
        }

        [Fact]
        public void HasResolvableAnchorData_AbsoluteThenCheckpoint_FalseDuringCheckpoint()
        {
            // Regression coverage for the second-pass review: a recording
            // whose section at currentUT is OrbitalCheckpoint while
            // earlier sections are sampleable would have raised the gate
            // forever during the checkpoint window. The predicate must
            // mirror the sampler's section-at-UT selection.
            var tree = new RecordingTree
            {
                Id = "tree-1",
                TreeName = "tree-1",
                RootRecordingId = "rec-mixed",
            };
            var rec = new Recording
            {
                RecordingId = "rec-mixed",
                TreeId = "tree-1",
                VesselName = "Mixed Sections",
            };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 0, endUT = 5,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 0.0, bodyName = "Kerbin" },
                },
            });
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = 5, endUT = 100,
            });
            tree.Recordings["rec-mixed"] = rec;
            RecordingStore.CommittedTrees.Add(tree);

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess",
                TreeId = "tree-1",
                ActiveReFlyRecordingId = "rec-mixed",
            };

            // currentUT inside the early Absolute section ⇒ true.
            Assert.True(ParsekFlight.HasResolvableReFlyAnchorData(marker, currentUT: 2.5));
            // currentUT inside the checkpoint section ⇒ FALSE, even
            // though the list contains a sampleable Absolute earlier.
            Assert.False(ParsekFlight.HasResolvableReFlyAnchorData(marker, currentUT: 50.0));
        }

        [Fact]
        public void HasSampleableTrackSectionAtUT_OnlyUnsampleableSections_AlwaysFalse()
        {
            // A list whose only sections are unsampleable (no frames,
            // checkpoint-only) returns false at every UT — including UTs
            // that clamp to the nearest endpoint.
            var sections = new List<TrackSection>
            {
                new TrackSection
                {
                    referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                    startUT = 0, endUT = 5,
                },
                new TrackSection
                {
                    referenceFrame = ReferenceFrame.Absolute,
                    startUT = 5, endUT = 10,
                    frames = null,
                },
            };
            Assert.False(ParsekFlight.HasSampleableTrackSectionAtUT(sections, ut: -10.0));
            Assert.False(ParsekFlight.HasSampleableTrackSectionAtUT(sections, ut: 2.5));
            Assert.False(ParsekFlight.HasSampleableTrackSectionAtUT(sections, ut: 7.5));
            Assert.False(ParsekFlight.HasSampleableTrackSectionAtUT(sections, ut: 1000.0));
        }

        [Fact]
        public void HasResolvableAnchorData_OrbitalCheckpointOnlySections_ReturnsFalse()
        {
            // OrbitalCheckpoint sections are rejected by
            // TrySampleRecordedAbsoluteWorld, so the predicate must NOT
            // accept a recording whose only sections are checkpoint —
            // otherwise the gate would hold the ghost hidden every frame
            // the offset query returns recorded-pos-unavailable.
            var tree = new RecordingTree
            {
                Id = "tree-1",
                TreeName = "tree-1",
                RootRecordingId = "rec-checkpoint-only",
            };
            var rec = new Recording
            {
                RecordingId = "rec-checkpoint-only",
                TreeId = "tree-1",
                VesselName = "Checkpoint Only",
            };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = 0, endUT = 100,
            });
            tree.Recordings["rec-checkpoint-only"] = rec;
            RecordingStore.CommittedTrees.Add(tree);

            Assert.False(ParsekFlight.HasResolvableReFlyAnchorData(
                new ReFlySessionMarker
                {
                    SessionId = "sess",
                    TreeId = "tree-1",
                    ActiveReFlyRecordingId = "rec-checkpoint-only",
                },
                currentUT: 50.0));
        }

        [Fact]
        public void IsRecordingInReFlyTreeMarker_RecordingInDifferentTree_ReturnsFalse()
        {
            var tree1 = MakeTreeWithRecording(treeId: "tree-1", recordingId: "rec-A");
            var tree2 = MakeTreeWithRecording(treeId: "tree-2", recordingId: "rec-B");
            RecordingStore.CommittedTrees.Add(tree1);
            RecordingStore.CommittedTrees.Add(tree2);

            Assert.False(ParsekFlight.IsRecordingInReFlyTreeMarker(
                "rec-A", new ReFlySessionMarker { TreeId = "tree-2" }));
            Assert.True(ParsekFlight.IsRecordingInReFlyTreeMarker(
                "rec-A", new ReFlySessionMarker { TreeId = "tree-1" }));
        }

        // ============================================================
        // Chain-successor extension (#688 follow-up regression)
        // ============================================================
        //
        // The Re-Fly anchor sampler must walk chain successors when the
        // active recording is part of a chain (e.g. an optimizer-split
        // atmospheric / exo pair). Re-Fly's QuickloadTrimScope.ActiveRecOnly
        // trim leaves chain successors untouched, so their TrackSections
        // still hold the original recorded path past the active's split UT.
        // Without the chain walk, sampling at currentUT past the active's
        // last section clamps to the split UT, producing a stale anchor
        // and ghosts that visually drift 1-2 km from where they should be
        // at decouple time.

        [Fact]
        public void AppendReFlyAnchorChainSuccessorSections_NoChain_ReturnsPrimaryUnchanged()
        {
            var primary = new List<TrackSection> { MakeAbsoluteSectionWithFrame(0, 5) };
            var rec = new Recording
            {
                RecordingId = "rec-no-chain",
                ChainId = null,
                ChainIndex = -1,
            };
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess", TreeId = "tree-1", ActiveReFlyRecordingId = "rec-no-chain",
            };

            var result = ParsekFlight.AppendReFlyAnchorChainSuccessorSections(
                primary, rec, marker);

            // Identity-equal: no allocation when there's no chain.
            Assert.Same(primary, result);
        }

        [Fact]
        public void AppendReFlyAnchorChainSuccessorSections_ChainNoSuccessors_ReturnsPrimaryUnchanged()
        {
            var primary = new List<TrackSection> { MakeAbsoluteSectionWithFrame(0, 5) };
            var rec = new Recording
            {
                RecordingId = "rec-only-member",
                ChainId = "chain-A",
                ChainIndex = 0,
                TreeId = "tree-1",
            };
            // Single-member tree with the same chain — no successors.
            var tree = new RecordingTree
            {
                Id = "tree-1", TreeName = "tree-1", RootRecordingId = "rec-only-member",
            };
            tree.Recordings["rec-only-member"] = rec;
            RecordingStore.CommittedTrees.Add(tree);

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess", TreeId = "tree-1", ActiveReFlyRecordingId = "rec-only-member",
            };

            var result = ParsekFlight.AppendReFlyAnchorChainSuccessorSections(
                primary, rec, marker);

            Assert.Same(primary, result);
        }

        [Fact]
        public void AppendReFlyAnchorChainSuccessorSections_ChainWithSuccessor_AppendsSections()
        {
            // Optimizer-split scenario: 2706bf3a covers [279.53, 324.95]
            // (FirstHalf), 5b07d74b covers [324.95, 423.18] (SecondHalf).
            // Re-Fly invoked on FirstHalf; snapshot captured FirstHalf's
            // sections. At currentUT 326.71 (post-decouple), sampler must
            // route to the SecondHalf's section.
            var primary = new List<TrackSection> { MakeAbsoluteSectionWithFrame(279.53, 324.95) };

            var firstHalf = new Recording
            {
                RecordingId = "first-half", TreeId = "tree-1",
                ChainId = "chain-X", ChainIndex = 0,
            };
            var secondHalf = new Recording
            {
                RecordingId = "second-half", TreeId = "tree-1",
                ChainId = "chain-X", ChainIndex = 1,
            };
            secondHalf.TrackSections.Add(MakeAbsoluteSectionWithFrame(324.95, 423.18));

            var tree = new RecordingTree
            {
                Id = "tree-1", TreeName = "tree-1", RootRecordingId = "first-half",
            };
            tree.Recordings["first-half"] = firstHalf;
            tree.Recordings["second-half"] = secondHalf;
            RecordingStore.CommittedTrees.Add(tree);

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess", TreeId = "tree-1", ActiveReFlyRecordingId = "first-half",
            };

            var combined = ParsekFlight.AppendReFlyAnchorChainSuccessorSections(
                primary, firstHalf, marker);

            Assert.NotSame(primary, combined);
            Assert.Equal(2, combined.Count);
            Assert.Equal(279.53, combined[0].startUT);
            Assert.Equal(324.95, combined[1].startUT);

            // Section-at-UT lookup on the combined list must route to the
            // successor for the post-split UT.
            Assert.True(ParsekFlight.HasSampleableTrackSectionAtUT(combined, ut: 326.71));
            Assert.True(ParsekFlight.HasSampleableTrackSectionAtUT(combined, ut: 300.0));
        }

        [Fact]
        public void AppendReFlyAnchorChainSuccessorSections_MultipleSuccessors_SortedByChainIndex()
        {
            // Three-member chain in non-natural insertion order: assert the
            // appended sections come out sorted by ChainIndex regardless of
            // dictionary iteration order. UT order falls out of ChainIndex
            // order because chain segments are temporally contiguous.
            var primary = new List<TrackSection> { MakeAbsoluteSectionWithFrame(0, 10) };

            var member0 = new Recording
            {
                RecordingId = "m0", TreeId = "tree-1",
                ChainId = "chain", ChainIndex = 0,
            };
            var member1 = new Recording
            {
                RecordingId = "m1", TreeId = "tree-1",
                ChainId = "chain", ChainIndex = 1,
            };
            member1.TrackSections.Add(MakeAbsoluteSectionWithFrame(10, 20));
            var member2 = new Recording
            {
                RecordingId = "m2", TreeId = "tree-1",
                ChainId = "chain", ChainIndex = 2,
            };
            member2.TrackSections.Add(MakeAbsoluteSectionWithFrame(20, 30));

            var tree = new RecordingTree
            {
                Id = "tree-1", TreeName = "tree-1", RootRecordingId = "m0",
            };
            // Insert in scrambled order to challenge any insertion-order bias.
            tree.Recordings["m2"] = member2;
            tree.Recordings["m0"] = member0;
            tree.Recordings["m1"] = member1;
            RecordingStore.CommittedTrees.Add(tree);

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess", TreeId = "tree-1", ActiveReFlyRecordingId = "m0",
            };

            var combined = ParsekFlight.AppendReFlyAnchorChainSuccessorSections(
                primary, member0, marker);

            Assert.Equal(3, combined.Count);
            Assert.Equal(0.0, combined[0].startUT);
            Assert.Equal(10.0, combined[1].startUT);
            Assert.Equal(20.0, combined[2].startUT);
        }

        [Fact]
        public void AppendReFlyAnchorChainSuccessorSections_SuccessorInDifferentTree_NotAppended()
        {
            // ChainId is tree-local. A successor with a colliding ChainId
            // in a different tree must NOT be picked up — that would
            // anchor against an unrelated recording.
            var primary = new List<TrackSection> { MakeAbsoluteSectionWithFrame(0, 5) };

            var active = new Recording
            {
                RecordingId = "active", TreeId = "tree-1",
                ChainId = "chain-shared", ChainIndex = 0,
            };
            var foreignSuccessor = new Recording
            {
                RecordingId = "foreign", TreeId = "tree-2",
                ChainId = "chain-shared", ChainIndex = 1,
            };
            foreignSuccessor.TrackSections.Add(MakeAbsoluteSectionWithFrame(5, 10));

            var treeActive = new RecordingTree
            {
                Id = "tree-1", TreeName = "tree-1", RootRecordingId = "active",
            };
            treeActive.Recordings["active"] = active;
            var treeForeign = new RecordingTree
            {
                Id = "tree-2", TreeName = "tree-2", RootRecordingId = "foreign",
            };
            treeForeign.Recordings["foreign"] = foreignSuccessor;
            RecordingStore.CommittedTrees.Add(treeActive);
            RecordingStore.CommittedTrees.Add(treeForeign);

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess", TreeId = "tree-1", ActiveReFlyRecordingId = "active",
            };

            var result = ParsekFlight.AppendReFlyAnchorChainSuccessorSections(
                primary, active, marker);

            // Foreign-tree match ignored → primary returned untouched.
            Assert.Same(primary, result);
        }

        [Fact]
        public void AppendReFlyAnchorChainSuccessorSections_DifferentChainBranch_NotAppended()
        {
            // Same ChainId but different ChainBranch represents a parallel
            // ghost-only continuation (e.g. dock / undock branch-1
            // recordings) — independent vessel paths. Appending a
            // branch-1 recording's TrackSections into a branch-0 anchor
            // sample list would interleave a parallel vessel's trajectory
            // and produce Re-Fly offsets computed against the wrong
            // vessel path (1-2km off in the typical dock/undock split).
            // The same (TreeId, ChainId, ChainBranch) triplet scopes
            // EffectiveState.EnqueueChainSiblings; this helper mirrors
            // that contract.
            var primary = new List<TrackSection> { MakeAbsoluteSectionWithFrame(0, 5) };

            var active = new Recording
            {
                RecordingId = "active-branch0", TreeId = "tree-1",
                ChainId = "chain-shared", ChainIndex = 0, ChainBranch = 0,
            };
            // Same-branch successor: SHOULD be appended.
            var sameBranchSuccessor = new Recording
            {
                RecordingId = "succ-branch0", TreeId = "tree-1",
                ChainId = "chain-shared", ChainIndex = 1, ChainBranch = 0,
            };
            sameBranchSuccessor.TrackSections.Add(MakeAbsoluteSectionWithFrame(5, 10));
            // Parallel-branch recording with the same ChainId+ChainIndex
            // shape — represents a docked/undocked parallel path. MUST
            // NOT be appended. Use a distinct, far-away UT range so a
            // bug that lets it through would be visible in the test.
            var parallelBranch = new Recording
            {
                RecordingId = "parallel-branch1", TreeId = "tree-1",
                ChainId = "chain-shared", ChainIndex = 1, ChainBranch = 1,
            };
            parallelBranch.TrackSections.Add(MakeAbsoluteSectionWithFrame(1000, 2000));

            var tree = new RecordingTree
            {
                Id = "tree-1", TreeName = "tree-1", RootRecordingId = "active-branch0",
            };
            tree.Recordings["active-branch0"] = active;
            tree.Recordings["succ-branch0"] = sameBranchSuccessor;
            tree.Recordings["parallel-branch1"] = parallelBranch;
            RecordingStore.CommittedTrees.Add(tree);

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess", TreeId = "tree-1", ActiveReFlyRecordingId = "active-branch0",
            };

            var combined = ParsekFlight.AppendReFlyAnchorChainSuccessorSections(
                primary, active, marker);

            // Only the same-branch successor's section appended; the
            // parallel-branch [1000, 2000] section must not appear.
            Assert.Equal(2, combined.Count);
            Assert.Equal(0.0, combined[0].startUT);
            Assert.Equal(5.0, combined[1].startUT);
            Assert.DoesNotContain(combined, s => s.startUT >= 1000.0);
        }

        [Fact]
        public void AppendReFlyAnchorChainSuccessorSections_ActiveIsBranch1_AppendsBranch1Successor()
        {
            // Symmetric coverage: when the active recording is itself on
            // a parallel branch (ChainBranch=1), only branch-1
            // successors must be appended; branch-0 successors with the
            // same ChainId stay independent.
            var primary = new List<TrackSection> { MakeAbsoluteSectionWithFrame(0, 5) };

            var active = new Recording
            {
                RecordingId = "active-branch1", TreeId = "tree-1",
                ChainId = "chain-shared", ChainIndex = 0, ChainBranch = 1,
            };
            var branch0Successor = new Recording
            {
                RecordingId = "succ-branch0", TreeId = "tree-1",
                ChainId = "chain-shared", ChainIndex = 1, ChainBranch = 0,
            };
            branch0Successor.TrackSections.Add(MakeAbsoluteSectionWithFrame(1000, 2000));
            var branch1Successor = new Recording
            {
                RecordingId = "succ-branch1", TreeId = "tree-1",
                ChainId = "chain-shared", ChainIndex = 1, ChainBranch = 1,
            };
            branch1Successor.TrackSections.Add(MakeAbsoluteSectionWithFrame(5, 10));

            var tree = new RecordingTree
            {
                Id = "tree-1", TreeName = "tree-1", RootRecordingId = "active-branch1",
            };
            tree.Recordings["active-branch1"] = active;
            tree.Recordings["succ-branch0"] = branch0Successor;
            tree.Recordings["succ-branch1"] = branch1Successor;
            RecordingStore.CommittedTrees.Add(tree);

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess", TreeId = "tree-1", ActiveReFlyRecordingId = "active-branch1",
            };

            var combined = ParsekFlight.AppendReFlyAnchorChainSuccessorSections(
                primary, active, marker);

            Assert.Equal(2, combined.Count);
            Assert.Equal(0.0, combined[0].startUT);
            Assert.Equal(5.0, combined[1].startUT);
            Assert.DoesNotContain(combined, s => s.startUT >= 1000.0);
        }

        [Fact]
        public void AppendReFlyAnchorChainSuccessorSections_PredecessorIgnored()
        {
            // Only ChainIndex strictly greater than active is a successor;
            // a chain member with smaller ChainIndex must not be appended
            // (would invert the UT order of the combined list).
            var primary = new List<TrackSection> { MakeAbsoluteSectionWithFrame(10, 20) };

            var active = new Recording
            {
                RecordingId = "active", TreeId = "tree-1",
                ChainId = "chain", ChainIndex = 1,
            };
            var predecessor = new Recording
            {
                RecordingId = "pred", TreeId = "tree-1",
                ChainId = "chain", ChainIndex = 0,
            };
            predecessor.TrackSections.Add(MakeAbsoluteSectionWithFrame(0, 10));

            var tree = new RecordingTree
            {
                Id = "tree-1", TreeName = "tree-1", RootRecordingId = "pred",
            };
            tree.Recordings["pred"] = predecessor;
            tree.Recordings["active"] = active;
            RecordingStore.CommittedTrees.Add(tree);

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess", TreeId = "tree-1", ActiveReFlyRecordingId = "active",
            };

            var result = ParsekFlight.AppendReFlyAnchorChainSuccessorSections(
                primary, active, marker);

            // Predecessor ignored → primary unchanged (no allocation).
            Assert.Same(primary, result);
        }

        [Fact]
        public void AppendReFlyAnchorChainSuccessorSections_SuccessorWithEmptySections_Skipped()
        {
            // A chain successor with no usable TrackSections (recently
            // truncated, never sampled, etc.) must not allocate an
            // unhelpful empty append; if it's the only successor, primary
            // is returned identity-equal.
            var primary = new List<TrackSection> { MakeAbsoluteSectionWithFrame(0, 5) };

            var active = new Recording
            {
                RecordingId = "active", TreeId = "tree-1",
                ChainId = "chain", ChainIndex = 0,
            };
            var emptySuccessor = new Recording
            {
                RecordingId = "empty-succ", TreeId = "tree-1",
                ChainId = "chain", ChainIndex = 1,
                // TrackSections list exists but is empty.
            };

            var tree = new RecordingTree
            {
                Id = "tree-1", TreeName = "tree-1", RootRecordingId = "active",
            };
            tree.Recordings["active"] = active;
            tree.Recordings["empty-succ"] = emptySuccessor;
            RecordingStore.CommittedTrees.Add(tree);

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess", TreeId = "tree-1", ActiveReFlyRecordingId = "active",
            };

            var result = ParsekFlight.AppendReFlyAnchorChainSuccessorSections(
                primary, active, marker);

            Assert.Same(primary, result);
        }

        [Fact]
        public void HasResolvableAnchorData_OptimizerSplitChain_TrueForSuccessorRange()
        {
            // End-to-end check that the predicate (and by mirror the
            // sampler) reports the post-split UT as resolvable when the
            // active recording's chain successor covers it. This is the
            // direct regression path for the user-reported bug:
            // upper-stage ghost spawning at decouple UT was getting placed
            // ~1.5km wrong because the sampler clamped to FirstHalf's
            // last UT instead of routing to SecondHalf.
            var firstHalf = new Recording
            {
                RecordingId = "first-half", TreeId = "tree-1",
                VesselName = "Probe Booster (FirstHalf)",
                ChainId = "chain-Probe", ChainIndex = 0,
            };
            firstHalf.TrackSections.Add(MakeAbsoluteSectionWithFrame(279.53, 324.95));
            firstHalf.CapturePreReFlyAnchorTrajectory("sess");

            var secondHalf = new Recording
            {
                RecordingId = "second-half", TreeId = "tree-1",
                VesselName = "Probe Booster (SecondHalf)",
                ChainId = "chain-Probe", ChainIndex = 1,
            };
            secondHalf.TrackSections.Add(MakeAbsoluteSectionWithFrame(324.95, 423.18));

            var tree = new RecordingTree
            {
                Id = "tree-1", TreeName = "tree-1", RootRecordingId = "first-half",
            };
            tree.Recordings["first-half"] = firstHalf;
            tree.Recordings["second-half"] = secondHalf;
            RecordingStore.CommittedTrees.Add(tree);

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess", TreeId = "tree-1", ActiveReFlyRecordingId = "first-half",
            };

            // currentUT inside FirstHalf snapshot range ⇒ true (snapshot covers it).
            Assert.True(ParsekFlight.HasResolvableReFlyAnchorData(marker, currentUT: 300.0));
            // currentUT past FirstHalf's last UT but inside SecondHalf's
            // range ⇒ true via chain successor extension. Without the
            // chain walk, the sampler would clamp to FirstHalf's 324.95
            // and the predicate would also clamp — but both would land on
            // a stale recorded position and the offset would be wrong by
            // the booster's ~1.5km of post-split motion.
            Assert.True(ParsekFlight.HasResolvableReFlyAnchorData(marker, currentUT: 326.71));
            Assert.True(ParsekFlight.HasResolvableReFlyAnchorData(marker, currentUT: 400.0));
        }

        // ============================================================
        // Helpers
        // ============================================================

        private static RecordingTree MakeTreeWithRecording(string treeId, string recordingId)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = treeId,
                RootRecordingId = recordingId,
            };
            var rec = new Recording
            {
                RecordingId = recordingId,
                VesselName = "Test Vessel",
                TreeId = treeId,
            };
            tree.Recordings[recordingId] = rec;
            return tree;
        }

        /// <summary>
        /// Builds a sampleable Absolute <see cref="TrackSection"/>: one
        /// frame at <paramref name="startUT"/> on Kerbin so
        /// <see cref="ParsekFlight.HasSampleableTrackSectionAtUT"/> returns
        /// true and <see cref="ParsekFlight.TrySampleRecordedAbsoluteWorld"/>
        /// can interpolate across the section bounds.
        /// </summary>
        private static TrackSection MakeAbsoluteSectionWithFrame(double startUT, double endUT)
        {
            return new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = startUT,
                endUT = endUT,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = startUT,
                        latitude = 0.0,
                        longitude = 0.0,
                        altitude = 100.0,
                        bodyName = "Kerbin",
                    },
                },
            };
        }

        private static void AssertVectorClose(Vector3d expected, Vector3d actual, double tolerance)
        {
            Assert.True(
                Math.Abs(expected.x - actual.x) <= tolerance
                && Math.Abs(expected.y - actual.y) <= tolerance
                && Math.Abs(expected.z - actual.z) <= tolerance,
                $"Expected ({expected.x:R}, {expected.y:R}, {expected.z:R}) "
                + $"but got ({actual.x:R}, {actual.y:R}, {actual.z:R})");
        }
    }
}
