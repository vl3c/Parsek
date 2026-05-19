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
                    RecordingStore.CurrentRecordingFormatVersion,
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
                    RecordingStore.CurrentRecordingFormatVersion,
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

        // ===== Scenario 3: Single recording with Absolute↔Relative
        // section-boundary transition. =====
        //
        // Verifies the section dispatcher's per-section frame routing across
        // a section boundary at mid-recording. A target recording with two
        // sections is sampled over the full UT span:
        //   Section 0 [0, 5]: Absolute, sweeps (100, 0, 0) → (105, 0, 0).
        //                    Resolver dispatches to TryResolveAbsoluteSectionPose.
        //   Section 1 [5, 10]: Relative, anchored to "anchor-s3", local
        //                    offset (1, 0, 0). Resolver dispatches to
        //                    TryResolveRelativeSectionPose →
        //                    TryResolveAnchorPose → recursive resolve of the
        //                    anchor recording → world pose composition.
        //
        // The "anchor-s3" recording sweeps (1000, 0, 0) → (1010, 0, 0) over
        // UT [0, 10] so the second-half samples produce world positions
        // distinguishable from the first-half samples by ~3 orders of
        // magnitude — a future regression that accidentally re-routes a
        // Relative section through the Absolute path (or vice versa) would
        // perturb the hash by a large, obvious delta.
        //
        // This scenario MUST remain stable across PR 3 (per the plan's
        // success criteria — non-debris invariants).
        internal static HarnessScenario BuildScenario3_AbsoluteRelativeSectionTransition()
        {
            var tree = new RecordingTree { Id = "tree-s3" };
            Recording anchor = MakeAbsoluteRecording(
                "anchor-s3",
                tree.Id,
                new Vector3d(1000, 0, 0),
                new Vector3d(1010, 0, 0),
                startUT: 0.0,
                endUT: 10.0);

            // Build the target by hand because the existing helpers create
            // single-section recordings; we need two sections joined at UT 5.
            var target = new Recording
            {
                RecordingId = "abs-rel-transition-target",
                RecordingFormatVersion =
                    RecordingStore.CurrentRecordingFormatVersion,
                TreeId = tree.Id,
                VesselName = "abs-rel-transition-target",
            };
            target.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 0.0,
                endUT = 5.0,
                frames = new List<TrajectoryPoint>
                {
                    MakePoint(0.0, new Vector3d(100, 0, 0), Quaternion.identity),
                    MakePoint(5.0, new Vector3d(105, 0, 0), Quaternion.identity),
                },
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active,
            });
            target.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 5.0,
                endUT = 10.0,
                anchorRecordingId = anchor.RecordingId,
                frames = new List<TrajectoryPoint>
                {
                    MakePoint(5.0, new Vector3d(1, 0, 0), Quaternion.identity),
                    MakePoint(10.0, new Vector3d(1, 0, 0), Quaternion.identity),
                },
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active,
            });

            tree.AddOrReplaceRecording(anchor);
            tree.AddOrReplaceRecording(target);
            return new HarnessScenario(
                "scenario-3-absolute-relative-section-transition",
                MakeContext(tree),
                target,
                startUT: 0.0,
                endUT: 10.0);
        }

        // ===== Scenario 8: legacy on-rails background recording without
        // checkpoint track sections. =====
        //
        // On-rails BG vessels deliberately emit no env-classified
        // TrackSections (per project CLAUDE.md "On-rails BG vessels emit
        // no env-classified per-frame TrackSections"; `BackgroundOnRailsState` omits
        // `currentTrackSection`/`trackSections` and `OnBackgroundPhysicsFrame`
        // early-returns on `bgVessel.packed`). New packed-coast recordings should
        // wrap closed orbit segments in OrbitalCheckpoint sections, but this harness
        // intentionally preserves the old missing-wrapper shape. For format-v6+
        // recordings, `RelativeAnchorResolver.TryResolveRecordingPose` requires
        // TrackSections to be non-empty, so empty TrackSections + v6+ format hits
        // the "anchor-track-sections-missing" branch and returns false for every UT.
        //
        // This scenario pins that consistent-fail behavior for on-rails
        // recordings. The recording has Points (legacy flat trajectory
        // list) and OrbitSegments populated to reflect a realistic
        // legacy on-rails state, but TrackSections is intentionally empty. The
        // hash captures all-NaN-sentinel samples — if a future change
        // adds a Points-fallback for empty-TrackSections recordings on
        // v6+ format (or routes OrbitSegments through pose resolution
        // without a TrackSection wrapper), the hash flips immediately.
        //
        // This scenario MUST remain stable across PR 3 (per the plan's
        // success criteria — non-debris invariants).
        internal static HarnessScenario BuildScenario8_OnRailsBackgroundVessel()
        {
            var tree = new RecordingTree { Id = "tree-s8" };
            var target = new Recording
            {
                RecordingId = "on-rails-bg",
                RecordingFormatVersion =
                    RecordingStore.CurrentRecordingFormatVersion,
                TreeId = tree.Id,
                VesselName = "on-rails-bg",
            };
            // Points: realistic flat trajectory list (legacy/non-track-section
            // shape). Resolver does NOT consult these for v6+ recordings with
            // empty TrackSections.
            target.Points.Add(MakePoint(0.0, new Vector3d(700000, 0, 0), Quaternion.identity));
            target.Points.Add(MakePoint(5.0, new Vector3d(700100, 0, 0), Quaternion.identity));
            target.Points.Add(MakePoint(10.0, new Vector3d(700200, 0, 0), Quaternion.identity));
            // OrbitSegments: a single circular Kerbin orbit. Resolver does not
            // invoke OrbitalCheckpointPoseResolver for these — they are only
            // referenced via a TrackSection of referenceFrame=OrbitalCheckpoint.
            target.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 0.0,
                endUT = 10.0,
                bodyName = "Kerbin",
                semiMajorAxis = 700000,
                eccentricity = 0.0,
                inclination = 0.0,
                isPredicted = false,
            });
            // TrackSections intentionally empty — this is the on-rails shape.

            tree.AddOrReplaceRecording(target);
            return new HarnessScenario(
                "scenario-8-on-rails-bg-vessel",
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

        // ===== Scenario 4: Focused-vessel debris (PR 3b parent-anchor contract). =====
        //
        // Reset by PR 3b: debris parent-anchor contract introduced
        // (plan §3b §"Helper" + §"Per-frame anchor write"). The recorder
        // now writes Relative sections anchored to the parent recording
        // and stamps `DebrisParentRecordingId` on the child. Composition
        // is now (parent_pos + offset) — the correct visual result.
        //
        // Topology (rewritten):
        //   parent (focused): Absolute, sweeps (100, 0, 0) → (200, 0, 0)
        //   other-vessel: Absolute, sweeps (300, 50, 0) → (310, 50, 0).
        //     Kept in the scene so the harness verifies the resolver
        //     ignores it — the contract pins the anchor regardless.
        //   debris: Relative anchored to "focused-parent" (the parent),
        //     `DebrisParentRecordingId = parent.RecordingId`, offset (1, 0, 0)
        // Expected debris world position(ut) =
        //   focused-parent(ut) + (1, 0, 0) = (101 + 10*(ut/10), 0, 0)
        //   i.e. parent sweeps 100..200, debris sweeps 101..201.
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
            parent.VesselPersistentId = 100u;

            // Kept around as a third vessel that the broken pre-PR-3b
            // recorder used to anchor against. After the contract lands,
            // this recording is irrelevant to the debris's resolution.
            Recording otherVessel = MakeAbsoluteRecording(
                "other-vessel",
                tree.Id,
                new Vector3d(300, 50, 0),
                new Vector3d(310, 50, 0),
                startUT: 0.0,
                endUT: 10.0);
            otherVessel.VesselPersistentId = 200u;

            // Debris correctly anchored to parent, with DebrisParentRecordingId
            // stamped per the PR 3b contract.
            Recording debris = MakeRelativeRecording(
                "focused-debris-wrong-anchor",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: parent.RecordingId,
                startUT: 0.0,
                endUT: 10.0);
            debris.IsDebris = true;
            debris.DebrisParentRecordingId = parent.RecordingId;
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
        // Reset by PR 3b: BG-vessel debris is now anchored to its bg parent
        // and stamped with DebrisParentRecordingId (plan §3b §"Primary creation
        // sites #2,#3"). Composition becomes (bg-parent + offset).
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

            // Kept as a third vessel the pre-PR-3b nearest-search would have
            // anchored against. After the contract lands, irrelevant.
            Recording bgOther = MakeAbsoluteRecording(
                "bg-other-vessel",
                tree.Id,
                new Vector3d(0, 100, 0),
                new Vector3d(0, 110, 0),
                startUT: 0.0,
                endUT: 10.0);
            bgOther.VesselPersistentId = 2000u;

            // Debris correctly anchored to bg-parent per the PR 3b contract.
            // Expected world: bg-parent(ut) + (0, 0, 7) = (-50, 100 + ut, 7).
            Recording debris = MakeRelativeRecording(
                "bg-debris-wrong-anchor",
                tree.Id,
                localOffset: new Vector3d(0, 0, 7),
                anchorRecordingId: bgParent.RecordingId,
                startUT: 0.0,
                endUT: 10.0);
            debris.IsDebris = true;
            debris.DebrisParentRecordingId = bgParent.RecordingId;
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
        // Reset by PR 3b: debris created during a Re-Fly session is now
        // anchored to the active-refly recording with DebrisParentRecordingId
        // set, so the resolver's TryResolveActiveReFlyAnchorRecording fires
        // and walks back to the frozen pre-Re-Fly snapshot rather than
        // resolving against the displaced pre-refly-ghost. The pre-refly-ghost
        // is kept in the scene to verify the resolver ignores it under the
        // contract. See plan §3b §"Re-Fly settle window hook" + §"Helper".
        // Composition is now (frozen pre-Re-Fly active + offset).
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
            // The Re-Fly walk-back (driven by the marker below) must read
            // the frozen pre-Re-Fly snapshot, not these mutated values.
            Recording postReFly = MakeAbsoluteRecording(
                active.RecordingId,
                tree.Id,
                new Vector3d(900, 0, 0),
                new Vector3d(910, 0, 0));
            active.TrackSections.Clear();
            active.TrackSections.Add(postReFly.TrackSections[0]);

            // Kept in scene as the (broken) pre-PR-3b nearest-search target;
            // post-contract, the debris no longer anchors here.
            Recording preReFlyGhost = MakeAbsoluteRecording(
                "pre-refly-ghost",
                tree.Id,
                new Vector3d(500, 200, 0),
                new Vector3d(510, 200, 0),
                startUT: 0.0,
                endUT: 10.0);

            // Debris correctly anchored to active-refly per the PR 3b
            // contract. Expected world: pre-Re-Fly active(ut) + (1, 0, 0) =
            // (101 + ut, 0, 0).
            Recording debris = MakeRelativeRecording(
                "refly-debris-wrong-anchor",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: active.RecordingId,
                startUT: 0.0,
                endUT: 10.0);
            debris.IsDebris = true;
            debris.DebrisParentRecordingId = active.RecordingId;

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
