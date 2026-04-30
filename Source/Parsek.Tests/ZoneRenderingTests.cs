using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for zone-based rendering decisions — zone classification, transition detection,
    /// rendering policy, looped ghost spawn gating, and diagnostic logging.
    /// </summary>
    [Collection("Sequential")]
    public class ZoneRenderingTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ZoneRenderingTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        #region Zone Classification

        [Fact]
        public void ClassifyDistance_WithinPhysicsBubble_ReturnsPhysics()
        {
            Assert.Equal(RenderingZone.Physics, RenderingZoneManager.ClassifyDistance(0));
            Assert.Equal(RenderingZone.Physics, RenderingZoneManager.ClassifyDistance(1000));
            Assert.Equal(RenderingZone.Physics, RenderingZoneManager.ClassifyDistance(2299));
        }

        [Fact]
        public void ClassifyDistance_AtPhysicsBoundary_ReturnsVisual()
        {
            // Exactly at 2300m boundary transitions to Visual
            Assert.Equal(RenderingZone.Visual, RenderingZoneManager.ClassifyDistance(2300));
        }

        [Fact]
        public void ClassifyDistance_InVisualRange_ReturnsVisual()
        {
            Assert.Equal(RenderingZone.Visual, RenderingZoneManager.ClassifyDistance(5000));
            Assert.Equal(RenderingZone.Visual, RenderingZoneManager.ClassifyDistance(50000));
            Assert.Equal(RenderingZone.Visual, RenderingZoneManager.ClassifyDistance(119999));
        }

        [Fact]
        public void ClassifyDistance_AtVisualBoundary_ReturnsBeyond()
        {
            Assert.Equal(RenderingZone.Beyond, RenderingZoneManager.ClassifyDistance(RenderingZoneManager.VisualRangeRadius));
        }

        [Fact]
        public void ClassifyDistance_FarBeyond_ReturnsBeyond()
        {
            Assert.Equal(RenderingZone.Beyond, RenderingZoneManager.ClassifyDistance(RenderingZoneManager.VisualRangeRadius + 100000));
            Assert.Equal(RenderingZone.Beyond, RenderingZoneManager.ClassifyDistance(double.MaxValue));
        }

        #endregion

        #region Zone Transition Detection

        [Fact]
        public void DetectZoneTransition_SameZone_ReturnsFalse()
        {
            string desc;
            Assert.False(GhostPlaybackLogic.DetectZoneTransition(
                RenderingZone.Physics, RenderingZone.Physics, out desc));
            Assert.Null(desc);
        }

        [Fact]
        public void DetectZoneTransition_PhysicsToVisual_ReturnsOutward()
        {
            string desc;
            Assert.True(GhostPlaybackLogic.DetectZoneTransition(
                RenderingZone.Physics, RenderingZone.Visual, out desc));
            Assert.Equal("outward", desc);
        }

        [Fact]
        public void DetectZoneTransition_VisualToBeyond_ReturnsOutward()
        {
            string desc;
            Assert.True(GhostPlaybackLogic.DetectZoneTransition(
                RenderingZone.Visual, RenderingZone.Beyond, out desc));
            Assert.Equal("outward", desc);
        }

        [Fact]
        public void DetectZoneTransition_PhysicsToBeyond_ReturnsOutward()
        {
            string desc;
            Assert.True(GhostPlaybackLogic.DetectZoneTransition(
                RenderingZone.Physics, RenderingZone.Beyond, out desc));
            Assert.Equal("outward", desc);
        }

        [Fact]
        public void DetectZoneTransition_BeyondToVisual_ReturnsInward()
        {
            string desc;
            Assert.True(GhostPlaybackLogic.DetectZoneTransition(
                RenderingZone.Beyond, RenderingZone.Visual, out desc));
            Assert.Equal("inward", desc);
        }

        [Fact]
        public void DetectZoneTransition_VisualToPhysics_ReturnsInward()
        {
            string desc;
            Assert.True(GhostPlaybackLogic.DetectZoneTransition(
                RenderingZone.Visual, RenderingZone.Physics, out desc));
            Assert.Equal("inward", desc);
        }

        [Fact]
        public void DetectZoneTransition_BeyondToPhysics_ReturnsInward()
        {
            string desc;
            Assert.True(GhostPlaybackLogic.DetectZoneTransition(
                RenderingZone.Beyond, RenderingZone.Physics, out desc));
            Assert.Equal("inward", desc);
        }

        #endregion

        #region Zone Rendering Policy

        [Fact]
        public void GetZoneRenderingPolicy_Physics_FullRendering()
        {
            var (shouldHide, skipPartEvents, skipPositioning) =
                GhostPlaybackLogic.GetZoneRenderingPolicy(RenderingZone.Physics);
            Assert.False(shouldHide);
            Assert.False(skipPartEvents);
            Assert.False(skipPositioning);
        }

        [Fact]
        public void GetZoneRenderingPolicy_Visual_MeshAndPartEvents()
        {
            var (shouldHide, skipPartEvents, skipPositioning) =
                GhostPlaybackLogic.GetZoneRenderingPolicy(RenderingZone.Visual);
            Assert.False(shouldHide);
            Assert.False(skipPartEvents); // part events apply in Visual zone for staging/jettison/destruction
            Assert.False(skipPositioning);
        }

        [Fact]
        public void GetZoneRenderingPolicy_Beyond_EverythingSkipped()
        {
            var (shouldHide, skipPartEvents, skipPositioning) =
                GhostPlaybackLogic.GetZoneRenderingPolicy(RenderingZone.Beyond);
            Assert.True(shouldHide);
            Assert.True(skipPartEvents);
            Assert.True(skipPositioning);
        }

        #endregion

        #region Watched Full-Fidelity Override

        [Fact]
        public void ShouldForceWatchedFullFidelity_WatchedWithinCutoff_ReturnsTrue()
        {
            Assert.True(GhostPlaybackLogic.ShouldForceWatchedFullFidelity(
                isWatchedGhost: true, ghostDistanceMeters: 100000, cutoffKm: 300));
        }

        [Fact]
        public void ShouldForceWatchedFullFidelity_AtCutoff_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldForceWatchedFullFidelity(
                isWatchedGhost: true, ghostDistanceMeters: 300000, cutoffKm: 300));
        }

        [Fact]
        public void ShouldForceWatchedFullFidelity_NotWatched_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldForceWatchedFullFidelity(
                isWatchedGhost: false, ghostDistanceMeters: 100000, cutoffKm: 300));
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(-1.0)]
        public void ShouldForceWatchedFullFidelity_InvalidDistance_ReturnsFalse(
            double ghostDistanceMeters)
        {
            Assert.False(GhostPlaybackLogic.ShouldForceWatchedFullFidelity(
                isWatchedGhost: true,
                ghostDistanceMeters: ghostDistanceMeters,
                cutoffKm: 300));
        }

        [Fact]
        public void ResolveUnresolvedRelativeSectionDistanceFallback_RelativeSection_ReturnsMaxValue()
        {
            var rec = new Recording();
            rec.TrackSections.Add(new TrackSection
            {
                startUT = 100.0,
                endUT = 200.0,
                referenceFrame = ReferenceFrame.Relative,
                anchorVesselId = 698412738u
            });

            double? distance = ParsekFlight.ResolveUnresolvedRelativeSectionDistanceFallback(
                rec,
                playbackUT: 150.0);

            Assert.Equal(double.MaxValue, distance.Value);
        }

        [Fact]
        public void TryGetAbsoluteSectionPlaybackFrames_AbsoluteSection_UsesSectionFrames()
        {
            var sectionFrame = new TrajectoryPoint { ut = 1373.8077522469166, altitude = 53728.0 };
            var relativeOffsetFrame = new TrajectoryPoint { ut = 1373.7277522469167, altitude = -0.05 };
            var sectionFrames = new List<TrajectoryPoint> { sectionFrame };
            var section = new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 1373.8077522469166,
                endUT = 1423.4706489753898,
                frames = sectionFrames
            };

            Assert.True(ParsekFlight.TryGetAbsoluteSectionPlaybackFrames(
                section,
                out List<TrajectoryPoint> resolvedFrames));
            Assert.Same(sectionFrames, resolvedFrames);
            Assert.Single(resolvedFrames);
            Assert.Equal(53728.0, resolvedFrames[0].altitude);
            Assert.DoesNotContain(resolvedFrames, p =>
                p.ut == relativeOffsetFrame.ut && p.altitude == relativeOffsetFrame.altitude);
        }

        [Fact]
        public void TryGetAbsoluteSectionPlaybackFrames_RelativeSection_ReturnsFalse()
        {
            var section = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 1373.7277522469167, altitude = -0.05 }
                }
            };

            Assert.False(ParsekFlight.TryGetAbsoluteSectionPlaybackFrames(
                section,
                out List<TrajectoryPoint> resolvedFrames));
            Assert.Null(resolvedFrames);
        }

        [Fact]
        public void TryGetAbsoluteSectionPlaybackFrames_AbsoluteSectionWithoutFrames_ReturnsFalse()
        {
            var section = new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute
            };

            Assert.False(ParsekFlight.TryGetAbsoluteSectionPlaybackFrames(
                section,
                out List<TrajectoryPoint> resolvedFrames));
            Assert.Null(resolvedFrames);

            section.frames = new List<TrajectoryPoint>();

            Assert.False(ParsekFlight.TryGetAbsoluteSectionPlaybackFrames(
                section,
                out resolvedFrames));
            Assert.Null(resolvedFrames);
        }

        [Fact]
        public void ApplyWatchedFullFidelityOverride_Forced_ClearsAllSuppression()
        {
            var (shouldHide, skipPartEvents, skipPositioning) =
                GhostPlaybackLogic.ApplyWatchedFullFidelityOverride(
                    shouldHideMesh: true, shouldSkipPartEvents: true, shouldSkipPositioning: true,
                    forceFullFidelity: true);

            Assert.False(shouldHide);
            Assert.False(skipPartEvents);
            Assert.False(skipPositioning);
        }

        [Fact]
        public void ApplyDistanceLodPolicy_ReducedTier_SuppressesEventsFx_AndReducesFidelity()
        {
            var result = GhostPlaybackLogic.ApplyDistanceLodPolicy(
                shouldHideMesh: false, shouldSkipPartEvents: false, shouldSkipPositioning: false,
                ghostDistanceMeters: 10000, forceFullFidelity: false);

            Assert.False(result.shouldHideMesh);
            Assert.True(result.shouldSkipPartEvents);
            Assert.False(result.shouldSkipPositioning);
            Assert.True(result.shouldSuppressVisualFx);
            Assert.True(result.shouldReduceFidelity);
        }

        [Fact]
        public void ApplyDistanceLodPolicy_HiddenTier_HidesMesh_AndSuppressesEverything()
        {
            var result = GhostPlaybackLogic.ApplyDistanceLodPolicy(
                shouldHideMesh: false, shouldSkipPartEvents: false, shouldSkipPositioning: false,
                ghostDistanceMeters: 60000, forceFullFidelity: false);

            Assert.True(result.shouldHideMesh);
            Assert.True(result.shouldSkipPartEvents);
            Assert.True(result.shouldSkipPositioning);
            Assert.True(result.shouldSuppressVisualFx);
            Assert.False(result.shouldReduceFidelity);
        }

        [Fact]
        public void ApplyDistanceLodPolicy_ForcedFullFidelity_OverridesReducedTier()
        {
            var result = GhostPlaybackLogic.ApplyDistanceLodPolicy(
                shouldHideMesh: false, shouldSkipPartEvents: false, shouldSkipPositioning: false,
                ghostDistanceMeters: 60000, forceFullFidelity: true);

            Assert.False(result.shouldHideMesh);
            Assert.False(result.shouldSkipPartEvents);
            Assert.False(result.shouldSkipPositioning);
            Assert.False(result.shouldSuppressVisualFx);
            Assert.False(result.shouldReduceFidelity);
        }

        [Fact]
        public void IsProtectedGhost_WatchedGhost_ReturnsTrue()
        {
            Assert.True(GhostPlaybackLogic.IsProtectedGhost(
                protectedIndex: 5, currentIndex: 5));
        }

        [Fact]
        public void IsProtectedGhost_UnwatchedGhost_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.IsProtectedGhost(
                protectedIndex: 5, currentIndex: 3));
        }

        [Fact]
        public void IsProtectedGhost_ExactCycleMatch_ReturnsTrue()
        {
            Assert.True(GhostPlaybackLogic.IsProtectedGhost(
                protectedIndex: 5, protectedLoopCycleIndex: 12,
                currentIndex: 5, currentLoopCycleIndex: 12));
        }

        [Fact]
        public void IsProtectedGhost_DifferentCycle_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.IsProtectedGhost(
                protectedIndex: 5, protectedLoopCycleIndex: 12,
                currentIndex: 5, currentLoopCycleIndex: 11));
        }

        [Fact]
        public void IsWatchProtectedRecording_ExactWatchedRecording_ReturnsTrue()
        {
            var watched = new Recording
            {
                RecordingId = "watched",
                TreeId = "tree1",
                VesselPersistentId = 100
            };

            Assert.True(GhostPlaybackLogic.IsWatchProtectedRecording(
                new List<Recording> { watched }, watchedRecordingIndex: 0, currentIndex: 0));
        }

        [Fact]
        public void IsWatchProtectedRecording_DebrisOfWatchedVesselContinuation_ReturnsTrue()
        {
            var root = new Recording
            {
                RecordingId = "root",
                TreeId = "tree1",
                VesselPersistentId = 100,
                IsDebris = false
            };
            var watchedContinuation = new Recording
            {
                RecordingId = "cont",
                TreeId = "tree1",
                VesselPersistentId = 100,
                IsDebris = false
            };
            var debris = new Recording
            {
                RecordingId = "debris",
                TreeId = "tree1",
                VesselPersistentId = 200,
                IsDebris = true,
                LoopSyncParentIdx = 0
            };

            var committed = new List<Recording> { root, watchedContinuation, debris };

            Assert.True(GhostPlaybackLogic.IsWatchProtectedRecording(
                committed, watchedRecordingIndex: 1, currentIndex: 2));
        }

        [Fact]
        public void IsWatchProtectedRecording_DebrisOfDifferentVessel_ReturnsFalse()
        {
            var watched = new Recording
            {
                RecordingId = "watched",
                TreeId = "tree1",
                VesselPersistentId = 100,
                IsDebris = false
            };
            var otherParent = new Recording
            {
                RecordingId = "other",
                TreeId = "tree1",
                VesselPersistentId = 300,
                IsDebris = false
            };
            var debris = new Recording
            {
                RecordingId = "debris",
                TreeId = "tree1",
                VesselPersistentId = 200,
                IsDebris = true,
                LoopSyncParentIdx = 1
            };

            var committed = new List<Recording> { watched, otherParent, debris };

            Assert.False(GhostPlaybackLogic.IsWatchProtectedRecording(
                committed, watchedRecordingIndex: 0, currentIndex: 2));
        }

        [Fact]
        public void IsWatchProtectedRecording_RecursiveDebrisDescendantOfWatchedLineage_ReturnsTrue()
        {
            var watched = new Recording
            {
                RecordingId = "root",
                TreeId = "tree1",
                VesselPersistentId = 100,
                IsDebris = false
            };
            var booster = new Recording
            {
                RecordingId = "booster",
                TreeId = "tree1",
                VesselPersistentId = 200,
                IsDebris = true,
                ParentBranchPointId = "bp-root"
            };
            var fragment = new Recording
            {
                RecordingId = "fragment",
                TreeId = "tree1",
                VesselPersistentId = 201,
                IsDebris = true,
                ParentBranchPointId = "bp-booster"
            };

            var tree = new RecordingTree
            {
                Id = "tree1",
                Recordings = new Dictionary<string, Recording>
                {
                    ["root"] = watched,
                    ["booster"] = booster,
                    ["fragment"] = fragment
                },
                BranchPoints = new List<BranchPoint>
                {
                    new BranchPoint
                    {
                        Id = "bp-root",
                        ParentRecordingIds = new List<string> { "root" }
                    },
                    new BranchPoint
                    {
                        Id = "bp-booster",
                        ParentRecordingIds = new List<string> { "booster" }
                    }
                }
            };

            var committed = new List<Recording> { watched, booster, fragment };

            Assert.True(GhostPlaybackLogic.IsWatchProtectedRecording(
                committed, new List<RecordingTree> { tree }, watchedRecordingIndex: 0, currentIndex: 2));
        }

        [Fact]
        public void Issue316_IsWatchProtectedRecording_ArchivedSplitLineageFallsBackToBranchAncestry()
        {
            var root = new Recording
            {
                RecordingId = "8582d3de9ee74681856352edc49563c3",
                TreeId = "tree-316",
                VesselPersistentId = 100,
                ChainId = "chain-316",
                ChainIndex = 0
            };
            var middle = new Recording
            {
                RecordingId = "06efb0cf37ac493ca3e3fa72c8c2d0d0",
                TreeId = "tree-316",
                VesselPersistentId = 100,
                ChainId = "chain-316",
                ChainIndex = 1
            };
            var watched = new Recording
            {
                RecordingId = "707490bbcabe495895eecadabed34c2b",
                TreeId = "tree-316",
                VesselPersistentId = 100,
                ChainId = "chain-316",
                ChainIndex = 2
            };
            var debrisDuringHold = new Recording
            {
                RecordingId = "aea21e7f06914584be43bebc00ce52f2",
                TreeId = "tree-316",
                VesselPersistentId = 200,
                IsDebris = true,
                LoopSyncParentIdx = -1,
                ParentBranchPointId = "bp-10"
            };
            var debrisAfterHold = new Recording
            {
                RecordingId = "62e4c90147454cc8aec091d2950e6056",
                TreeId = "tree-316",
                VesselPersistentId = 201,
                IsDebris = true,
                LoopSyncParentIdx = -1,
                ParentBranchPointId = "bp-11"
            };

            var tree = new RecordingTree
            {
                Id = "tree-316",
                Recordings = new Dictionary<string, Recording>
                {
                    ["8582d3de9ee74681856352edc49563c3"] = root,
                    ["06efb0cf37ac493ca3e3fa72c8c2d0d0"] = middle,
                    ["707490bbcabe495895eecadabed34c2b"] = watched,
                    ["aea21e7f06914584be43bebc00ce52f2"] = debrisDuringHold,
                    ["62e4c90147454cc8aec091d2950e6056"] = debrisAfterHold
                },
                BranchPoints = new List<BranchPoint>
                {
                    new BranchPoint
                    {
                        Id = "bp-10",
                        ParentRecordingIds = new List<string> { "8582d3de9ee74681856352edc49563c3" }
                    },
                    new BranchPoint
                    {
                        Id = "bp-11",
                        ParentRecordingIds = new List<string> { "8582d3de9ee74681856352edc49563c3" }
                    }
                }
            };

            var committed = new List<Recording> { root, middle, watched, debrisDuringHold, debrisAfterHold };

            Assert.True(GhostPlaybackLogic.IsWatchProtectedRecording(
                committed, new List<RecordingTree> { tree }, watchedRecordingIndex: 2, currentIndex: 3));
            Assert.True(GhostPlaybackLogic.IsWatchProtectedRecording(
                committed, new List<RecordingTree> { tree }, watchedRecordingIndex: 2, currentIndex: 4));
        }

        [Fact]
        public void Issue316_ComputeWatchLineageProtectionUntilUT_ExtendsThroughLateDebris()
        {
            var root = new Recording
            {
                RecordingId = "8582d3de9ee74681856352edc49563c3",
                TreeId = "tree-316",
                VesselPersistentId = 100,
                ChainId = "chain-316",
                ChainIndex = 0
            };
            var middle = new Recording
            {
                RecordingId = "06efb0cf37ac493ca3e3fa72c8c2d0d0",
                TreeId = "tree-316",
                VesselPersistentId = 100,
                ChainId = "chain-316",
                ChainIndex = 1
            };
            var watched = new Recording
            {
                RecordingId = "707490bbcabe495895eecadabed34c2b",
                TreeId = "tree-316",
                VesselPersistentId = 100,
                ChainId = "chain-316",
                ChainIndex = 2
            };
            var debrisDuringHold = new Recording
            {
                RecordingId = "aea21e7f06914584be43bebc00ce52f2",
                TreeId = "tree-316",
                VesselPersistentId = 200,
                IsDebris = true,
                LoopSyncParentIdx = -1,
                ParentBranchPointId = "bp-10",
                ExplicitStartUT = 817.66795427392969,
                ExplicitEndUT = 850.44795427389988
            };
            var debrisAfterHold = new Recording
            {
                RecordingId = "62e4c90147454cc8aec091d2950e6056",
                TreeId = "tree-316",
                VesselPersistentId = 201,
                IsDebris = true,
                LoopSyncParentIdx = -1,
                ParentBranchPointId = "bp-11",
                ExplicitStartUT = 921.89877580711743,
                ExplicitEndUT = 932.71452898094788
            };

            var tree = new RecordingTree
            {
                Id = "tree-316",
                Recordings = new Dictionary<string, Recording>
                {
                    ["8582d3de9ee74681856352edc49563c3"] = root,
                    ["06efb0cf37ac493ca3e3fa72c8c2d0d0"] = middle,
                    ["707490bbcabe495895eecadabed34c2b"] = watched,
                    ["aea21e7f06914584be43bebc00ce52f2"] = debrisDuringHold,
                    ["62e4c90147454cc8aec091d2950e6056"] = debrisAfterHold
                },
                BranchPoints = new List<BranchPoint>
                {
                    new BranchPoint
                    {
                        Id = "bp-10",
                        ParentRecordingIds = new List<string> { "8582d3de9ee74681856352edc49563c3" }
                    },
                    new BranchPoint
                    {
                        Id = "bp-11",
                        ParentRecordingIds = new List<string> { "8582d3de9ee74681856352edc49563c3" }
                    }
                }
            };

            var committed = new List<Recording> { root, middle, watched, debrisDuringHold, debrisAfterHold };

            double protectionUntilUT = GhostPlaybackLogic.ComputeWatchLineageProtectionUntilUT(
                committed, new List<RecordingTree> { tree }, watchedRecordingIndex: 2, currentUT: 820.0);

            Assert.Equal(932.71452898094788, protectionUntilUT, 12);
        }

        [Fact]
        public void Issue316_ComputeWatchLineageProtectionUntilUT_AfterLateDebrisEnds_ReturnsNaN()
        {
            var watched = new Recording
            {
                RecordingId = "watched",
                TreeId = "tree1",
                VesselPersistentId = 100
            };
            var debris = new Recording
            {
                RecordingId = "debris",
                TreeId = "tree1",
                VesselPersistentId = 200,
                IsDebris = true,
                ParentBranchPointId = "bp1",
                ExplicitStartUT = 10,
                ExplicitEndUT = 20
            };
            var tree = new RecordingTree
            {
                Id = "tree1",
                Recordings = new Dictionary<string, Recording>
                {
                    ["watched"] = watched,
                    ["debris"] = debris
                },
                BranchPoints = new List<BranchPoint>
                {
                    new BranchPoint
                    {
                        Id = "bp1",
                        ParentRecordingIds = new List<string> { "watched" }
                    }
                }
            };

            double protectionUntilUT = GhostPlaybackLogic.ComputeWatchLineageProtectionUntilUT(
                new List<Recording> { watched, debris },
                new List<RecordingTree> { tree },
                watchedRecordingIndex: 0,
                currentUT: 25.0);

            Assert.True(double.IsNaN(protectionUntilUT));
        }

        [Fact]
        public void ShouldApplyWarpZoneHideExemption_BeyondOrbitalGhost_ReturnsTrue()
        {
            Assert.True(GhostPlaybackLogic.ShouldApplyWarpZoneHideExemption(
                shouldHideMesh: true,
                zone: RenderingZone.Beyond,
                currentWarpRate: 10f,
                hasOrbitalSegments: true));
        }

        [Fact]
        public void ShouldApplyWarpZoneHideExemption_HiddenTierVisualGhost_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldApplyWarpZoneHideExemption(
                shouldHideMesh: true,
                zone: RenderingZone.Visual,
                currentWarpRate: 10f,
                hasOrbitalSegments: true));
        }

        #endregion

        #region Looped Ghost Spawn Gating

        [Fact]
        public void EvaluateLoopedGhostSpawn_WithinPhysicsBubble_FullFidelity()
        {
            var (shouldSpawn, simplified) = GhostPlaybackLogic.EvaluateLoopedGhostSpawn(1000);
            Assert.True(shouldSpawn);
            Assert.False(simplified);
        }

        [Fact]
        public void EvaluateLoopedGhostSpawn_BetweenPhysicsAndSimplified_Simplified()
        {
            var (shouldSpawn, simplified) = GhostPlaybackLogic.EvaluateLoopedGhostSpawn(10000);
            Assert.True(shouldSpawn);
            Assert.True(simplified);
        }

        [Fact]
        public void EvaluateLoopedGhostSpawn_AtSimplifiedBoundary_NotSpawned()
        {
            // At exactly 50km: beyond spawn threshold
            var (shouldSpawn, _) = GhostPlaybackLogic.EvaluateLoopedGhostSpawn(50000);
            Assert.False(shouldSpawn);
        }

        [Fact]
        public void EvaluateLoopedGhostSpawn_FarAway_NotSpawned()
        {
            var (shouldSpawn, _) = GhostPlaybackLogic.EvaluateLoopedGhostSpawn(200000);
            Assert.False(shouldSpawn);
        }

        [Fact]
        public void EvaluateLoopedGhostSpawn_AtPhysicsBoundary_Simplified()
        {
            // Exactly at 2300m: transitions to simplified
            var (shouldSpawn, simplified) = GhostPlaybackLogic.EvaluateLoopedGhostSpawn(2300);
            Assert.True(shouldSpawn);
            Assert.True(simplified);
        }

        #endregion

        #region Mesh Rendering

        [Fact]
        public void ShouldRenderMesh_InPhysicsZone_ReturnsTrue()
        {
            Assert.True(RenderingZoneManager.ShouldRenderMesh(1000));
        }

        [Fact]
        public void ShouldRenderMesh_InVisualZone_ReturnsTrue()
        {
            Assert.True(RenderingZoneManager.ShouldRenderMesh(50000));
        }

        [Fact]
        public void ShouldRenderMesh_InBeyondZone_ReturnsFalse()
        {
            Assert.False(RenderingZoneManager.ShouldRenderMesh(RenderingZoneManager.VisualRangeRadius));
            Assert.False(RenderingZoneManager.ShouldRenderMesh(RenderingZoneManager.VisualRangeRadius + 100000));
        }

        #endregion

        #region Part Event Rendering

        [Fact]
        public void ShouldRenderPartEvents_InPhysicsZone_ReturnsTrue()
        {
            Assert.True(RenderingZoneManager.ShouldRenderPartEvents(1000));
        }

        [Fact]
        public void ShouldRenderPartEvents_InVisualZone_ReturnsFalse()
        {
            Assert.False(RenderingZoneManager.ShouldRenderPartEvents(5000));
        }

        [Fact]
        public void ShouldRenderPartEvents_InBeyondZone_ReturnsFalse()
        {
            Assert.False(RenderingZoneManager.ShouldRenderPartEvents(200000));
        }

        #endregion

        #region Zone Transition Logging

        [Fact]
        public void LogZoneTransition_EmitsLogWithZoneTag()
        {
            RenderingZoneManager.LogZoneTransition(
                "#0 \"TestVessel\"", RenderingZone.Physics, RenderingZone.Visual, 5000);

            Assert.Contains(logLines, l =>
                l.Contains("[Zone]") &&
                l.Contains("Physics->Visual") &&
                l.Contains("TestVessel") &&
                l.Contains("5000m"));
        }

        [Fact]
        public void LogZoneTransition_BeyondTransition_EmitsLog()
        {
            RenderingZoneManager.LogZoneTransition(
                "#3 \"Orbiter\"", RenderingZone.Visual, RenderingZone.Beyond, 150000);

            Assert.Contains(logLines, l =>
                l.Contains("[Zone]") &&
                l.Contains("Visual->Beyond") &&
                l.Contains("Orbiter") &&
                l.Contains("150000m"));
        }

        [Fact]
        public void LogZoneTransition_InwardTransition_EmitsLog()
        {
            RenderingZoneManager.LogZoneTransition(
                "#1 \"Lander\"", RenderingZone.Beyond, RenderingZone.Visual, 100000);

            Assert.Contains(logLines, l =>
                l.Contains("[Zone]") &&
                l.Contains("Beyond->Visual") &&
                l.Contains("Lander"));
        }

        [Fact]
        public void LogZoneTransition_UnresolvedDistance_EmitsUnresolvedInsteadOfMaxValue()
        {
            RenderingZoneManager.LogZoneTransition(
                "#1 \"Relative\"", RenderingZone.Physics, RenderingZone.Beyond, double.MaxValue);

            Assert.Contains(logLines, l =>
                l.Contains("[Zone]") &&
                l.Contains("Physics->Beyond") &&
                l.Contains("dist=unresolved") &&
                !l.Contains("179769313486232"));
        }

        #endregion

        #region Looped Ghost Spawn Decision Logging

        [Fact]
        public void LogLoopedGhostSpawnDecision_Suppressed_EmitsLog()
        {
            RenderingZoneManager.LogLoopedGhostSpawnDecision(
                "#2 \"Shuttle\"", 60000, false, false);

            Assert.Contains(logLines, l =>
                l.Contains("[Zone]") &&
                l.Contains("suppressed") &&
                l.Contains("Shuttle") &&
                l.Contains("60000m"));
        }

        [Fact]
        public void LogLoopedGhostSpawnDecision_SimplifiedSpawn_EmitsLog()
        {
            RenderingZoneManager.LogLoopedGhostSpawnDecision(
                "#4 \"Rover\"", 10000, true, true);

            Assert.Contains(logLines, l =>
                l.Contains("[Zone]") &&
                l.Contains("simplified") &&
                l.Contains("Rover") &&
                l.Contains("no part events"));
        }

        [Fact]
        public void LogLoopedGhostSpawnDecision_FullFidelity_EmitsLog()
        {
            RenderingZoneManager.LogLoopedGhostSpawnDecision(
                "#0 \"Rocket\"", 500, true, false);

            Assert.Contains(logLines, l =>
                l.Contains("[Zone]") &&
                l.Contains("full fidelity") &&
                l.Contains("Rocket"));
        }

        #endregion

        #region GhostPlaybackState Zone Default

        [Fact]
        public void GhostPlaybackState_DefaultZone_IsPhysics()
        {
            var state = new GhostPlaybackState();
            Assert.Equal(RenderingZone.Physics, state.currentZone);
        }

        #endregion

        #region Zone Boundary Constants

        [Fact]
        public void ZoneBoundaries_CorrectValues()
        {
            Assert.Equal(2300.0, RenderingZoneManager.PhysicsBubbleRadius);
            Assert.Equal(120000.0, RenderingZoneManager.VisualRangeRadius);
            Assert.Equal(2300.0, RenderingZoneManager.LoopFullFidelityRadius);
            Assert.Equal(50000.0, RenderingZoneManager.LoopSimplifiedRadius);
        }

        #endregion

        #region Integration: Zone + Loop Spawn Cross-Check

        [Fact]
        public void ZoneAndLoopSpawn_ConsistentAtBoundaries()
        {
            // At physics boundary (2300m):
            // Zone: Visual, Loop: simplified
            var zone = RenderingZoneManager.ClassifyDistance(2300);
            var (spawn, simplified) = RenderingZoneManager.ShouldSpawnLoopedGhostAtDistance(2300);
            Assert.Equal(RenderingZone.Visual, zone);
            Assert.True(spawn);
            Assert.True(simplified);

            // Both agree: no part events at 2300m
            Assert.False(RenderingZoneManager.ShouldRenderPartEvents(2300));
        }

        [Fact]
        public void ZoneAndLoopSpawn_BeyondVisualRange_BothSuppress()
        {
            // At visual range boundary: Beyond zone, loop spawn suppressed (>50km)
            double vr = RenderingZoneManager.VisualRangeRadius;
            var zone = RenderingZoneManager.ClassifyDistance(vr);
            var (spawn, _) = RenderingZoneManager.ShouldSpawnLoopedGhostAtDistance(vr);
            Assert.Equal(RenderingZone.Beyond, zone);
            Assert.False(spawn);
            Assert.False(RenderingZoneManager.ShouldRenderMesh(vr));
        }

        [Fact]
        public void ZoneAndLoopSpawn_InPhysicsBubble_BothFull()
        {
            // At 1000m: Physics zone, loop full fidelity
            var zone = RenderingZoneManager.ClassifyDistance(1000);
            var (spawn, simplified) = RenderingZoneManager.ShouldSpawnLoopedGhostAtDistance(1000);
            Assert.Equal(RenderingZone.Physics, zone);
            Assert.True(spawn);
            Assert.False(simplified);
            Assert.True(RenderingZoneManager.ShouldRenderPartEvents(1000));
            Assert.True(RenderingZoneManager.ShouldRenderMesh(1000));
        }

        #endregion
    }
}
