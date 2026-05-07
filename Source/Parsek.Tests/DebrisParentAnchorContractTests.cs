using System;
using System.Collections.Generic;
using Parsek;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// PR 3b unit tests covering the v12+ debris parent-anchor contract.
    ///
    /// Coverage matrix:
    ///   - <see cref="Recording.ApplyDebrisAnchorContract"/> helper (both overloads):
    ///     non-debris no-op, debris stamps the field, null parent.
    ///   - The eight propagation sites enumerated in plan §"`IsDebris` propagation
    ///     surface": three primary creation sites (focused-vessel via
    ///     `CreateBreakupChildRecording`, BG-vessel via `RegisterChildRecordingsFromSplit`
    ///     and `BuildBackgroundSplitBranchData`) and five secondary copy sites
    ///     (Recording.ApplyPersistenceArtifactsFrom, SessionMerger,
    ///     RewindInvoker Re-Fly inheritance, RecordingOptimizer.SplitAtSection,
    ///     BackgroundRecorder parent-continuation).
    ///   - <see cref="RecordingOptimizer.CanAutoMerge"/> mismatch guards
    ///     (IsDebris differs / DebrisParentRecordingId differs).
    ///   - <see cref="RecordingOptimizer.SplitAtSection"/>: both halves keep the
    ///     contract.
    ///   - <see cref="FlightRecorder.IsReFlyPostLoadSettleActiveForRecording"/>
    ///     accessor: false when no focus, false when no settle, false when id
    ///     mismatch, true when all match.
    ///
    /// Tests requiring Unity scene state (live vessel resolution, section flips
    /// via OnVesselBackgrounded → InitializeLoadedState, structural-event seam
    /// driving) are deferred to in-game tests — see
    /// <c>Source/Parsek/InGameTests/RuntimeTests.cs</c>.
    /// </summary>
    [Collection("Sequential")]
    public class DebrisParentAnchorContractTests : IDisposable
    {
        public DebrisParentAnchorContractTests()
        {
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            // Defensive: clear any focus recorder set by the settle accessor tests.
            Patches.PhysicsFramePatch.ActiveRecorder = null;
        }

        // ===== A. Helper =====

        [Fact]
        public void ApplyDebrisAnchorContract_NonDebrisChild_DoesNothing()
        {
            var parent = new Recording { RecordingId = "parent-1" };
            var child = new Recording { RecordingId = "child-1", IsDebris = false };

            Recording.ApplyDebrisAnchorContract(child, parent);

            Assert.Null(child.DebrisParentRecordingId);
        }

        [Fact]
        public void ApplyDebrisAnchorContract_DebrisChild_StampsParentRecordingId()
        {
            var parent = new Recording { RecordingId = "parent-2" };
            var child = new Recording { RecordingId = "child-2", IsDebris = true };

            Recording.ApplyDebrisAnchorContract(child, parent);

            Assert.Equal("parent-2", child.DebrisParentRecordingId);
        }

        [Fact]
        public void ApplyDebrisAnchorContract_DebrisChild_NullParent_LeavesFieldNull()
        {
            var child = new Recording { RecordingId = "child-3", IsDebris = true };

            Recording.ApplyDebrisAnchorContract(child, (Recording)null);

            Assert.Null(child.DebrisParentRecordingId);
        }

        [Fact]
        public void ApplyDebrisAnchorContract_StringOverload_DebrisChild_StampsId()
        {
            var child = new Recording { RecordingId = "child-4", IsDebris = true };

            Recording.ApplyDebrisAnchorContract(child, "parent-id-string");

            Assert.Equal("parent-id-string", child.DebrisParentRecordingId);
        }

        [Fact]
        public void ApplyDebrisAnchorContract_StringOverload_NonDebrisChild_NoOp()
        {
            var child = new Recording { RecordingId = "child-5", IsDebris = false };

            Recording.ApplyDebrisAnchorContract(child, "parent-id-string");

            Assert.Null(child.DebrisParentRecordingId);
        }

        [Fact]
        public void ApplyDebrisAnchorContract_NullChild_DoesNotThrow()
        {
            // Defensive: helper guard. Both overloads should be safe to call with
            // a null child (guarantees the helper can wrap an in-progress null check).
            Recording.ApplyDebrisAnchorContract((Recording)null, new Recording());
            Recording.ApplyDebrisAnchorContract((Recording)null, "parent");
        }

        // ===== B. Primary creation site #3 — BuildBackgroundSplitBranchData =====

        [Fact]
        public void BuildBackgroundSplitBranchData_DebrisChild_StampsParentRecordingId()
        {
            var newVessels = new List<(uint pid, string name, bool hasController)>
            {
                (300u, "Booster", false)
            };

            var (bp, children) = BackgroundRecorder.BuildBackgroundSplitBranchData(
                "parent_rec_42", "tree_1", 750.0, BranchPointType.JointBreak,
                100u, newVessels);

            Assert.Single(children);
            Assert.True(children[0].IsDebris);
            Assert.Equal("parent_rec_42", children[0].DebrisParentRecordingId);
        }

        [Fact]
        public void BuildBackgroundSplitBranchData_ControlledChild_DoesNotStampField()
        {
            var newVessels = new List<(uint pid, string name, bool hasController)>
            {
                (300u, "Probe Ship", true)
            };

            var (bp, children) = BackgroundRecorder.BuildBackgroundSplitBranchData(
                "parent_rec_42", "tree_1", 750.0, BranchPointType.JointBreak,
                100u, newVessels);

            Assert.Single(children);
            Assert.False(children[0].IsDebris);
            Assert.Null(children[0].DebrisParentRecordingId);
        }

        [Fact]
        public void BuildBackgroundSplitBranchData_MixedChildren_OnlyDebrisGetField()
        {
            var newVessels = new List<(uint pid, string name, bool hasController)>
            {
                (300u, "Probe Ship", true),
                (301u, "Booster", false),
                (302u, "Fairing", false)
            };

            var (bp, children) = BackgroundRecorder.BuildBackgroundSplitBranchData(
                "parent_rec_42", "tree_1", 750.0, BranchPointType.JointBreak,
                100u, newVessels);

            Assert.Equal(3, children.Count);
            Assert.False(children[0].IsDebris);
            Assert.Null(children[0].DebrisParentRecordingId);
            Assert.True(children[1].IsDebris);
            Assert.Equal("parent_rec_42", children[1].DebrisParentRecordingId);
            Assert.True(children[2].IsDebris);
            Assert.Equal("parent_rec_42", children[2].DebrisParentRecordingId);
        }

        // ===== C. Secondary propagation site #4 — Recording.ApplyPersistenceArtifactsFrom =====

        [Fact]
        public void ApplyPersistenceArtifactsFrom_DebrisRecording_PropagatesParentRecordingId()
        {
            var source = new Recording
            {
                RecordingId = "src",
                IsDebris = true,
                DebrisParentRecordingId = "parent-from-src"
            };
            var dest = new Recording { RecordingId = "dst" };

            dest.ApplyPersistenceArtifactsFrom(source);

            Assert.True(dest.IsDebris);
            Assert.Equal("parent-from-src", dest.DebrisParentRecordingId);
        }

        [Fact]
        public void ApplyPersistenceArtifactsFrom_NonDebrisRecording_LeavesFieldNull()
        {
            var source = new Recording { RecordingId = "src", IsDebris = false };
            var dest = new Recording { RecordingId = "dst" };

            dest.ApplyPersistenceArtifactsFrom(source);

            Assert.False(dest.IsDebris);
            Assert.Null(dest.DebrisParentRecordingId);
        }

        // ===== C. Secondary propagation site #5 — SessionMerger =====
        // PR 3b review follow-up §3: drive SessionMerger.MergeTree with a
        // debris source recording and assert the merged copy carries both
        // IsDebris and DebrisParentRecordingId. A future refactor that
        // touches one field-copy line but forgets the other lights up here.

        [Fact]
        public void SessionMerger_DebrisRecording_MergedCopyKeepsParentRecordingId()
        {
            var src = new Recording
            {
                RecordingId = "src-debris",
                VesselName = "Booster Debris",
                TreeId = "test-tree",
                VesselPersistentId = 4242u,
                IsDebris = true,
                DebrisParentRecordingId = "parent-rec-from-merger",
                ExplicitStartUT = 0.0,
                ExplicitEndUT = 10.0,
            };
            // Minimum payload: one TrackSection so MergeTree's per-recording
            // pipeline runs end-to-end.
            src.TrackSections.Add(new TrackSection
            {
                source = TrackSectionSource.Active,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 0.0,
                endUT = 10.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 0.0 },
                    new TrajectoryPoint { ut = 10.0 },
                },
            });

            var tree = new RecordingTree { Id = "test-tree" };
            tree.Recordings[src.RecordingId] = src;
            tree.RootRecordingId = src.RecordingId;
            tree.ActiveRecordingId = src.RecordingId;

            Dictionary<string, Recording> merged = SessionMerger.MergeTree(tree);

            Assert.True(merged.ContainsKey("src-debris"));
            Recording mergedRec = merged["src-debris"];
            Assert.True(mergedRec.IsDebris);
            Assert.Equal("parent-rec-from-merger", mergedRec.DebrisParentRecordingId);
        }

        [Fact]
        public void SessionMerger_NonDebrisRecording_MergedCopyHasNullParentRecordingId()
        {
            // Mirror the sparse-write contract on the merge path: a non-debris
            // recording's merged copy must NOT carry a non-null
            // DebrisParentRecordingId (the source side has it null too; this
            // test pins the no-stamping invariant on the merger).
            var src = new Recording
            {
                RecordingId = "src-non-debris",
                VesselName = "Active Probe",
                TreeId = "test-tree",
                VesselPersistentId = 1u,
                IsDebris = false,
                DebrisParentRecordingId = null,
            };
            src.TrackSections.Add(new TrackSection
            {
                source = TrackSectionSource.Active,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 0.0,
                endUT = 10.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 0.0 },
                    new TrajectoryPoint { ut = 10.0 },
                },
            });

            var tree = new RecordingTree { Id = "test-tree" };
            tree.Recordings[src.RecordingId] = src;
            tree.RootRecordingId = src.RecordingId;
            tree.ActiveRecordingId = src.RecordingId;

            Dictionary<string, Recording> merged = SessionMerger.MergeTree(tree);

            Assert.True(merged.ContainsKey("src-non-debris"));
            Recording mergedRec = merged["src-non-debris"];
            Assert.False(mergedRec.IsDebris);
            Assert.Null(mergedRec.DebrisParentRecordingId);
        }

        // ===== C. Secondary propagation site #6 — RewindInvoker Re-Fly inheritance =====
        // PR 3b review follow-up §3: the in-place fork's identity-copy block
        // (RewindInvoker.cs:920+) was extracted into the pure-static
        // RewindInvoker.CopyInheritedIdentityForFork(provisional, inheritFrom)
        // helper so xUnit can validate the field-copy contract directly.
        // Tests cover null guards plus every field copied including the
        // load-bearing DebrisParentRecordingId.

        [Fact]
        public void CopyInheritedIdentityForFork_DebrisProvisional_PropagatesParentRecordingId()
        {
            var inheritFrom = new Recording
            {
                RecordingId = "inherit-source",
                VesselPersistentId = 9999u,
                VesselName = "Booster Debris (origin)",
                IsDebris = true,
                DebrisParentRecordingId = "parent-rec-from-rewind",
                Generation = 1,
                SegmentPhase = "Atmospheric",
                SegmentBodyName = "Kerbin",
                StartBodyName = "Kerbin",
                StartBiome = "Shores",
                StartSituation = "FLYING",
                LaunchSiteName = "LaunchPad",
            };
            var provisional = new Recording
            {
                RecordingId = "provisional-fork",
                // All identity fields start at default — we must observe
                // every one of them flip to the inheritFrom values.
            };

            RewindInvoker.CopyInheritedIdentityForFork(provisional, inheritFrom);

            Assert.Equal(9999u, provisional.VesselPersistentId);
            Assert.Equal("Booster Debris (origin)", provisional.VesselName);
            Assert.True(provisional.IsDebris);
            Assert.Equal("parent-rec-from-rewind", provisional.DebrisParentRecordingId);
            Assert.Equal(1, provisional.Generation);
            Assert.Equal("Atmospheric", provisional.SegmentPhase);
            Assert.Equal("Kerbin", provisional.SegmentBodyName);
            Assert.Equal("Kerbin", provisional.StartBodyName);
            Assert.Equal("Shores", provisional.StartBiome);
            Assert.Equal("FLYING", provisional.StartSituation);
            Assert.Equal("LaunchPad", provisional.LaunchSiteName);
            // ChainId / ChainIndex / ChainBranch are intentionally NOT copied —
            // the supersede table is the only authority on chain-tip resolution.
            // See the call-site comment in AtomicMarkerWrite.
            Assert.Null(provisional.ChainId);
        }

        [Fact]
        public void CopyInheritedIdentityForFork_NonDebrisProvisional_LeavesParentRecordingIdNull()
        {
            // A non-debris source recording must NOT stamp a parent on the
            // provisional. (No-op contract for the field-copy block.)
            var inheritFrom = new Recording
            {
                RecordingId = "inherit-source-nondebris",
                VesselPersistentId = 1234u,
                VesselName = "Manned Probe",
                IsDebris = false,
                DebrisParentRecordingId = null,
                Generation = 0,
            };
            var provisional = new Recording { RecordingId = "provisional-fork" };

            RewindInvoker.CopyInheritedIdentityForFork(provisional, inheritFrom);

            Assert.Equal(1234u, provisional.VesselPersistentId);
            Assert.False(provisional.IsDebris);
            Assert.Null(provisional.DebrisParentRecordingId);
        }

        [Fact]
        public void CopyInheritedIdentityForFork_NullGuards_AreNoOp()
        {
            // Defensive: null inputs do not throw. The Re-Fly path should
            // never call this helper with nulls in practice, but the
            // null-safe contract makes the helper safe to invoke from
            // future call sites that haven't been hardened against
            // unexpected state.
            RewindInvoker.CopyInheritedIdentityForFork(null, new Recording());
            RewindInvoker.CopyInheritedIdentityForFork(new Recording(), null);
            RewindInvoker.CopyInheritedIdentityForFork(null, null);
        }

        // ===== C. Secondary propagation site #7 — RecordingOptimizer.SplitAtSection =====

        [Fact]
        public void SplitAtSection_DebrisRecording_BothHalvesKeepParentRecordingId()
        {
            var rec = new Recording
            {
                RecordingId = "split-src",
                IsDebris = true,
                DebrisParentRecordingId = "parent-split",
                ExplicitStartUT = 0.0,
                ExplicitEndUT = 30.0,
                ChainId = "chain-x"
            };
            // Two TrackSections so we can split between them.
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                startUT = 0.0,
                endUT = 15.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 0.0 },
                    new TrajectoryPoint { ut = 15.0 }
                }
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 15.0,
                endUT = 30.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 15.0 },
                    new TrajectoryPoint { ut = 30.0 }
                }
            });
            rec.Points.Add(new TrajectoryPoint { ut = 0.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 15.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 30.0 });

            Recording second = RecordingOptimizer.SplitAtSection(rec, sectionIndex: 1);

            Assert.NotNull(second);
            // `original` half: in-place mutation must not drop the contract.
            Assert.True(rec.IsDebris);
            Assert.Equal("parent-split", rec.DebrisParentRecordingId);
            // `second` half: explicit propagation line copies the contract.
            Assert.True(second.IsDebris);
            Assert.Equal("parent-split", second.DebrisParentRecordingId);
        }

        // ===== C. Secondary propagation site #8 — BackgroundRecorder.cs:673 parent continuation =====
        // The BackgroundRecorder parent-continuation initializer is a mechanical
        // `DebrisParentRecordingId = parentRec.DebrisParentRecordingId` adjacent to
        // the existing IsDebris copy. With MaxRecordingGeneration=1 this site never
        // creates a debris continuation today (forward-compat per Decision §10);
        // in-game tests cover the full BG-split flow.

        // ===== H. Optimizer guards — CanAutoMerge =====

        private static Recording MakeChainSegmentForMerge(string chainId, int chainIndex,
            string body = "Mun", double startUT = 17000, double endUT = 17060)
        {
            return new Recording
            {
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = 0,
                SegmentPhase = "exo",
                SegmentBodyName = body,
                LoopPlayback = false,
                PlaybackEnabled = true,
                Hidden = false,
                LoopIntervalSeconds = LoopTiming.UntouchedLoopIntervalSentinel,
                LoopAnchorVesselId = 0,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
            };
        }

        [Fact]
        public void CanAutoMerge_BothNonDebris_ReturnsTrue()
        {
            var a = MakeChainSegmentForMerge("chain-a", 0);
            var b = MakeChainSegmentForMerge("chain-a", 1);

            Assert.True(RecordingOptimizer.CanAutoMerge(a, b));
        }

        [Fact]
        public void CanAutoMerge_OneDebrisOneNotDebris_ReturnsFalse()
        {
            var a = MakeChainSegmentForMerge("chain-a", 0);
            var b = MakeChainSegmentForMerge("chain-a", 1);
            a.IsDebris = true;
            a.DebrisParentRecordingId = "parent-a";

            Assert.False(RecordingOptimizer.CanAutoMerge(a, b));
            Assert.False(RecordingOptimizer.CanAutoMerge(b, a));
        }

        [Fact]
        public void CanAutoMerge_BothDebrisDifferentParents_ReturnsFalse()
        {
            var a = MakeChainSegmentForMerge("chain-a", 0);
            var b = MakeChainSegmentForMerge("chain-a", 1);
            a.IsDebris = true;
            a.DebrisParentRecordingId = "parent-a";
            b.IsDebris = true;
            b.DebrisParentRecordingId = "parent-b";

            Assert.False(RecordingOptimizer.CanAutoMerge(a, b));
        }

        [Fact]
        public void CanAutoMerge_BothDebrisSameParent_ReturnsTrue()
        {
            var a = MakeChainSegmentForMerge("chain-a", 0);
            var b = MakeChainSegmentForMerge("chain-a", 1);
            a.IsDebris = true;
            a.DebrisParentRecordingId = "parent-a";
            b.IsDebris = true;
            b.DebrisParentRecordingId = "parent-a";

            Assert.True(RecordingOptimizer.CanAutoMerge(a, b));
        }

        // ===== G. FlightRecorder.IsReFlyPostLoadSettleActiveForRecording =====

        [Fact]
        public void IsReFlyPostLoadSettleActiveForRecording_NoFocusRecorder_ReturnsFalse()
        {
            Patches.PhysicsFramePatch.ActiveRecorder = null;

            Assert.False(FlightRecorder.IsReFlyPostLoadSettleActiveForRecording("any-id"));
        }

        [Fact]
        public void IsReFlyPostLoadSettleActiveForRecording_FocusActiveButNoSettle_ReturnsFalse()
        {
            var focus = new FlightRecorder();
            Patches.PhysicsFramePatch.ActiveRecorder = focus;
            try
            {
                Assert.False(FlightRecorder.IsReFlyPostLoadSettleActiveForRecording("any-id"));
            }
            finally
            {
                Patches.PhysicsFramePatch.ActiveRecorder = null;
            }
        }

        [Fact]
        public void IsReFlyPostLoadSettleActiveForRecording_SettleActiveButIdMismatch_ReturnsFalse()
        {
            var focus = new FlightRecorder();
            focus.ActivateReFlyPostLoadSettleForTesting("session-1", "recording-A");
            Patches.PhysicsFramePatch.ActiveRecorder = focus;
            try
            {
                Assert.False(FlightRecorder.IsReFlyPostLoadSettleActiveForRecording("recording-B"));
            }
            finally
            {
                Patches.PhysicsFramePatch.ActiveRecorder = null;
            }
        }

        [Fact]
        public void IsReFlyPostLoadSettleActiveForRecording_SettleActiveAndIdMatches_ReturnsTrue()
        {
            var focus = new FlightRecorder();
            focus.ActivateReFlyPostLoadSettleForTesting("session-1", "recording-A");
            Patches.PhysicsFramePatch.ActiveRecorder = focus;
            try
            {
                Assert.True(FlightRecorder.IsReFlyPostLoadSettleActiveForRecording("recording-A"));
            }
            finally
            {
                Patches.PhysicsFramePatch.ActiveRecorder = null;
            }
        }

        [Fact]
        public void IsReFlyPostLoadSettleActiveForRecording_NullRecordingId_ReturnsFalse()
        {
            var focus = new FlightRecorder();
            focus.ActivateReFlyPostLoadSettleForTesting("session-1", "recording-A");
            Patches.PhysicsFramePatch.ActiveRecorder = focus;
            try
            {
                // Defensive: never claim true for a null queryId, even if the
                // accessor's underlying string.Equals would also return false.
                Assert.False(FlightRecorder.IsReFlyPostLoadSettleActiveForRecording(null));
                Assert.False(FlightRecorder.IsReFlyPostLoadSettleActiveForRecording(string.Empty));
            }
            finally
            {
                Patches.PhysicsFramePatch.ActiveRecorder = null;
            }
        }
    }
}
