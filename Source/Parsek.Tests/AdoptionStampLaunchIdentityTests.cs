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
            VesselNameStore.ResetForTesting();
            VesselNameStore.NowUtProviderForTesting = () => 1000.0;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            VesselSpawner.ResetMaterializedSourceVesselExistsOverrideForTesting();
        }

        public void Dispose()
        {
            VesselSpawner.ResetMaterializedSourceVesselExistsOverrideForTesting();
            VesselNameStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        /// <summary>A recording end snapshot as <c>ProtoVessel.Save</c> writes it: the launch
        /// guid in <c>pid</c>, the uint in <c>persistentId</c>.</summary>
        private static ConfigNode SnapshotOf(string guid, uint pid)
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("pid", guid);
            node.AddValue("persistentId", pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return node;
        }

        /// <summary>L1's committed recording, its end vessel adopted (stamp = baked pid), its
        /// end snapshot captured from L1.</summary>
        private static Recording AdoptedLaunchOneRecording(string id = "rec_l1")
        {
            return new Recording
            {
                VesselSnapshot = SnapshotOf(LaunchOneGuid, BakedPid),
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

        #region Resumed Parsek-spawned vessel (review regression)

        private const string SpawnGuid = "55555555555555555555555555555555";

        // Fails if: a resumed Parsek-spawned vessel reads as an adoption stamp of another
        // launch. The spawn re-keys the vessel (fresh guid, unique pid 200); the Tracking Station
        // Fly re-pids the recording to 200 and keeps its guid; the next commit re-installs the
        // spawn claim, so spawned pid == recorded pid with differing guids. The Resume / Spawn
        // link rows join the two guids, and all three readers must honour them.
        [Fact]
        public void ResumedSpawnedVessel_RecommitThenAliveVesselHoldsItsClaim()
        {
            var tree = new RecordingTree
            {
                Id = "tree_resume", TreeName = "Probe", RootRecordingId = "rec_tip", ActiveRecordingId = "rec_tip",
            };
            var committed = new Recording
            {
                RecordingId = "rec_tip", TreeId = "tree_resume", VesselName = "Probe",
                ExplicitStartUT = 150, ExplicitEndUT = 200, VesselPersistentId = 20, RecordedVesselGuid = LaunchOneGuid,
                VesselSpawned = true, SpawnedVesselPersistentId = 200, TerminalStateValue = TerminalState.Landed,
            };
            tree.Recordings["rec_tip"] = committed;
            RecordingStore.AddCommittedTreeForTesting(tree);
            RecordingStore.AddCommittedInternal(committed);

            Assert.True(ParsekFlight.TryTakeCommittedTreeForSpawnedVesselRestore(
                200, out RecordingTree liveTree, out string recId, out _, activeVesselGuid: SpawnGuid));
            Recording resumed = liveTree.Recordings[recId];
            Assert.Equal(200u, resumed.VesselPersistentId);
            Assert.Equal(0u, resumed.SpawnedVesselPersistentId);
            Assert.Equal(LaunchOneGuid, resumed.RecordedVesselGuid);

            var livePids = new HashSet<uint> { 200u };
            var guids = new Dictionary<uint, string> { { 200u, SpawnGuid } };
            RecordingStore.PreserveLiveRuntimeFieldsOnReplace(
                committed, resumed, out _, out _, livePids, guids);
            Assert.True(resumed.VesselSpawned);
            Assert.Equal(200u, resumed.SpawnedVesselPersistentId);

            Assert.True(ParsekPlaybackPolicy.IsSpawnClaimHeldByLiveVessel(resumed, 200u, SpawnGuid));
            Assert.True(ParsekFlight.IsCommittedSpawnStampMatch(resumed, 200u, SpawnGuid));
            Assert.False(ParsekFlight.IsOtherLaunchSpawnStamp(resumed, 200u, SpawnGuid));
            Assert.False(RecordingStore.IsSpawnStampStaleForReplace(resumed, livePids, guids, out _));
        }

        [Fact]
        public void ResumedSpawnedVessel_SpawnLinkKeepsSecondFlyAndStamp()
        {
            Assert.True(VesselNameStore.AddLink(SpawnGuid, LaunchOneGuid, VesselIdentityLinkKind.Spawn, "test"));
            var resumed = new Recording
            {
                RecordingId = "rec_tip", TreeId = "tree_resume", VesselName = "Probe",
                ExplicitStartUT = 150, ExplicitEndUT = 300, VesselPersistentId = 200, RecordedVesselGuid = LaunchOneGuid,
                VesselSpawned = true, SpawnedVesselPersistentId = 200, TerminalStateValue = TerminalState.Landed,
            };

            Assert.True(ParsekFlight.IsCommittedSpawnStampMatch(resumed, 200u, SpawnGuid));
            Assert.True(ParsekPlaybackPolicy.IsSpawnClaimHeldByLiveVessel(resumed, 200u, SpawnGuid));
            Assert.False(RecordingStore.IsSpawnStampStaleForReplace(
                resumed, new HashSet<uint> { 200u }, new Dictionary<uint, string> { { 200u, SpawnGuid } }, out _));
            // A relaunch with no link stays refused.
            Assert.False(ParsekFlight.IsCommittedSpawnStampMatch(resumed, 200u, LaunchTwoGuid));
        }

        [Fact]
        public void SpawnStampHeldBy_FollowsALinkChain_AndIgnoresUnrelatedLinks()
        {
            const string middle = "33333333333333333333333333333333";
            var rec = AdoptedLaunchOneRecording();
            Assert.True(VesselNameStore.AddLink(LaunchTwoGuid, "44444444444444444444444444444444",
                VesselIdentityLinkKind.Spawn, "unrelated"));
            Assert.False(VesselLaunchIdentity.SpawnStampHeldBy(rec, BakedPid, LaunchTwoGuid));

            Assert.True(VesselNameStore.AddLink(SpawnGuid, middle, VesselIdentityLinkKind.Resume, "test"));
            Assert.True(VesselNameStore.AddLink(middle, LaunchOneGuid, VesselIdentityLinkKind.Spawn, "test"));
            Assert.True(VesselLaunchIdentity.SpawnStampHeldBy(rec, BakedPid, SpawnGuid));
        }

        [Fact]
        public void SpawnStampHeldByCore_TakesTheLinkAnswerAsInput()
        {
            var rec = AdoptedLaunchOneRecording();
            Assert.False(VesselLaunchIdentity.SpawnStampHeldByCore(rec, BakedPid, LaunchTwoGuid, false));
            Assert.True(VesselLaunchIdentity.SpawnStampHeldByCore(rec, BakedPid, LaunchTwoGuid, true));
            Assert.True(VesselLaunchIdentity.SpawnStampHeldByCore(rec, BakedPid, LaunchOneGuid, false));
            Assert.False(VesselLaunchIdentity.SpawnStampHeldByCore(rec, 1u, LaunchOneGuid, true));
        }

        #endregion

        #region Upgraded save: resumed Parsek-spawned vessel with no link rows

        // A save from before resumes recorded their link (0.10.5 and earlier) can hold a resumed,
        // re-committed Parsek-spawned vessel with NO link: recording pid 200, spawn pid 200,
        // guid L1; live vessel pid 200 with the spawn's fresh guid. The commit re-snapshotted
        // the live vessel, so the recording's end snapshot carries the live guid.
        private static Recording UpgradedSaveResumedRecording(string snapshotGuid)
        {
            return new Recording
            {
                RecordingId = "rec_tip", TreeId = "tree_resume", VesselName = "Probe",
                ExplicitStartUT = 150, ExplicitEndUT = 300, VesselPersistentId = 200,
                RecordedVesselGuid = LaunchOneGuid, VesselSpawned = true,
                SpawnedVesselPersistentId = 200, TerminalStateValue = TerminalState.Landed,
                VesselSnapshot = snapshotGuid != null ? SnapshotOf(snapshotGuid, 200) : null,
            };
        }

        private static LiveVesselEntry LiveEntry(string guid, uint pid, string name)
        {
            return new LiveVesselEntry { Guid = guid, Pid = pid, Name = name };
        }

        // Fails if: the load-time backfill skips the upgraded-save resumed spawn, so the
        // Tracking Station Fly refuses it and the death check / replace gate read it as another
        // launch's stamp.
        [Fact]
        public void Backfill_UpgradedSaveResumedSpawn_WritesResumeLinkFromTheEndSnapshot()
        {
            var resumed = UpgradedSaveResumedRecording(SpawnGuid);
            var recs = new List<Recording> { resumed };
            var vessels = new List<LiveVesselEntry> { LiveEntry(SpawnGuid, 200u, "Probe") };

            Assert.False(ParsekFlight.IsCommittedSpawnStampMatch(resumed, 200u, SpawnGuid));

            VesselNameStore.BackfillResult r = VesselNameStore.BackfillSpawnLinks(recs, vessels, "test");
            Assert.Equal(1, r.ResumeAdded);
            Assert.Equal(0, r.Added);
            Assert.Equal(0, r.AdoptionStampSkipped);
            Assert.True(VesselNameStore.TryGetLink(SpawnGuid, out VesselIdentityLink link));
            Assert.Equal(LaunchOneGuid, link.ToGuid);
            Assert.Equal(VesselIdentityLinkKind.Resume, link.Kind);
            Assert.Contains(logLines, l => l.Contains("[INFO][VesselNames]")
                && l.Contains("Spawn link backfill (test):") && l.Contains("resumeAdded=1"));

            Assert.True(ParsekFlight.IsCommittedSpawnStampMatch(resumed, 200u, SpawnGuid));
            Assert.True(ParsekPlaybackPolicy.IsSpawnClaimHeldByLiveVessel(resumed, 200u, SpawnGuid));
            Assert.False(RecordingStore.IsSpawnStampStaleForReplace(
                resumed, new HashSet<uint> { 200u }, new Dictionary<uint, string> { { 200u, SpawnGuid } }, out _));

            VesselNameStore.BackfillResult again = VesselNameStore.BackfillSpawnLinks(recs, vessels, "test");
            Assert.Equal(0, again.ResumeAdded);
            Assert.Equal(1, again.AlreadyLinked);
            Assert.Equal(1, VesselNameStore.LinkCount);
        }

        // Fails if: the backfill links a fresh relaunch of the same craft to the older launch.
        // The relaunch reuses the baked pid; the older recording's end snapshot names its own
        // launch, so there is no evidence and no link, and the Tracking Station Fly still refuses.
        [Fact]
        public void Backfill_RelaunchOfTheSameCraft_NoLinkAndTheMatcherStillRefuses()
        {
            var adopted = AdoptedLaunchOneRecording();
            Commit(TreeOf(adopted));
            var vessels = new List<LiveVesselEntry> { LiveEntry(LaunchTwoGuid, BakedPid, "Kerbal X") };

            VesselNameStore.BackfillResult r = VesselNameStore.BackfillSpawnLinks(
                new List<Recording> { adopted }, vessels, "test");
            Assert.Equal(0, r.ResumeAdded);
            Assert.Equal(1, r.AdoptionStampSkipped);
            Assert.Equal(0, VesselNameStore.LinkCount);

            Assert.False(ParsekFlight.IsCommittedSpawnStampMatch(adopted, BakedPid, LaunchTwoGuid));
            Assert.False(ParsekFlight.TryTakeCommittedTreeForSpawnedVesselRestore(
                BakedPid, out _, out _, out _, activeVesselGuid: LaunchTwoGuid));
            Assert.False(ParsekPlaybackPolicy.IsSpawnClaimHeldByLiveVessel(adopted, BakedPid, LaunchTwoGuid));
        }

        // Fails if: the backfill writes a link with no snapshot evidence, or the destructive
        // readers drop a claim with none.
        [Fact]
        public void UpgradedSaveResumedSpawn_NoSnapshotGuid_NoLinkButTheClaimIsKept()
        {
            var resumed = UpgradedSaveResumedRecording(null);
            VesselNameStore.BackfillResult r = VesselNameStore.BackfillSpawnLinks(
                new List<Recording> { resumed },
                new List<LiveVesselEntry> { LiveEntry(SpawnGuid, 200u, "Probe") },
                "test", _ => null);
            Assert.Equal(0, r.ResumeAdded);
            Assert.Equal(1, r.AdoptionStampSkipped);
            Assert.Equal(0, VesselNameStore.LinkCount);

            // The matcher only declines a restore, so it stays strict; the death check and the
            // replace gate need positive evidence before declaring the vessel gone.
            Assert.False(ParsekFlight.IsCommittedSpawnStampMatch(resumed, 200u, SpawnGuid));
            Assert.True(ParsekPlaybackPolicy.IsSpawnClaimHeldByLiveVessel(resumed, 200u, SpawnGuid, _ => null));
            Assert.False(RecordingStore.IsSpawnStampStaleForReplace(
                resumed, new HashSet<uint> { 200u }, new Dictionary<uint, string> { { 200u, SpawnGuid } },
                out bool otherLaunch, _ => null));
            Assert.False(otherLaunch);
        }

        [Fact]
        public void SpawnClaimStillHeldCore_TruthTable()
        {
            var rec = AdoptedLaunchOneRecording();
            // Same launch, or unknown live guid: held whatever the snapshot says.
            Assert.True(VesselLaunchIdentity.SpawnClaimStillHeldCore(rec, BakedPid, LaunchOneGuid, false, LaunchTwoGuid));
            Assert.True(VesselLaunchIdentity.SpawnClaimStillHeldCore(rec, BakedPid, null, false, LaunchOneGuid));
            // Other launch: dropped only when the snapshot names a launch that is not the live one.
            Assert.False(VesselLaunchIdentity.SpawnClaimStillHeldCore(rec, BakedPid, LaunchTwoGuid, false, LaunchOneGuid));
            Assert.True(VesselLaunchIdentity.SpawnClaimStillHeldCore(rec, BakedPid, LaunchTwoGuid, false, null));
            Assert.True(VesselLaunchIdentity.SpawnClaimStillHeldCore(rec, BakedPid, LaunchTwoGuid, false, LaunchTwoGuid));
            Assert.True(VesselLaunchIdentity.SpawnClaimStillHeldCore(rec, BakedPid, LaunchTwoGuid, true, LaunchOneGuid));
            // Not the stamp's pid: never held.
            Assert.False(VesselLaunchIdentity.SpawnClaimStillHeldCore(rec, 1u, LaunchOneGuid, true, null));
            // Genuine spawn pid: held whatever the guids.
            rec.SpawnedVesselPersistentId = 999u;
            Assert.True(VesselLaunchIdentity.SpawnClaimStillHeldCore(rec, 999u, LaunchTwoGuid, false, LaunchOneGuid));
        }

        #endregion

        #region Gap 2 - #226 bypass launch check

        private static Recording UnspawnedLaunchOneRecording()
        {
            var rec = AdoptedLaunchOneRecording("rec_unspawned");
            rec.VesselSpawned = false;
            rec.SpawnedVesselPersistentId = 0u;
            return rec;
        }

        [Fact]
        public void ShouldKeepReplayBypass_SameLaunch_KeptWhateverTheUT()
        {
            // A revert / rewind re-flight, and a scene entered after the recording's end: both
            // keep the bypass (an after-end re-flight must not be adopted as the end vessel).
            Assert.True(VesselSpawner.ShouldKeepReplayBypassForLaunchIdentity(
                UnspawnedLaunchOneRecording(), BakedPid, LaunchOneGuid, BakedPid, LaunchOneGuid,
                null, out string reason));
            Assert.Equal(VesselSpawner.ReplayBypassReasonSameLaunch, reason);
        }

        [Fact]
        public void ShouldKeepReplayBypass_DifferentLaunch_Withheld()
        {
            Assert.False(VesselSpawner.ShouldKeepReplayBypassForLaunchIdentity(
                UnspawnedLaunchOneRecording(), BakedPid, LaunchTwoGuid, BakedPid, LaunchTwoGuid,
                null, out string reason));
            Assert.Equal(VesselSpawner.ReplayBypassReasonDifferentLaunch, reason);
        }

        [Fact]
        public void ShouldKeepReplayBypass_LinkAnswerCountsAsSameLaunch()
        {
            Assert.True(VesselSpawner.ShouldKeepReplayBypassForLaunchIdentity(
                UnspawnedLaunchOneRecording(), BakedPid, LaunchTwoGuid, 0u, null,
                (recGuid, liveGuid) => true, out string reason));
            Assert.Equal(VesselSpawner.ReplayBypassReasonSameLaunch, reason);
        }

        [Fact]
        public void ShouldKeepReplayBypass_UnknownContext_KeepsTodaysBypass()
        {
            string reason;
            Assert.True(VesselSpawner.ShouldKeepReplayBypassForLaunchIdentity(
                UnspawnedLaunchOneRecording(), BakedPid, null, 0u, null, null, out reason));
            Assert.True(VesselSpawner.ShouldKeepReplayBypassForLaunchIdentity(
                null, BakedPid, LaunchOneGuid, 0u, null, null, out reason));
            Assert.Equal(VesselSpawner.ReplayBypassReasonPidOnly, reason);
        }

        [Fact]
        public void ShouldKeepReplayBypass_ActiveVesselSameLaunch_KeptWhenSceneEntryIsOtherLaunch()
        {
            Assert.True(VesselSpawner.ShouldKeepReplayBypassForLaunchIdentity(
                UnspawnedLaunchOneRecording(), BakedPid, LaunchTwoGuid, BakedPid, LaunchOneGuid,
                null, out string reason));
            Assert.Equal(VesselSpawner.ReplayBypassReasonSameLaunch, reason);
        }

        // Fails if: the bypass a revert / rewind re-flight needs is withheld, so the committed
        // recording adopts the player's re-flight instead of spawning its end vessel.
        [Fact]
        public void CurrentFlightBypass_SameLaunchReFlight_KeepsBypassAndSkipsAdoption()
        {
            var rec = UnspawnedLaunchOneRecording();
            RecordingStore.SceneEntryActiveVesselPid = BakedPid;
            RecordingStore.SceneEntryActiveVesselGuid = LaunchOneGuid;
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

        // A different launch of the craft: the bypass is withheld (logged), and adoption
        // refuses it on the guid, so the spawn proceeds exactly as it did under the bypass.
        [Fact]
        public void CurrentFlightBypass_DifferentLaunch_WithheldButAdoptionStillRefuses()
        {
            var rec = UnspawnedLaunchOneRecording();
            RecordingStore.SceneEntryActiveVesselPid = BakedPid;
            RecordingStore.SceneEntryActiveVesselGuid = LaunchTwoGuid;
            VesselSpawner.SetMaterializedSourceVesselExistsOverrideForTesting(pid => pid == BakedPid);
            VesselSpawner.SetMaterializedSourceVesselGuidOverrideForTesting(pid => LaunchTwoGuid);

            bool bypass = VesselSpawner.ShouldAllowExistingSourceDuplicateForCurrentFlight(rec);
            bool adopted = VesselSpawner.TryAdoptExistingSourceVesselForSpawn(
                rec, "Spawner", "unit-test", bypass);

            Assert.False(bypass);
            Assert.False(adopted);
            Assert.False(rec.VesselSpawned);
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]")
                && l.Contains("reason=" + VesselSpawner.ReplayBypassReasonDifferentLaunch)
                && l.Contains("#226 bypass withheld"));
        }

        #endregion
    }
}
