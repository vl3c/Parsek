using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for <see cref="RecordingTreeSplitter.SplitOriginAtRewindUT"/>
    /// — the Re-Fly merge-time orchestrator that splits the origin recording at
    /// the rewind UT into a kept HEAD half and a TIP half that the fork will
    /// supersede. Plan §7a (Task A4) enumerates the 12 required test shapes;
    /// case 13 additionally exercises <c>RollBackInMemory</c> by direct invocation
    /// (the production catch-and-rollback path is exercised via an in-game test
    /// in Task A6).
    /// </summary>
    [Collection("Sequential")]
    public class RecordingTreeSplitterTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public RecordingTreeSplitterTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // ---------- Fixture helpers ----------------------------------------

        private static TrajectoryPoint PointAt(double ut, double altitude = 50000.0,
            string body = "Kerbin")
        {
            return new TrajectoryPoint
            {
                ut = ut,
                altitude = altitude,
                latitude = 0.0,
                longitude = 0.0,
                bodyName = body,
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
            };
        }

        /// <summary>
        /// Builds a single-section recording spanning [startUT, endUT] with a
        /// point at <paramref name="midUT"/> so <c>SplitAtSection</c>'s
        /// interpolation branch (Unity-runtime-only Slerp) is bypassed.
        /// </summary>
        private static Recording BuildRecording(string id, double startUT, double endUT,
            double midUT, string treeId, TerminalState? terminal = null)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = treeId,
                MergeState = MergeState.Immutable,
                TerminalStateValue = terminal,
            };
            rec.Points.Add(PointAt(startUT));
            if (midUT > startUT && midUT < endUT)
                rec.Points.Add(PointAt(midUT));
            rec.Points.Add(PointAt(endUT));
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = startUT,
                endUT = endUT,
                sampleRateHz = 1f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
                frames = midUT > startUT && midUT < endUT
                    ? new List<TrajectoryPoint>
                    {
                        PointAt(startUT),
                        PointAt(midUT),
                        PointAt(endUT),
                    }
                    : new List<TrajectoryPoint>
                    {
                        PointAt(startUT),
                        PointAt(endUT),
                    },
            });
            return rec;
        }

        /// <summary>
        /// Registers a recording (origin) under its own RecordingTree and
        /// installs both into <see cref="RecordingStore"/>. Returns the tree.
        /// </summary>
        private static RecordingTree InstallOriginInTree(Recording origin, string treeId,
            string rootId = null)
        {
            origin.TreeId = treeId;
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = treeId,
                RootRecordingId = rootId ?? origin.RecordingId,
                ActiveRecordingId = origin.RecordingId,
            };
            tree.AddOrReplaceRecording(origin);
            RecordingStore.AddCommittedInternal(origin);
            RecordingStore.AddCommittedTreeForTesting(tree);
            return tree;
        }

        private static ReFlySessionMarker BuildMarker(Recording origin, double rewindUT,
            string forkId = "rec_fork")
        {
            return new ReFlySessionMarker
            {
                SessionId = "sess_test",
                TreeId = origin.TreeId,
                ActiveReFlyRecordingId = forkId,
                OriginChildRecordingId = origin.RecordingId,
                SupersedeTargetId = origin.RecordingId,
                RewindPointUT = rewindUT,
                InvokedUT = rewindUT,
            };
        }

        private static Recording FindCommitted(string id)
        {
            var src = RecordingStore.CommittedRecordings;
            for (int i = 0; i < src.Count; i++)
            {
                if (src[i] != null && src[i].RecordingId == id) return src[i];
            }
            return null;
        }

        // =====================================================================
        // 1. Happy path
        // =====================================================================

        [Fact]
        public void SplitOriginAtRewindUT_OriginSpansRewindUT_SplitsHeadAndTip()
        {
            var origin = BuildRecording("rec_origin", startUT: 8.0, endUT: 53.0,
                midUT: 34.0, treeId: "tree_1",
                terminal: TerminalState.Destroyed);
            var tree = InstallOriginInTree(origin, "tree_1");
            var marker = BuildMarker(origin, rewindUT: 34.0);

            var result = RecordingTreeSplitter.SplitOriginAtRewindUT(marker, null);

            Assert.False(result.Skipped);
            Assert.Null(result.SkipReason);
            Assert.Equal("rec_origin", result.HeadRecordingId);
            Assert.NotNull(result.TipRecordingId);
            Assert.NotEqual("rec_origin", result.TipRecordingId);

            Recording head = FindCommitted("rec_origin");
            Recording tip = FindCommitted(result.TipRecordingId);
            Assert.NotNull(head);
            Assert.NotNull(tip);

            // UT bounds
            Assert.Equal(8.0, head.StartUT);
            Assert.Equal(34.0, head.EndUT);
            Assert.Equal(34.0, tip.StartUT);
            Assert.Equal(53.0, tip.EndUT);

            // Chain wiring: shared ChainId, head=0 tip=1.
            Assert.False(string.IsNullOrEmpty(head.ChainId));
            Assert.Equal(head.ChainId, tip.ChainId);
            Assert.Equal(0, head.ChainIndex);
            Assert.Equal(1, tip.ChainIndex);

            // Terminal state transferred to TIP via SplitAtSection.
            Assert.Null(head.TerminalStateValue);
            Assert.Equal(TerminalState.Destroyed, tip.TerminalStateValue);

            // Marker mutated.
            Assert.Equal(tip.RecordingId, marker.SupersedeTargetId);

            // FilesDirty flagged on both halves.
            Assert.True(head.FilesDirty);
            Assert.True(tip.FilesDirty);

            // Tree-dict membership.
            Assert.True(tree.Recordings.ContainsKey(head.RecordingId));
            Assert.True(tree.Recordings.ContainsKey(tip.RecordingId));

            Assert.Contains(logLines, l =>
                l.Contains("[Splitter]")
                && l.Contains("Split origin rec_origin at UT=34.00")
                && l.Contains("HEAD=[8.00..34.00]")
                && l.Contains($"TIP={tip.RecordingId}"));
        }

        // =====================================================================
        // 2. Origin entirely pre-rewind -> Skipped
        // =====================================================================

        [Fact]
        public void SplitOriginAtRewindUT_OriginEntirelyPreRewind_NoSplit()
        {
            var origin = BuildRecording("rec_pre", 8.0, 30.0, midUT: double.NaN,
                treeId: "tree_2");
            InstallOriginInTree(origin, "tree_2");
            var marker = BuildMarker(origin, rewindUT: 34.0);
            string markerTargetBefore = marker.SupersedeTargetId;
            int committedCountBefore = RecordingStore.CommittedRecordings.Count;

            var result = RecordingTreeSplitter.SplitOriginAtRewindUT(marker, null);

            Assert.True(result.Skipped);
            Assert.Equal("OriginDoesNotSpanRewindUT", result.SkipReason);
            Assert.Equal(committedCountBefore, RecordingStore.CommittedRecordings.Count);
            Assert.Equal(markerTargetBefore, marker.SupersedeTargetId);
        }

        // =====================================================================
        // 3. Origin entirely post-rewind -> Skipped
        // =====================================================================

        [Fact]
        public void SplitOriginAtRewindUT_OriginEntirelyPostRewind_NoSplit()
        {
            var origin = BuildRecording("rec_post", 40.0, 53.0, midUT: double.NaN,
                treeId: "tree_3");
            InstallOriginInTree(origin, "tree_3");
            var marker = BuildMarker(origin, rewindUT: 34.0);
            int committedCountBefore = RecordingStore.CommittedRecordings.Count;

            var result = RecordingTreeSplitter.SplitOriginAtRewindUT(marker, null);

            Assert.True(result.Skipped);
            Assert.Equal("OriginDoesNotSpanRewindUT", result.SkipReason);
            Assert.Equal(committedCountBefore, RecordingStore.CommittedRecordings.Count);
        }

        // =====================================================================
        // 4. NaN rewindUT -> Skipped
        // =====================================================================

        [Fact]
        public void SplitOriginAtRewindUT_NaNRewindUT_NoSplit()
        {
            var origin = BuildRecording("rec_nan", 8.0, 53.0, midUT: 34.0,
                treeId: "tree_4");
            InstallOriginInTree(origin, "tree_4");
            var marker = BuildMarker(origin, rewindUT: double.NaN);

            var result = RecordingTreeSplitter.SplitOriginAtRewindUT(marker, null);

            Assert.True(result.Skipped);
            Assert.Equal("RewindPointUTUnset", result.SkipReason);
            Assert.Contains(logLines, l =>
                l.Contains("[Splitter]") && l.Contains("RewindPointUT is NaN"));
        }

        // =====================================================================
        // 5. BranchPoints reparented
        // =====================================================================

        [Fact]
        public void SplitOriginAtRewindUT_BPsReparented()
        {
            var origin = BuildRecording("rec_origin", 8.0, 53.0, midUT: 34.0,
                treeId: "tree_5", terminal: TerminalState.Destroyed);
            var tree = InstallOriginInTree(origin, "tree_5");

            var bpEarly = new BranchPoint
            {
                Id = "bp_early",
                Type = BranchPointType.Breakup,
                UT = 20.0,
                ParentRecordingIds = new List<string> { origin.RecordingId },
            };
            var bpEdge = new BranchPoint
            {
                Id = "bp_edge",
                Type = BranchPointType.Breakup,
                UT = 34.0,
                ParentRecordingIds = new List<string> { origin.RecordingId },
            };
            var bpLate = new BranchPoint
            {
                Id = "bp_late",
                Type = BranchPointType.Breakup,
                UT = 40.0,
                ParentRecordingIds = new List<string> { origin.RecordingId },
            };
            tree.BranchPoints.Add(bpEarly);
            tree.BranchPoints.Add(bpEdge);
            tree.BranchPoints.Add(bpLate);

            var marker = BuildMarker(origin, rewindUT: 34.0);

            var result = RecordingTreeSplitter.SplitOriginAtRewindUT(marker, null);

            Assert.False(result.Skipped);
            Recording tip = FindCommitted(result.TipRecordingId);

            // bp_early stays on HEAD.
            Assert.Contains(origin.RecordingId, bpEarly.ParentRecordingIds);
            Assert.DoesNotContain(tip.RecordingId, bpEarly.ParentRecordingIds);

            // bp_edge (UT == rewindUT) reparents to TIP per Step 2.6 edge rule.
            Assert.DoesNotContain(origin.RecordingId, bpEdge.ParentRecordingIds);
            Assert.Contains(tip.RecordingId, bpEdge.ParentRecordingIds);

            // bp_late reparents to TIP.
            Assert.DoesNotContain(origin.RecordingId, bpLate.ParentRecordingIds);
            Assert.Contains(tip.RecordingId, bpLate.ParentRecordingIds);

            // Counter (2 = edge + late; early stayed on HEAD).
            Assert.Equal(2, result.BpReparented);
        }

        // =====================================================================
        // 6. Debris reparented
        // =====================================================================

        [Fact]
        public void SplitOriginAtRewindUT_DebrisReparented()
        {
            var origin = BuildRecording("rec_origin", 8.0, 53.0, midUT: 34.0,
                treeId: "tree_6", terminal: TerminalState.Destroyed);
            var tree = InstallOriginInTree(origin, "tree_6");

            // Two pre-rewind, two post-rewind debris.
            var debrisPre1 = BuildDebrisRecording("d_pre1", origin.RecordingId, startUT: 22.0,
                endUT: 30.0, treeId: "tree_6");
            var debrisPre2 = BuildDebrisRecording("d_pre2", origin.RecordingId, startUT: 22.5,
                endUT: 32.0, treeId: "tree_6");
            var debrisPost1 = BuildDebrisRecording("d_post1", origin.RecordingId, startUT: 40.0,
                endUT: 52.0, treeId: "tree_6");
            var debrisPost2 = BuildDebrisRecording("d_post2", origin.RecordingId, startUT: 42.0,
                endUT: 50.0, treeId: "tree_6");
            tree.AddOrReplaceRecording(debrisPre1);
            tree.AddOrReplaceRecording(debrisPre2);
            tree.AddOrReplaceRecording(debrisPost1);
            tree.AddOrReplaceRecording(debrisPost2);
            RecordingStore.AddCommittedInternal(debrisPre1);
            RecordingStore.AddCommittedInternal(debrisPre2);
            RecordingStore.AddCommittedInternal(debrisPost1);
            RecordingStore.AddCommittedInternal(debrisPost2);

            var marker = BuildMarker(origin, rewindUT: 34.0);

            var result = RecordingTreeSplitter.SplitOriginAtRewindUT(marker, null);

            Assert.False(result.Skipped);
            Assert.Equal(2, result.DebrisReparented);
            Assert.Equal(origin.RecordingId, debrisPre1.DebrisParentRecordingId);
            Assert.Equal(origin.RecordingId, debrisPre2.DebrisParentRecordingId);
            Assert.Equal(result.TipRecordingId, debrisPost1.DebrisParentRecordingId);
            Assert.Equal(result.TipRecordingId, debrisPost2.DebrisParentRecordingId);
        }

        private static Recording BuildDebrisRecording(string id, string parentRecordingId,
            double startUT, double endUT, string treeId)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = treeId,
                IsDebris = true,
                DebrisParentRecordingId = parentRecordingId,
                MergeState = MergeState.Immutable,
            };
            rec.Points.Add(PointAt(startUT));
            rec.Points.Add(PointAt(endUT));
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = startUT,
                endUT = endUT,
                sampleRateHz = 1f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
                frames = new List<TrajectoryPoint>
                {
                    PointAt(startUT),
                    PointAt(endUT),
                },
            });
            return rec;
        }

        // =====================================================================
        // 7. Ledger action retag by UT
        // =====================================================================

        [Fact]
        public void SplitOriginAtRewindUT_LedgerActionsRetagged()
        {
            var origin = BuildRecording("rec_origin", 8.0, 53.0, midUT: 34.0,
                treeId: "tree_7", terminal: TerminalState.Destroyed);
            InstallOriginInTree(origin, "tree_7");

            var a10 = new GameAction
            {
                UT = 10.0,
                Type = GameActionType.FundsEarning,
                RecordingId = origin.RecordingId,
                FundsAwarded = 100f,
            };
            var a20 = new GameAction
            {
                UT = 20.0,
                Type = GameActionType.ScienceEarning,
                RecordingId = origin.RecordingId,
                ScienceAwarded = 5f,
            };
            var a50 = new GameAction
            {
                UT = 50.0,
                Type = GameActionType.ReputationPenalty,
                RecordingId = origin.RecordingId,
                NominalPenalty = 10f,
            };
            Ledger.AddAction(a10);
            Ledger.AddAction(a20);
            Ledger.AddAction(a50);

            int versionBefore = Ledger.StateVersion;

            var marker = BuildMarker(origin, rewindUT: 34.0);
            var result = RecordingTreeSplitter.SplitOriginAtRewindUT(marker, null);

            Assert.False(result.Skipped);
            Assert.Equal(origin.RecordingId, a10.RecordingId);
            Assert.Equal(origin.RecordingId, a20.RecordingId);
            Assert.Equal(result.TipRecordingId, a50.RecordingId);
            Assert.Equal(1, result.ActionsRetagged);
            Assert.True(Ledger.StateVersion > versionBefore,
                $"Expected Ledger.StateVersion bump after retag (before={versionBefore} after={Ledger.StateVersion})");
        }

        // =====================================================================
        // 8. Partial-resume idempotent
        // =====================================================================

        [Fact]
        public void SplitOriginAtRewindUT_PartialResumeIdempotent()
        {
            var origin = BuildRecording("rec_origin", 8.0, 53.0, midUT: 34.0,
                treeId: "tree_8", terminal: TerminalState.Destroyed);
            var tree = InstallOriginInTree(origin, "tree_8");

            // Add a late BP that should reparent to TIP.
            var bpLate = new BranchPoint
            {
                Id = "bp_late",
                Type = BranchPointType.Breakup,
                UT = 40.0,
                ParentRecordingIds = new List<string> { origin.RecordingId },
            };
            tree.BranchPoints.Add(bpLate);

            var marker = BuildMarker(origin, rewindUT: 34.0);

            // First call: split runs end-to-end.
            var first = RecordingTreeSplitter.SplitOriginAtRewindUT(marker, null);
            Assert.False(first.Skipped);
            string tipId = first.TipRecordingId;
            Assert.Equal(tipId, marker.SupersedeTargetId);
            Assert.Equal(1, first.BpReparented);
            Assert.DoesNotContain(origin.RecordingId, bpLate.ParentRecordingIds);
            Assert.Contains(tipId, bpLate.ParentRecordingIds);

            int committedCount = RecordingStore.CommittedRecordings.Count;

            // Second call: idempotent re-entry path detects the existing TIP
            // and replays the post-split steps without adding a duplicate.
            var second = RecordingTreeSplitter.SplitOriginAtRewindUT(marker, null);
            Assert.False(second.Skipped);
            Assert.Equal(tipId, second.TipRecordingId);
            // No new recording added.
            Assert.Equal(committedCount, RecordingStore.CommittedRecordings.Count);
            // Predicate no longer matches -> no new BP retag.
            Assert.Equal(0, second.BpReparented);
            // Marker still points at TIP.
            Assert.Equal(tipId, marker.SupersedeTargetId);

            Assert.Contains(logLines, l =>
                l.Contains("[Splitter]")
                && l.Contains("idempotent re-entry"));
        }

        // =====================================================================
        // 9. Orbit-segment straddles rewindUT -> split now succeeds (tail-clone)
        // =====================================================================

        [Fact]
        public void SplitOriginAtRewindUT_OrbitSegmentStraddlesRewindUT_SplitsCleanly()
        {
            // Task A7 removed the SplitAtUT orbit-segment-straddle guard.
            // SplitAtSection's OrbitSegments partition now tail-clones the
            // straddling segment into TIP at startUT=splitUT, so the splitter
            // completes cleanly and marker.SupersedeTargetId is mutated to TIP's
            // id (so AppendRelations writes a TIP -> fork row instead of
            // origin -> fork).
            //
            // Build the origin directly (not via BuildRecording) so the
            // TrackSection has null frames. HasCompleteTrackSectionPayloadForFlatSync
            // then fails and SplitAtSection's downstream TrySyncFlat does not
            // rebuild OrbitSegments from the (un-partitioned) OrbitalCheckpoint
            // section's checkpoints — which would undo the partition under test.
            // Also use isPredicted=true so the EnsureCheckpoint bridge does not
            // add an OrbitalCheckpoint section that would be re-sorted in front
            // of the synthetic boundary section, invalidating sectionIndex and
            // routing SplitAtSection into the Unity-only Slerp interpolation
            // branch (xUnit tests run outside the Unity runtime).
            var origin = new Recording
            {
                RecordingId = "rec_straddle",
                VesselName = "rec_straddle",
                TreeId = "tree_9",
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Destroyed,
            };
            origin.Points.Add(PointAt(8.0));
            origin.Points.Add(PointAt(34.0));
            origin.Points.Add(PointAt(53.0));
            origin.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 8.0,
                endUT = 53.0,
                sampleRateHz = 1f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
                frames = null,
            });
            var orbitalRot = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f);
            var angVel = new Vector3(0.01f, 0.02f, 0.03f);
            origin.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 20.0,
                endUT = 50.0,
                bodyName = "Kerbin",
                inclination = 12.5,
                eccentricity = 0.123,
                semiMajorAxis = 700000.0,
                longitudeOfAscendingNode = 45.0,
                argumentOfPeriapsis = 90.0,
                meanAnomalyAtEpoch = 1.5,
                epoch = 20.0,
                isPredicted = true,
                orbitalFrameRotation = orbitalRot,
                angularVelocity = angVel,
            });
            InstallOriginInTree(origin, "tree_9");

            var marker = BuildMarker(origin, rewindUT: 34.0);

            var result = RecordingTreeSplitter.SplitOriginAtRewindUT(marker, null);

            // Split completes (no fallback).
            Assert.False(result.Skipped);
            Assert.Null(result.SkipReason);
            Assert.Equal("rec_straddle", result.HeadRecordingId);
            Assert.NotNull(result.TipRecordingId);
            Assert.NotEqual("rec_straddle", result.TipRecordingId);

            Recording head = FindCommitted("rec_straddle");
            Recording tip = FindCommitted(result.TipRecordingId);
            Assert.NotNull(head);
            Assert.NotNull(tip);

            // HEAD keeps the head-trimmed segment [20, 34].
            Assert.Single(head.OrbitSegments);
            Assert.Equal(20.0, head.OrbitSegments[0].startUT);
            Assert.Equal(34.0, head.OrbitSegments[0].endUT);

            // TIP carries the tail-clone [34, 50] with identical Kepler elements.
            Assert.Single(tip.OrbitSegments);
            var tipSeg = tip.OrbitSegments[0];
            Assert.Equal(34.0, tipSeg.startUT);
            Assert.Equal(50.0, tipSeg.endUT);
            Assert.Equal("Kerbin", tipSeg.bodyName);
            Assert.Equal(12.5, tipSeg.inclination);
            Assert.Equal(0.123, tipSeg.eccentricity);
            Assert.Equal(700000.0, tipSeg.semiMajorAxis);
            Assert.Equal(45.0, tipSeg.longitudeOfAscendingNode);
            Assert.Equal(90.0, tipSeg.argumentOfPeriapsis);
            Assert.Equal(1.5, tipSeg.meanAnomalyAtEpoch);
            Assert.Equal(20.0, tipSeg.epoch);
            Assert.True(tipSeg.isPredicted);
            Assert.Equal(orbitalRot, tipSeg.orbitalFrameRotation);
            Assert.Equal(angVel, tipSeg.angularVelocity);

            // Marker mutated to TIP's id — AppendRelations will write TIP -> fork.
            Assert.Equal(tip.RecordingId, marker.SupersedeTargetId);
        }

        // =====================================================================
        // 10. EVA recording preserves linkage
        // =====================================================================

        [Fact]
        public void SplitOriginAtRewindUT_OriginIsEVARecording_SplitsAndPreservesEVALinkage()
        {
            var origin = BuildRecording("rec_eva", 8.0, 53.0, midUT: 34.0,
                treeId: "tree_10", terminal: TerminalState.Destroyed);
            origin.EvaCrewName = "Bob Kerman";
            origin.ParentRecordingId = "rec_parent_vessel";
            InstallOriginInTree(origin, "tree_10");

            var marker = BuildMarker(origin, rewindUT: 34.0);
            var result = RecordingTreeSplitter.SplitOriginAtRewindUT(marker, null);

            Assert.False(result.Skipped);
            Recording tip = FindCommitted(result.TipRecordingId);
            Recording head = FindCommitted(origin.RecordingId);
            Assert.NotNull(tip);

            Assert.Equal("Bob Kerman", head.EvaCrewName);
            Assert.Equal("Bob Kerman", tip.EvaCrewName);
            Assert.Equal("rec_parent_vessel", head.ParentRecordingId);
            Assert.Equal("rec_parent_vessel", tip.ParentRecordingId);
        }

        // =====================================================================
        // 11. ChildBranchPointId move rule
        // =====================================================================

        [Fact]
        public void SplitOriginAtRewindUT_ChildBranchPointIdMovesPerRewindUTRule_PreRewindStays()
        {
            var origin = BuildRecording("rec_origin", 8.0, 53.0, midUT: 34.0,
                treeId: "tree_11", terminal: TerminalState.Destroyed);
            var tree = InstallOriginInTree(origin, "tree_11");

            var bpPre = new BranchPoint
            {
                Id = "bp_pre",
                Type = BranchPointType.Undock,
                UT = 20.0, // pre-rewind
                ParentRecordingIds = new List<string> { origin.RecordingId },
            };
            tree.BranchPoints.Add(bpPre);
            origin.ChildBranchPointId = "bp_pre";

            var marker = BuildMarker(origin, rewindUT: 34.0);
            var result = RecordingTreeSplitter.SplitOriginAtRewindUT(marker, null);

            Assert.False(result.Skipped);
            Recording tip = FindCommitted(result.TipRecordingId);

            Assert.Equal("bp_pre", origin.ChildBranchPointId);
            Assert.Null(tip.ChildBranchPointId);
            // BP's ParentRecordingIds still references origin (HEAD).
            Assert.Contains(origin.RecordingId, bpPre.ParentRecordingIds);
        }

        [Fact]
        public void SplitOriginAtRewindUT_ChildBranchPointIdMovesPerRewindUTRule_AtOrAfterRewindMoves()
        {
            var origin = BuildRecording("rec_origin", 8.0, 53.0, midUT: 34.0,
                treeId: "tree_11b", terminal: TerminalState.Destroyed);
            var tree = InstallOriginInTree(origin, "tree_11b");

            var bpAt = new BranchPoint
            {
                Id = "bp_at",
                Type = BranchPointType.Breakup,
                UT = 34.0, // exactly at rewindUT
                ParentRecordingIds = new List<string> { origin.RecordingId },
            };
            tree.BranchPoints.Add(bpAt);
            origin.ChildBranchPointId = "bp_at";

            var marker = BuildMarker(origin, rewindUT: 34.0);
            var result = RecordingTreeSplitter.SplitOriginAtRewindUT(marker, null);

            Assert.False(result.Skipped);
            Recording tip = FindCommitted(result.TipRecordingId);

            Assert.Null(origin.ChildBranchPointId);
            Assert.Equal("bp_at", tip.ChildBranchPointId);
            // BP's ParentRecordingIds rewritten to TIP.
            Assert.Contains(tip.RecordingId, bpAt.ParentRecordingIds);
            Assert.DoesNotContain(origin.RecordingId, bpAt.ParentRecordingIds);
        }

        // =====================================================================
        // 12. Milestones retagged by UT
        // =====================================================================

        [Fact]
        public void SplitOriginAtRewindUT_MilestonesRetaggedByUT()
        {
            var origin = BuildRecording("rec_origin", 8.0, 53.0, midUT: 34.0,
                treeId: "tree_12", terminal: TerminalState.Destroyed);
            InstallOriginInTree(origin, "tree_12");

            var msPre1 = new Milestone
            {
                MilestoneId = "ms_pre1",
                StartUT = 0.0,
                EndUT = 9.0,
                RecordingId = origin.RecordingId,
                Committed = true,
                Events = new List<GameStateEvent>(),
            };
            var msPre2 = new Milestone
            {
                MilestoneId = "ms_pre2",
                StartUT = 9.0,
                EndUT = 22.0,
                RecordingId = origin.RecordingId,
                Committed = true,
                Events = new List<GameStateEvent>(),
            };
            var msPost = new Milestone
            {
                MilestoneId = "ms_post",
                StartUT = 40.0,
                EndUT = 52.0,
                RecordingId = origin.RecordingId,
                Committed = true,
                Events = new List<GameStateEvent>(),
            };
            MilestoneStore.AddMilestoneForTesting(msPre1);
            MilestoneStore.AddMilestoneForTesting(msPre2);
            MilestoneStore.AddMilestoneForTesting(msPost);

            bool timelineSignalFired = false;
            LedgerOrchestrator.OnTimelineDataChanged = () => timelineSignalFired = true;

            try
            {
                var marker = BuildMarker(origin, rewindUT: 34.0);
                var result = RecordingTreeSplitter.SplitOriginAtRewindUT(marker, null);

                Assert.False(result.Skipped);
                Assert.Equal(1, result.MilestonesRetagged);
                Assert.Equal(origin.RecordingId, msPre1.RecordingId);
                Assert.Equal(origin.RecordingId, msPre2.RecordingId);
                Assert.Equal(result.TipRecordingId, msPost.RecordingId);

                Assert.True(timelineSignalFired,
                    "Expected LedgerOrchestrator.OnTimelineDataChanged invocation after milestone retag");
            }
            finally
            {
                LedgerOrchestrator.OnTimelineDataChanged = null;
            }
        }

        // =====================================================================
        // 13. RollBackInMemory direct invocation
        //
        // Production code does not expose a clean throw-injection seam mid-split
        // (the catch path in `SplitOriginAtRewindUT` invokes RollBackInMemory
        // and re-throws — exercised end-to-end by the in-game acceptance test
        // in Task A6). Per the task spec's fallback option, this test builds a
        // SplitSnapshot manually, exercises the production split until partial
        // state exists, then calls RollBackInMemory directly and asserts every
        // inverse mutation took effect.
        // =====================================================================

        [Fact]
        public void SplitOriginAtRewindUT_RollBackInMemory_DirectInvocation_RestoresAllMutations()
        {
            // Build a scenario with origin, a late BP, post-rewind debris, a
            // post-rewind ledger action, and a post-rewind milestone.
            var origin = BuildRecording("rec_origin", 8.0, 53.0, midUT: 34.0,
                treeId: "tree_13", terminal: TerminalState.Destroyed);
            origin.ChainId = "chain_X";
            origin.ChainIndex = 0;
            var tree = InstallOriginInTree(origin, "tree_13");

            var bpLate = new BranchPoint
            {
                Id = "bp_late",
                Type = BranchPointType.Breakup,
                UT = 40.0,
                ParentRecordingIds = new List<string> { origin.RecordingId },
            };
            tree.BranchPoints.Add(bpLate);

            var debrisPost = BuildDebrisRecording("d_post", origin.RecordingId,
                startUT: 40.0, endUT: 52.0, treeId: "tree_13");
            tree.AddOrReplaceRecording(debrisPost);
            RecordingStore.AddCommittedInternal(debrisPost);

            var action50 = new GameAction
            {
                UT = 50.0,
                Type = GameActionType.ReputationPenalty,
                RecordingId = origin.RecordingId,
                NominalPenalty = 10f,
            };
            Ledger.AddAction(action50);
            int ledgerVersionBefore = Ledger.StateVersion;

            var msPost = new Milestone
            {
                MilestoneId = "ms_post",
                StartUT = 40.0,
                EndUT = 52.0,
                RecordingId = origin.RecordingId,
                Committed = true,
                Events = new List<GameStateEvent>(),
            };
            MilestoneStore.AddMilestoneForTesting(msPost);

            // Snapshot the pre-call state we'll assert restoration against.
            string originChildBpBefore = origin.ChildBranchPointId; // null
            int originChainIndexBefore = origin.ChainIndex;
            string debrisParentBefore = debrisPost.DebrisParentRecordingId;
            string actionTagBefore = action50.RecordingId;
            string msTagBefore = msPost.RecordingId;
            string markerTargetBefore = origin.RecordingId;
            int committedCountBefore = RecordingStore.CommittedRecordings.Count;
            int treeRecCountBefore = tree.Recordings.Count;

            // Run a real split end-to-end.
            var marker = BuildMarker(origin, rewindUT: 34.0);
            var result = RecordingTreeSplitter.SplitOriginAtRewindUT(marker, null);
            Assert.False(result.Skipped);
            string tipId = result.TipRecordingId;

            // Verify the post-split state is mutated as expected before rollback.
            Assert.Equal(tipId, marker.SupersedeTargetId);
            Assert.Equal(tipId, debrisPost.DebrisParentRecordingId);
            Assert.Equal(tipId, action50.RecordingId);
            Assert.Equal(tipId, msPost.RecordingId);
            Assert.Contains(tipId, bpLate.ParentRecordingIds);
            Assert.True(RecordingStore.CommittedRecordings.Count > committedCountBefore);
            Assert.True(tree.Recordings.ContainsKey(tipId));

            // Manually build a snapshot mirroring the freshly-run split, then
            // call RollBackInMemory directly. The snapshot fields are exactly
            // what the production catch path would carry.
            var snapshot = new RecordingTreeSplitter.SplitSnapshot
            {
                TreeId = "tree_13",
                TipAdded = true,
                TipRecordingId = tipId,
                // OriginClone left null intentionally: we want to verify the
                // ledger-walk path independently. The reference-swap path is
                // separately covered by the partial-snapshot test below.
            };
            // Mirror the production ledger entries (reverse order on undo).
            // BP late reparent.
            int parentIdx = bpLate.ParentRecordingIds.IndexOf(tipId);
            Assert.True(parentIdx >= 0);
            snapshot.Ledger.Add(RecordingTreeSplitter.SplitMutationLedger.BpParent(
                bpLate, parentIdx, origin.RecordingId, tipId));
            // Debris reparent.
            snapshot.Ledger.Add(RecordingTreeSplitter.SplitMutationLedger.DebrisParent(
                debrisPost, origin.RecordingId, tipId));
            // Ledger action retag.
            snapshot.Ledger.Add(RecordingTreeSplitter.SplitMutationLedger.LedgerAction(
                action50, origin.RecordingId, tipId));
            // Milestone retag.
            snapshot.Ledger.Add(RecordingTreeSplitter.SplitMutationLedger.MilestoneRetag(
                msPost, origin.RecordingId, tipId));

            RecordingTreeSplitter.RollBackInMemory(null, snapshot);

            // Tip removed from committed list + tree dict.
            Assert.Null(FindCommitted(tipId));
            Assert.False(tree.Recordings.ContainsKey(tipId));
            // Ledger entries reversed.
            Assert.Equal(origin.RecordingId, debrisPost.DebrisParentRecordingId);
            Assert.Equal(origin.RecordingId, action50.RecordingId);
            Assert.Equal(origin.RecordingId, msPost.RecordingId);
            Assert.Contains(origin.RecordingId, bpLate.ParentRecordingIds);
            Assert.DoesNotContain(tipId, bpLate.ParentRecordingIds);

            // RollBack log present.
            Assert.Contains(logLines, l =>
                l.Contains("[Splitter]")
                && l.Contains("RollBackInMemory: rolled back split")
                && l.Contains("tipsRemoved=1")
                && l.Contains("ledgerEntries=4"));
        }

        // -----------------------------------------------------------------
        // Reference-swap rollback (origin clone restoration)
        // -----------------------------------------------------------------

        [Fact]
        public void SplitOriginAtRewindUT_RollBackInMemory_SwapsOriginReferenceBack()
        {
            var origin = BuildRecording("rec_origin", 8.0, 53.0, midUT: 34.0,
                treeId: "tree_swap", terminal: TerminalState.Destroyed);
            origin.ChainId = "chain_S";
            origin.ChainIndex = 0;
            var tree = InstallOriginInTree(origin, "tree_swap");

            // Capture a clone of pre-split origin (this mirrors what the
            // production splitter does BEFORE calling SplitAtUT).
            Recording originClone = Recording.DeepClone(origin);
            int chainIndexBefore = origin.ChainIndex;

            // Now intentionally mutate origin's fields so we can prove the
            // reference swap restored the pre-mutation values.
            origin.ChainIndex = 7;
            origin.VesselName = "MUTATED";

            var snapshot = new RecordingTreeSplitter.SplitSnapshot
            {
                TreeId = "tree_swap",
                TipAdded = false,
                OriginClone = originClone,
            };
            // Capture the pre-call chainSiblings map so RollBack restores
            // ChainIndex from the snapshot map.
            snapshot.ChainSiblingsBefore[origin.RecordingId] = chainIndexBefore;

            RecordingTreeSplitter.RollBackInMemory(null, snapshot);

            // Lookup via CommittedRecordings: should now resolve to the clone.
            Recording resolved = FindCommitted(origin.RecordingId);
            Assert.NotNull(resolved);
            Assert.Same(originClone, resolved);
            // Tree dict also points at the clone.
            Assert.Same(originClone, tree.Recordings[origin.RecordingId]);
            // ChainIndex restored.
            Assert.Equal(chainIndexBefore, resolved.ChainIndex);
        }

        // =====================================================================
        // 14. End-to-end: split + supersede row + RunOptimizationPass
        //
        // Reviewer Pass 1 Finding 5: the splitter and the optimizer's CanAutoMerge
        // supersede-row guard are each unit-tested in isolation. This test wires
        // them together to lock in the system-level invariant: after the splitter
        // produces HEAD+TIP and AppendRelations writes a TIP→fork row, a later
        // RunOptimizationPass must NOT merge HEAD+TIP back together. Without the
        // CanAutoMerge guard, HEAD and TIP would qualify as merge candidates
        // (same env, adjacent chain, no ghosting-trigger events in this synthetic
        // fixture) and the optimizer would silently undo the split.
        // =====================================================================

        [Fact]
        public void SplitThenSupersedeRow_OptimizerPreservesHeadAndTip()
        {
            // Step 1: build origin (homogeneous Atmospheric env so post-split
            // halves would be auto-merge candidates without a guard).
            var origin = BuildRecording("rec_origin", startUT: 8.0, endUT: 53.0,
                midUT: 34.0, treeId: "tree_14",
                terminal: TerminalState.Destroyed);
            InstallOriginInTree(origin, "tree_14");

            // Step 2: split with a null scenario (matches happy-path setup).
            var marker = BuildMarker(origin, rewindUT: 34.0);
            var splitResult = RecordingTreeSplitter.SplitOriginAtRewindUT(marker, null);
            Assert.False(splitResult.Skipped);
            string tipId = splitResult.TipRecordingId;
            Recording head = FindCommitted("rec_origin");
            Recording tip = FindCommitted(tipId);
            Assert.NotNull(head);
            Assert.NotNull(tip);
            Assert.Equal(head.ChainId, tip.ChainId);
            Assert.Equal(0, head.ChainIndex);
            Assert.Equal(1, tip.ChainIndex);

            // Step 3: install a scenario carrying the production-shaped
            // supersede row that AppendRelations would have written
            // (TIP -> fork). CanAutoMerge reads ParsekScenario.Instance to
            // find this row, so a plain-CLR scenario installed via
            // SetInstanceForTesting is all the guard needs.
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>
                {
                    new RecordingSupersedeRelation
                    {
                        RelationId = "rsr_test_14",
                        OldRecordingId = tipId,
                        NewRecordingId = "rec_fork",
                        UT = 34.0,
                        CreatedRealTime = "2026-05-16T00:00:00Z",
                    },
                },
            };
            ParsekScenario.SetInstanceForTesting(scenario);

            // Step 4: run the optimizer. With the guard in place, HEAD+TIP
            // must NOT be merged. (Without the guard, FindMergeCandidates
            // would return (head, tip) and MergeInto would collapse them
            // into a single recording.)
            int committedCountBefore = RecordingStore.CommittedRecordings.Count;
            RecordingStore.RunOptimizationPass();

            // Step 5: assert HEAD+TIP both survived intact.
            Assert.Equal(committedCountBefore, RecordingStore.CommittedRecordings.Count);
            Recording headAfter = FindCommitted("rec_origin");
            Recording tipAfter = FindCommitted(tipId);
            Assert.NotNull(headAfter);
            Assert.NotNull(tipAfter);
            Assert.Equal(8.0, headAfter.StartUT);
            Assert.Equal(34.0, headAfter.EndUT);
            Assert.Equal(34.0, tipAfter.StartUT);
            Assert.Equal(53.0, tipAfter.EndUT);
            Assert.Equal(headAfter.ChainId, tipAfter.ChainId);
            Assert.Equal(0, headAfter.ChainIndex);
            Assert.Equal(1, tipAfter.ChainIndex);

            // Supersede row untouched.
            Assert.Single(scenario.RecordingSupersedes);
            Assert.Equal(tipId, scenario.RecordingSupersedes[0].OldRecordingId);
            Assert.Equal("rec_fork", scenario.RecordingSupersedes[0].NewRecordingId);

            // Guard's Verbose log fired naming the rejected pair.
            Assert.Contains(logLines, l =>
                l.Contains("[Optimizer]")
                && l.Contains("CanAutoMerge: rejecting merge")
                && l.Contains("rec_origin")
                && l.Contains(tipId));
        }

        // =====================================================================
        // 15. RollBackInMemory restores marker.SupersedeTargetId
        //
        // Pass 2 review Opus-H3 / User-M1: the orchestrator's step 2.10
        // mutates marker.SupersedeTargetId from its pre-call value to TIP's
        // id. Without snapshot capture, an exception in steps 2.11-2.13 (or
        // any future tail step) would leave the marker pointing at TIP's
        // about-to-be-removed id after rollback. The snapshot now carries the
        // pre-mutation value + the marker reference; rollback restores both.
        // =====================================================================

        [Fact]
        public void SplitOriginAtRewindUT_RollBackInMemory_RestoresMarkerSupersedeTargetId()
        {
            var origin = BuildRecording("rec_origin", 8.0, 53.0, midUT: 34.0,
                treeId: "tree_15", terminal: TerminalState.Destroyed);
            origin.ChainId = "chain_M";
            origin.ChainIndex = 0;
            InstallOriginInTree(origin, "tree_15");

            var marker = BuildMarker(origin, rewindUT: 34.0);
            // Initial SupersedeTargetId — typically the origin's id before the
            // splitter runs; could also be null on a marker that hasn't been
            // routed yet. We pick origin.RecordingId so the post-rollback value
            // is observably distinct from "null".
            marker.SupersedeTargetId = origin.RecordingId;
            string preMutationSupersedeTargetId = marker.SupersedeTargetId;

            // Synthesize a snapshot mirroring a partially-completed split:
            // step 2.10 ran (PreSplitSupersedeTargetId captured, marker
            // mutated to TIP); steps 2.11-2.13 threw before they could be
            // recorded into the ledger.
            string tipId = "tip_synth_" + Guid.NewGuid().ToString("N");
            marker.SupersedeTargetId = tipId;

            // Add a synthetic TIP into the store + tree dict so rollback's
            // step-1 removal has something to remove.
            var tip = new Recording
            {
                RecordingId = tipId,
                TreeId = "tree_15",
                ChainId = origin.ChainId,
                ChainBranch = origin.ChainBranch,
                ChainIndex = 1,
                MergeState = MergeState.Immutable,
            };
            RecordingStore.AddCommittedInternal(tip);
            var tree = RecordingStore.CommittedTrees[0];
            tree.AddOrReplaceRecording(tip);

            var snapshot = new RecordingTreeSplitter.SplitSnapshot
            {
                TreeId = "tree_15",
                TipAdded = true,
                TipRecordingId = tipId,
                MarkerForRollback = marker,
                PreSplitSupersedeTargetId = preMutationSupersedeTargetId,
                SupersedeTargetIdCaptured = true,
            };

            RecordingTreeSplitter.RollBackInMemory(null, snapshot);

            // Marker's SupersedeTargetId restored to pre-mutation value.
            Assert.Equal(preMutationSupersedeTargetId, marker.SupersedeTargetId);
            // TIP gone from store + tree.
            Assert.Null(FindCommitted(tipId));
            Assert.False(tree.Recordings.ContainsKey(tipId));
            // Rollback log surfaces the marker restoration.
            Assert.Contains(logLines, l =>
                l.Contains("[Splitter]")
                && l.Contains("RollBackInMemory")
                && l.Contains("markerRestored=true")
                && l.Contains($"markerRestoredTo={preMutationSupersedeTargetId}"));
        }

        [Fact]
        public void SplitOriginAtRewindUT_RollBackInMemory_MarkerUntouchedIfSupersedeTargetIdNotCaptured()
        {
            // Rollback path before step 2.10 ran: SupersedeTargetIdCaptured is
            // false (default), so rollback must NOT touch marker.SupersedeTargetId.
            var origin = BuildRecording("rec_origin", 8.0, 53.0, midUT: 34.0,
                treeId: "tree_15b", terminal: TerminalState.Destroyed);
            InstallOriginInTree(origin, "tree_15b");

            var marker = BuildMarker(origin, rewindUT: 34.0);
            marker.SupersedeTargetId = "external_value_unchanged_by_rollback";

            var snapshot = new RecordingTreeSplitter.SplitSnapshot
            {
                TreeId = "tree_15b",
                TipAdded = false,
                MarkerForRollback = null,
                SupersedeTargetIdCaptured = false,
            };

            RecordingTreeSplitter.RollBackInMemory(null, snapshot);

            Assert.Equal("external_value_unchanged_by_rollback", marker.SupersedeTargetId);
            Assert.Contains(logLines, l =>
                l.Contains("[Splitter]")
                && l.Contains("RollBackInMemory")
                && l.Contains("markerRestored=false"));
        }
    }
}
