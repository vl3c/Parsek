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
