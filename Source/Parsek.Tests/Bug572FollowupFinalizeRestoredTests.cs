using System;
using System.Collections.Generic;
using Parsek.InGameTests;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the PR #572 second-order data-loss companion fix:
    /// `ParsekFlight.FinalizeIndividualRecording` and
    /// `EnsureActiveRecordingTerminalState` must not bulldoze a recording's
    /// terminal state into Landed/Splashed when the recording was just
    /// repaired from the committed tree this frame
    /// (`Recording.RestoredFromCommittedTreeThisFrame`). That single flag
    /// is the entire gate. An orbital-evidence clause was considered as
    /// Option D in the design plan
    /// (`docs/dev/plans/refly-finalize-stripped-vessel-landed-fix.md`)
    /// and rejected because the legitimate orbit-then-land case
    /// (`Bug278FinalizeLimboTests.EnsureActiveRecordingTerminalState_NoLiveVesselOnSceneExit_InfersFromTrajectory`,
    /// `SceneExitInferredActiveNonLeaf_DefaultsToPersistInMergeDialog`)
    /// shares the same shape; the `OrbitThenLand_StillInfersLanded` case
    /// below pins that the unrestored orbit-then-land path still infers
    /// Landed.
    ///
    /// <para>Reproduces the 2026-04-25_2334 playtest where the capsule
    /// recording <c>66be32fa…</c>, repaired from the committed tree at
    /// <c>SaveActiveTreeIfAny</c>, was clobbered to <c>Landed</c> by
    /// <c>FinalizeTreeRecordings</c> two log lines later because the live
    /// pid had been Re-Fly-stripped and the last trajectory point sat at
    /// 10.6 m altitude (very early atmospheric ascent point — not a
    /// landing).</para>
    /// </summary>
    [Collection("Sequential")]
    public class Bug572FollowupFinalizeRestoredTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly VesselSpawner.ResolveBodyIndexDelegate originalBodyIndexResolver;

        public Bug572FollowupFinalizeRestoredTests()
        {
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            originalBodyIndexResolver = VesselSpawner.BodyIndexResolverForTesting;
        }

        public void Dispose()
        {
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            TestBodyRegistry.Reset();
            VesselSpawner.BodyIndexResolverForTesting = originalBodyIndexResolver;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // -- The user's exact case (the regression) --------------------------

        /// <summary>
        /// User scenario from logs/2026-04-25_2334_refly-followup-test/KSP.log:15829.
        /// Capsule recording was repaired from the committed tree at SaveActiveTreeIfAny.
        /// Live pid was Re-Fly-stripped earlier in the session. The last trajectory
        /// point sits at altitude=10.6m (atmospheric ascent point, not a landing). The
        /// recording also has high MaxDistanceFromLaunch from its earlier orbital
        /// portion. Finalize must NOT stamp Landed; it must leave terminal unset and
        /// log the structured skip line.
        /// </summary>
        [Fact]
        public void FinalizeIndividualRecording_RestoredFromCommittedTree_StripCasualty_DoesNotInferLanded()
        {
            var rec = new Recording
            {
                RecordingId = "66be32fa-restored-strip-casualty",
                VesselName = "Kerbal X",
                VesselPersistentId = 2708531065u,
                ChildBranchPointId = null, // leaf
                MaxDistanceFromLaunch = 1198159.0,
                RestoredFromCommittedTreeThisFrame = true,
            };
            // Trajectory looks like a sub-orbital ballistic from launch site —
            // the post-orbit data was pruned at chain segmentation, so the last
            // point is back near the surface.
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 75000.0, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, altitude = 10.6, bodyName = "Kerbin" });

            ParsekFlight.FinalizeIndividualRecording(rec, commitUT: 250.0, isSceneExit: true);

            Assert.False(rec.TerminalStateValue.HasValue);
            Assert.False(rec.RestoredFromCommittedTreeThisFrame); // gate cleared on read
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][INFO][Flight]") &&
                l.Contains("FinalizeTreeRecordings: skipping Landed/Splashed inference") &&
                l.Contains("66be32fa-restored-strip-casualty") &&
                l.Contains("repaired from committed tree this frame"));
            Assert.Contains(logLines, l =>
                l.Contains("66be32fa-restored-strip-casualty") &&
                l.Contains("lastPtAlt=10") &&
                l.Contains("maxDist=1198159") &&
                l.Contains("orbitSegs=0"));
        }

        /// <summary>
        /// Same restored-record gate, but covering the scene-exit finalization cache
        /// path that runs before trajectory inference. A stale background cache is
        /// scene-exit evidence, not committed-tree truth, so it must not stamp a
        /// terminal state onto the just-repaired recording.
        /// </summary>
        [Fact]
        public void FinalizeIndividualRecording_RestoredFromCommittedTree_SkipsCacheFallback()
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording recording, Vessel vessel, double commitUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    result = default(IncompleteBallisticFinalizationResult);
                    return false;
                };

            var rec = new Recording
            {
                RecordingId = "restored-cache-fallback-skip",
                VesselName = "Kerbal X",
                VesselPersistentId = 2708531065u,
                ChildBranchPointId = null,
                ExplicitEndUT = 200.0,
                RestoredFromCommittedTreeThisFrame = true,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, altitude = 10.6, bodyName = "Kerbin" });

            RecordingFinalizationCache cache = MakeDestroyedCache(
                rec.RecordingId,
                rec.VesselPersistentId,
                terminalUT: 260.0);

            ParsekFlight.FinalizeIndividualRecording(
                rec,
                commitUT: 240.0,
                isSceneExit: true,
                finalizationCache: cache);

            Assert.False(rec.TerminalStateValue.HasValue);
            Assert.Equal(200.0, rec.ExplicitEndUT);
            Assert.Empty(rec.OrbitSegments);
            Assert.False(rec.RestoredFromCommittedTreeThisFrame);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[FinalizerCache]") &&
                l.Contains("Apply accepted") &&
                l.Contains("restored-cache-fallback-skip"));
            Assert.Contains(logLines, l =>
                l.Contains("skipping Landed/Splashed inference") &&
                l.Contains("restored-cache-fallback-skip") &&
                l.Contains("repaired from committed tree this frame"));
        }

        /// <summary>
        /// If the committed-tree repair brought back an explicit terminal state, the
        /// destroyed-cache repair path must not reinterpret that state during the
        /// same scene-exit pass.
        /// </summary>
        [Fact]
        public void FinalizeIndividualRecording_RestoredFromCommittedTree_PreservesExistingTerminalAgainstCacheRepair()
        {
            var rec = new Recording
            {
                RecordingId = "restored-terminal-cache-repair-skip",
                VesselName = "Kerbal X",
                VesselPersistentId = 2708531065u,
                ChildBranchPointId = null,
                ExplicitEndUT = 200.0,
                TerminalStateValue = TerminalState.SubOrbital,
                RestoredFromCommittedTreeThisFrame = true,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, altitude = 5000.0, bodyName = "Kerbin" });

            RecordingFinalizationCache cache = MakeDestroyedCache(
                rec.RecordingId,
                rec.VesselPersistentId,
                terminalUT: 230.0);

            ParsekFlight.FinalizeIndividualRecording(
                rec,
                commitUT: 240.0,
                isSceneExit: true,
                finalizationCache: cache);

            Assert.Equal(TerminalState.SubOrbital, rec.TerminalStateValue);
            Assert.Equal(200.0, rec.ExplicitEndUT);
            Assert.Empty(rec.OrbitSegments);
            Assert.False(rec.RestoredFromCommittedTreeThisFrame);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[FinalizerCache]") &&
                l.Contains("Apply accepted") &&
                l.Contains("restored-terminal-cache-repair-skip"));
            Assert.Contains(logLines, l =>
                l.Contains("preserving repaired terminalState=SubOrbital") &&
                l.Contains("restored-terminal-cache-repair-skip"));
        }

        /// <summary>
        /// Full-tree regression for the 5.5 xhigh review finding: active leaf
        /// recordings are visited by the per-recording finalize pass, then used to
        /// be visited again by EnsureActiveRecordingTerminalState. The first pass
        /// clears the restored flag after preserving the repair; the second pass
        /// must not run for active leaves or it can apply stale cache/inference.
        /// </summary>
        [Fact]
        public void FinalizeTreeRecordingsAfterFlush_RestoredActiveLeaf_DoesNotRunActiveEnsureSecondPass()
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording recording, Vessel vessel, double commitUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    result = default(IncompleteBallisticFinalizationResult);
                    return false;
                };

            var tree = new RecordingTree
            {
                Id = "tree-restored-active-leaf",
                TreeName = "Restored Active Leaf",
                ActiveRecordingId = "restored-active-leaf"
            };
            var active = new Recording
            {
                RecordingId = tree.ActiveRecordingId,
                TreeId = tree.Id,
                VesselName = "Kerbal X",
                VesselPersistentId = 2708531065u,
                ChildBranchPointId = null,
                ExplicitEndUT = 200.0,
                RestoredFromCommittedTreeThisFrame = true,
            };
            active.Points.Add(new TrajectoryPoint { ut = 200.0, altitude = 10.6, bodyName = "Kerbin" });
            tree.Recordings[active.RecordingId] = active;

            RecordingFinalizationCache cache = MakeDestroyedCache(
                active.RecordingId,
                active.VesselPersistentId,
                terminalUT: 260.0);

            ParsekFlight.FinalizeTreeRecordingsAfterFlush(
                tree,
                commitUT: 240.0,
                isSceneExit: true,
                resolveFinalizationCache: recording => recording.RecordingId == active.RecordingId
                    ? cache
                    : null);

            Assert.False(active.TerminalStateValue.HasValue);
            Assert.Equal(200.0, active.ExplicitEndUT);
            Assert.Empty(active.OrbitSegments);
            Assert.False(active.RestoredFromCommittedTreeThisFrame);
            Assert.False(ParsekFlight.ShouldEnsureActiveRecordingTerminalState(tree));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[FinalizerCache]") &&
                l.Contains("Apply accepted") &&
                l.Contains("restored-active-leaf"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("active recording 'restored-active-leaf'") &&
                l.Contains("inferred"));
            Assert.Contains(logLines, l =>
                l.Contains("skipping Landed/Splashed inference") &&
                l.Contains("restored-active-leaf") &&
                l.Contains("repaired from committed tree this frame"));
        }

        /// <summary>
        /// Same scenario but on the active-recording-non-leaf path
        /// (`EnsureActiveRecordingTerminalState`). When the active recording is
        /// non-leaf, has no terminal state, and was repaired from the committed
        /// tree this frame, the active-state finalizer must also skip the
        /// surface inference.
        /// </summary>
        [Fact]
        public void EnsureActiveRecordingTerminalState_RestoredFromCommittedTree_DoesNotInferLanded()
        {
            var tree = new RecordingTree { TreeName = "Test" };
            var active = new Recording
            {
                RecordingId = "active-non-leaf-restored",
                VesselPersistentId = 2708531065u,
                ChildBranchPointId = "branch-1", // non-leaf
                MaxDistanceFromLaunch = 1198159.0,
                RestoredFromCommittedTreeThisFrame = true,
            };
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

            Assert.False(active.TerminalStateValue.HasValue);
            Assert.False(active.RestoredFromCommittedTreeThisFrame);
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][INFO][Flight]") &&
                l.Contains("FinalizeTreeRecordings: skipping Landed/Splashed inference") &&
                l.Contains("active recording 'active-non-leaf-restored'") &&
                l.Contains("repaired from committed tree this frame"));
        }

        /// <summary>
        /// The active non-leaf path also sees finalization-cache data before its
        /// trajectory inference fallback. It must preserve the committed-tree repair
        /// and skip the cache for the same reason as the leaf path.
        /// </summary>
        [Fact]
        public void EnsureActiveRecordingTerminalState_RestoredFromCommittedTree_SkipsCacheFallback()
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording recording, Vessel vessel, double commitUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    result = default(IncompleteBallisticFinalizationResult);
                    return false;
                };

            var tree = new RecordingTree { TreeName = "Test" };
            var active = new Recording
            {
                RecordingId = "active-restored-cache-fallback-skip",
                VesselName = "Kerbal X",
                VesselPersistentId = 2708531065u,
                ChildBranchPointId = "branch-1",
                ExplicitEndUT = 200.0,
                RestoredFromCommittedTreeThisFrame = true,
            };
            active.Points.Add(new TrajectoryPoint
            {
                ut = 200.0,
                altitude = 18.0,
                bodyName = "Kerbin",
                latitude = 0.1,
                longitude = 0.2,
            });
            tree.Recordings[active.RecordingId] = active;
            tree.ActiveRecordingId = active.RecordingId;

            RecordingFinalizationCache cache = MakeDestroyedCache(
                active.RecordingId,
                active.VesselPersistentId,
                terminalUT: 260.0);

            ParsekFlight.EnsureActiveRecordingTerminalState(
                tree,
                isSceneExit: true,
                commitUT: 240.0,
                finalizationCache: cache);

            Assert.False(active.TerminalStateValue.HasValue);
            Assert.Equal(200.0, active.ExplicitEndUT);
            Assert.Empty(active.OrbitSegments);
            Assert.False(active.RestoredFromCommittedTreeThisFrame);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[FinalizerCache]") &&
                l.Contains("Apply accepted") &&
                l.Contains("active-restored-cache-fallback-skip"));
            Assert.Contains(logLines, l =>
                l.Contains("skipping Landed/Splashed inference") &&
                l.Contains("active recording 'active-restored-cache-fallback-skip'") &&
                l.Contains("repaired from committed tree this frame"));
        }

        [Fact]
        public void ShouldEnsureActiveRecordingTerminalState_TrueNonLeaf_ReturnsTrue()
        {
            var tree = new RecordingTree
            {
                Id = "tree-true-non-leaf",
                TreeName = "True Non Leaf",
                ActiveRecordingId = "active-true-non-leaf"
            };
            var active = new Recording
            {
                RecordingId = tree.ActiveRecordingId,
                TreeId = tree.Id,
                VesselPersistentId = 42u,
                ChildBranchPointId = "bp-1"
            };
            var child = new Recording
            {
                RecordingId = "child-same-pid",
                TreeId = tree.Id,
                VesselPersistentId = 42u,
                ParentBranchPointId = "bp-1"
            };
            tree.Recordings[active.RecordingId] = active;
            tree.Recordings[child.RecordingId] = child;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-1",
                ChildRecordingIds = new List<string> { child.RecordingId }
            });

            Assert.True(ParsekFlight.ShouldEnsureActiveRecordingTerminalState(tree));
        }

        [Fact]
        public void ShouldEnsureActiveRecordingTerminalState_EffectiveLeaf_ReturnsFalse()
        {
            var tree = new RecordingTree
            {
                Id = "tree-effective-leaf",
                TreeName = "Effective Leaf",
                ActiveRecordingId = "active-effective-leaf"
            };
            var active = new Recording
            {
                RecordingId = tree.ActiveRecordingId,
                TreeId = tree.Id,
                VesselPersistentId = 42u,
                ChildBranchPointId = "bp-1"
            };
            var child = new Recording
            {
                RecordingId = "child-other-pid",
                TreeId = tree.Id,
                VesselPersistentId = 99u,
                ParentBranchPointId = "bp-1"
            };
            tree.Recordings[active.RecordingId] = active;
            tree.Recordings[child.RecordingId] = child;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-1",
                ChildRecordingIds = new List<string> { child.RecordingId }
            });

            Assert.False(ParsekFlight.ShouldEnsureActiveRecordingTerminalState(tree));
        }

        // -- Legitimate Landed inference (regression guard) -------------------

        /// <summary>
        /// Sanity: a normally-finalized scene-exit recording with a low last-
        /// point altitude and the restore flag NOT set must still hit the
        /// original Landed inference path. PR #572 follow-up must not regress
        /// legitimate "vessel landed and unloaded".
        /// </summary>
        [Fact]
        public void FinalizeIndividualRecording_NormalUnloadedLanding_StillInfersLanded()
        {
            var rec = new Recording
            {
                RecordingId = "normal-landed",
                VesselName = "Lander",
                VesselPersistentId = 0, // pid==0 so live lookup fails
                ChildBranchPointId = null,
                MaxDistanceFromLaunch = 12345.0, // sub-orbital distance, well below 70 km
                // RestoredFromCommittedTreeThisFrame = false (default)
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 5.0 });

            ParsekFlight.FinalizeIndividualRecording(rec, commitUT: 200.0, isSceneExit: true);

            Assert.True(rec.TerminalStateValue.HasValue);
            Assert.Equal(TerminalState.Landed, rec.TerminalStateValue.Value);
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][INFO][Flight]") &&
                l.Contains("inferred Landed from trajectory") &&
                l.Contains("normal-landed"));
            // Confirm the new skip line did NOT fire
            Assert.DoesNotContain(logLines, l =>
                l.Contains("skipping Landed/Splashed inference") &&
                l.Contains("normal-landed"));
        }

        // -- Orbit-then-land regression guard --------------------------------

        /// <summary>
        /// Orbit-then-land legitimate case: the recording reached a stable
        /// orbit and then deorbited to a low-altitude landing. The restore
        /// flag is NOT set. The original Landed inference must still run —
        /// the existing finalize tests
        /// (`Bug278FinalizeLimboTests.EnsureActiveRecordingTerminalState_NoLiveVesselOnSceneExit_InfersFromTrajectory`
        /// and `SceneExitInferredActiveNonLeaf_DefaultsToPersistInMergeDialog`)
        /// already pin this on `EnsureActiveRecordingTerminalState`. Here we
        /// pin it on the leaf finalize path too.
        /// </summary>
        [Fact]
        public void FinalizeIndividualRecording_OrbitThenLand_StillInfersLanded()
        {
            var rec = new Recording
            {
                RecordingId = "orbit-then-land",
                VesselPersistentId = 0,
                ChildBranchPointId = null,
                MaxDistanceFromLaunch = 750_000.0,
                // RestoredFromCommittedTreeThisFrame = false (default)
            };
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                semiMajorAxis = 700_000.0,
                eccentricity = 0.05,
                startUT = 50.0,
                endUT = 150.0,
            });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, altitude = 5.0, bodyName = "Kerbin" });

            ParsekFlight.FinalizeIndividualRecording(rec, commitUT: 250.0, isSceneExit: true);

            Assert.True(rec.TerminalStateValue.HasValue);
            Assert.Equal(TerminalState.Landed, rec.TerminalStateValue.Value);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("skipping Landed/Splashed inference") &&
                l.Contains("orbit-then-land"));
        }

        // -- Restore-then-finalize integration (matches scenario.cs producer) -

        /// <summary>
        /// End-to-end pin: simulate the producer side
        /// (`RestoreCommittedSidecarPayloadIntoActiveTreeRecording` setting
        /// the flag) by simply setting the flag, then run the finalize path
        /// and assert the gate fires. Confirms the producer/consumer wiring
        /// is wired up correctly without spinning up the live restore helper.
        /// </summary>
        [Fact]
        public void RestoredRecording_FinalizeRespectsRestoredFlag_ClearsAfterRead()
        {
            var rec = new Recording
            {
                RecordingId = "restored-then-finalized",
                VesselPersistentId = 12345u,
                ChildBranchPointId = null,
                RestoredFromCommittedTreeThisFrame = true,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 1.0 });

            // First finalize: gate fires
            ParsekFlight.FinalizeIndividualRecording(rec, commitUT: 200.0, isSceneExit: true);
            Assert.False(rec.TerminalStateValue.HasValue);
            Assert.False(rec.RestoredFromCommittedTreeThisFrame);
            Assert.Contains(logLines, l =>
                l.Contains("skipping Landed/Splashed inference") &&
                l.Contains("restored-then-finalized") &&
                l.Contains("repaired from committed tree this frame"));
        }

        private static RecordingFinalizationCache MakeDestroyedCache(
            string recordingId,
            uint vesselPersistentId,
            double terminalUT)
        {
            var cache = new RecordingFinalizationCache
            {
                RecordingId = recordingId,
                VesselPersistentId = vesselPersistentId,
                Owner = FinalizationCacheOwner.BackgroundLoaded,
                Status = FinalizationCacheStatus.Fresh,
                CachedAtUT = terminalUT - 5.0,
                RefreshReason = "test-cache",
                LastObservedUT = terminalUT - 5.0,
                LastObservedBodyName = "Kerbin",
                LastSituation = Vessel.Situations.FLYING,
                LastWasInAtmosphere = true,
                TailStartsAtUT = 200.0,
                TerminalUT = terminalUT,
                TerminalState = TerminalState.Destroyed,
                TerminalBodyName = "Kerbin"
            };
            cache.PredictedSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 200.0,
                endUT = terminalUT,
                semiMajorAxis = 700000.0,
                eccentricity = 0.1,
                inclination = 2.0,
                isPredicted = true
            });
            return cache;
        }
    }
}
