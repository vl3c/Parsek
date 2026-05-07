using System.Collections.Generic;
using Parsek;
using UnityEngine;

namespace Parsek.Tests.Harness
{
    /// <summary>
    /// Scenario factory for the resolver-level regression harness.
    ///
    /// Each <see cref="Build"/> method assembles a self-contained
    /// <see cref="HarnessScenario"/> that exercises one resolver path —
    /// Absolute, Relative-with-fixed-anchor, Re-Fly walk-back, loop
    /// rejection. PR 1 ships scenarios 1, 2, 6, 9 (the cases whose hashes do
    /// NOT change in PR 3); debris baselines (scenarios 4, 5, 7, 10) ship
    /// in PR 2.
    ///
    /// All scenarios use deterministic synthetic positions and identity body
    /// rotation so the SHA-256 baselines are stable across machines. The
    /// resolver context's <c>AbsoluteWorldPositionResolver</c> reads
    /// <c>(latitude, longitude, altitude)</c> as a Cartesian world position —
    /// matching the existing <c>RelativeAnchorResolverTests</c> convention.
    /// </summary>
    internal static class HarnessScenarios
    {
        // The resolver context needs callbacks instead of a Unity body. The
        // existing RelativeAnchorResolverTests use the same convention: read
        // the trajectory point's lat/lon/alt as a Cartesian world position
        // and treat the body rotation as identity. This keeps the harness
        // pure C# without Unity-runtime stubs.
        internal static RelativeAnchorResolverContext MakeContext(
            RecordingTree tree,
            RecordingTree pendingTree = null,
            ReFlySessionMarker marker = null)
        {
            return new RelativeAnchorResolverContext(
                tree,
                focusRecordingId: null,
                focusTreeId: tree?.Id,
                activeReFlyMarker: marker,
                pendingTree: pendingTree,
                absoluteWorldPositionResolver:
                    p => new Vector3d(p.latitude, p.longitude, p.altitude),
                bodyWorldRotationResolver: p => Quaternion.identity);
        }

