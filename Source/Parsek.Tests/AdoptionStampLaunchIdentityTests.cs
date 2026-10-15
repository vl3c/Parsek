using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Adoption stamps (<c>SpawnedVesselPersistentId == VesselPersistentId</c>) carry the
    /// craft-baked pid, which every launch of the craft reuses, so a spawn-pid reader must not
    /// treat them as KSP-unique (gap 1), and the #226 replay/revert bypass is kept only for the
    /// recording's own launch taken back into the recording's past (gap 2).
    /// </summary>
    [Collection("Sequential")]
    public class AdoptionStampLaunchIdentityTests : System.IDisposable
    {
        private const uint BakedPid = 4242u;
        private const string LaunchOneGuid = "11111111111111111111111111111111";
        private const string LaunchTwoGuid = "22222222222222222222222222222222";

        private readonly List<string> logLines = new List<string>();

        public AdoptionStampLaunchIdentityTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            VesselSpawner.ResetMaterializedSourceVesselExistsOverrideForTesting();
        }

        public void Dispose()
        {
            VesselSpawner.ResetMaterializedSourceVesselExistsOverrideForTesting();
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        /// <summary>L1's committed recording, its end vessel adopted (stamp = baked pid).</summary>
        private static Recording AdoptedLaunchOneRecording(string id = "rec_l1")
        {
            return new Recording
            {
                RecordingId = id,
                TreeId = "tree_l1",
                VesselName = "Kerbal X",
                VesselPersistentId = BakedPid,
                RecordedVesselGuid = LaunchOneGuid,
                VesselSpawned = true,
                SpawnedVesselPersistentId = BakedPid,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0,
                TerminalStateValue = TerminalState.Orbiting,
            };
        }

        private static RecordingTree TreeOf(Recording rec)
        {
            var tree = new RecordingTree
            {
                Id = rec.TreeId,
                TreeName = "Kerbal X",
                RootRecordingId = rec.RecordingId,
                ActiveRecordingId = rec.RecordingId,
            };
            tree.Recordings[rec.RecordingId] = rec;
            return tree;
        }

        private static void Commit(RecordingTree tree)
        {
            RecordingStore.AddCommittedTreeForTesting(tree);
            foreach (Recording rec in tree.Recordings.Values)
                RecordingStore.AddCommittedInternal(rec);
        }

        #region Gap 1 - committed-tree matchers

        // Fails if: Tracking Station Fly / Map Switch-To of a relaunch of the same craft
        // (same baked pid, new launch guid) matches the older launch's committed tree through
        // its adoption stamp, resuming the old recording onto the new vessel.
        [Fact]
        public void TryFindCommittedTreeMatchingVessel_AdoptionStampOtherLaunch_NoMatchAndLogs()
        {
            Commit(TreeOf(AdoptedLaunchOneRecording()));

            bool matched = ParsekFlight.TryFindCommittedTreeMatchingVessel(
                BakedPid, LaunchTwoGuid, out RecordingTree tree);

            Assert.False(matched);
            Assert.Null(tree);
            Assert.Contains(logLines, l =>
                l.Contains("[Flight]")
                && l.Contains("TryFindCommittedTreeMatchingVessel: refused 1 adoption-stamped")
                && l.Contains("pid=" + BakedPid)
                && l.Contains("liveGuid=" + LaunchTwoGuid));
        }

        [Fact]
        public void TryFindCommittedTreeMatchingVessel_AdoptionStampSameLaunch_Matches()
        {
            var rec = AdoptedLaunchOneRecording();
            Commit(TreeOf(rec));

            Assert.True(ParsekFlight.TryFindCommittedTreeMatchingVessel(BakedPid, LaunchOneGuid));
            // An unknown live guid keeps the pid-only fallback.
            Assert.True(ParsekFlight.TryFindCommittedTreeMatchingVessel(BakedPid, null));
            Assert.DoesNotContain(logLines, l => l.Contains("adoption-stamped"));
        }

        // Fails if: the guid gate is applied to a GENUINE Parsek spawn pid (KSP-unique, minted at
        // spawn), whose spawned vessel may carry a guid other than the recorded one.
        [Fact]
        public void TryFindCommittedTreeMatchingVessel_GenuineSpawnPid_MatchesWhateverTheGuid()
        {
            var rec = AdoptedLaunchOneRecording();
            rec.SpawnedVesselPersistentId = 999u;
            Commit(TreeOf(rec));

            Assert.True(ParsekFlight.TryFindCommittedTreeMatchingVessel(999u, LaunchTwoGuid));
        }

        [Fact]
        public void TryFindCommittedTreeForSpawnedVessel_AdoptionStampOtherLaunch_NoMatch()
        {
            var tree = TreeOf(AdoptedLaunchOneRecording());

            bool found = ParsekFlight.TryFindCommittedTreeForSpawnedVessel(
                new List<RecordingTree> { tree },
                BakedPid,
                out RecordingTree matchedTree,
                out string matchedRecordingId,
                LaunchTwoGuid);

            Assert.False(found);
            Assert.Null(matchedTree);
            Assert.Null(matchedRecordingId);
            Assert.Contains(logLines, l =>
                l.Contains("TryFindCommittedTreeForSpawnedVessel: refused 1 adoption-stamped"));
        }

        [Fact]
        public void TryFindCommittedTreeForSpawnedVessel_AdoptionStampSameLaunch_Matches()
        {
            var tree = TreeOf(AdoptedLaunchOneRecording());

            bool found = ParsekFlight.TryFindCommittedTreeForSpawnedVessel(
                new List<RecordingTree> { tree },
                BakedPid,
                out RecordingTree matchedTree,
                out string matchedRecordingId,
                LaunchOneGuid);

            Assert.True(found);
            Assert.Same(tree, matchedTree);
            Assert.Equal("rec_l1", matchedRecordingId);
        }

        [Fact]
        public void IsCommittedSpawnStampMatch_TruthTable()
        {
            var adopted = AdoptedLaunchOneRecording();
            Assert.True(ParsekFlight.IsCommittedSpawnStampMatch(adopted, BakedPid, LaunchOneGuid));
            Assert.True(ParsekFlight.IsCommittedSpawnStampMatch(adopted, BakedPid, null));
            Assert.False(ParsekFlight.IsCommittedSpawnStampMatch(adopted, BakedPid, LaunchTwoGuid));
            Assert.True(ParsekFlight.IsOtherLaunchSpawnStamp(adopted, BakedPid, LaunchTwoGuid));
            Assert.False(ParsekFlight.IsOtherLaunchSpawnStamp(adopted, BakedPid, LaunchOneGuid));

            adopted.VesselSpawned = false;
            Assert.False(ParsekFlight.IsCommittedSpawnStampMatch(adopted, BakedPid, LaunchOneGuid));
            Assert.False(ParsekFlight.IsOtherLaunchSpawnStamp(adopted, BakedPid, LaunchTwoGuid));
        }

        #endregion

        #region Gap 1 - spawn-death liveness and the replace gate

        // Fails if: a relaunch of the same craft keeps an older launch's adoption stamp alive
        // in the spawn-death check.
        [Fact]
        public void IsSpawnClaimHeldByLiveVessel_AdoptionStampNeedsTheStampedLaunch()
        {
            var adopted = AdoptedLaunchOneRecording();
            Assert.True(ParsekPlaybackPolicy.IsSpawnClaimHeldByLiveVessel(adopted, BakedPid, LaunchOneGuid));
            Assert.True(ParsekPlaybackPolicy.IsSpawnClaimHeldByLiveVessel(adopted, BakedPid, null));
            Assert.False(ParsekPlaybackPolicy.IsSpawnClaimHeldByLiveVessel(adopted, BakedPid, LaunchTwoGuid));

            adopted.SpawnedVesselPersistentId = 999u; // genuine spawn pid
            Assert.True(ParsekPlaybackPolicy.IsSpawnClaimHeldByLiveVessel(adopted, 999u, LaunchTwoGuid));
        }

        [Fact]
        public void IsSpawnStampStaleForReplace_TruthTable()
        {
            var adopted = AdoptedLaunchOneRecording();
            var live = new HashSet<uint> { BakedPid };
            bool otherLaunch;

            Assert.False(RecordingStore.IsSpawnStampStaleForReplace(adopted, null, null, out otherLaunch));
            Assert.True(RecordingStore.IsSpawnStampStaleForReplace(
                adopted, new HashSet<uint> { 1u }, null, out otherLaunch));
            Assert.False(otherLaunch);
            Assert.False(RecordingStore.IsSpawnStampStaleForReplace(adopted, live, null, out otherLaunch));
            Assert.False(RecordingStore.IsSpawnStampStaleForReplace(
                adopted, live, new Dictionary<uint, string> { { BakedPid, LaunchOneGuid } }, out otherLaunch));
            Assert.True(RecordingStore.IsSpawnStampStaleForReplace(
                adopted, live, new Dictionary<uint, string> { { BakedPid, LaunchTwoGuid } }, out otherLaunch));
            Assert.True(otherLaunch);
        }

        [Fact]
        public void PreserveLiveRuntimeFieldsOnReplace_StampHeldByOtherLaunch_NotReinstalled()
        {
            var existing = AdoptedLaunchOneRecording();
            var incoming = new Recording
            {
                RecordingId = existing.RecordingId,
                VesselName = existing.VesselName,
                VesselPersistentId = BakedPid,
                RecordedVesselGuid = LaunchOneGuid,
            };

            RecordingStore.PreserveLiveRuntimeFieldsOnReplace(
                existing, incoming, out _, out _,
                new HashSet<uint> { BakedPid },
                new Dictionary<uint, string> { { BakedPid, LaunchTwoGuid } });

            Assert.False(incoming.VesselSpawned);
            Assert.Equal(0u, incoming.SpawnedVesselPersistentId);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]")
                && l.Contains("dropped stale spawn stamp")
                && l.Contains("adoption stamp now held by another launch of the craft"));
        }

        [Fact]
        public void PreserveLiveRuntimeFieldsOnReplace_StampHeldBySameLaunch_Reinstalled()
        {
            var existing = AdoptedLaunchOneRecording();
            var incoming = new Recording
            {
                RecordingId = existing.RecordingId,
                VesselPersistentId = BakedPid,
                RecordedVesselGuid = LaunchOneGuid,
            };

            RecordingStore.PreserveLiveRuntimeFieldsOnReplace(
                existing, incoming, out _, out _,
                new HashSet<uint> { BakedPid },
                new Dictionary<uint, string> { { BakedPid, LaunchOneGuid } });

            Assert.True(incoming.VesselSpawned);
            Assert.Equal(BakedPid, incoming.SpawnedVesselPersistentId);
        }

        [Fact]
        public void AddLiveVesselGuidForReplaceGate_DisagreeingSourcesRecordUnknown()
        {
            var map = new Dictionary<uint, string>();
            RecordingStore.AddLiveVesselGuidForReplaceGate(map, 7u, LaunchOneGuid);
            RecordingStore.AddLiveVesselGuidForReplaceGate(map, 7u, LaunchOneGuid);
            Assert.Equal(LaunchOneGuid, map[7u]);
            RecordingStore.AddLiveVesselGuidForReplaceGate(map, 7u, LaunchTwoGuid);
            Assert.Null(map[7u]);
            RecordingStore.AddLiveVesselGuidForReplaceGate(map, 7u, LaunchOneGuid);
            Assert.Null(map[7u]);
        }

        #endregion

        #region Gap 2 - #226 bypass narrowing

        private static Recording UnspawnedLaunchOneRecording()
        {
            var rec = AdoptedLaunchOneRecording("rec_unspawned");
            rec.VesselSpawned = false;
            rec.SpawnedVesselPersistentId = 0u;
            return rec;
        }

        [Fact]
        public void ShouldKeepReplayBypass_SameLaunchInRecordingPast_Kept()
        {
            // A revert / rewind re-flight: same launch, scene entered before the end.
            Assert.True(VesselSpawner.ShouldKeepReplayBypassForLaunchIdentity(
                UnspawnedLaunchOneRecording(), BakedPid, LaunchOneGuid, 150.0, BakedPid, LaunchOneGuid,
                out string reason));
            Assert.Equal(VesselSpawner.ReplayBypassReasonSameLaunchInPast, reason);
        }

        [Fact]
        public void ShouldKeepReplayBypass_SceneEnteredAfterEnd_Withheld()
        {
            Assert.False(VesselSpawner.ShouldKeepReplayBypassForLaunchIdentity(
                UnspawnedLaunchOneRecording(), BakedPid, LaunchOneGuid, 200.0, BakedPid, LaunchOneGuid,
                out string reason));
            Assert.Equal(VesselSpawner.ReplayBypassReasonSceneEnteredAfterEnd, reason);
        }

        [Fact]
        public void ShouldKeepReplayBypass_DifferentLaunch_Withheld()
        {
            Assert.False(VesselSpawner.ShouldKeepReplayBypassForLaunchIdentity(
                UnspawnedLaunchOneRecording(), BakedPid, LaunchTwoGuid, 150.0, BakedPid, LaunchTwoGuid,
                out string reason));
            Assert.Equal(VesselSpawner.ReplayBypassReasonDifferentLaunch, reason);
        }

        [Fact]
        public void ShouldKeepReplayBypass_UnknownContext_KeepsTodaysBypass()
        {
            var rec = UnspawnedLaunchOneRecording();
            string reason;
            // Unknown scene-entry UT.
            Assert.True(VesselSpawner.ShouldKeepReplayBypassForLaunchIdentity(
                rec, BakedPid, LaunchOneGuid, double.NaN, 0u, null, out reason));
            // Unknown live guid.
            Assert.True(VesselSpawner.ShouldKeepReplayBypassForLaunchIdentity(
                rec, BakedPid, null, 150.0, 0u, null, out reason));
            // Recording without an end.
            var noEnd = new Recording { RecordingId = "no_end", VesselPersistentId = BakedPid };
            Assert.True(VesselSpawner.ShouldKeepReplayBypassForLaunchIdentity(
                noEnd, BakedPid, LaunchOneGuid, 5000.0, 0u, null, out reason));
            // No recording at all.
            Assert.True(VesselSpawner.ShouldKeepReplayBypassForLaunchIdentity(
                null, BakedPid, LaunchOneGuid, 5000.0, 0u, null, out reason));
            Assert.Equal(VesselSpawner.ReplayBypassReasonPidOnly, reason);
        }

        [Fact]
        public void ShouldKeepReplayBypass_ActiveVesselSameLaunch_KeptWhenSceneEntryIsOtherLaunch()
        {
            Assert.True(VesselSpawner.ShouldKeepReplayBypassForLaunchIdentity(
                UnspawnedLaunchOneRecording(), BakedPid, LaunchTwoGuid, 150.0, BakedPid, LaunchOneGuid,
                out string reason));
            Assert.Equal(VesselSpawner.ReplayBypassReasonSameLaunchInPast, reason);
        }

        // Fails if: entering a flight scene on a recording's own launch AFTER the recording's
        // end (the vessel IS the recording's end vessel) still bypasses adoption and spawns a
        // second copy of the vessel being flown.
        [Fact]
        public void CurrentFlightBypass_SceneEnteredAfterEnd_FallsThroughToAdoption()
        {
            var rec = UnspawnedLaunchOneRecording();
            RecordingStore.SceneEntryActiveVesselPid = BakedPid;
            RecordingStore.SceneEntryActiveVesselGuid = LaunchOneGuid;
            RecordingStore.SceneEntryUT = 250.0;
            VesselSpawner.SetMaterializedSourceVesselExistsOverrideForTesting(pid => pid == BakedPid);
            VesselSpawner.SetMaterializedSourceVesselGuidOverrideForTesting(pid => LaunchOneGuid);

            bool bypass = VesselSpawner.ShouldAllowExistingSourceDuplicateForCurrentFlight(rec);
            bool adopted = VesselSpawner.TryAdoptExistingSourceVesselForSpawn(
                rec, "Spawner", "unit-test", bypass);

            Assert.False(bypass);
            Assert.True(adopted);
            Assert.Equal(BakedPid, rec.SpawnedVesselPersistentId);
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]")
                && l.Contains("ShouldAllowExistingSourceDuplicateForCurrentFlight=false")
                && l.Contains("reason=" + VesselSpawner.ReplayBypassReasonSceneEnteredAfterEnd)
                && l.Contains("#226 bypass withheld"));
        }

        // Fails if: the narrowing removes the bypass a revert / rewind re-flight needs, so the
        // committed recording adopts the player's re-flight instead of spawning its end vessel.
        [Fact]
        public void CurrentFlightBypass_RevertReFlight_KeepsBypassAndSkipsAdoption()
        {
            var rec = UnspawnedLaunchOneRecording();
            RecordingStore.SceneEntryActiveVesselPid = BakedPid;
            RecordingStore.SceneEntryActiveVesselGuid = LaunchOneGuid;
            RecordingStore.SceneEntryUT = 100.0;
            VesselSpawner.SetMaterializedSourceVesselExistsOverrideForTesting(pid => pid == BakedPid);
            VesselSpawner.SetMaterializedSourceVesselGuidOverrideForTesting(pid => LaunchOneGuid);

            bool bypass = VesselSpawner.ShouldAllowExistingSourceDuplicateForCurrentFlight(rec);
            bool adopted = VesselSpawner.TryAdoptExistingSourceVesselForSpawn(
                rec, "Spawner", "unit-test", bypass);

            Assert.True(bypass);
            Assert.False(adopted);
            Assert.False(rec.VesselSpawned);
            Assert.Contains(logLines, l =>
                l.Contains("ShouldAllowExistingSourceDuplicateForCurrentFlight=true"));
        }

        // A different launch of the craft: the bypass is withheld, and adoption refuses it on
        // the guid, so the spawn proceeds exactly as it did under the bypass.
        [Fact]
        public void CurrentFlightBypass_DifferentLaunch_WithheldButAdoptionStillRefuses()
        {
            var rec = UnspawnedLaunchOneRecording();
            RecordingStore.SceneEntryActiveVesselPid = BakedPid;
            RecordingStore.SceneEntryActiveVesselGuid = LaunchTwoGuid;
            RecordingStore.SceneEntryUT = 100.0;
            VesselSpawner.SetMaterializedSourceVesselExistsOverrideForTesting(pid => pid == BakedPid);
            VesselSpawner.SetMaterializedSourceVesselGuidOverrideForTesting(pid => LaunchTwoGuid);

            bool bypass = VesselSpawner.ShouldAllowExistingSourceDuplicateForCurrentFlight(rec);
            bool adopted = VesselSpawner.TryAdoptExistingSourceVesselForSpawn(
                rec, "Spawner", "unit-test", bypass);

            Assert.False(bypass);
            Assert.False(adopted);
            Assert.False(rec.VesselSpawned);
            Assert.Contains(logLines, l =>
                l.Contains("reason=" + VesselSpawner.ReplayBypassReasonDifferentLaunch));
        }

        #endregion
    }
}
