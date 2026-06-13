using System;
using System.Collections.Generic;
using Parsek;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    // Logistics route live-anchor bind (Step 2): unit coverage for the guid gate on
    // the anchor's live-vessel match and the narrow "is this anchor serving an
    // in-window relative member" suppression predicate. Both touch the shared
    // GhostPlaybackLogic vessel-exists / guid-resolver overrides, so the class is
    // [Collection("Sequential")] and resets them in the constructor + Dispose.
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

        [Fact]
        public void RealVesselExistsForRecording_SameCraftDifferentLaunch_DoesNotMatch()
        {
            // The Depot recording's craft is loaded (same baked pid) but it is a
            // DIFFERENT launch (guid conclusively differs). A bare pid match would
            // bind the live-anchor to the wrong same-craft vessel; the guid gate
            // rejects it. Proves the three on-disk Depot launches never cross-bind.
            Recording rec = MakeDepotAnchorRecording();
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == DepotPid);
            GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => DepotRelaunchGuid);

            Assert.False(GhostPlaybackLogic.RealVesselExistsForRecording(rec));
        }

        [Fact]
        public void RealVesselExistsForRecording_SameLaunch_Matches()
        {
            // Recording + live vessel share the launch guid: the fix fires for the
            // real Depot the player is flying.
            Recording rec = MakeDepotAnchorRecording();
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == DepotPid);
            GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => DepotLaunchGuid);

            Assert.True(GhostPlaybackLogic.RealVesselExistsForRecording(rec));
        }

        [Fact]
        public void IsLiveLaunchMatchedAnchorForActiveRelativeMember_TrueWithActiveRelativeDependent()
        {
            // Anchor's launch-matched live vessel is loaded AND an in-window Relative
            // member points at it -> suppress the anchor's own loop ghost double.
            Recording anchor = MakeDepotAnchorRecording();
            Recording member = MakeRelativeMember("deliverer", DepotRecordingId, currentUT: 5.0);
            var committed = new List<Recording> { anchor, member };

            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == DepotPid);
            GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => DepotLaunchGuid);

            bool result = GhostPlaybackLogic.IsLiveLaunchMatchedAnchorForActiveRelativeMember(
                anchor, committed, GhostPlaybackLogic.LoopUnitSet.Empty, currentUT: 5.0);

            Assert.True(result);
        }

        [Fact]
        public void IsLiveLaunchMatchedAnchorForActiveRelativeMember_FalseWithNoRelativeDependent()
        {
            // A Depot watched looping from afar with NO live relative dependent still
            // draws its own ghost (validators' point d): no member anchored to it ->
            // predicate false even though the live vessel is loaded.
            Recording anchor = MakeDepotAnchorRecording();
            // A member that is NOT relative-anchored to the Depot.
            Recording member = MakeRelativeMember("unrelated", "some-other-anchor", currentUT: 5.0);
            var committed = new List<Recording> { anchor, member };

            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == DepotPid);
            GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => DepotLaunchGuid);

            bool result = GhostPlaybackLogic.IsLiveLaunchMatchedAnchorForActiveRelativeMember(
                anchor, committed, GhostPlaybackLogic.LoopUnitSet.Empty, currentUT: 5.0);

            Assert.False(result);
        }

        [Fact]
        public void IsLiveLaunchMatchedAnchorForActiveRelativeMember_FalseWhenAnchorLiveVesselDifferentLaunch()
        {
            // Same-craft DIFFERENT-launch live vessel: the guid gate rejects the live
            // match, so the predicate short-circuits false (no suppression).
            Recording anchor = MakeDepotAnchorRecording();
            Recording member = MakeRelativeMember("deliverer", DepotRecordingId, currentUT: 5.0);
            var committed = new List<Recording> { anchor, member };

            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == DepotPid);
            GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => DepotRelaunchGuid);

            bool result = GhostPlaybackLogic.IsLiveLaunchMatchedAnchorForActiveRelativeMember(
                anchor, committed, GhostPlaybackLogic.LoopUnitSet.Empty, currentUT: 5.0);

            Assert.False(result);
        }

        [Fact]
        public void IsLiveLaunchMatchedAnchorForActiveRelativeMember_FalseWhenAnchorLiveVesselAbsent()
        {
            // No live vessel loaded at all -> distant-watch playback unchanged, the
            // anchor draws its own ghost.
            Recording anchor = MakeDepotAnchorRecording();
            Recording member = MakeRelativeMember("deliverer", DepotRecordingId, currentUT: 5.0);
            var committed = new List<Recording> { anchor, member };

            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(_ => false);

            bool result = GhostPlaybackLogic.IsLiveLaunchMatchedAnchorForActiveRelativeMember(
                anchor, committed, GhostPlaybackLogic.LoopUnitSet.Empty, currentUT: 5.0);

            Assert.False(result);
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

        private static Recording MakeRelativeMember(
            string recordingId, string anchorRecordingId, double currentUT)
        {
            var rec = new Recording
            {
                RecordingId = recordingId,
                VesselName = recordingId,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = currentUT - 5.0,
                endUT = currentUT + 5.0,
                anchorRecordingId = anchorRecordingId,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = currentUT - 5.0 },
                    new TrajectoryPoint { ut = currentUT + 5.0 },
                },
                checkpoints = new List<OrbitSegment>(),
            });
            return rec;
        }
    }
}