        internal static TrajectoryPoint MakePoint(double ut, Vector3d xyz, Quaternion rotation)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = xyz.x,
                longitude = xyz.y,
                altitude = xyz.z,
                bodyName = "Kerbin",
                rotation = rotation,
            };
        }

        internal static Recording MakeAbsoluteRecording(
            string recordingId,
            string treeId,
            Vector3d startWorld,
            Vector3d endWorld,
            Quaternion? rotation = null,
            double startUT = 0.0,
            double endUT = 10.0)
        {
            Quaternion rot = rotation ?? Quaternion.identity;
            var rec = new Recording
            {
                RecordingId = recordingId,
                RecordingFormatVersion =
                    RelativeAnchorResolver.RecordingAnchorChainFormatVersion,
                TreeId = treeId,
                VesselName = recordingId,
            };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = startUT,
                endUT = endUT,
                frames = new List<TrajectoryPoint>
                {
                    MakePoint(startUT, startWorld, rot),
                    MakePoint(endUT, endWorld, rot),
                },
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active,
            });
            return rec;
        }

        internal static Recording MakeRelativeRecording(
            string recordingId,
            string treeId,
            Vector3d localOffset,
            string anchorRecordingId,
            double startUT = 0.0,
            double endUT = 10.0,
            Quaternion? localRotation = null)
        {
            Quaternion rot = localRotation ?? Quaternion.identity;
            var rec = new Recording
            {
                RecordingId = recordingId,
                RecordingFormatVersion =
                    RelativeAnchorResolver.RecordingAnchorChainFormatVersion,
                TreeId = treeId,
                VesselName = recordingId,
            };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = startUT,
                endUT = endUT,
                anchorRecordingId = anchorRecordingId,
                frames = new List<TrajectoryPoint>
                {
                    MakePoint(startUT, localOffset, rot),
                    MakePoint(endUT, localOffset, rot),
                },
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active,
            });
            return rec;
        }

        // ===== Scenario 1: Single recording, Absolute throughout. =====
        //
        // Baseline for the simplest resolver call. Exercises
        // TryResolveAbsoluteSectionPose / TryResolveAbsoluteFramesPose.
        // World position interpolates linearly from (100,0,0) to
        // (200,0,0) across UT [0, 10]. Body rotation is identity, surface
        // rotation on each point is identity, so world rotation samples
        // are identity throughout.
        internal static HarnessScenario BuildScenario1_AbsoluteSingle()
        {
            var tree = new RecordingTree { Id = "tree-s1" };
            Recording target = MakeAbsoluteRecording(
                "abs-target",
                tree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(200, 0, 0),
                rotation: Quaternion.identity,
                startUT: 0.0,
                endUT: 10.0);
            tree.AddOrReplaceRecording(target);
            return new HarnessScenario(
                "scenario-1-absolute-single",
                MakeContext(tree),
                target,
                startUT: 0.0,
                endUT: 10.0);
        }

        // ===== Scenario 2: Single recording, Relative throughout, fixed
        // anchor. =====
        //
        // Two recordings: an Absolute "anchor" sweeping (100,0,0) to
        // (110,0,0), and a Relative "target" with a constant offset
        // (1, 2, 3) anchored to it. Resolver should compose:
        //   target_world(ut) = anchor_world(ut) + offset
        //                    = (100 + ut, 2, 3)
        // Exercises TryResolveRelativeSectionPose →
        // TryResolveAnchorPose → TryResolveAbsoluteSectionPose chain.
        internal static HarnessScenario BuildScenario2_RelativeSingleFixedAnchor()
        {
            var tree = new RecordingTree { Id = "tree-s2" };
            Recording anchor = MakeAbsoluteRecording(
                "anchor",
                tree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0),
                startUT: 0.0,
                endUT: 10.0);
            Recording target = MakeRelativeRecording(
                "rel-target",
                tree.Id,
                localOffset: new Vector3d(1, 2, 3),
                anchorRecordingId: anchor.RecordingId,
                startUT: 0.0,
                endUT: 10.0);
            tree.AddOrReplaceRecording(anchor);
            tree.AddOrReplaceRecording(target);
            return new HarnessScenario(
                "scenario-2-relative-fixed-anchor",
                MakeContext(tree),
                target,
                startUT: 0.0,
                endUT: 10.0);
        }

        // ===== Scenario 6: Re-Fly, provisional supersedes origin. =====
        //
        // The origin recording is captured pre-Re-Fly via
        // CapturePreReFlyAnchorTrajectory(sessionId), then its TrackSections
        // are mutated to simulate the post-Re-Fly state. A child Relative
        // recording is anchored to the origin's RecordingId. The active
        // ReFlySessionMarker points at the origin so the resolver walks
        // back through the frozen snapshot rather than reading the mutated
        // post-Re-Fly TrackSections.
        //
        // Expected hash captures the frozen pre-Re-Fly anchor positions
        // (100..110 sweep) plus the constant local offset (1, 0, 0) → the
        // child's world pose is (101 + ut, 0, 0).
        //
        // If a future change makes the resolver read the mutated
        // post-Re-Fly origin TrackSections instead of the frozen snapshot,
        // the hash diverges and this test fails — exactly the regression
        // tripwire the harness exists to provide.
        internal static HarnessScenario BuildScenario6_ReFlyProvisionalSupersedesOrigin()
        {
            var tree = new RecordingTree { Id = "tree-s6" };
            Recording active = MakeAbsoluteRecording(
                "active-refly",
                tree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0),
                startUT: 0.0,
                endUT: 10.0);

            // Freeze the pre-Re-Fly trajectory under session-a.
            active.CapturePreReFlyAnchorTrajectory("session-a");

            // Mutate the active recording's live TrackSection to simulate
            // post-Re-Fly state. If resolver wrongly reads this, the hash
            // diverges from the baseline.
            Recording postReFly = MakeAbsoluteRecording(
                active.RecordingId,
                tree.Id,
                new Vector3d(900, 0, 0),
                new Vector3d(910, 0, 0),
                startUT: 0.0,
                endUT: 10.0);
            active.TrackSections.Clear();
            active.TrackSections.Add(postReFly.TrackSections[0]);

            Recording child = MakeRelativeRecording(
                "refly-child",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: active.RecordingId,
                startUT: 0.0,
                endUT: 10.0);

            tree.AddOrReplaceRecording(active);
            tree.AddOrReplaceRecording(child);

            var marker = new ReFlySessionMarker
            {
                SessionId = "session-a",
                TreeId = tree.Id,
                ActiveReFlyRecordingId = active.RecordingId,
                OriginChildRecordingId = active.RecordingId,
            };

            return new HarnessScenario(
                "scenario-6-refly-walk-back",
                MakeContext(tree, marker: marker),
                child,
                startUT: 0.0,
                endUT: 10.0);
        }

        // ===== Scenario 4: Focused-vessel debris with wrong anchor. =====
        //
        // Today's bug surface (per refactor plan §"Bug evidence"
        // failure-mode #1): focused-vessel debris (booster decouples,
        // engine fragment from staging, etc.) gets its `anchorRecordingId`
        // set to "nearest eligible vessel at sample time" rather than the
        // parent recording. When that nearest vessel is NOT the parent,
        // the debris's Relative offsets are encoded in a frame that
        // doesn't follow the parent — so on playback the debris snaps
        // to whatever pose the (wrong) anchor recording is at, not where
        // the actual breakup happened.
        //
        // This baseline encodes that broken behavior. The debris is
        // marked IsDebris=true and its Relative section is anchored to
        // "other-vessel" instead of "parent". The resolver dutifully
        // composes: debris_world = other_world + offset. After PR 3b,
        // the scenario will be rewritten to anchor to parent and set
        // DebrisParentRecordingId — the hash will change and the
        // baseline will be reset with a justification comment.
        //
        // Topology:
        //   parent: Absolute, sweeps (100, 0, 0) → (200, 0, 0)
        //   other-vessel: Absolute, sweeps (300, 50, 0) → (310, 50, 0)
        //   debris: Relative anchored to "other-vessel", offset (1, 0, 0)
        // Expected debris world position(ut) =
        //   other-vessel(ut) + (1, 0, 0) = (301 + ut, 50, 0).
        internal static HarnessScenario BuildScenario4_FocusedVesselDebrisWrongAnchor()
        {
            var tree = new RecordingTree { Id = "tree-s4" };
            Recording parent = MakeAbsoluteRecording(
                "focused-parent",
                tree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(200, 0, 0),
                startUT: 0.0,
                endUT: 10.0);
            // Mark with a long live trajectory so it reads as the focused
            // vessel — not load-bearing for the resolver, but documents
            // the intent that this is the focused-vessel breakup case.
            parent.VesselPersistentId = 100u;

            Recording otherVessel = MakeAbsoluteRecording(
                "other-vessel",
                tree.Id,
                new Vector3d(300, 50, 0),
                new Vector3d(310, 50, 0),
                startUT: 0.0,
                endUT: 10.0);
            otherVessel.VesselPersistentId = 200u;

            // Debris with WRONG anchor — points at other-vessel rather
            // than parent. This is what "nearest eligible vessel at
            // sample time" picks when other-vessel happens to be closer
            // than parent at the moment the debris spawns.
            Recording debris = MakeRelativeRecording(
                "focused-debris-wrong-anchor",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: otherVessel.RecordingId,
                startUT: 0.0,
                endUT: 10.0);
            debris.IsDebris = true;
            debris.VesselPersistentId = 300u;

            tree.AddOrReplaceRecording(parent);
            tree.AddOrReplaceRecording(otherVessel);
            tree.AddOrReplaceRecording(debris);

            return new HarnessScenario(
                "scenario-4-focused-debris-wrong-anchor",
                MakeContext(tree),
                debris,
                startUT: 0.0,
                endUT: 10.0);
        }

        // ===== Scenario 5: Background-vessel debris with wrong anchor. =====
        //
        // The other half of plan §"Bug evidence" failure mode #1: a
        // background vessel breaks up and the spawned debris's
        // `anchorRecordingId` is set to whatever loaded candidate
        // happens to be nearest at sample time. Different creation site
        // (BackgroundRecorder.RegisterChildRecordingsFromSplit /
        // BuildBackgroundSplitBranchData) than scenario 4
        // (ParsekFlight.CreateBreakupChildRecording), but the resolver
        // treats both identically — the harness verifies the resolver's
        // composition is byte-identical for both creation sites by
        // making this scenario structurally distinct from scenario 4
        // (different positions, different anchors) so a bug that affects
        // only one path lights up only one baseline.
        //
        // Topology:
        //   bg-parent: Absolute, sweeps (-50, 100, 0) → (-50, 110, 0)
        //              (no controller, so it's background-recorded)
        //   bg-other: Absolute, sweeps (0, 100, 0) → (0, 110, 0)
        //             (the wrong nearest-at-sample-time vessel)
        //   debris: Relative anchored to "bg-other", offset (0, 0, 7)
        //           — encoded in a frame that should have been bg-parent
        // Expected debris world position(ut) =
        //   bg-other(ut) + (0, 0, 7) = (0, 100 + ut, 7).
        internal static HarnessScenario BuildScenario5_BackgroundVesselDebrisWrongAnchor()
        {
            var tree = new RecordingTree { Id = "tree-s5" };
            Recording bgParent = MakeAbsoluteRecording(
                "bg-parent",
                tree.Id,
                new Vector3d(-50, 100, 0),
                new Vector3d(-50, 110, 0),
                startUT: 0.0,
                endUT: 10.0);
            bgParent.VesselPersistentId = 1000u;

            Recording bgOther = MakeAbsoluteRecording(
                "bg-other-vessel",
                tree.Id,
                new Vector3d(0, 100, 0),
                new Vector3d(0, 110, 0),
                startUT: 0.0,
                endUT: 10.0);
            bgOther.VesselPersistentId = 2000u;

            Recording debris = MakeRelativeRecording(
                "bg-debris-wrong-anchor",
                tree.Id,
                localOffset: new Vector3d(0, 0, 7),
                anchorRecordingId: bgOther.RecordingId,
                startUT: 0.0,
                endUT: 10.0);
            debris.IsDebris = true;
            debris.VesselPersistentId = 3000u;

            tree.AddOrReplaceRecording(bgParent);
            tree.AddOrReplaceRecording(bgOther);
            tree.AddOrReplaceRecording(debris);

            return new HarnessScenario(
                "scenario-5-bg-debris-wrong-anchor",
                MakeContext(tree),
                debris,
                startUT: 0.0,
                endUT: 10.0);
        }

        // ===== Scenario 7: Debris created during a Re-Fly session. =====
        //
        // Plan §"Bug evidence" failure mode #2 — the highest-impact
        // visible defect. During a Re-Fly load,
        // `RestoreActiveTreeFromPending` removes the active Re-Fly
        // recording from `tree.BackgroundMap` (`bgMapEntries=1->0` in
        // the retained log). When the player's vessel sheds new debris
        // mid-Re-Fly, `BackgroundRecorder.AddBackgroundLiveAnchorCandidates`
        // resolves loaded live candidates only through `BackgroundMap`,
        // so the active Re-Fly vessel is NOT a candidate — the debris
        // anchors to whatever pre-Re-Fly ghost happens to be nearby.
        // That ghost's recorded world position is by definition the
        // un-Re-Flown trajectory (Re-Fly is the divergence), so the
        // debris is encoded in a frame whose anchor is in the wrong
        // place; on playback it renders at a displaced pre-Re-Fly
        // position rather than at the actual breakup site.
        //
        // Today's broken composition the harness pins:
        //   active-refly: Absolute, frozen pre-Re-Fly = (100, 0, 0) →
        //     (110, 0, 0); live mutated post-Re-Fly = (900, 0, 0) →
        //     (910, 0, 0); marker.ActiveReFlyRecordingId = "active-refly"
        //   pre-refly-ghost: Absolute, sweeps (500, 200, 0) →
        //     (510, 200, 0). This is the loaded background candidate the
        //     debris anchors to today (because the active Re-Fly
        //     recording is excluded from BackgroundMap).
        //   debris: IsDebris=true, Relative anchored to
        //     "pre-refly-ghost", offset (1, 0, 0). The marker does NOT
        //     fire walk-back because the debris's anchor is the ghost,
        //     not the active provisional.
        // Expected debris world position(ut) =
        //   pre-refly-ghost(ut) + (1, 0, 0) = (501 + ut, 200, 0).
        //
        // After PR 3b, the scenario builder will be rewritten so the
        // debris is anchored to "active-refly" (parent) with
        // DebrisParentRecordingId set; the resolver's
        // TryResolveActiveReFlyAnchorRecording fires and walks back to
        // the frozen pre-Re-Fly snapshot — debris world position
        // becomes (101 + ut, 0, 0), which is the actual breakup site.
        // The hash will change; reset with justification.
        internal static HarnessScenario BuildScenario7_ReFlyDebrisWrongAnchor()
        {
            var tree = new RecordingTree { Id = "tree-s7" };

            Recording active = MakeAbsoluteRecording(
                "active-refly",
                tree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0),
                startUT: 0.0,
                endUT: 10.0);
            active.CapturePreReFlyAnchorTrajectory("session-7");
            // Mutate the live TrackSection to simulate post-Re-Fly state.
            // If a future change wrongly walks back to this for the
            // (wrong-anchor) debris, the hash flips because positions
            // jump from ~500..510 to ~900..910.
            Recording postReFly = MakeAbsoluteRecording(
                active.RecordingId,
                tree.Id,
                new Vector3d(900, 0, 0),
                new Vector3d(910, 0, 0));
            active.TrackSections.Clear();
            active.TrackSections.Add(postReFly.TrackSections[0]);

            Recording preReFlyGhost = MakeAbsoluteRecording(
                "pre-refly-ghost",
                tree.Id,
                new Vector3d(500, 200, 0),
                new Vector3d(510, 200, 0),
                startUT: 0.0,
                endUT: 10.0);

            Recording debris = MakeRelativeRecording(
                "refly-debris-wrong-anchor",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: preReFlyGhost.RecordingId,
                startUT: 0.0,
                endUT: 10.0);
            debris.IsDebris = true;

            tree.AddOrReplaceRecording(active);
            tree.AddOrReplaceRecording(preReFlyGhost);
            tree.AddOrReplaceRecording(debris);

            var marker = new ReFlySessionMarker
            {
                SessionId = "session-7",
                TreeId = tree.Id,
                ActiveReFlyRecordingId = active.RecordingId,
                OriginChildRecordingId = active.RecordingId,
            };

            return new HarnessScenario(
                "scenario-7-refly-debris-wrong-anchor",
                MakeContext(tree, marker: marker),
                debris,
                startUT: 0.0,
                endUT: 10.0);
        }

        // ===== Scenario 10: Same-chain continuation. =====
        //
        // Non-debris invariant — resolver walks past the requested-UT
        // anchor's recorded range into the chain successor. This
        // scenario must NOT change in PR 3b (per the plan's success
        // criteria: scenarios 1, 2, 3, 6, 8, 9, 10 unchanged).
        //
        // Topology:
        //   first-half: Absolute, ChainId="chain-x", ChainIndex=0,
        //     UT [0, 10], sweeps (100, 0, 0) → (110, 0, 0)
        //   second-half: Absolute, ChainId="chain-x", ChainIndex=1,
        //     UT [10, 20], sweeps (110, 0, 0) → (120, 0, 0)
        //   target (Relative): anchored to "first-half", UT [10, 20],
        //     offset (1, 0, 0). At UT 12, first-half doesn't cover, so
        //     resolver walks via TryResolveSameChainContinuationPose to
        //     second-half(12) = (112, 0, 0); target world = (113, 0, 0).
        //
        // Sampled over UT [10, 20] — first-half's recorded range
        // doesn't cover any of it, so every sample exercises the
        // chain-continuation walk.
        internal static HarnessScenario BuildScenario10_SameChainContinuation()
        {
            var tree = new RecordingTree { Id = "tree-s10" };

            Recording firstHalf = MakeAbsoluteRecording(
                "first-half",
                tree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0),
                startUT: 0.0,
                endUT: 10.0);
            firstHalf.ChainId = "chain-x";
            firstHalf.ChainIndex = 0;

            Recording secondHalf = MakeAbsoluteRecording(
                "second-half",
                tree.Id,
                new Vector3d(110, 0, 0),
                new Vector3d(120, 0, 0),
                startUT: 10.0,
                endUT: 20.0);
            secondHalf.ChainId = firstHalf.ChainId;
            secondHalf.ChainIndex = 1;

            Recording target = MakeRelativeRecording(
                "chain-child",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: firstHalf.RecordingId,
                startUT: 10.0,
                endUT: 20.0);

            tree.AddOrReplaceRecording(firstHalf);
            tree.AddOrReplaceRecording(secondHalf);
            tree.AddOrReplaceRecording(target);

            return new HarnessScenario(
                "scenario-10-same-chain-continuation",
                MakeContext(tree),
                target,
                startUT: 10.0,
                endUT: 20.0);
        }

        // ===== Scenario 9: Loop-anchor recording (live-PID rejection). =====
        //
        // The loop recording has LoopAnchorVesselId != 0 (live-PID
        // contract). The resolver MUST reject it as a recorded anchor on
        // every UT — the child Relative recording cannot resolve. Every
        // sample emits the unresolved sentinel; the hash is stable for as
        // long as the loop-rejection invariant holds.
        //
        // If a future change accepts a loop-rooted recording as a recorded
        // anchor, the child resolves and the hash diverges — exactly the
        // tripwire the harness exists to provide.
        internal static HarnessScenario BuildScenario9_LoopAnchorRejection()
        {
            var tree = new RecordingTree { Id = "tree-s9" };
            Recording loopRoot = MakeAbsoluteRecording(
                "loop-root",
                tree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0),
                startUT: 0.0,
                endUT: 10.0);
            loopRoot.LoopPlayback = true;
            loopRoot.LoopAnchorVesselId = 42u;

            Recording child = MakeRelativeRecording(
                "loop-child",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: loopRoot.RecordingId,
                startUT: 0.0,
                endUT: 10.0);

            tree.AddOrReplaceRecording(loopRoot);
            tree.AddOrReplaceRecording(child);

            return new HarnessScenario(
                "scenario-9-loop-anchor-rejection",
                MakeContext(tree),
                child,
                startUT: 0.0,
                endUT: 10.0);
        }
    }
}
