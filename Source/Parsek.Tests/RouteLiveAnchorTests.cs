using System;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    // Logistics route live-anchor bind: unit coverage for the guid gate on the anchor's
    // live-vessel match (Step 1) and the generalized whole-loop Step-2 double-suppression.
    // The whole-loop suppression keys directly on the guid-gated
    // GhostPlaybackLogic.RealVesselExistsForRecording: a loop member's OWN ghost is hidden
    // for the ENTIRE loop whenever its launch-matched live vessel is loaded, not just
    // during the ~2-frame docking bind window (the LiveAnchorBindTracker that drove the
    // bind-window variant was removed because the existence check subsumes it). The guid
    // tests touch the shared GhostPlaybackLogic vessel-exists / guid-resolver overrides, so
    // the class is [Collection("Sequential")] and resets them in the ctor + Dispose.
    [Collection("Sequential")]
    public class RouteLiveAnchorTests : IDisposable
    {
        private const uint DepotPid = 4277041026u;
        private const string DepotRecordingId = "7e0d79b5d57a40d29400afd8b49d1906";
        private const string DepotLaunchGuid = "424fd14c8870407f81dffdf606a92db2";
        private const string DepotRelaunchGuid = "05d3ea0f00000000000000000000ffff";

        public RouteLiveAnchorTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GhostPlaybackLogic.ResetVesselExistsOverride();
            GhostPlaybackLogic.ResetVesselCacheForTesting();
        }

        public void Dispose()
        {
            GhostPlaybackLogic.ResetVesselExistsOverride();
            GhostPlaybackLogic.ResetVesselCacheForTesting();
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
    }
}
