using System;
using System.Collections.Generic;
using System.IO;
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
    /// <para>These cells drive the REAL codec on both ends and replay the reset → restore
    /// sequence exactly as OnLoad runs it. Residual not modelled here: the OnLoad wiring
    /// itself (which branch runs, and that the id set is threaded from the reset loop into
    /// the restore loop) is Unity-runtime-only — <see cref="SceneChangeResetRestorePairIsWiredInOnLoad"/>
    /// pins it at source level instead.</para>
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
        /// The OnLoad `tree-mutable-state` reset leg, replayed field-for-field.
        /// Returns the cleared-id set the restore leg consumes.
        /// </summary>
        private static HashSet<string> ReplayResetLeg(params Recording[] recordings)
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

            HashSet<string> cleared = ReplayResetLeg(rec);

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
        /// The defect, stated as the exact shape the lane measured on the produced-save
        /// bytes: without the restore leg the retraction is permanent and the NEXT save
        /// carries no <c>terminalState</c> key at all — not a wrong value, an absent key.
        /// This is what origin/main does.
        /// </summary>
        [Fact]
        public void WithoutTheRestoreLeg_TheNextSaveOmitsTheTerminalStateKeyEntirely()
        {
            var rec = SpawnedDebris("0b4193a0540e4fa7af9890fe4ba5c10d", TerminalState.Destroyed);

            ReplayResetLeg(rec);

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
            HashSet<string> cleared = ReplayResetLeg(rec);

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

            HashSet<string> cleared = ReplayResetLeg(orbiting);

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

            HashSet<string> cleared = ReplayResetLeg(rec);
            bool restored = ParsekScenario.RestoreClearedPostSpawnTerminalState(
                rec, preStampNode, cleared, "tree recording");

            Assert.False(restored);
            Assert.Null(rec.TerminalStateValue);
            Assert.Empty(cleared);
            Assert.Contains(logLines, l => l.Contains("[Scenario]")
                && l.Contains("stays cleared")
                && l.Contains("no terminalState key"));
        }

        [Fact]
        public void RevertShapedLoad_SkipsTheRestore_AndLeavesTheVerdictInTheResidue()
        {
            // Revert: OnLoad gates the restore off that branch entirely (the spawn is undone,
            // so a verdict the spawned vessel earned is stale). Nothing calls the restore, the
            // clear stands, and the id stays in the residue the caller reports.
            var rec = SpawnedDebris("fbc705e91fcd4b5a8176cf5493807a0b", TerminalState.Destroyed);

            HashSet<string> cleared = ReplayResetLeg(rec);

            Assert.Null(rec.TerminalStateValue);
            Assert.Contains(rec.RecordingId, cleared);
        }

        [Fact]
        public void CorruptSavedTerminalState_LeavesClearedWarnsAndStaysInTheResidue()
        {
            var rec = SpawnedDebris("8eda6186afcd46db81551ada10dcef9a", TerminalState.Destroyed);
            var corrupt = new ConfigNode("RECORDING");
            corrupt.AddValue("recordingId", rec.RecordingId);
            corrupt.AddValue("terminalState", "not-a-number");

            HashSet<string> cleared = ReplayResetLeg(rec);
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

            HashSet<string> cleared = ReplayResetLeg(rec);
            bool restored = ParsekScenario.RestoreClearedPostSpawnTerminalState(
                rec, bogus, cleared, "tree recording");

            Assert.False(restored);
            Assert.Null(rec.TerminalStateValue);
            Assert.Contains(rec.RecordingId, cleared);
        }

        // ────────────────────────────────────────────────────────────
        //  Null-argument hygiene
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void NullArguments_AreNoOps()
        {
            var rec = SpawnedDebris("null-args", TerminalState.Destroyed);
            var cleared = new HashSet<string> { rec.RecordingId };

            Assert.False(ParsekScenario.RestoreClearedPostSpawnTerminalState(null, new ConfigNode(), cleared));
            Assert.False(ParsekScenario.RestoreClearedPostSpawnTerminalState(rec, null, cleared));
            Assert.False(ParsekScenario.RestoreClearedPostSpawnTerminalState(rec, new ConfigNode(), null));
        }

        // ────────────────────────────────────────────────────────────
        //  The one thing the headless cells cannot execute: the wiring
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// The reset and restore legs are two loops inside <c>ParsekScenario.OnLoad</c>, an
        /// instance method that only runs under Unity. What the headless cells above cannot
        /// prove is that the two legs are still WIRED to each other — that the reset feeds
        /// its cleared ids into a set and the restore loop consumes that same set. A silent
        /// unwiring would restore this bug with every cell above still green, so pin it at
        /// source level.
        /// </summary>
        [Fact]
        public void SceneChangeResetRestorePairIsWiredInOnLoad()
        {
            string path = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "Source", "Parsek", "ParsekScenario.cs"));
            Assert.True(File.Exists(path), $"ParsekScenario.cs not found at {path}");
            // Indentation- and line-ending-independent: collapse every whitespace run.
            string src = System.Text.RegularExpressions.Regex.Replace(
                File.ReadAllText(path), @"\s+", " ");

            // The reset leg captures what it retracted.
            Assert.Contains(
                "if (ClearPostSpawnTerminalState(recordings[i], \"tree recording\") "
                + "&& !string.IsNullOrEmpty(recordings[i].RecordingId)) "
                + "clearedPostSpawnTerminalIds.Add(recordings[i].RecordingId);", src);

            // The restore leg consumes the same set, and stays gated off the revert branch.
            Assert.Contains(
                "if (!isRevert) RestoreClearedPostSpawnTerminalState( "
                + "recordings[i], savedTreeRecNode, "
                + "clearedPostSpawnTerminalIds, \"tree recording\");", src);

            // And the residue is reported rather than swallowed.
            Assert.Contains("post-spawn terminal verdict(s) ", src);
        }
    }
}
