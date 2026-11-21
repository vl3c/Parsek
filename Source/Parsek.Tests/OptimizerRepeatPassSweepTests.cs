using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Repeat-pass sweep (recording split / trim / merge review, section 4): every committed
    /// fixture save (harness/fixtures/saves and Source/Parsek.Tests/Fixtures) is loaded
    /// headlessly - its committed RECORDING_TREE nodes through the production tree codec, each
    /// recording's trajectory through the production sidecar codec - and the optimization pass
    /// runs over it twice, under the rules the save carries (version 1) and with every
    /// recording forced to version 2 and to version 3. The second pass must change nothing: no recording added
    /// or removed, no range, phase, role or chain slot moved. A pass that is not idempotent
    /// re-cuts (or re-glues) the player's rows on every load.
    /// </summary>
    [Collection("Sequential")]
    public class OptimizerRepeatPassSweepTests : IDisposable
    {
        public OptimizerRepeatPassSweepTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            Reset();
        }

        public void Dispose()
        {
            Reset();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
        }

        internal static void Reset()
        {
            MilestoneStore.ResetForTesting();
            Ledger.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        internal static string RepoRoot()
        {
            return Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
        }

        internal static IEnumerable<string> FixtureSaveDirs()
        {
            var roots = new[]
            {
                Path.Combine(RepoRoot(), "harness", "fixtures", "saves"),
                Path.Combine(RepoRoot(), "Source", "Parsek.Tests", "Fixtures"),
            };
            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (string sfs in Directory.GetFiles(root, "persistent.sfs", SearchOption.AllDirectories)
                    .OrderBy(x => x, StringComparer.Ordinal))
                    yield return Path.GetDirectoryName(sfs);
            }
        }

        /// <summary>Loads the committed trees of one fixture save into the store; returns how many
        /// recordings carry track sections.</summary>
        internal static int LoadFixture(string saveDir)
        {
            ConfigNode root = ConfigNode.Load(Path.Combine(saveDir, "persistent.sfs"));
            ConfigNode game = root?.GetNode("GAME") ?? root;
            if (game == null) return 0;
            ConfigNode scenario = game.GetNodes("SCENARIO")
                .FirstOrDefault(n => n.GetValue("name") == "ParsekScenario");
            if (scenario == null) return 0;

            int withSections = 0;
            foreach (ConfigNode treeNode in scenario.GetNodes("RECORDING_TREE"))
            {
                if (treeNode.HasValue("isActive") || treeNode.HasValue("isPending")) continue;
                RecordingTree tree;
                try { tree = RecordingTree.Load(treeNode); }
                catch { continue; }
                if (tree?.Recordings == null || tree.Recordings.Count == 0) continue;
                foreach (var rec in tree.Recordings.Values.ToList())
                {
                    LoadTrajectory(saveDir, rec);
                    rec.FilesDirty = false;
                    if (rec.TrackSections != null && rec.TrackSections.Count > 0) withSections++;
                }
                RecordingStore.AddCommittedTreeForTesting(tree);
                foreach (var rec in tree.Recordings.Values)
                    RecordingStore.AddRecordingWithTreeForTesting(rec);
            }
            return withSections;
        }

        private static void LoadTrajectory(string saveDir, Recording rec)
        {
            string dir = Path.Combine(saveDir, "Parsek", "Recordings");
            string prec = Path.Combine(dir, rec.RecordingId + ".prec");
            string text = prec + ".txt";
            try
            {
                if (File.Exists(prec) && TrajectorySidecarBinary.HasBinaryMagic(prec)
                    && TrajectorySidecarBinary.TryProbe(prec, out var probe))
                {
                    TrajectorySidecarBinary.Read(prec, rec, probe);
                    return;
                }
                string textPath = File.Exists(text) ? text : (File.Exists(prec) ? prec : null);
                if (textPath == null) return;
                ConfigNode node = ConfigNode.Load(textPath);
                if (node != null) TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(node, rec);
            }
            catch
            {
                // A sidecar the codec cannot read leaves the recording without trajectory;
                // the sweep is about the pass, not the codec.
            }
        }

        private static string Snapshot()
        {
            return string.Join("\n", RecordingStore.CommittedRecordings
                .OrderBy(r => r.TreeId, StringComparer.Ordinal)
                .ThenBy(r => r.StartUT)
                .ThenBy(r => r.RecordingId, StringComparer.Ordinal)
                .Select(r => string.Join("|",
                    r.TreeId, r.RecordingId, r.StartUT.ToString("R"), r.EndUT.ToString("R"),
                    r.SegmentPhase, r.SegmentRole, r.ChainId, r.ChainIndex.ToString(),
                    (r.TrackSections?.Count ?? 0).ToString(), (r.Points?.Count ?? 0).ToString())));
        }

        private static List<string> SweepAll(int forcedVersion, out int fixtures, out int recordings)
        {
            var unstable = new List<string>();
            fixtures = 0;
            recordings = 0;
            foreach (string saveDir in FixtureSaveDirs())
            {
                Reset();
                int withSections = LoadFixture(saveDir);
                if (withSections == 0) continue;
                fixtures++;
                recordings += RecordingStore.CommittedRecordings.Count;
                if (forcedVersion > 0)
                    RecordingOptimizer.ForceSplitRuleVersionForTesting(
                        RecordingStore.CommittedRecordings.ToList(), forcedVersion);

                CommittedListOptimizer.RunOptimizationPass();
                string afterFirst = Snapshot();
                CommittedListOptimizer.RunOptimizationPass();
                string afterSecond = Snapshot();
                if (afterFirst != afterSecond)
                    unstable.Add(Path.GetFileName(saveDir));
            }
            return unstable;
        }

        [Fact]
        public void EveryFixtureSave_SecondPassChangesNothing_VersionOne()
        {
            var unstable = SweepAll(0, out int fixtures, out int recordings);
            Assert.True(fixtures >= 50, $"the sweep must cover the fixture inventory (covered {fixtures})");
            Assert.True(unstable.Count == 0,
                $"second pass changed {unstable.Count} of {fixtures} fixture save(s) " +
                $"({recordings} recordings): {string.Join(", ", unstable)}");
        }

        [Fact]
        public void EveryFixtureSave_SecondPassChangesNothing_VersionTwo()
        {
            var unstable = SweepAll(RecordingOptimizer.SplitRuleVersionPassRules, out int fixtures, out int recordings);
            Assert.True(fixtures >= 50, $"the sweep must cover the fixture inventory (covered {fixtures})");
            Assert.True(unstable.Count == 0,
                $"second pass changed {unstable.Count} of {fixtures} fixture save(s) " +
                $"({recordings} recordings): {string.Join(", ", unstable)}");
        }

        [Fact]
        public void EveryFixtureSave_SecondPassChangesNothing_VersionThree()
        {
            var unstable = SweepAll(RecordingOptimizer.SplitRuleVersionBurnCuts, out int fixtures, out int recordings);
            Assert.True(fixtures >= 50, $"the sweep must cover the fixture inventory (covered {fixtures})");
            Assert.True(unstable.Count == 0,
                $"second pass changed {unstable.Count} of {fixtures} fixture save(s) " +
                $"({recordings} recordings): {string.Join(", ", unstable)}");
        }
    }
}
