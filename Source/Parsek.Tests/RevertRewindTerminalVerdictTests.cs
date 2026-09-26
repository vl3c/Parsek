using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// REVERT-BLANKET-CLEARS-PRE-FLIGHT-TERMINAL (operator ruling 2026-09-26: retire the
    /// post-spawn terminal-verdict clear on BOTH the revert / in-session OnLoad path and the
    /// rewind path).
    ///
    /// <para>A committed recording's <c>TerminalStateValue</c> is recorded content: real-vessel
    /// terminal events stamp only the pending tree, and Destroyed / Recovered are never
    /// spawnable. The retired clear keyed on <c>VesselSpawned &amp;&amp; (Destroyed || Recovered)</c>
    /// and in practice erased genuine debris endings (debris <c>VesselSpawned=true</c> is only
    /// the crew-auto-unreserve marker). These cells pin that both resets now keep the verdict
    /// while still zeroing the spawn-tracking fields, and that the revert / rewind flight
    /// dispositions (unstash vs commit) are untouched.</para>
    ///
    /// <para>The OnLoad reset loop runs inside a Unity-only instance method, so its body
    /// lives in <see cref="ParsekScenario.ResetTreeRecordingMutableStateForLoad"/> (driven
    /// directly here) and the call site is pinned by the source gate at the bottom.</para>
    /// </summary>
    [Collection("Sequential")]
    public class RevertRewindTerminalVerdictTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RevertRewindTerminalVerdictTests()
        {
            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.SkipSidecarCurrencyCheckForTesting = true;
            MilestoneStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // ---- fixtures ----

        private static ConfigNode Snapshot()
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("name", "Test Vessel");
            node.AddValue("pid", "12345");
            return node;
        }

        /// <summary>
        /// A committed debris recording in the dominant measured shape: Destroyed during
        /// recording, and <c>VesselSpawned=true</c> because the OnLoad crew auto-unreserve
        /// pass marks a snapshot-bearing recording whose EndUT passed without a spawn.
        /// </summary>
        private static Recording MarkedDebris(string id, string treeId)
        {
            var rec = new Recording
            {
                RecordingId = id,
                TreeId = treeId,
                VesselName = "Kerbal X Debris",
                IsDebris = true,
                VesselSpawned = true,
                SpawnAttempts = 2,
                SpawnDeathCount = 1,
                SpawnedVesselPersistentId = 2708531065u,
                LastAppliedResourceIndex = 4,
                TerminalStateValue = TerminalState.Destroyed,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100 });
            rec.Points.Add(new TrajectoryPoint { ut = 200 });
            return rec;
        }

        private static void AssertSpawnFieldsReset(Recording rec)
        {
            Assert.False(rec.VesselSpawned);
            Assert.Equal(0, rec.SpawnAttempts);
            Assert.Equal(0, rec.SpawnDeathCount);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
            Assert.Equal(-1, rec.LastAppliedResourceIndex);
            Assert.False(rec.TerminalSpawnSafetyDeferred);
            Assert.False(rec.TerminalSpawnCannotSpawnSafely);
        }

        // ---- revert / in-session OnLoad reset ----

        [Fact]
        public void RevertReset_KeepsCommittedDebrisDestroyed_AndResetsSpawnFields()
        {
            var debris = MarkedDebris("debris-1", "tree-old");
            debris.TerminalSpawnSafetyDeferred = true;
            RecordingStore.AddRecordingWithTreeForTesting(debris, "Old Mission");

            int reset = ParsekScenario.ResetTreeRecordingMutableStateForLoad(
                RecordingStore.CommittedRecordings);

            Assert.Equal(1, reset);
            Assert.Equal(TerminalState.Destroyed, debris.TerminalStateValue);
            AssertSpawnFieldsReset(debris);
            Assert.Contains(logLines, l => l.Contains("[Scenario]")
                && l.Contains("OnLoad tree-mutable-state: reset spawn tracking on 1 tree recording(s)")
                && l.Contains("1 recorded terminal verdict(s) kept"));
            Assert.DoesNotContain(logLines, l => l.Contains("Clearing post-spawn terminal state"));
        }

        [Fact]
        public void RevertReset_KeepsRecoveredVerdict()
        {
            var rec = MarkedDebris("rec-recovered", "tree-old");
            rec.IsDebris = false;
            rec.VesselName = "Recovered Pod";
            rec.TerminalStateValue = TerminalState.Recovered;
            RecordingStore.AddRecordingWithTreeForTesting(rec, "Old Mission");

            ParsekScenario.ResetTreeRecordingMutableStateForLoad(RecordingStore.CommittedRecordings);

            Assert.Equal(TerminalState.Recovered, rec.TerminalStateValue);
            Assert.False(rec.VesselSpawned);
        }

        [Fact]
        public void RevertReset_RollsBackContinuationData_AndSkipsNonTreeRecordings()
        {
            var tree = MarkedDebris("tree-rec", "tree-old");
            tree.Points.Add(new TrajectoryPoint { ut = 300 });
            tree.ContinuationBoundaryIndex = 2;

            var loose = new Recording
            {
                RecordingId = "loose",
                VesselSpawned = true,
                SpawnedVesselPersistentId = 7u,
            };

            int reset = ParsekScenario.ResetTreeRecordingMutableStateForLoad(
                new List<Recording> { tree, loose });

            Assert.Equal(1, reset);
            Assert.Equal(2, tree.Points.Count);
            Assert.Equal(-1, tree.ContinuationBoundaryIndex);
            Assert.True(loose.VesselSpawned);
            Assert.Equal(7u, loose.SpawnedVesselPersistentId);
        }

        [Fact]
        public void Revert_UnstashesTheRevertedFlight_AndKeepsTheCommittedDebrisVerdict()
        {
            // Committed before the launch the revert target snapshots.
            var debris = MarkedDebris("debris-prior", "tree-prior");
            RecordingStore.AddRecordingWithTreeForTesting(debris, "Prior Mission");
            int committedBefore = RecordingStore.CommittedRecordings.Count;

            // The reverted flight: stashed pending, never committed.
            var reverted = new RecordingTree
            {
                Id = "tree-reverted",
                TreeName = "Reverted Flight",
                RootRecordingId = "rec-reverted",
                ActiveRecordingId = "rec-reverted",
            };
            reverted.Recordings["rec-reverted"] = new Recording
            {
                RecordingId = "rec-reverted",
                TreeId = "tree-reverted",
                VesselName = "Reverted Flight",
                TerminalStateValue = TerminalState.Destroyed,
            };
            RecordingStore.StashPendingTree(reverted, PendingTreeState.Finalized);
            Assert.True(RecordingStore.HasPendingTree);

            // The two OnLoad revert steps this change sits between.
            RecordingStore.UnstashPendingTreeOnRevert();
            ParsekScenario.ResetTreeRecordingMutableStateForLoad(RecordingStore.CommittedRecordings);

            Assert.False(RecordingStore.HasPendingTree);
            Assert.Equal(committedBefore, RecordingStore.CommittedRecordings.Count);
            foreach (var rec in RecordingStore.CommittedRecordings)
                Assert.NotEqual("rec-reverted", rec.RecordingId);
            Assert.Equal(TerminalState.Destroyed, debris.TerminalStateValue);
            AssertSpawnFieldsReset(debris);
        }

        // ---- rewind reset ----

        [Fact]
        public void RewindReset_KeepsCommittedDebrisDestroyed_AndResetsSpawnFields()
        {
            var debris = MarkedDebris("debris-rw", "tree-rw");
            debris.TerminalSpawnCannotSpawnSafely = true;
            RecordingStore.AddRecordingWithTreeForTesting(debris, "Rewound Mission");

            RecordingStore.ResetAllPlaybackState();

            Assert.Equal(TerminalState.Destroyed, debris.TerminalStateValue);
            AssertSpawnFieldsReset(debris);
        }

        [Fact]
        public void FlightCommittedAtRewindSceneExit_KeepsItsDebrisVerdicts()
        {
            // The live tree auto-commits at the rewind's scene exit ...
            var tree = new RecordingTree
            {
                Id = "tree-live",
                TreeName = "Live Flight",
                RootRecordingId = "root",
                ActiveRecordingId = "root",
            };
            var root = new Recording
            {
                RecordingId = "root",
                TreeId = tree.Id,
                VesselName = "Live Flight",
                ChildBranchPointId = "bp1",
                TerminalStateValue = TerminalState.Orbiting,
                VesselSnapshot = Snapshot(),
            };
            var debrisA = MarkedDebris("debris-a", tree.Id);
            var debrisB = MarkedDebris("debris-b", tree.Id);
            foreach (var d in new[] { debrisA, debrisB })
            {
                d.VesselSpawned = false;
                d.SpawnedVesselPersistentId = 0;
                d.ParentBranchPointId = "bp1";
            }
            tree.Recordings[root.RecordingId] = root;
            tree.Recordings[debrisA.RecordingId] = debrisA;
            tree.Recordings[debrisB.RecordingId] = debrisB;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp1",
                UT = 150.0,
                Type = BranchPointType.Breakup,
                ParentRecordingIds = new List<string> { "root" },
                ChildRecordingIds = new List<string> { "debris-a", "debris-b" },
            });
            RecordingStore.CommitTree(tree);
            Assert.Equal(3, RecordingStore.CommittedRecordings.Count);

            // ... the crew auto-unreserve pass marks the debris (the measured shape) ...
            debrisA.VesselSpawned = true;
            debrisB.VesselSpawned = true;

            // ... and the rewind OnLoad resets playback state.
            RecordingStore.ResetAllPlaybackState();

            Assert.Equal(TerminalState.Destroyed, debrisA.TerminalStateValue);
            Assert.Equal(TerminalState.Destroyed, debrisB.TerminalStateValue);
            Assert.Equal(TerminalState.Orbiting, root.TerminalStateValue);
            Assert.False(debrisA.VesselSpawned);
            Assert.False(debrisB.VesselSpawned);
        }

        // ---- the one post-spawn shape: a continuation the optimizer merged into its parent ----

        /// <summary>
        /// The only reachable way a committed Destroyed lands on a recording that carries
        /// <c>VesselSpawned=true</c> from a real spawn: the optimizer absorbs a later chain
        /// segment (the vessel flown after its spawn) into the spawned parent, and
        /// <c>MergeInto</c> stamps the absorbed segment's verdict onto the parent. That
        /// verdict describes the merged recording's own trajectory end, which replays after a
        /// revert or rewind, so it must stay: a nulled verdict would make
        /// <c>ShouldSpawnAtRecordingEnd</c> materialize a vessel at the end of a flight that
        /// ended in destruction.
        /// </summary>
        private static Recording MergedSpawnedParentEndingDestroyed()
        {
            var parent = new Recording
            {
                RecordingId = "sw-parent",
                TreeId = "tree-sw",
                VesselName = "Relay Probe",
                ChainId = "chain-sw",
                ChainIndex = 0,
                ChainBranch = 0,
                VesselSnapshot = Snapshot(),
                VesselSpawned = true,
                SpawnedVesselPersistentId = 555u,
                TerminalStateValue = TerminalState.Orbiting,
            };
            parent.Points.Add(new TrajectoryPoint { ut = 100 });
            parent.Points.Add(new TrajectoryPoint { ut = 200 });

            var continuation = new Recording
            {
                RecordingId = "sw-continuation",
                TreeId = "tree-sw",
                VesselName = "Relay Probe",
                ChainId = "chain-sw",
                ChainIndex = 1,
                ChainBranch = 0,
                TerminalStateValue = TerminalState.Destroyed,
            };
            continuation.Points.Add(new TrajectoryPoint { ut = 210 });
            continuation.Points.Add(new TrajectoryPoint { ut = 300 });

            Assert.True(RecordingOptimizer.CanAutoMerge(parent, continuation));
            Assert.Equal("sw-continuation", RecordingOptimizer.MergeInto(parent, continuation));
            Assert.Equal(TerminalState.Destroyed, parent.TerminalStateValue);
            Assert.True(parent.VesselSpawned);
            return parent;
        }

        [Fact]
        public void MergedContinuation_RevertReset_KeepsTheRecordedDestroyed_AndDoesNotRespawn()
        {
            var merged = MergedSpawnedParentEndingDestroyed();
            RecordingStore.AddRecordingWithTreeForTesting(merged, "Relay Mission");

            ParsekScenario.ResetTreeRecordingMutableStateForLoad(RecordingStore.CommittedRecordings);

            Assert.Equal(TerminalState.Destroyed, merged.TerminalStateValue);
            Assert.False(merged.VesselSpawned);
            Assert.Equal(0u, merged.SpawnedVesselPersistentId);
            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                merged, isActiveChainMember: false, isChainLooping: false);
            Assert.False(needsSpawn);
            Assert.Contains("terminal state Destroyed", reason);
        }

        [Fact]
        public void MergedContinuation_RewindReset_KeepsTheRecordedDestroyed_AndDoesNotRespawn()
        {
            var merged = MergedSpawnedParentEndingDestroyed();
            RecordingStore.AddRecordingWithTreeForTesting(merged, "Relay Mission");

            RecordingStore.ResetAllPlaybackState();

            Assert.Equal(TerminalState.Destroyed, merged.TerminalStateValue);
            Assert.False(merged.VesselSpawned);
            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                merged, isActiveChainMember: false, isChainLooping: false);
            Assert.False(needsSpawn);
            Assert.Contains("terminal state Destroyed", reason);
        }

        // ---- source gate: OnLoad still routes its reset through the helper ----

        [Fact]
        public void OnLoad_TreeMutableStatePhase_CallsTheResetHelper()
        {
            string src = StripComments(ReadParsekSource("ParsekScenario.cs"));
            string onLoad = Flatten(BraceBody(src, "public override void OnLoad(ConfigNode node)"));

            Assert.Contains(
                "loadPhase = \"tree-mutable-state\"; ResetTreeRecordingMutableStateForLoad(recordings);",
                onLoad);
            Assert.Equal(1, Occurrences(onLoad, "ResetTreeRecordingMutableStateForLoad("));
        }

        [Fact]
        public void NeitherResetTouchesTheTerminalVerdict()
        {
            string scenario = StripComments(ReadParsekSource("ParsekScenario.cs"));
            string helper = BraceBody(scenario,
                "internal static int ResetTreeRecordingMutableStateForLoad(");
            string store = StripComments(ReadParsekSource("RecordingStore.cs"));
            string rewind = BraceBody(store,
                "private static void ResetRecordingPlaybackFields(Recording rec)");

            foreach (var body in new[] { helper, rewind })
            {
                Assert.DoesNotContain("StampTerminalState", body);
                Assert.DoesNotContain("TerminalStateValue =", body);
            }
            Assert.Contains("VesselSpawned = false", helper);
            Assert.Contains("VesselSpawned = false", rewind);
        }

        // ---- helpers ----

        private static string BraceBody(string src, string signature)
        {
            int sig = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(sig >= 0, "source gate: signature not found: " + signature);
            Assert.True(src.IndexOf(signature, sig + 1, StringComparison.Ordinal) < 0,
                "source gate: signature is ambiguous: " + signature);
            int open = src.IndexOf('{', sig);
            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0)
                    return src.Substring(open, i - open + 1);
            }
            Assert.True(false, "source gate: unbalanced braces after " + signature);
            return null;
        }

        private static string Flatten(string s) => Regex.Replace(s, @"\s+", " ").Trim();

        private static int Occurrences(string haystack, string needle)
        {
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        private static string ReadParsekSource(string relPath)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(root, "Source", "Parsek", relPath);
            Assert.True(File.Exists(path), "Source file not found at " + path);
            return File.ReadAllText(path);
        }

        private static string StripComments(string source)
        {
            var sb = new StringBuilder(source.Length);
            int i = 0, n = source.Length;
            while (i < n)
            {
                if (source[i] == '/' && i + 1 < n && source[i + 1] == '/')
                {
                    int j = source.IndexOf('\n', i);
                    i = j < 0 ? n : j;
                    continue;
                }
                if (source[i] == '/' && i + 1 < n && source[i + 1] == '*')
                {
                    int j = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = j < 0 ? n : j + 2;
                    continue;
                }
                sb.Append(source[i]);
                i++;
            }
            return sb.ToString();
        }
    }
}
