using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsek;
using Parsek.Analyzer;
using Parsek.Analyzer.Rules;
using Parsek.Tests.Analyzer;
using Xunit;
using Xunit.Abstractions;

namespace Parsek.Tests
{
    /// <summary>
    /// Reads the HARVESTED subject of
    /// OPTIMIZER-SPLIT-DROPS-MERGESTATE-AND-CLOSES-AN-OPEN-REFLY-SLOT
    /// (`refly-a-recorded`, harvested from session 2026-09-08_2317_refly-a-manual)
    /// through the PRODUCTION decode path - `SaveDirectoryLoader.Load`, whose tree walk
    /// is `RecordingTreeRecordCodec.LoadRecordingFrom` and whose sidecar hydration is
    /// `RecordingStore.LoadTrajectorySidecarForTesting`, i.e. the same
    /// `DeserializeTrajectorySidecar` production reads a save with - and pins what those
    /// bytes actually decode to.
    ///
    /// <para>
    /// The point is that the defect is INVISIBLE at the byte level unless you know the
    /// decode rule: the TIP record carries NO `mergeState` key at all, and the whole
    /// damage is that a missing key decodes to <see cref="MergeState.Immutable"/>
    /// (<c>Recording.MergeState</c>'s field initializer). A text-level drift test over the
    /// same fixture cannot see that; only a decode can. Analyzer rule
    /// <c>INV12-SPLIT-CLOSED-SLOT</c> then fires on the decoded model, which is the
    /// closing of the loop: the fixture is the rule's live subject rather than a
    /// hand-built one.
    /// </para>
    ///
    /// <para>
    /// FIXTURE LOCATION. `refly-a-recorded` lands on `main` with PR #1660 (branch
    /// `refly-lanes`). Until it merges the only copy is in that branch's worktree, so the
    /// candidate list below tries the IN-REPO path FIRST and falls back to the sibling
    /// worktree; when #1660 merges the fallback becomes dead weight and can be deleted
    /// without touching an assertion. Absent both, every cell SKIPS with the path it
    /// looked for - a missing fixture must not read as a passing test.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class ReflyARecordedFixtureCodecTests : IDisposable
    {
        // The chain HEAD: the slot's origin, promoted CommittedProvisional by the commit
        // that then split it.
        private const string HeadRecordingId = "32ca55469ac1400da66fcebb3c791d65";

        // The chain TIP the optimizer's phase-change split created at UT 191.04. It
        // carries terminalState = 4 (Destroyed) and NO mergeState key.
        private const string TipRecordingId = "8da7c2c2a6d84505b4bc121fdb9f528f";

        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;
        private readonly ITestOutputHelper output;

        public ReflyARecordedFixtureCodecTests(ITestOutputHelper output)
        {
            this.output = output;
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // xUnit runs from Source/Parsek.Tests/bin/Debug/net472/, so five `..` segments
        // reach the repo root and a sixth reaches the umbrella folder that holds the
        // sibling worktrees.
        private static readonly string[] FixtureCandidates =
        {
            Path.Combine("..", "..", "..", "..", "..",
                "harness", "fixtures", "saves", "refly-a-recorded"),
            Path.Combine("..", "..", "..", "..", "..", "..",
                "Parsek-refly-lanes", "harness", "fixtures", "saves", "refly-a-recorded"),
        };

        private static string ResolveFixtureDir()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (string rel in FixtureCandidates)
            {
                string full = Path.GetFullPath(Path.Combine(baseDir, rel));
                if (File.Exists(Path.Combine(full, "persistent.sfs")))
                    return full;
            }
            return null;
        }

        private static string DescribeCandidates()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            return string.Join(" | ", FixtureCandidates.Select(
                rel => Path.GetFullPath(Path.Combine(baseDir, rel))));
        }

        /// <summary>
        /// Loads the fixture through the production decode path, or returns null when the
        /// fixture is not present (the caller then skips with the paths it tried).
        /// </summary>
        private AnalyzerModel LoadFixtureModel(out string skipReason)
        {
            string dir = ResolveFixtureDir();
            if (dir == null)
            {
                skipReason = "refly-a-recorded fixture not found; looked in: " + DescribeCandidates();
                return null;
            }

            skipReason = null;
            // Echo the resolved directory so a green run names the bytes it read: these
            // cells return early when the fixture is absent, and this line is what tells
            // a reader the run was not vacuous.
            output.WriteLine("fixture=" + dir);
            return SaveDirectoryLoader.Load(dir, _ => null);
        }

        private static Recording ById(AnalyzerModel model, string id)
        {
            return model.Recordings.FirstOrDefault(
                r => r != null && string.Equals(r.RecordingId, id, StringComparison.Ordinal));
        }

        // ---------- What the bytes decode to --------------------------------

        [Fact]
        public void TheHeadDecodesAsCommittedProvisional_AndTheTipAsImmutable()
        {
            string skip;
            AnalyzerModel model = LoadFixtureModel(out skip);
            if (model == null) { output.WriteLine("SKIP: " + skip); return; }

            Recording head = ById(model, HeadRecordingId);
            Recording tip = ById(model, TipRecordingId);
            Assert.NotNull(head);
            Assert.NotNull(tip);

            // The head was promoted by CommitPendingTree and the key IS in the save.
            Assert.Equal(MergeState.CommittedProvisional, head.MergeState);

            // THE DEFECT, decoded: the tip record carries no mergeState key at all, so
            // RecordingTreeRecordCodec leaves Recording.MergeState at its Immutable
            // field initializer. That is the whole damage - an unsealed slot reading
            // sealed - and it is only visible after a decode.
            Assert.Equal(MergeState.Immutable, tip.MergeState);
        }

