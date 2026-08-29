using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// TS-FLUSHED-SAVE-DROPS-DEBRIS-TERMINALSTATE.
    ///
    /// <para>A save written after a scene change out of FLIGHT lost the
    /// <c>terminalState</c> key on every committed recording that Parsek had spawned
    /// during that flight and whose verdict was Destroyed / Recovered. Measured off the
    /// V8T-eve-ts-arrival lane: three archived produced saves each carry 9 RECORDING
    /// nodes with a terminal histogram of <c>{Orbiting 2}</c>, while every FLIGHT-flushed
    /// sibling run of the same fixture carries <c>{Orbiting 2, Destroyed 6}</c>.</para>
    ///
    /// <para>The mechanism is the OnLoad in-session branch's reset/restore PAIR, not the
    /// codec and not the TRACKSTATION scene: the <c>tree-mutable-state</c> reset retracts
    /// the verdict via <see cref="ParsekScenario.ClearPostSpawnTerminalState"/>, and the
    /// <c>tree-state-restore</c> loop below it — which puts back every OTHER field the
    /// reset clears — had nothing that put the verdict back, because <c>terminalState</c>
    /// lives in the STRUCTURAL half of <c>RecordingTreeRecordCodec</c> that this branch
    /// never re-runs (the committed store is preserved in memory across the scene change).
    /// The null was therefore permanent, and the next OnSave wrote the recording with no
    /// <c>terminalState</c> key at all.</para>
    ///
    /// <para><b>What these cells actually prove, stated precisely.</b> Two things, both
    /// against real production code: (1) the CODEC's <c>HasValue</c> gate — a retracted
    /// verdict is written as an ABSENT key, and that absence round-trips, which is the
    /// shape measured on the produced-save bytes; and (2) the COMPOSITION of the two
    /// helpers — <see cref="ParsekScenario.ClearPostSpawnTerminalState"/> reports what it
    /// retracted, and <see cref="ParsekScenario.RestoreClearedPostSpawnTerminalState"/>
    /// puts back exactly that, from a node the real codec authored, under each of the
    /// dispositions below.</para>
    ///
    /// <para><b>What they do NOT prove.</b> They are not a replay of OnLoad. The
    /// <see cref="ApplyResetLeg"/> helper reproduces the reset loop's terminal-verdict
    /// statements only — it does not reproduce the other five field resets, and the
    /// restore LOOP (saved-node lookup, id match, revert gate) is not replayed at all: the
    /// cells call the helper directly on a node they built. OnLoad is Unity-only, so the
    /// wiring — that the two legs exist, in that order, reachable, with nothing emptying
    /// the set between them — is pinned at source level by
    /// <see cref="SceneChangeTerminalStateWiringGateTests"/> instead. Read the two files as
    /// one proof: behaviour here, wiring there.</para>
    /// </summary>
    [Collection("Sequential")]
    public class SceneChangeTerminalStatePreservationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SceneChangeTerminalStatePreservationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        /// <summary>
        /// One committed debris recording in the shape the lane measured: spawned back into
        /// the world during the flight being left, terminal verdict Destroyed.
        /// </summary>
        private static Recording SpawnedDebris(string id, TerminalState terminal)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "Kerbal X Debris",
                TreeId = "tree-1",   // makes IsTreeRecording true
                VesselSpawned = true,
                SpawnedVesselPersistentId = 2708531065u,
                TerminalStateValue = terminal
            };
        }

        /// <summary>The saved RECORDING node OnLoad reads, authored by the real codec.</summary>
        private static ConfigNode SavedNodeFor(Recording rec)
        {
            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, rec);
            return node;
        }

        /// <summary>
        /// The OnLoad `tree-mutable-state` reset loop's TERMINAL-VERDICT statements only —
        /// the retraction plus the id capture, which is all these cells are about. The
        /// loop's other field resets (SpawnAttempts, SpawnDeathCount,
        /// TerminalOrbitSpawnSafety, RollbackContinuationData) are deliberately NOT
        /// reproduced; this is not a replay of OnLoad. Returns the cleared-id set the
        /// restore leg consumes.
        /// </summary>
        private static HashSet<string> ApplyResetLeg(params Recording[] recordings)
        {
            var cleared = new HashSet<string>();
            foreach (var rec in recordings)
            {
                if (!rec.IsTreeRecording) continue;
                if (ParsekScenario.ClearPostSpawnTerminalState(rec, "tree recording")
                    && !string.IsNullOrEmpty(rec.RecordingId))
                    cleared.Add(rec.RecordingId);
                rec.VesselSpawned = false;
                rec.SpawnedVesselPersistentId = 0;
                rec.LastAppliedResourceIndex = -1;
            }
            return cleared;
        }

        // ────────────────────────────────────────────────────────────
        //  The headline repro
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void SceneChangeResetRestorePair_KeepsDestroyedVerdict_OnSpawnedDebris()
        {
            var rec = SpawnedDebris("7d373f22d0a04ff2a58d43bbcca47757", TerminalState.Destroyed);
            ConfigNode savedNode = SavedNodeFor(rec);
            Assert.Equal(
                ((int)TerminalState.Destroyed).ToString(),
                savedNode.GetValue("terminalState"));

            HashSet<string> cleared = ApplyResetLeg(rec);

            // The reset really does retract the verdict — that half is not in dispute.
            Assert.Null(rec.TerminalStateValue);
            Assert.Contains(rec.RecordingId, cleared);

            bool restored = ParsekScenario.RestoreClearedPostSpawnTerminalState(
                rec, savedNode, cleared, "tree recording");

            Assert.True(restored);
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.Empty(cleared);
            Assert.Contains(logLines, l => l.Contains("[Scenario]")
                && l.Contains("Restored post-spawn terminal state Destroyed"));

            // The measured surface: the save the scene we just entered writes.
            ConfigNode reSaved = SavedNodeFor(rec);
            Assert.Equal(
                ((int)TerminalState.Destroyed).ToString(),
                reSaved.GetValue("terminalState"));
        }

        /// <summary>
        /// CHARACTERIZATION of the codec's <c>HasValue</c> gate, which is what turned a
        /// retracted verdict into the measured shape: with the verdict cleared, the next
        /// save carries no <c>terminalState</c> key at all — not a wrong value, an absent
        /// key — and the absence round-trips back to an unclassified recording. This cell
        /// asserts a property of the codec that is TRUE ON MAIN AND HERE; it documents the
        /// loss shape, it does not detect the defect. What makes the loss permanent (no
        /// restore leg) is a property of OnLoad, pinned by
        /// <see cref="SceneChangeTerminalStateWiringGateTests"/>.
        /// </summary>
        [Fact]
        public void WithoutTheRestoreLeg_TheNextSaveOmitsTheTerminalStateKeyEntirely()
        {
            var rec = SpawnedDebris("0b4193a0540e4fa7af9890fe4ba5c10d", TerminalState.Destroyed);

            ApplyResetLeg(rec);

            ConfigNode reSaved = SavedNodeFor(rec);
            Assert.Null(reSaved.GetValue("terminalState"));

            // And it does not come back through the codec either — the load half is
            // symmetric, so an absent key round-trips to an unclassified recording.
            var reloaded = new Recording();
            RecordingTree.LoadRecordingFrom(reSaved, reloaded);
            Assert.Null(reloaded.TerminalStateValue);
        }

        [Fact]
        public void RestoredVerdict_RoundTripsThroughTheRealCodec()
        {
            var rec = SpawnedDebris("2f3bf43348534202b226afbb6ae00ce9", TerminalState.Recovered);
            ConfigNode savedNode = SavedNodeFor(rec);
            HashSet<string> cleared = ApplyResetLeg(rec);

            ParsekScenario.RestoreClearedPostSpawnTerminalState(rec, savedNode, cleared, "tree recording");

            var reloaded = new Recording();
            RecordingTree.LoadRecordingFrom(SavedNodeFor(rec), reloaded);
            Assert.Equal(TerminalState.Recovered, reloaded.TerminalStateValue);
        }

        // ────────────────────────────────────────────────────────────
        //  Scoping — the restore may only undo THIS pass's retraction
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void RestoreIsScopedToTheIdsTheResetActuallyCleared()
        {
            // A recording whose verdict was retracted on purpose elsewhere (the
            // RecordingOptimizer.SplitAtUT HEAD carve-out) is NOT in the cleared set,
            // so a saved node that predates that retraction must not re-stamp it.
            var head = SpawnedDebris("head-recording", TerminalState.Destroyed);
            ConfigNode stalePreSplitNode = SavedNodeFor(head);
            head.TerminalStateValue = null;   // the deliberate retraction

            var cleared = new HashSet<string>();   // this pass cleared nothing

            bool restored = ParsekScenario.RestoreClearedPostSpawnTerminalState(
                head, stalePreSplitNode, cleared, "tree recording");

            Assert.False(restored);
            Assert.Null(head.TerminalStateValue);
        }

        [Fact]
        public void RestoreIgnoresRecordingsWhoseVerdictSurvivedTheReset()
        {
            // Orbiting is not a post-spawn verdict, so the reset never touches it and the
            // id never enters the set — the restore must leave it exactly alone.
            var orbiting = SpawnedDebris("081b06e81737471fb5d85f3e0e92d49b", TerminalState.Orbiting);
            ConfigNode savedNode = SavedNodeFor(orbiting);

            HashSet<string> cleared = ApplyResetLeg(orbiting);

            Assert.Empty(cleared);
            Assert.Equal(TerminalState.Orbiting, orbiting.TerminalStateValue);

            ParsekScenario.RestoreClearedPostSpawnTerminalState(orbiting, savedNode, cleared, "tree recording");
            Assert.Equal(TerminalState.Orbiting, orbiting.TerminalStateValue);
        }

        // ────────────────────────────────────────────────────────────
        //  The cases where staying cleared is the right answer
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void SavedNodeWithoutTheKey_LeavesTheRecordingClearedAndConsumesTheId()
        {
            // Quickload to a save point that predates the verdict: null IS the save-point
            // truth, so the recording stays cleared and the id leaves the residue.
            var rec = SpawnedDebris("c5403bf4a1144815bfa0d8691e38a587", TerminalState.Destroyed);
            var preStampNode = new ConfigNode("RECORDING");
            preStampNode.AddValue("recordingId", rec.RecordingId);

            HashSet<string> cleared = ApplyResetLeg(rec);
            bool restored = ParsekScenario.RestoreClearedPostSpawnTerminalState(
                rec, preStampNode, cleared, "tree recording");

            Assert.False(restored);
            Assert.Null(rec.TerminalStateValue);
            Assert.Empty(cleared);
            Assert.Contains(logLines, l => l.Contains("[Scenario]")
                && l.Contains("stays cleared")
                && l.Contains("no terminalState key"));
        }

        /// <summary>
        /// The revert gate, driven rather than described. OnLoad wraps the restore call in
        /// <c>if (!isRevert)</c>; this drives that same decision over a node that DOES carry
        /// a restorable verdict, so a gate inverted to <c>if (isRevert)</c> would show up
        /// here as a verdict that came back on revert. (That the gate exists at the call
        /// site is the source gate's job; this cell owns the consequence.)
        /// </summary>
        [Theory]
        [InlineData(true)]    // revert: restore skipped, verdict stays retracted
        [InlineData(false)]   // scene change / quickload: restore runs
        public void TheRevertGateDecidesWhetherTheVerdictComesBack(bool isRevert)
        {
            var rec = SpawnedDebris("fbc705e91fcd4b5a8176cf5493807a0b", TerminalState.Destroyed);
            ConfigNode savedNode = SavedNodeFor(rec);

            HashSet<string> cleared = ApplyResetLeg(rec);
            Assert.Null(rec.TerminalStateValue);

            // The OnLoad call site, verbatim in shape.
            if (!isRevert)
                ParsekScenario.RestoreClearedPostSpawnTerminalState(
                    rec, savedNode, cleared, "tree recording");

            if (isRevert)
            {
                Assert.Null(rec.TerminalStateValue);
                Assert.Contains(rec.RecordingId, cleared);
            }
            else
            {
                Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
                Assert.Empty(cleared);
            }
        }

        /// <summary>
        /// The one reachable loss shape left, and its DESIGNED disposition.
        ///
        /// <para>A cleared id can reach the end of the restore loop having met no saved
        /// node at all: `savedTreeNodes` empty, the recording absent from the loaded save,
        /// or its tree node skipped as the active / pending marker. The archived logs show
        /// memory and node counts are independent, so the reachable player shape is an F9
        /// quickload onto an OLDER quicksave — one taken before the recording existed, or
        /// before its verdict was stamped.</para>
        ///
        /// <para>Staying cleared is the RIGHT answer there: the save point genuinely
        /// predates the verdict, and re-stamping from nothing would invent one. So the
        /// verdict is permanently retracted by design, the id stays in the residue, and the
        /// once-per-load residue line is the evidence. This cell exists so that disposition
        /// is a decision on record rather than an accident.</para>
        /// </summary>
        [Fact]
        public void ClearedIdThatMeetsNoSavedNode_StaysClearedByDesign_AndRemainsInTheResidue()
        {
            var rec = SpawnedDebris("fbc705e91fcd4b5a8176cf5493807a0b", TerminalState.Destroyed);

            HashSet<string> cleared = ApplyResetLeg(rec);

            // The restore loop iterates saved RECORDING nodes; with none, it never runs for
            // this recording — no call, no restore, no consumption of the id.
            Assert.Null(rec.TerminalStateValue);
            Assert.Contains(rec.RecordingId, cleared);

            // And the recording persists unclassified — the measured loss shape, reached
            // through a path where it is the intended outcome rather than a bug.
            Assert.Null(SavedNodeFor(rec).GetValue("terminalState"));
        }

        [Fact]
        public void CorruptSavedTerminalState_LeavesClearedWarnsAndStaysInTheResidue()
        {
            var rec = SpawnedDebris("8eda6186afcd46db81551ada10dcef9a", TerminalState.Destroyed);
            var corrupt = new ConfigNode("RECORDING");
            corrupt.AddValue("recordingId", rec.RecordingId);
            corrupt.AddValue("terminalState", "not-a-number");

            HashSet<string> cleared = ApplyResetLeg(rec);
            bool restored = ParsekScenario.RestoreClearedPostSpawnTerminalState(
                rec, corrupt, cleared, "tree recording");

            Assert.False(restored);
            Assert.Null(rec.TerminalStateValue);
            Assert.Contains(rec.RecordingId, cleared);
            Assert.Contains(logLines, l => l.Contains("[Scenario]")
                && l.Contains("Cannot restore post-spawn terminal state"));
        }

        [Fact]
        public void OutOfRangeSavedTerminalState_IsRejectedLikeACorruptOne()
        {
            var rec = SpawnedDebris("out-of-range", TerminalState.Destroyed);
            var bogus = new ConfigNode("RECORDING");
            bogus.AddValue("recordingId", rec.RecordingId);
            bogus.AddValue("terminalState", "9999");

            HashSet<string> cleared = ApplyResetLeg(rec);
            bool restored = ParsekScenario.RestoreClearedPostSpawnTerminalState(
                rec, bogus, cleared, "tree recording");

            Assert.False(restored);
            Assert.Null(rec.TerminalStateValue);
            Assert.Contains(rec.RecordingId, cleared);
            // Same Warn as the unparseable sibling: an out-of-range int must be REJECTED
            // loudly, never silently cast to whatever enum member happens to sit there.
            Assert.Contains(logLines, l => l.Contains("[Scenario]")
                && l.Contains("Cannot restore post-spawn terminal state")
                && l.Contains("9999"));
        }

        // ────────────────────────────────────────────────────────────
        //  Null-argument hygiene
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void NullArguments_AreNoOps()
        {
            var rec = SpawnedDebris("null-args", TerminalState.Destroyed);
            var cleared = new HashSet<string> { rec.RecordingId };

            Assert.False(ParsekScenario.RestoreClearedPostSpawnTerminalState(null, new ConfigNode(), cleared, "ctx"));
            Assert.False(ParsekScenario.RestoreClearedPostSpawnTerminalState(rec, null, cleared, "ctx"));
            Assert.False(ParsekScenario.RestoreClearedPostSpawnTerminalState(rec, new ConfigNode(), null, "ctx"));
        }
    }
}
