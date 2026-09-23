using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class ChainGhostSkipTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ChainGhostSkipTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GhostPlaybackLogic.ResetVesselExistsOverride();
            GhostPlaybackLogic.ResetIsGhostedOverride();
        }

        public void Dispose()
        {
            GhostPlaybackLogic.ResetVesselExistsOverride();
            GhostPlaybackLogic.ResetVesselCacheForTesting();
            GhostPlaybackLogic.ResetIsGhostedOverride();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Chain-aware ghost skip: ghosted vessels must NOT be skipped
        // ================================================================

        [Fact]
        public void GhostedVessel_SkipBypassed()
        {
            // A vessel ghosted by the chain system should NOT be skipped,
            // even if the real vessel "exists" — because VesselGhoster has
            // despawned it, background recording data must produce a ghost GO.
            GhostPlaybackLogic.SetIsGhostedOverride(pid => pid == 100);
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => true);

            bool result = GhostPlaybackLogic.ShouldSkipExternalVesselGhost(
                "tree-abc", 100, false);

            Assert.False(result);
            Assert.Contains(logLines, l =>
                l.Contains("[Ghoster]") && l.Contains("pid=100") &&
                l.Contains("ghosted by chain") && l.Contains("NOT skipping"));
        }

        [Fact]
        public void NullTreeId_NoEffect()
        {
            // Null treeId triggers early return regardless of ghosted state.
            GhostPlaybackLogic.SetIsGhostedOverride(pid => true);
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => true);

            bool result = GhostPlaybackLogic.ShouldSkipExternalVesselGhost(
                null, 100, false);

            Assert.False(result);
        }

        [Fact]
        public void ActiveRecording_NeverSkipped()
        {
            // Active recording is the player's own vessel — always produce ghost.
            GhostPlaybackLogic.SetIsGhostedOverride(pid => true);
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => true);

            bool result = GhostPlaybackLogic.ShouldSkipExternalVesselGhost(
                "tree-abc", 100, true);

            Assert.False(result);
        }

        [Fact]
        public void GhostedOverrideNull_FallsToRealVesselCheck()
        {
            // When no chain system is active (isGhostedOverride = null),
            // behavior is identical to pre-6b: falls through to RealVesselExists.
            GhostPlaybackLogic.ResetIsGhostedOverride();
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => true);

            bool result = GhostPlaybackLogic.ShouldSkipExternalVesselGhost(
                "tree-abc", 400, false);

            Assert.True(result);
        }

        // ================================================================
        // #16: the production overload is launch-Guid gated. "The real vessel
        // is already its own visual" only holds when the LIVE vessel is the
        // recording's own launch; a preserved relaunch of the same craft
        // carries the same craft-baked pid and must not hide the ghost of a
        // vessel that is not in the world at all.
        // ================================================================

        [Fact]
        public void ExternalVesselGhost_LiveVesselIsADifferentLaunchOfTheSameCraft_NotSkipped()
        {
            GhostPlaybackLogic.SetIsGhostedOverride(pid => false);
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => true);
            GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(
                pid => "22222222-2222-2222-2222-222222222222");

            var rec = new Recording
            {
                RecordingId = "rec-a",
                TreeId = "tree-abc",
                VesselName = "Kerbal X",
                VesselPersistentId = 500u,
                RecordedVesselGuid = "11111111-1111-1111-1111-111111111111"
            };

            Assert.False(GhostPlaybackLogic.ShouldSkipExternalVesselGhost(rec, false));
        }

        [Fact]
        public void ExternalVesselGhost_LiveVesselIsTheRecordedLaunch_StillSkipped()
        {
            const string launchGuid = "11111111-1111-1111-1111-111111111111";
            GhostPlaybackLogic.SetIsGhostedOverride(pid => false);
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => true);
            GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => launchGuid);

            var rec = new Recording
            {
                RecordingId = "rec-a",
                TreeId = "tree-abc",
                VesselPersistentId = 500u,
                RecordedVesselGuid = launchGuid
            };

            Assert.True(GhostPlaybackLogic.ShouldSkipExternalVesselGhost(rec, false));
        }

        [Fact]
        public void ExternalVesselGhost_UnknownLiveGuid_FallsBackToPidOnlySkip()
        {
            // Legacy save / un-backfillable Guid: unchanged pre-#16 behaviour.
            GhostPlaybackLogic.SetIsGhostedOverride(pid => false);
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => true);
            GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => null);

            var rec = new Recording
            {
                RecordingId = "rec-a",
                TreeId = "tree-abc",
                VesselPersistentId = 500u,
                RecordedVesselGuid = "11111111-1111-1111-1111-111111111111"
            };

            Assert.True(GhostPlaybackLogic.ShouldSkipExternalVesselGhost(rec, false));
        }

        [Fact]
        public void ExternalVesselGhost_NullRecording_NotSkipped()
        {
            Assert.False(GhostPlaybackLogic.ShouldSkipExternalVesselGhost(null, false));
        }
    }
}