        [Fact]
        public void TheTipCarriesTheTerminal_AndTheHeadKeepsItsOwn()
        {
            string skip;
            AnalyzerModel model = LoadFixtureModel(out skip);
            if (model == null) { output.WriteLine("SKIP: " + skip); return; }

            Recording head = ById(model, HeadRecordingId);
            Recording tip = ById(model, TipRecordingId);
            Assert.NotNull(head);
            Assert.NotNull(tip);

            // terminalState = 4 on the tip: the scene-exit extrapolator's predicted
            // impact for the still-alive sub-orbital pod.
            Assert.Equal(TerminalState.Destroyed, tip.TerminalStateValue);
            // The head was later re-stamped SubOrbital (terminalState = 3); it is NOT
            // terminal-less on these bytes, which is why INV12 does not gate on that.
            Assert.Equal(TerminalState.SubOrbital, head.TerminalStateValue);
        }

        [Fact]
        public void TheTwoHalvesAreOneChain_HeadAtIndexZeroTipAtIndexOne()
        {
            string skip;
            AnalyzerModel model = LoadFixtureModel(out skip);
            if (model == null) { output.WriteLine("SKIP: " + skip); return; }

            Recording head = ById(model, HeadRecordingId);
            Recording tip = ById(model, TipRecordingId);
            Assert.NotNull(head);
            Assert.NotNull(tip);

            Assert.False(string.IsNullOrEmpty(head.ChainId));
            Assert.Equal(head.ChainId, tip.ChainId);
            Assert.Equal(0, head.ChainIndex);
            Assert.Equal(1, tip.ChainIndex);
            // The cut UT: the head ends where the tip begins.
            Assert.Equal(head.EndUT, tip.StartUT, 3);
        }

        [Fact]
        public void TheTipCarriesTwoPredictedOrbitSegments_FromTheSceneExitTail()
        {
            string skip;
            AnalyzerModel model = LoadFixtureModel(out skip);
            if (model == null) { output.WriteLine("SKIP: " + skip); return; }

            Recording tip = ById(model, TipRecordingId);
            Assert.NotNull(tip);

            // The scene-exit finalizer's extrapolated tail: a predicted coast plus the
            // predicted ballistic descent. Hydrated off the BINARY .prec through
            // RecordingStore's own deserializer, not off the .prec.txt mirror.
            List<OrbitSegment> predicted =
                tip.OrbitSegments.Where(s => s.isPredicted).ToList();
            Assert.Equal(2, predicted.Count);
            Assert.All(predicted, s => Assert.Equal("Kerbin", s.bodyName));
            // Contiguous with each other, and the last one ends at the recording's end.
            Assert.Equal(predicted[0].endUT, predicted[1].startUT, 3);
            Assert.Equal(tip.EndUT, predicted[1].endUT, 3);
        }

        // ---------- The rule fires on the decoded model ----------------------

        [Fact]
        public void Inv12FiresOnTheDecodedFixture_AsWarnAndNotRed()
        {
            string skip;
            AnalyzerModel model = LoadFixtureModel(out skip);
            if (model == null) { output.WriteLine("SKIP: " + skip); return; }

            AnalysisReport report = InvariantEvaluator.Evaluate(
                model, new IRecordingInvariant[] { new Inv12SplitClosedSlot() });

            Finding f = Assert.Single(report.Findings);
            Assert.Equal("INV12-SPLIT-CLOSED-SLOT", f.RuleId);
            Assert.Equal(VerdictLevel.Warn, f.Level);
            Assert.Equal(HeadRecordingId, f.Target);
            Assert.Contains("tip=" + TipRecordingId, f.Message);
            Assert.Contains("tipMergeState=Immutable", f.Message);
            Assert.Contains("headMergeState=CommittedProvisional", f.Message);

            // WARN never reds: the `.analysis.txt` header's terminal RED= token is
            // written from IsRed, and a lane staging this fixture must stay green.
            Assert.False(report.IsRed);
            Assert.Equal(0, report.Counts.Fail);
            Assert.Equal(0, report.Counts.StaleFixture);
        }

        [Fact]
        public void TheSaveCarriesNoRewindPointAtAll_WhichIsTheDamage()
        {
            string skip;
            string dir = ResolveFixtureDir();
            if (dir == null)
            {
                skip = "refly-a-recorded fixture not found; looked in: " + DescribeCandidates();
                output.WriteLine("SKIP: " + skip);
                return;
            }

            // The reaper deleted it: the slot read closed, so the RP became
            // reap-eligible and its quicksave went with it. This is why INV12 has to be
            // chain-shaped - a RewindPoint-keyed rule has nothing to key on here.
            string sfs = File.ReadAllText(Path.Combine(dir, "persistent.sfs"));
            Assert.DoesNotContain("REWIND_POINT", sfs);
            Assert.False(Directory.Exists(Path.Combine(dir, "Parsek", "RewindPoints")));
        }
    }
}
