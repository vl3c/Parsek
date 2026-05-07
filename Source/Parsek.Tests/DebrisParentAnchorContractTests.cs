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
        // SessionMerger is a heavy harness; the propagation line itself is a
        // mechanical "merged.DebrisParentRecordingId = srcRec.DebrisParentRecordingId"
        // adjacent to the existing IsDebris copy. ApplyPersistenceArtifactsFrom (above)
        // exercises the same field-copy contract; in-game tests cover the full merger.

        // ===== C. Secondary propagation site #6 — RewindInvoker Re-Fly inheritance =====
        // The provisional.DebrisParentRecordingId = inheritFrom.DebrisParentRecordingId
        // line is mechanical (one line adjacent to existing IsDebris copy). The
        // resolver harness scenario 7 exercises the live Re-Fly walk-back; in-game
        // tests cover full RewindInvoker flow.

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
