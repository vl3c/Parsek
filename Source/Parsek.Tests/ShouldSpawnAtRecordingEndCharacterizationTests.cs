using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization of <see cref="GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(Recording, bool, RecordingTree, bool)"/>:
    /// every rejection reason string byte for byte, and the precedence order between them. The peel test
    /// starts from a recording that trips every gate and clears one gate at a time, so a reordering of
    /// two checks (or a reason string that changes) reds here.
    /// </summary>
    [Collection("Sequential")]
    public class ShouldSpawnAtRecordingEndCharacterizationTests : IDisposable
    {
        private const string TreeId = "char-tree";
        private const uint Pid = 4242u;

        public ShouldSpawnAtRecordingEndCharacterizationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            EffectiveState.ResetRecoveredIndexForTesting();
        }

        public void Dispose()
        {
            EffectiveState.ResetRecoveredIndexForTesting();
            EffectiveState.ResetCachesForTesting();
            Ledger.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static ConfigNode Snapshot(string sit)
        {
            var node = new ConfigNode("VESSEL");
            if (sit != null)
                node.AddValue("sit", sit);
            node.AddValue("type", "Ship");
            return node;
        }

        /// <summary>A recording every gate admits: standalone, landed terminal, LANDED snapshot.</summary>
        private static Recording Spawnable(string id = "rec-ok")
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "Char Craft",
                VesselPersistentId = Pid,
                VesselSnapshot = Snapshot("LANDED"),
                TerminalStateValue = TerminalState.Landed,
            };
        }

        private static (bool, string) Decide(
            Recording rec, bool activeChain = false, RecordingTree tree = null, bool liveSameLaunch = true)
        {
            return GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(rec, activeChain, tree, liveSameLaunch);
        }

        private static RecordingTree TreeWithBranch(Recording parent, Recording child, BranchPointType type)
        {
            var tree = new RecordingTree { Id = TreeId, TreeName = "CharTree", RootRecordingId = parent.RecordingId };
            var bp = new BranchPoint { Id = "bp-char", Type = type, UT = 50.0 };
            bp.ParentRecordingIds.Add(parent.RecordingId);
            bp.ChildRecordingIds.Add(child.RecordingId);
            tree.Recordings[parent.RecordingId] = parent;
            tree.Recordings[child.RecordingId] = child;
            tree.BranchPoints.Add(bp);
            return tree;
        }

        [Fact]
        public void Spawnable_Recording_IsAllowedWithEmptyReason()
        {
            Assert.Equal((true, ""), Decide(Spawnable()));
        }

        [Fact]
        public void TwoArgOverload_MatchesFourArgDefaults()
        {
            var rec = Spawnable();
            rec.ChainBranch = 1;
            Assert.Equal(Decide(rec), GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(rec, false));
            Assert.Equal(Decide(Spawnable()), GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(Spawnable(), false));
        }

        [Fact]
        public void Superseded_ReasonNamesTheSuccessor()
        {
            var rec = Spawnable();
            rec.TerminalSpawnSupersededByRecordingId = "rec-next";
            Assert.Equal((false, "terminal spawn superseded by recording rec-next"), Decide(rec));
        }

        [Fact]
        public void RewindSuppression_ChainTargetBlocks_StandaloneLiftsOnlyWithoutLiveVessel()
        {
            const string blocked = "spawn suppressed post-rewind (same-recording active/source protection, #573)";

            var chained = Spawnable();
            chained.ChainId = "chain-1";
            chained.SpawnSuppressedByRewind = true;
            chained.SpawnSuppressedByRewindReason = ParsekScenario.RewindSpawnSuppressionReasonSameRecording;
            Assert.Equal((false, blocked), Decide(chained, liveSameLaunch: false));

            var standalone = Spawnable();
            standalone.SpawnSuppressedByRewind = true;
            standalone.SpawnSuppressedByRewindReason = ParsekScenario.RewindSpawnSuppressionReasonSameRecording;
            Assert.Equal((false, blocked), Decide(standalone, liveSameLaunch: true));
            Assert.Equal((true, ""), Decide(standalone, liveSameLaunch: false));

            var otherReason = Spawnable();
            otherReason.SpawnSuppressedByRewind = true;
            otherReason.SpawnSuppressedByRewindReason = "something-else";
            Assert.Equal((true, ""), Decide(otherReason));
        }

        [Fact]
        public void NoSnapshot_NotWorthHydrating_RejectsWithoutDiskProbe()
        {
            var debris = Spawnable();
            debris.IsDebris = true;
            debris.VesselSnapshot = null;
            Assert.Equal((false, "no vessel snapshot"), Decide(debris));

            var noTerminal = Spawnable();
            noTerminal.TerminalStateValue = null;
            noTerminal.VesselSnapshot = null;
            Assert.Equal((false, "no vessel snapshot"), Decide(noTerminal));

            var destroyedTerminal = Spawnable();
            destroyedTerminal.TerminalStateValue = TerminalState.Destroyed;
            destroyedTerminal.VesselSnapshot = null;
            Assert.Equal((false, "no vessel snapshot"), Decide(destroyedTerminal));
        }

        [Fact]
        public void ChildBranchPoint_WithoutSamePidChild_IsEffectiveLeafAndSpawns()
        {
            var parent = Spawnable("rec-parent");
            parent.TreeId = TreeId;
            parent.ChildBranchPointId = "bp-char";
            var child = Spawnable("rec-child");
            child.TreeId = TreeId;
            child.VesselPersistentId = Pid + 1;
            var tree = TreeWithBranch(parent, child, BranchPointType.Breakup);

            Assert.Equal((true, ""), Decide(parent, tree: tree));
        }

        [Fact]
        public void ChildBranchPoint_WithSamePidChild_IsNonLeaf()
        {
            var parent = Spawnable("rec-parent");
            parent.TreeId = TreeId;
            parent.ChildBranchPointId = "bp-char";
            var child = Spawnable("rec-child");
            child.TreeId = TreeId;
            var tree = TreeWithBranch(parent, child, BranchPointType.Breakup);

            Assert.Equal((false, "non-leaf tree recording"), Decide(parent, tree: tree));
        }

        [Fact]
        public void ChildBranchPoint_EffectiveLeafNeedsSpawnableTerminalAndNonDebris()
        {
            var parent = Spawnable("rec-parent");
            parent.TreeId = TreeId;
            parent.ChildBranchPointId = "bp-char";
            parent.TerminalStateValue = null;
            var child = Spawnable("rec-child");
            child.TreeId = TreeId;
            child.VesselPersistentId = Pid + 1;
            var tree = TreeWithBranch(parent, child, BranchPointType.Breakup);
            Assert.Equal((false, "non-leaf tree recording"), Decide(parent, tree: tree));

            parent.TerminalStateValue = TerminalState.Landed;
            parent.IsDebris = true;
            Assert.Equal((false, "non-leaf tree recording"), Decide(parent, tree: tree));
        }

        [Fact]
        public void SafetyNet_ParentOfBranchPointWithNullChildId_IsNonLeaf()
        {
            var parent = Spawnable("rec-parent");
            parent.TreeId = TreeId;
            var child = Spawnable("rec-child");
            child.TreeId = TreeId;
            var tree = TreeWithBranch(parent, child, BranchPointType.Breakup);

            Assert.Equal((false, "non-leaf in tree (safety net)"), Decide(parent, tree: tree));
        }

        [Fact]
        public void TerminalAndSituationGates()
        {
            var destroyed = Spawnable();
            destroyed.TerminalStateValue = TerminalState.Docked;
            Assert.Equal((false, "terminal state Docked"), Decide(destroyed));

            var subOrbital = Spawnable();
            subOrbital.TerminalStateValue = TerminalState.SubOrbital;
            Assert.Equal((false, "terminal state SubOrbital"), Decide(subOrbital));

            // A spawnable terminal overrides an unsafe snapshot situation.
            var orbitingFlyingSnap = Spawnable();
            orbitingFlyingSnap.TerminalStateValue = TerminalState.Orbiting;
            orbitingFlyingSnap.VesselSnapshot = Snapshot("FLYING");
            Assert.Equal((true, ""), Decide(orbitingFlyingSnap));

            var unfinalizedFlying = Spawnable();
            unfinalizedFlying.TerminalStateValue = null;
            unfinalizedFlying.VesselSnapshot = Snapshot("SUB_ORBITAL");
            Assert.Equal((false, "snapshot situation unsafe (FLYING/SUB_ORBITAL)"), Decide(unfinalizedFlying));

            var unfinalizedEscaping = Spawnable();
            unfinalizedEscaping.TerminalStateValue = null;
            unfinalizedEscaping.VesselSnapshot = Snapshot("ESCAPING");
            Assert.Equal((false, "unfinalized recording, snapshot situation maps to terminal SubOrbital"),
                Decide(unfinalizedEscaping));

            var unfinalizedDocked = Spawnable();
            unfinalizedDocked.TerminalStateValue = null;
            unfinalizedDocked.VesselSnapshot = Snapshot("DOCKED");
            Assert.Equal((false, "unfinalized recording, snapshot situation maps to terminal Docked"),
                Decide(unfinalizedDocked));

            var unfinalizedLanded = Spawnable();
            unfinalizedLanded.TerminalStateValue = null;
            unfinalizedLanded.VesselSnapshot = Snapshot("LANDED");
            Assert.Equal((true, ""), Decide(unfinalizedLanded));

            var unfinalizedNoSit = Spawnable();
            unfinalizedNoSit.TerminalStateValue = null;
            unfinalizedNoSit.VesselSnapshot = Snapshot(null);
            Assert.Equal((true, ""), Decide(unfinalizedNoSit));
        }

        [Fact]
        public void RecoveredByLedgerRow_ReasonCarriesTerminal()
        {
            var rec = Spawnable("rec-recovered");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            Ledger.AddAction(new GameAction
            {
                UT = 300.0,
                Type = GameActionType.FundsEarning,
                FundsSource = FundsEarningSource.Recovery,
                FundsAwarded = 1000f,
                RecordingId = "rec-recovered",
            });

            Assert.Equal((false, "recovered after commit (recovery ledger row, terminal Landed)"), Decide(rec));
        }

        [Fact]
        public void SpawnedPid_IsTheLastGate()
        {
            var rec = Spawnable();
            rec.SpawnedVesselPersistentId = 777u;
            Assert.Equal((false, "already spawned (pid=777)"), Decide(rec));
        }

        /// <summary>
        /// Precedence: a recording that trips every gate reports the FIRST gate's reason; clearing that gate
        /// exposes the next one, in source order, until the recording spawns.
        /// </summary>
        [Fact]
        public void Precedence_PeelEveryGateInOrder()
        {
            var parent = Spawnable("rec-peel");
            parent.TreeId = TreeId;
            var child = Spawnable("rec-peel-child");
            child.TreeId = TreeId;
            var tree = TreeWithBranch(parent, child, BranchPointType.Breakup);

            var rec = parent;
            rec.TerminalSpawnSupersededByRecordingId = "rec-later";
            rec.ChainId = "chain-peel";
            rec.SpawnSuppressedByRewind = true;
            rec.SpawnSuppressedByRewindReason = ParsekScenario.RewindSpawnSuppressionReasonSameRecording;
            rec.VesselSpawned = true;
            rec.VesselDestroyed = true;
            rec.IsDebris = true;
            rec.VesselSnapshot = null;
            rec.ChainBranch = 2;
            rec.SpawnedVesselPersistentId = 99u;
            rec.ChildBranchPointId = "bp-char"; // the child shares the pid: not an effective leaf
            bool activeChain = true;

            var steps = new List<(string reason, Action clear)>
            {
                ("terminal spawn superseded by recording rec-later", () => rec.TerminalSpawnSupersededByRecordingId = null),
                ("spawn suppressed post-rewind (same-recording active/source protection, #573)", () => rec.SpawnSuppressedByRewind = false),
                ("already spawned (VesselSpawned=true)", () => rec.VesselSpawned = false),
                ("vessel destroyed", () => rec.VesselDestroyed = false),
                ("no vessel snapshot", () => rec.VesselSnapshot = Snapshot("SUB_ORBITAL")),
                ("branch > 0 (ghost-only)", () => rec.ChainBranch = 0),
                ("active chain being built", () => activeChain = false),
                ("non-leaf tree recording", () => rec.ChildBranchPointId = null),
                ("non-leaf in tree (safety net)", () => tree.BranchPoints.Clear()),
                ("debris recording (visual-only)", () => { rec.IsDebris = false; rec.TerminalStateValue = TerminalState.Recovered; }),
                ("terminal state Recovered", () => rec.TerminalStateValue = null),
                ("snapshot situation unsafe (FLYING/SUB_ORBITAL)", () => rec.VesselSnapshot = Snapshot("ESCAPING")),
                ("unfinalized recording, snapshot situation maps to terminal SubOrbital", () => rec.VesselSnapshot = Snapshot("LANDED")),
                ("already spawned (pid=99)", () => rec.SpawnedVesselPersistentId = 0),
            };

            for (int i = 0; i < steps.Count; i++)
            {
                Assert.Equal((false, steps[i].reason), Decide(rec, activeChain, tree));
                steps[i].clear();
            }
            Assert.Equal((true, ""), Decide(rec, activeChain, tree));
        }
    }
}
