using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// CHAIN-TIP-RECOVER-AFTER-SWITCH-RESPAWNS-DUPLICATE: the LoadScene-prefix auto-discard
    /// fast paths (no-op switch segment, no-op no-session resume, idle-on-pad) run before the
    /// scene-exit finalize that applies an in-flight Recover. When the live tree is a
    /// committed-restore clone holding the recovered launch's tip, they must keep it, so the
    /// finalize stamps the tip Recovered and the commit stores it; otherwise the committed tip
    /// keeps Landed and the next flight scene spawns the recovered vessel again.
    /// </summary>
    [Collection("Sequential")]
    public class RecoveryKeepsResumedCloneTests : IDisposable
    {
        private const string Name = "CTR Lander";
        private const uint Pid = 1344998135u;
        private const string LaunchGuid = "37dea4d8a66b38defea7de3991a9de3a";

        private readonly List<string> logLines = new List<string>();

        public RecoveryKeepsResumedCloneTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            RecordingStore.ClearCommittedTreeRestoreAttempt("test-setup");
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            RecoveredRecordingEvidence.ResetForTesting();
            InFlightRecoveryRequest.ResetForTesting();
            GhostMapPresence.ResetForTesting();
        }

        public void Dispose()
        {
            InFlightRecoveryRequest.ResetForTesting();
            RecoveredRecordingEvidence.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            Ledger.ResetForTesting();
            GhostMapPresence.ResetForTesting();
            GameStateStore.ResetForTesting();
            RecordingStore.ClearCommittedTreeRestoreAttempt("test-teardown");
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static ConfigNode LandedSnapshot()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "LANDED");
            snapshot.AddValue("type", "Lander");
            snapshot.AddValue("name", Name);
            return snapshot;
        }

        private static Recording Tip(string id, TerminalState? terminal)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = Name,
                VesselPersistentId = Pid,
                RecordedVesselGuid = LaunchGuid,
                ExplicitStartUT = 22.0,
                ExplicitEndUT = 624.2,
                VesselSnapshot = LandedSnapshot(),
                TerminalStateValue = terminal,
                PlaybackEnabled = true,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 22.0, bodyName = "Kerbin", latitude = -0.09, longitude = -74.55, altitude = 70 },
                    new TrajectoryPoint { ut = 624.2, bodyName = "Kerbin", latitude = -0.09, longitude = -74.55, altitude = 70 },
                },
            };
        }

        private static RecordingTree TreeOf(params Recording[] recs)
        {
            var tree = new RecordingTree
            {
                Id = "tree-ctr",
                TreeName = "CTR Carrier",
                RootRecordingId = recs[0].RecordingId,
            };
            foreach (var r in recs)
            {
                r.TreeId = tree.Id;
                tree.AddOrReplaceRecording(r);
            }
            return tree;
        }

        // catches: the no-op discard reverting the resumed clone before the finalize applies
        // the recovery (the CI-6 reading run's step 3), for each of the three fast paths.
        [Theory]
        [InlineData("noop-switch-segment")]
        [InlineData("noop-no-session-resume")]
        [InlineData("idle-on-pad")]
        public void ArmedRecovery_OfTheClonesTip_KeepsTheClone(string fastPath)
        {
            var tip = Tip("ctr-tip-rec", TerminalState.Landed);
            var tree = TreeOf(tip);
            RecordingStore.ArmCommittedTreeRestoreAttempt(tree, "test-resume");
            InFlightRecoveryRequest.Arm(Pid, LaunchGuid, Name, 626.7);

            Assert.True(SceneExitInterceptor.PendingRecoveryKeepsLiveTree(
                tree, GameScenes.SPACECENTER, fastPath));

            // The guard reads the request; the finalize consumes it.
            Assert.NotNull(InFlightRecoveryRequest.Armed);
            Assert.Contains(logLines, l =>
                l.Contains("[Recovery]")
                && l.Contains("Auto-discard refused: fastPath=" + fastPath)
                && l.Contains("pid=" + Pid)
                && l.Contains("tree='CTR Carrier' (id=tree-ctr)"));
        }

        // catches: the kept clone still committing the tip Landed (the stored truth must be
        // Recovered, so the committed tip neither spawns nor reads as a live vessel).
        [Theory]
        [InlineData(TerminalState.Landed)]
        [InlineData(null)]
        public void KeptClone_FinalizeStampsTheTipRecovered_AndItNeverSpawns(TerminalState? resumedTerminal)
        {
            var tip = Tip("ctr-tip-rec", resumedTerminal);
            var tree = TreeOf(tip);
            RecordingStore.ArmCommittedTreeRestoreAttempt(tree, "test-resume");
            InFlightRecoveryRequest.Arm(Pid, LaunchGuid, Name, 626.7);

            Assert.True(SceneExitInterceptor.PendingRecoveryKeepsLiveTree(
                tree, GameScenes.SPACECENTER, "noop-no-session-resume"));
            int stamped = InFlightRecoveryRequest.ApplyAtSceneExit(
                tree, GameScenes.SPACECENTER, "scene-exit autoMerge-on");

            Assert.Equal(1, stamped);
            Assert.Equal(TerminalState.Recovered, tip.TerminalStateValue);
            Assert.Null(InFlightRecoveryRequest.Armed);

            RecordingStore.AddRecordingWithTreeForTesting(tip);
            var spawn = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(tip, false);
            Assert.False(spawn.needsSpawn);
        }

        // catches: a fresh (never committed) tree being held: discarding it loses nothing
        // committed, so idle-on-pad recover-from-the-pad keeps its silent discard.
        [Fact]
        public void ArmedRecovery_FreshTreeNotAClone_DoesNotHold()
        {
            var tip = Tip("fresh-rec", null);
            var tree = TreeOf(tip);
            InFlightRecoveryRequest.Arm(Pid, LaunchGuid, Name, 626.7);

            Assert.False(SceneExitInterceptor.PendingRecoveryKeepsLiveTree(
                tree, GameScenes.SPACECENTER, "idle-on-pad"));
            Assert.Contains(logLines, l =>
                l.Contains("[Recovery]") && l.Contains("is not a committed-restore clone"));
        }

        // catches: a pid-only match holding a clone for a different launch: with no live
        // recorder binding, a conclusively different launch guid is not this vessel.
        [Fact]
        public void ArmedRecovery_OfAnotherLaunch_Unbound_DoesNotHold()
        {
            var tip = Tip("ctr-tip-rec", TerminalState.Landed);
            var tree = TreeOf(tip);
            RecordingStore.ArmCommittedTreeRestoreAttempt(tree, "test-resume");
            InFlightRecoveryRequest.Arm(Pid, "fedcba9876543210fedcba9876543210", Name, 626.7);

            Assert.False(SceneExitInterceptor.PendingRecoveryKeepsLiveTree(
                tree, GameScenes.SPACECENTER, "noop-switch-segment"));
            Assert.Contains(logLines, l =>
                l.Contains("[Recovery]") && l.Contains("records no tip of vessel='CTR Lander'"));
        }

        // catches: the end-of-recording spawn (identity regenerated: fresh pid AND fresh
        // guid) being lost. The resume moves the live pid onto the tip but the tip keeps the
        // original launch guid, so only the live recorder binding captured when the request
        // armed identifies it; the clone must be kept AND the tip stamped Recovered.
        [Fact]
        public void ArmedRecovery_RegeneratedIdentitySpawn_BoundByLiveRecorder_KeepsAndStamps()
        {
            const uint spawnPid = 3842087101u;
            const string spawnGuid = "aaaabbbbccccddddeeeeffff00001111";
            var tip = Tip("ctr-tip-rec", TerminalState.Landed);
            tip.VesselPersistentId = spawnPid;   // promoted by the resume
            var tree = TreeOf(tip);
            RecordingStore.ArmCommittedTreeRestoreAttempt(tree, "test-resume");
            InFlightRecoveryRequest.Arm(spawnPid, spawnGuid, Name, 626.7, "ctr-tip-rec");

            Assert.Contains(logLines, l =>
                l.Contains("[Recovery]") && l.Contains("In-flight recovery requested")
                && l.Contains("boundRec=ctr-tip-rec"));
            Assert.True(SceneExitInterceptor.PendingRecoveryKeepsLiveTree(
                tree, GameScenes.SPACECENTER, "noop-no-session-resume"));
            Assert.Equal(1, InFlightRecoveryRequest.ApplyAtSceneExit(
                tree, GameScenes.SPACECENTER, "scene-exit autoMerge-on"));
            Assert.Equal(TerminalState.Recovered, tip.TerminalStateValue);
        }

        // catches: a bound recording id stamping a recording that does not carry the
        // recovered vessel's pid (a stale or mismatched binding).
        [Fact]
        public void ArmedRecovery_BoundRecordingWithOtherPid_DoesNotHold()
        {
            var tip = Tip("ctr-tip-rec", TerminalState.Landed);
            var tree = TreeOf(tip);
            RecordingStore.ArmCommittedTreeRestoreAttempt(tree, "test-resume");
            InFlightRecoveryRequest.Arm(
                3842087101u, "aaaabbbbccccddddeeeeffff00001111", Name, 626.7, "ctr-tip-rec");

            Assert.False(SceneExitInterceptor.PendingRecoveryKeepsLiveTree(
                tree, GameScenes.SPACECENTER, "noop-no-session-resume"));
            Assert.Equal(0, InFlightRecoveryRequest.ApplyAtSceneExit(
                tree, GameScenes.SPACECENTER, "scene-exit autoMerge-on"));
            Assert.Equal(TerminalState.Landed, tip.TerminalStateValue);
        }

        // catches: a recovery request held against a scene change stock recovery never makes.
        [Theory]
        [InlineData(GameScenes.FLIGHT)]
        [InlineData(GameScenes.TRACKSTATION)]
        [InlineData(GameScenes.MAINMENU)]
        public void ArmedRecovery_NonSpaceCenterDestination_DoesNotHold(GameScenes destination)
        {
            var tree = TreeOf(Tip("ctr-tip-rec", TerminalState.Landed));
            RecordingStore.ArmCommittedTreeRestoreAttempt(tree, "test-resume");
            InFlightRecoveryRequest.Arm(Pid, LaunchGuid, Name, 626.7);

            Assert.False(SceneExitInterceptor.PendingRecoveryKeepsLiveTree(
                tree, destination, "noop-no-session-resume"));
        }

        // catches: the guard holding every no-op resume (no recovery armed) or crashing on
        // a missing tree.
        [Fact]
        public void NoArmedRecovery_OrNoTree_DoesNotHold()
        {
            var tree = TreeOf(Tip("ctr-tip-rec", TerminalState.Landed));
            RecordingStore.ArmCommittedTreeRestoreAttempt(tree, "test-resume");

            Assert.False(SceneExitInterceptor.PendingRecoveryKeepsLiveTree(
                tree, GameScenes.SPACECENTER, "noop-no-session-resume"));

            InFlightRecoveryRequest.Arm(Pid, LaunchGuid, Name, 626.7);
            Assert.False(SceneExitInterceptor.PendingRecoveryKeepsLiveTree(
                null, GameScenes.SPACECENTER, "noop-no-session-resume"));
            Assert.DoesNotContain(logLines, l => l.Contains("Auto-discard refused"));
        }

        // catches: a tip whose terminal is final (Destroyed) holding the clone: the finalize
        // could not stamp it, so keeping the clone would buy nothing.
        [Fact]
        public void ArmedRecovery_TipAlreadyDestroyed_DoesNotHold()
        {
            var tip = Tip("ctr-tip-rec", TerminalState.Destroyed);
            var tree = TreeOf(tip);
            RecordingStore.ArmCommittedTreeRestoreAttempt(tree, "test-resume");
            InFlightRecoveryRequest.Arm(Pid, LaunchGuid, Name, 626.7);

            Assert.False(SceneExitInterceptor.PendingRecoveryKeepsLiveTree(
                tree, GameScenes.SPACECENTER, "noop-no-session-resume"));
        }

        // catches: the commit of the kept clone re-installing the committed original's spawn
        // claim onto the Recovered tip (CI-6 `2026-10-03_1154`: `preservedRuntimeFields=2`, then
        // `Spawn-death detected` on the next flight). Stock recovers the vessel 8 frames after
        // the Space Center commit, so the pid is still live and the stale-stamp guard passes.
        [Theory]
        [InlineData(TerminalState.Recovered)]
        [InlineData(TerminalState.Destroyed)]
        [InlineData(TerminalState.Disassembled)]
        public void Replace_VessellessTerminal_DoesNotInheritSpawnClaim(TerminalState terminal)
        {
            var existing = Tip("ctr-tip-rec", TerminalState.Landed);
            existing.VesselSpawned = true;
            existing.SpawnedVesselPersistentId = Pid;
            var incoming = Tip("ctr-tip-rec", terminal);

            RecordingStore.PreserveLiveRuntimeFieldsOnReplace(
                existing, incoming, out _, out int otherPreserved,
                new HashSet<uint> { Pid });

            Assert.False(incoming.VesselSpawned);
            Assert.Equal(0u, incoming.SpawnedVesselPersistentId);
            Assert.Equal(0, otherPreserved);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]")
                && l.Contains("spawn stamp pid=" + Pid + " not re-installed")
                && l.Contains("its terminal " + terminal + " leaves no vessel"));
        }

        // catches: the guard over-reaching: a replacement whose vessel still exists keeps
        // the live spawn claim it lost in the pending tree (#264).
        [Fact]
        public void Replace_LandedTerminal_KeepsLiveSpawnClaim()
        {
            var existing = Tip("ctr-tip-rec", TerminalState.Landed);
            existing.VesselSpawned = true;
            existing.SpawnedVesselPersistentId = Pid;
            var incoming = Tip("ctr-tip-rec", TerminalState.Landed);

            RecordingStore.PreserveLiveRuntimeFieldsOnReplace(
                existing, incoming, out _, out int otherPreserved,
                new HashSet<uint> { Pid });

            Assert.True(incoming.VesselSpawned);
            Assert.Equal(Pid, incoming.SpawnedVesselPersistentId);
            Assert.Equal(2, otherPreserved);
        }
    }
}
