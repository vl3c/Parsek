using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// GC-7 Phase 0 (docs/dev/design-vessel-and-mission-names.md section 7.9): every site that
    /// used a vessel NAME as its identity now decides by launch identity (pid + launch guid),
    /// with the name kept only as the fallback for guid-less legacy data. Each site is pinned
    /// in both directions: a RENAMED vessel is still matched by identity, and a SAME-NAMED
    /// vessel of a different launch is not.
    /// </summary>
    [Collection("Sequential")]
    public class VesselNameIdentityPhase0Tests : IDisposable
    {
        private const string GuidA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string GuidB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string GuidC = "cccccccccccccccccccccccccccccccc";

        private readonly List<string> logLines = new List<string>();

        public VesselNameIdentityPhase0Tests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            RecoveryDispatchScope.ResetForTesting();
        }

        public void Dispose()
        {
            RecoveryDispatchScope.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
        }

        // ============================================================
        // ParsekScenario.MatchesVessel / ClassifyVesselMatch
        // ============================================================

        private static Recording MakeRec(string id, string name, uint pid, string guid)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = name,
                VesselPersistentId = pid,
                RecordedVesselGuid = guid,
                TerminalStateValue = TerminalState.Orbiting,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0 });
            return rec;
        }

        private static RecordingTree MakePendingTree(params Recording[] recs)
        {
            var tree = new RecordingTree
            {
                Id = "phase0-tree-" + recs[0].RecordingId,
                TreeName = recs[0].VesselName,
                RootRecordingId = recs[0].RecordingId
            };
            foreach (var r in recs)
            {
                r.TreeId = tree.Id;
                tree.AddOrReplaceRecording(r);
            }
            return tree;
        }

        [Fact]
        public void ClassifyVesselMatch_RenamedVessel_MatchedByLaunchIdentity()
        {
            var rec = MakeRec("r1", "Probe", 4242u, GuidA);
            var renamed = RecoveredVesselIdentity.FromRawName("Relay-2", GuidA, 4242u);

            Assert.Equal(ParsekScenario.VesselMatchKind.LaunchIdentity,
                ParsekScenario.ClassifyVesselMatch(rec, renamed, 4242u));
            Assert.True(ParsekScenario.MatchesVessel(rec, renamed, 4242u));
        }

        [Fact]
        public void ClassifyVesselMatch_SameNameDifferentLaunch_NotMatched()
        {
            // The craft-baked pid is shared by every launch of the craft; the fresh guid is not.
            var rec = MakeRec("r1", "Kerbal X", 4242u, GuidA);
            var relaunch = RecoveredVesselIdentity.FromRawName("Kerbal X", GuidB, 4242u);

            Assert.Equal(ParsekScenario.VesselMatchKind.None,
                ParsekScenario.ClassifyVesselMatch(rec, relaunch, 4242u));
        }

        [Fact]
        public void ClassifyVesselMatch_GuidEqualButPidConclusivelyDiffers_NotMatched()
        {
            var rec = MakeRec("r1", "Kerbal X", 4242u, GuidA);
            var other = RecoveredVesselIdentity.FromRawName("Kerbal X", GuidA, 9999u);

            Assert.False(ParsekScenario.MatchesVessel(rec, other, 9999u));
        }

        [Fact]
        public void ClassifyVesselMatch_GuidlessLegacy_StillMatchedByName()
        {
            var rec = MakeRec("legacy", "Kerbal X", 4242u, guid: null);

            Assert.Equal(ParsekScenario.VesselMatchKind.LegacyName,
                ParsekScenario.ClassifyVesselMatch(
                    rec, RecoveredVesselIdentity.FromRawName("Kerbal X", GuidA, 4242u), 4242u));
            // Unknown live guid also falls back to the name.
            Assert.Equal(ParsekScenario.VesselMatchKind.LegacyName,
                ParsekScenario.ClassifyVesselMatch(
                    MakeRec("modern", "Kerbal X", 4242u, GuidA),
                    RecoveredVesselIdentity.FromRawName("Kerbal X"), 4242u));
            // A legacy record under another name does not match.
            Assert.Equal(ParsekScenario.VesselMatchKind.None,
                ParsekScenario.ClassifyVesselMatch(
                    rec, RecoveredVesselIdentity.FromRawName("Other", GuidA, 4242u), 4242u));
        }

        // catches: the name REQUIRED gate, under which a vessel renamed after its recording
        // captured the name missed its Recovered stamp (and with it the recovery-cost rows).
        [Fact]
        public void UpdateRecordingsForTerminalEvent_RenamedVessel_StampedAndLogged()
        {
            var rec = MakeRec("pending-renamed", "Probe", 4242u, GuidA);
            RecordingStore.StashPendingTree(MakePendingTree(rec));

            bool updated = ParsekScenario.UpdateRecordingsForTerminalEvent(
                RecoveredVesselIdentity.FromRawName("Relay-2", GuidA, 4242u),
                TerminalState.Recovered, 300.0, vesselPid: 4242u);

            Assert.True(updated);
            Assert.Equal(TerminalState.Recovered, rec.TerminalStateValue);
            Assert.Null(rec.VesselSnapshot);
            Assert.Contains(logLines, l => l.Contains("[Scenario]")
                && l.Contains("UpdateRecordingsForTerminalEvent: vessel='Relay-2' pid=4242 state=Recovered")
                && l.Contains("stampedByLaunchIdentity=1"));
        }

        [Fact]
        public void UpdateRecordingsForTerminalEvent_SameNameDifferentLaunch_LeftAlone()
        {
            var rec = MakeRec("pending-other", "Kerbal X", 4242u, GuidA);
            RecordingStore.StashPendingTree(MakePendingTree(rec));

            bool updated = ParsekScenario.UpdateRecordingsForTerminalEvent(
                RecoveredVesselIdentity.FromRawName("Kerbal X", GuidB, 4242u),
                TerminalState.Recovered, 300.0, vesselPid: 4242u);

            Assert.False(updated);
            Assert.Equal(TerminalState.Orbiting, rec.TerminalStateValue);
            Assert.NotNull(rec.VesselSnapshot);
            Assert.DoesNotContain(logLines, l => l.Contains("stampedByLaunchIdentity="));
        }

        [Fact]
        public void UpdateRecordingsForTerminalEvent_SameNameMatch_DoesNotLogRenamedLine()
        {
            var rec = MakeRec("pending-same", "Kerbal X", 4242u, GuidA);
            RecordingStore.StashPendingTree(MakePendingTree(rec));

            Assert.True(ParsekScenario.UpdateRecordingsForTerminalEvent(
                RecoveredVesselIdentity.FromRawName("Kerbal X", GuidA, 4242u),
                TerminalState.Recovered, 300.0, vesselPid: 4242u));
            Assert.DoesNotContain(logLines, l => l.Contains("stampedByLaunchIdentity="));
        }

        [Fact]
        public void ShouldPatchRecoveryFundsOutsideFlight_RenamedPendingOwner_DefersToCommit()
        {
            var rec = MakeRec("pending-funds", "Probe", 4242u, GuidA);
            RecordingStore.StashPendingTree(MakePendingTree(rec));

            // The renamed vessel is still owned by its pending recording: the commit pays it.
            Assert.False(ParsekScenario.ShouldPatchRecoveryFundsOutsideFlight(
                GameScenes.SPACECENTER,
                RecoveredVesselIdentity.FromRawName("Relay-2", GuidA, 4242u), 4242u));
            // A same-named relaunch is not owned by it: patch now.
            Assert.True(ParsekScenario.ShouldPatchRecoveryFundsOutsideFlight(
                GameScenes.SPACECENTER,
                RecoveredVesselIdentity.FromRawName("Probe", GuidB, 4242u), 4242u));
        }

        [Fact]
        public void RecoveryDispatchScope_RenamedPendingVessel_OwnsTheProgressEvent()
        {
            var rec = MakeRec("pending-scope", "Probe", 4242u, GuidA);
            RecordingStore.StashPendingTree(MakePendingTree(rec));

            RecoveryDispatchScope.Open(
                RecoveredVesselIdentity.FromRawName("Relay-2", GuidA, 4242u), 4242u, "test");
            RecoveryEventOwner owner = RecoveryDispatchScope.ResolveOwnerForEvent(
                GameStateEventType.MilestoneAchieved, "FirstCrewToSurvive", 300.0);
            RecoveryDispatchScope.Close("test");

            Assert.Equal(RecoveryEventOwnerKind.PendingRecording, owner.Kind);
            Assert.Equal("pending-scope", owner.RecordingId);
        }

        // ============================================================
        // RewindInvoker.ResolveInPlaceContinuationDebrisToKill
        // ============================================================

        private static ReFlySessionMarker InPlaceMarker() => new ReFlySessionMarker
        {
            SessionId = "sess_phase0",
            ActiveReFlyRecordingId = "rec-active",
            OriginChildRecordingId = "rec-active",
            TreeId = "tree-p0",
            InPlaceContinuation = true,
        };

        private static List<RecordingTree> KillTree(params Recording[] recs)
        {
            var tree = new RecordingTree { Id = "tree-p0", TreeName = "tree-p0" };
            foreach (var r in recs)
            {
                r.TreeId = tree.Id;
                tree.AddOrReplaceRecording(r);
            }
            return new List<RecordingTree> { tree };
        }

        // catches: the name-keyed kill set, under which the quicksave copy of a Destroyed
        // recording's own vessel survived once that vessel had been renamed (a doubled vessel).
        [Fact]
        public void ResolveDebris_RenamedSameLaunchDebris_KilledByIdentity()
        {
            var active = MakeRec("rec-active", "Kerbal X", 200u, GuidC);
            var debris = MakeRec("rec-debris", "Kerbal X Debris", 501u, GuidA);
            debris.TerminalStateValue = TerminalState.Destroyed;

            var kill = RewindInvoker.ResolveInPlaceContinuationDebrisToKill(
                InPlaceMarker(), KillTree(active, debris),
                new List<(uint, string, string)> { (501u, GuidA, "Old Booster Name") },
                protectedPids: null);

            Assert.Equal(new List<uint> { 501u }, kill);
            Assert.Contains(logLines, l => l.Contains("ResolveInPlaceContinuationDebrisToKill")
                && l.Contains("matched 1 pid(s)")
                && l.Contains("byLaunchIdentity=1")
                && l.Contains("byPriorCareerDebrisName=0"));
        }

        [Fact]
        public void ResolveDebris_PriorCareerDebrisNamesake_StillKilledByDebrisNameRule()
        {
            var active = MakeRec("rec-active", "Kerbal X", 200u, GuidC);
            var debris = MakeRec("rec-debris", "Kerbal X Debris", 501u, GuidA);
            debris.TerminalStateValue = TerminalState.Destroyed;

            // Debris of an EARLIER launch of the same craft: other pid, other guid, same name.
            var kill = RewindInvoker.ResolveInPlaceContinuationDebrisToKill(
                InPlaceMarker(), KillTree(active, debris),
                new List<(uint, string, string)> { (777u, GuidB, "Kerbal X Debris") },
                protectedPids: null);

            Assert.Equal(new List<uint> { 777u }, kill);
            Assert.Contains(logLines, l => l.Contains("byLaunchIdentity=0")
                && l.Contains("byPriorCareerDebrisName=1"));
        }

        [Fact]
        public void ResolveDebris_CandidateOfTheActiveLaunch_NeverKilled()
        {
            // A parent-chain recording shares the active vessel's launch; a candidate that is
            // that launch is the re-flown vessel itself even when its pid is not protected.
            var parent = MakeRec("rec-parent", "Kerbal X", 200u, GuidC);
            var active = MakeRec("rec-active", "Kerbal X", 200u, GuidC);
            active.ParentBranchPointId = "bp-1";
            var tree = KillTree(parent, active);
            tree[0].BranchPoints.Add(new BranchPoint
            {
                Id = "bp-1",
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { "rec-parent" },
                ChildRecordingIds = new List<string> { "rec-active" },
            });

            var kill = RewindInvoker.ResolveInPlaceContinuationDebrisToKill(
                InPlaceMarker(), tree,
                new List<(uint, string, string)> { (200u, GuidC, "Renamed Stage") },
                protectedPids: null);

            Assert.Empty(kill);
            Assert.Contains(logLines, l => l.Contains("keptActiveLaunch=1"));
        }

        [Fact]
        public void BuildLeftAlone_CarriesTheVesselLaunchGuid()
        {
            var debris = new StubStrippable
            {
                PersistentId = 501u,
                VesselName = "Kerbal X Debris",
                LaunchGuid = "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA",
                VesselType = VesselType.Debris,
            };
            var result = RewindInvoker.BuildLeftAlonePidNamesForInPlaceContinuation(
                new List<IStrippableVessel> { debris },
                new PostLoadStripResult { LeftAlonePidNames = new List<(uint, string)>() },
                null);

            Assert.Single(result);
            Assert.Equal(501u, result[0].pid);
            Assert.Equal(GuidA, result[0].guid);
            Assert.Equal("Kerbal X Debris", result[0].name);
        }

        private sealed class StubStrippable : IStrippableVessel
        {
            public uint PersistentId { get; set; }
            public uint RootPartPersistentId { get; set; }
            public string VesselName { get; set; }
            public string LaunchGuid { get; set; }
            public VesselType VesselType { get; set; } = VesselType.Ship;
            public Vessel LiveVessel => null;
            public void Die() { }
        }

        // ============================================================
        // VesselLaunchIdentity.RecordingsShareVesselIdentity +
        // ParsekFlight.IsTimelineSpawnOfLeafVessel (RecoverTimelineSpawnedVessel)
        // ============================================================

        [Fact]
        public void RecordingsShareVesselIdentity_TruthTable()
        {
            Assert.True(VesselLaunchIdentity.RecordingsShareVesselIdentity(
                MakeRec("a", "Probe", 1u, GuidA), MakeRec("b", "Relay-2", 2u, GuidA)));
            Assert.False(VesselLaunchIdentity.RecordingsShareVesselIdentity(
                MakeRec("a", "Kerbal X", 1u, GuidA), MakeRec("b", "Kerbal X", 1u, GuidB)));
            Assert.True(VesselLaunchIdentity.RecordingsShareVesselIdentity(
                MakeRec("a", "Kerbal X", 1u, null), MakeRec("b", "Kerbal X", 1u, GuidB)));
            Assert.False(VesselLaunchIdentity.RecordingsShareVesselIdentity(
                MakeRec("a", "Kerbal X", 1u, null), MakeRec("b", "Other", 1u, null)));
            Assert.False(VesselLaunchIdentity.RecordingsShareVesselIdentity(null, MakeRec("b", "X", 1u, GuidA)));
        }

        [Fact]
        public void IsTimelineSpawnOfLeafVessel_RenamedSameVessel_SelectedForRecovery()
        {
            var older = MakeRec("older", "Probe", 1u, GuidA);
            older.VesselSpawned = true;
            older.SpawnedVesselPersistentId = 90001u;
            var leaf = MakeRec("leaf", "Relay-2", 1u, GuidA);

            Assert.True(ParsekFlight.IsTimelineSpawnOfLeafVessel(older, leaf));
        }

        // catches: the name selector, under which a tree leaf's spawn recovered (and reset)
        // ANOTHER launch's spawned vessel that merely carried the same craft name.
        [Fact]
        public void IsTimelineSpawnOfLeafVessel_SameNameOtherLaunch_LeftAlone()
        {
            var older = MakeRec("older", "Kerbal X", 1u, GuidA);
            older.VesselSpawned = true;
            older.SpawnedVesselPersistentId = 90001u;
            var leaf = MakeRec("leaf", "Kerbal X", 1u, GuidB);

            Assert.False(ParsekFlight.IsTimelineSpawnOfLeafVessel(older, leaf));
        }

        [Fact]
        public void IsTimelineSpawnOfLeafVessel_GuidlessLegacy_MatchedByName_AndNeedsALiveSpawn()
        {
            var older = MakeRec("older", "Kerbal X", 1u, null);
            older.VesselSpawned = true;
            older.SpawnedVesselPersistentId = 90001u;
            var leaf = MakeRec("leaf", "Kerbal X", 1u, GuidB);
            Assert.True(ParsekFlight.IsTimelineSpawnOfLeafVessel(older, leaf));

            older.SpawnedVesselPersistentId = 0u;
            Assert.False(ParsekFlight.IsTimelineSpawnOfLeafVessel(older, leaf));
        }

        // ============================================================
        // RecordingStore.IsSameVesselForContinuationRepair (SupersedeTerminalSpawn)
        // ============================================================

        [Fact]
        public void ContinuationRepair_SameNameDifferentLaunch_NotSuperseded()
        {
            Assert.False(RecordingStore.IsSameVesselForContinuationRepair(
                MakeRec("prior", "Rover", 1u, GuidA), MakeRec("cont", "Rover", 2u, GuidB),
                out string reason));
            Assert.Null(reason);
        }

        [Fact]
        public void ContinuationRepair_RenamedSameLaunch_Superseded()
        {
            Assert.True(RecordingStore.IsSameVesselForContinuationRepair(
                MakeRec("prior", "Rover", 1u, GuidA), MakeRec("cont", "Base Rover", 2u, GuidA),
                out string reason));
            Assert.Equal("same-launch-overlapping-continuation-tree", reason);
        }

        [Fact]
        public void ContinuationRepair_GuidlessLegacy_StillMatchedByName()
        {
            Assert.True(RecordingStore.IsSameVesselForContinuationRepair(
                MakeRec("prior", "Rover", 1u, null), MakeRec("cont", "Rover", 2u, GuidB),
                out string reason));
            Assert.Equal("same-name-overlapping-continuation-tree", reason);
            Assert.False(RecordingStore.IsSameVesselForContinuationRepair(
                MakeRec("prior", "Rover", 1u, null), MakeRec("cont", "Other", 2u, null),
                out _));
        }

        // ============================================================
        // QuickloadResumeMatchGuard.MayMatchByNameFallback (quickload restore)
        // ============================================================

        [Fact]
        public void MayMatchByNameFallback_OnlyForIdentityLessRecordings()
        {
            Assert.True(QuickloadResumeMatchGuard.MayMatchByNameFallback(0u, null));
            Assert.True(QuickloadResumeMatchGuard.MayMatchByNameFallback(0u, ""));
            Assert.False(QuickloadResumeMatchGuard.MayMatchByNameFallback(4242u, null));
            Assert.False(QuickloadResumeMatchGuard.MayMatchByNameFallback(0u, GuidA));
            Assert.False(QuickloadResumeMatchGuard.MayMatchByNameFallback(4242u, GuidA));
        }

        // ============================================================
        // GhostPlaybackLogic.IsSameWatchedVessel (watch-camera handoff)
        // ============================================================

        [Fact]
        public void IsSameWatchedVessel_IdentityFirst_NameOnlyForUnknownGuids()
        {
            // Renamed leg of the watched vessel: same guid -> same vessel.
            Assert.True(GhostPlaybackLogic.IsSameWatchedVessel(GuidA, "Kerbal X", GuidA, "P1"));
            // Same-named leg of another vessel: different guid -> not the watched vessel.
            Assert.False(GhostPlaybackLogic.IsSameWatchedVessel(GuidA, "Kerbal X", GuidB, "Kerbal X"));
            // Legacy (a guid unknown): the name decides.
            Assert.True(GhostPlaybackLogic.IsSameWatchedVessel(null, "Kerbal X", GuidB, "Kerbal X"));
            Assert.False(GhostPlaybackLogic.IsSameWatchedVessel(GuidA, "Kerbal X", null, "Other"));
            // Nothing watched never matches.
            Assert.False(GhostPlaybackLogic.IsSameWatchedVessel(null, null, null, null));
        }

        [Fact]
        public void PlaybackTrajectory_ExposesRecordedVesselGuid()
        {
            IPlaybackTrajectory traj = MakeRec("t", "Kerbal X", 1u, GuidA);
            Assert.Equal(GuidA, traj.RecordedVesselGuid);
        }

        // ============================================================
        // RecordingStore.RewindOwnerStrip (unchanged in Phase 0): why its name fallback stays
        // ============================================================

        // Settles design 7.9's open check: the PID strip does NOT cover a re-keyed owner copy
        // once SpawnedVesselPersistentId was reset (the resume path clears it and keeps the
        // old guid), so the unanchored name fallback is the only thing that strips that copy.
        [Fact]
        public void RewindOwnerStrip_ReKeyedOwnerCopy_OnlyTheUnanchoredNameFallbackStripsIt()
        {
            // A resumed spawned vessel's recording: pid promoted to the spawn pid, original
            // launch guid kept, spawn tracking cleared.
            var owner = MakeRec("owner", "Relay", 90001u, GuidA);
            owner.VesselSpawned = false;
            owner.SpawnedVesselPersistentId = 0u;

            var scope = RecordingStore.ResolveRewindSpawnStripScope(
                new List<Recording> { owner }, double.NaN);
            Assert.DoesNotContain(90001u, scope.StripPids);

            var strip = RecordingStore.BuildRewindOwnerStrip(owner);
            // The quicksave holds only the re-keyed copy (fresh spawn guid): no vessel carries
            // the owner guid, so the strip is unanchored and the name decides.
            bool anchored = RecordingStore.RewindOwnerStripIsAnchored(strip, new[] { GuidB });
            Assert.False(anchored);
            Assert.Equal(RecordingStore.RewindOwnerStripDecision.StripName,
                RecordingStore.ClassifyRewindOwnerStripVessel("Relay", GuidB, strip, anchored));
            // Neither the name nor the guid reaches a copy under another name: the gap Phase
            // 2b's Spawn / Resume links close (matching the identity's full guid set).
            Assert.Equal(RecordingStore.RewindOwnerStripDecision.NotMatched,
                RecordingStore.ClassifyRewindOwnerStripVessel("Relay-2", GuidB, strip, anchored));
        }

        // ============================================================
        // Capture refresh at a leg's close (design 7.4 last paragraph)
        // ============================================================

        [Fact]
        public void ResolveRecordedVesselNameAtClose_PositiveMatch_TakesRecordedVesselName()
        {
            string name = ParsekFlight.ResolveRecordedVesselNameAtClose(
                "Kerbal X", 4242u, GuidA, 4242u, GuidA, "P1", out string source);
            Assert.Equal("P1", name);
            Assert.Equal("recorded-vessel", source);
        }

        [Fact]
        public void ResolveRecordedVesselNameAtClose_NoPositiveMatch_KeepsExistingName()
        {
            // Recorded vessel gone (dock-merge stop: the absorbed vessel no longer exists).
            Assert.Equal("Kerbal X", ParsekFlight.ResolveRecordedVesselNameAtClose(
                "Kerbal X", 4242u, GuidA, 0u, null, null, out string s1));
            Assert.Equal("kept-existing", s1);
            // A different launch reusing the baked pid.
            Assert.Equal("Kerbal X", ParsekFlight.ResolveRecordedVesselNameAtClose(
                "Kerbal X", 4242u, GuidA, 4242u, GuidB, "Interloper", out _));
            // Unknown recorded guid: not a positive match.
            Assert.Equal("Kerbal X", ParsekFlight.ResolveRecordedVesselNameAtClose(
                "Kerbal X", 4242u, null, 4242u, GuidA, "P1", out _));
            // Nothing to keep and no match: the caller decides.
            Assert.Null(ParsekFlight.ResolveRecordedVesselNameAtClose(
                null, 4242u, GuidA, 0u, null, null, out string s2));
            Assert.Equal("none", s2);
        }

        // catches: the stop-time capture (whose VesselName is the ACTIVE vessel's name, which
        // after a split may already be the new pod) relabelling the closing tree leg.
        [Fact]
        public void AppendCapturedDataToRecording_SplitOrDockStop_KeepsTheLegName()
        {
            var leg = new Recording { RecordingId = "leg", VesselName = "Kerbal X" };
            var capture = new Recording { RecordingId = "cap", VesselName = "Kerbal X Probe" };
            capture.Points.Add(new TrajectoryPoint { ut = 10.0 });

            ParsekFlight.AppendCapturedDataToRecording(leg, capture, 10.0);

            Assert.Equal("Kerbal X", leg.VesselName);
        }

        [Fact]
        public void ResolveParentContinuationName_PrefersTheLiveParentVessel()
        {
            Assert.Equal("Mission1 Debris",
                BackgroundRecorder.ResolveParentContinuationName("Mission1 Debris", "Mission1"));
            Assert.Equal("Mission1",
                BackgroundRecorder.ResolveParentContinuationName(null, "Mission1"));
            Assert.Equal("Mission1",
                BackgroundRecorder.ResolveParentContinuationName("", "Mission1"));
        }

        [Fact]
        public void TryBuildAndAttachParentContinuation_CapturesTheLiveParentName()
        {
            var tree = new RecordingTree { Id = "bg-tree", TreeName = "Mission1" };
            var parent = MakeRec("bg-parent", "Mission1", 100u, GuidA);
            parent.TreeId = tree.Id;
            tree.AddOrReplaceRecording(parent);
            var bp = new BranchPoint
            {
                Id = "bp-bg",
                ParentRecordingIds = new List<string> { parent.RecordingId },
                ChildRecordingIds = new List<string>(),
            };

            Recording cont = BackgroundRecorder.TryBuildAndAttachParentContinuation(
                tree, bp, parent, 100u, 150.0,
                parentVesselAlive: true,
                continuationRecordingId: "bg-cont",
                liveParentGuid: GuidA,
                controllers: null,
                liveParentName: "Mission1 Debris");

            Assert.NotNull(cont);
            Assert.Equal("Mission1 Debris", cont.VesselName);
            Assert.Equal("Mission1", parent.VesselName);
        }
    }
}
