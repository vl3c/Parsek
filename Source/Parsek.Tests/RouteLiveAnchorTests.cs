using System;
using Parsek;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    // Logistics route live-anchor bind: unit coverage for the guid gate on the anchor's
    // live-vessel match (Step 1) and the live-bind-event-scoped Step-2 double-suppression.
    // Step 2 hides a loop member's OWN ghost ONLY while its launch-matched live vessel is
    // loaded AND it was the LIVE docking anchor of an in-window relative member during
    // this-or-the-previous frame (the Step-1 live-bind event captured at
    // RelativeAnchorResolver.WasLiveBoundThisOrLastFrame), NOT for the whole loop (which
    // over-suppressed every parked route craft). The guid tests touch the shared
    // GhostPlaybackLogic vessel-exists / guid-resolver overrides and the process-static
    // RelativeAnchorResolver live-bind ledger, so the class is [Collection("Sequential")]
    // and resets both in the ctor + Dispose.
    [Collection("Sequential")]
    public class RouteLiveAnchorTests : IDisposable
    {
        private const uint DepotPid = 4277041026u;
        private const string DepotRecordingId = "7e0d79b5d57a40d29400afd8b49d1906";
        private const string DepotLaunchGuid = "424fd14c8870407f81dffdf606a92db2";
        private const string DepotRelaunchGuid = "05d3ea0f00000000000000000000ffff";

        // Real route fixtures from the orbital-supply-route save: the Deliverer is a
        // relative MEMBER whose section anchors against the Depot. Its own RecordingId
        // never enters the live-bind set (only its anchor target does), so even with its
        // live vessel loaded + looping it must never be Step-2 suppressed - the regression.
        private const uint DelivererPid = 2448645546u;
        private const string DelivererRecordingId = "b9f08d0f269346ee84162dd763e462aa";
        private const string DelivererLaunchGuid = "9f08d0f269346ee84162dd763e462aa0";
        // A pure ANCHOR that 7 members dock (Kerbal X 56298d83): a static anchor-graph
        // predicate would keep suppressing its delivery mesh, but the live-bind gate only
        // fires while it is actually being docked.
        private const string KerbalXAnchorRecordingId = "56298d8360d14db68d488f6d2aee7f72";

        public RouteLiveAnchorTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GhostPlaybackLogic.ResetVesselExistsOverride();
            GhostPlaybackLogic.ResetVesselCacheForTesting();
            RelativeAnchorResolver.ResetForTesting();
        }

        public void Dispose()
        {
            GhostPlaybackLogic.ResetVesselExistsOverride();
            GhostPlaybackLogic.ResetVesselCacheForTesting();
            RelativeAnchorResolver.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // --- Step 1 + Step-2 gate: guid-gated live-vessel existence -------------
        // RealVesselExistsForRecording is BOTH the Step-1 resolver guard and the Step-2
        // whole-loop suppression predicate, so these three cases pin the suppression
        // condition directly: suppress when the same-launch live vessel is loaded; do NOT
        // suppress for a same-craft different-launch vessel; do NOT suppress when absent.

        [Fact]
        public void RealVesselExistsForRecording_SameLaunch_Matches()
        {
            // Recording + live vessel share the launch guid: the launch-matched live Depot
            // the player is flying exists, so Step 2 suppresses the Depot's own loop ghost
            // for the whole loop (criterion A at the predicate level).
            Recording rec = MakeDepotAnchorRecording();
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == DepotPid);
            GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => DepotLaunchGuid);

            Assert.True(GhostPlaybackLogic.RealVesselExistsForRecording(rec));
        }

        [Fact]
        public void RealVesselExistsForRecording_SameCraftDifferentLaunch_DoesNotMatch()
        {
            // The Depot recording's craft is loaded (same baked pid) but it is a DIFFERENT
            // launch (guid conclusively differs). A bare pid match would suppress the wrong
            // same-craft vessel's ghost; the guid gate rejects it. Proves the three on-disk
            // Depot launches never cross-suppress (criterion B).
            Recording rec = MakeDepotAnchorRecording();
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == DepotPid);
            GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => DepotRelaunchGuid);

            Assert.False(GhostPlaybackLogic.RealVesselExistsForRecording(rec));
        }

        [Fact]
        public void RealVesselExistsForRecording_NoLiveVessel_DoesNotMatch()
        {
            // Watch-from-afar: the launch-matched live vessel is NOT loaded in the scene,
            // so Step 2 does NOT suppress and the Depot's own loop ghost still draws
            // (criterion D). The guid resolver is irrelevant once existence fails.
            Recording rec = MakeDepotAnchorRecording();
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => false);
            GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => DepotLaunchGuid);

            Assert.False(GhostPlaybackLogic.RealVesselExistsForRecording(rec));
        }

        // --- Step 2: map-surface decision gate ----------------------------------
        // ResolveMapPresenceGhostSource is the create-time decision feeding both map
        // surfaces (icon + orbit line). The whole-loop suppression term
        // (liveLaunchMatchedAnchorOfActiveMember) tightens the loopMemberInWindow carve-out
        // so an already-materialized loop member's own ghost is dropped to source=None.

        [Fact]
        public void ResolveMapPresenceGhostSource_WholeLoopSuppression_ReturnsNone()
        {
            // alreadyMaterialized (the launch-matched live vessel is loaded) + the
            // whole-loop suppression term => source None even though the member is in its
            // loop window. Pins the map half of criterion A.
            Recording rec = MakeDepotAnchorRecording();
            int cachedIndex = -1;
            GhostMapPresence.TrackingStationGhostSource source = GhostMapPresence.ResolveMapPresenceGhostSource(
                rec,
                isSuppressed: false,
                alreadyMaterialized: true,
                currentUT: 1000.0,
                allowTerminalOrbitFallback: false,
                logOperationName: null,
                stateVectorCachedIndex: ref cachedIndex,
                out _,
                out _,
                out string skipReason,
                loopMemberInWindow: true,
                liveLaunchMatchedAnchorOfActiveMember: true);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.None, source);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSkipLiveAnchorDouble, skipReason);
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_LoopAlongsideRealVessel_NotLiveAnchorSuppressed()
        {
            // The legitimate Mission-loop-alongside-real-vessel case: the launch-matched
            // live vessel is NOT loaded (liveLaunchMatchedAnchorOfActiveMember=false) while
            // the member loops in its window. The whole-loop gate must NOT fire, so the
            // decision falls through past the live-anchor branch (criterion D/E). A
            // non-eligible terminal state makes the fall-through deterministic without
            // requiring orbit data; the load-bearing assertion is that it did NOT short
            // out as a live-anchor double.
            Recording rec = MakeDepotAnchorRecording();
            rec.TerminalStateValue = TerminalState.Landed; // not map-presence eligible
            int cachedIndex = -1;
            GhostMapPresence.TrackingStationGhostSource source = GhostMapPresence.ResolveMapPresenceGhostSource(
                rec,
                isSuppressed: false,
                alreadyMaterialized: true,
                currentUT: 1000.0,
                allowTerminalOrbitFallback: false,
                logOperationName: null,
                stateVectorCachedIndex: ref cachedIndex,
                out _,
                out _,
                out string skipReason,
                loopMemberInWindow: true,
                liveLaunchMatchedAnchorOfActiveMember: false);

            // It still resolves to None here (the non-eligible terminal short-circuits),
            // but crucially NOT via the live-anchor / already-spawned suppression branch:
            // the whole-loop gate did not fire, so a real loop member with renderable data
            // would draw alongside the live vessel.
            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.None, source);
            Assert.NotEqual(
                GhostMapPresence.TrackingStationGhostSkipLiveAnchorDouble, skipReason);
            Assert.NotEqual(
                GhostMapPresence.TrackingStationGhostSkipAlreadySpawned, skipReason);
        }

        // --- Step 2: live-bind ledger (RelativeAnchorResolver.WasLiveBoundThisOrLastFrame) ---
        // The ledger is stamped by Step-1 (TryBindLiveLaunchMatchedAnchorPose source=live)
        // and consumed one frame later by the flight/map Step-2 gate. These tests stamp it
        // by driving a real resolve through the public TryResolveAnchorPose path with a
        // live-anchor delegate (the exact stamp site), using an injectable frame provider
        // for the one-frame-lag contract.

        [Fact]
        public void WasLiveBoundThisOrLastFrame_FalseBeforeAnyBind()
        {
            RelativeAnchorResolver.FrameProviderForTesting = () => 5;
            Assert.False(RelativeAnchorResolver.WasLiveBoundThisOrLastFrame(DepotRecordingId));
        }

        [Fact]
        public void WasLiveBoundThisOrLastFrame_TrueAtBindFrameAndNext_FalseAfterTwoFrames()
        {
            int frame = 10;
            RelativeAnchorResolver.FrameProviderForTesting = () => frame;
            StampLiveBind(DepotRecordingId);

            // Frame N (the bind frame): bound.
            Assert.True(RelativeAnchorResolver.WasLiveBoundThisOrLastFrame(DepotRecordingId));

            // Frame N+1: still bound (one-frame tolerance, so a still-docking anchor read
            // before it re-stamps stays suppressed).
            frame = 11;
            Assert.True(RelativeAnchorResolver.WasLiveBoundThisOrLastFrame(DepotRecordingId));

            // Frame N+2: stale.
            frame = 12;
            Assert.False(RelativeAnchorResolver.WasLiveBoundThisOrLastFrame(DepotRecordingId));
        }

        [Fact]
        public void WasLiveBoundThisOrLastFrame_OnlyTheBoundId()
        {
            RelativeAnchorResolver.FrameProviderForTesting = () => 20;
            StampLiveBind(DepotRecordingId);

            Assert.True(RelativeAnchorResolver.WasLiveBoundThisOrLastFrame(DepotRecordingId));
            Assert.False(RelativeAnchorResolver.WasLiveBoundThisOrLastFrame(KerbalXAnchorRecordingId));
        }

        [Fact]
        public void LiveBindSet_ClearsWhenFrameAdvances()
        {
            int frame = 30;
            RelativeAnchorResolver.FrameProviderForTesting = () => frame;
            StampLiveBind(DepotRecordingId);

            // Advance one frame and bind a different id: the most-recent set holds only B,
            // and A is exactly one frame back (still within tolerance).
            frame = 31;
            StampLiveBind(KerbalXAnchorRecordingId);
            Assert.True(RelativeAnchorResolver.WasLiveBoundThisOrLastFrame(KerbalXAnchorRecordingId));
            Assert.False(RelativeAnchorResolver.WasLiveBoundThisOrLastFrame(DepotRecordingId));
        }

        // --- Step 2: IsLiveAnchorDoubleSuppressed predicate --------------------------

        [Fact]
        public void IsLiveAnchorDoubleSuppressed_AnchorLiveVesselAndLiveBound_Suppressed()
        {
            // The live Depot the player is flying is currently being docked (live-bound),
            // it is a loop member, and its launch-matched live vessel is loaded: its own
            // duplicate ghost is suppressed.
            Recording rec = MakeDepotAnchorRecording();
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == DepotPid);
            GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => DepotLaunchGuid);
            RelativeAnchorResolver.FrameProviderForTesting = () => 40;
            StampLiveBind(DepotRecordingId);

            Assert.True(GhostPlaybackLogic.IsLiveAnchorDoubleSuppressed(rec, loopingLike: true));
        }

        [Fact]
        public void IsLiveAnchorDoubleSuppressed_AnchorLiveVesselButNotLiveBound_NotSuppressed()
        {
            // THE scene-fix case: loop member, launch-matched live vessel loaded, but NO
            // delivery member is docking it this/last frame (no live bind). The earlier
            // whole-loop gate suppressed here; the live-bind gate does not, so the mesh
            // draws. This is why all 11 route meshes were wrongly hidden.
            Recording rec = MakeDepotAnchorRecording();
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == DepotPid);
            GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => DepotLaunchGuid);
            RelativeAnchorResolver.FrameProviderForTesting = () => 40;
            // No StampLiveBind.

            Assert.False(GhostPlaybackLogic.IsLiveAnchorDoubleSuppressed(rec, loopingLike: true));
        }

        [Fact]
        public void IsLiveAnchorDoubleSuppressed_RelativeMember_NotSuppressed()
        {
            // THE user-visible regression: the inbound Deliverer is a relative MEMBER. Its
            // anchor TARGET (the Depot) is the one that gets live-bound, never the member's
            // own id. Even with the Deliverer's live vessel loaded + looping, its own id is
            // absent from the bind set, so it is NOT suppressed - its delivery mesh draws.
            Recording member = MakeDelivererMemberRecording();
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == DelivererPid);
            GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => DelivererLaunchGuid);
            RelativeAnchorResolver.FrameProviderForTesting = () => 50;
            // The Depot anchor is live-bound (the docking event), NOT the Deliverer member.
            StampLiveBind(DepotRecordingId);

            Assert.False(GhostPlaybackLogic.IsLiveAnchorDoubleSuppressed(member, loopingLike: true));
        }

        [Fact]
        public void IsLiveAnchorDoubleSuppressed_NoLiveVessel_NotSuppressed()
        {
            // Watch-from-afar: live-bound but the launch-matched live vessel is NOT loaded
            // (existence fails), so not suppressed.
            Recording rec = MakeDepotAnchorRecording();
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => false);
            GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => DepotLaunchGuid);
            RelativeAnchorResolver.FrameProviderForTesting = () => 60;
            StampLiveBind(DepotRecordingId);

            Assert.False(GhostPlaybackLogic.IsLiveAnchorDoubleSuppressed(rec, loopingLike: true));
        }

        [Fact]
        public void IsLiveAnchorDoubleSuppressed_NotLoopingLike_NotSuppressed()
        {
            // Scope gate: live vessel loaded + live-bound, but NOT a loop member, so the
            // gate never fires (matches the map loopMemberInWindow carve-out).
            Recording rec = MakeDepotAnchorRecording();
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == DepotPid);
            GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => DepotLaunchGuid);
            RelativeAnchorResolver.FrameProviderForTesting = () => 70;
            StampLiveBind(DepotRecordingId);

            Assert.False(GhostPlaybackLogic.IsLiveAnchorDoubleSuppressed(rec, loopingLike: false));
        }

        // Drives a real resolve through the public TryResolveAnchorPose path with a
        // live-anchor delegate, which is the EXACT site that stamps the live-bind ledger
        // (TryBindLiveLaunchMatchedAnchorPose, source=live). Asserts the resolve actually
        // bound so the stamp is proven, not assumed.
        private static void StampLiveBind(string anchorRecordingId)
        {
            var anchorRec = new Recording
            {
                RecordingId = anchorRecordingId,
                VesselName = "Anchor",
                VesselPersistentId = DepotPid,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            };
            var tree = new RecordingTree { Id = "tree-test" };
            tree.Recordings[anchorRecordingId] = anchorRec;

            var context = new RelativeAnchorResolverContext(
                focusTree: tree,
                focusRecordingId: "focus-member",
                focusTreeId: tree.Id,
                tryResolveLiveLaunchMatchedAnchorPose: (rec, ut) =>
                    (new Vector3d(1.0, 2.0, 3.0), Quaternion.identity));

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                context,
                anchorRecordingId,
                ut: 100.0,
                visited: null,
                out _,
                out _);
            Assert.True(resolved);
        }

        private static Recording MakeDepotAnchorRecording()
        {
            return new Recording
            {
                RecordingId = DepotRecordingId,
                VesselName = "Depot",
                VesselPersistentId = DepotPid,
                RecordedVesselGuid = DepotLaunchGuid,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            };
        }

        private static Recording MakeDelivererMemberRecording()
        {
            return new Recording
            {
                RecordingId = DelivererRecordingId,
                VesselName = "Deliverer Mun 1",
                VesselPersistentId = DelivererPid,
                RecordedVesselGuid = DelivererLaunchGuid,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            };
        }
    }
}
