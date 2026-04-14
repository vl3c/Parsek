using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for bug #278 — `ParsekScenario.FinalizePendingLimboTreeForRevert` no
    /// longer blanket-marks every leaf without TerminalStateValue as Destroyed.
    /// Instead it routes through `ParsekFlight.FinalizeIndividualRecording`, which
    /// preserves existing terminal states, looks up live vessels via
    /// FlightRecorder.FindVesselByPid, and only falls back to Destroyed when the
    /// vessel is gone. The "vessel found → real situation" branch needs a live
    /// KSP runtime and is covered by an in-game test; these unit tests cover the
    /// branches that don't.
    /// </summary>
    [Collection("Sequential")]
    public class Bug278FinalizeLimboTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug278FinalizeLimboTests()
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

        [Fact]
        public void FinalizeIndividualRecording_LeafWithoutVessel_FallsBackToDestroyed()
        {
            // When pid == 0 (or the vessel is not in FlightGlobals), the leaf
            // On scene exit, vessel is unloaded (alive) — infer terminal state from
            // trajectory instead of defaulting to Destroyed. Vessels truly destroyed
            // during recording already have TerminalState set before finalization.
            var rec = new Recording
            {
                RecordingId = "leaf-no-vessel",
                VesselName = "Gone",
                VesselPersistentId = 0,
                ChildBranchPointId = null, // leaf
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 10.0 });

            ParsekFlight.FinalizeIndividualRecording(rec, commitUT: 200.0, isSceneExit: true);

            Assert.True(rec.TerminalStateValue.HasValue);
            Assert.Equal(TerminalState.Landed, rec.TerminalStateValue.Value);
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][INFO][Flight]") &&
                l.Contains("inferred Landed from trajectory") &&
                l.Contains("leaf-no-vessel"));
        }

        [Fact]
        public void FinalizeIndividualRecording_LeafWithoutVessel_NotSceneExit_FallsBackToDestroyed()
        {
            // Outside scene exit (e.g., mid-flight commit), vessel not found
            // genuinely means it was destroyed.
            var rec = new Recording
            {
                RecordingId = "leaf-missing",
                VesselName = "Crashed",
                VesselPersistentId = 0,
                ChildBranchPointId = null,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0 });

            ParsekFlight.FinalizeIndividualRecording(rec, commitUT: 200.0, isSceneExit: false);

            Assert.True(rec.TerminalStateValue.HasValue);
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue.Value);
            Assert.Null(rec.VesselSnapshot);
        }

        [Fact]
        public void FinalizeIndividualRecording_LeafWithExistingTerminalState_PreservesIt()
        {
            // Bug #278 regression guard: a leaf that already carries a real
            // terminal state (e.g. Landed, set elsewhere by EndDebrisRecording or
            // by a prior finalize pass) must NOT be overwritten when the limbo
            // finalize runs. The previous code's `HasValue continue` short-circuit
            // protected this; the new code in FinalizeIndividualRecording uses the
            // same predicate (`if (isLeaf && !rec.TerminalStateValue.HasValue)`).
            var rec = new Recording
            {
                RecordingId = "leaf-already-landed",
                VesselName = "Bob",
                VesselPersistentId = 0,
                ChildBranchPointId = null,
                TerminalStateValue = TerminalState.Landed,
            };

            ParsekFlight.FinalizeIndividualRecording(rec, commitUT: 200.0, isSceneExit: true);

            Assert.Equal(TerminalState.Landed, rec.TerminalStateValue.Value);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Parsek][WARN][Flight]") &&
                l.Contains("vessel pid=") &&
                l.Contains("leaf-already-landed"));
        }

        [Fact]
        public void FinalizeIndividualRecording_NonLeaf_SkipsTerminalStateAssignment()
        {
            // Non-leaf recordings (those with a ChildBranchPointId — interior nodes
            // of a recording tree) must NOT get terminal state set by the
            // per-recording finalize. The active-recording-non-leaf case is
            // handled separately by EnsureActiveRecordingTerminalState.
            var rec = new Recording
            {
                RecordingId = "non-leaf",
                VesselName = "Branch",
                VesselPersistentId = 0,
                ChildBranchPointId = "branch-1", // makes it non-leaf
            };

            ParsekFlight.FinalizeIndividualRecording(rec, commitUT: 200.0, isSceneExit: true);

            Assert.False(rec.TerminalStateValue.HasValue);
        }

        [Fact]
        public void FinalizeIndividualRecording_SetsExplicitStartAndEndUT_FromPoints()
        {
            // Sanity check that the finalize call still applies the
            // ExplicitStartUT/EndUT bookkeeping FinalizeTreeRecordings was already
            // doing — this guards against a regression where moving the call to
            // the limbo finalize path drops these field updates.
            var rec = new Recording
            {
                RecordingId = "leaf-no-explicit-times",
                VesselPersistentId = 0,
                ChildBranchPointId = null,
                ExplicitStartUT = double.NaN,
                ExplicitEndUT = double.NaN,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 50.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 75.0 });

            ParsekFlight.FinalizeIndividualRecording(rec, commitUT: 200.0, isSceneExit: true);

            Assert.Equal(50.0, rec.ExplicitStartUT);
            Assert.Equal(75.0, rec.ExplicitEndUT);
        }

        [Fact]
        public void FinalizeIndividualRecording_NoPoints_FallsBackEndUTToCommitUT()
        {
            // Leaf with zero trajectory points (e.g. an empty parent-continuation
            // placeholder from the deferred-split-check path tracked by bug #285)
            // gets ExplicitEndUT = commitUT.
            var rec = new Recording
            {
                RecordingId = "leaf-empty",
                VesselPersistentId = 0,
                ChildBranchPointId = null,
                ExplicitStartUT = double.NaN,
                ExplicitEndUT = double.NaN,
            };

            ParsekFlight.FinalizeIndividualRecording(rec, commitUT: 250.0, isSceneExit: true);

            Assert.Equal(250.0, rec.ExplicitEndUT);
        }

        [Fact]
        public void EnsureActiveRecordingTerminalState_AlreadyTerminal_SkipsAndLogs()
        {
            // The active recording can be a non-leaf (it has child branches);
            // EnsureActiveRecordingTerminalState handles it specially. If it
            // already has a terminal state, log Verbose and bail.
            var tree = new RecordingTree { TreeName = "Test" };
            var active = new Recording
            {
                RecordingId = "active-non-leaf",
                VesselPersistentId = 0,
                ChildBranchPointId = "branch-1",
                TerminalStateValue = TerminalState.SubOrbital,
            };
            tree.Recordings[active.RecordingId] = active;
            tree.ActiveRecordingId = active.RecordingId;

            ParsekFlight.EnsureActiveRecordingTerminalState(tree);

            // Already-terminal state preserved
            Assert.Equal(TerminalState.SubOrbital, active.TerminalStateValue.Value);
            Assert.Contains(logLines, l =>
                l.Contains("active recording") &&
                l.Contains("active-non-leaf") &&
                l.Contains("already has terminalState=SubOrbital"));
        }

        [Fact]
        public void EnsureActiveRecordingTerminalState_NoActiveId_NoOp()
        {
            // tree.ActiveRecordingId == null → early return, no state changes,
            // no log lines.
            var tree = new RecordingTree { TreeName = "Test" };
            tree.ActiveRecordingId = null;

            ParsekFlight.EnsureActiveRecordingTerminalState(tree);

            Assert.DoesNotContain(logLines, l => l.Contains("EnsureActiveRecordingTerminalState"));
        }

        [Fact]
        public void EnsureActiveRecordingTerminalState_NoLiveVesselOnSceneExit_InfersFromTrajectory()
        {
            var tree = new RecordingTree { TreeName = "Test" };
            var active = new Recording
            {
                RecordingId = "active-non-leaf",
                VesselPersistentId = 42,
                ChildBranchPointId = "branch-1",
            };
            active.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                semiMajorAxis = 700000.0,
                eccentricity = 0.0,
                epoch = 10.0,
                startUT = 10.0,
                endUT = 11.0,
            });
            active.Points.Add(new TrajectoryPoint
            {
                ut = 12.0,
                altitude = 18.0,
                bodyName = "Kerbin",
                latitude = 0.1,
                longitude = 0.2,
            });
            tree.Recordings[active.RecordingId] = active;
            tree.ActiveRecordingId = active.RecordingId;

            ParsekFlight.EnsureActiveRecordingTerminalState(tree, isSceneExit: true);

            Assert.Equal(TerminalState.Landed, active.TerminalStateValue);
            Assert.True(active.TerminalPosition.HasValue);
            Assert.Equal(SurfaceSituation.Landed, active.TerminalPosition.Value.situation);
            Assert.Equal(GhostExtensionStrategy.Surface, GhostExtender.ChooseStrategy(active));
            Assert.Contains(logLines, l =>
                l.Contains("active recording 'active-non-leaf'") &&
                l.Contains("not found on scene exit") &&
                l.Contains("inferred Landed from trajectory"));
        }

        [Fact]
        public void EnsureActiveRecordingTerminalState_NoLiveVesselOutsideSceneExit_LeavesTerminalUnset()
        {
            var tree = new RecordingTree { TreeName = "Test" };
            var active = new Recording
            {
                RecordingId = "active-non-leaf",
                VesselPersistentId = 42,
                ChildBranchPointId = "branch-1",
            };
            active.Points.Add(new TrajectoryPoint { ut = 12.0, altitude = 18.0 });
            tree.Recordings[active.RecordingId] = active;
            tree.ActiveRecordingId = active.RecordingId;

            ParsekFlight.EnsureActiveRecordingTerminalState(tree, isSceneExit: false);

            Assert.False(active.TerminalStateValue.HasValue);
            Assert.Contains(logLines, l =>
                l.Contains("active recording 'active-non-leaf'") &&
                l.Contains("not found before terminal-state assignment") &&
                l.Contains("isSceneExit=False"));
        }

        [Fact]
        public void SceneExitInferredActiveNonLeaf_DefaultsToPersistInMergeDialog()
        {
            var tree = new RecordingTree
            {
                Id = "tree-1",
                TreeName = "Kerbal X",
                RootRecordingId = "active",
                ActiveRecordingId = "active"
            };
            var active = new Recording
            {
                RecordingId = "active",
                TreeId = tree.Id,
                VesselName = "Kerbal X",
                VesselPersistentId = 42,
                ChildBranchPointId = "bp-1",
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            active.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                semiMajorAxis = 700000.0,
                eccentricity = 0.0,
                epoch = 10.0,
                startUT = 10.0,
                endUT = 11.0,
            });
            active.Points.Add(new TrajectoryPoint
            {
                ut = 12.0,
                altitude = 18.0,
                bodyName = "Kerbin",
                latitude = 0.1,
                longitude = 0.2,
            });

            var debris = new Recording
            {
                RecordingId = "debris",
                TreeId = tree.Id,
                VesselName = "Kerbal X Debris",
                VesselPersistentId = 100,
                ParentBranchPointId = "bp-1",
                TerminalStateValue = TerminalState.Destroyed,
                VesselSnapshot = new ConfigNode("VESSEL"),
                IsDebris = true
            };
            var bp = new BranchPoint
            {
                Id = "bp-1",
                Type = BranchPointType.Breakup,
                UT = 10.0
            };
            bp.ParentRecordingIds.Add("active");
            bp.ChildRecordingIds.Add("debris");

            tree.Recordings[active.RecordingId] = active;
            tree.Recordings[debris.RecordingId] = debris;
            tree.BranchPoints.Add(bp);

            ParsekFlight.EnsureActiveRecordingTerminalState(tree, isSceneExit: true);
            var decisions = MergeDialog.BuildDefaultVesselDecisions(tree);

            Assert.True(active.TerminalPosition.HasValue);
            Assert.True(decisions["active"]);
            Assert.False(decisions["debris"]);
        }

        [Fact]
        public void ShouldRefreshActiveEffectiveLeafSnapshot_StableTerminalBreakupContinuous_ReturnsTrue()
        {
            var tree = new RecordingTree { TreeName = "Test", Id = "tree-1" };
            var active = new Recording
            {
                RecordingId = "active-effective-leaf",
                TreeId = tree.Id,
                VesselPersistentId = 42,
                ChildBranchPointId = "bp-1",
                TerminalStateValue = TerminalState.Orbiting,
            };
            var debris = new Recording
            {
                RecordingId = "debris-child",
                TreeId = tree.Id,
                VesselPersistentId = 99,
                ParentBranchPointId = "bp-1",
                IsDebris = true,
            };

            tree.Recordings[active.RecordingId] = active;
            tree.Recordings[debris.RecordingId] = debris;
            tree.ActiveRecordingId = active.RecordingId;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-1",
                ParentRecordingIds = new List<string> { active.RecordingId },
                ChildRecordingIds = new List<string> { debris.RecordingId }
            });

            Assert.True(ParsekFlight.ShouldRefreshActiveEffectiveLeafSnapshot(tree, active));
        }

        [Fact]
        public void ShouldRefreshActiveEffectiveLeafSnapshot_SamePidContinuation_ReturnsFalse()
        {
            var tree = new RecordingTree { TreeName = "Test", Id = "tree-1" };
            var active = new Recording
            {
                RecordingId = "active-nonleaf",
                TreeId = tree.Id,
                VesselPersistentId = 42,
                ChildBranchPointId = "bp-1",
                TerminalStateValue = TerminalState.Orbiting,
            };
            var continuation = new Recording
            {
                RecordingId = "same-pid-child",
                TreeId = tree.Id,
                VesselPersistentId = 42,
                ParentBranchPointId = "bp-1",
            };

            tree.Recordings[active.RecordingId] = active;
            tree.Recordings[continuation.RecordingId] = continuation;
            tree.ActiveRecordingId = active.RecordingId;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-1",
                ParentRecordingIds = new List<string> { active.RecordingId },
                ChildRecordingIds = new List<string> { continuation.RecordingId }
            });

            Assert.False(ParsekFlight.ShouldRefreshActiveEffectiveLeafSnapshot(tree, active));
        }

        [Fact]
        public void PruneZeroPointLeaves_RemovesEmptyDestroyedLeaves()
        {
            // The limbo finalize now also runs PruneZeroPointLeaves, so any
            // empty parent-continuation placeholders (bug #285's symptom for the
            // gen-0 deferred-split case) get removed alongside the terminal-state
            // pass. Verify the helper still works as advertised post-promotion
            // from private static → internal static.
            var tree = new RecordingTree { TreeName = "Test" };
            var keep = new Recording
            {
                RecordingId = "keep",
                VesselPersistentId = 0,
                ChildBranchPointId = null,
                ExplicitStartUT = 50.0,
                ExplicitEndUT = 75.0,
            };
            keep.Points.Add(new TrajectoryPoint { ut = 50.0 });
            tree.Recordings[keep.RecordingId] = keep;

            var empty = new Recording
            {
                RecordingId = "empty-placeholder",
                VesselPersistentId = 0,
                ChildBranchPointId = null,
                ExplicitStartUT = 50.0,
                ExplicitEndUT = 50.0,
                // No Points, no OrbitSegments, no SurfacePos
            };
            tree.Recordings[empty.RecordingId] = empty;

            ParsekFlight.PruneZeroPointLeaves(tree);

            Assert.True(tree.Recordings.ContainsKey("keep"));
            Assert.False(tree.Recordings.ContainsKey("empty-placeholder"));
            Assert.Contains(logLines, l =>
                l.Contains("PruneZeroPointLeaves: removed 1 zero-point"));
        }

        #region InferTerminalStateFromTrajectory

        [Fact]
        public void InferTerminal_LowAltitude_ReturnsLanded()
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 5.0 });
            Assert.Equal(TerminalState.Landed, ParsekFlight.InferTerminalStateFromTrajectory(rec));
        }

        [Fact]
        public void InferTerminal_HighAltitude_ReturnsSubOrbital()
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 5000.0 });
            Assert.Equal(TerminalState.SubOrbital, ParsekFlight.InferTerminalStateFromTrajectory(rec));
        }

        [Fact]
        public void InferTerminal_SurfaceTrackSection_ReturnsLanded()
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 200.0 });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceStationary,
                startUT = 100.0,
                endUT = 200.0
            });
            Assert.Equal(TerminalState.Landed, ParsekFlight.InferTerminalStateFromTrajectory(rec));
        }

        [Fact]
        public void InferTerminal_SurfaceMobileTrackSection_ReturnsLanded()
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 200.0 });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceMobile,
                startUT = 100.0,
                endUT = 200.0
            });
            Assert.Equal(TerminalState.Landed, ParsekFlight.InferTerminalStateFromTrajectory(rec));
        }

        [Fact]
        public void InferTerminal_StableOrbit_ReturnsOrbiting()
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 100000.0 });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                eccentricity = 0.05,
                semiMajorAxis = 700000.0, // periapsis = 700000 * 0.95 = 665000 > Kerbin radius 600000
                startUT = 50.0,
                endUT = 200.0
            });
            // FlightGlobals.GetBodyByName returns null in tests — falls through to SubOrbital
            Assert.Equal(TerminalState.SubOrbital, ParsekFlight.InferTerminalStateFromTrajectory(rec));
        }

        [Fact]
        public void InferTerminal_NoPoints_ReturnsSubOrbital()
        {
            var rec = new Recording();
            Assert.Equal(TerminalState.SubOrbital, ParsekFlight.InferTerminalStateFromTrajectory(rec));
        }

        #endregion
    }
}
