using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the Re-Fly tree anchor lock helpers in
    /// <see cref="ParsekFlight"/>. The lock translates ghost positions in
    /// the active Re-Fly tree by the per-frame offset
    /// <c>live_active_world(now) - recorded_active_world(currentUT)</c> so
    /// each ghost sits at its recorded relative offset from where the live
    /// vessel actually is. The active recording's role is purely to define
    /// inter-vessel relative geometry; its absolute world coordinates are
    /// not the source of truth.
    ///
    /// <para>
    /// These tests exercise the parts that don't require a live KSP
    /// runtime: the recording-id → tree-id resolution path that decides
    /// which ghost positions get translated, the activation-gate decision
    /// rule, the snapshot lifecycle, and the codec round-trip. The
    /// per-frame offset compute itself is covered by the in-game test in
    /// <c>RuntimeTests</c>.
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
            // snapshot — otherwise the per-frame anchor falls back to the
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
        // HasResolvableReFlyAnchorData (#688 safety-net branch)
        // ============================================================

        [Fact]
        public void HasResolvableAnchorData_NullOrEmptyMarker_ReturnsFalse()
        {
            Assert.False(ParsekFlight.HasResolvableReFlyAnchorData(null));
            Assert.False(ParsekFlight.HasResolvableReFlyAnchorData(
                new ReFlySessionMarker { ActiveReFlyRecordingId = null }));
            Assert.False(ParsekFlight.HasResolvableReFlyAnchorData(
                new ReFlySessionMarker { ActiveReFlyRecordingId = "" }));
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
                }));
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
                }));
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
                }));
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
                }));
        }

        [Fact]
        public void HasResolvableAnchorData_SnapshotSessionMismatch_FallsThroughToTrackSections()
        {
            // Snapshot present but for a different session id — it should
            // not satisfy the predicate via the snapshot branch. The live
            // recording's track sections then determine the result.
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

            // No track sections + sessionId mismatch ⇒ false.
            Assert.False(ParsekFlight.HasResolvableReFlyAnchorData(
                new ReFlySessionMarker
                {
                    SessionId = "sess-current",
                    TreeId = "tree-1",
                    ActiveReFlyRecordingId = "rec-mismatch",
                }));

            // Add a sampleable Absolute section (frames non-empty) ⇒ true.
            rec.TrackSections.Add(MakeAbsoluteSectionWithFrame(0, 5));
            Assert.True(ParsekFlight.HasResolvableReFlyAnchorData(
                new ReFlySessionMarker
                {
                    SessionId = "sess-current",
                    TreeId = "tree-1",
                    ActiveReFlyRecordingId = "rec-mismatch",
                }));
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
                }));

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
                }));
        }

        [Fact]
        public void HasResolvableAnchorData_RelativeSectionWithoutShadow_ReturnsFalse()
        {
            // Regression coverage for the #688 follow-up: a Relative
            // section without an absoluteFrames shadow (legacy v6 format
            // or upgraded recording where the shadow was never written)
            // looks plausible to a section-presence-only check but is
            // structurally unsampleable for the per-frame anchor model.
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
                }));

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
                }));
        }

        [Fact]
        public void HasSampleableTrackSection_PicksFirstSampleableMatch()
        {
            // Mixed section list: only one sampleable section anywhere in
            // the list is enough.
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
            Assert.True(ParsekFlight.HasSampleableTrackSection(sections));

            // Drop the sampleable Relative section — list becomes empty
            // of usable sections.
            sections.RemoveAt(2);
            Assert.False(ParsekFlight.HasSampleableTrackSection(sections));

            // Null / empty list short-circuits.
            Assert.False(ParsekFlight.HasSampleableTrackSection(null));
            Assert.False(ParsekFlight.HasSampleableTrackSection(new List<TrackSection>()));
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
                }));
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
        /// <see cref="ParsekFlight.HasSampleableTrackSection"/> returns
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
    }
}
